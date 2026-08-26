namespace Iridium.Tests;

public sealed class VoiceStreamViewerShareControlTests
{
    [Fact]
    public void OwnPublishedStreamUsesShareMenuWhileRemoteStreamKeepsStopWatching()
    {
        var viewer = Source("Iridium.Web", "Components", "VoiceStreamViewer.razor");
        var controls = Section(viewer, "<div class=\"viewer-controls\">", "</section>");

        Assert.Contains("@if (IsOwnPublishedShare(stream))", controls);
        Assert.Contains("aria-label=\"Screen share options\"", controls);
        Assert.Contains("Change Shared Window", controls);
        Assert.Contains("Stop Sharing", controls);
        Assert.Contains("else", controls);
        Assert.Contains("Stop Watching", controls);
        Assert.Contains("Voice.PublishedStreams.Any(value => value.StreamId == stream.StreamId", viewer);
    }

    [Fact]
    public void ShareMenuUsesExistingSwitchAndStopPublicationActions()
    {
        var viewer = Source("Iridium.Web", "Components", "VoiceStreamViewer.razor");

        Assert.Contains("await SwitchSharingAsync()", Method(viewer, "private async Task ChangeSharedWindowAsync", "private async Task StopOwnShareAsync"));
        Assert.Contains("await StopSharingAsync()", Method(viewer, "private async Task StopOwnShareAsync", "private async Task CloseAsync"));
        Assert.Contains("Voice.SwitchScreenShareAsync()", viewer);
        Assert.Contains("Voice.StopScreenShareAsync()", viewer);
    }

    [Fact]
    public void ViewerCloseStopsWatchingWithoutEndingOwnPublication()
    {
        var viewer = Source("Iridium.Web", "Components", "VoiceStreamViewer.razor");
        var close = Method(viewer, "private async Task CloseAsync", "private async Task LeaveAsync");

        Assert.Contains("Voice.StopWatchingAsync()", close);
        Assert.DoesNotContain("StopScreenShareAsync", close);
    }

    [Fact]
    public void ShareMenuClosesOnOutsideClickEscapeAndSelection()
    {
        var viewer = Source("Iridium.Web", "Components", "VoiceStreamViewer.razor");

        Assert.Contains("@onclick=\"CloseShareMenu\" @onkeydown=\"ViewerKeyDown\"", viewer);
        Assert.Contains("if (args.Key == \"Escape\") CloseShareMenu()", viewer);
        Assert.Contains("CloseShareMenu();\n        await SwitchSharingAsync()", Normalize(viewer));
        Assert.Contains("CloseShareMenu();\n        await StopSharingAsync()", Normalize(viewer));
    }

    [Fact]
    public void SwitchingCapturesBeforeReplacingSoPickerCancellationPreservesShare()
    {
        var liveKit = Source("Iridium.Web", "wwwroot", "js", "liveKitMedia.js");
        var method = Method(liveKit, "export async function switchScreenShare", "export async function replacePublishedScreenTracks");

        Assert.True(method.IndexOf("const replacement = await captureScreenTracks()", StringComparison.Ordinal) <
                    method.IndexOf("replacePublishedScreenTracks(session, replacement)", StringComparison.Ordinal));
        Assert.DoesNotContain("stopScreenShare", method);
    }

    private static string Method(string source, string startMarker, string endMarker) =>
        Section(source, startMarker, endMarker);

    private static string Section(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

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
