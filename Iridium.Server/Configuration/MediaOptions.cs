using Iridium.Protocol;

namespace Iridium.Server.Configuration;

public sealed class MediaOptions
{
    public const string SectionName = "Media";
    public MediaProvider Provider { get; set; } = MediaProvider.Disabled;
    public string? PublicUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
    public int JoinTokenLifetimeSeconds { get; set; } = 300;
    public VoiceMediaOptions Voice { get; set; } = new();
    public MediaMode Mode { get; set; } = MediaMode.NodeSfu;
    public int RingTimeoutSeconds { get; set; } = 30;
    public int SignalingLossTimeoutSeconds { get; set; } = 45;
    public bool EnableDevelopmentCommunityPeerMesh { get; set; }
    public int DevelopmentCommunityPeerLimit { get; set; } = 6;
}

public sealed class VoiceMediaOptions
{
    public const int DefaultBitrate = 96_000;
    public const int MinimumBitrate = 64_000;
    public const int MaximumBitrate = 128_000;

    public int Bitrate { get; set; } = DefaultBitrate;
    public int EffectiveBitrate => Math.Clamp(Bitrate, MinimumBitrate, MaximumBitrate);
}

public enum MediaProvider
{
    Disabled,
    LiveKit,
    DevelopmentPeerToPeer
}
