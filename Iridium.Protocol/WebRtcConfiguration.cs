namespace Iridium.Protocol;

public sealed record WebRtcIceConfigurationDto(
    IReadOnlyList<IceServerDto> IceServers,
    string IceTransportPolicy = "all",
    DateTimeOffset? ExpiresAt = null);
