using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR.Client;

namespace Iridium.Client.Core;

public sealed class CommunityVoiceSession(RealtimeConnectionService realtime, NodeSession nodeSession,
    ICommunityVoiceMediaClient media) : ICommunityVoiceControlSession, IAsyncDisposable
{
    private readonly Dictionary<Guid, ActiveVoiceRoomDto> _rooms = [];
    private readonly List<IDisposable> _handlers = [];
    private HubConnection? _connection;
    private Guid? _observedCommunityId;
    private bool _disposed;
    private bool _mediaConnected;
    private bool _mediaEventsWired;

    public event Action? Changed;
    public IReadOnlyCollection<ActiveVoiceRoomDto> Rooms => _rooms.Values;
    public ActiveVoiceRoomDto? CurrentRoom { get; private set; }
    public bool Muted { get; private set; }
    public bool Deafened { get; private set; }
    public bool IsJoined => CurrentRoom is not null;
    public string? MediaError { get; private set; }
    public CommunityVoiceMediaSessionDto? MediaSession { get; private set; }

    public ActiveVoiceRoomDto? Room(Guid channelId) => _rooms.GetValueOrDefault(channelId);

    public async Task ObserveCommunityAsync(Guid communityId, CancellationToken cancellationToken = default)
    {
        var connection = await realtime.EnsureConnectedAsync("community voice rooms", cancellationToken);
        Wire(connection);
        _observedCommunityId = communityId;
        var rooms = await connection.InvokeAsync<IReadOnlyList<ActiveVoiceRoomDto>>(
            CommunityVoiceHubContract.GetRooms, communityId, cancellationToken);
        _rooms.Clear();
        foreach (var room in rooms) _rooms[room.ChannelId] = room;
        Changed?.Invoke();
    }

    public async Task JoinAsync(Guid communityId, Guid channelId, CancellationToken cancellationToken = default)
    {
        EnsureMediaEvents();
        var connection = await realtime.EnsureConnectedAsync("join Community voice", cancellationToken);
        Wire(connection);
        if (CurrentRoom?.CommunityId == communityId && CurrentRoom.ChannelId == channelId)
        {
            Changed?.Invoke();
            return;
        }
        if (CurrentRoom is not null) await LeaveAsync(cancellationToken);
        var room = await connection.InvokeAsync<ActiveVoiceRoomDto>(CommunityVoiceHubContract.Join,
            communityId, channelId, cancellationToken);
        CurrentRoom = room;
        MediaSession = await connection.InvokeAsync<CommunityVoiceMediaSessionDto>(
            CommunityVoiceHubContract.GetMediaSession, cancellationToken);
        _rooms[channelId] = room;
        Muted = room.Participants.FirstOrDefault(value => value.ParticipantId == connection.ConnectionId)?.Muted ?? false;
        Deafened = room.Participants.FirstOrDefault(value => value.ParticipantId == connection.ConnectionId)?.Deafened ?? false;
        MediaError = null;
        try
        {
            await media.ConnectAsync(MediaSession, room,
                nodeSession.Account?.Id ?? throw new InvalidOperationException("The active account changed."),
                cancellationToken);
            _mediaConnected = true;
        }
        catch
        {
            await LeaveAsync(cancellationToken);
            throw;
        }
        Changed?.Invoke();
    }

    public async Task LeaveAsync(CancellationToken cancellationToken = default)
    {
        var previousRoom = CurrentRoom;
        if (_mediaConnected)
        {
            await media.DisconnectAsync("Community voice session left", cancellationToken);
            _mediaConnected = false;
        }
        try
        {
            if (_connection?.State == HubConnectionState.Connected)
                await _connection.InvokeAsync(CommunityVoiceHubContract.Leave, cancellationToken);
        }
        catch
        {
            if (previousRoom is not null && MediaSession is not null)
            {
                await media.ConnectAsync(MediaSession, previousRoom,
                    nodeSession.Account?.Id ?? Guid.Empty, cancellationToken);
                _mediaConnected = true;
            }
            throw;
        }
        CurrentRoom = null;
        MediaSession = null;
        MediaError = null;
        Muted = Deafened = false;
        Changed?.Invoke();
    }

    public async Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        if (_connection?.State != HubConnectionState.Connected || CurrentRoom is null) return;
        Muted = muted || Deafened;
        if (_mediaConnected) await media.SetMutedAsync(Muted, cancellationToken);
        await _connection.InvokeAsync(CommunityVoiceHubContract.SetState, Muted, Deafened, cancellationToken);
        Changed?.Invoke();
    }

    public async Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken = default)
    {
        if (_connection?.State != HubConnectionState.Connected || CurrentRoom is null) return;
        Deafened = deafened;
        if (deafened) Muted = true;
        if (_mediaConnected)
        {
            await media.SetMutedAsync(Muted, cancellationToken);
            await media.SetDeafenedAsync(Deafened, cancellationToken);
        }
        await _connection.InvokeAsync(CommunityVoiceHubContract.SetState, Muted, Deafened, cancellationToken);
        Changed?.Invoke();
    }

    public Task SetSpeakingAsync(bool speaking, CancellationToken cancellationToken = default) =>
        _connection?.State == HubConnectionState.Connected && CurrentRoom is not null
            ? _connection.InvokeAsync(CommunityVoiceHubContract.SetSpeaking, speaking, cancellationToken)
            : Task.CompletedTask;

    private void Wire(HubConnection connection)
    {
        if (ReferenceEquals(_connection, connection)) return;
        foreach (var handler in _handlers) handler.Dispose();
        _handlers.Clear();
        _connection = connection;
        _handlers.Add(connection.On<VoiceParticipantJoinedEvent>(CommunityVoiceHubContract.ParticipantJoined,
            async value =>
            {
                ApplyRoom(value.Room);
                if (_mediaConnected && value.Participant.ParticipantId != connection.ConnectionId)
                    await media.ParticipantJoinedAsync(value.Participant);
            }));
        _handlers.Add(connection.On<VoiceParticipantLeftEvent>(CommunityVoiceHubContract.ParticipantLeft,
            async value =>
            {
                if (value.Room is null) _rooms.Remove(value.ChannelId);
                else ApplyRoom(value.Room);
                if (CurrentRoom?.ChannelId == value.ChannelId)
                    CurrentRoom = value.ParticipantId == connection.ConnectionId ? null : value.Room;
                if (_mediaConnected) await media.ParticipantLeftAsync(value.ParticipantId);
                Changed?.Invoke();
            }));
        _handlers.Add(connection.On<VoiceParticipantStateChangedEvent>(CommunityVoiceHubContract.ParticipantStateChanged,
            value =>
            {
                if (_rooms.TryGetValue(value.ChannelId, out var room))
                    ApplyRoom(room with { Participants = room.Participants.Select(item =>
                        item.ParticipantId == value.Participant.ParticipantId ? value.Participant : item).ToArray() });
            }));
        _handlers.Add(connection.On<CommunityVoiceMediaDescriptionEvent>(CommunityVoiceHubContract.MediaOffer,
            value => media.HandleOfferAsync(value)));
        _handlers.Add(connection.On<CommunityVoiceMediaDescriptionEvent>(CommunityVoiceHubContract.MediaAnswer,
            value => media.HandleAnswerAsync(value)));
        _handlers.Add(connection.On<CommunityVoiceMediaIceCandidateEvent>(CommunityVoiceHubContract.MediaIceCandidate,
            value => media.HandleIceCandidateAsync(value)));
        connection.Reconnected += async _ =>
        {
            var previous = CurrentRoom;
            CurrentRoom = null;
            if (previous is not null)
            {
                try { await JoinAsync(previous.CommunityId, previous.ChannelId); }
                catch { Changed?.Invoke(); }
            }
            else if (_observedCommunityId is { } communityId)
            {
                try { await ObserveCommunityAsync(communityId); } catch { }
            }
        };
        connection.Closed += async _ =>
        {
            if (_mediaConnected) await media.DisconnectAsync("Community voice signaling disconnected");
            _mediaConnected = false;
            CurrentRoom = null;
            MediaSession = null;
            Changed?.Invoke();
        };
    }

    private void EnsureMediaEvents()
    {
        if (_mediaEventsWired) return;
        _mediaEventsWired = true;
        media.SpeakingChanged += MediaSpeakingChangedAsync;
        media.Error += MediaErrorAsync;
        media.OfferCreated += MediaOfferCreatedAsync;
        media.AnswerCreated += MediaAnswerCreatedAsync;
        media.IceCandidateGenerated += MediaIceCandidateGeneratedAsync;
    }

    private async Task MediaSpeakingChangedAsync(bool speaking)
    {
        if (Muted) speaking = false;
        await SetSpeakingAsync(speaking);
    }

    private Task MediaErrorAsync(string error)
    {
        MediaError = error;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    private Task MediaOfferCreatedAsync(string targetParticipantId, Guid negotiationId,
        WebRtcSessionDescription description) =>
        _connection?.InvokeAsync(CommunityVoiceHubContract.SendMediaOffer, targetParticipantId, negotiationId,
            description) ?? Task.CompletedTask;

    private Task MediaAnswerCreatedAsync(string targetParticipantId, Guid negotiationId,
        WebRtcSessionDescription description) =>
        _connection?.InvokeAsync(CommunityVoiceHubContract.SendMediaAnswer, targetParticipantId, negotiationId,
            description) ?? Task.CompletedTask;

    private Task MediaIceCandidateGeneratedAsync(string targetParticipantId, Guid negotiationId,
        WebRtcIceCandidate candidate) =>
        _connection?.InvokeAsync(CommunityVoiceHubContract.SendMediaIceCandidate, targetParticipantId, negotiationId,
            candidate) ?? Task.CompletedTask;

    private void ApplyRoom(ActiveVoiceRoomDto room)
    {
        _rooms[room.ChannelId] = room;
        if (CurrentRoom?.ChannelId == room.ChannelId) CurrentRoom = room;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await LeaveAsync(); } catch { }
        foreach (var handler in _handlers) handler.Dispose();
        _handlers.Clear();
        media.SpeakingChanged -= MediaSpeakingChangedAsync;
        media.Error -= MediaErrorAsync;
        media.OfferCreated -= MediaOfferCreatedAsync;
        media.AnswerCreated -= MediaAnswerCreatedAsync;
        media.IceCandidateGenerated -= MediaIceCandidateGeneratedAsync;
        await media.DisposeAsync();
    }
}
