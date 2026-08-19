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
        endpoints.MapPost(
            "/api/communities/{communityId:guid}/channels/{channelId:guid}/read",
            MarkReadAsync);
        endpoints.MapGet("/api/communities/{communityId:guid}/messages/search", SearchAsync);
        endpoints.MapPost("/api/communities/{communityId:guid}/messages/search", SearchRequestAsync);
        return endpoints;
    }

    private static async Task<IResult> MarkReadAsync(
        Guid communityId, Guid channelId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await authorization.HasPermissionAsync(communityId, session.AccountId,
                CommunityPermission.ViewChannels, db)) return Results.Forbid();
        if (!await db.CommunityChannels.AnyAsync(value => value.CommunityId == communityId && value.Id == channelId))
            return Results.NotFound();

        var latest = await db.ChannelMessages
            .Where(value => value.CommunityId == communityId && value.ChannelId == channelId)
            .MaxAsync(value => (DateTimeOffset?)value.CreatedAt) ?? DateTimeOffset.UtcNow;
        var state = await db.CommunityChannelReadStates.SingleOrDefaultAsync(value =>
            value.CommunityId == communityId && value.ChannelId == channelId && value.AccountId == session.AccountId);
        if (state is null)
            db.CommunityChannelReadStates.Add(new Iridium.Server.Domain.CommunityChannelReadState
            {
                CommunityId = communityId, ChannelId = channelId, AccountId = session.AccountId,
                LastReadAt = latest, Channel = null!, Account = null!
            });
        else if (latest > state.LastReadAt) state.LastReadAt = latest;

        var mentionNotifications = await db.CommunityMentionNotifications
            .Where(value => value.AccountId == session.AccountId &&
                            value.CommunityId == communityId && value.ChannelId == channelId &&
                            value.ReadAt == null)
            .ToListAsync();
        var readAt = DateTimeOffset.UtcNow;
        foreach (var notification in mentionNotifications) notification.ReadAt = readAt;
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> GetRecentAsync(
        Guid communityId,
        Guid channelId,
        int? limit,
        string? before,
        Guid? around,
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

        var take = Math.Clamp(limit ?? MessageHistoryDefaults.PageSize, 1, MessageHistoryDefaults.MaximumPageSize);
        if (!string.IsNullOrWhiteSpace(before) && !MessageHistoryCursor.TryDecode(before, out _))
            return Results.BadRequest(new { message = "The history cursor is invalid." });

        if (around is { } targetId)
            return await AroundAsync(communityId, channelId, targetId, take, db);

        var query = db.ChannelMessages.AsNoTracking()
            .Where(value => value.CommunityId == communityId && value.ChannelId == channelId);
        if (MessageHistoryCursor.TryDecode(before, out var cursor))
        {
            var cursorAt = new DateTimeOffset(cursor.UtcTicks, TimeSpan.Zero);
            query = query.Where(value => value.CreatedAt < cursorAt ||
                                         value.CreatedAt == cursorAt && value.Id.CompareTo(cursor.MessageId) < 0);
        }
        var newest = await query
            .Include(value => value.AuthorAccount)
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .OrderByDescending(value => value.CreatedAt)
            .ThenByDescending(value => value.Id)
            .Take(take + 1)
            .ToListAsync();
        var hasOlder = newest.Count > take;
        if (hasOlder) newest.RemoveAt(newest.Count - 1);
        var messages = newest.OrderBy(value => value.CreatedAt).ThenBy(value => value.Id)
            .Select(ChannelMessageMapper.ToDto).ToList();
        var resolved = await ChannelMessageMapper.ResolveMentionNamesAsync(messages, db);
        var olderCursor = resolved.Count == 0 ? null : MessageHistoryCursor.Encode(resolved[0].CreatedAt, resolved[0].Id);
        return Results.Ok(new MessageHistoryPage<ChannelMessageDto>(resolved, olderCursor, hasOlder));
    }

    private static async Task<IResult> AroundAsync(
        Guid communityId, Guid channelId, Guid targetId, int take, IridiumDbContext db)
    {
        var target = await db.ChannelMessages.AsNoTracking()
            .Include(value => value.AuthorAccount)
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .SingleOrDefaultAsync(value => value.Id == targetId && value.CommunityId == communityId && value.ChannelId == channelId);
        if (target is null) return Results.NotFound(new { message = "Message not found in this channel." });
        var half = Math.Min(MessageHistoryDefaults.AroundHalfWindow, Math.Max(1, take / 2));
        var before = await db.ChannelMessages.AsNoTracking()
            .Where(value => value.CommunityId == communityId && value.ChannelId == channelId &&
                            (value.CreatedAt < target.CreatedAt || value.CreatedAt == target.CreatedAt && value.Id.CompareTo(target.Id) < 0))
            .Include(value => value.AuthorAccount).Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .OrderByDescending(value => value.CreatedAt).ThenByDescending(value => value.Id).Take(half + 1).ToListAsync();
        var hasOlder = before.Count > half;
        if (hasOlder) before.RemoveAt(before.Count - 1);
        var after = await db.ChannelMessages.AsNoTracking()
            .Where(value => value.CommunityId == communityId && value.ChannelId == channelId &&
                            (value.CreatedAt > target.CreatedAt || value.CreatedAt == target.CreatedAt && value.Id.CompareTo(target.Id) > 0))
            .Include(value => value.AuthorAccount).Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .OrderBy(value => value.CreatedAt).ThenBy(value => value.Id).Take(half).ToListAsync();
        var entities = before.OrderBy(value => value.CreatedAt).ThenBy(value => value.Id).Append(target).Concat(after);
        var messages = entities.Select(ChannelMessageMapper.ToDto).ToList();
        var resolved = await ChannelMessageMapper.ResolveMentionNamesAsync(messages, db);
        var olderCursor = resolved.Count == 0 ? null : MessageHistoryCursor.Encode(resolved[0].CreatedAt, resolved[0].Id);
        return Results.Ok(new MessageHistoryPage<ChannelMessageDto>(resolved, olderCursor, hasOlder, true, targetId));
    }

    private static async Task<IResult> SearchAsync(
        Guid communityId, string? q, string? from, string? before, int? limit,
        HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await authorization.HasPermissionAsync(communityId, session.AccountId,
                CommunityPermission.ViewChannels, db)) return Results.Forbid();
        var channelFilter = context.Request.Query["in"].ToString();
        var take = Math.Clamp(limit ?? MessageHistoryDefaults.SearchPageSize, 1, MessageHistoryDefaults.MaximumPageSize);
        var query = db.ChannelMessages.AsNoTracking()
            .Where(value => value.CommunityId == communityId && !value.IsDeleted)
            .Include(value => value.AuthorAccount).Include(value => value.Channel).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var text = q.ToLower();
            query = query.Where(value => value.Content.ToLower().Contains(text));
        }
        if (!string.IsNullOrWhiteSpace(from))
        {
            var author = from.ToLower();
            query = query.Where(value => value.AuthorAccount.Username.ToLower() == author ||
                                         value.AuthorAccount.DisplayName.ToLower() == author);
        }
        if (!string.IsNullOrWhiteSpace(channelFilter))
        {
            var channel = channelFilter.ToLower();
            query = query.Where(value => value.Channel.Name.ToLower() == channel);
        }
        if (MessageHistoryCursor.TryDecode(before, out var cursor))
        {
            var cursorAt = new DateTimeOffset(cursor.UtcTicks, TimeSpan.Zero);
            query = query.Where(value => value.CreatedAt < cursorAt ||
                                         value.CreatedAt == cursorAt && value.Id.CompareTo(cursor.MessageId) < 0);
        }
        var found = await query.OrderByDescending(value => value.CreatedAt).ThenByDescending(value => value.Id)
            .Take(take + 1).ToListAsync();
        var hasMore = found.Count > take;
        if (hasMore) found.RemoveAt(found.Count - 1);
        var results = found.Select(value => new MessageSearchResultDto(value.Id, value.CommunityId, value.ChannelId, null,
            value.Channel.Name, new(value.AuthorAccountId, value.AuthorAccount.Username, value.AuthorAccount.DisplayName),
            value.Content, value.CreatedAt)).ToArray();
        var next = results.Length == 0 ? null : MessageHistoryCursor.Encode(results[^1].CreatedAt, results[^1].MessageId);
        return Results.Ok(new MessageSearchPageDto(results, next, hasMore));
    }

    private static async Task<IResult> SearchRequestAsync(
        Guid communityId, MessageSearchRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await authorization.HasPermissionAsync(communityId, session.AccountId,
                CommunityPermission.ViewChannels, db)) return Results.Forbid();
        var criteria = request.Query;
        var take = Math.Clamp(request.Limit, 1, MessageHistoryDefaults.MaximumPageSize);
        var query = db.ChannelMessages.AsNoTracking()
            .Where(value => value.CommunityId == communityId && !value.IsDeleted)
            .Include(value => value.AuthorAccount).Include(value => value.Channel).AsQueryable();
        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            var text = criteria.Text.ToLower();
            query = query.Where(value => value.Content.ToLower().Contains(text));
        }
        if (criteria.FromAccountId is { } authorId) query = query.Where(value => value.AuthorAccountId == authorId);
        if (criteria.ChannelId is { } channelId) query = query.Where(value => value.ChannelId == channelId);
        if (criteria.MentionedAccountId is { } mentionedId)
        {
            var stableId = mentionedId.ToString();
            query = query.Where(value => value.MentionsJson != null && value.MentionsJson.Contains(stableId));
        }
        if (criteria.BeforeUtc is { } beforeUtc) query = query.Where(value => value.CreatedAt < beforeUtc);
        if (criteria.AfterUtc is { } afterUtc) query = query.Where(value => value.CreatedAt > afterUtc);
        if (criteria.DuringStartUtc is { } duringStart) query = query.Where(value => value.CreatedAt >= duringStart);
        if (criteria.DuringEndUtc is { } duringEnd) query = query.Where(value => value.CreatedAt < duringEnd);
        if (criteria.AuthorType != MessageAuthorType.User) query = query.Where(_ => false);
        if (criteria.HasTypes.Count > 0)
        {
            if (criteria.HasTypes.Contains(MessageSearchContentType.Link))
                query = query.Where(value => value.Content.Contains("http://") || value.Content.Contains("https://"));
            else query = query.Where(_ => false);
        }
        if (MessageHistoryCursor.TryDecode(request.Cursor, out var cursor))
        {
            var cursorAt = new DateTimeOffset(cursor.UtcTicks, TimeSpan.Zero);
            query = criteria.Sort == MessageSearchSort.Newest
                ? query.Where(value => value.CreatedAt < cursorAt || value.CreatedAt == cursorAt && value.Id.CompareTo(cursor.MessageId) < 0)
                : query.Where(value => value.CreatedAt > cursorAt || value.CreatedAt == cursorAt && value.Id.CompareTo(cursor.MessageId) > 0);
        }
        query = criteria.Sort == MessageSearchSort.Newest
            ? query.OrderByDescending(value => value.CreatedAt).ThenByDescending(value => value.Id)
            : query.OrderBy(value => value.CreatedAt).ThenBy(value => value.Id);
        var found = await query.Take(take + 1).ToListAsync();
        var hasMore = found.Count > take;
        if (hasMore) found.RemoveAt(found.Count - 1);
        var results = found.Select(value => new MessageSearchResultDto(value.Id, value.CommunityId, value.ChannelId, null,
            value.Channel.Name, new(value.AuthorAccountId, value.AuthorAccount.Username, value.AuthorAccount.DisplayName),
            value.Content, value.CreatedAt)).ToArray();
        var next = results.Length == 0 ? null : MessageHistoryCursor.Encode(results[^1].CreatedAt, results[^1].MessageId);
        return Results.Ok(new MessageSearchPageDto(results, next, hasMore));
    }
}
