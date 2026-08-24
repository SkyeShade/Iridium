using System.Security.Cryptography;
using System.Text;
using Iridium.Protocol;
using Iridium.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Iridium.Server.Calls;

public interface IWebRtcIceConfigurationService
{
    WebRtcIceConfigurationDto Create(Guid accountId);
}

public sealed class WebRtcIceConfigurationService(
    IOptions<WebRtcOptions> options,
    TimeProvider timeProvider,
    ILogger<WebRtcIceConfigurationService> logger) : IWebRtcIceConfigurationService
{
    private const int MinimumLifetimeSeconds = 300;
    private const int MaximumLifetimeSeconds = 86_400;
    private int _invalidTurnConfigurationLogged;

    public WebRtcIceConfigurationDto Create(Guid accountId)
    {
        var configured = options.Value;
        var iceServers = configured.IceServers
            .Select(value => ValidUrls(value.Urls, "stun:", "stuns:"))
            .Where(value => value.Count > 0)
            .Select(value => new IceServerDto(value))
            .ToList();
        DateTimeOffset? expiresAt = null;
        var turn = configured.Turn;
        var turnUrls = ValidUrls(turn.Urls, "turn:", "turns:");
        var turnUsable = turnUrls.Count > 0 && !string.IsNullOrWhiteSpace(turn.SharedSecret);
        if (turn.Enabled && turnUsable)
        {
            var lifetime = Math.Clamp(turn.CredentialLifetimeSeconds, MinimumLifetimeSeconds, MaximumLifetimeSeconds);
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(
                timeProvider.GetUtcNow().AddSeconds(lifetime).ToUnixTimeSeconds());
            var username = $"{expiresAt.Value.ToUnixTimeSeconds()}:{accountId:N}";
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(turn.SharedSecret!));
            var credential = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(username)));
            iceServers.Add(new IceServerDto(turnUrls, username, credential));
        }
        else if (turn.Enabled && Interlocked.Exchange(ref _invalidTurnConfigurationLogged, 1) == 0)
        {
            logger.LogError(
                "TURN is enabled but unusable. ValidTurnUrlsPresent={ValidTurnUrlsPresent}; SharedSecretPresent={SharedSecretPresent}. " +
                "Configure at least one turn:/turns: URL and WebRtc:Turn:SharedSecret.",
                turnUrls.Count > 0, !string.IsNullOrWhiteSpace(turn.SharedSecret));
        }

        var policy = string.Equals(configured.IceTransportPolicy, "relay", StringComparison.OrdinalIgnoreCase)
            ? "relay" : "all";
        if (policy == "relay" && !iceServers.Any(server => server.Urls.Any(url =>
                url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))) &&
            Interlocked.Exchange(ref _invalidTurnConfigurationLogged, 1) == 0)
            logger.LogError(
                "WebRtc:IceTransportPolicy is relay, but no usable TURN server could be issued. " +
                "Relay-only WebRTC connections cannot succeed until TURN is enabled and configured.");
        var result = new WebRtcIceConfigurationDto(iceServers, policy, expiresAt);
        logger.LogDebug(
            "ICE configuration issued: STUN servers={StunServers}; TURN servers={TurnServers}; " +
            "TURN credentials present={TurnCredentialsPresent}; ExpiresAt={ExpiresAt}; TransportPolicy={TransportPolicy}.",
            iceServers.Count(server => server.Urls.Any(url => url.StartsWith("stun:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("stuns:", StringComparison.OrdinalIgnoreCase))),
            iceServers.Count(server => server.Urls.Any(url => url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))),
            iceServers.Any(server => !string.IsNullOrWhiteSpace(server.Username) &&
                !string.IsNullOrWhiteSpace(server.Credential)), expiresAt, policy);
        return result;
    }

    private static List<string> ValidUrls(IEnumerable<string>? urls, params string[] schemes) =>
        (urls ?? []).Select(value => value?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value) &&
            schemes.Any(scheme => value!.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)))
            .Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
