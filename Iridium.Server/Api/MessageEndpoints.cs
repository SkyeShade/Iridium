using Iridium.Protocol;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Api;

public static class MessageEndpoints
{
    public static IEndpointRouteBuilder MapMessageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/communities/{communityId:guid}/channels/{channelId:guid}/messages",
            GetRecentAsync);
        return endpoints;
    }

    private static async Task<IResult> GetRecentAsync(
        Guid communityId,
        Guid channelId,
        int? limit,
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions,
        CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await authorization.HasPermissionAsync(
                communityId, session.AccountId, CommunityPermission.ViewChannels, db)) return Results.Forbid();
        if (!await db.CommunityChannels.AnyAsync(value => value.CommunityId == communityId && value.Id == channelId))
            return Results.NotFound();

        var take = Math.Clamp(limit ?? 75, 1, 100);
        var newest = await db.ChannelMessages.AsNoTracking()
            .Where(value => value.CommunityId == communityId && value.ChannelId == channelId)
            .Include(value => value.AuthorAccount)
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .OrderByDescending(value => value.CreatedAt)
            .ThenByDescending(value => value.Id)
            .Take(take)
            .ToListAsync();
        var messages = newest.OrderBy(value => value.CreatedAt).ThenBy(value => value.Id)
            .Select(ChannelMessageMapper.ToDto).ToList();
        return Results.Ok(await ChannelMessageMapper.ResolveMentionNamesAsync(messages, db));
    }
}
