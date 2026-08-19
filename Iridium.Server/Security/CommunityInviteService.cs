using System.Data;
using Iridium.Protocol;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Security;

public sealed class CommunityInviteService
{
    public async Task<CommunityInvite?> FindAsync(string token, IridiumDbContext db)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return null;
        var hash = InviteTokenService.Hash(token);
        return await db.CommunityInvites.Include(value => value.Community)
            .SingleOrDefaultAsync(value => value.TokenHash == hash);
    }

    public async Task<CommunityInviteJoinOutcome> JoinAsync(
        string token, NodeAccount account, IridiumDbContext db, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var invite = await FindAsync(token, db);
        if (invite is null) throw new CommunityInviteJoinException(CommunityInviteStatus.NotFound, "Invite not found.");
        var status = InviteTokenService.GetStatus(invite, DateTimeOffset.UtcNow);
        if (status != CommunityInviteStatus.Valid)
            throw new CommunityInviteJoinException(status, $"This invite is {status.ToString().ToLowerInvariant()}.");
        if (await db.CommunityBans.AnyAsync(value =>
                value.CommunityId == invite.CommunityId && value.AccountId == account.Id, cancellationToken))
            throw new CommunityInviteJoinException(null, "You are banned from this Community.");

        var existing = await db.CommunityMembers.AnyAsync(value =>
            value.CommunityId == invite.CommunityId && value.AccountId == account.Id, cancellationToken);
        if (existing)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CommunityInviteJoinOutcome(invite.Community, true, invite.Uses);
        }

        db.CommunityMembers.Add(new CommunityMember
        {
            CommunityId = invite.CommunityId, AccountId = account.Id, Community = invite.Community,
            Account = account, JoinedAt = DateTimeOffset.UtcNow
        });
        invite.Uses++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CommunityInviteJoinOutcome(invite.Community, false, invite.Uses);
    }
}

public sealed record CommunityInviteJoinOutcome(Community Community, bool AlreadyMember, int Uses);

public sealed class CommunityInviteJoinException(CommunityInviteStatus? status, string message) : Exception(message)
{
    public CommunityInviteStatus? Status { get; } = status;
}
