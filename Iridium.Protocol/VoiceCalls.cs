namespace Iridium.Protocol;

public static class VoiceCallHubContract
{
    public const string Start = "StartDirectVoiceCall";
    public const string Accept = "AcceptVoiceCall";
    public const string Reject = "RejectVoiceCall";
    public const string Cancel = "CancelVoiceCall";
    public const string HangUp = "HangUpVoiceCall";
    public const string SetParticipantState = "SetCallParticipantState";
    public const string SetSpeaking = "SetCallParticipantSpeaking";
    public const string Heartbeat = "HeartbeatVoiceCall";
    public const string RequestMediaRetry = "RequestCallMediaRetry";
    public const string GetMediaConfiguration = "GetCallMediaConfiguration";
    public const string GetCurrent = "GetCurrentCall";
    public const string SendOffer = "SendWebRtcOffer";
    public const string SendAnswer = "SendWebRtcAnswer";
    public const string SendIceCandidate = "SendWebRtcIceCandidate";
    public const string ReportDiagnostic = "ReportVoiceDiagnostic";

    public const string Incoming = "IncomingCall";
    public const string Accepted = "CallAccepted";
    public const string Rejected = "CallRejected";
    public const string Cancelled = "CallCancelled";
    public const string Ended = "CallEnded";
    public const string ParticipantStateChanged = "CallParticipantStateChanged";
    public const string ParticipantSpeakingChanged = "CallParticipantSpeakingChanged";
    public const string MediaRetryRequested = "CallMediaRetryRequested";
    public const string Offer = "WebRtcOffer";
    public const string Answer = "WebRtcAnswer";
    public const string IceCandidate = "WebRtcIceCandidate";
}

public enum CallKind { DirectVoice, CommunityVoice }
public enum CallState { Ringing, Active, Ended, Rejected, Cancelled }
public enum CallConnectionState { New, Connecting, Connected, Disconnected, Failed, Closed }
public enum MediaMode { DirectWebRtc, NodeSfu }
public enum NodeMediaRoomKind { DirectCall, CommunityVoice }
public enum WebRtcNegotiationKind { Initial, Renegotiation, IceRestart }

public sealed record CallParticipantDto(
    Guid AccountId,
    string DisplayName,
    bool IsMuted,
    bool IsDeafened,
    bool IsSpeaking,
    DateTimeOffset? JoinedAt,
    CallConnectionState ConnectionState);

public sealed record CallSessionDto(
    Guid Id,
    CallKind Kind,
    Guid? DirectConversationId,
    Guid CallerAccountId,
    CallState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<CallParticipantDto> Participants,
    DateTimeOffset? AcceptedAt = null);

public sealed record IncomingCallEvent(
    Guid CallId,
    Guid DirectConversationId,
    Guid CallerAccountId,
    string CallerDisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record CallStateEvent(Guid CallId, CallState State, string? Reason = null, Guid? SignalId = null);
public sealed record CallParticipantStateEvent(
    Guid CallId,
    Guid AccountId,
    bool IsMuted,
    bool IsDeafened,
    CallConnectionState ConnectionState);
public sealed record CallParticipantSpeakingEvent(Guid CallId, Guid AccountId, bool IsSpeaking);

public sealed record WebRtcSessionDescription(string Type, string Sdp);
public sealed record WebRtcIceCandidate(string Candidate, string? SdpMid, int? SdpMLineIndex, string? UsernameFragment);
// TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
public sealed record WebRtcDescriptionEvent(
    Guid CallId,
    Guid SenderAccountId,
    Guid NegotiationId,
    int NegotiationGeneration,
    int SenderPeerGeneration,
    Guid SignalId,
    WebRtcSessionDescription Description,
    WebRtcNegotiationKind NegotiationKind = WebRtcNegotiationKind.Initial);
public sealed record WebRtcIceCandidateEvent(
    Guid CallId,
    Guid SenderAccountId,
    Guid NegotiationId,
    int NegotiationGeneration,
    int SenderPeerGeneration,
    Guid SignalId,
    WebRtcIceCandidate Candidate);

// TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
// Deliberately whitelist safe metadata. SDP and ICE candidate payloads have no place in this DTO.
public sealed record VoiceDiagnosticReport(
    Guid CallId,
    string Event,
    int? PeerGeneration = null,
    int? NegotiationGeneration = null,
    Guid? SignalId = null,
    int? Sequence = null,
    string? OldState = null,
    string? NewState = null,
    string? SignalingState = null,
    string? IceGatheringState = null,
    string? IceConnectionState = null,
    string? ConnectionState = null,
    string? LocalDescriptionType = null,
    string? RemoteDescriptionType = null,
    string? CandidateType = null,
    string? Protocol = null,
    string? SdpMid = null,
    int? SdpMLineIndex = null,
    string? TrackKind = null,
    bool? TrackEnabled = null,
    string? TrackReadyState = null,
    bool? TrackMuted = null,
    string? IceTransportPolicy = null,
    string? ErrorName = null,
    string? SafeMessage = null,
    string? Reason = null,
    int? Count = null,
    int? QueueLength = null,
    int? AudioTrackCount = null,
    int? SenderCount = null,
    int? IceServerCount = null,
    int? SdpLength = null,
    int? CandidateLineCount = null,
    bool? CandidatePresent = null,
    bool? HasAudioMediaSection = null,
    int? OffersCreated = null,
    int? OffersReceived = null,
    int? AnswersCreated = null,
    int? AnswersReceived = null,
    int? LocalIceGenerated = null,
    int? LocalIceSent = null,
    int? RemoteIceReceived = null,
    int? RemoteIceQueued = null,
    int? RemoteIceAdded = null,
    int? RemoteIceAddFailures = null,
    bool? RemoteTrackReceived = null,
    bool? RemoteAudioPlaySucceeded = null,
    bool? MediaTrafficDetected = null,
    int? LocalCandidateStats = null,
    int? RemoteCandidateStats = null,
    int? CandidatePairStats = null,
    int? SucceededCandidatePairs = null,
    bool? NominatedPairExists = null,
    bool? SelectedPairExists = null,
    string? PairState = null,
    string? LocalCandidateType = null,
    string? RemoteCandidateType = null,
    long? PacketsSent = null,
    long? PacketsReceived = null,
    long? PacketsLost = null,
    long? BytesSent = null,
    long? BytesReceived = null,
    long? FramesEncoded = null,
    long? FramesDecoded = null,
    long? FramesDropped = null,
    int? FrameWidth = null,
    int? FrameHeight = null,
    bool? HostCandidateAvailable = null,
    bool? ServerReflexiveCandidateAvailable = null,
    bool? PeerReflexiveCandidateAvailable = null,
    bool? RelayCandidateAvailable = null,
    bool? TurnConfigured = null,
    bool? TurnCredentialsPresent = null,
    bool? TurnConfiguredButNoRelayCandidate = null);

public sealed record IceServerDto(IReadOnlyList<string> Urls, string? Username = null, string? Credential = null);
public sealed record NodeMediaSessionDto(
    string Provider,
    string PublicUrl,
    string AccessToken,
    string RoomName,
    string ParticipantIdentity,
    NodeMediaRoomKind RoomKind,
    DateTimeOffset ExpiresAt,
    bool DiagnosticsEnabled = false);

public sealed record CallMediaConfigurationDto(
    MediaMode Mode,
    IReadOnlyList<IceServerDto> IceServers,
    NodeMediaSessionDto? NodeSession = null);
