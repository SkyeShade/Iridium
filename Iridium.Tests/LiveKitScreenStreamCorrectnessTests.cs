namespace Iridium.Tests;

public sealed class LiveKitScreenStreamCorrectnessTests
{
    [Fact]
    public void ViewerParticipantStripUsesSharedBasePresentationWithoutPersonaOverride()
    {
        var viewer = Source("Iridium.Web", "Components", "VoiceStreamViewer.razor");

        Assert.Contains("VoiceParticipantPresentationResolver.ResolveBase", viewer);
        Assert.Contains("CommunityManagement?.Members ?? []", viewer);
        Assert.Contains("Session.DirectConversations, Session.Friends", viewer);
        Assert.DoesNotContain("AvatarPresetId=\"participant.AvatarPresetId\"", viewer);
        Assert.Contains("Presence=\"@presentation.Presence\"", viewer);
        Assert.Contains("DisplayName=\"@presentation.DisplayName\"", viewer);
    }

    [Fact]
    public void LiveViewerHasNoNativePlaybackControlsAndActivelyRejectsPause()
    {
        var viewer = Source("Iridium.Web", "Components", "VoiceStreamViewer.razor");
        var liveKit = Source("Iridium.Web", "wwwroot", "js", "liveKitMedia.js");

        Assert.Contains("<video id=\"@_elementId\" autoplay playsinline", viewer);
        Assert.DoesNotContain("<video id=\"@_elementId\" autoplay playsinline controls", viewer);
        Assert.Contains("element.controls = false", liveKit);
        Assert.Contains("element.removeAttribute(\"controls\")", liveKit);
        Assert.Contains("element.addEventListener(\"pause\", viewer.resumeLiveVideo)", liveKit);
        Assert.Contains("!session.viewers.has(viewer.elementId)", liveKit);
    }

    [Fact]
    public void DisplayAudioUsesCanonicalCapturePublishSubscribeAndGainPath()
    {
        var viewer = Source("Iridium.Web", "Components", "VoiceStreamViewer.razor");
        var liveKit = Source("Iridium.Web", "wwwroot", "js", "liveKitMedia.js");

        Assert.Contains("const captureOptions = { audio: true, video: true }", liveKit);
        Assert.Contains("captureOptions.systemAudio = \"include\"", liveKit);
        Assert.Contains("audioTrack.source = Track.Source.ScreenShareAudio", liveKit);
        Assert.Contains("session.room.localParticipant.publishTrack(track", liveKit);
        Assert.Contains("publication.source === Track.Source.ScreenShareAudio", liveKit);
        Assert.Contains("createRemoteVoicePlayback(new MediaStream([audioTrack])", liveKit);
        Assert.Contains("@if (stream.HasAudio)", viewer);
        Assert.Contains("Stream Volume", viewer);
        Assert.Contains("No share audio", viewer);
    }

    [Fact]
    public void DiagnosticsCaptureSenderAndSubscriberFrameHealth()
    {
        var liveKit = Source("Iridium.Web", "wwwroot", "js", "liveKitMedia.js");

        Assert.Contains("frameRate: videoSettings.frameRate", liveKit);
        Assert.Contains("framesEncoded", liveKit);
        Assert.Contains("framesSent", liveKit);
        Assert.Contains("totalEncodeTime", liveKit);
        Assert.Contains("qualityLimitationDurations", liveKit);
        Assert.Contains("framesDecoded", liveKit);
        Assert.Contains("framesDropped", liveKit);
        Assert.Contains("freezeCount", liveKit);
        Assert.Contains("totalFreezesDuration", liveKit);
        Assert.Contains("jitterMs", liveKit);
        Assert.Contains("decoderImplementations", liveKit);
        Assert.Contains("sample: \"10s\"", liveKit);
    }

    private static string Source(params string[] parts) => File.ReadAllText(
        Path.Combine([FindRepositoryRoot(), .. parts]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Iridium.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
