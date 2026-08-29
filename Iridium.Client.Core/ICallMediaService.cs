using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed record CallMediaSessionContext(
    Guid CallId,
    Guid LocalAccountId,
    string Role,
    int PeerGeneration,
    Guid? NegotiationId,
    int NegotiationGeneration,
    Guid? RemoteAccountId = null,
    bool Muted = false,
    bool Deafened = false);

public sealed record RemoteAnswerApplyResult(bool Applied, string SignalingState, string? IgnoreReason);
public sealed record LocalIceCandidateSignal(
    int Sequence,
    Guid SignalId,
    int PeerGeneration,
    int NegotiationGeneration,
    WebRtcIceCandidate Candidate);

public sealed record WebRtcDiagnosticSnapshot(
    string SignalingState,
    string IceGatheringState,
    string IceConnectionState,
    string ConnectionState,
    string? LocalDescriptionType,
    string? RemoteDescriptionType,
    int LocalCandidateCount,
    int RemoteCandidateCount,
    int RemoteCandidateAddedCount,
    int RemoteCandidateAddFailureCount,
    int QueuedRemoteCandidateCount,
    string LocalCandidateTypes,
    string RemoteCandidateTypes,
    string? SelectedLocalCandidateType,
    string? SelectedRemoteCandidateType,
    string? SelectedCandidateProtocol,
    int AnswersReceived,
    int AnswersApplied,
    int AnswersIgnored,
    string? LastAnswerSignalingStateBefore,
    string? LastAnswerSignalingStateAfter,
    int CreateOfferCount,
    int CreateAnswerCount,
    int NegotiationNeededCount,
    int NegotiationGeneration,
    int PeerGeneration,
    string Role,
    int StatsLocalCandidateCount,
    int StatsRemoteCandidateCount,
    int StatsCandidatePairCount,
    string CandidatePairSummary,
    int StatsSucceededCandidatePairCount,
    bool StatsNominatedPairExists,
    bool StatsSelectedPairExists,
    long PacketsSent,
    long PacketsReceived,
    long PacketsLost,
    long BytesSent,
    long BytesReceived,
    bool RemoteTrackReceived,
    bool RemoteAudioPlaySucceeded,
    bool MediaTrafficDetected,
    bool HostCandidateAvailable = false,
    bool ServerReflexiveCandidateAvailable = false,
    bool PeerReflexiveCandidateAvailable = false,
    bool RelayCandidateAvailable = false,
    bool TurnConfigured = false,
    bool TurnCredentialsPresent = false);

public interface ICallMediaService : IAsyncDisposable
{
    bool DiagnosticsEnabled { get; }
    event Func<LocalIceCandidateSignal, Task>? IceCandidateGenerated;
    event Func<CallConnectionState, Task>? ConnectionStateChanged;
    event Func<string, Task>? IceConnectionStateChanged;
    event Func<bool, Task>? SpeakingChanged;
    event Func<string, Task>? ScreenShareEnded;
    event Func<bool, Task>? ScreenShareAudioAvailabilityChanged;
    event Func<string, bool, Task>? WatchedStreamAudioAvailabilityChanged
    {
        add { }
        remove { }
    }
    event Func<string, Task>? Error;
    event Func<VoiceDiagnosticReport, Task>? DiagnosticGenerated;

    Task InitializeAsync(CallMediaConfigurationDto configuration, CallMediaSessionContext context,
        CancellationToken cancellationToken = default);
    Task<WebRtcSessionDescription> CreateOfferAsync(Guid negotiationId, Guid signalId,
        CancellationToken cancellationToken = default);
    Task<WebRtcSessionDescription> AcceptOfferAsync(Guid negotiationId, Guid offerSignalId, Guid answerSignalId,
        WebRtcSessionDescription offer,
        CancellationToken cancellationToken = default);
    Task<RemoteAnswerApplyResult> ApplyAnswerAsync(Guid negotiationId, Guid signalId, WebRtcSessionDescription answer,
        CancellationToken cancellationToken = default);
    Task AddIceCandidateAsync(Guid signalId, WebRtcIceCandidate candidate,
        CancellationToken cancellationToken = default);
    Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default);
    Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken = default);
    Task<LocalVoiceStreamPublication> StartScreenShareAsync(CancellationToken cancellationToken = default);
    Task<LocalVoiceStreamPublication> SwitchScreenShareAsync(CancellationToken cancellationToken = default);
    Task StopScreenShareAsync(string reason, CancellationToken cancellationToken = default);
    Task AttachStreamViewerAsync(string mediaStreamId, string elementId, bool audioMuted, int volumePercent,
        CancellationToken cancellationToken = default);
    Task DetachStreamViewerAsync(string elementId, CancellationToken cancellationToken = default);
    Task SetStreamSubscriptionAsync(string mediaStreamId, bool subscribed,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetStreamSubscriptionAsync(string iridiumStreamId, string mediaStreamId,
        string? participantIdentity, bool subscribed, CancellationToken cancellationToken = default) =>
        SetStreamSubscriptionAsync(mediaStreamId, subscribed, cancellationToken);
    Task SetStreamAudioMutedAsync(string elementId, bool muted, CancellationToken cancellationToken = default);
    Task SetStreamAudioVolumeAsync(string elementId, int volumePercent,
        CancellationToken cancellationToken = default);
    Task RequestStreamFullscreenAsync(string elementId, CancellationToken cancellationToken = default);
    Task<string?> CaptureStreamThumbnailAsync(string mediaStreamId, CancellationToken cancellationToken = default);
    Task<WebRtcDiagnosticSnapshot?> GetDiagnosticSnapshotAsync(CancellationToken cancellationToken = default);
    Task CleanupAsync(string reason, CancellationToken cancellationToken = default);
}
