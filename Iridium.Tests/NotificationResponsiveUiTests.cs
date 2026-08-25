using Iridium.Protocol;
using Iridium.UI;

namespace Iridium.Tests;

public sealed class NotificationResponsiveUiTests
{
    [Fact]
    public void MentionHighlightTargetsOnlyCurrentAccountAndRespondsToEdits()
    {
        var current = Guid.NewGuid();
        var other = Guid.NewGuid();
        var message = Message([new(CommunityMentionKind.Account, current, 0, 5, "@me")]);

        Assert.True(CommunityMentionPresentation.IsTargetedAt(message, current));
        Assert.False(CommunityMentionPresentation.IsTargetedAt(message, other));
        Assert.False(CommunityMentionPresentation.IsTargetedAt(message with { Mentions = [] }, current));
        Assert.True(CommunityMentionPresentation.IsTargetedAt(message with
        {
            Mentions = [new(CommunityMentionKind.Account, current, 0, 5, "@me")]
        }, current));
    }

    [Fact]
    public void SelfTargetingHighlightsButNeverDeliversNotification()
    {
        var author = Guid.NewGuid();
        var other = Guid.NewGuid();
        var everyone = Message([new(CommunityMentionKind.Everyone, null, 0, 9, "@everyone")]) with
        {
            Author = new(author, "author", "Author")
        };
        var self = everyone with
        {
            Mentions = [new(CommunityMentionKind.Account, author, 0, 7, "@Author")]
        };

        Assert.True(CommunityMentionPresentation.IsTargetedAt(everyone, author));
        Assert.False(CommunityMentionPresentation.ShouldNotify(everyone, author));
        Assert.True(CommunityMentionPresentation.IsTargetedAt(everyone, other));
        Assert.True(CommunityMentionPresentation.ShouldNotify(everyone, other));
        Assert.True(CommunityMentionPresentation.IsTargetedAt(self, author));
        Assert.False(CommunityMentionPresentation.ShouldNotify(self, author));
        Assert.False(CommunityMentionPresentation.ShouldDeliverNotification(author, author));
    }

    [Fact]
    public void MobilePanelsFollowNavigationConversationAndContextFlow()
    {
        var state = new MobilePanelNavigationState();
        Assert.Equal(MobilePanel.Navigation, state.Current);
        state.ShowConversation();
        Assert.Equal(MobilePanel.Conversation, state.Current);
        state.ShowContext();
        Assert.Equal(MobilePanel.Context, state.Current);
        state.CloseContext();
        Assert.Equal(MobilePanel.Conversation, state.Current);
        state.ShowNavigation();
        Assert.Equal(MobilePanel.Navigation, state.Current);
    }

    [Fact]
    public void FaviconAndLayoutUseBoundedMentionVariantsAndMobileOnlyPanels()
    {
        var root = FindRepositoryRoot();
        var favicon = File.ReadAllText(Path.Combine(root, "Iridium.Web", "wwwroot", "js", "faviconNotifications.js"));
        var shell = File.ReadAllText(Path.Combine(root, "Iridium.UI", "ApplicationShell.razor.css"));
        var home = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Pages", "Home.razor"));

        Assert.Contains("count > 9 ? '9+'", favicon);
        Assert.Contains("link.href = originalHref", favicon);
        Assert.Contains("@media (max-width: 860px)", shell);
        Assert.Contains("grid-template-columns: 4.75rem 15.5rem minmax(0, 1fr)", shell);
        Assert.Contains("Session.Communities.Sum", home);
    }

    [Fact]
    public void MobileSwipeUsesDirectionLockInteractiveExclusionsAndSharedBackCallback()
    {
        var root = FindRepositoryRoot();
        var swipe = File.ReadAllText(Path.Combine(root, "Iridium.UI", "wwwroot", "js", "mobileConversationSwipe.js"));
        var shell = File.ReadAllText(Path.Combine(root, "Iridium.UI", "ApplicationShell.razor"));
        var css = File.ReadAllText(Path.Combine(root, "Iridium.UI", "ApplicationShell.razor.css"));

        Assert.Contains("const completionRatio = 0.5", swipe);
        Assert.Contains("const directionDeadZone = 14", swipe);
        Assert.Contains("const dominance = 1.2", swipe);
        Assert.Contains("return 'horizontal'", swipe);
        Assert.Contains("return 'vertical'", swipe);
        Assert.Contains("classifyMobileSwipeDirection(dx, dy)", swipe);
        Assert.Contains("setPointerCapture(event.pointerId)", swipe);
        Assert.Contains("releasePointerCapture(event.pointerId)", swipe);
        Assert.Contains("pointerType !== 'touch'", swipe);
        Assert.Contains("textarea", swipe);
        Assert.Contains("video", swipe);
        Assert.Contains("dx > width * completionRatio", swipe);
        Assert.Contains("Math.min(width, Math.max(0, dx))", swipe);
        Assert.Contains("MobileConversationSwipeBackAsync", shell);
        Assert.Contains("? OnMobileBack.InvokeAsync()", shell);
        Assert.Contains("width:3rem; height:3rem", css);
    }

    [Fact]
    public void MobileDirectMessagesUseOneCompactHeaderWithoutChangingChannelHeaders()
    {
        var root = FindRepositoryRoot();
        var home = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Pages", "Home.razor"));
        var shell = File.ReadAllText(Path.Combine(root, "Iridium.UI", "ApplicationShell.razor"));
        var shellCss = File.ReadAllText(Path.Combine(root, "Iridium.UI", "ApplicationShell.razor.css"));
        var direct = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "DirectMessageView.razor"));
        var directCss = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "DirectMessageView.razor.css"));

        Assert.Contains("UseCompactMobileConversationHeader=\"@IsDirectConversationSelected\"", home);
        Assert.Contains("<MobileConversationIdentity>", home);
        Assert.Contains("<ProfileAvatar", home);
        Assert.Contains("@onclick=\"StartMobileDirectVoiceCallAsync\"", home);
        Assert.Contains("MobileConversationIdentity is not null", shell);
        Assert.Contains("grid-template-columns:3.1rem minmax(0,1fr) 2.75rem 2.75rem", shellCss);
        Assert.Contains("min-width:0", shellCss);
        Assert.Contains("text-overflow:ellipsis", shellCss);
        Assert.Contains("@media(max-width:860px)", directCss);
        Assert.Contains(".dm-header{display:none}", directCss);
        Assert.Contains("public async Task StartVoiceCallAsync()", direct);
        Assert.Contains("else\n            {\n                <strong title=\"@MobileConversationTitle\">", shell.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ComposerUsesCanonicalSendabilityAndMatchingSourceGeometryAtEveryWidth()
    {
        var root = FindRepositoryRoot();
        var composer = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageComposer.razor"));
        var css = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageComposer.razor.css"));
        var shell = File.ReadAllText(Path.Combine(root, "Iridium.UI", "ApplicationShell.razor.css"));
        var swipe = File.ReadAllText(Path.Combine(root, "Iridium.UI", "wwwroot", "js", "mobileConversationSwipe.js"));

        Assert.Contains("private bool HasSendableText => !string.IsNullOrWhiteSpace(_content)", composer);
        Assert.Contains("@onclick=\"SubmitFromKeyboardAsync\"", composer);
        Assert.Contains(".mobile-send-button{display:none}", css);
        Assert.Contains("@media (max-width:860px)", css);
        Assert.Contains("composer-highlight composer-text-geometry", composer);
        Assert.Contains("composer-rich-editor composer-text-geometry", composer);
        Assert.Contains(".composer-text-geometry{box-sizing:border-box;min-width:0;width:100%", css);
        Assert.Contains("overflow-wrap:anywhere;word-break:normal;tab-size:4;scrollbar-gutter:stable", css);
        Assert.Contains(".composer-editor .composer-highlight{position:absolute;z-index:1;inset:0", css);
        Assert.Contains("--composer-text-end-padding:4rem", css);
        Assert.Contains("--composer-text-end-padding:0", css);
        Assert.DoesNotContain("inset:0 1rem 0 0", css);
        Assert.DoesNotContain("overflow-wrap:break-word", css);
        Assert.DoesNotContain(".composer-input-row .composer-rich-editor { padding-right", css);
        Assert.Contains("--iridium-mobile-viewport-height", shell);
        Assert.Contains("window.visualViewport", swipe);
        Assert.Contains("keyboardInset > 80 ? '0px'", swipe);
        Assert.Contains(".attachment-button{align-self:end;margin:0 0 calc((var(--composer-min-height) - 2.25rem)/2)}", css);
        Assert.Contains(".emoji-button{grid-column:3;align-self:end;margin:0 .2rem calc((var(--composer-min-height) - 2.3rem)/2) 0}", css);
        Assert.Contains("top:auto;bottom:calc((var(--composer-min-height) - 2.35rem)/2 + 2.45rem)", css);
    }

    private static ChannelMessageDto Message(IReadOnlyList<CommunityMentionDto> mentions) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        new(Guid.NewGuid(), "author", "Author"), "hello", DateTimeOffset.UtcNow, null, false, null, mentions);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Iridium.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate Iridium.sln.");
    }
}
