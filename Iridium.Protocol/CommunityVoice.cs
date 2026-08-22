namespace Iridium.Protocol;

public static class CommunityVoiceHubContract
{
    public const string GetRooms = "GetCommunityVoiceRooms";
    public const string Join = "JoinVoiceChannel";
    public const string Leave = "LeaveVoiceChannel";
    public const string SetState = "SetVoiceParticipantState";
    public const string SetSpeaking = "SetVoiceParticipantSpeaking";
    public const string GetMediaSession = "GetCommunityVoiceMediaSession";
    public const string SendMediaOffer = "SendCommunityVoiceMediaOffer";
    public const string SendMediaAnswer = "SendCommunityVoiceMediaAnswer";
    public const string SendMediaIceCandidate = "SendCommunityVoiceMediaIceCandidate";
    public const string RoomChanged = "VoiceRoomChanged";
    public const string ParticipantJoined = "VoiceParticipantJoined";
    public const string ParticipantLeft = "VoiceParticipantLeft";
    public const string ParticipantStateChanged = "VoiceParticipantStateChanged";
    public const string MediaOffer = "CommunityVoiceMediaOffer";
    public const string MediaAnswer = "CommunityVoiceMediaAnswer";
    public const string MediaIceCandidate = "CommunityVoiceMediaIceCandidate";
}

public enum CommunityVoiceMediaStatus
{
    ControlConnected,
    MediaUnavailable,
    Connecting,
    Connected,
    Failed
}

public sealed record CommunityVoiceMediaSessionDto(
    CommunityVoiceMediaStatus Status,
    string Provider,
    string? WebRtcSignalingEndpoint = null,
    string? AccessToken = null,
    string? ParticipantId = null,
    IReadOnlyList<IceServerDto>? IceServers = null,
    bool DiagnosticsEnabled = false);

public sealed record CommunityVoiceMediaDescriptionEvent(string SourceParticipantId, Guid NegotiationId,
    WebRtcSessionDescription Description);
public sealed record CommunityVoiceMediaIceCandidateEvent(string SourceParticipantId, Guid NegotiationId,
    WebRtcIceCandidate Candidate);

public sealed record CommunityVoiceMediaDiagnosticDto(
    string Event,
    string? RemoteParticipantId = null,
    bool? LocalStreamPresent = null,
    int? LocalAudioTracks = null,
    int? AttachedSenderCount = null,
    string? ConnectionState = null,
    string? IceConnectionState = null,
    int? LocalIceGenerated = null,
    int? RemoteIceReceived = null,
    int? RemoteTrackCount = null,
    int? RemoteAudioElements = null,
    bool? RemoteAudioPlaySucceeded = null,
    long? PacketsSent = null,
    long? PacketsReceived = null,
    long? BytesSent = null,
    long? BytesReceived = null,
    string? RemoteTrackReadyState = null,
    bool? RemoteTrackMuted = null,
    bool? ElementMuted = null,
    double? ElementVolume = null,
    string? AudioContextState = null,
    double? GainValue = null,
    long? FramesEncoded = null,
    long? FramesDecoded = null,
    long? FramesDropped = null,
    int? FrameWidth = null,
    int? FrameHeight = null,
    string? ErrorName = null,
    string? ErrorMessage = null);

public sealed record VoiceParticipantDto(
    Guid AccountId,
    string ParticipantId,
    string DisplayName,
    PublicPresence Presence,
    DateTimeOffset JoinedAt,
    bool Muted,
    bool Deafened,
    bool Speaking,
    CommunityVoiceMediaStatus MediaStatus,
    string? Username = null);

public sealed record ActiveVoiceRoomDto(
    Guid CommunityId,
    Guid ChannelId,
    DateTimeOffset StartedAt,
    IReadOnlyList<VoiceParticipantDto> Participants,
    string CommunityName = "Community",
    string ChannelName = "Voice");

public sealed record VoiceParticipantJoinedEvent(ActiveVoiceRoomDto Room, VoiceParticipantDto Participant);
public sealed record VoiceParticipantLeftEvent(Guid CommunityId, Guid ChannelId, Guid AccountId,
    string ParticipantId, ActiveVoiceRoomDto? Room);
public sealed record VoiceParticipantStateChangedEvent(Guid CommunityId, Guid ChannelId,
    VoiceParticipantDto Participant);
