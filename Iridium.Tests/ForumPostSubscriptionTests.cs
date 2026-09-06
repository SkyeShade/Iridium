using Iridium.Protocol;
using Iridium.Server.Domain;
using Iridium.Server.Notifications;

namespace Iridium.Tests;

public sealed class ForumPostSubscriptionTests
{
    [Theory]
    [InlineData(null, false, true)]
    [InlineData(ForumPostNotificationLevel.AllMessages, true, true)]
    [InlineData(ForumPostNotificationLevel.MentionsOnly, false, true)]
    [InlineData(ForumPostNotificationLevel.Nothing, false, true)]
    [InlineData(ForumPostNotificationLevel.Muted, false, false)]
    public void NotificationMatrixMatchesForumThreadSemantics(ForumPostNotificationLevel? level,
        bool ordinary, bool mention)
    {
        var subscription = level is null ? null : new ForumPostSubscription
        {
            ForumPostId = Guid.NewGuid(), AccountId = Guid.NewGuid(), JoinedAt = DateTimeOffset.UtcNow,
            NotificationLevel = level.Value, ForumPost = null!, Account = null!
        };

        Assert.Equal(ordinary, ForumPostNotificationPolicy.DeliversOrdinaryReply(subscription));
        Assert.Equal(mention, ForumPostNotificationPolicy.DeliversMention(subscription));
    }

    [Fact]
    public void SidebarAndContextMenuUseCanonicalFollowStateAndDesktopOnlyChildren()
    {
        var sidebar = Source("Iridium.Web", "Components", "ForumPostSidebarChildren.razor");
        var styles = Source("Iridium.Web", "Components", "ForumPostSidebarChildren.razor.css");
        var menu = Source("Iridium.Web", "Components", "ForumPostContextMenu.razor");
        var home = Source("Iridium.Web", "Pages", "Home.razor");

        Assert.Contains("SidebarLimit = 15", sidebar);
        Assert.Contains("OrderByDescending(value => value.IsPinned)", Source("Iridium.Web", "Components", "CommunitySidebar.razor"));
        Assert.Contains("See all followed posts", sidebar);
        Assert.Contains("@media(max-width:860px)", styles);
        Assert.Contains(".forum-post-children { display: none; }", styles);
        Assert.Contains("Follow Post", menu);
        Assert.Contains("Unfollow Post", menu);
        Assert.Contains("All Messages", menu);
        Assert.Contains("Mentions Only", menu);
        Assert.Contains("InitialPostId=\"_selectedForumPostId\"", home);
    }

    [Fact]
    public void FollowedPostChildrenUseCompactDesktopHierarchyStyles()
    {
        var markup = Source("Iridium.Web", "Components", "ForumPostSidebarChildren.razor");
        var styles = Source("Iridium.Web", "Components", "ForumPostSidebarChildren.razor.css");
        var shared = Source("Iridium.Web", "wwwroot", "css", "app.css");

        Assert.Contains("forum-post-child-compact", markup);
        Assert.Contains("last-child-post", markup);
        Assert.Contains("min-height: 2.35rem", styles);
        Assert.Contains("min-height: 2.25rem", styles);
        Assert.Contains("font-size: .82rem", styles);
        Assert.Contains("border-radius: .42rem", shared);
        Assert.Contains("min-width: 0", styles);
        Assert.Contains("overflow: hidden", styles);
        Assert.Contains("text-overflow: ellipsis", styles);
        Assert.Contains("white-space: nowrap", styles);
        Assert.Contains(".forum-post-child.unread .forum-post-title", styles);
        Assert.Contains(".forum-post-mentions", styles);
    }

    [Fact]
    public void FollowedPostStatesAndConnectorGuidesHaveExplicitVisualPrecedence()
    {
        var styles = Source("Iridium.Web", "Components", "ForumPostSidebarChildren.razor.css");
        var shared = Source("Iridium.Web", "wwwroot", "css", "app.css");
        var hover = shared.IndexOf(".sidebar-nav-row:hover > .sidebar-nav-surface", StringComparison.Ordinal);
        var selected = shared.IndexOf(".sidebar-nav-row.is-active > .sidebar-nav-surface", StringComparison.Ordinal);
        var selectedHover = shared.IndexOf(".sidebar-nav-row.is-active:hover > .sidebar-nav-surface", StringComparison.Ordinal);

        Assert.True(hover >= 0 && selected > hover && selectedHover > selected);
        Assert.Contains("var(--surface-hover)", shared);
        Assert.Contains("var(--surface-active)", shared);
        Assert.Contains("border-left: 1px solid color-mix(in srgb, var(--text-faint) 42%, transparent)", styles);
        Assert.Contains("border-top: 1px solid color-mix(in srgb, var(--text-faint) 42%, transparent)", styles);
        Assert.Contains("width: .52rem", styles);
    }

    [Fact]
    public void ChannelsAndForumChildrenShareOuterHitTargetAndFullRemainingWidthPaintedSurface()
    {
        var channel = Source("Iridium.UI", "ChannelRow.razor");
        var channelStyles = Source("Iridium.UI", "ChannelRow.razor.css");
        var child = Source("Iridium.Web", "Components", "ForumPostSidebarChildren.razor");
        var shared = Source("Iridium.Web", "wwwroot", "css", "app.css");

        Assert.Contains("channel-row sidebar-nav-row", channel);
        Assert.Contains("sidebar-nav-surface channel-nav-surface", channel);
        Assert.Contains("forum-post-child-compact sidebar-nav-row", child);
        Assert.Contains("sidebar-nav-surface forum-post-nav-surface", child);
        Assert.DoesNotContain("width: fit-content", shared);
        Assert.Contains("flex: 1 1 0", shared);
        Assert.Contains("width: auto", shared);
        Assert.Contains("min-height:2.35rem", channelStyles);
        Assert.Contains("min-height:2.25rem", channelStyles);
        Assert.Contains("flex: 1 1 auto", channelStyles);
        Assert.Contains("@onclick=\"SelectAsync\"", channel);
        Assert.Contains("@onclick=\"() => OnSelect.InvokeAsync(post)\"", child);
        Assert.Contains("margin-left:auto", channelStyles);
        Assert.Contains("text-overflow: ellipsis", channelStyles);
    }

    [Fact]
    public void ChannelActionsRemainInsideTheCompactSurfaceWithoutChangingVisibilityRules()
    {
        var channel = Source("Iridium.UI", "ChannelRow.razor");
        var surfaceStart = channel.IndexOf("sidebar-nav-surface channel-nav-surface", StringComparison.Ordinal);
        var settings = channel.IndexOf("class=\"channel-settings\"", surfaceStart, StringComparison.Ordinal);
        var surfaceEnd = channel.IndexOf("</span>", settings, StringComparison.Ordinal);
        var styles = Source("Iridium.UI", "ChannelRow.razor.css");

        Assert.True(surfaceStart >= 0 && settings > surfaceStart && surfaceEnd > settings);
        Assert.Contains("opacity: 0", styles);
        Assert.Contains(".channel-row:hover .channel-settings, .channel-row.selected .channel-settings", styles);
    }

    [Fact]
    public void CategoriesAndForumsReuseOneChevronWithForumDisclosureAfterItsName()
    {
        var category = Source("Iridium.UI", "CategoryRow.razor");
        var channel = Source("Iridium.UI", "ChannelRow.razor");
        var chevron = Source("Iridium.UI", "SidebarDisclosureChevron.razor");
        var styles = Source("Iridium.UI", "SidebarDisclosureChevron.razor.css");
        var name = channel.IndexOf("class=\"channel-name", StringComparison.Ordinal);
        var disclosure = channel.IndexOf("class=\"channel-disclosure\"", StringComparison.Ordinal);

        Assert.Contains("<SidebarDisclosureChevron Collapsed=\"Collapsed\" />", category);
        Assert.Contains("<SidebarDisclosureChevron Collapsed=\"ChildrenCollapsed\" />", channel);
        Assert.True(name >= 0 && disclosure > name);
        Assert.Contains("<Icon Name=\"chevron\" />", chevron);
        Assert.Contains("transform: rotate(-90deg)", styles);
        Assert.DoesNotContain(">@(ChildrenCollapsed ?", channel);
    }

    [Fact]
    public void ReplyAutoFollowAndMentionDeliveryAreServerAuthoritative()
    {
        var hub = Source("Iridium.Server", "Hubs", "ChatHub.cs");
        var endpoints = Source("Iridium.Server", "Api", "CommunityForumEndpoints.cs");

        Assert.Contains("INSERT OR IGNORE INTO ForumPostSubscriptions", hub);
        Assert.Contains("BeginTransactionAsync", hub);
        Assert.Contains("ForumPostNotificationLevel.MentionsOnly", hub);
        Assert.Contains("recipients.ExceptWith(mutedRecipients)", hub);
        Assert.Contains("value.NotificationLevel == ForumPostNotificationLevel.AllMessages", hub);
        Assert.Contains("db.ForumPostSubscriptions.Add", endpoints);
        Assert.Contains("group.MapPut(\"/{postId:guid}/subscription\"", endpoints);
        Assert.Contains("group.MapDelete(\"/{postId:guid}/subscription\"", endpoints);
    }

    [Fact]
    public void CommunitySidebarSubscriptionLoadIsBoundedAndDoesNotAuthorizeOnePostAtATime()
    {
        var endpoints = Source("Iridium.Server", "Api", "CommunityForumEndpoints.cs");
        var start = endpoints.IndexOf("private static async Task<IResult> ListCommunityFollowedAsync",
            StringComparison.Ordinal);
        var end = endpoints.IndexOf("private static async Task<IResult> GetEmbedDocumentAsync", start,
            StringComparison.Ordinal);
        var method = endpoints[start..end];

        Assert.Contains(".Take(101)", method);
        Assert.Contains("VisibleChannelIdsAsync", method);
        Assert.DoesNotContain("foreach (var forumGroup", method);
        Assert.Contains("GetCommunityFollowedForumPostsAsync", Source("Iridium.Client.Core", "CommunitySession.cs"));
    }

    [Fact]
    public void SidebarSwitchUsesOneCanonicalAtomicForumPostNavigationPath()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var canonical = Slice(home, "private async Task OpenForumPostAsync", "private async Task ForumPostNavigationRequestedAsync");
        var sidebar = Source("Iridium.Web", "Components", "ForumPostSidebarChildren.razor");

        Assert.Contains("OnForumPostSelected=\"SelectForumPostFromSidebarAsync\"", home);
        Assert.Contains("OpenForumPostAsync(post.ForumChannelId, post.Id, ForumPostNavigationSource.SidebarChild", home);
        Assert.Contains("forumPostId: postId", canonical);
        Assert.Contains("forumDiscussionChannelId: discussionChannelId", canonical);
        Assert.DoesNotContain("_selectedForumPostId = post.Id", canonical);
        Assert.Contains("@key=\"post.Id\"", sidebar);
        Assert.Contains("SelectedPostId == post.Id ? \"selected is-active\"", sidebar);
    }

    [Fact]
    public void ForumOverviewAndSearchConvergeOnCanonicalNavigation()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var forum = Source("Iridium.Web", "Components", "ForumChannelView.razor");

        Assert.Contains("OnPostNavigationRequested=\"ForumPostNavigationRequestedAsync\"", home);
        Assert.Contains("ForumPostNavigationSource.ForumOverview", home);
        Assert.Contains("ForumPostNavigationSource.Search", home);
        Assert.Contains("OnOpen=\"RequestOpenPostAsync\"", forum);
        Assert.DoesNotContain("OnOpen=\"OpenPostAsync\"", forum);
    }

    [Fact]
    public void RapidPostLoadsCancelAndDiscardStaleCompletionBeforeMutatingSelection()
    {
        var forum = Source("Iridium.Web", "Components", "ForumChannelView.razor");
        var load = Slice(forum, "private async Task OpenPostAsync(Guid postId)", "public async Task OpenPostByIdAsync");
        var byId = Slice(forum, "public async Task OpenPostByIdAsync", "private void ComposerFocusConsumed");

        var cancel = load.IndexOf("_postLoadCancellation?.Cancel()", StringComparison.Ordinal);
        var fetch = load.IndexOf("await Session.GetForumPostAsync", StringComparison.Ordinal);
        var generationGuard = load.IndexOf("generation != _postLoadGeneration", StringComparison.Ordinal);
        var selection = load.IndexOf("_selectedPost = post", StringComparison.Ordinal);
        Assert.True(cancel >= 0 && fetch > cancel && generationGuard > fetch && selection > generationGuard);
        Assert.Contains("_loadingPostId == postId", byId);
        Assert.Contains("await OpenPostAsync(postId)", byId);
        Assert.DoesNotContain("GetForumPostAsync", byId);
    }

    [Fact]
    public void RealtimeRefreshAndLoadedCallbacksCannotNavigateBackToAnOlderPost()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var forum = Source("Iridium.Web", "Components", "ForumChannelView.razor");
        var realtime = Slice(forum, "private void ForumChanged()", "private async Task SearchChangedAsync");
        var completion = Slice(home, "private void ForumPostOpenChanged", "private void ForumSubViewChanged");

        Assert.DoesNotContain("OnPostOpenChanged.InvokeAsync(_selectedPost)", realtime);
        Assert.DoesNotContain("OnPostNavigationRequested", realtime.Replace(
            "_ = OnPostNavigationRequested.InvokeAsync(null);", string.Empty, StringComparison.Ordinal));
        Assert.Contains("if (post?.Id != _selectedForumPostId) return", completion);
        Assert.DoesNotContain("_selectedForumPostId = post?.Id", completion);
    }

    [Fact]
    public void SameAndDifferentForumTransitionsHonorTheLastExplicitPostIdentity()
    {
        var home = Source("Iridium.Web", "Pages", "Home.razor");
        var canonical = Slice(home, "private async Task OpenForumPostAsync", "private async Task ForumPostNavigationRequestedAsync");

        Assert.Contains("value.Id == forumId", canonical);
        Assert.Contains("forumPostId: postId", canonical);
        Assert.DoesNotContain("_selectedChannel?.Id == forumId", canonical);
        Assert.DoesNotContain("previousPostId == postId", canonical);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex, $"Could not slice {start} -> {end}.");
        return source[startIndex..endIndex];
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")), Path.Combine(parts)));
}
