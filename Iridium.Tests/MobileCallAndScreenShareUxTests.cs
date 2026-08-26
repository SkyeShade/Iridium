using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class MobileCallAndScreenShareUxTests
{
    [Fact]
    public void MobileDirectCallPresentationIsLocalAndConversationScoped()
    {
        var state = new Iridium.UI.MobileDirectCallPresentationState();
        var activeConversationId = Guid.NewGuid();
        var otherConversationId = Guid.NewGuid();

        Assert.Equal(Iridium.UI.MobileDirectCallPresentation.Collapsed, state.Current);
        state.Expand(activeConversationId);
        Assert.True(state.IsExpandedFor(activeConversationId));
        Assert.False(state.IsExpandedFor(otherConversationId));
        state.Collapse();
        Assert.Equal(Iridium.UI.MobileDirectCallPresentation.Collapsed, state.Current);
        Assert.Null(state.ConversationId);
    }

    [Fact]
    public void MobileBarShowsThroughoutCurrentCallExceptInFullMediaView()
    {
        var conversationId = Guid.NewGuid();
        Assert.False(MobileDirectCallBarState.ShouldShow(null, false));
        Assert.True(MobileDirectCallBarState.ShouldShow(Call(conversationId, CallState.Ringing), false));
        Assert.True(MobileDirectCallBarState.ShouldShow(Call(conversationId), false));
        Assert.False(MobileDirectCallBarState.ShouldShow(Call(conversationId), true));
        Assert.False(MobileDirectCallBarState.ShouldShow(Call(conversationId, CallState.Ended), false));
    }

    [Theory]
    [InlineData(CallState.Ringing, CallConnectionState.New, MobileDirectCallPhase.Calling, "Calling", false, true)]
    [InlineData(CallState.Active, CallConnectionState.New, MobileDirectCallPhase.Connecting, "Connecting", false, false)]
    [InlineData(CallState.Active, CallConnectionState.Connecting, MobileDirectCallPhase.Connecting, "Connecting", false, false)]
    [InlineData(CallState.Active, CallConnectionState.Connected, MobileDirectCallPhase.Connected, "Connected", true, false)]
    [InlineData(CallState.Active, CallConnectionState.Disconnected, MobileDirectCallPhase.Reconnecting, "Reconnecting", false, false)]
    [InlineData(CallState.Active, CallConnectionState.Failed, MobileDirectCallPhase.Failed, "Unable to connect.", false, false)]
    public void MobileBarProjectsAuthoritativeCallAndMediaState(CallState callState,
        CallConnectionState connectionState, MobileDirectCallPhase phase, string status,
        bool canUseMediaControls, bool isPreAnswer)
    {
        var projection = MobileDirectCallBarState.Project(Call(Guid.NewGuid(), callState), connectionState);

        Assert.NotNull(projection);
        Assert.Equal(phase, projection.Phase);
        Assert.Equal(status, projection.Status);
        Assert.Equal(canUseMediaControls, projection.CanUseMediaControls);
        Assert.Equal(isPreAnswer, projection.IsPreAnswer);
    }

    [Theory]
    [InlineData("DeviceUnsupportedError: getDisplayMedia not supported", ScreenShareFailureKind.Unsupported,
        "Screen sharing is not supported by this browser or device.")]
    [InlineData("NotSupportedError", ScreenShareFailureKind.Unsupported,
        "Screen sharing is not supported by this browser or device.")]
    [InlineData("AbortError: picker cancelled", ScreenShareFailureKind.Cancelled, null)]
    [InlineData("NotAllowedError: Permission denied", ScreenShareFailureKind.PermissionDenied,
        "Permission to share your screen was denied.")]
    [InlineData("TypeError: capture exploded", ScreenShareFailureKind.Unexpected,
        "Unable to start screen sharing.")]
    public void ScreenShareFailuresAreClassifiedWithoutRawUserFacingExceptions(
        string detail, ScreenShareFailureKind expectedKind, string? expectedMessage)
    {
        var exception = new InvalidOperationException(detail);
        Assert.Equal(expectedKind, ScreenShareFailure.Classify(exception));
        Assert.Equal(expectedMessage, ScreenShareFailure.UserMessage(exception));
    }

    [Fact]
    public void MobileBarAndCapabilityDetectionAreShellLevelAndDesktopSafe()
    {
        var root = FindRepositoryRoot();
        var home = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Pages", "Home.razor"));
        var shell = File.ReadAllText(Path.Combine(root, "Iridium.UI", "ApplicationShell.razor"));
        var shellCss = File.ReadAllText(Path.Combine(root, "Iridium.UI", "ApplicationShell.razor.css"));
        var bar = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MobileDirectCallBar.razor"));
        var voicePanel = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "VoiceConnectionPanel.razor"));
        var directStage = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "DirectVoiceCallStage.razor"));
        var directStageCss = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "DirectVoiceCallStage.razor.css"));
        var directView = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "DirectMessageView.razor"));
        var program = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Program.cs"));
        var callService = File.ReadAllText(Path.Combine(root, "Iridium.Client.Core", "CallClientService.cs"));
        var capability = File.ReadAllText(Path.Combine(root, "Iridium.Web", "wwwroot", "js", "screenShareCapability.js"));
        var viewerCss = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "VoiceStreamViewer.razor.css"));

        Assert.Contains("<MobileCallBar>", home);
        Assert.Contains("MobileDirectCallBarState.ShouldShow", home);
        Assert.Contains("ReturnToActiveDirectCallAsync", home);
        Assert.Contains("ReturnToActiveDirectStreamAsync", home);
        Assert.Contains("mobile-call-bar-slot", shell);
        Assert.Contains("grid-template-rows:3rem minmax(0,1fr)", shellCss);
        Assert.Contains(".has-mobile-call-bar .main-content { top:3rem; }", shellCss);
        Assert.Contains("@media (max-width: 860px)", shellCss);
        Assert.Contains("Voice.ToggleMuteAsync", bar);
        Assert.Contains("Voice.ToggleDeafenAsync", bar);
        Assert.Contains("Voice.LeaveCurrentVoiceSessionAsync", bar);
        Assert.Contains("projection.CanUseMediaControls && RelevantStream is not null", bar);
        Assert.Contains("projection.IsPreAnswer ? \"Cancel call\" : \"Hang up\"", bar);
        Assert.Contains("Calls.CanRetry", bar);
        Assert.DoesNotContain("<Icon Name=\"phone\" />", bar);
        Assert.Contains("Voice.WatchedStream ?? Voice.PublishedStreams", bar);
        Assert.Contains("VoiceSessions.WatchedStream is not null && VoiceSessions.ViewerMode == StreamViewerMode.Full", home);
        Assert.Contains("CallState.Ringing or CallState.Active", home);
        Assert.Contains(".dm-call-stage.outgoing:not(.mobile-expanded),.dm-call-stage.active:not(.mobile-expanded){display:none}", directStageCss);
        Assert.DoesNotContain(".dm-call-stage.incoming{display:none}", directStageCss);
        Assert.Contains("else if (CallForConversation is { } call)", directStage);
        Assert.Contains("call.State == CallState.Ringing", directStage);
        Assert.Contains("? VoiceCallHubContract.Cancel : VoiceCallHubContract.HangUp", callService);
        Assert.Contains("session.CanPublishMedia && ScreenShareCapability.IsSupported", voicePanel);
        Assert.Contains("IsSharing || CanStartScreenShare", directStage);
        Assert.Contains("ScreenShareFailure.UserMessage(exception)", directStage);
        Assert.Contains("typeof navigator?.mediaDevices?.getDisplayMedia === \"function\"", capability);
        Assert.Contains(".voice-stream-viewer.floating{display:none}", viewerCss);
        Assert.Contains("object-fit:contain", viewerCss);
        Assert.Contains("orientation:landscape", viewerCss);
        Assert.Contains("min-width:44px;min-height:44px", viewerCss);
        Assert.Contains("AddScoped<MobileDirectCallPresentationState>()", program);
        Assert.Contains("MobileCallPresentation.Expand(conversationId)", home);
        Assert.Contains("!IsExpandedMobileDirectCall", home);
        Assert.Contains("MobileCallExpanded=\"@IsExpandedMobileDirectCall\"", home);
        Assert.Contains("MobileExpanded=\"@(IsMobileLayout && MobileCallExpanded)\"", directView);
        Assert.Contains("class=\"mobile-call-minimize\"", directStage);
        Assert.Contains("AccountId=\"CurrentAccountId\"", directStage);
        Assert.Contains("AccountId=\"Participant.AccountId\"", directStage);
        Assert.Contains("VoiceSessions.ToggleMuteAsync", directStage);
        Assert.Contains("VoiceSessions.ToggleDeafenAsync", directStage);
        Assert.Contains("VoiceSessions.LeaveCurrentVoiceSessionAsync", directStage);
        Assert.Contains("StopWatchingAsync", directStage);
        Assert.Contains("RemoteScreenShare", directStage);
        Assert.Contains("flex:0 1 clamp(12rem,46%,23rem)", directStageCss);
        Assert.Contains("max-height:50%", directStageCss);
    }

    [Fact]
    public void ReturningThroughTheMobileCallBarExpandsWithoutRequestingComposerFocus()
    {
        var root = FindRepositoryRoot();
        var home = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Pages", "Home.razor"));
        var start = home.IndexOf("private async Task ReturnToActiveDirectCallAsync", StringComparison.Ordinal);
        var end = home.IndexOf("private async Task ReturnToActiveDirectStreamAsync", start, StringComparison.Ordinal);
        var method = home[start..end];

        Assert.True(method.IndexOf("MobileCallPresentation.Expand(conversationId)", StringComparison.Ordinal) <
                    method.IndexOf("await SelectDirectConversationAsync(conversation)", StringComparison.Ordinal));
        Assert.Contains("ShowMobileConversation(\"ReturnToActiveCall\")", method);
        Assert.DoesNotContain("RequestDirectComposerFocus", method);
        Assert.DoesNotContain("FocusAsync", method);
    }

    [Fact]
    public void MobileNavigationAndCallEndCollapseExpandedPresentation()
    {
        var root = FindRepositoryRoot();
        var home = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Pages", "Home.razor"));

        Assert.Contains("private void CollapseMobileDirectCall() => MobileCallPresentation.Collapse()", home);
        Assert.Contains("if (MobileCallPresentation.ConversationId != conversation.Id) CollapseMobileDirectCall()", home);
        Assert.Contains("CurrentDirectCallConversationId is not { } activeCallConversationId", home);
        Assert.Contains("MobileCallPresentation.Reset()", home);
        Assert.Contains("OnMobileCallMinimize=\"CollapseMobileDirectCall\"", home);

        foreach (var methodName in new[]
                 {
                     "private async Task SelectCommunity(",
                     "private async Task SelectChannelAsync(",
                     "private void SelectHomeView(",
                     "private async Task HandleMobileBackAsync("
                 })
        {
            var methodStart = home.IndexOf(methodName, StringComparison.Ordinal);
            Assert.True(methodStart >= 0, $"Could not find {methodName}");
            var collapse = home.IndexOf("CollapseMobileDirectCall();", methodStart, StringComparison.Ordinal);
            Assert.True(collapse > methodStart && collapse - methodStart < 600,
                $"{methodName} must collapse the expanded mobile call before navigating away");
        }
    }

    private static CallSessionDto Call(Guid conversationId, CallState state = CallState.Active) => new(
        Guid.NewGuid(), CallKind.DirectVoice, conversationId, Guid.NewGuid(), state,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), [], DateTimeOffset.UtcNow);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Iridium.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
