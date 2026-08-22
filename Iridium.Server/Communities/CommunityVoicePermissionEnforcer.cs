using Iridium.Protocol;
using Iridium.Server.Hubs;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Iridium.Server.Voice;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Communities;

public sealed class CommunityVoicePermissionEnforcer(
    CommunityVoiceRoomService rooms,
    VoiceStreamRegistry streams,
    CommunityAuthorizationService authorization,
    IHubContext<ChatHub> hub)
{
    public async Task EnforceAsync(Guid communityId, IridiumDbContext db, CancellationToken cancellationToken = default)
    {
        var accountIds = await db.CommunityMembers.AsNoTracking().Where(value => value.CommunityId == communityId)
            .Select(value => value.AccountId).ToListAsync(cancellationToken);
        var ownerId = await db.Communities.AsNoTracking().Where(value => value.Id == communityId)
            .Select(value => (Guid?)value.OwnerAccountId).SingleOrDefaultAsync(cancellationToken);
        if (ownerId.HasValue) accountIds.Add(ownerId.Value);
        var accountGroups = accountIds.Distinct().Select(ChatHub.AccountGroup).ToArray();

        foreach (var room in rooms.GetRooms(communityId))
        foreach (var participant in room.Participants.ToArray())
        {
            var access = await authorization.GetChannelAccessAsync(communityId, room.ChannelId,
                participant.AccountId, db);
            if (!access.Has(CommunityPermission.ViewChannels) || !access.Has(CommunityPermission.ConnectVoice))
            {
                var ended = streams.RemoveConnection(participant.ParticipantId, "PermissionRevoked");
                var left = await rooms.LeaveAsync(participant.ParticipantId, cancellationToken);
                if (left is not null && accountGroups.Length > 0)
                    await hub.Clients.Groups(accountGroups).SendAsync(CommunityVoiceHubContract.ParticipantLeft,
                        new VoiceParticipantLeftEvent(left.CommunityId, left.ChannelId, left.Participant.AccountId,
                            left.Participant.ParticipantId, left.Room), cancellationToken);
                foreach (var stream in ended)
                    if (accountGroups.Length > 0)
                        await hub.Clients.Groups(accountGroups).SendAsync(VoiceStreamHubContract.Ended, stream,
                            cancellationToken);
                continue;
            }
            if (!access.Has(CommunityPermission.SpeakVoice) && !participant.Muted)
            {
                var changed = await rooms.SetStateAsync(participant.ParticipantId, true, participant.Deafened,
                    cancellationToken: cancellationToken);
                if (changed is not null && accountGroups.Length > 0)
                    await hub.Clients.Groups(accountGroups).SendAsync(CommunityVoiceHubContract.ParticipantStateChanged,
                        changed, cancellationToken);
            }
            if (!access.Has(CommunityPermission.ShareScreen))
            {
                foreach (var stream in streams.Get(VoiceMediaSessionKind.CommunityVoice, room.ChannelId)
                             .Where(value => value.OwnerParticipantId == participant.ParticipantId).ToArray())
                {
                    var ended = streams.Stop(VoiceMediaSessionKind.CommunityVoice, room.ChannelId, stream.StreamId,
                        participant.ParticipantId, "PermissionRevoked");
                    if (ended is not null && accountGroups.Length > 0)
                        await hub.Clients.Groups(accountGroups).SendAsync(VoiceStreamHubContract.Ended, ended,
                            cancellationToken);
                }
            }
        }
    }
}
