using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iridium.Protocol;
using Iridium.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Iridium.Server.Voice;

/// <summary>Issues short-lived, room-scoped LiveKit participant credentials. The API secret never leaves the Node.</summary>
public sealed class LiveKitMediaSessionService : INodeMediaSessionService
{
    private readonly MediaOptions _options;
    private readonly TimeProvider _time;
    private readonly bool _development;

    public LiveKitMediaSessionService(IOptions<MediaOptions> options, TimeProvider time,
        IHostEnvironment environment, ILogger<LiveKitMediaSessionService> logger)
    {
        _options = options.Value;
        _time = time;
        _development = environment.IsDevelopment();
        if (_options.Provider == MediaProvider.LiveKit && !TryValidate(out var error))
            logger.LogError("LiveKit media is enabled but its configuration is unusable: {Error}", error);
        else if (Enabled)
            logger.LogInformation("LiveKit media enabled at {PublicUrl}; join-token lifetime={LifetimeSeconds}s.",
                _options.PublicUrl, Lifetime.TotalSeconds);
    }

    public bool Enabled => _options.Provider == MediaProvider.LiveKit && TryValidate(out _);
    public string Provider => Enabled ? "livekit" : "none";

    public NodeMediaSessionDto CreateDirectCallSession(Guid callId, Guid accountId) =>
        Create($"iridium-direct-{callId:N}", accountId, NodeMediaRoomKind.DirectCall, true);

    public NodeMediaSessionDto CreateCommunityVoiceSession(Guid communityId, Guid channelId, Guid accountId,
        bool canPublishScreen) =>
        Create($"iridium-community-{communityId:N}-voice-{channelId:N}", accountId,
            NodeMediaRoomKind.CommunityVoice, canPublishScreen);

    private NodeMediaSessionDto Create(string roomName, Guid accountId, NodeMediaRoomKind kind, bool canPublishScreen)
    {
        if (!TryValidate(out var error)) throw new InvalidOperationException($"Node media is unavailable: {error}");
        var now = _time.GetUtcNow();
        var expires = now.Add(Lifetime);
        var identity = accountId.ToString("N");
        var token = Sign(new Dictionary<string, object?>
        {
            ["iss"] = _options.ApiKey!, ["sub"] = identity,
            ["nbf"] = now.ToUnixTimeSeconds() - 5, ["exp"] = expires.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["video"] = new Dictionary<string, object?>
            {
                ["roomJoin"] = true, ["room"] = roomName,
                ["canPublish"] = true, ["canSubscribe"] = true, ["canPublishData"] = false,
                ["canPublishSources"] = canPublishScreen
                    ? new[] { "microphone", "screen_share", "screen_share_audio", "camera" }
                    : new[] { "microphone" }
            }
        });
        return new NodeMediaSessionDto("livekit", _options.PublicUrl!, token, roomName, identity, kind,
            expires, _development);
    }

    private TimeSpan Lifetime => TimeSpan.FromSeconds(Math.Clamp(_options.JoinTokenLifetimeSeconds, 60, 900));

    private bool TryValidate(out string error)
    {
        if (_options.Provider != MediaProvider.LiveKit) { error = "provider is disabled"; return false; }
        if (!Uri.TryCreate(_options.PublicUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("ws" or "wss")) { error = "PublicUrl must be an absolute ws:// or wss:// URL"; return false; }
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) { error = "ApiKey is missing"; return false; }
        if (string.IsNullOrWhiteSpace(_options.ApiSecret)) { error = "ApiSecret is missing"; return false; }
        error = string.Empty; return true;
    }

    private string Sign(Dictionary<string, object?> payload)
    {
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var body = Encode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var unsigned = $"{header}.{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.ApiSecret!));
        return $"{unsigned}.{Encode(hmac.ComputeHash(Encoding.ASCII.GetBytes(unsigned)))}";
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=')
        .Replace('+', '-').Replace('/', '_');
}
