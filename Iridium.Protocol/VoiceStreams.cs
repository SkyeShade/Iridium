namespace Iridium.Protocol;

public static class VoiceStreamHubContract
{
    public const string GetPublished = "GetPublishedVoiceStreams";
    public const string Publish = "PublishVoiceStream";
    public const string StopPublishing = "StopPublishedVoiceStream";
    public const string Watch = "WatchVoiceStream";
    public const string StopWatching = "StopWatchingVoiceStream";
    public const string Published = "VoiceStreamPublished";
    public const string Ended = "VoiceStreamEnded";
}

public enum VoiceMediaSessionKind
{
    DirectCall,
    CommunityVoice
}

public enum VoicePublishedStreamKind
{
    ScreenShare,
    Camera
}

public sealed record PublishVoiceStreamRequest(
    Guid StreamId,
    VoicePublishedStreamKind Kind,
    bool HasAudio,
    string MediaStreamId);

public sealed record PublishedVoiceStreamDto(
    Guid StreamId,
    VoiceMediaSessionKind SessionKind,
    Guid SessionId,
    Guid OwnerAccountId,
    string OwnerDisplayName,
    string? OwnerParticipantId,
    VoicePublishedStreamKind Kind,
    bool HasAudio,
    string MediaStreamId,
    DateTimeOffset StartedAt);

public sealed record VoiceStreamPublishedEvent(PublishedVoiceStreamDto Stream);
public sealed record VoiceStreamEndedEvent(
    VoiceMediaSessionKind SessionKind,
    Guid SessionId,
    Guid StreamId,
    string Reason);

public sealed record LocalVoiceStreamPublication(
    Guid StreamId,
    VoicePublishedStreamKind Kind,
    bool HasAudio,
    string MediaStreamId);
