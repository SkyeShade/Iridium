using Iridium.Protocol;

namespace Iridium.Client.Core;

/// <summary>
/// Browser/native media boundary for Community voice. Implementations connect one local media
/// endpoint to the Node/SFU; Razor components consume only CommunityVoiceSession state.
/// </summary>
public interface ICommunityVoiceMediaClient : IAsyncDisposable
{
    event Func<bool, Task>? SpeakingChanged;
    event Func<string, Task>? ScreenShareEnded;
    event Func<string, Task>? Error;
    event Func<string, Guid, WebRtcSessionDescription, Task>? OfferCreated;
    event Func<string, Guid, WebRtcSessionDescription, Task>? AnswerCreated;
    event Func<string, Guid, WebRtcIceCandidate, Task>? IceCandidateGenerated;
    Task ConnectAsync(CommunityVoiceMediaSessionDto mediaSession, ActiveVoiceRoomDto room,
        Guid localAccountId, bool muted = false, bool deafened = false,
        CancellationToken cancellationToken = default);
    Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default);
    Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken = default);
    Task ParticipantJoinedAsync(VoiceParticipantDto participant, CancellationToken cancellationToken = default);
    Task ParticipantLeftAsync(string participantId, CancellationToken cancellationToken = default);
    Task HandleOfferAsync(CommunityVoiceMediaDescriptionEvent offer, CancellationToken cancellationToken = default);
    Task HandleAnswerAsync(CommunityVoiceMediaDescriptionEvent answer, CancellationToken cancellationToken = default);
    Task HandleIceCandidateAsync(CommunityVoiceMediaIceCandidateEvent candidate,
        CancellationToken cancellationToken = default);
    Task<LocalVoiceStreamPublication> StartScreenShareAsync(CancellationToken cancellationToken = default);
    Task<LocalVoiceStreamPublication> SwitchScreenShareAsync(CancellationToken cancellationToken = default);
    Task StopScreenShareAsync(string reason, CancellationToken cancellationToken = default);
    Task AttachStreamViewerAsync(string mediaStreamId, string elementId, bool audioMuted, int volumePercent,
        CancellationToken cancellationToken = default);
    Task DetachStreamViewerAsync(string elementId, CancellationToken cancellationToken = default);
    Task SetStreamSubscriptionAsync(string mediaStreamId, bool subscribed,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetStreamAudioMutedAsync(string elementId, bool muted, CancellationToken cancellationToken = default);
    Task SetStreamAudioVolumeAsync(string elementId, int volumePercent,
        CancellationToken cancellationToken = default);
    Task RequestStreamFullscreenAsync(string elementId, CancellationToken cancellationToken = default);
    Task<string?> CaptureStreamThumbnailAsync(string mediaStreamId, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string reason, CancellationToken cancellationToken = default);
}
