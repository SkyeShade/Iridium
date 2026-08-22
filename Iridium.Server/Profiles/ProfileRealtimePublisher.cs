using Iridium.Protocol;
using Iridium.Server.Hubs;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Profiles;

public sealed class ProfileRealtimePublisher(
    IHubContext<ChatHub> hub,
    ILogger<ProfileRealtimePublisher> logger)
{
    public async Task PublishAsync(Guid accountId, long avatarRevision, IridiumDbContext db,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recipients = new HashSet<Guid> { accountId };
            var communityIds = await db.CommunityMembers.AsNoTracking()
                .Where(value => value.AccountId == accountId).Select(value => value.CommunityId)
                .ToArrayAsync(cancellationToken);
            if (communityIds.Length > 0)
                recipients.UnionWith(await db.CommunityMembers.AsNoTracking()
                    .Where(value => communityIds.Contains(value.CommunityId)).Select(value => value.AccountId)
                    .ToArrayAsync(cancellationToken));
            recipients.UnionWith(await db.Friendships.AsNoTracking()
                .Where(value => value.Status == FriendshipState.Accepted &&
                                (value.RequesterAccountId == accountId || value.AddresseeAccountId == accountId))
                .Select(value => value.RequesterAccountId == accountId
                    ? value.AddresseeAccountId : value.RequesterAccountId)
                .ToArrayAsync(cancellationToken));
            recipients.UnionWith(await db.DirectConversations.AsNoTracking()
                .Where(value => value.ParticipantAAccountId == accountId || value.ParticipantBAccountId == accountId)
                .Select(value => value.ParticipantAAccountId == accountId
                    ? value.ParticipantBAccountId : value.ParticipantAAccountId)
                .ToArrayAsync(cancellationToken));
            await hub.Clients.Groups(recipients.Select(ChatHub.AccountGroup).ToArray()).SendAsync(
                ProfileHubContract.Updated, new ProfileUpdatedEvent(accountId, avatarRevision), cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not publish profile update for {AccountId}.", accountId);
        }
    }
}
