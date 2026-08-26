using Iridium.Protocol;
using Iridium.Server.Communities;
using Iridium.Server.Domain;
using Iridium.Server.Hubs;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Iridium.Server.Voice;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Api;

public static class ProfilePresetEndpoints
{
    private const int StorageSafetyLimit = 256;

    public static IEndpointRouteBuilder MapProfilePresetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/communities/{communityId:guid}/profile-presets");
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPatch("/{presetId:guid}", UpdateAsync);
        group.MapPut("/{presetId:guid}/avatar", SetAvatarAsync);
        group.MapDelete("/{presetId:guid}/avatar", ClearAvatarAsync);
        group.MapDelete("/{presetId:guid}", DeleteAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(Guid communityId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await IsMemberAsync(db, communityId, session.AccountId, cancellationToken)) return Forbidden();
        var presets = await Owned(db, session.AccountId, communityId).ToArrayAsync(cancellationToken);
        return Results.Ok(presets.Select(value => ToDto(value, context)).ToArray());
    }

    private static async Task<IResult> CreateAsync(Guid communityId, CreateUserProfilePresetRequest request, HttpContext context,
        IridiumDbContext db, SessionService sessions, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await IsMemberAsync(db, communityId, session.AccountId, cancellationToken)) return Forbidden();
        var name = ValidDisplayName(request.DisplayName);
        if (name is null) return InvalidName();
        if (await db.UserProfilePresets.CountAsync(value => value.AccountId == session.AccountId &&
                value.CommunityId == communityId, cancellationToken)
            >= StorageSafetyLimit)
            return Results.Conflict(new { message = "Too many saved Avatars." });
        var maximumPosition = await db.UserProfilePresets.Where(value => value.AccountId == session.AccountId &&
                value.CommunityId == communityId)
            .Select(value => (int?)value.Position).MaxAsync(cancellationToken);
        var position = (maximumPosition ?? -1) + 1;
        var now = DateTimeOffset.UtcNow;
        var preset = new UserProfilePreset
        {
            Id = Guid.NewGuid(), AccountId = session.AccountId, Account = session.Account,
            CommunityId = communityId, Community = null!,
            DisplayName = name, Position = position, CreatedAt = now, UpdatedAt = now
        };
        db.UserProfilePresets.Add(preset);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToDto(preset, context));
    }

    private static async Task<IResult> UpdateAsync(Guid communityId, Guid presetId, UpdateProfilePresetRequest request,
        HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityRealtimePublisher realtime, CommunityVoiceRoomService voiceRooms,
        IHubContext<ChatHub> hub, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var name = ValidDisplayName(request.DisplayName);
        if (name is null) return InvalidName();
        if (!await IsMemberAsync(db, communityId, session.AccountId, cancellationToken)) return Forbidden();
        var preset = await Owned(db, session.AccountId, communityId).SingleOrDefaultAsync(value => value.Id == presetId,
            cancellationToken);
        if (preset is null) return Results.NotFound();
        preset.DisplayName = name;
        preset.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(communityId, preset.Id, session.AccountId, db, realtime, voiceRooms, hub, cancellationToken);
        return Results.Ok(ToDto(preset, context));
    }

    private static async Task<IResult> SetAvatarAsync(Guid communityId, Guid presetId, SetUserProfilePresetAvatarRequest request,
        HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityRealtimePublisher realtime, CommunityVoiceRoomService voiceRooms,
        IHubContext<ChatHub> hub, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await IsMemberAsync(db, communityId, session.AccountId, cancellationToken)) return Forbidden();
        var preset = await Owned(db, session.AccountId, communityId).SingleOrDefaultAsync(value => value.Id == presetId,
            cancellationToken);
        if (preset is null) return Results.NotFound();
        if (request.AvatarPresetId is not { } avatarId) return Results.BadRequest();
        var avatar = await db.AccountAvatarPresets.SingleOrDefaultAsync(value =>
            value.Id == avatarId && value.AccountId == session.AccountId, cancellationToken);
        if (avatar is null) return Results.BadRequest(new { message = "Choose one of your own profile pictures." });
        preset.AvatarPresetId = avatar.Id;
        preset.AvatarPreset = avatar;
        preset.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(communityId, preset.Id, session.AccountId, db, realtime, voiceRooms, hub, cancellationToken);
        return Results.Ok(ToDto(preset, context));
    }

    private static async Task<IResult> ClearAvatarAsync(Guid communityId, Guid presetId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityRealtimePublisher realtime, CommunityVoiceRoomService voiceRooms,
        IHubContext<ChatHub> hub, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await IsMemberAsync(db, communityId, session.AccountId, cancellationToken)) return Forbidden();
        var preset = await Owned(db, session.AccountId, communityId).SingleOrDefaultAsync(value => value.Id == presetId,
            cancellationToken);
        if (preset is null) return Results.NotFound();
        preset.AvatarPresetId = null;
        preset.AvatarPreset = null;
        preset.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(communityId, preset.Id, session.AccountId, db, realtime, voiceRooms, hub, cancellationToken);
        return Results.Ok(ToDto(preset, context));
    }

    private static async Task<IResult> DeleteAsync(Guid communityId, Guid presetId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityRealtimePublisher realtime, CommunityVoiceRoomService voiceRooms,
        IHubContext<ChatHub> hub, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await IsMemberAsync(db, communityId, session.AccountId, cancellationToken)) return Forbidden();
        var preset = await db.UserProfilePresets.SingleOrDefaultAsync(value =>
            value.Id == presetId && value.AccountId == session.AccountId && value.CommunityId == communityId,
            cancellationToken);
        if (preset is null) return Results.NotFound();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.CommunityMembers.Where(value => value.CommunityId == communityId &&
                value.AccountId == session.AccountId && value.ProfilePresetId == preset.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.ProfilePresetId, (Guid?)null),
                cancellationToken);
        db.UserProfilePresets.Remove(preset);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishVoiceAsync(session.AccountId, [communityId], db, voiceRooms, hub, cancellationToken);
        await realtime.PublishAsync(communityId, "member-profile-updated", db, cancellationToken);
        return Results.NoContent();
    }

    private static IQueryable<UserProfilePreset> Owned(IridiumDbContext db, Guid accountId, Guid communityId) =>
        db.UserProfilePresets.Where(value => value.AccountId == accountId && value.CommunityId == communityId)
            .Include(value => value.AvatarPreset).OrderBy(value => value.Position);

    private static UserProfilePresetDto ToDto(UserProfilePreset preset, HttpContext context) => new(
        preset.Id, preset.AccountId, preset.CommunityId, preset.DisplayName,
        preset.AvatarPreset is null ? null : AvatarPresetEndpoints.ToDto(preset.AvatarPreset, context),
        preset.Position, preset.CreatedAt, preset.UpdatedAt);

    private static async Task PublishAsync(Guid communityId, Guid presetId, Guid accountId, IridiumDbContext db,
        CommunityRealtimePublisher realtime, CommunityVoiceRoomService voiceRooms, IHubContext<ChatHub> hub,
        CancellationToken cancellationToken)
    {
        var assigned = await db.CommunityMembers.AsNoTracking().AnyAsync(value => value.CommunityId == communityId &&
            value.AccountId == accountId && value.ProfilePresetId == presetId, cancellationToken);
        if (!assigned) return;
        await PublishVoiceAsync(accountId, [communityId], db, voiceRooms, hub, cancellationToken);
        await realtime.PublishAsync(communityId, "member-profile-updated", db, cancellationToken);
    }

    private static Task<bool> IsMemberAsync(IridiumDbContext db, Guid communityId, Guid accountId,
        CancellationToken cancellationToken) => db.CommunityMembers.AsNoTracking().AnyAsync(value =>
        value.CommunityId == communityId && value.AccountId == accountId, cancellationToken);

    private static async Task PublishVoiceAsync(Guid accountId, IReadOnlyList<Guid> communityIds,
        IridiumDbContext db, CommunityVoiceRoomService rooms, IHubContext<ChatHub> hub,
        CancellationToken cancellationToken)
    {
        foreach (var communityId in communityIds)
        {
            var member = await db.CommunityMembers.AsNoTracking().Include(value => value.Account)
                .Include(value => value.ProfilePreset).ThenInclude(value => value!.AvatarPreset)
                .SingleOrDefaultAsync(value => value.CommunityId == communityId && value.AccountId == accountId,
                    cancellationToken);
            if (member is null) continue;
            var profile = ChannelMessageMapper.ValidPreset(member);
            var changes = rooms.UpdateDisplayProfile(communityId, accountId,
                ChannelMessageMapper.ResolveDisplayName(member), profile?.AvatarPresetId,
                profile?.AvatarPreset?.Revision ?? member.Account.AvatarRevision);
            if (changes.Count == 0) continue;
            var recipients = await db.CommunityMembers.AsNoTracking().Where(value => value.CommunityId == communityId)
                .Select(value => value.AccountId).Distinct().ToArrayAsync(cancellationToken);
            foreach (var change in changes)
                await hub.Clients.Groups(recipients.Select(ChatHub.AccountGroup).ToArray()).SendAsync(
                    CommunityVoiceHubContract.ParticipantStateChanged, change, cancellationToken);
        }
    }

    private static string? ValidDisplayName(string value)
    {
        var name = value.Trim();
        return name.Length is >= 1 and <= 64 ? name : null;
    }

    private static IResult InvalidName() => Results.BadRequest(new
        { message = "Avatar display names must be between 1 and 64 characters." });

    private static IResult Forbidden() => Results.StatusCode(StatusCodes.Status403Forbidden);
}
