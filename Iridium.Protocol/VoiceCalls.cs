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
    public const string RequestMediaRetry = "RequestCallMediaRetry";
    public const string GetMediaConfiguration = "GetCallMediaConfiguration";
    public const string GetCurrent = "GetCurrentCall";
    public const string SendOffer = "SendWebRtcOffer";
    public const string SendAnswer = "SendWebRtcAnswer";
    public const string SendIceCandidate = "SendWebRtcIceCandidate";

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
    IReadOnlyList<CallParticipantDto> Participants);

public sealed record IncomingCallEvent(
    Guid CallId,
    Guid DirectConversationId,
    Guid CallerAccountId,
    string CallerDisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record CallStateEvent(Guid CallId, CallState State, string? Reason = null);
public sealed record CallParticipantStateEvent(
    Guid CallId,
    Guid AccountId,
    bool IsMuted,
    bool IsDeafened,
    CallConnectionState ConnectionState);
public sealed record CallParticipantSpeakingEvent(Guid CallId, Guid AccountId, bool IsSpeaking);

public sealed record WebRtcSessionDescription(string Type, string Sdp);
public sealed record WebRtcIceCandidate(string Candidate, string? SdpMid, int? SdpMLineIndex, string? UsernameFragment);
public sealed record WebRtcDescriptionEvent(
    Guid CallId,
    Guid SenderAccountId,
    Guid NegotiationId,
    WebRtcSessionDescription Description);
public sealed record WebRtcIceCandidateEvent(
    Guid CallId,
    Guid SenderAccountId,
    Guid NegotiationId,
    WebRtcIceCandidate Candidate);

public sealed record IceServerDto(IReadOnlyList<string> Urls, string? Username = null, string? Credential = null);
public sealed record CallMediaConfigurationDto(MediaMode Mode, IReadOnlyList<IceServerDto> IceServers);
