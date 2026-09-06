namespace Iridium.Tests;

using Iridium.Client.Core;

public sealed class CommunityMemberListUiContractTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void CommunityHeaderPlacesAccessibleMemberToggleImmediatelyBeforeSearch()
    {
        var sidebar = Source("Iridium.Web", "Components", "CommunityMemberSidebar.razor");
        var styles = Source("Iridium.Web", "Components", "CommunityMemberSidebar.razor.css");

        var toggle = sidebar.IndexOf("class=\"member-list-toggle ", StringComparison.Ordinal);
        var search = sidebar.IndexOf("<MessageSearch", StringComparison.Ordinal);
        Assert.True(toggle >= 0 && search > toggle);
        Assert.Contains("MemberListVisible ? \"Hide member list\" : \"Show member list\"", sidebar);
        Assert.Contains("aria-label=\"@MemberListToggleLabel\"", sidebar);
        Assert.Contains("aria-pressed=\"@MemberListVisible\"", sidebar);
        Assert.Contains("<Icon Name=\"friends\"", sidebar);
        Assert.Contains(".member-list-toggle.active", styles);
        Assert.Contains("min-width:0;flex:1;width:auto", styles);
        Assert.Contains("@media (max-width:1180px){.member-list-toggle{display:none}", styles);
    }

    [Fact]
    public void HiddenCommunityMembersExpandConversationWithoutRemovingMobileContext()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var shell = Source("Iridium.UI", "ApplicationShell.razor");
        var styles = Source("Iridium.UI", "ApplicationShell.razor.css");

        Assert.Contains("ShowRightSidebar=\"@HasContextSidebar\"", home);
        Assert.Contains("ShowDesktopRightSidebar=\"@(!HasCommunityContextSidebar || _communityMemberListVisible)\"", home);
        Assert.Contains("ShowRightSidebar && ShowDesktopRightSidebar ? \"has-context-sidebar\"", shell);
        Assert.Contains("desktop-context-hidden", shell);
        Assert.Contains("@media (min-width:1181px){.desktop-context-hidden .context-sidebar{position:fixed", styles);
        Assert.Contains("width:16.2rem;height:3.45rem", styles);
        Assert.Contains(".desktop-context-hidden .context-sidebar ::deep .member-groups{display:none}", styles);
        Assert.Contains(".app-shell.has-context-sidebar { grid-template-columns:", styles);
        Assert.Contains(".context-sidebar { position:fixed;", styles);
        Assert.Contains(".mobile-context .context-sidebar { transform:none;", styles);
    }

    [Fact]
    public void MemberVisibilityIsLocalAndScopedByNodeAndAccount()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var storage = Source("Iridium.Web", "Services", "BrowserClientStorage.cs");

        Assert.Contains("new CommunityMemberListPreferenceScope(Session.SelectedNode.PublicAuthority, Session.Account.Id)", home);
        Assert.Contains("MemberListPreferences.LoadAsync(scope)", home);
        Assert.Contains("MemberListPreferences.SaveAsync(scope, visible)", home);
        Assert.Contains("stored ?? true", home);
        Assert.Contains("iridium.community-member-list-visible.v1:", storage);
        Assert.Contains("Uri.EscapeDataString(scope.NodeAuthority)", storage);
        Assert.Contains("scope.AccountId:N", storage);
    }

    [Fact]
    public void PreferenceScopeSeparatesNodesAndAccounts()
    {
        var firstAccount = Guid.NewGuid();
        var secondAccount = Guid.NewGuid();
        var values = new Dictionary<CommunityMemberListPreferenceScope, bool>
        {
            [new("node-a", firstAccount)] = false
        };

        Assert.False(values[new("node-a", firstAccount)]);
        Assert.False(values.ContainsKey(new("node-a", secondAccount)));
        Assert.False(values.ContainsKey(new("node-b", firstAccount)));
    }

    [Fact]
    public void ToggleIsOnlyProvidedForCommunityContext()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var predicate = Slice(home, "private bool HasCommunityContextSidebar", "private bool IsDirectConversationSelected");

        Assert.Contains("_selectedCommunity is not null", predicate);
        Assert.Contains("_selectedChannel is not null", predicate);
        Assert.Contains("CommunityState.Management is not null", predicate);
        Assert.DoesNotContain("_homeView == \"dm\"", predicate);
        Assert.Contains("MemberListVisible=\"_communityMemberListVisible\"", home);
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Missing start marker: {start}");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"Missing end marker: {end}");
        return source[from..to];
    }
}
