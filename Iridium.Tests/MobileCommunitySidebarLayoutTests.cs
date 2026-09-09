using System.Runtime.CompilerServices;

namespace Iridium.Tests;

public sealed class MobileCommunitySidebarLayoutTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void MobileChannelRowsKeepIconNameDisclosureAndActionsOnOneLine()
    {
        var styles = Source("Iridium.UI", "ChannelRow.razor.css");
        var mobile = Slice(styles, "@media(max-width:860px)");

        Assert.Contains(".channel-row{flex-direction:row;flex-wrap:nowrap;align-items:center;min-width:0", mobile);
        Assert.Contains(".channel-nav-surface{min-width:0;min-height:0;display:flex;flex-direction:row;flex-wrap:nowrap;align-items:center;flex:1 1 0", mobile);
        Assert.Contains(".channel-name,.channel-name.has-disclosure{min-width:0;flex:1 1 auto;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}", mobile);
        Assert.Contains(".channel-symbol,.private-lock,.channel-disclosure,.channel-settings{flex:0 0 auto}", mobile);
        Assert.Contains(".channel-mention-badge{flex:0 0 auto}", mobile);
        Assert.Contains("padding:.28rem .4rem .28rem", mobile);
        Assert.Contains(".channel-row.selected{background:var(--surface-active)}", mobile);
        Assert.DoesNotContain("overflow:visible", mobile);
    }

    [Fact]
    public void MobileForumAndThreadChildrenShareTheSameNoWrapEllipsisContract()
    {
        var styles = Source("Iridium.Web", "Components", "SidebarChildConversationList.razor.css");
        var mobile = Slice(styles, "@media(max-width:860px)");
        var forumStyles = Source("Iridium.Web", "Components", "ForumPostSidebarChildren.razor.css");

        Assert.Contains(".sidebar-child-row{flex-direction:row;flex-wrap:nowrap;align-items:center;min-width:0}", mobile);
        Assert.Contains(".sidebar-child-nav-surface{min-width:0;display:flex;flex-direction:row;flex-wrap:nowrap;align-items:center;flex:1 1 0;overflow:hidden}", mobile);
        Assert.Contains(".sidebar-child-title{min-width:0;flex:1 1 auto;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}", mobile);
        Assert.Contains(".sidebar-child-nav-surface ::deep svg,.sidebar-child-mentions{flex:0 0 auto}", mobile);
        Assert.DoesNotContain(".sidebar-child-conversations{display:none}", mobile);
        Assert.DoesNotContain("@media(max-width:860px)", forumStyles);
    }

    [Fact]
    public void DesktopSidebarSurfaceAndChannelVisualRulesRemainScopedAndUnchanged()
    {
        var shared = Source("Iridium.Web", "wwwroot", "css", "app.css");
        var channelStyles = Source("Iridium.UI", "ChannelRow.razor.css");
        var desktop = Slice(shared, "@media (min-width:861px)");
        var beforeMobile = channelStyles[..channelStyles.IndexOf("@media(max-width:860px)", StringComparison.Ordinal)];

        Assert.Contains(".sidebar-nav-row > .sidebar-nav-surface", desktop);
        Assert.Contains("width: auto", desktop);
        Assert.Contains("display: flex", desktop);
        Assert.Contains("flex: 1 1 0", desktop);
        Assert.Contains(".sidebar-nav-row.is-active > .sidebar-nav-surface", desktop);
        Assert.Contains(".channel-name.has-disclosure { flex: 0 1 auto; }", beforeMobile);
        Assert.Contains(".channel-row:hover .channel-settings, .channel-row.selected .channel-settings", beforeMobile);
    }

    private static string Slice(string source, string start)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find {start}.");
        return source[startIndex..];
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));
    private static string FindRoot([CallerFilePath] string sourceFile = "") =>
        Directory.GetParent(Path.GetDirectoryName(sourceFile)!)!.FullName;
}
