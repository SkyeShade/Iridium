using Iridium.Protocol;
using Iridium.Server.Domain;
using Iridium.Server.Hubs;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Api;

public static class FriendEndpoints
{
    public static IEndpointRouteBuilder MapFriendEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/friends");
        group.MapGet("/", ListAsync);
        endpoints.MapGet("/api/profiles/{username}", ResolveProfileAsync);
        group.MapPost("/requests", RequestAsync);
        group.MapPost("/requests/{friendshipId:guid}/accept", AcceptAsync);
        group.MapDelete("/{friendshipId:guid}", RemoveAsync);
        endpoints.MapPut("/api/profiles/{accountId:guid}/block", BlockAsync);
        endpoints.MapDelete("/api/profiles/{accountId:guid}/block", UnblockAsync);
        return endpoints;
    }

    private static async Task<IResult> ResolveProfileAsync(
        string username,
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions,
        PresenceTracker presence)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var normalized = username.Trim().ToLowerInvariant();
        var target = await db.Accounts.SingleOrDefaultAsync(value => value.Username == normalized);
        if (target is null) return Results.NotFound(new { message = "No account with that identity exists on this Node." });

        var friendship = target.Id == session.AccountId
            ? null
            : await db.Friendships.SingleOrDefaultAsync(value =>
                (value.RequesterAccountId == session.AccountId && value.AddresseeAccountId == target.Id) ||
                (value.RequesterAccountId == target.Id && value.AddresseeAccountId == session.AccountId));
        var relationship = target.Id == session.AccountId
            ? ProfileRelationshipStatus.Self
            : friendship is null
                ? ProfileRelationshipStatus.None
                : friendship.Status == FriendshipState.Accepted
                    ? ProfileRelationshipStatus.Friends
                    : friendship.RequesterAccountId == session.AccountId
                        ? ProfileRelationshipStatus.OutgoingPending
                        : ProfileRelationshipStatus.IncomingPending;
        var blocked = target.Id != session.AccountId && await db.AccountBlocks.AnyAsync(value =>
            value.BlockingAccountId == session.AccountId && value.BlockedAccountId == target.Id);
        return Results.Ok(new ResolvedProfileDto(
            target.Id, target.Username, target.DisplayName, target.Pronouns, target.Description,
            relationship, friendship?.Id, presence.GetPublic(target.Id), blocked));
    }

    private static async Task<IResult> BlockAsync(Guid accountId, HttpContext context, IridiumDbContext db, SessionService sessions)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (accountId == session.AccountId) return Results.BadRequest(new { message = "You cannot block yourself." });
        if (!await db.Accounts.AnyAsync(value => value.Id == accountId)) return Results.NotFound();
        if (!await db.AccountBlocks.AnyAsync(value => value.BlockingAccountId == session.AccountId && value.BlockedAccountId == accountId))
        {
            db.AccountBlocks.Add(new AccountBlock { BlockingAccountId = session.AccountId, BlockedAccountId = accountId,
                CreatedAt = DateTimeOffset.UtcNow, BlockingAccount = null!, BlockedAccount = null! });
            await db.SaveChangesAsync();
        }
        return Results.NoContent();
    }

    private static async Task<IResult> UnblockAsync(Guid accountId, HttpContext context, IridiumDbContext db, SessionService sessions)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var block = await db.AccountBlocks.SingleOrDefaultAsync(value => value.BlockingAccountId == session.AccountId && value.BlockedAccountId == accountId);
        if (block is not null) { db.AccountBlocks.Remove(block); await db.SaveChangesAsync(); }
        return Results.NoContent();
    }

    private static async Task<IResult> ListAsync(HttpContext context, IridiumDbContext db, SessionService sessions, PresenceTracker presence)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();

        var accountId = session.AccountId;
        var rows = await db.Friendships
            .Where(value => value.RequesterAccountId == accountId || value.AddresseeAccountId == accountId)
            .Select(value => new
            {
                Friendship = value,
                Other = value.RequesterAccountId == accountId ? value.AddresseeAccount : value.RequesterAccount
            })
            .ToListAsync();

        var friends = rows.Select(value => new FriendDto(
                value.Friendship.Id,
                value.Other.Id,
                value.Other.Username,
                value.Other.DisplayName,
                value.Other.Pronouns,
                value.Other.Description,
                value.Friendship.Status == FriendshipState.Accepted ? FriendshipStatus.Accepted : FriendshipStatus.Pending,
                value.Friendship.RequesterAccountId == accountId,
                presence.GetPublic(value.Other.Id)))
            .OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Username, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Results.Ok(friends);
    }

    private static async Task<IResult> RequestAsync(
        SendFriendRequest request,
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions,
        IHubContext<ChatHub> hub,
        PresenceTracker presence)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();

        var username = request.Username.Trim().ToLowerInvariant();
        var target = await db.Accounts.SingleOrDefaultAsync(value => value.Username == username);
        if (target is null) return Results.NotFound(new { message = "No account with that username exists on this Node." });
        if (target.Id == session.AccountId) return Results.BadRequest(new { message = "You cannot add yourself as a friend." });

        var existing = await db.Friendships.SingleOrDefaultAsync(value =>
            (value.RequesterAccountId == session.AccountId && value.AddresseeAccountId == target.Id) ||
            (value.RequesterAccountId == target.Id && value.AddresseeAccountId == session.AccountId));
        if (existing is not null)
        {
            if (existing.Status == FriendshipState.Pending && existing.AddresseeAccountId == session.AccountId)
            {
                existing.Status = FriendshipState.Accepted;
                existing.AcceptedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                await NotifyBothAsync(hub, existing, FriendshipHubContract.RequestAccepted);
                return Results.Ok(new FriendDto(existing.Id, target.Id, target.Username, target.DisplayName,
                    target.Pronouns, target.Description, FriendshipStatus.Accepted, false, presence.GetPublic(target.Id)));
            }
            return Results.Conflict(new { message = "A friendship or request already exists." });
        }

        var friendship = new Friendship
        {
            Id = Guid.NewGuid(),
            RequesterAccountId = session.AccountId,
            RequesterAccount = session.Account,
            AddresseeAccountId = target.Id,
            AddresseeAccount = target,
            Status = FriendshipState.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Friendships.Add(friendship);
        await db.SaveChangesAsync();
        await hub.Clients.Group(ChatHub.AccountGroup(target.Id)).SendAsync(
            FriendshipHubContract.RequestReceived, new FriendshipChangedEvent(friendship.Id));
        return Results.Created($"/api/friends/requests/{friendship.Id}", new FriendDto(
            friendship.Id, target.Id, target.Username, target.DisplayName, target.Pronouns, target.Description,
            FriendshipStatus.Pending, true, presence.GetPublic(target.Id)));
    }

    private static async Task<IResult> AcceptAsync(
        Guid friendshipId,
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions,
        IHubContext<ChatHub> hub)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();

        var friendship = await db.Friendships.SingleOrDefaultAsync(value => value.Id == friendshipId);
        if (friendship is null) return Results.NotFound();
        if (friendship.AddresseeAccountId != session.AccountId) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (friendship.Status != FriendshipState.Pending) return Results.Conflict();

        friendship.Status = FriendshipState.Accepted;
        friendship.AcceptedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await NotifyBothAsync(hub, friendship, FriendshipHubContract.RequestAccepted);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveAsync(
        Guid friendshipId,
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions,
        IHubContext<ChatHub> hub)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var friendship = await db.Friendships.SingleOrDefaultAsync(value => value.Id == friendshipId);
        if (friendship is null) return Results.NotFound();
        if (friendship.RequesterAccountId != session.AccountId && friendship.AddresseeAccountId != session.AccountId)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var eventName = friendship.Status == FriendshipState.Pending
            ? FriendshipHubContract.RequestDeclined
            : FriendshipHubContract.FriendshipRemoved;
        db.Friendships.Remove(friendship);
        await db.SaveChangesAsync();
        await NotifyBothAsync(hub, friendship, eventName);
        return Results.NoContent();
    }

    private static Task NotifyBothAsync(IHubContext<ChatHub> hub, Friendship friendship, string eventName) =>
        hub.Clients.Groups(
            ChatHub.AccountGroup(friendship.RequesterAccountId),
            ChatHub.AccountGroup(friendship.AddresseeAccountId))
            .SendAsync(eventName, new FriendshipChangedEvent(friendship.Id));
}
