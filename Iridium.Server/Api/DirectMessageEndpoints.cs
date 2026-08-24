using Iridium.Protocol;
using Iridium.Server.Domain;
using Iridium.Server.Hubs;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Api;

public static class DirectMessageEndpoints
{
    public static IEndpointRouteBuilder MapDirectMessageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/direct-messages");
        group.MapGet("/", ListAsync);
        group.MapPost("/with/{accountId:guid}", OpenAsync);
        group.MapGet("/{conversationId:guid}/messages", HistoryAsync);
        group.MapGet("/{conversationId:guid}/messages/search", SearchAsync);
        group.MapPost("/{conversationId:guid}/messages/search", SearchRequestAsync);
        group.MapPost("/{conversationId:guid}/hide", HideAsync);
        group.MapPost("/{conversationId:guid}/read", MarkReadAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(HttpContext context, IridiumDbContext db, SessionService sessions, PresenceTracker presence)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var accountId = session.AccountId;
        var conversations = await db.DirectConversations
            .Include(value => value.ParticipantAAccount)
            .Include(value => value.ParticipantBAccount)
            .Include(value => value.Messages)
            .Include(value => value.ParticipantStates)
            .Where(value => value.ParticipantAAccountId == accountId || value.ParticipantBAccountId == accountId)
            .ToListAsync();
        var visible = conversations
            .Select(value =>
            {
                var participantState = value.ParticipantStates.FirstOrDefault(state => state.AccountId == accountId);
                return new
                {
                    Conversation = value,
                    LastMessageAt = value.Messages.Count == 0 ? (DateTimeOffset?)null : value.Messages.Max(message => message.CreatedAt),
                    HiddenAt = participantState?.HiddenAt,
                    UnreadCount = value.Messages.Count(message => message.AuthorAccountId != accountId && !message.IsDeleted &&
                        (participantState?.LastReadAt is not { } lastReadAt || message.CreatedAt > lastReadAt))
                };
            })
            .Where(value => value.LastMessageAt is not null &&
                            (value.HiddenAt is null || value.LastMessageAt > value.HiddenAt))
            .OrderByDescending(value => value.LastMessageAt)
            .Select(value => DirectMessageMapper.ConversationToDto(value.Conversation, accountId,
                presence.GetPublic(value.Conversation.ParticipantAAccountId == accountId
                    ? value.Conversation.ParticipantBAccountId
                    : value.Conversation.ParticipantAAccountId), value.LastMessageAt, value.UnreadCount))
            .ToArray();
        return Results.Ok(visible);
    }

    private static async Task<IResult> OpenAsync(
        Guid accountId,
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions,
        PresenceTracker presence)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (accountId == session.AccountId) return Results.BadRequest(new { message = "You cannot message yourself." });
        var other = await db.Accounts.SingleOrDefaultAsync(value => value.Id == accountId);
        if (other is null) return Results.NotFound(new { message = "Account not found on this Node." });
        var (first, second) = Ordered(session.AccountId, accountId);
        var conversation = await db.DirectConversations
            .Include(value => value.ParticipantAAccount)
            .Include(value => value.ParticipantBAccount)
            .Include(value => value.Messages)
            .Include(value => value.ParticipantStates)
            .SingleOrDefaultAsync(value => value.ParticipantAAccountId == first && value.ParticipantBAccountId == second);
        if (conversation is null)
        {
            conversation = new DirectConversation
            {
                Id = Guid.NewGuid(),
                ParticipantAAccountId = first,
                ParticipantBAccountId = second,
                ParticipantAAccount = first == session.AccountId ? session.Account : other,
                ParticipantBAccount = second == session.AccountId ? session.Account : other,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.DirectConversations.Add(conversation);
            await db.SaveChangesAsync();
        }
        var state = conversation.ParticipantStates.FirstOrDefault(value => value.AccountId == session.AccountId);
        var unread = conversation.Messages.Count(message => message.AuthorAccountId != session.AccountId && !message.IsDeleted &&
            (state?.LastReadAt is null || message.CreatedAt > state.LastReadAt));
        return Results.Ok(DirectMessageMapper.ConversationToDto(conversation, session.AccountId,
            presence.GetPublic(other.Id), unreadCount: unread));
    }

    private static async Task<IResult> HistoryAsync(
        Guid conversationId,
        int? limit,
        string? before,
        Guid? around,
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await IsParticipantAsync(conversationId, session.AccountId, db)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var take = Math.Clamp(limit ?? MessageHistoryDefaults.PageSize, 1, MessageHistoryDefaults.MaximumPageSize);
        if (!string.IsNullOrWhiteSpace(before) && !MessageHistoryCursor.TryDecode(before, out _))
            return Results.BadRequest(new { message = "The history cursor is invalid." });
        if (around is { } targetId) return await AroundAsync(conversationId, targetId, take, db);
        var query = db.DirectMessages.AsNoTracking()
            .Where(value => value.ConversationId == conversationId && !value.IsDeleted);
        if (MessageHistoryCursor.TryDecode(before, out var cursor))
        {
            var cursorAt = new DateTimeOffset(cursor.UtcTicks, TimeSpan.Zero);
            query = query.Where(value => value.CreatedAt < cursorAt ||
                                         value.CreatedAt == cursorAt && value.Id.CompareTo(cursor.MessageId) < 0);
        }
        var messages = await query
            .Include(value => value.AuthorAccount)
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .Include(value => value.Attachments)
            .OrderByDescending(value => value.CreatedAt)
            .ThenByDescending(value => value.Id)
            .Take(take + 1)
            .ToListAsync();
        var hasOlder = messages.Count > take;
        if (hasOlder) messages.RemoveAt(messages.Count - 1);
        var result = messages.OrderBy(value => value.CreatedAt).ThenBy(value => value.Id)
            .Select(DirectMessageMapper.ToDto).ToArray();
        var olderCursor = result.Length == 0 ? null : MessageHistoryCursor.Encode(result[0].CreatedAt, result[0].Id);
        return Results.Ok(new MessageHistoryPage<DirectMessageDto>(result, olderCursor, hasOlder));
    }

    private static async Task<IResult> AroundAsync(Guid conversationId, Guid targetId, int take, IridiumDbContext db)
    {
        var target = await db.DirectMessages.AsNoTracking().Include(value => value.AuthorAccount)
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .Include(value => value.Attachments)
            .SingleOrDefaultAsync(value => value.Id == targetId && value.ConversationId == conversationId && !value.IsDeleted);
        if (target is null) return Results.NotFound(new { message = "Message not found in this conversation." });
        var half = Math.Min(MessageHistoryDefaults.AroundHalfWindow, Math.Max(1, take / 2));
        var older = await db.DirectMessages.AsNoTracking()
            .Where(value => value.ConversationId == conversationId &&
                            !value.IsDeleted &&
                            (value.CreatedAt < target.CreatedAt || value.CreatedAt == target.CreatedAt && value.Id.CompareTo(target.Id) < 0))
            .Include(value => value.AuthorAccount).Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .Include(value => value.Attachments)
            .OrderByDescending(value => value.CreatedAt).ThenByDescending(value => value.Id).Take(half + 1).ToListAsync();
        var hasOlder = older.Count > half;
        if (hasOlder) older.RemoveAt(older.Count - 1);
        var newer = await db.DirectMessages.AsNoTracking()
            .Where(value => value.ConversationId == conversationId &&
                            !value.IsDeleted &&
                            (value.CreatedAt > target.CreatedAt || value.CreatedAt == target.CreatedAt && value.Id.CompareTo(target.Id) > 0))
            .Include(value => value.AuthorAccount).Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .Include(value => value.Attachments)
            .OrderBy(value => value.CreatedAt).ThenBy(value => value.Id).Take(half).ToListAsync();
        var result = older.OrderBy(value => value.CreatedAt).ThenBy(value => value.Id).Append(target).Concat(newer)
            .Select(DirectMessageMapper.ToDto).ToArray();
        var cursor = result.Length == 0 ? null : MessageHistoryCursor.Encode(result[0].CreatedAt, result[0].Id);
        return Results.Ok(new MessageHistoryPage<DirectMessageDto>(result, cursor, hasOlder, true, targetId));
    }

    private static async Task<IResult> SearchAsync(
        Guid conversationId, string? q, string? from, string? before, int? limit,
        HttpContext context, IridiumDbContext db, SessionService sessions)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await IsParticipantAsync(conversationId, session.AccountId, db))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var take = Math.Clamp(limit ?? MessageHistoryDefaults.SearchPageSize, 1, MessageHistoryDefaults.MaximumPageSize);
        var query = db.DirectMessages.AsNoTracking().Where(value => value.ConversationId == conversationId &&
                !value.IsDeleted && value.Kind == MessageKind.User)
            .Include(value => value.AuthorAccount).AsQueryable();
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
        var results = found.Select(value => new MessageSearchResultDto(value.Id, null, null, value.ConversationId, null,
            new(value.AuthorAccountId, value.AuthorAccount.Username, value.AuthorAccount.DisplayName), value.Content, value.CreatedAt)).ToArray();
        var next = results.Length == 0 ? null : MessageHistoryCursor.Encode(results[^1].CreatedAt, results[^1].MessageId);
        return Results.Ok(new MessageSearchPageDto(results, next, hasMore));
    }

    private static async Task<IResult> SearchRequestAsync(
        Guid conversationId, MessageSearchRequest request, HttpContext context,
        IridiumDbContext db, SessionService sessions)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await IsParticipantAsync(conversationId, session.AccountId, db))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var criteria = request.Query;
        var take = Math.Clamp(request.Limit, 1, MessageHistoryDefaults.MaximumPageSize);
        var query = db.DirectMessages.AsNoTracking()
            .Where(value => value.ConversationId == conversationId && !value.IsDeleted &&
                            value.Kind == MessageKind.User)
            .Include(value => value.AuthorAccount).AsQueryable();
        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            var text = criteria.Text.ToLower();
            query = query.Where(value => value.Content.ToLower().Contains(text));
        }
        if (criteria.FromAccountId is { } authorId) query = query.Where(value => value.AuthorAccountId == authorId);
        if (criteria.MentionedAccountId is not null || criteria.ChannelId is not null || criteria.AuthorType != MessageAuthorType.User)
            query = query.Where(_ => false);
        if (criteria.BeforeUtc is { } beforeUtc) query = query.Where(value => value.CreatedAt < beforeUtc);
        if (criteria.AfterUtc is { } afterUtc) query = query.Where(value => value.CreatedAt > afterUtc);
        if (criteria.DuringStartUtc is { } duringStart) query = query.Where(value => value.CreatedAt >= duringStart);
        if (criteria.DuringEndUtc is { } duringEnd) query = query.Where(value => value.CreatedAt < duringEnd);
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
        var results = found.Select(value => new MessageSearchResultDto(value.Id, null, null, value.ConversationId, null,
            new(value.AuthorAccountId, value.AuthorAccount.Username, value.AuthorAccount.DisplayName), value.Content, value.CreatedAt)).ToArray();
        var next = results.Length == 0 ? null : MessageHistoryCursor.Encode(results[^1].CreatedAt, results[^1].MessageId);
        return Results.Ok(new MessageSearchPageDto(results, next, hasMore));
    }

    private static async Task<IResult> HideAsync(
        Guid conversationId,
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await IsParticipantAsync(conversationId, session.AccountId, db)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var state = await db.DirectConversationStates.FindAsync([conversationId, session.AccountId]);
        if (state is null)
        {
            state = new DirectConversationState
            {
                ConversationId = conversationId,
                AccountId = session.AccountId,
                Conversation = null!,
                Account = session.Account,
                HiddenAt = DateTimeOffset.UtcNow
            };
            db.DirectConversationStates.Add(state);
        }
        else state.HiddenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> MarkReadAsync(
        Guid conversationId,
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await IsParticipantAsync(conversationId, session.AccountId, db))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var latest = await db.DirectMessages.Where(value => value.ConversationId == conversationId)
            .MaxAsync(value => (DateTimeOffset?)value.CreatedAt);
        var state = await db.DirectConversationStates.FindAsync([conversationId, session.AccountId]);
        if (state is null)
        {
            state = new DirectConversationState
            {
                ConversationId = conversationId,
                AccountId = session.AccountId,
                Conversation = null!,
                Account = session.Account,
                LastReadAt = latest ?? DateTimeOffset.UtcNow
            };
            db.DirectConversationStates.Add(state);
        }
        else state.LastReadAt = latest ?? DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    internal static Task<bool> IsParticipantAsync(Guid conversationId, Guid accountId, IridiumDbContext db) =>
        db.DirectConversations.AnyAsync(value => value.Id == conversationId &&
            (value.ParticipantAAccountId == accountId || value.ParticipantBAccountId == accountId));

    private static (Guid First, Guid Second) Ordered(Guid left, Guid right) =>
        left.CompareTo(right) < 0 ? (left, right) : (right, left);
}
