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
    IReadOnlyList<PublishedVoiceStreamDto> PublishedStreams { get; }
    PublishedVoiceStreamDto? WatchedStream { get; }
    Task StartAsync(DirectConversationDto conversation, CancellationToken cancellationToken = default);
    Task AcceptAsync(CancellationToken cancellationToken = default);
    Task DeclineAsync(CancellationToken cancellationToken = default);
    Task HangUpAsync(CancellationToken cancellationToken = default);
    Task ToggleMuteAsync(CancellationToken cancellationToken = default);
    Task ToggleDeafenAsync(CancellationToken cancellationToken = default);
    Task StartScreenShareAsync(CancellationToken cancellationToken = default);
    Task SwitchScreenShareAsync(CancellationToken cancellationToken = default);
    Task StopScreenShareAsync(string reason = "UserStoppedInIridium", CancellationToken cancellationToken = default);
    Task WatchStreamAsync(Guid streamId, CancellationToken cancellationToken = default);
    Task StopWatchingAsync(CancellationToken cancellationToken = default);
    Task AttachWatchedStreamAsync(string elementId, int volumePercent = 100, CancellationToken cancellationToken = default);
    Task DetachWatchedStreamAsync(string elementId, CancellationToken cancellationToken = default);
    Task SetStreamAudioMutedAsync(string elementId, bool muted, CancellationToken cancellationToken = default);
    Task SetStreamAudioVolumeAsync(string elementId, int volumePercent, CancellationToken cancellationToken = default);
    Task RequestStreamFullscreenAsync(string elementId, CancellationToken cancellationToken = default);
    Task<string?> CaptureStreamThumbnailAsync(string mediaStreamId, CancellationToken cancellationToken = default);
}

public interface ICommunityVoiceControlSession
{
    event Action? Changed;
    ActiveVoiceRoomDto? CurrentRoom { get; }
    CommunityVoiceMediaSessionDto? MediaSession { get; }
    bool Muted { get; }
    bool Deafened { get; }
    IReadOnlyList<PublishedVoiceStreamDto> PublishedStreams { get; }
    PublishedVoiceStreamDto? WatchedStream { get; }
    Task JoinAsync(Guid communityId, Guid channelId, CancellationToken cancellationToken = default);
    Task LeaveAsync(CancellationToken cancellationToken = default);
    Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default);
    Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken = default);
    Task StartScreenShareAsync(CancellationToken cancellationToken = default);
    Task SwitchScreenShareAsync(CancellationToken cancellationToken = default);
    Task StopScreenShareAsync(string reason = "UserStoppedInIridium", CancellationToken cancellationToken = default);
    Task WatchStreamAsync(Guid streamId, CancellationToken cancellationToken = default);
    Task StopWatchingAsync(CancellationToken cancellationToken = default);
    Task AttachWatchedStreamAsync(string elementId, int volumePercent = 100, CancellationToken cancellationToken = default);
    Task DetachWatchedStreamAsync(string elementId, CancellationToken cancellationToken = default);
    Task SetStreamAudioMutedAsync(string elementId, bool muted, CancellationToken cancellationToken = default);
    Task SetStreamAudioVolumeAsync(string elementId, int volumePercent, CancellationToken cancellationToken = default);
    Task RequestStreamFullscreenAsync(string elementId, CancellationToken cancellationToken = default);
    Task<string?> CaptureStreamThumbnailAsync(string mediaStreamId, CancellationToken cancellationToken = default);
}
