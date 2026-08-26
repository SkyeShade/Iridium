using System.Diagnostics;
using System.Net.Sockets;
using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR.Client;

namespace Iridium.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ForumIntegrationCollection
{
    public const string Name = "Forum integration";
}

[Collection(ForumIntegrationCollection.Name)]
public sealed class ForumFlowTests
{
    [Fact(Timeout = 60_000)]
    public async Task ForumPostsPersistOrderBroadcastAndReuseMessageDiscussionRules()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var project = Path.Combine(root, "Iridium.Server", "Iridium.Server.csproj");
        var temp = Path.Combine(Path.GetTempPath(), $"iridium-forum-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var address = new Uri($"http://127.0.0.1:{FreePort()}/");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        using var server = StartServer(project, address, Path.Combine(temp, "forum.db"),
            Path.Combine(temp, "objects"), configuration);
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();
        try
        {
            await WaitForServerAsync(address, server, output, error);
            var owner = new NodeClient(address);
            var ownerAuth = await owner.RegisterAsync(new("forum-owner", "Forum Owner", "test-password"));
            var member = new NodeClient(address);
            var memberAuth = await member.RegisterAsync(new("forum-member", "Forum Member", "test-password"));
            var community = await owner.CreateCommunityAsync(new("Forum Server", null));
            var initial = Assert.Single((await owner.GetCommunityStructureAsync(community.Id)).Channels);
            var forum = await owner.CreateChannelAsync(community.Id, "support", null, CommunityChannelKind.Forum);
            Assert.Equal(CommunityChannelKind.Forum, forum.Kind);
            var invite = await owner.CreateCommunityInviteAsync(community.Id, new(null, null));
            await member.JoinCommunityInviteAsync(CommunityInviteLink.Find(invite.InviteUrl!)!.Token);
            var initialAttachment = await member.UploadAttachmentAsync(new MemoryStream("forum file"u8.ToArray()),
                "forum.txt", "text/plain");
            var memberPost = await member.CreateForumPostAsync(community.Id, forum.Id,
                new("Member topic", new("Members can create posts by default", null,
                    ClientMessageId: Guid.NewGuid(), AttachmentIds: [initialAttachment.Id])));
            Assert.Equal(memberAuth.Account.Id, memberPost.Author.AccountId);
            Assert.Equal(initialAttachment.Id, Assert.Single((await member.GetChannelMessagePageAsync(
                community.Id, memberPost.DiscussionChannelId)).Messages).Attachments!.Single().Id);
            var management = await owner.GetCommunityManagementAsync(community.Id);
            var everyone = Assert.Single(management.Roles, value => value.IsDefault);
            await owner.UpdateCommunityRoleAsync(community.Id, everyone.Id, new(everyone.Name,
                everyone.Permissions & ~CommunityPermission.CreateForumPosts, everyone.Color,
                everyone.DisplaySeparately, everyone.IsMentionable));
            var denied = await Assert.ThrowsAsync<NodeApiException>(() => member.CreateForumPostAsync(community.Id,
                forum.Id, new("Denied topic", new("No permission", null, ClientMessageId: Guid.NewGuid()))));
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);

            await using var memberHub = Connection(address, memberAuth.AccessToken);
            await memberHub.StartAsync();
            await memberHub.InvokeAsync(ChatHubContract.JoinChannel, community.Id, memberPost.DiscussionChannelId);
            var editedRoot = await memberHub.InvokeAsync<ChannelMessageDto>(ChatHubContract.EditMessage,
                community.Id, memberPost.DiscussionChannelId, memberPost.RootMessageId,
                new EditChannelMessageRequest("Updated root **Markdown**"));
            Assert.Equal("Updated root **Markdown**", editedRoot.Content);
            Assert.Equal("Updated root **Markdown**", (await member.GetForumPostAsync(
                community.Id, forum.Id, memberPost.Id)).RootPreview);
            await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => memberHub.InvokeAsync(
                ChatHubContract.DeleteMessage, community.Id, memberPost.DiscussionChannelId, memberPost.RootMessageId));

            var changed = new TaskCompletionSource<CommunityForumPostChangedEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            memberHub.On<CommunityForumPostChangedEvent>(CommunityForumHubContract.PostChanged,
                value => changed.TrySetResult(value));

            var first = await owner.CreateForumPostAsync(community.Id, forum.Id,
                new("First topic", new("Initial **Markdown** body", null, ClientMessageId: Guid.NewGuid())));
            Assert.Equal(first.Id, (await changed.Task.WaitAsync(TimeSpan.FromSeconds(15))).PostId);
            Assert.NotEqual(first.Id, first.DiscussionChannelId);
            var rootMessage = Assert.Single((await owner.GetChannelMessagePageAsync(
                community.Id, first.DiscussionChannelId)).Messages);
            Assert.Equal(first.RootMessageId, rootMessage.Id);
            Assert.Equal("Initial **Markdown** body", rootMessage.Content);

            await memberHub.InvokeAsync(ChatHubContract.JoinChannel, community.Id, first.DiscussionChannelId);
            var reply = await memberHub.InvokeAsync<ChannelMessageDto>(ChatHubContract.SendMessage,
                community.Id, first.DiscussionChannelId,
                new SendChannelMessageRequest("A reply", null, ClientMessageId: Guid.NewGuid()));
            var afterReply = await owner.GetForumPostAsync(community.Id, forum.Id, first.Id);
            Assert.Equal(1, afterReply.ReplyCount);
            Assert.True(afterReply.LastActivityAt >= first.LastActivityAt);
            var edited = await memberHub.InvokeAsync<ChannelMessageDto>(ChatHubContract.EditMessage,
                community.Id, first.DiscussionChannelId, reply.Id, new EditChannelMessageRequest("Edited reply"));
            Assert.Equal("Edited reply", edited.Content);
            await memberHub.InvokeAsync(ChatHubContract.DeleteMessage, community.Id, first.DiscussionChannelId, reply.Id);
            Assert.Equal(0, (await owner.GetForumPostAsync(community.Id, forum.Id, first.Id)).ReplyCount);

            var second = await owner.CreateForumPostAsync(community.Id, forum.Id,
                new("Second topic", new("Second body", null, ClientMessageId: Guid.NewGuid())));
            var renamed = await owner.UpdateForumPostAsync(community.Id, forum.Id, first.Id,
                new(Title: "Renamed topic", IsPinned: true));
            Assert.Equal("Renamed topic", renamed.Title);
            var page = await owner.GetForumPostsAsync(community.Id, forum.Id);
            Assert.Equal(first.Id, page.Posts[0].Id);
            Assert.Equal("Initial **Markdown** body", page.Posts[0].RootPreview);
            Assert.Contains(page.Posts, value => value.Id == second.Id);

            await owner.UpdateForumPostAsync(community.Id, forum.Id, first.Id, new(IsLocked: true));
            await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => memberHub.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.SendMessage, community.Id, first.DiscussionChannelId,
                new SendChannelMessageRequest("blocked", null, ClientMessageId: Guid.NewGuid())));
            await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => memberHub.InvokeAsync(
                ChatHubContract.DeleteMessage, community.Id, first.DiscussionChannelId, first.RootMessageId));

            var structure = await owner.GetCommunityStructureAsync(community.Id);
            Assert.DoesNotContain(structure.Channels, value => value.Id == first.DiscussionChannelId);
            Assert.Contains(structure.Channels, value => value.Id == initial.Id);
            Assert.Contains(structure.Channels, value => value.Id == forum.Id && value.Kind == CommunityChannelKind.Forum);
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try { Directory.Delete(temp, true); break; }
                catch (IOException) when (attempt < 19) { await Task.Delay(100); }
            }
        }
    }

    [Fact]
    public void ForumUiKeepsDraftAndMessageInfrastructureScopedPerPost()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumChannelView.razor"));
        Assert.Contains("DraftConversationKind=\"forum\"", source);
        Assert.Contains("DraftConversationId=\"selectedPost.Id\"", source);
        Assert.Contains("ChannelView", source);
        Assert.Contains("DiscussionChannelId", source);
        var first = new CommunityForumPostCacheScope("https://node.example", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var second = first with { ForumChannelId = Guid.NewGuid() };
        Assert.NotEqual(first.StorageKey, second.StorageKey);
    }

    [Fact]
    public void ForumIndexSearchMatchesTitleRootPreviewAndAuthorWithoutChangingServerOrder()
    {
        var first = Post("Pinned help", "Set up your material template", "EntitySpike", pinned: true);
        var second = Post("Rendering", "A normal discussion", "Skye");
        var posts = new[] { first, second };

        Assert.Equal([first.Id], CommunityForumPostSearch.Filter(posts, "material").Select(value => value.Id));
        Assert.Equal([first.Id], CommunityForumPostSearch.Filter(posts, "entity").Select(value => value.Id));
        Assert.Equal([second.Id], CommunityForumPostSearch.Filter(posts, "render").Select(value => value.Id));
        Assert.Equal(posts.Select(value => value.Id), CommunityForumPostSearch.Filter(posts, " ").Select(value => value.Id));
    }

    [Fact]
    public void ForumUiUsesExplicitIndexCreateAndViewSurfaces()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumChannelView.razor"));
        var browser = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumPostBrowser.razor"));
        var styles = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumChannelView.razor.css"));
        var messageList = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageList.razor"));
        var messageRow = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageRow.razor"));
        var messageRowStyles = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageRow.razor.css"));
        var home = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Pages", "Home.razor"));

        Assert.Contains("ForumViewMode { Index, CreatingPost, ViewingPost }", source);
        Assert.Contains("Search or create a post...", browser);
        Assert.Contains("case ForumViewMode.CreatingPost:", source);
        Assert.DoesNotContain("_showNewPost", source);
        Assert.DoesNotContain("new-post-panel", source);
        Assert.Contains("ConversationKind=\"@CreateBodyDraftScope.ConversationKind\"", source);
        Assert.Contains("CommunityForumPostDrafts.TitleScope", source);
        Assert.Contains("OnSubmitted=\"FinishCreatedPostAsync\"", source);
        Assert.Contains("RootMessageId=\"selectedPost.RootMessageId\"", source);
        Assert.Contains("This post has been locked. Only moderators can send messages.", source);
        Assert.Contains("SelectedForumPostId", source);
        Assert.Contains("forum-split-shell", source);
        Assert.Contains("<ForumPostBrowser", source);
        Assert.Contains("grid-template-columns:clamp(17.5rem,32%,24rem) minmax(0,1fr)", styles);
        Assert.Contains("@media(max-width:1100px)", styles);
        Assert.Contains("root-message-separator", messageList);
        Assert.DoesNotContain("RootPostTitle", messageList);
        Assert.DoesNotContain("IsRootPost", messageRow);
        Assert.DoesNotContain("root-post", messageRowStyles);
        Assert.Contains("--composer-viewport-ratio:.34", styles);
        Assert.Contains("grid-template-rows:auto minmax(0,1fr) auto", styles);
        Assert.Contains("OnSubViewChanged=\"ForumSubViewChanged\"", home);
        Assert.Contains("_forumView.BackAsync()", home);
    }

    [Fact]
    public void NewPostTitleAndBodyDraftsAreScopedByNodeAccountCommunityAndForum()
    {
        var account = Guid.NewGuid();
        var community = Guid.NewGuid();
        var forum = Guid.NewGuid();
        var title = CommunityForumPostDrafts.TitleScope("node.example", account, community, forum);
        var body = CommunityForumPostDrafts.BodyScope("node.example", account, community, forum);

        Assert.NotEqual(title.StorageKey, body.StorageKey);
        Assert.NotEqual(title.StorageKey, CommunityForumPostDrafts.TitleScope("other.example", account, community, forum).StorageKey);
        Assert.NotEqual(title.StorageKey, CommunityForumPostDrafts.TitleScope("node.example", Guid.NewGuid(), community, forum).StorageKey);
        Assert.NotEqual(title.StorageKey, CommunityForumPostDrafts.TitleScope("node.example", account, Guid.NewGuid(), forum).StorageKey);
        Assert.NotEqual(title.StorageKey, CommunityForumPostDrafts.TitleScope("node.example", account, community, Guid.NewGuid()).StorageKey);
    }

    [Fact]
    public void ForumPinToolbarHasOneStatefulActionAndCompactCardsReuseSharedMarkdownRendering()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var forum = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumChannelView.razor"));
        var card = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumPostCard.razor"));
        var css = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumPostCard.razor.css"));

        Assert.Equal(1, Occurrences(forum, "@onclick=\"TogglePinnedAsync\""));
        Assert.Contains("selectedPost.IsPinned && !CanManageMessages", forum);
        Assert.Contains("PinActionTitle", forum);
        Assert.Contains("aria-pressed=\"@selectedPost.IsPinned\"", forum);
        Assert.Contains("? \"Unpin post\" : \"Pin post\"", forum);
        Assert.Contains("<MentionedMessageContent", card);
        Assert.Contains("Mentions=\"Post.RootMentions\"", card);
        Assert.Contains("Compact=\"true\"", card);
        Assert.DoesNotContain("MessageRow", card);
        Assert.DoesNotContain("MessageAttachments", card);
        Assert.DoesNotContain("MessageExternalEmbeds", card);
        Assert.Contains("text-overflow:ellipsis", css);
        Assert.Contains("white-space:nowrap", css);
        Assert.DoesNotContain("<ProfileAvatar", card);
        Assert.Contains("@Post.Author.DisplayName:", card);
        Assert.Contains("CommunityRolePresentation.MemberColor", card);
        Assert.Contains("CommunityManagement=\"CommunityManagement\"", forum);
        Assert.Contains("NodeAuthority=\"@NodeAuthority\"", forum);
        Assert.DoesNotContain("NodeAuthority=\"NodeAuthority\"", forum);

        var italic = Assert.IsType<MessageContainerNode>(Assert.Single(
            MessageContentSegments.Parse("hmm *test*", null), node => node is MessageContainerNode));
        Assert.Equal(MessageContentKind.Italic, italic.Kind);
        Assert.Equal(MessageContentKind.Bold, Assert.IsType<MessageContainerNode>(
            Assert.Single(MessageContentSegments.Parse("**bold**", null))).Kind);
        Assert.Equal(MessageContentKind.InlineCode, Assert.IsType<MessageContainerNode>(
            Assert.Single(MessageContentSegments.Parse("`code`", null))).Kind);
        Assert.Equal("*incomplete", MessageContentSegments.PlainText(MessageContentSegments.Parse("*incomplete", null)));
    }

    private static int Occurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static CommunityForumPostDto Post(string title, string preview, string author, bool pinned = false) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        new(Guid.NewGuid(), author.ToLowerInvariant(), author), title, DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, false, pinned, RootPreview: preview);

    private static HubConnection Connection(Uri address, string token) => new HubConnectionBuilder()
        .WithUrl(new Uri(address, "hubs/chat"), options => options.AccessTokenProvider =
            () => Task.FromResult<string?>(token)).Build();

    private static Process StartServer(string project, Uri address, string database, string storage,
        string configuration)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[]
                 { "run", "--project", project, "--no-build", "--configuration", configuration, "--no-launch-profile" })
            start.ArgumentList.Add(argument);
        start.Environment["ASPNETCORE_URLS"] = address.ToString().TrimEnd('/');
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__Iridium"] = $"Data Source={database}";
        start.Environment["Node__AttachmentStoragePath"] = storage;
        start.Environment["Deployment__UseHttpsRedirection"] = "false";
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the Iridium test node.");
    }

    private static async Task WaitForServerAsync(Uri address, Process server, Task<string> output, Task<string> error)
    {
        using var http = new HttpClient { BaseAddress = address };
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (server.HasExited)
                throw new InvalidOperationException($"Server exited early.\n{await output}\n{await error}");
            try
            {
                using var response = await http.GetAsync("health");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Server did not start.\n{await output}\n{await error}");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
