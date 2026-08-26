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
        var channel = Slice(home, "private Task SelectChannelFromNavigationAsync", "private async Task CompleteChannelSelectionAsync");
        var direct = Slice(home, "private Task SelectDirectConversationFromNavigationAsync", "private bool IsCurrentCommunityNavigation");

        Assert.Contains("requestComposerFocus: !_isMobileLayout", channel);
        Assert.Contains("requestComposerFocus: !_isMobileLayout", direct);
        Assert.Contains("if (channel?.Kind == CommunityChannelKind.Text && requestComposerFocus)", home);
        Assert.Contains("if (requestComposerFocus) RequestDirectComposerFocus(conversation.Id)", home);
    }

    [Fact]
    public void ExplicitServerNavigationFocusesOnlyItsRestoredTextChannel()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var community = Slice(home, "private async Task SelectCommunityFromNavigationAsync", "private async Task SelectChannelAsync");

        Assert.Contains("OnCommunitySelected=\"SelectCommunityFromNavigationAsync\"", home);
        Assert.Contains("await SelectCommunity(community, focusRestoredChannel: true)", community);
        Assert.Contains("SelectChannelAsync(channel ?? CommunityState.FirstOrderedChannel(), focusRestoredChannel)", home);
        Assert.DoesNotContain("RequestChannelComposerFocus", Slice(home,
            "private async Task SelectCommunity(CommunityDto? community, bool focusRestoredChannel = false)",
            "private async Task SelectCommunityFromNavigationAsync"));
    }

    [Theory]
    [InlineData("ChannelView.razor")]
    [InlineData("DirectMessageView.razor")]
    public void ConversationViewConsumesFocusOnceAfterLoadingAndRender(string file)
    {
        var source = Source("Iridium.Web", "Components", file);
        Assert.Contains("OnAfterRenderAsync", source);
        Assert.Contains("if (!_focusAfterRender) return", source);
        Assert.DoesNotContain("if (!_focusAfterRender || _loading) return", source);
        Assert.Contains("_focusAfterRender = false", source);
        Assert.Contains("await _composer.FocusAsync()", source);
        Assert.Contains("await OnFocusConsumed.InvokeAsync(FocusRequest)", source);
    }

    [Theory]
    [InlineData("ChannelView.razor", "Messaging.OpenChannelAsync")]
    [InlineData("DirectMessageView.razor", "Messaging.OpenDirectConversationAsync")]
    public void HistoryHydrationIsTrackedButNotAwaitedByConversationRendering(string file, string openCall)
    {
        var source = Source("Iridium.Web", "Components", file);
        Assert.Contains("protected override void OnParametersSet()", source);
        Assert.Contains("_ = HydrateHistoryAsync", source);
        Assert.Contains(openCall, source);
        Assert.Contains("IsCurrentHistoryLoad", source);
        Assert.DoesNotContain("Busy=\"@(_sending || _loading)\"", source);
        Assert.DoesNotContain("if (_sending || _loading)", source);
    }

    [Fact]
    public void ChannelAndDirectSelectionRenderBeforeBackgroundNavigationWork()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var channel = Slice(home, "private async Task SelectChannelAsync", "private Task SelectChannelFromNavigationAsync");
        var direct = Slice(home, "private async Task SelectDirectConversationAsync", "private Task SelectDirectConversationFromNavigationAsync");

        Assert.True(channel.IndexOf("_selectedChannel = channel", StringComparison.Ordinal) <
                    channel.IndexOf("await InvokeAsync(StateHasChanged)", StringComparison.Ordinal));
        Assert.True(channel.IndexOf("await InvokeAsync(StateHasChanged)", StringComparison.Ordinal) <
                    channel.IndexOf("TrackBackground(CompleteChannelSelectionAsync", StringComparison.Ordinal));
        Assert.True(direct.IndexOf("_selectedDirectConversation = conversation", StringComparison.Ordinal) <
                    direct.IndexOf("await InvokeAsync(StateHasChanged)", StringComparison.Ordinal));
        Assert.Contains("TrackBackground(Messaging.ClearAsync()", home);
        Assert.Contains("ObserveBackgroundAsync", home);
    }

    [Fact]
    public void ConversationViewsNeverBindTheSharedListWithoutMatchingItsIdentity()
    {
        var channel = Source("Iridium.Web", "Components", "ChannelView.razor");
        var direct = Source("Iridium.Web", "Components", "DirectMessageView.razor");

        Assert.Contains("Messaging.MessagesFor(Community.Id, Channel.Id)", channel);
        Assert.DoesNotContain("Messages=\"Messaging.Messages\"", channel);
        Assert.Contains("Messaging.DirectMessagesFor(Conversation.Id)", direct);
        Assert.DoesNotContain("Messaging.DirectMessages.Select", direct);
        Assert.Contains("<MessageList @key=\"Channel.Id\"", channel);
        Assert.Contains("<MessageList @key=\"Conversation.Id\"", direct);
    }

    [Fact]
    public void ConversationSessionAttachesScopedHotStateBeforeAwaitingOldCleanupAndRejectsLateLoads()
    {
        var session = Source("Iridium.Client.Core", "ChannelMessagingSession.cs");
        var channel = Slice(session, "public async Task OpenChannelAsync", "public async Task OpenDirectConversationAsync");
        var direct = Slice(session, "public async Task OpenDirectConversationAsync", "public Task LoadOlderAsync");

        Assert.True(channel.IndexOf("AttachChannelState", StringComparison.Ordinal) <
                    channel.IndexOf("await LeaveChannelAsync", StringComparison.Ordinal));
        Assert.True(direct.IndexOf("AttachDirectState", StringComparison.Ordinal) <
                    direct.IndexOf("await LeaveChannelAsync", StringComparison.Ordinal));
        Assert.Contains("Interlocked.Increment(ref _conversationLoadGeneration)", channel);
        Assert.Contains("IsCurrentChannelLoad(generation, scope)", channel);
        Assert.Contains("IsCurrentDirectLoad(generation, scope)", direct);
        Assert.Contains("_channelHotStates", session);
        Assert.Contains("_directHotStates", session);
        Assert.Contains("HotConversationLimit = 8", session);
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

    [Fact]
    public void MobileHomeAndServerNavigationShareBoundedScrollableCenterAndPersistentFooters()
    {
        var shell = Source("Iridium.UI", "ApplicationShell.razor");
        var shellStyles = Source("Iridium.UI", "ApplicationShell.razor.css");
        var homeStyles = Source("Iridium.UI", "HomeSidebar.razor.css");
        var rail = Source("Iridium.UI", "CommunityRail.razor");

        Assert.Equal(1, shell.Split("<ProfilePanel", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, rail.Split("class=\"rail-item add\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("grid-template-rows:minmax(0,1fr)", shellStyles);
        Assert.Contains(".secondary-sidebar { grid-column:2; min-width:0; min-height:0; overflow:hidden; }", shellStyles);
        Assert.Contains(".sidebar-body { overflow:hidden; }", shellStyles);
        Assert.Contains(".home-navigation{height:100%;min-height:0;display:flex;flex-direction:column;overflow:hidden}", homeStyles);
        Assert.Contains(".dm-list{flex:1;min-height:0;padding-bottom:var(--bottom-control-inset)}", homeStyles);
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
