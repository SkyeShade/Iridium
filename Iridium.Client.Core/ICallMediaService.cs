using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed record CallMediaSessionContext(
    Guid CallId,
    Guid LocalAccountId,
    string Role,
    int PeerGeneration,
    Guid? NegotiationId);

public sealed record RemoteAnswerApplyResult(bool Applied, string SignalingState, string? IgnoreReason);

public sealed record WebRtcDiagnosticSnapshot(
    string SignalingState,
    string IceGatheringState,
    string IceConnectionState,
    string ConnectionState,
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
    int PeerGeneration,
    string Role);

public interface ICallMediaService : IAsyncDisposable
{
    event Func<WebRtcIceCandidate, Task>? IceCandidateGenerated;
    event Func<CallConnectionState, Task>? ConnectionStateChanged;
    event Func<bool, Task>? SpeakingChanged;
    event Func<string, Task>? Error;

    Task InitializeAsync(CallMediaConfigurationDto configuration, CallMediaSessionContext context,
        CancellationToken cancellationToken = default);
    Task<WebRtcSessionDescription> CreateOfferAsync(Guid negotiationId, CancellationToken cancellationToken = default);
    Task<WebRtcSessionDescription> AcceptOfferAsync(Guid negotiationId, WebRtcSessionDescription offer,
        CancellationToken cancellationToken = default);
    Task<RemoteAnswerApplyResult> ApplyAnswerAsync(Guid negotiationId, WebRtcSessionDescription answer,
        CancellationToken cancellationToken = default);
    Task AddIceCandidateAsync(WebRtcIceCandidate candidate, CancellationToken cancellationToken = default);
    Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default);
    Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken = default);
    Task<WebRtcDiagnosticSnapshot?> GetDiagnosticSnapshotAsync(CancellationToken cancellationToken = default);
    Task CleanupAsync(CancellationToken cancellationToken = default);
}
