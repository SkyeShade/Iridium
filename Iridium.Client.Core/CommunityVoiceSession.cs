using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Iridium.Client.Core;

public sealed class CommunityVoiceSession(RealtimeConnectionService realtime, NodeSession nodeSession,
    ICommunityVoiceMediaClient media, ILogger<CommunityVoiceSession> logger) : ICommunityVoiceControlSession, IAsyncDisposable
{
    private readonly Dictionary<Guid, ActiveVoiceRoomDto> _rooms = [];
    private readonly List<IDisposable> _handlers = [];
    private IDisposable? _recoveryRegistration;
    private bool _lifecycleRegistered;
    private HubConnection? _connection;
    private Guid? _observedCommunityId;
    private bool _disposed;
    private bool _mediaConnected;
    private bool _mediaEventsWired;
    private readonly List<PublishedVoiceStreamDto> _publishedStreams = [];

    public event Action? Changed;
    public IReadOnlyCollection<ActiveVoiceRoomDto> Rooms => _rooms.Values;
    public ActiveVoiceRoomDto? CurrentRoom { get; private set; }
    public bool Muted { get; private set; }
    public bool Deafened { get; private set; }
    public bool IsJoined => CurrentRoom is not null;
    public string? MediaError { get; private set; }
    public CommunityVoiceMediaSessionDto? MediaSession { get; private set; }
    public IReadOnlyList<PublishedVoiceStreamDto> PublishedStreams => _publishedStreams;
    public PublishedVoiceStreamDto? WatchedStream { get; private set; }

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
        _publishedStreams.Clear();
        _publishedStreams.AddRange(await connection.InvokeAsync<IReadOnlyList<PublishedVoiceStreamDto>>(
            VoiceStreamHubContract.GetPublished, VoiceMediaSessionKind.CommunityVoice, channelId, cancellationToken));
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
            MediaSession = MediaSession with { Status = CommunityVoiceMediaStatus.Connected };
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
        _publishedStreams.Clear();
        WatchedStream = null;
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

    public async Task StartScreenShareAsync(CancellationToken cancellationToken = default)
    {
        if (_connection?.State != HubConnectionState.Connected || CurrentRoom is null || !_mediaConnected) return;
        var publication = await media.StartScreenShareAsync(cancellationToken);
        PublishedVoiceStreamDto? publishedStream = null;
        try
        {
            publishedStream = await _connection.InvokeAsync<PublishedVoiceStreamDto>(VoiceStreamHubContract.Publish,
                VoiceMediaSessionKind.CommunityVoice, CurrentRoom.ChannelId,
                new PublishVoiceStreamRequest(publication.StreamId, publication.Kind, publication.HasAudio,
                    publication.MediaStreamId), cancellationToken);
            ApplyPublishedStream(publishedStream);
        }
        catch
        {
            await media.StopScreenShareAsync("PublicationRejected", cancellationToken);
            throw;
        }
        await WatchStreamAsync(publishedStream.StreamId, cancellationToken);
    }

    public async Task SwitchScreenShareAsync(CancellationToken cancellationToken = default)
    {
        var accountId = nodeSession.Account?.Id;
        var stream = _publishedStreams.FirstOrDefault(value => value.OwnerAccountId == accountId &&
            value.Kind == VoicePublishedStreamKind.ScreenShare);
        if (stream is null || CurrentRoom is null || _connection?.State != HubConnectionState.Connected) return;
        var publication = await media.SwitchScreenShareAsync(cancellationToken);
        var updated = await _connection.InvokeAsync<PublishedVoiceStreamDto>(VoiceStreamHubContract.Update,
            VoiceMediaSessionKind.CommunityVoice, CurrentRoom.ChannelId, stream.StreamId,
            publication.HasAudio, cancellationToken);
        ApplyPublishedStream(updated);
    }

    public async Task StopScreenShareAsync(string reason = "UserStoppedInIridium",
        CancellationToken cancellationToken = default)
    {
        if (CurrentRoom is null) return;
        var accountId = nodeSession.Account?.Id;
        var stream = _publishedStreams.FirstOrDefault(value => value.OwnerAccountId == accountId &&
            value.Kind == VoicePublishedStreamKind.ScreenShare);
        await media.StopScreenShareAsync(reason, cancellationToken);
        if (stream is null || _connection?.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync(VoiceStreamHubContract.StopPublishing,
            VoiceMediaSessionKind.CommunityVoice, CurrentRoom.ChannelId, stream.StreamId, reason, cancellationToken);
        ApplyEndedStream(stream.StreamId);
    }

    public async Task WatchStreamAsync(Guid streamId, CancellationToken cancellationToken = default)
    {
        if (_connection?.State != HubConnectionState.Connected || CurrentRoom is null) return;
        var stream = _publishedStreams.FirstOrDefault(value => value.StreamId == streamId)
            ?? throw new InvalidOperationException("That stream is no longer available.");
        if (WatchedStream is not null) await StopWatchingAsync(cancellationToken);
        if (stream.OwnerAccountId != nodeSession.Account?.Id)
            await _connection.InvokeAsync(VoiceStreamHubContract.Watch, VoiceMediaSessionKind.CommunityVoice,
                CurrentRoom.ChannelId, streamId, cancellationToken);
        WatchedStream = stream;
        await media.SetStreamSubscriptionAsync(stream.MediaStreamId, true, cancellationToken);
        Changed?.Invoke();
    }

    public async Task StopWatchingAsync(CancellationToken cancellationToken = default)
    {
        var stream = WatchedStream;
        WatchedStream = null;
        if (stream is not null && stream.OwnerAccountId != nodeSession.Account?.Id &&
            _connection?.State == HubConnectionState.Connected)
            await _connection.InvokeAsync(VoiceStreamHubContract.StopWatching, stream.StreamId, cancellationToken);
        if (stream is not null)
            await media.SetStreamSubscriptionAsync(stream.MediaStreamId, false, cancellationToken);
        Changed?.Invoke();
    }

    public Task AttachWatchedStreamAsync(string elementId, int volumePercent = 100,
        CancellationToken cancellationToken = default) =>
        WatchedStream is { } stream
            ? media.AttachStreamViewerAsync(stream.MediaStreamId, elementId, audioMuted: !stream.HasAudio,
                volumePercent, cancellationToken)
            : Task.CompletedTask;

    public Task DetachWatchedStreamAsync(string elementId, CancellationToken cancellationToken = default) =>
        media.DetachStreamViewerAsync(elementId, cancellationToken);

    public Task SetStreamAudioMutedAsync(string elementId, bool muted,
        CancellationToken cancellationToken = default) =>
        media.SetStreamAudioMutedAsync(elementId, muted, cancellationToken);

    public Task SetStreamAudioVolumeAsync(string elementId, int volumePercent,
        CancellationToken cancellationToken = default) =>
        media.SetStreamAudioVolumeAsync(elementId, volumePercent, cancellationToken);

    public Task RequestStreamFullscreenAsync(string elementId, CancellationToken cancellationToken = default) =>
        media.RequestStreamFullscreenAsync(elementId, cancellationToken);

    public Task<string?> CaptureStreamThumbnailAsync(string mediaStreamId,
        CancellationToken cancellationToken = default) =>
        media.CaptureStreamThumbnailAsync(mediaStreamId, cancellationToken);

    private void Wire(HubConnection connection)
    {
        EnsureRecoveryRegistration();
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
                foreach (var stream in _publishedStreams.Where(item => item.OwnerParticipantId == value.ParticipantId)
                             .Select(item => item.StreamId).ToArray()) ApplyEndedStream(stream);
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
        _handlers.Add(connection.On<VoiceStreamPublishedEvent>(VoiceStreamHubContract.Published,
            value => { ApplyPublishedStream(value.Stream); }));
        _handlers.Add(connection.On<VoiceStreamEndedEvent>(VoiceStreamHubContract.Ended,
            value => { if (value.SessionKind == VoiceMediaSessionKind.CommunityVoice) ApplyEndedStream(value.StreamId); }));
    }

    private void EnsureRecoveryRegistration()
    {
        _recoveryRegistration ??= realtime.RegisterRecoveryHandler("community-voice", RecoverSignalingAsync);
        if (_lifecycleRegistered) return;
        realtime.LifecycleChanged += RealtimeLifecycleChanged;
        _lifecycleRegistered = true;
    }

    private void RealtimeLifecycleChanged(RealtimeLifecycleSnapshot snapshot)
    {
        if (_disposed || snapshot.Connection is null || !ReferenceEquals(_connection, snapshot.Connection)) return;
        if (snapshot.State == RealtimeLifecycleState.Disconnected)
            _ = HandleSignalingClosedAsync(snapshot.Connection);
    }

    private async Task HandleSignalingClosedAsync(HubConnection disconnectedConnection)
    {
        if (_disposed || !ReferenceEquals(_connection, disconnectedConnection)) return;
        try
        {
            if (_mediaConnected) await media.DisconnectAsync("Community voice signaling disconnected");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not disconnect Community voice media after signaling closed.");
        }
        if (_disposed || !ReferenceEquals(_connection, disconnectedConnection)) return;
        _mediaConnected = false;
        CurrentRoom = null;
        MediaSession = null;
        _publishedStreams.Clear();
        WatchedStream = null;
        Changed?.Invoke();
    }

    private async Task RecoverSignalingAsync(RealtimeRecoveryContext context, CancellationToken cancellationToken)
    {
        if (_disposed || !ReferenceEquals(_connection, context.Connection)) return;
        var previous = CurrentRoom;
        if (previous is not null)
        {
            var room = await context.Connection.InvokeAsync<ActiveVoiceRoomDto>(CommunityVoiceHubContract.Join,
                previous.CommunityId, previous.ChannelId, cancellationToken);
            var mediaSession = await context.Connection.InvokeAsync<CommunityVoiceMediaSessionDto>(
                CommunityVoiceHubContract.GetMediaSession, cancellationToken);
            var streams = await context.Connection.InvokeAsync<IReadOnlyList<PublishedVoiceStreamDto>>(
                VoiceStreamHubContract.GetPublished, VoiceMediaSessionKind.CommunityVoice, previous.ChannelId,
                cancellationToken);
            if (_disposed || !ReferenceEquals(_connection, context.Connection)) return;
            CurrentRoom = room;
            MediaSession = mediaSession;
            _rooms[room.ChannelId] = room;
            _publishedStreams.Clear();
            _publishedStreams.AddRange(streams);
            Muted = room.Participants.FirstOrDefault(value => value.ParticipantId == context.Connection.ConnectionId)?.Muted ?? false;
            Deafened = room.Participants.FirstOrDefault(value => value.ParticipantId == context.Connection.ConnectionId)?.Deafened ?? false;
            if (!_mediaConnected)
            {
                await media.ConnectAsync(mediaSession, room,
                    nodeSession.Account?.Id ?? throw new InvalidOperationException("The active account changed."),
                    cancellationToken);
                _mediaConnected = true;
            }
            logger.LogInformation("Restored Community voice signaling for {CommunityId}/{ChannelId} without replacing healthy media.",
                previous.CommunityId, previous.ChannelId);
        }
        else if (_observedCommunityId is { } communityId)
        {
            var rooms = await context.Connection.InvokeAsync<IReadOnlyList<ActiveVoiceRoomDto>>(
                CommunityVoiceHubContract.GetRooms, communityId, cancellationToken);
            if (_disposed || !ReferenceEquals(_connection, context.Connection)) return;
            _rooms.Clear();
            foreach (var room in rooms) _rooms[room.ChannelId] = room;
            logger.LogInformation("Restored Community voice observation for {CommunityId}.", communityId);
        }
        Changed?.Invoke();
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
        media.ScreenShareEnded += MediaScreenShareEndedAsync;
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

    private Task MediaScreenShareEndedAsync(string reason) => StopScreenShareAsync(reason);

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

    private void ApplyPublishedStream(PublishedVoiceStreamDto stream)
    {
        _publishedStreams.RemoveAll(value => value.StreamId == stream.StreamId ||
            value.OwnerAccountId == stream.OwnerAccountId && value.Kind == stream.Kind);
        _publishedStreams.Add(stream);
        if (WatchedStream?.StreamId == stream.StreamId) WatchedStream = stream;
        Changed?.Invoke();
    }

    private void ApplyEndedStream(Guid streamId)
    {
        _publishedStreams.RemoveAll(value => value.StreamId == streamId);
        if (WatchedStream?.StreamId == streamId) WatchedStream = null;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _recoveryRegistration?.Dispose();
        _recoveryRegistration = null;
        if (_lifecycleRegistered)
        {
            realtime.LifecycleChanged -= RealtimeLifecycleChanged;
            _lifecycleRegistered = false;
        }
        try { await LeaveAsync(); } catch { }
        foreach (var handler in _handlers) handler.Dispose();
        _handlers.Clear();
        media.SpeakingChanged -= MediaSpeakingChangedAsync;
        media.Error -= MediaErrorAsync;
        media.OfferCreated -= MediaOfferCreatedAsync;
        media.AnswerCreated -= MediaAnswerCreatedAsync;
        media.IceCandidateGenerated -= MediaIceCandidateGeneratedAsync;
        media.ScreenShareEnded -= MediaScreenShareEndedAsync;
        await media.DisposeAsync();
    }
}
