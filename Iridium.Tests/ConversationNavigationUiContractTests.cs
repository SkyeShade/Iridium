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
    public void ExplicitTextChannelAndDirectNavigationSeparatePanelTransitionFromDesktopFocus()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var channel = Slice(home, "private Task SelectChannelFromNavigationAsync", "private async Task CompleteChannelSelectionAsync");
        var direct = Slice(home, "private Task SelectDirectConversationFromNavigationAsync", "private bool IsCurrentCommunityNavigation");

        Assert.Contains("mobilePanelSource: channel?.Kind is CommunityChannelKind.Text or CommunityChannelKind.Forum", channel);
        Assert.Contains("\"TextChannelClick\"", channel);
        Assert.Contains("requestComposerFocus: ShouldAutoFocusNavigation", channel);
        Assert.Contains("mobilePanelSource: \"DirectMessageClick\"", direct);
        Assert.Contains("requestComposerFocus: ShouldAutoFocusNavigation", direct);
        Assert.Contains("_mobileLayoutKnown && !_isMobileLayout", home);
        Assert.Contains("if (channel?.Kind == CommunityChannelKind.Text && requestComposerFocus)", home);
        Assert.Contains("if (requestComposerFocus) RequestDirectComposerFocus(conversation.Id)", home);
    }

    [Fact]
    public void ExplicitServerNavigationFocusesOnlyItsRestoredTextChannel()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var community = Slice(home, "private async Task SelectCommunityFromNavigationAsync", "private async Task SelectChannelAsync");

        Assert.Contains("OnCommunitySelected=\"SelectCommunityFromNavigationAsync\"", home);
        Assert.Contains("mobilePanelSource: community is not null ? \"CommunityClick\" : null", community);
        Assert.Contains("await SelectCommunity(community, focusRestoredChannel: ShouldAutoFocusNavigation", community);
        Assert.Contains("SelectChannelAsync(channel ?? CommunityState.FirstOrderedChannel(), focusRestoredChannel)", home);
        Assert.DoesNotContain("RequestChannelComposerFocus", Slice(home,
            "private async Task SelectCommunity(CommunityDto? community, bool focusRestoredChannel = false,",
            "private async Task SelectCommunityFromNavigationAsync"));
    }

    [Theory]
    [InlineData("ChannelView.razor")]
    [InlineData("DirectMessageView.razor")]
    public void ConversationViewConsumesFocusOnceAfterLoadingAndRender(string file)
    {
        var source = Source("Iridium.Web", "Components", file);
        Assert.Contains("OnAfterRenderAsync", source);
        Assert.Contains("if (!_focusAfterRender || IsMobileLayout) return", source);
        Assert.DoesNotContain("if (!_focusAfterRender || _loading) return", source);
        Assert.Contains("_focusAfterRender = false", source);
        Assert.Contains("await _composer.FocusAsync()", source);
        Assert.Contains("await OnFocusConsumed.InvokeAsync(FocusRequest)", source);
    }

    [Theory]
    [InlineData("ChannelView.razor")]
    [InlineData("DirectMessageView.razor")]
    public void MobileConversationViewsDiscardNavigationFocusRequests(string file)
    {
        var source = Source("Iridium.Web", "Components", file);
        var parameters = Slice(source, "protected override void OnParametersSet()", "private async Task HydrateHistoryAsync");

        Assert.Contains("if (IsMobileLayout)", parameters);
        Assert.Contains("_focusAfterRender = false", parameters);
        Assert.Contains("_consumedFocusRequest = Math.Max(_consumedFocusRequest, FocusRequest)", parameters);
    }

    [Fact]
    public void FriendAndProfileMessageNavigationOpenThePanelBeforeAwaitingTheConversation()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var open = Slice(home, "private async Task OpenDirectMessageAsync", "private Task OpenFriendDirectMessageAsync");

        Assert.True(open.IndexOf("ShowMobileConversation(\"OpenDirectMessage\")", StringComparison.Ordinal) <
                    open.IndexOf("await Session.OpenDirectConversationAsync(accountId)", StringComparison.Ordinal));
        Assert.Contains("requestComposerFocus: ShouldAutoFocusNavigation", open);
    }

    [Fact]
    public void MobilePanelBackAndContextClosePreserveTheConversationHierarchy()
    {
        var state = new Iridium.UI.MobilePanelNavigationState();
        state.ShowConversation();
        state.ShowContext();
        state.CloseContext();
        Assert.Equal(Iridium.UI.MobilePanel.Conversation, state.Current);
        state.ShowNavigation();
        Assert.Equal(Iridium.UI.MobilePanel.Navigation, state.Current);
    }

    [Fact]
    public void MobileConversationStateOwnsFullWidthVisibilityAndResetsTransientSwipePresentation()
    {
        var shell = Source("Iridium.UI", "ApplicationShell.razor");
        var styles = Source("Iridium.UI", "ApplicationShell.razor.css");
        var swipe = Source("Iridium.UI", "wwwroot", "js", "mobileConversationSwipe.js");
        var home = Source("Iridium.Web", "Pages", "Home.razor");

        Assert.Contains("mobile-{MobilePanels.Current.ToString().ToLowerInvariant()}", shell);
        Assert.Contains("<header class=\"mobile-conversation-header", shell);
        Assert.Contains("class=\"mobile-back\" aria-label=\"Back to navigation\"", shell);
        Assert.Contains("MobilePanels.Changed += MobilePanelChanged", shell);
        Assert.Contains("MobilePanels.Changed -= MobilePanelChanged", shell);
        Assert.Contains("_ = InvokeAsync(StateHasChanged)", shell);
        Assert.Contains("resetMobileConversationSwipe", shell);
        Assert.Contains("class=\"mobile-navigation-panel\"", shell);
        Assert.Contains("<main class=\"main-content\"", shell);
        Assert.Contains(".mobile-conversation .mobile-navigation-panel,.mobile-context .mobile-navigation-panel{transform:translateX(-100%);visibility:hidden;pointer-events:none", styles);
        Assert.Contains(".mobile-conversation .main-content,.mobile-context .main-content { transform:translateX(0); visibility:visible; pointer-events:auto", styles);
        Assert.Contains(".main-content { grid-column:1 / -1;grid-row:1 / -1;position:absolute", styles);
        Assert.Contains("width:100%;min-width:100%;max-width:none", styles);
        Assert.Contains("export function resetMobileConversationSwipe", swipe);
        Assert.Contains("state.presentationRevision++", swipe);
        Assert.Contains("state.cancelAnimation?.()", swipe);
        Assert.Contains("if (presentationRevision !== state.presentationRevision) return", swipe);
        Assert.Contains("clearSwipeStyles(element, shell)", swipe);
        Assert.Contains("MobileConversationSwipeBackAsync()", shell);
        Assert.Contains("? OnMobileBack.InvokeAsync()", shell);
        Assert.Contains("requestAnimationFrame(writeDragVisual)", swipe);
        Assert.Contains("translate3d(${state.renderedX}px,0,0)", swipe);
        Assert.Contains("mobile-swipe-revealing", styles);
        Assert.Contains("data-swipe-nav-ignore", swipe);
        Assert.Contains("@inject MobilePanelNavigationState MobilePanels", home);
        Assert.Contains("private void ShowMobileConversation(string source)", home);
        Assert.Contains("MobilePanels.ShowConversation(source)", home);
    }

    [Fact]
    public void ClaimedMobileSwipeOwnsItsPointerUntilARealTermination()
    {
        var shell = Source("Iridium.UI", "ApplicationShell.razor");
        var styles = Source("Iridium.UI", "ApplicationShell.razor.css");
        var swipe = Source("Iridium.UI", "wwwroot", "js", "mobileConversationSwipe.js");
        var chat = Source("Iridium.Web", "wwwroot", "js", "chat.js");

        Assert.Contains("EnableMobilePanelDiagnostics", shell);
        Assert.Contains("touch-action:pan-y", styles);
        Assert.Contains("MobileConversationSwipePhase", swipe);
        Assert.Contains("state.phase = MobileConversationSwipePhase.draggingHorizontal", swipe);
        Assert.Contains("hasPointerCapture: element.hasPointerCapture?.(event.pointerId) === true", swipe);
        Assert.Contains("gotpointercapture", swipe);
        Assert.Contains("lostpointercapture", swipe);
        Assert.Contains("'bottom-sheet-cancel-event'", swipe);
        Assert.Contains("reason: 'active-horizontal-capture'", swipe);
        Assert.Contains("'iridium-mobile-navigation-swipe-claimed'", swipe);
        Assert.Contains("gesture.cancelPointer(event.detail?.pointerId)", chat);
        Assert.DoesNotContain("addEventListener('pointerleave'", swipe);
        Assert.DoesNotContain("addEventListener('scroll'", Slice(swipe,
            "export function wireMobileConversationSwipe", "function hasHorizontalScrollTarget"));
    }

    [Fact]
    public void MobileShellUsesTwoFullWidthSiblingPanelsAndReportsTheirRenderedGeometry()
    {
        var shell = Source("Iridium.UI", "ApplicationShell.razor");
        var styles = Source("Iridium.UI", "ApplicationShell.razor.css");
        var diagnostics = Source("Iridium.UI", "wwwroot", "js", "mobileConversationSwipe.js");

        var navigationStart = shell.IndexOf("<div class=\"mobile-navigation-panel\"", StringComparison.Ordinal);
        var rail = shell.IndexOf("<CommunityRail", navigationStart, StringComparison.Ordinal);
        var sidebar = shell.IndexOf("<aside class=\"secondary-sidebar", rail, StringComparison.Ordinal);
        var mobileProfile = shell.IndexOf("<div class=\"mobile-profile-panel-slot\"", sidebar, StringComparison.Ordinal);
        var conversation = shell.IndexOf("<main class=\"main-content\"", mobileProfile, StringComparison.Ordinal);

        Assert.True(navigationStart >= 0 && rail > navigationStart && sidebar > rail &&
                    mobileProfile > sidebar && conversation > mobileProfile);
        Assert.Contains("position:absolute;z-index:20;inset:0;display:grid", styles);
        Assert.Contains("grid-template-columns:4.25rem minmax(0,1fr)", styles);
        Assert.Contains("grid-column:1 / -1;grid-row:2", styles);
        Assert.Contains("export function inspectMobilePanels(shell, navigation, conversation)", diagnostics);
        Assert.Contains("getBoundingClientRect()", diagnostics);
        Assert.Contains("header: Boolean(conversation.querySelector('.mobile-conversation-header'))", diagnostics);
        Assert.Contains("directMessageView: Boolean(conversation.querySelector('.direct-message-view'))", diagnostics);
        Assert.Contains("channelView: Boolean(conversation.querySelector('.channel-view'))", diagnostics);
        Assert.Contains("dmMessageRegion: Boolean(conversation.querySelector('.dm-message-region'))", diagnostics);
        Assert.Contains("dmMessageHistory: Boolean(conversation.querySelector('.dm-message-history'))", diagnostics);
        Assert.Contains("composer: Boolean(conversation.querySelector('.composer-wrap'))", diagnostics);
        Assert.Contains("messageList: Boolean(conversation.querySelector('.message-list'))", diagnostics);
        Assert.Contains("missingNodes: required.filter", diagnostics);
        Assert.Contains("Conversation DOM incomplete for Home branch", shell);
    }

    [Fact]
    public void MobileConversationPanelRetainsTheHistoricalHeaderAndHomeContentComposition()
    {
        var shell = Source("Iridium.UI", "ApplicationShell.razor");
        var home = Source("Iridium.Web", "Pages", "Home.razor");

        var main = shell.IndexOf("<main class=\"main-content\"", StringComparison.Ordinal);
        var header = shell.IndexOf("<header class=\"mobile-conversation-header", main, StringComparison.Ordinal);
        var childSlot = shell.IndexOf("<div class=\"main-content-slot\">@ChildContent</div>", header, StringComparison.Ordinal);
        var mainEnd = shell.IndexOf("</main>", childSlot, StringComparison.Ordinal);
        Assert.True(main >= 0 && header > main && childSlot > header && mainEnd > childSlot);

        var namedContent = Slice(home, "<ChildContent>", "</ChildContent>");
        Assert.Contains("_homeView == \"dm\" && _selectedDirectConversation is not null", namedContent);
        Assert.Contains("<DirectMessageView", namedContent);
        Assert.Contains("_selectedChannel is null", namedContent);
        Assert.Contains("<ChannelView", namedContent);
        Assert.Contains("MobileConversationContentKind=\"@MobileConversationContentKind\"", home);

        var direct = Source("Iridium.Web", "Components", "DirectMessageView.razor");
        var channel = Source("Iridium.Web", "Components", "ChannelView.razor");
        Assert.Contains("<MessageList", direct);
        Assert.Contains("<MessageComposer", direct);
        Assert.Contains("<MessageList", channel);
        Assert.Contains("<MessageComposer", channel);
    }

    [Fact]
    public void MobileConversationPresentationDoesNotWaitForHistoryOrComposerFocus()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var channelNavigation = Slice(home, "private async Task SelectChannelAsync", "private Task SelectChannelFromNavigationAsync");
        var directNavigation = Slice(home, "private async Task SelectDirectConversationAsync", "private Task SelectDirectConversationFromNavigationAsync");

        Assert.True(channelNavigation.IndexOf("_selectedChannel = channel", StringComparison.Ordinal) <
                    channelNavigation.IndexOf("ShowMobileConversation(mobilePanelSource)", StringComparison.Ordinal));
        Assert.True(channelNavigation.IndexOf("ShowMobileConversation(mobilePanelSource)", StringComparison.Ordinal) <
                    channelNavigation.IndexOf("await InvokeAsync(StateHasChanged)", StringComparison.Ordinal));
        Assert.True(directNavigation.IndexOf("_selectedDirectConversation = conversation", StringComparison.Ordinal) <
                    directNavigation.IndexOf("ShowMobileConversation(mobilePanelSource)", StringComparison.Ordinal));
        Assert.True(directNavigation.IndexOf("ShowMobileConversation(mobilePanelSource)", StringComparison.Ordinal) <
                    directNavigation.IndexOf("await InvokeAsync(StateHasChanged)", StringComparison.Ordinal));
        Assert.DoesNotContain("FocusAsync", channelNavigation);
        Assert.DoesNotContain("FocusAsync", directNavigation);
        Assert.DoesNotContain("HydrateHistoryAsync", channelNavigation);
        Assert.DoesNotContain("HydrateHistoryAsync", directNavigation);
    }

    [Fact]
    public void MobilePanelStateIsOneScopedClientServiceSharedByHomeAndShell()
    {
        var program = Source("Iridium.Web", "Program.cs");
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var shell = Source("Iridium.UI", "ApplicationShell.razor");

        Assert.Contains("AddScoped<MobilePanelNavigationState>()", program);
        Assert.Contains("@inject MobilePanelNavigationState MobilePanels", home);
        Assert.Contains("@inject MobilePanelNavigationState MobilePanels", shell);
        Assert.DoesNotContain("new MobilePanelNavigationState", home);
        Assert.Contains("InstanceId", shell);
    }

    [Fact]
    public void DirectMessageTransitionNotifiesTheShellOnceWithoutAnImplicitNavigationReset()
    {
        var state = new Iridium.UI.MobilePanelNavigationState();
        var transitions = new List<Iridium.UI.MobilePanelTransition>();
        state.Changed += transitions.Add;

        state.ShowConversation("DirectMessageClick");

        var transition = Assert.Single(transitions);
        Assert.Equal(Iridium.UI.MobilePanel.Navigation, transition.Previous);
        Assert.Equal(Iridium.UI.MobilePanel.Conversation, transition.Current);
        Assert.Equal("DirectMessageClick", transition.Source);
        Assert.Equal(state.InstanceId, transition.InstanceId);
        Assert.Equal(Iridium.UI.MobilePanel.Conversation, state.Current);
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

        Assert.Equal(2, shell.Split("<ProfilePanel", StringSplitOptions.None).Length - 1);
        Assert.Contains("class=\"desktop-player-panel-slot\"", shell);
        Assert.Contains("class=\"mobile-profile-panel-slot\"", shell);
        Assert.Equal(1, shell.Split("ShowVoiceControls=\"true\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, rail.Split("class=\"rail-item add\"", StringSplitOptions.None).Length - 1);
        Assert.True(rail.IndexOf("class=\"rail-item add\"", StringComparison.Ordinal) <
                    rail.IndexOf("</div>", rail.IndexOf("class=\"rail-item add\"", StringComparison.Ordinal),
                        StringComparison.Ordinal));
        Assert.Contains("grid-template-rows:minmax(0,1fr)", shellStyles);
        Assert.Contains(".secondary-sidebar { grid-column:2;grid-row:1; min-width:0; min-height:0; overflow:hidden; }", shellStyles);
        Assert.Contains(".mobile-profile-panel-slot{display:block;grid-column:1 / -1;grid-row:2", shellStyles);
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
