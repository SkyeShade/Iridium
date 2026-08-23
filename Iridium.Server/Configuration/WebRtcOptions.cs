namespace Iridium.Server.Configuration;

public sealed class WebRtcOptions
{
    public const string SectionName = "WebRtc";
    public List<WebRtcIceServerOptions> IceServers { get; set; } = [];
    public WebRtcTurnOptions Turn { get; set; } = new();
    public string IceTransportPolicy { get; set; } = "all";
}

public sealed class WebRtcIceServerOptions
{
    public List<string> Urls { get; set; } = [];
}

public sealed class WebRtcTurnOptions
{
    public bool Enabled { get; set; }
    public List<string> Urls { get; set; } = [];
    public string? SharedSecret { get; set; }
    public int CredentialLifetimeSeconds { get; set; } = 3600;
}
