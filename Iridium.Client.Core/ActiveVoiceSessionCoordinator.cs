using Iridium.Protocol;

namespace Iridium.Client.Core;

public enum ActiveVoiceSessionKind
{
    DirectCall,
    CommunityVoiceChannel
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
    bool Deafened);

public sealed class ActiveVoiceSessionCoordinator : IAsyncDisposable
{
    private readonly IDirectVoiceSession _direct;
    private readonly ICommunityVoiceControlSession _community;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
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
                _direct.MediaConnectionState, _direct.IsMuted, _direct.IsDeafened);
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
                _community.Muted, _community.Deafened);
        }
        return null;
    }

    private void UnderlyingChanged() => Changed?.Invoke();

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _direct.Changed -= UnderlyingChanged;
        _community.Changed -= UnderlyingChanged;
        _switchGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
