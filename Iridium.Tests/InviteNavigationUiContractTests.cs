namespace Iridium.Tests;

public sealed class InviteNavigationUiContractTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void SuccessfulJoinAndAlreadyMemberOpenUseCanonicalServerNavigation()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var invite = Source("Iridium.Web", "Components", "InviteEmbed.razor");
        var action = Slice(invite, "private async Task JoinAsync()", "private static string JoinLabel");
        var navigation = Slice(home, "private async Task OpenCommunityFromInviteAsync", "private async Task SelectChannelAsync");

        Assert.Contains("AlreadyMember ? \"Open Server\" : \"Join Server\"", invite);
        Assert.Contains("OnCommunityOpened=\"OpenCommunityFromInviteAsync\"", home);
        Assert.True(action.IndexOf("await Resolver.JoinAsync(_reference)", StringComparison.Ordinal) <
                    action.IndexOf("await OnCommunityOpened.InvokeAsync(result.Community)", StringComparison.Ordinal));
        Assert.Contains("await SelectCommunityFromNavigationAsync(community)", navigation);
        Assert.Contains("Navigation.NavigateTo(Navigation.BaseUri, replace: true)", navigation);
    }

    [Fact]
    public void InviteRouteIsClearedOnlyAfterAuthoritativeJoinSucceeds()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var invite = Source("Iridium.Web", "Components", "InviteEmbed.razor");
        var action = Slice(invite, "private async Task JoinAsync()", "private static string JoinLabel");
        var navigation = Slice(home, "private async Task OpenCommunityFromInviteAsync", "private async Task SelectChannelAsync");

        var join = action.IndexOf("await Resolver.JoinAsync(_reference)", StringComparison.Ordinal);
        var callback = action.IndexOf("await OnCommunityOpened.InvokeAsync(result.Community)", StringComparison.Ordinal);
        var catchBlock = action.IndexOf("catch (Exception exception)", StringComparison.Ordinal);
        Assert.True(join >= 0 && callback > join && catchBlock > callback);

        var select = navigation.IndexOf("await SelectCommunityFromNavigationAsync(community)", StringComparison.Ordinal);
        var clear = navigation.IndexOf("InviteToken = null", StringComparison.Ordinal);
        var replace = navigation.IndexOf("Navigation.NavigateTo(Navigation.BaseUri, replace: true)", StringComparison.Ordinal);
        Assert.True(select >= 0 && clear > select && replace > clear);
        Assert.DoesNotContain("forceLoad: true", navigation);
    }

    [Fact]
    public void LoggedOutInviteIntentSurvivesAuthenticationAndRouteJoinDoesNotDoubleOpen()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var authentication = Slice(home, "private async Task AuthenticateAsync", "private async Task<bool> SaveProfileAsync");
        var joined = Slice(home, "private void OnCommunityJoined", "private void OnCommunityStateChanged");

        Assert.Contains("@page \"/invite/{InviteToken}\"", home);
        Assert.Contains("else if (_authenticationVisible || !Session.IsAuthenticated)", home);
        Assert.DoesNotContain("NavigateTo", authentication);
        Assert.Contains("if (!string.IsNullOrWhiteSpace(InviteToken)) return", joined);
        Assert.Contains("_ = InvokeAsync(() => SelectCommunity(community))", joined);
    }

    [Fact]
    public void InviteActionPreventsRepeatedOpenWhileRequestIsInFlight()
    {
        var invite = Source("Iridium.Web", "Components", "InviteEmbed.razor");
        var action = Slice(invite, "private async Task JoinAsync()", "private static string JoinLabel");

        Assert.Contains("if (_reference is null || _joining) return", action);
        Assert.Contains("_joining = true", action);
        Assert.Contains("finally { _joining = false; }", action);
        Assert.Contains("disabled=\"@_joining\"", invite);
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find start marker '{startMarker}'.");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find end marker '{endMarker}'.");
        return source[start..end];
    }
}
