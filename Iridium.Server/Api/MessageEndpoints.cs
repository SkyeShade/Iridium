using Iridium.Protocol;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Iridium.Server.Storage;
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
        endpoints.MapGet("/api/messages/{messageId:guid}/author-avatar/metadata", GetAuthorAvatarMetadataAsync);
        endpoints.MapGet("/api/messages/{messageId:guid}/author-avatar", DownloadAuthorAvatarAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAuthorAvatarMetadataAsync(
        Guid messageId, HttpContext context, IridiumDbContext db, SessionService sessions,
        CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var message = await db.ChannelMessages.AsNoTracking().SingleOrDefaultAsync(value => value.Id == messageId);
        if (message?.AuthorAvatarObjectKeySnapshot is null) return Results.NotFound();
        var access = await authorization.GetChannelAccessAsync(
            message.CommunityId, message.ChannelId, session.AccountId, db);
        if (!access.Has(CommunityPermission.ViewChannels) ||
            !access.Has(CommunityPermission.ReadMessageHistory)) return Results.Forbid();
        var revision = message.AuthorAvatarRevisionSnapshot ?? 0;
        var url = $"{context.Request.Scheme}://{context.Request.Host}/api/messages/{message.Id}/author-avatar?v={revision}";
        return Results.Ok(new ProfileAvatarDto(true, url, revision,
            message.AuthorAvatarCropXSnapshot ?? 0, message.AuthorAvatarCropYSnapshot ?? 0,
            message.AuthorAvatarZoomSnapshot ?? 1, message.AuthorAvatarWidthSnapshot ?? 0,
            message.AuthorAvatarHeightSnapshot ?? 0));
    }

    private static async Task<IResult> DownloadAuthorAvatarAsync(
        Guid messageId, HttpContext context, IridiumDbContext db, IAttachmentStorage storage,
        CancellationToken cancellationToken)
    {
        var avatar = await db.ChannelMessages.AsNoTracking().Where(value => value.Id == messageId)
            .Select(value => new { value.AuthorAvatarObjectKeySnapshot, value.AuthorAvatarContentTypeSnapshot })
            .SingleOrDefaultAsync(cancellationToken);
        if (avatar?.AuthorAvatarObjectKeySnapshot is null) return Results.NotFound();
        var stream = await storage.OpenReadAsync(avatar.AuthorAvatarObjectKeySnapshot, cancellationToken);
        if (stream is not null && context.Request.Query.ContainsKey("v"))
            context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        return stream is null ? Results.NotFound() : Results.File(stream,
            avatar.AuthorAvatarContentTypeSnapshot ?? "application/octet-stream", enableRangeProcessing: true);
    }

    private static async Task<IResult> MarkReadAsync(
        Guid communityId, Guid channelId, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await authorization.HasChannelPermissionAsync(communityId, channelId, session.AccountId,
                CommunityPermission.ViewChannels, db)) return Results.Forbid();
        if (!await db.CommunityChannels.AnyAsync(value => value.CommunityId == communityId && value.Id == channelId &&
                value.Kind == CommunityChannelKind.Text))
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
        var channelAccess = await authorization.GetChannelAccessAsync(communityId, channelId, session.AccountId, db);
        if (!channelAccess.Has(CommunityPermission.ViewChannels) ||
            !channelAccess.Has(CommunityPermission.ReadMessageHistory)) return Results.Forbid();
        if (!await db.CommunityChannels.AnyAsync(value => value.CommunityId == communityId && value.Id == channelId &&
                value.Kind == CommunityChannelKind.Text))
            return Results.NotFound();

        var take = Math.Clamp(limit ?? MessageHistoryDefaults.PageSize, 1, MessageHistoryDefaults.MaximumPageSize);
        if (!string.IsNullOrWhiteSpace(before) && !MessageHistoryCursor.TryDecode(before, out _))
            return Results.BadRequest(new { message = "The history cursor is invalid." });

        if (around is { } targetId)
            return await AroundAsync(communityId, channelId, targetId, take, db);

        var query = db.ChannelMessages.AsNoTracking()
            .Where(value => value.CommunityId == communityId && value.ChannelId == channelId && !value.IsDeleted);
        if (MessageHistoryCursor.TryDecode(before, out var cursor))
        {
            var cursorAt = new DateTimeOffset(cursor.UtcTicks, TimeSpan.Zero);
            query = query.Where(value => value.CreatedAt < cursorAt ||
                                         value.CreatedAt == cursorAt && value.Id.CompareTo(cursor.MessageId) < 0);
        }
        var newest = await query
            .Include(value => value.AuthorAccount)
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.Attachments)
            .Include(value => value.Attachments)
            .IncludeForwardedSnapshot()
            .OrderByDescending(value => value.CreatedAt)
            .ThenByDescending(value => value.Id)
            .Take(take + 1)
            .ToListAsync();
        var hasOlder = newest.Count > take;
        if (hasOlder) newest.RemoveAt(newest.Count - 1);
        var messages = newest.OrderBy(value => value.CreatedAt).ThenBy(value => value.Id)
            .Select(ChannelMessageMapper.ToDto).ToList();
        var profiled = await ChannelMessageMapper.ResolveCommunityProfilesAsync(messages, db);
        var resolved = await ChannelMessageMapper.ResolveMentionNamesAsync(profiled, db);
        var olderCursor = resolved.Count == 0 ? null : MessageHistoryCursor.Encode(resolved[0].CreatedAt, resolved[0].Id);
        return Results.Ok(new MessageHistoryPage<ChannelMessageDto>(resolved, olderCursor, hasOlder));
    }

    private static async Task<IResult> AroundAsync(
        Guid communityId, Guid channelId, Guid targetId, int take, IridiumDbContext db)
    {
        var target = await db.ChannelMessages.AsNoTracking()
            .Include(value => value.AuthorAccount)
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.Attachments)
            .Include(value => value.Attachments)
            .IncludeForwardedSnapshot()
            .SingleOrDefaultAsync(value => value.Id == targetId && value.CommunityId == communityId &&
                                           value.ChannelId == channelId && !value.IsDeleted);
        if (target is null) return Results.NotFound(new { message = "Message not found in this channel." });
        var half = Math.Min(MessageHistoryDefaults.AroundHalfWindow, Math.Max(1, take / 2));
        var before = await db.ChannelMessages.AsNoTracking()
            .Where(value => value.CommunityId == communityId && value.ChannelId == channelId &&
                            !value.IsDeleted &&
                            (value.CreatedAt < target.CreatedAt || value.CreatedAt == target.CreatedAt && value.Id.CompareTo(target.Id) < 0))
            .Include(value => value.AuthorAccount).Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.Attachments)
            .Include(value => value.Attachments)
            .IncludeForwardedSnapshot()
            .OrderByDescending(value => value.CreatedAt).ThenByDescending(value => value.Id).Take(half + 1).ToListAsync();
        var hasOlder = before.Count > half;
        if (hasOlder) before.RemoveAt(before.Count - 1);
        var after = await db.ChannelMessages.AsNoTracking()
            .Where(value => value.CommunityId == communityId && value.ChannelId == channelId &&
                            !value.IsDeleted &&
                            (value.CreatedAt > target.CreatedAt || value.CreatedAt == target.CreatedAt && value.Id.CompareTo(target.Id) > 0))
            .Include(value => value.AuthorAccount).Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.Attachments)
            .Include(value => value.Attachments)
            .IncludeForwardedSnapshot()
            .OrderBy(value => value.CreatedAt).ThenBy(value => value.Id).Take(half).ToListAsync();
        var entities = before.OrderBy(value => value.CreatedAt).ThenBy(value => value.Id).Append(target).Concat(after);
        var messages = entities.Select(ChannelMessageMapper.ToDto).ToList();
        var profiled = await ChannelMessageMapper.ResolveCommunityProfilesAsync(messages, db);
        var resolved = await ChannelMessageMapper.ResolveMentionNamesAsync(profiled, db);
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
        var communityAccess = await authorization.GetAccessAsync(communityId, session.AccountId, db);
        if (!communityAccess.IsOwner && !await authorization.IsMemberAsync(communityId, session.AccountId, db))
            return Results.Forbid();
        var accessibleChannelIds = await AccessibleTextChannelIdsAsync(communityId, session.AccountId, db, authorization);
        if (accessibleChannelIds.Count == 0) return Results.Ok(new MessageSearchPageDto([], null, false));
        var channelFilter = context.Request.Query["in"].ToString();
        var take = Math.Clamp(limit ?? MessageHistoryDefaults.SearchPageSize, 1, MessageHistoryDefaults.MaximumPageSize);
        var query = db.ChannelMessages.AsNoTracking()
            .Where(value => value.CommunityId == communityId && accessibleChannelIds.Contains(value.ChannelId) && !value.IsDeleted)
            .Include(value => value.AuthorAccount).Include(value => value.Channel)
            .IncludeForwardedSnapshot().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var text = q.ToLower();
            query = query.Where(value => value.Content.ToLower().Contains(text) ||
                value.ForwardedMessageSnapshot != null && value.ForwardedMessageSnapshot.Content.ToLower().Contains(text));
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
            value.Channel.Name, new(value.AuthorAccountId, value.AuthorAccount.Username,
                value.AuthorDisplayNameSnapshot ?? value.AuthorAccount.DisplayName,
                AvatarRevision: value.AuthorAvatarRevisionSnapshot ?? value.AuthorAccount.AvatarRevision,
                AvatarSnapshotMessageId: value.AuthorAvatarObjectKeySnapshot is null ? null : value.Id,
                HasHistoricalSnapshot: value.AuthorDisplayNameSnapshot is not null),
            SearchContent(value.Content, value.ForwardedMessageSnapshot?.Content), value.CreatedAt)).ToArray();
        var next = results.Length == 0 ? null : MessageHistoryCursor.Encode(results[^1].CreatedAt, results[^1].MessageId);
        return Results.Ok(new MessageSearchPageDto(results, next, hasMore));
    }

    private static async Task<IResult> SearchRequestAsync(
        Guid communityId, MessageSearchRequest request, HttpContext context, IridiumDbContext db,
        SessionService sessions, CommunityAuthorizationService authorization)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var communityAccess = await authorization.GetAccessAsync(communityId, session.AccountId, db);
        if (!communityAccess.IsOwner && !await authorization.IsMemberAsync(communityId, session.AccountId, db))
            return Results.Forbid();
        var accessibleChannelIds = await AccessibleTextChannelIdsAsync(communityId, session.AccountId, db, authorization);
        if (accessibleChannelIds.Count == 0) return Results.Ok(new MessageSearchPageDto([], null, false));
        var criteria = request.Query;
        var take = Math.Clamp(request.Limit, 1, MessageHistoryDefaults.MaximumPageSize);
        var query = db.ChannelMessages.AsNoTracking()
            .Where(value => value.CommunityId == communityId && accessibleChannelIds.Contains(value.ChannelId) && !value.IsDeleted)
            .Include(value => value.AuthorAccount).Include(value => value.Channel)
            .IncludeForwardedSnapshot().AsQueryable();
        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            var text = criteria.Text.ToLower();
            query = query.Where(value => value.Content.ToLower().Contains(text) ||
                value.ForwardedMessageSnapshot != null && value.ForwardedMessageSnapshot.Content.ToLower().Contains(text));
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
            value.Channel.Name, new(value.AuthorAccountId, value.AuthorAccount.Username,
                value.AuthorDisplayNameSnapshot ?? value.AuthorAccount.DisplayName,
                AvatarRevision: value.AuthorAvatarRevisionSnapshot ?? value.AuthorAccount.AvatarRevision,
                AvatarSnapshotMessageId: value.AuthorAvatarObjectKeySnapshot is null ? null : value.Id,
                HasHistoricalSnapshot: value.AuthorDisplayNameSnapshot is not null),
            SearchContent(value.Content, value.ForwardedMessageSnapshot?.Content), value.CreatedAt)).ToArray();
        var next = results.Length == 0 ? null : MessageHistoryCursor.Encode(results[^1].CreatedAt, results[^1].MessageId);
        return Results.Ok(new MessageSearchPageDto(results, next, hasMore));
    }

    private static string SearchContent(string note, string? forwarded) => string.IsNullOrWhiteSpace(forwarded)
        ? note
        : string.IsNullOrWhiteSpace(note) ? forwarded : $"{note}\n{forwarded}";

    private static async Task<List<Guid>> AccessibleTextChannelIdsAsync(Guid communityId, Guid accountId,
        IridiumDbContext db, CommunityAuthorizationService authorization)
    {
        var ids = await db.CommunityChannels.AsNoTracking().Where(value => value.CommunityId == communityId &&
            value.Kind == CommunityChannelKind.Text).Select(value => value.Id).ToListAsync();
        var result = new List<Guid>();
        foreach (var id in ids)
        {
            var access = await authorization.GetChannelAccessAsync(communityId, id, accountId, db);
            if (access.Has(CommunityPermission.ViewChannels) && access.Has(CommunityPermission.ReadMessageHistory))
                result.Add(id);
        }
        return result;
    }

}
