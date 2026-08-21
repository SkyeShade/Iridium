using Iridium.Server.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Calls;

public sealed class DirectCallAuthorizationService(IridiumDbContext db)
{
    public async Task<DirectCallParties> AuthorizeStartAsync(Guid conversationId, Guid callerAccountId)
    {
        var conversation = await db.DirectConversations.AsNoTracking()
            .Include(value => value.ParticipantAAccount)
            .Include(value => value.ParticipantBAccount)
            .SingleOrDefaultAsync(value => value.Id == conversationId &&
                (value.ParticipantAAccountId == callerAccountId || value.ParticipantBAccountId == callerAccountId))
            ?? throw new HubException("Direct conversation not found for this account.");
        var caller = conversation.ParticipantAAccountId == callerAccountId
            ? conversation.ParticipantAAccount : conversation.ParticipantBAccount;
        var callee = conversation.ParticipantAAccountId == callerAccountId
            ? conversation.ParticipantBAccount : conversation.ParticipantAAccount;
        if (caller.Id == callee.Id) throw new HubException("You cannot call yourself.");
        var blocked = await db.AccountBlocks.AsNoTracking().AnyAsync(value =>
            value.BlockingAccountId == caller.Id && value.BlockedAccountId == callee.Id ||
            value.BlockingAccountId == callee.Id && value.BlockedAccountId == caller.Id);
        if (blocked) throw new HubException("This call is not allowed because one participant has blocked the other.");
        return new(conversation.Id, caller.Id, caller.DisplayName, callee.Id, callee.DisplayName);
    }
}

public sealed record DirectCallParties(Guid ConversationId, Guid CallerId, string CallerDisplayName,
    Guid CalleeId, string CalleeDisplayName);
