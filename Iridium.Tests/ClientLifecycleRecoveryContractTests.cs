namespace Iridium.Tests;

public sealed class ClientLifecycleRecoveryContractTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void RealtimeServiceCentralizesSerializedGenerationSafeRecovery()
    {
        var source = Source("Iridium.Client.Core", "RealtimeConnectionService.cs");

        Assert.Contains("public enum RealtimeLifecycleState", source);
        Assert.Contains("private readonly SemaphoreSlim _recoveryGate", source);
        Assert.Contains("public async Task VerifyAndRecoverAsync", source);
        Assert.Contains("RegisterRecoveryHandler", source);
        Assert.Contains("RunRecoveryPipelineAsync", source);
        Assert.Contains("fresh-start:", source);
        Assert.Contains("automatic-reconnected", source);
        Assert.Contains("_deferredRecoveryRequested", source);
        Assert.Contains("VerifyAfterDeferredCloseAsync", source);
        Assert.Contains("ReferenceEquals(_connection, connection) && _connectionGeneration == generation", source);
        Assert.DoesNotContain("connection.Closed += async", source);
    }

    [Fact]
    public void BrowserResumeEventsCoalesceIntoOneVerificationBridgeAndAreDisposed()
    {
        var js = Source("Iridium.UI", "wwwroot", "js", "mobileConversationSwipe.js");
        var shell = Source("Iridium.UI", "ApplicationShell.razor");

        Assert.Contains("document.addEventListener('visibilitychange', visibility)", js);
        Assert.Contains("window.addEventListener('pageshow', pageshow)", js);
        Assert.Contains("window.addEventListener('online', online)", js);
        Assert.Contains("window.addEventListener('focus', focus)", js);
        Assert.Contains("window.setTimeout", js);
        Assert.Contains("unwireRealtimeResume", js);
        Assert.Contains("Realtime.VerifyAndRecoverAsync", shell);
    }

    [Fact]
    public void MessagingRecoverySeparatesRejoinsAndReconcilesVisibleAndSummaryState()
    {
        var source = Source("Iridium.Client.Core", "ChannelMessagingSession.cs");
        var recovery = Slice(source, "private async Task RecoverRealtimeAsync", "private async Task TryRecoveryStepAsync");

        Assert.Contains("TryRecoveryStepAsync(\"channel rejoin\"", recovery);
        Assert.Contains("TryRecoveryStepAsync(\"Direct Message rejoin\"", recovery);
        Assert.Contains("GetChannelMessagePageAsync", recovery);
        Assert.Contains("ReconcileChannelRecent", recovery);
        Assert.Contains("GetDirectMessagePageAsync", recovery);
        Assert.Contains("ReconcileDirectRecent", recovery);
        Assert.Contains("RefreshCommunitiesAsync", recovery);
        Assert.Contains("RefreshDirectConversationsAsync", recovery);
        Assert.Contains("RefreshFriendsAsync", recovery);
        Assert.Contains("ApplyRealtimeReconnected", recovery);
        Assert.DoesNotContain("connection.Reconnected +=", source);
    }

    [Fact]
    public void CallRecoveryClearsTransientBannerForRestoredAndMissingCalls()
    {
        var source = Source("Iridium.Client.Core", "CallClientService.cs");
        var lifecycle = Slice(source, "private void RealtimeLifecycleChanged", "private async Task RecoverSignalingAsync");
        var restore = Slice(source, "private async Task RestoreCurrentCallAsync", "private async Task FinishAsync");

        Assert.Contains("Signaling reconnecting; audio may continue…", lifecycle);
        Assert.Contains("RealtimeLifecycleState.Disconnected", lifecycle);
        Assert.Contains("Signaling reconnected, but call state could not be restored.", source);
        Assert.Contains("else if (restored is null)\n        {\n            StatusMessage = null;", restore.Replace("\r\n", "\n"));
        Assert.Contains("CurrentCall = restored;\n            StatusMessage = null;", restore.Replace("\r\n", "\n"));
        Assert.DoesNotContain("connection.Reconnected +=", source);
    }

    [Fact]
    public void CommunityVoiceRecoveryKeepsHealthyMediaIndependent()
    {
        var source = Source("Iridium.Client.Core", "CommunityVoiceSession.cs");
        var recovery = Slice(source, "private async Task RecoverSignalingAsync", "private void EnsureMediaEvents");

        Assert.Contains("CommunityVoiceHubContract.Join", recovery);
        Assert.Contains("if (!_mediaConnected)", recovery);
        Assert.Contains("await media.ConnectAsync", recovery);
        Assert.DoesNotContain("connection.Reconnected +=", source);
    }

    [Fact]
    public void MobileViewportFocusPolicyAndCleanupAreExplicit()
    {
        var source = Source("Iridium.UI", "wwwroot", "js", "mobileConversationSwipe.js");

        Assert.Contains("shouldSuppressMobileSafeBottom", source);
        Assert.Contains("composerFocused = true", source);
        Assert.Contains("composerFocused = false", source);
        Assert.Contains("document.addEventListener('focusout', focusout", source);
        Assert.Contains("query,", source);
        Assert.Contains("layoutChanged,", source);
        Assert.Contains("binding.query.removeEventListener('change', binding.layoutChanged)", source);
        Assert.Contains("document.removeEventListener('focusout', binding.focusout)", source);
        Assert.DoesNotContain("keyboardInset > 80", source);
    }

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from);
        return source[from..to];
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));
}
