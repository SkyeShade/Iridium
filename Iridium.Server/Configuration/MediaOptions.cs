using Iridium.Protocol;

namespace Iridium.Server.Configuration;

public sealed class MediaOptions
{
    public const string SectionName = "Media";
    public MediaMode Mode { get; set; } = MediaMode.DirectWebRtc;
    public int RingTimeoutSeconds { get; set; } = 30;
    public int SignalingLossTimeoutSeconds { get; set; } = 45;
    public bool EnableDevelopmentCommunityPeerMesh { get; set; }
    public int DevelopmentCommunityPeerLimit { get; set; } = 6;
    public List<IceServerOptions> IceServers { get; set; } = [];
}

public sealed class IceServerOptions
{
    public List<string> Urls { get; set; } = [];
    public string? Username { get; set; }
    public string? Credential { get; set; }
}
