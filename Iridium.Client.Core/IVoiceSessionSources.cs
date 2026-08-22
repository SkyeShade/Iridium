using Iridium.Protocol;

namespace Iridium.Client.Core;

public interface IDirectVoiceSession
{
    event Action? Changed;
    CallSessionDto? CurrentCall { get; }
    IncomingCallEvent? IncomingCall { get; }
    Guid? AccountId { get; }
    bool IsMuted { get; }
    bool IsDeafened { get; }
    CallConnectionState MediaConnectionState { get; }
    Task StartAsync(DirectConversationDto conversation, CancellationToken cancellationToken = default);
    Task AcceptAsync(CancellationToken cancellationToken = default);
    Task DeclineAsync(CancellationToken cancellationToken = default);
    Task HangUpAsync(CancellationToken cancellationToken = default);
    Task ToggleMuteAsync(CancellationToken cancellationToken = default);
    Task ToggleDeafenAsync(CancellationToken cancellationToken = default);
}

public interface ICommunityVoiceControlSession
{
    event Action? Changed;
    ActiveVoiceRoomDto? CurrentRoom { get; }
    CommunityVoiceMediaSessionDto? MediaSession { get; }
    bool Muted { get; }
    bool Deafened { get; }
    Task JoinAsync(Guid communityId, Guid channelId, CancellationToken cancellationToken = default);
    Task LeaveAsync(CancellationToken cancellationToken = default);
    Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default);
    Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken = default);
}
