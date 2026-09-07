using System.Runtime.CompilerServices;

namespace Iridium.Tests;

public sealed class ThreadUiPolishTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void ThreadsLivesInTheHeaderAsASecondaryIconAndUsesAnchoredPopover()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var homeCss = Source("Iridium.Web", "Pages", "Home.razor.css");
        var browser = Source("Iridium.Web", "Components", "ThreadBrowser.razor");
        var browserCss = Source("Iridium.Web", "Components", "ThreadBrowser.razor.css");

        Assert.Contains("class=\"channel-header-actions\"", home);
        Assert.Contains("class=\"channel-header-action\"", home);
        Assert.Contains("title=\"Threads\"", home);
        Assert.Contains("aria-label=\"Threads\"", home);
        Assert.Contains("<Icon Name=\"thread\"", home);
        Assert.DoesNotContain("<Icon Name=\"message\" /> Threads", home);
        Assert.Contains("Anchor=\"_threadBrowserAnchor\"", home);
        Assert.Contains("wireAnchoredPopup", browser);
        Assert.Contains("DismissAsync", browser);
        Assert.Contains("Task.Delay(250", browser);
        Assert.Contains("No threads yet.", browser);
        Assert.Contains("position:fixed", browserCss);
        Assert.Contains("visibility:hidden", browserCss);
        Assert.Contains("margin-left:auto", homeCss);
    }

    [Fact]
    public void RightClickAndMoreShareTheCanonicalPermissionAwareMessageMenu()
    {
        var row = Source("Iridium.Web", "Components", "MessageRow.razor");
        var css = Source("Iridium.Web", "Components", "MessageRow.razor.css");
        var script = Source("Iridium.Web", "wwwroot", "js", "emojiPicker.js");

        Assert.Contains("@oncontextmenu=\"OpenMessageContextMenuAsync\"", row);
        Assert.Contains("@oncontextmenu:preventDefault=\"true\"", row);
        Assert.Contains("@onclick=\"ToggleMenu\"", row);
        Assert.Equal(1, Occurrences(row, "class=\"message-menu\""));
        Assert.Contains("preferences.UsageHistory", row);
        Assert.Contains("IsMobileLayout ? 8 : 4", row);
        Assert.Contains("Glyph=\"@standard.Glyph\"", row);
        Assert.DoesNotContain("Glyph=\"standard.Glyph\"", row);
        Assert.Contains("CanUseExternalEmoji || value.Community.Id == Message.CommunityId", row);
        Assert.Contains("QuickReactionAsync", row);
        Assert.Contains("OpenReactionPickerFromMenu", row);
        Assert.Contains("> Reply</button>", row);
        Assert.Contains("> Create Thread</button>", row);
        Assert.Contains("> Forward</button>", row);
        Assert.Contains("Edit Message", row);
        Assert.Contains("Copy Text", row);
        Assert.Contains("Mark Unread", row);
        Assert.Contains("class=\"destructive\"", row);
        Assert.Contains("CanCreateThread && Message.Thread is null && IsThreadable", row);
        Assert.Contains("Open Thread", row);
        Assert.Contains("overflow-x:hidden", css);
        Assert.Contains("position: fixed", css);
        Assert.Contains("visibility:hidden", css);
        Assert.Contains("wirePointerPopup", script);
        Assert.Contains("if (x + rect.width > window.innerWidth - margin)", script);
        Assert.Contains("if (y + rect.height > window.innerHeight - margin)", script);
    }

    [Fact]
    public void MobileLongPressUsesTheSameActionsAndQuickReactionSource()
    {
        var row = Source("Iridium.Web", "Components", "MessageRow.razor");
        var sheet = Source("Iridium.Web", "Components", "MobileMessageActionSheet.razor");
        var list = Source("Iridium.Web", "Components", "MessageList.razor");

        Assert.Contains("QuickReactions=\"_quickReactions\"", row);
        Assert.Contains("OnQuickReaction=\"QuickReactionAsync\"", row);
        Assert.Contains("QuickReactions.Take(8)", sheet);
        Assert.Contains("Glyph=\"@standard.Glyph\"", sheet);
        Assert.DoesNotContain("Glyph=\"standard.Glyph\"", sheet);
        Assert.Contains("Create Thread", sheet);
        Assert.Contains("Copy Text", sheet);
        Assert.Contains("Mark Unread", sheet);
        Assert.Contains("OpenMessageActionsFromLongPressAsync", list);
        Assert.Contains("message.Kind != MessageKind.User || message.IsDeleted", list);
    }

    [Fact]
    public void ThreadCreationUsesImmediateValidationAndTheSharedPrivateSwitch()
    {
        var browser = Source("Iridium.Web", "Components", "ThreadBrowser.razor");

        Assert.Contains("@oninput=\"NameChanged\"", browser);
        Assert.Contains("private bool CanSubmit", browser);
        Assert.Contains("_name.Trim()", browser);
        Assert.Contains("<SettingsSwitch", browser);
        Assert.Contains("Private Thread", browser);
        Assert.Contains("Only invited members can view this thread.", browser);
        Assert.Contains("disabled=\"@(!CanSubmit)\"", browser);
    }

    [Fact]
    public void ForumPostsAndThreadsUseOneNestedConversationRenderer()
    {
        var forum = Source("Iridium.Web", "Components", "ForumPostSidebarChildren.razor");
        var threads = Source("Iridium.Web", "Components", "ThreadSidebarChildren.razor");
        var shared = Source("Iridium.Web", "Components", "SidebarChildConversationList.razor");
        var sharedCss = Source("Iridium.Web", "Components", "SidebarChildConversationList.razor.css");

        Assert.Contains("<SidebarChildConversationList", forum);
        Assert.Contains("<SidebarChildConversationList", threads);
        Assert.Contains("sidebar-child-tree", shared);
        Assert.Contains("sidebar-child-branch", shared);
        Assert.Contains("sidebar-nav-surface sidebar-child-nav-surface", shared);
        Assert.Contains("selected is-active", shared);
        Assert.Contains(".sidebar-child-row:hover", sharedCss);
        Assert.Contains(".sidebar-child-tree::before", sharedCss);
    }

    [Fact]
    public void ThreadBackUsesCanonicalChannelNavigationAndGuardsTheDepartedRoute()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var closeStart = home.IndexOf("private async Task CloseThreadAsync()", StringComparison.Ordinal);
        var closeEnd = home.IndexOf("private void ThreadChanged", closeStart, StringComparison.Ordinal);
        var close = home[closeStart..closeEnd];

        Assert.Contains("_dismissedThreadRouteId", home);
        Assert.Contains("await SelectChannelFromNavigationAsync(parent)", close);
        Assert.Contains("Navigation.NavigateTo(Navigation.BaseUri)", close);
        Assert.True(close.IndexOf("SelectChannelFromNavigationAsync", StringComparison.Ordinal) <
                    close.IndexOf("NavigateTo", StringComparison.Ordinal));
        Assert.Contains("_dismissedThreadRouteId != threadId", home);
    }

    [Fact]
    public void SourceMessageSummaryIsBatchLoadedPatchedAndOpensExistingThread()
    {
        var row = Source("Iridium.Web", "Components", "MessageRow.razor");
        var session = Source("Iridium.Client.Core", "ChannelMessagingSession.cs");
        var endpoints = Source("Iridium.Server", "Api", "MessageEndpoints.cs");
        var home = Source("Iridium.Web", "Pages", "Home.razor");

        Assert.Contains("count == 1 ? \"1 Message\" : $\"{count} Messages\"", row);
        Assert.Contains("Open Thread", row);
        Assert.Contains("ApplyThreadSummary", session);
        Assert.Contains("ApplyThreadActivity", session);
        Assert.Contains("RemoveThreadSummary", session);
        Assert.Contains("AttachThreadSummariesAsync", endpoints);
        Assert.Contains("messageIds.Contains", endpoints);
        Assert.Contains("CommunityThreadActivityChanged +=", home);
        Assert.Contains("if (change.IsDeleted)", home);
    }

    private static int Occurrences(string value, string term) =>
        (value.Length - value.Replace(term, string.Empty, StringComparison.Ordinal).Length) / term.Length;
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));
    private static string FindRoot([CallerFilePath] string sourceFile = "") =>
        Directory.GetParent(Path.GetDirectoryName(sourceFile)!)!.FullName;
}
