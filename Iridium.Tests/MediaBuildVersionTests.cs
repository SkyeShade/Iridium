using System.Reflection;
using System.Text.Json;

namespace Iridium.Tests;

public sealed class MediaBuildVersionTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void GeneratedManifestMatchesTheWasmAssemblyBuildMetadata()
    {
        var manifestPath = Path.Combine(Root, "Iridium.Web", "wwwroot", "media-build.json");
        var assemblyPath = Path.Combine(Root, "Iridium.Web", "bin", "Debug", "net10.0", "Iridium.Web.dll");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifestId = manifest.RootElement.GetProperty("buildId").GetString();
        var assembly = Assembly.LoadFile(assemblyPath);
        var assemblyId = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(value => value.Key == "IridiumMediaBuildId").Value;

        Assert.False(string.IsNullOrWhiteSpace(manifestId));
        Assert.Equal(assemblyId, manifestId);
    }

    [Fact]
    public void MediaModulesUseAutomaticBuildUrlsWithoutManualReleaseTags()
    {
        var direct = Source("Iridium.Web", "Services", "WebRtcCallMediaService.cs");
        var community = Source("Iridium.Web", "Services", "BrowserCommunityVoiceMediaClient.cs");
        var liveKit = Source("Iridium.Web", "wwwroot", "js", "liveKitMedia.js");
        var directJs = Source("Iridium.Web", "wwwroot", "js", "voiceCall.js");
        var communityJs = Source("Iridium.Web", "wwwroot", "js", "communityVoiceMedia.js");
        var project = Source("Iridium.Web", "Iridium.Web.csproj");

        Assert.Contains("MediaBuildInfo.Id", direct);
        Assert.Contains("MediaBuildInfo.Id", community);
        Assert.Contains("?build=", direct);
        Assert.Contains("?build=", community);
        Assert.Contains("IridiumMediaBuildId", project);
        Assert.Contains("requireMatchingMediaBuild(mediaBuildId)", directJs);
        Assert.Contains("requireMatchingMediaBuild(mediaBuildId)", communityJs);
        Assert.Contains("LivekitClient", liveKit);
        Assert.DoesNotContain("screen-v1", direct);
        Assert.DoesNotContain("screen-v1", community);
        Assert.DoesNotContain("IceInteropProtocolVersion", direct);
    }

    [Fact]
    public void MismatchRecoveryIsSessionScopedAndReloadsOnlyOncePerBuild()
    {
        var recovery = Source("Iridium.Web", "wwwroot", "js", "clientUpdate.js");
        Assert.Contains("sessionStorage.getItem(key)", recovery);
        Assert.Contains("sessionStorage.setItem(key, \"attempted\")", recovery);
        Assert.Contains("registration?.update()", recovery);
        Assert.Contains("location.replace(target.href)", recovery);
        Assert.Contains("return false", recovery);
    }

    [Fact]
    public void PublishedWorkerAndNginxRevalidateEntryAndOwnedAssets()
    {
        var worker = Source("Iridium.Web", "wwwroot", "service-worker.published.js");
        var nginx = Source("deploy", "nginx-iridium.conf");
        Assert.Contains("self.skipWaiting()", worker);
        Assert.Contains("self.clients.claim()", worker);
        Assert.Contains("requestUrl.pathname.includes('/js/')", worker);
        Assert.Contains("cache: 'no-cache'", worker);
        Assert.Contains("location = /index.html", nginx);
        Assert.Contains("location = /service-worker.js", nginx);
        Assert.Contains("max-age=31536000, immutable", nginx);
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));
}
