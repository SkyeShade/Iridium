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
    public MediaMode Mode { get; set; } = MediaMode.NodeSfu;
    public int RingTimeoutSeconds { get; set; } = 30;
    public int SignalingLossTimeoutSeconds { get; set; } = 45;
    public bool EnableDevelopmentCommunityPeerMesh { get; set; }
    public int DevelopmentCommunityPeerLimit { get; set; } = 6;
}

public enum MediaProvider
{
    Disabled,
    LiveKit,
    DevelopmentPeerToPeer
}
