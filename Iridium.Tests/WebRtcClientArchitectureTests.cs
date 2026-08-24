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
        Assert.Contains("WebRTC connected", directJs);
        Assert.Contains("WebRTC connected", communityJs);
        Assert.Contains("WebRTC connection failed", directJs);
        Assert.Contains("WebRTC connection failed", communityJs);
        Assert.Contains("hostCandidateAvailable", directJs);
        Assert.Contains("hostCandidateAvailable", communityJs);
        Assert.Contains("turnConfiguredButNoRelayCandidate", directJs);
        Assert.Contains("turnConfiguredButNoRelayCandidate", communityJs);
        Assert.DoesNotContain("mediaSession.iceServers", communityJs);
    }

    [Fact]
    public void ProductionMediaUsesOneSfuRoomAndNeverCreatesRemoteUserPeerConnections()
    {
        var registration = Source("Iridium.Web", "Program.cs");
        var sfu = Source("Iridium.Web", "wwwroot", "js", "liveKitMedia.js");
        var callAdapter = Source("Iridium.Web", "Services", "LiveKitCallMediaService.cs");
        var communityAdapter = Source("Iridium.Web", "Services", "LiveKitCommunityVoiceMediaClient.cs");

        Assert.Contains("AddScoped<ICallMediaService, LiveKitCallMediaService>", registration);
        Assert.Contains("AddScoped<ICommunityVoiceMediaClient, LiveKitCommunityVoiceMediaClient>", registration);
        Assert.DoesNotContain("new RTCPeerConnection(", sfu);
        Assert.Contains("new Room(", sfu);
        Assert.Contains("autoSubscribe: false", sfu);
        Assert.Contains("setStreamSubscription", sfu);
        Assert.Contains("NodeSfu", callAdapter);
        Assert.Contains("livekit", communityAdapter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CreateOfferAsync", communityAdapter);
    }

    [Fact]
    public void LiveKitScreenShareUsesNativeHighRateCaptureAndExplicitQualityControls()
    {
        var sfu = Source("Iridium.Web", "wwwroot", "js", "liveKitMedia.js");

        Assert.Contains("width: 3840, height: 2160, frameRate: 60", sfu);
        Assert.Contains("name !== \"Safari\"", sfu);
        Assert.Contains("contentHint: \"detail\"", sfu);
        Assert.Contains("screenShareEncoding: { maxBitrate, maxFramerate: frameRate, priority: \"high\" }", sfu);
        Assert.Contains("screenShareSimulcastLayers", sfu);
        Assert.Contains("simulcast: true", sfu);
        Assert.Contains("degradationPreference: \"maintain-resolution\"", sfu);
        Assert.Contains("videoCodec: \"vp8\"", sfu);
        Assert.Contains("setVideoQuality(VideoQuality.HIGH)", sfu);
        Assert.Contains("adaptiveStream: false", sfu);
        Assert.Contains("targetMaxBitrateBps", sfu);
        Assert.Contains("bitrateBps", sfu);
        Assert.Contains("{ pixels: 640 * 360, fps30: 1_000_000, fps60: 1_500_000 }", sfu);
        Assert.Contains("{ pixels: 1920 * 1080, fps30: 6_000_000, fps60: 10_000_000 }", sfu);
        Assert.Contains("{ pixels: 2560 * 1440, fps30: 10_000_000, fps60: 16_000_000 }", sfu);
        Assert.Contains("{ pixels: 3840 * 2160, fps30: 20_000_000, fps60: 30_000_000 }", sfu);
        Assert.Contains("senderEncodingSummary(track)", sfu);
        Assert.Contains("qualityLimitationReasons", sfu);
        Assert.Contains("framesPerSecond", sfu);
    }

    [Fact]
    public void LiveKitMicrophoneUsesNodeConfiguredHighQualityMonoOpusProfile()
    {
        var sfu = Source("Iridium.Web", "wwwroot", "js", "liveKitMedia.js");

        Assert.Contains("microphoneProfile(nodeSession.voiceBitrate)", sfu);
        Assert.Contains("channelCount: 1", sfu);
        Assert.Contains("echoCancellation: true", sfu);
        Assert.Contains("noiseSuppression: true", sfu);
        Assert.Contains("autoGainControl: true", sfu);
        Assert.Contains("audioPreset: { maxBitrate: bitrate, priority: \"high\" }", sfu);
        Assert.Contains("true, microphone.capture, microphone.publish", sfu);
        Assert.Contains("dtx: true", sfu);
        Assert.Contains("red: true", sfu);
        Assert.Contains("forceStereo: false", sfu);
        Assert.Contains("actualBitrateBps", sfu);
        Assert.Contains("packetLossPercent", sfu);
        Assert.Contains("jitterMs", sfu);
        Assert.Contains("rttMs", sfu);
        Assert.Contains("opusInBandFec", sfu);
        Assert.Contains("senderEncodingSummary(microphonePublication.track)", sfu);
        Assert.Contains("screenShareEncoding: { maxBitrate, maxFramerate: frameRate", sfu);
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
