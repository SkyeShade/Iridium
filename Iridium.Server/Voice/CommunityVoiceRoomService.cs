using Iridium.Protocol;

namespace Iridium.Server.Voice;

public sealed record VoiceLeaveResult(Guid CommunityId, Guid ChannelId, VoiceParticipantDto Participant,
    ActiveVoiceRoomDto? Room);
public sealed record VoiceJoinResult(ActiveVoiceRoomDto Room, VoiceParticipantDto Participant,
    VoiceLeaveResult? PreviousRoom, bool AlreadyJoined);

public sealed class CommunityVoiceRoomService(TimeProvider timeProvider, ICommunityVoiceMediaGateway media)
{
    private sealed class Participant
    {
        public required Guid AccountId { get; init; }
        public required string ParticipantId { get; init; }
        public required string DisplayName { get; set; }
        public required string Username { get; init; }
        public required PublicPresence Presence { get; init; }
        public required DateTimeOffset JoinedAt { get; init; }
        public Guid? AvatarPresetId { get; set; }
        public long AvatarRevision { get; set; }
        public bool Muted { get; set; }
        public bool Deafened { get; set; }
        public bool Speaking { get; set; }
    }

    private sealed class Room(Guid communityId, Guid channelId, DateTimeOffset startedAt,
        string communityName, string channelName)
    {
        public Guid CommunityId { get; } = communityId;
        public Guid ChannelId { get; } = channelId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public string CommunityName { get; } = communityName;
        public string ChannelName { get; } = channelName;
        public Dictionary<string, Participant> Participants { get; } = [];
    }

    private readonly object _gate = new();
    private readonly Dictionary<(Guid CommunityId, Guid ChannelId), Room> _rooms = [];
    private readonly Dictionary<string, (Guid CommunityId, Guid ChannelId)> _connectionRooms = [];

    public IReadOnlyList<ActiveVoiceRoomDto> GetRooms(Guid communityId)
    {
        lock (_gate) return _rooms.Values.Where(value => value.CommunityId == communityId)
            .Select(ToDto).OrderBy(value => value.StartedAt).ToArray();
    }

    public (Guid CommunityId, Guid ChannelId)? RoomFor(string participantId)
    {
        lock (_gate) return _connectionRooms.TryGetValue(participantId, out var value) ? value : null;
    }

    public async Task<VoiceJoinResult> JoinAsync(Guid communityId, Guid channelId, Guid accountId,
        string participantId, string displayName, string username, PublicPresence presence, string communityName, string channelName,
        Guid? avatarPresetId = null, long avatarRevision = 0,
        CancellationToken cancellationToken = default)
    {
        VoiceLeaveResult? previous = null;
        Participant participant;
        ActiveVoiceRoomDto result;
        var duplicate = false;
        lock (_gate)
        {
            if (_connectionRooms.TryGetValue(participantId, out var current))
            {
                if (current == (communityId, channelId))
                {
                    var existing = _rooms[current].Participants[participantId];
                    return new(ToDto(_rooms[current]), ToDto(existing), null, true);
                }
                previous = LeaveLocked(participantId);
            }
            var key = (communityId, channelId);
            if (!_rooms.TryGetValue(key, out var room))
            {
                room = new Room(communityId, channelId, timeProvider.GetUtcNow(), communityName, channelName);
                _rooms.Add(key, room);
            }
            participant = new Participant
            {
                AccountId = accountId, ParticipantId = participantId, DisplayName = displayName, Username = username,
                Presence = presence, JoinedAt = timeProvider.GetUtcNow(), AvatarPresetId = avatarPresetId,
                AvatarRevision = avatarRevision
            };
            duplicate = !room.Participants.TryAdd(participantId, participant);
            _connectionRooms[participantId] = key;
            result = ToDto(room);
        }
        if (previous is not null)
            await media.ParticipantLeftAsync(previous.CommunityId, previous.ChannelId,
                previous.Participant.ParticipantId, cancellationToken);
        if (!duplicate)
            await media.ParticipantJoinedAsync(communityId, channelId, participantId, accountId, cancellationToken);
        return new(result, ToDto(participant), previous, duplicate);
    }

    // Compatibility overload for callers that do not yet supply the canonical username.
    public Task<VoiceJoinResult> JoinAsync(Guid communityId, Guid channelId, Guid accountId,
        string participantId, string displayName, PublicPresence presence, string communityName, string channelName,
        CancellationToken cancellationToken = default) =>
        JoinAsync(communityId, channelId, accountId, participantId, displayName, displayName, presence,
            communityName, channelName, cancellationToken: cancellationToken);

    public async Task<VoiceLeaveResult?> LeaveAsync(string participantId, CancellationToken cancellationToken = default)
    {
        VoiceLeaveResult? result;
        Guid communityId = default, channelId = default;
        lock (_gate)
        {
            if (_connectionRooms.TryGetValue(participantId, out var roomKey))
                (communityId, channelId) = roomKey;
            result = LeaveLocked(participantId);
        }
        if (result is not null)
            await media.ParticipantLeftAsync(communityId, channelId, participantId, cancellationToken);
        return result;
    }

    public async Task<VoiceParticipantStateChangedEvent?> SetStateAsync(string participantId, bool muted,
        bool deafened, bool? speaking = null, CancellationToken cancellationToken = default)
    {
        VoiceParticipantStateChangedEvent? result;
        lock (_gate)
        {
            if (!_connectionRooms.TryGetValue(participantId, out var key) ||
                !_rooms[key].Participants.TryGetValue(participantId, out var participant)) return null;
            participant.Deafened = deafened;
            participant.Muted = muted || deafened;
            if (participant.Muted) participant.Speaking = false;
            if (speaking.HasValue) participant.Speaking = speaking.Value && !participant.Muted;
            result = new(key.CommunityId, key.ChannelId, ToDto(participant));
        }
        await media.ParticipantStateChangedAsync(result.CommunityId, result.ChannelId, participantId,
            result.Participant.Muted, result.Participant.Deafened, cancellationToken);
        return result;
    }

    public VoiceParticipantStateChangedEvent? SetSpeaking(string participantId, bool speaking)
    {
        lock (_gate)
        {
            if (!_connectionRooms.TryGetValue(participantId, out var key) ||
                !_rooms[key].Participants.TryGetValue(participantId, out var participant)) return null;
            speaking = speaking && !participant.Muted;
            if (participant.Speaking == speaking) return null;
            participant.Speaking = speaking;
            return new(key.CommunityId, key.ChannelId, ToDto(participant));
        }
    }

    public IReadOnlyList<VoiceParticipantStateChangedEvent> UpdateDisplayName(
        Guid communityId, Guid accountId, string displayName)
        => UpdateDisplayProfile(communityId, accountId, displayName, null, 0, updateAvatar: false);

    public IReadOnlyList<VoiceParticipantStateChangedEvent> UpdateDisplayProfile(
        Guid communityId, Guid accountId, string displayName, Guid? avatarPresetId, long avatarRevision,
        bool updateAvatar = true)
    {
        lock (_gate)
        {
            var changed = new List<VoiceParticipantStateChangedEvent>();
            foreach (var room in _rooms.Values.Where(value => value.CommunityId == communityId))
            foreach (var participant in room.Participants.Values.Where(value => value.AccountId == accountId))
            {
                participant.DisplayName = displayName;
                if (updateAvatar)
                {
                    participant.AvatarPresetId = avatarPresetId;
                    participant.AvatarRevision = avatarRevision;
                }
                changed.Add(new(communityId, room.ChannelId, ToDto(participant)));
            }
            return changed;
        }
    }

    private VoiceLeaveResult? LeaveLocked(string participantId)
    {
        if (!_connectionRooms.Remove(participantId, out var key) || !_rooms.TryGetValue(key, out var room) ||
            !room.Participants.Remove(participantId, out var participant)) return null;
        if (room.Participants.Count == 0) _rooms.Remove(key);
        return new(key.CommunityId, key.ChannelId, ToDto(participant),
            room.Participants.Count == 0 ? null : ToDto(room));
    }

    private ActiveVoiceRoomDto ToDto(Room room) => new(room.CommunityId, room.ChannelId, room.StartedAt,
        room.Participants.Values.Select(ToDto).OrderBy(value => value.JoinedAt).ToArray(),
        room.CommunityName, room.ChannelName);
    private VoiceParticipantDto ToDto(Participant value) => new(value.AccountId, value.ParticipantId,
        value.DisplayName, value.Presence, value.JoinedAt, value.Muted, value.Deafened, value.Speaking, media.Status,
        value.Username, value.AvatarPresetId, value.AvatarRevision);
}
