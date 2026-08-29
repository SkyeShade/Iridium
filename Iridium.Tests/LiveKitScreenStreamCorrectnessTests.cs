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
        Assert.Contains("BadgeMode=\"AvatarBadgeMode.None\"", viewer);
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
        Assert.Contains("publication?.trackInfo?.stream", liveKit);
        Assert.Contains("publicationStreamIdentity(p) === mediaStreamId", liveKit);
        Assert.Contains("createRemoteVoicePlayback(new MediaStream([audioTrack])", liveKit);
        Assert.Contains("@if (stream.HasAudio)", viewer);
        Assert.Contains("Stream Volume", viewer);
        Assert.Contains("No share audio", viewer);
    }

    [Fact]
    public void StreamAudioDiagnosticsCoverEveryCaptureToPlaybackBoundary()
    {
        var liveKit = Source("Iridium.Web", "wwwroot", "js", "liveKitMedia.js");

        Assert.Contains("videoTracks: videoTracks.length, audioTracks: audioTracks.length", liveKit);
        Assert.Contains("LiveKit screen share audio published", liveKit);
        Assert.Contains("publication: safePublicationDiagnostic(publication)", liveKit);
        Assert.Contains("LiveKit screen publication discovered", liveKit);
        Assert.Contains("LiveKit screen track subscribed", liveKit);
        Assert.Contains("LiveKit screen viewer media attached", liveKit);
        Assert.Contains("screenAudioAttached: true, playSucceeded: !playback.playBlocked", liveKit);
        Assert.Contains("selfPreview:", liveKit);
        Assert.Contains("audioMuted: viewer.audioMuted", liveKit);
    }

    [Fact]
    public void ScreenAudioLifecycleUpdatesMetadataAndReconnectRestoresExplicitSubscriptions()
    {
        var liveKit = Source("Iridium.Web", "wwwroot", "js", "liveKitMedia.js");
        var calls = Source("Iridium.Client.Core", "CallClientService.cs");
        var community = Source("Iridium.Client.Core", "CommunityVoiceSession.cs");

        Assert.Contains("OnScreenShareAudioAvailabilityChanged", liveKit);
        Assert.Contains("screenTracks.filter(track => track !== audio)", liveKit);
        Assert.Contains("RoomEvent.TrackUnpublished", liveKit);
        Assert.Contains("RoomEvent.TrackMuted", liveKit);
        Assert.Contains("RoomEvent.TrackUnmuted", liveKit);
        Assert.Contains("RoomEvent.LocalTrackPublished", liveKit);
        Assert.Contains("RoomEvent.LocalTrackUnpublished", liveKit);
        Assert.Contains("RoomEvent.Reconnected", liveKit);
        Assert.Contains("for (const mediaStreamId of session.watched) refreshViewers", liveKit);
        Assert.Contains("ScreenShareAudioAvailabilityChanged += MediaScreenShareAudioAvailabilityChangedAsync", calls);
        Assert.Contains("VoiceStreamHubContract.Update", calls);
        Assert.Contains("ScreenShareAudioAvailabilityChanged += MediaScreenShareAudioAvailabilityChangedAsync", community);
        Assert.Contains("VoiceStreamHubContract.Update", community);
    }

    [Fact]
    public void StreamAudioPlaybackIsSeparateFromMicrophoneAndIsDestroyedWithViewer()
    {
        var liveKit = Source("Iridium.Web", "wwwroot", "js", "liveKitMedia.js");
        var coordinator = Source("Iridium.Client.Core", "ActiveVoiceSessionCoordinator.cs");

        Assert.Contains("if (publication.source === Track.Source.Microphone) makeAudioPlayback", liveKit);
        Assert.Contains("viewer.audioPlayback = playback", liveKit);
        Assert.Contains("updateRemoteVoicePlayback(viewer?.audioPlayback, { locallyMuted: muted", liveKit);
        Assert.Contains("if (viewer?.audioPlayback) destroyRemoteVoicePlayback(viewer.audioPlayback)", liveKit);
        Assert.Contains("if (watched.OwnerAccountId == LocalAccountId) _mutedStreamAudio.Add", coordinator);
    }

    [Fact]
    public void DiagnosticsCaptureSenderAndSubscriberFrameHealth()
    {
        var liveKit = Source("Iridium.Web", "wwwroot", "js", "liveKitMedia.js");

        Assert.Contains("frameRate: settings.frameRate", liveKit);
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

    [Fact]
    public void StreamVolumeIsLocalPerViewerAndPersistedByOwner()
    {
        var viewer = Source("Iridium.Web", "Components", "VoiceStreamViewer.razor");
        var liveKit = Source("Iridium.Web", "wwwroot", "js", "liveKitMedia.js");

        Assert.Contains("Preferences.GetAsync(stream.OwnerAccountId)", viewer);
        Assert.Contains("Preferences.SetScreenShareVolumeAsync(stream.OwnerAccountId", viewer);
        Assert.Contains("session.viewers.set(elementId", liveKit);
        Assert.Contains("viewer.audioPlayback", liveKit);
        Assert.Contains("minimumVolumePercent: 0", liveKit);
        Assert.Contains("volumePercent: viewer.volumePercent", liveKit);
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
