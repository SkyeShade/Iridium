namespace Iridium.Tests;

public sealed class ConversationNavigationUiContractTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void SuccessfulFriendAcceptanceOpensTheCapturedAccountAfterAcceptance()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var accept = Slice(home, "private async Task AcceptFriendRequestAsync", "private async Task RemoveFriendshipAsync");

        var serverAccept = accept.IndexOf("await Session.AcceptFriendRequestAsync(friendshipId)", StringComparison.Ordinal);
        var openDirect = accept.IndexOf("await OpenDirectMessageAsync(acceptedAccountId)", StringComparison.Ordinal);
        Assert.True(serverAccept >= 0 && openDirect > serverAccept);
        Assert.Contains("friend.Status == FriendshipStatus.Pending && !friend.IsOutgoing", accept);
        Assert.Contains("friend.Status == FriendshipStatus.Accepted", accept);
        Assert.Contains("catch (Exception exception)", accept);
    }

    [Fact]
    public void ExplicitTextChannelAndDirectNavigationRequestTargetScopedFocus()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var channel = Slice(home, "private async Task SelectChannelFromNavigationAsync", "private async Task OpenCommunitySearchResultAsync");
        var direct = Slice(home, "private async Task SelectDirectConversationFromNavigationAsync", "private void RequestChannelComposerFocus");

        Assert.Contains("channel?.Kind == CommunityChannelKind.Text", channel);
        Assert.Contains("RequestChannelComposerFocus(channel.Id)", channel);
        Assert.Contains("RequestDirectComposerFocus(conversation.Id)", direct);
        Assert.DoesNotContain("RequestChannelComposerFocus", Slice(home,
            "private async Task SelectChannelAsync", "private async Task SelectChannelFromNavigationAsync"));
        Assert.DoesNotContain("RequestDirectComposerFocus", Slice(home,
            "private async Task SelectDirectConversationAsync", "private async Task SelectDirectConversationFromNavigationAsync"));
    }

    [Fact]
    public void ExplicitServerNavigationFocusesOnlyItsRestoredTextChannel()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var community = Slice(home, "private async Task SelectCommunityFromNavigationAsync", "private async Task SelectChannelAsync");

        Assert.Contains("OnCommunitySelected=\"SelectCommunityFromNavigationAsync\"", home);
        Assert.Contains("await SelectCommunity(community)", community);
        Assert.Contains("_selectedChannel?.Kind == CommunityChannelKind.Text", community);
        Assert.Contains("RequestChannelComposerFocus(_selectedChannel.Id)", community);
        Assert.DoesNotContain("RequestChannelComposerFocus", Slice(home,
            "private async Task SelectCommunity(CommunityDto? community)", "private async Task SelectCommunityFromNavigationAsync"));
    }

    [Theory]
    [InlineData("ChannelView.razor")]
    [InlineData("DirectMessageView.razor")]
    public void ConversationViewConsumesFocusOnceAfterLoadingAndRender(string file)
    {
        var source = Source("Iridium.Web", "Components", file);
        Assert.Contains("OnAfterRenderAsync", source);
        Assert.Contains("if (!_focusAfterRender || _loading) return", source);
        Assert.Contains("_focusAfterRender = false", source);
        Assert.Contains("await _composer.FocusAsync()", source);
        Assert.Contains("await OnFocusConsumed.InvokeAsync(FocusRequest)", source);
    }

    [Fact]
    public void FriendBadgesAreFedByOneRoutedSessionCount()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        Assert.Contains("Session.IncomingFriendRequestCount", home);
        Assert.Contains("FriendRequestNotifications.Route", home);
        Assert.Contains("FriendRequestNotificationLocation.Home", home);
        Assert.Contains("FriendRequestNotificationLocation.Friends", home);
        Assert.Contains("FriendRequestNotificationLocation.Pending", home);
        Assert.Contains("outsideHome: _selectedCommunity is not null", home);
        Assert.DoesNotContain("outsideHome: _selectedCommunity is not null || _homeView == \"dm\"", home);
    }

    [Fact]
    public void FriendBadgesUseCompactSharedSizingAndAnchoredPlacement()
    {
        var badge = Source("Iridium.UI", "FriendRequestBadge.razor.css");
        var rail = Source("Iridium.UI", "CommunityRail.razor.css");
        var sidebar = Source("Iridium.UI", "HomeSidebar.razor.css");
        var friends = Source("Iridium.Web", "Components", "FriendsView.razor.css");

        Assert.Contains("min-width:1.2rem", badge);
        Assert.Contains("height:1.2rem", badge);
        Assert.Contains("font-size:.55rem", badge);
        Assert.Contains("border:2px", badge);
        Assert.Contains(".rail-item.home ::deep .friend-request-badge", rail);
        Assert.Contains(".home-link ::deep .friend-request-badge", sidebar);
        Assert.Contains(".tab ::deep .friend-request-badge{top:-.3rem;right:-.3rem}", friends);
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
