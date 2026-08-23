using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class WebRtcClientArchitectureTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void EveryPeerFactoryConsumesTheCommonNodeConfiguration()
    {
        var direct = Source("Iridium.Web", "Services", "WebRtcCallMediaService.cs");
        var community = Source("Iridium.Web", "Services", "BrowserCommunityVoiceMediaClient.cs");
        var directJs = Source("Iridium.Web", "wwwroot", "js", "voiceCall.js");
        var communityJs = Source("Iridium.Web", "wwwroot", "js", "communityVoiceMedia.js");

        Assert.Contains("IWebRtcConfigurationProvider webRtcConfiguration", direct);
        Assert.Contains("IWebRtcConfigurationProvider webRtcConfiguration", community);
        Assert.Contains("webRtcConfiguration.GetAsync", direct);
        Assert.Contains("webRtcConfiguration.GetAsync", community);
        Assert.Equal(1, Count(directJs, "new RTCPeerConnection("));
        Assert.Equal(1, Count(communityJs, "new RTCPeerConnection("));
        Assert.Contains("iceTransportPolicy", directJs);
        Assert.Contains("iceTransportPolicy", communityJs);
        Assert.DoesNotContain("mediaSession.iceServers", communityJs);
    }

    [Fact]
    public void InviteRouteAndCopySurfaceUseCanonicalServerUrl()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var endpoint = Source("Iridium.Server", "Api", "CommunityManagementEndpoints.cs");
        Assert.Contains("@page \"/invite/{InviteToken}\"", home);
        Assert.Contains("InviteUrl=\"@_quickInviteUrl\"", home);
        Assert.DoesNotContain("InviteUrl=\"_quickInviteUrl\"", home);
        Assert.Contains("PublicOrigin(context, options.Value)", endpoint);
        Assert.Contains("options.PublicAuthority", endpoint);
    }

    [Fact]
    public async Task ConfigurationProviderCachesAndRefreshesBeforeTurnExpiry()
    {
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-08-24T00:00:00Z"));
        var source = new TestConfigurationSource(clock);
        using var provider = new WebRtcConfigurationProvider(source, clock);

        var first = await provider.GetAsync();
        var cached = await provider.GetAsync();
        Assert.Same(first, cached);
        Assert.Equal(1, source.FetchCount);

        clock.Advance(TimeSpan.FromMinutes(49));
        Assert.Same(first, await provider.GetAsync());
        Assert.Equal(1, source.FetchCount);

        clock.Advance(TimeSpan.FromMinutes(7));
        Assert.NotSame(first, await provider.GetAsync());
        Assert.Equal(2, source.FetchCount);
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));
    private static int Count(string source, string value) =>
        (source.Length - source.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private sealed class TestConfigurationSource(TestTimeProvider clock) : IWebRtcConfigurationSource
    {
        public string CacheKey => "https://node.example|account";
        public int FetchCount { get; private set; }
        public Task<WebRtcIceConfigurationDto> FetchAsync(CancellationToken cancellationToken = default)
        {
            FetchCount++;
            return Task.FromResult(new WebRtcIceConfigurationDto([], ExpiresAt: clock.GetUtcNow().AddHours(1)));
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
