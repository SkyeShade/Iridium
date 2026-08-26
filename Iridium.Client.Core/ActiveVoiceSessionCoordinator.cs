using Iridium.Protocol;

namespace Iridium.Client.Core;

public enum ActiveVoiceSessionKind
{
    DirectCall,
    CommunityVoiceChannel
}

public enum StreamViewerMode { None, Full, Floating }

public enum FloatingStreamPosition
{
    TopLeft, TopMiddle, TopRight, BottomLeft, BottomMiddle, BottomRight
}

public sealed record ActiveVoiceSession(
    ActiveVoiceSessionKind Kind,
    Guid SessionId,
    string DisplayName,
    Guid? CommunityId,
    Guid? ChannelId,
    Guid? DirectConversationId,
    DateTimeOffset? StartedAt,
    CallConnectionState ConnectionState,
    bool Muted,
    bool Deafened,
    bool CanPublishMedia,
    IReadOnlyList<PublishedVoiceStreamDto> PublishedStreams,
    PublishedVoiceStreamDto? WatchedStream);

public sealed class ActiveVoiceSessionCoordinator : IAsyncDisposable
{
    private readonly IDirectVoiceSession _direct;
    private readonly ICommunityVoiceControlSession _community;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly HashSet<Guid> _mutedStreamAudio = [];
    private readonly Dictionary<Guid, string> _streamThumbnails = [];
    private CancellationTokenSource? _thumbnailLoopCancellation;
    private Guid? _lastWatchedStreamId;
    private bool _disposed;

    public ActiveVoiceSessionCoordinator(IDirectVoiceSession direct, ICommunityVoiceControlSession community)
    {
        _direct = direct;
        _community = community;
        _direct.Changed += UnderlyingChanged;
        _community.Changed += UnderlyingChanged;
    }

    public event Action? Changed;
    public ActiveVoiceSession? Current => ProjectCurrent();
    public IncomingCallEvent? IncomingCall => _direct.IncomingCall;
    public IDirectVoiceSession DirectCalls => _direct;
    public ICommunityVoiceControlSession CommunityVoice => _community;
    public IReadOnlyList<PublishedVoiceStreamDto> PublishedStreams => Current?.PublishedStreams ?? [];
    public PublishedVoiceStreamDto? WatchedStream => Current?.WatchedStream;
    public StreamViewerMode ViewerMode { get; private set; }
    public IReadOnlyDictionary<Guid, string> StreamThumbnails => _streamThumbnails;
    public Guid? LocalAccountId => _direct.AccountId ?? (_community.MediaSession?.ParticipantId is { } participantId
        ? _community.CurrentRoom?.Participants.FirstOrDefault(value => value.ParticipantId == participantId)?.AccountId
        : null);
    public bool IsStreamAudioMuted(Guid streamId) => _mutedStreamAudio.Contains(streamId);
    public string? GetStreamThumbnail(Guid streamId) =>
        _streamThumbnails.TryGetValue(streamId, out var value) ? value : null;

    public async Task StartDirectAsync(DirectConversationDto conversation,
        CancellationToken cancellationToken = default) => await SwitchAsync(async token =>
    {
        await LeaveCurrentUnsafeAsync("starting a Direct Call", token);
        await _direct.StartAsync(conversation, token);
    }, cancellationToken);

    public async Task AcceptIncomingDirectAsync(CancellationToken cancellationToken = default) =>
        await SwitchAsync(async token =>
        {
            if (_direct.IncomingCall is null) return;
            if (_community.CurrentRoom is not null) await _community.LeaveAsync(token);
            if (_direct.CurrentCall is not null) await _direct.HangUpAsync(token);
            await _direct.AcceptAsync(token);
        }, cancellationToken);

    public Task DeclineIncomingDirectAsync(CancellationToken cancellationToken = default) =>
        _direct.DeclineAsync(cancellationToken);

    public async Task JoinCommunityAsync(Guid communityId, Guid channelId,
        CancellationToken cancellationToken = default) => await SwitchAsync(async token =>
    {
        if (_community.CurrentRoom?.CommunityId == communityId &&
            _community.CurrentRoom.ChannelId == channelId) return;
        if (_direct.CurrentCall is not null) await _direct.HangUpAsync(token);
        if (_community.CurrentRoom is not null) await _community.LeaveAsync(token);
        await _community.JoinAsync(communityId, channelId, token);
    }, cancellationToken);

    public async Task LeaveCurrentVoiceSessionAsync(string reason,
        CancellationToken cancellationToken = default) => await SwitchAsync(
        token => LeaveCurrentUnsafeAsync(reason, token), cancellationToken);

    public Task ToggleMuteAsync(CancellationToken cancellationToken = default) => Current?.Kind switch
    {
        ActiveVoiceSessionKind.DirectCall => _direct.ToggleMuteAsync(cancellationToken),
        ActiveVoiceSessionKind.CommunityVoiceChannel => _community.SetMutedAsync(!_community.Muted, cancellationToken),
        _ => Task.CompletedTask
    };

    public Task ToggleDeafenAsync(CancellationToken cancellationToken = default) => Current?.Kind switch
    {
        ActiveVoiceSessionKind.DirectCall => _direct.ToggleDeafenAsync(cancellationToken),
        ActiveVoiceSessionKind.CommunityVoiceChannel => _community.SetDeafenedAsync(!_community.Deafened, cancellationToken),
        _ => Task.CompletedTask
    };

    public Task StartScreenShareAsync(CancellationToken cancellationToken = default) => Current?.Kind switch
    {
        ActiveVoiceSessionKind.DirectCall => _direct.StartScreenShareAsync(cancellationToken),
        ActiveVoiceSessionKind.CommunityVoiceChannel => _community.StartScreenShareAsync(cancellationToken),
        _ => Task.CompletedTask
    };

    public Task StopScreenShareAsync(string reason = "UserStoppedInIridium",
        CancellationToken cancellationToken = default) => Current?.Kind switch
    {
        ActiveVoiceSessionKind.DirectCall => _direct.StopScreenShareAsync(reason, cancellationToken),
        ActiveVoiceSessionKind.CommunityVoiceChannel => _community.StopScreenShareAsync(reason, cancellationToken),
        _ => Task.CompletedTask
    };

    public async Task WatchStreamAsync(Guid streamId, CancellationToken cancellationToken = default)
    {
        switch (Current?.Kind)
        {
            case ActiveVoiceSessionKind.DirectCall: await _direct.WatchStreamAsync(streamId, cancellationToken); break;
            case ActiveVoiceSessionKind.CommunityVoiceChannel: await _community.WatchStreamAsync(streamId, cancellationToken); break;
            default: return;
        }
        ViewerMode = StreamViewerMode.Full;
        Changed?.Invoke();
    }

    public async Task StopWatchingAsync(CancellationToken cancellationToken = default)
    {
        switch (Current?.Kind)
        {
            case ActiveVoiceSessionKind.DirectCall: await _direct.StopWatchingAsync(cancellationToken); break;
            case ActiveVoiceSessionKind.CommunityVoiceChannel: await _community.StopWatchingAsync(cancellationToken); break;
        }
        ViewerMode = StreamViewerMode.None;
        Changed?.Invoke();
    }

    public void MinimizeWatchedStream()
    {
        if (WatchedStream is null || ViewerMode != StreamViewerMode.Full) return;
        ViewerMode = StreamViewerMode.Floating;
        Changed?.Invoke();
    }

    public void ShowWatchedStreamFull()
    {
        if (WatchedStream is null) return;
        ViewerMode = StreamViewerMode.Full;
        Changed?.Invoke();
    }

    public async Task RefreshStreamThumbnailsAsync(CancellationToken cancellationToken = default)
    {
        var changed = false;
        foreach (var stream in PublishedStreams.ToArray())
        {
            try
            {
                var thumbnail = Current?.Kind switch
                {
                    ActiveVoiceSessionKind.DirectCall => await _direct.CaptureStreamThumbnailAsync(stream.MediaStreamId, cancellationToken),
                    ActiveVoiceSessionKind.CommunityVoiceChannel => await _community.CaptureStreamThumbnailAsync(stream.MediaStreamId, cancellationToken),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(thumbnail) && _streamThumbnails.GetValueOrDefault(stream.StreamId) != thumbnail)
                { _streamThumbnails[stream.StreamId] = thumbnail; changed = true; }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* A thumbnail is optional; stream playback remains authoritative. */ }
        }
        if (changed) Changed?.Invoke();
    }

    public async Task AttachWatchedStreamAsync(string elementId, CancellationToken cancellationToken = default)
    {
        switch (Current?.Kind)
        {
            case ActiveVoiceSessionKind.DirectCall:
                await _direct.AttachWatchedStreamAsync(elementId, cancellationToken); break;
            case ActiveVoiceSessionKind.CommunityVoiceChannel:
                await _community.AttachWatchedStreamAsync(elementId, cancellationToken); break;
            default: return;
        }
        if (WatchedStream is { } stream && IsStreamAudioMuted(stream.StreamId))
            await SetStreamAudioMutedAsync(elementId, true, cancellationToken);
    }

    public Task DetachWatchedStreamAsync(string elementId, CancellationToken cancellationToken = default) =>
        Current?.Kind switch
        {
            ActiveVoiceSessionKind.DirectCall => _direct.DetachWatchedStreamAsync(elementId, cancellationToken),
            ActiveVoiceSessionKind.CommunityVoiceChannel => _community.DetachWatchedStreamAsync(elementId, cancellationToken),
            _ => Task.CompletedTask
        };

    public Task SetStreamAudioMutedAsync(string elementId, bool muted,
        CancellationToken cancellationToken = default)
    {
        if (WatchedStream is { } stream)
        {
            if (muted) _mutedStreamAudio.Add(stream.StreamId);
            else _mutedStreamAudio.Remove(stream.StreamId);
        }
        return Current?.Kind switch
        {
            ActiveVoiceSessionKind.DirectCall => _direct.SetStreamAudioMutedAsync(elementId, muted, cancellationToken),
            ActiveVoiceSessionKind.CommunityVoiceChannel => _community.SetStreamAudioMutedAsync(elementId, muted, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    public Task RequestStreamFullscreenAsync(string elementId, CancellationToken cancellationToken = default) =>
        Current?.Kind switch
        {
            ActiveVoiceSessionKind.DirectCall => _direct.RequestStreamFullscreenAsync(elementId, cancellationToken),
            ActiveVoiceSessionKind.CommunityVoiceChannel => _community.RequestStreamFullscreenAsync(elementId, cancellationToken),
            _ => Task.CompletedTask
        };

    private async Task LeaveCurrentUnsafeAsync(string reason, CancellationToken cancellationToken)
    {
        if (_community.CurrentRoom is not null) await _community.LeaveAsync(cancellationToken);
        if (_direct.CurrentCall is not null) await _direct.HangUpAsync(cancellationToken);
    }

    private async Task SwitchAsync(Func<CancellationToken, Task> transition, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _switchGate.WaitAsync(cancellationToken);
        try { await transition(cancellationToken); }
        finally { _switchGate.Release(); Changed?.Invoke(); }
    }

    private ActiveVoiceSession? ProjectCurrent()
    {
        if (_direct.CurrentCall is { } call)
        {
            var remote = call.Participants.FirstOrDefault(value => value.AccountId != _direct.AccountId);
            return new(ActiveVoiceSessionKind.DirectCall, call.Id, remote?.DisplayName ?? "Direct Call",
                null, null, call.DirectConversationId, call.AcceptedAt ?? call.CreatedAt,
                _direct.MediaConnectionState, _direct.IsMuted, _direct.IsDeafened,
                call.State == CallState.Active && _direct.MediaConnectionState == CallConnectionState.Connected,
                _direct.PublishedStreams, _direct.WatchedStream);
        }
        if (_community.CurrentRoom is { } room)
        {
            var state = _community.MediaSession?.Status switch
            {
                CommunityVoiceMediaStatus.Connected => CallConnectionState.Connected,
                CommunityVoiceMediaStatus.Connecting => CallConnectionState.Connecting,
                CommunityVoiceMediaStatus.Failed => CallConnectionState.Failed,
                _ => CallConnectionState.New
            };
            return new(ActiveVoiceSessionKind.CommunityVoiceChannel, room.ChannelId, room.ChannelName,
                room.CommunityId, room.ChannelId, null, room.StartedAt, state,
                _community.Muted, _community.Deafened,
                _community.MediaSession?.Status == CommunityVoiceMediaStatus.Connected,
                _community.PublishedStreams, _community.WatchedStream);
        }
        return null;
    }

    private void UnderlyingChanged()
    {
        _mutedStreamAudio.RemoveWhere(id => PublishedStreams.All(value => value.StreamId != id));
        foreach (var id in _streamThumbnails.Keys.Where(id => PublishedStreams.All(value => value.StreamId != id)).ToArray())
            _streamThumbnails.Remove(id);
        if (WatchedStream is { } watched && watched.StreamId != _lastWatchedStreamId)
        {
            _lastWatchedStreamId = watched.StreamId;
            ViewerMode = StreamViewerMode.Full;
            if (watched.OwnerAccountId == LocalAccountId) _mutedStreamAudio.Add(watched.StreamId);
        }
        else if (WatchedStream is null)
        {
            _lastWatchedStreamId = null;
            ViewerMode = StreamViewerMode.None;
        }
        EnsureThumbnailLoop();
        Changed?.Invoke();
    }

    private void EnsureThumbnailLoop()
    {
        if (PublishedStreams.Count == 0)
        {
            _thumbnailLoopCancellation?.Cancel();
            _thumbnailLoopCancellation?.Dispose();
            _thumbnailLoopCancellation = null;
            return;
        }
        if (_thumbnailLoopCancellation is not null) return;
        _thumbnailLoopCancellation = new CancellationTokenSource();
        _ = RunThumbnailLoopAsync(_thumbnailLoopCancellation.Token);
    }

    private async Task RunThumbnailLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshStreamThumbnailsAsync(cancellationToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await RefreshStreamThumbnailsAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _direct.Changed -= UnderlyingChanged;
        _community.Changed -= UnderlyingChanged;
        _thumbnailLoopCancellation?.Cancel();
        _thumbnailLoopCancellation?.Dispose();
        _switchGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
