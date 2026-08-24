using Iridium.Protocol;

namespace Iridium.Web.Services;

internal static class WebRtcConfigurationDiagnostics
{
    public static void LogLoaded(ILogger logger, WebRtcIceConfigurationDto configuration)
    {
        var stunServers = configuration.IceServers.Count(server =>
            server.Urls.Any(url => HasScheme(url, "stun:", "stuns:")));
        var turnServers = configuration.IceServers.Count(server =>
            server.Urls.Any(url => HasScheme(url, "turn:", "turns:")));
        var turnCredentialsPresent = configuration.IceServers.Any(server =>
            server.Urls.Any(url => HasScheme(url, "turn:", "turns:")) &&
            !string.IsNullOrWhiteSpace(server.Username) && !string.IsNullOrWhiteSpace(server.Credential));

        logger.LogDebug(
            "ICE configuration loaded: STUN servers={StunServers}; TURN servers={TurnServers}; " +
            "TURN credentials present={TurnCredentialsPresent}; ExpiresAt={ExpiresAt}; TransportPolicy={TransportPolicy}.",
            stunServers, turnServers, turnCredentialsPresent, configuration.ExpiresAt,
            configuration.IceTransportPolicy);
    }

    private static bool HasScheme(string? value, params string[] schemes) =>
        value is not null && schemes.Any(scheme => value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase));
}
