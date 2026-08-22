using System.Collections.Concurrent;
using Iridium.Protocol;
using Iridium.Server.Hubs;
using Iridium.Server.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Communities;

public sealed class CommunityRevisionTracker
{
    private readonly ConcurrentDictionary<Guid, long> _revisions = new();

    public long Next(Guid communityId)
    {
        var timestamp = DateTimeOffset.UtcNow.UtcTicks;
        return _revisions.AddOrUpdate(communityId, timestamp,
            (_, current) => Math.Max(checked(current + 1), timestamp));
    }
}

public sealed class CommunityRealtimePublisher(
    CommunityRevisionTracker revisions,
    IHubContext<ChatHub> hub,
    IHostEnvironment environment,
    ILogger<CommunityRealtimePublisher> logger)
{
    public async Task PublishAccessRevokedAsync(
        CommunityAccessRevokedEvent change,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await hub.Clients.Group(ChatHub.AccountGroup(change.AccountId)).SendAsync(
                CommunityHubContract.AccessRevoked, change, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Could not publish Community access revocation for account {AccountId} in {CommunityId}.",
                change.AccountId, change.CommunityId);
        }
    }

    public async Task<long> PublishAsync(
        Guid communityId,
        string change,
        IridiumDbContext db,
        CancellationToken cancellationToken = default)
    {
        var revision = revisions.Next(communityId);
        try
        {
            var accountIds = await db.CommunityMembers.AsNoTracking()
                .Where(value => value.CommunityId == communityId)
                .Select(value => value.AccountId)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            if (accountIds.Length == 0) return revision;
            await hub.Clients.Groups(accountIds.Select(ChatHub.AccountGroup).ToArray()).SendAsync(
                CommunityHubContract.StateChanged,
                new CommunityStateChangedEvent(communityId, change, revision),
                cancellationToken);
            if (environment.IsDevelopment())
                logger.LogInformation(
                    "COMMUNITY REALTIME Mutation={Mutation} CommunityId={CommunityId} Revision={Revision} Recipients={Recipients}",
                    change, communityId, revision, accountIds.Length);
        }
        catch (Exception exception)
        {
            // Persistence has already committed. Reconnect refresh is the recovery path for a missed invalidation.
            logger.LogWarning(exception,
                "Could not publish Community mutation {Mutation} for {CommunityId} at revision {Revision}.",
                change, communityId, revision);
        }
        return revision;
    }
}
