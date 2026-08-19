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
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (!await IsParticipantAsync(conversationId, session.AccountId, db)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var take = Math.Clamp(limit ?? 75, 1, 100);
        var messages = await db.DirectMessages
            .Include(value => value.AuthorAccount)
            .Include(value => value.ReplyToMessage).ThenInclude(value => value!.AuthorAccount)
            .Where(value => value.ConversationId == conversationId)
            .OrderByDescending(value => value.CreatedAt)
            .Take(take)
            .ToListAsync();
        return Results.Ok(messages.OrderBy(value => value.CreatedAt).Select(DirectMessageMapper.ToDto).ToArray());
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
