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
    TimeProvider timeProvider) : IWebRtcIceConfigurationService
{
    private const int MinimumLifetimeSeconds = 300;
    private const int MaximumLifetimeSeconds = 86_400;

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
        if (turn.Enabled && turnUrls.Count > 0 && !string.IsNullOrWhiteSpace(turn.SharedSecret))
        {
            var lifetime = Math.Clamp(turn.CredentialLifetimeSeconds, MinimumLifetimeSeconds, MaximumLifetimeSeconds);
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(
                timeProvider.GetUtcNow().AddSeconds(lifetime).ToUnixTimeSeconds());
            var username = $"{expiresAt.Value.ToUnixTimeSeconds()}:{accountId:N}";
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(turn.SharedSecret));
            var credential = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(username)));
            iceServers.Add(new IceServerDto(turnUrls, username, credential));
        }

        var policy = string.Equals(configured.IceTransportPolicy, "relay", StringComparison.OrdinalIgnoreCase)
            ? "relay" : "all";
        return new WebRtcIceConfigurationDto(iceServers, policy, expiresAt);
    }

    private static List<string> ValidUrls(IEnumerable<string>? urls, params string[] schemes) =>
        (urls ?? []).Select(value => value?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value) &&
            schemes.Any(scheme => value!.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)))
            .Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
