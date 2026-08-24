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

        Assert.Contains("const threshold = 84", swipe);
        Assert.Contains("const dominance = 1.35", swipe);
        Assert.Contains("pointerType !== 'touch'", swipe);
        Assert.Contains("textarea", swipe);
        Assert.Contains("video", swipe);
        Assert.Contains("dx >= threshold && dx > 0", swipe);
        Assert.Contains("MobileConversationSwipeBackAsync", shell);
        Assert.Contains("? OnMobileBack.InvokeAsync()", shell);
        Assert.Contains("width:3rem; height:3rem", css);
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
