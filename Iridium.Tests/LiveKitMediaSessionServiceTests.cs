using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iridium.Protocol;
using Iridium.Server.Configuration;
using Iridium.Server.Voice;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iridium.Tests;

public sealed class LiveKitMediaSessionServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-24T12:00:00Z");
    private const string Secret = "server-only-livekit-secret";

    [Fact]
    public void DirectCallTokenIsSignedShortLivedAndIdentityScoped()
    {
        var accountId = Guid.Parse("76f4db0d-fd45-412c-8d29-1c0ae95125ea");
        var callId = Guid.Parse("63c335e6-08f3-43ca-90af-b56f9c550e44");
        var session = Create().CreateDirectCallSession(callId, accountId);
        var pieces = session.AccessToken.Split('.');
        Assert.Equal(3, pieces.Length);
        using var document = JsonDocument.Parse(Decode(pieces[1]));
        var payload = document.RootElement;

        Assert.Equal("iridium-api-key", payload.GetProperty("iss").GetString());
        Assert.Equal(accountId.ToString("N"), payload.GetProperty("sub").GetString());
        Assert.Equal($"iridium-direct-{callId:N}", payload.GetProperty("video").GetProperty("room").GetString());
        Assert.True(payload.GetProperty("video").GetProperty("roomJoin").GetBoolean());
        Assert.True(payload.GetProperty("video").GetProperty("canPublish").GetBoolean());
        Assert.True(payload.GetProperty("video").GetProperty("canSubscribe").GetBoolean());
        Assert.InRange(payload.GetProperty("exp").GetInt64() - Now.ToUnixTimeSeconds(), 60, 300);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        Assert.Equal(pieces[2], Encode(hmac.ComputeHash(Encoding.ASCII.GetBytes($"{pieces[0]}.{pieces[1]}"))));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(session), StringComparison.Ordinal);
    }

    [Fact]
    public void CommunityRoomAndConversationKindAreDistinctAndStable()
    {
        var service = Create();
        var accountId = Guid.NewGuid(); var communityId = Guid.NewGuid(); var channelId = Guid.NewGuid();
        var session = service.CreateCommunityVoiceSession(communityId, channelId, accountId, true);
        Assert.Equal(NodeMediaRoomKind.CommunityVoice, session.RoomKind);
        Assert.Equal($"iridium-community-{communityId:N}-voice-{channelId:N}", session.RoomName);
        Assert.Equal(accountId.ToString("N"), session.ParticipantIdentity);
        Assert.Equal("livekit", session.Provider);
    }

    [Fact]
    public void CommunityTokenOmitsScreenSourcesWithoutSharePermission()
    {
        var session = Create().CreateCommunityVoiceSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), false);
        using var document = JsonDocument.Parse(Decode(session.AccessToken.Split('.')[1]));
        var sources = document.RootElement.GetProperty("video").GetProperty("canPublishSources")
            .EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Equal(["microphone"], sources);
    }

    [Theory]
    [InlineData(null, "key", "secret")]
    [InlineData("https://media.example.net", "key", "secret")]
    [InlineData("wss://media.example.net", null, "secret")]
    [InlineData("wss://media.example.net", "key", null)]
    public void InvalidConfigurationDisablesMediaWithoutBreakingNode(string? url, string? key, string? secret)
    {
        var service = Create(url, key, secret);
        Assert.False(service.Enabled);
        Assert.Throws<InvalidOperationException>(() => service.CreateDirectCallSession(Guid.NewGuid(), Guid.NewGuid()));
    }

    private static LiveKitMediaSessionService Create(string? url = "wss://media.example.net",
        string? key = "iridium-api-key", string? secret = Secret) => new(
        Options.Create(new MediaOptions { Provider = MediaProvider.LiveKit, PublicUrl = url, ApiKey = key,
            ApiSecret = secret, JoinTokenLifetimeSeconds = 300 }),
        new TestTimeProvider(Now), new TestEnvironment(), NullLogger<LiveKitMediaSessionService>.Instance);

    private static byte[] Decode(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        value += new string('=', (4 - value.Length % 4) % 4);
        return Convert.FromBase64String(value);
    }
    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Iridium.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
