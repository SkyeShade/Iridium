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
    public async Task ForumTagsEnforceScopeModerationRequiredLimitFilteringAndCascade()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var project = Path.Combine(root, "Iridium.Server", "Iridium.Server.csproj");
        var temp = Path.Combine(Path.GetTempPath(), $"iridium-forum-tags-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var address = new Uri($"http://127.0.0.1:{FreePort()}/");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        using var server = StartServer(project, address, Path.Combine(temp, "forum-tags.db"),
            Path.Combine(temp, "objects"), configuration);
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();
        try
        {
            await WaitForServerAsync(address, server, output, error);
            var owner = new NodeClient(address);
            var ownerAuth = await owner.RegisterAsync(new("tag-owner", "Tag Owner", "test-password"));
            var member = new NodeClient(address);
            var memberAuth = await member.RegisterAsync(new("tag-member", "Tag Member", "test-password"));
            var community = await owner.CreateCommunityAsync(new("Tagged Forum Server", null));
            var forum = await owner.CreateChannelAsync(community.Id, "issues", null, CommunityChannelKind.Forum);
            var otherForum = await owner.CreateChannelAsync(community.Id, "ideas", null, CommunityChannelKind.Forum);
            var invite = await owner.CreateCommunityInviteAsync(community.Id, new(null, null));
            await member.JoinCommunityInviteAsync(CommunityInviteLink.Find(invite.InviteUrl!)!.Token);

            await using var memberHub = Connection(address, memberAuth.AccessToken);
            var definitionsChanged = new TaskCompletionSource<CommunityForumTagsChangedEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            memberHub.On<CommunityForumTagsChangedEvent>(CommunityForumHubContract.TagsChanged,
                value => definitionsChanged.TrySetResult(value));
            await memberHub.StartAsync();

            var bug = await owner.CreateForumTagAsync(community.Id, forum.Id,
                new("Bug", ReactionEmojiKind.Standard, "🐛"));
            Assert.Contains((await definitionsChanged.Task.WaitAsync(TimeSpan.FromSeconds(15))).Tags,
                value => value.Id == bug.Id);
            var feature = await owner.CreateForumTagAsync(community.Id, forum.Id, new("Feature"));
            var confirmed = await owner.CreateForumTagAsync(community.Id, forum.Id, new("Confirmed", Moderated: true));
            var foreign = await owner.CreateForumTagAsync(community.Id, otherForum.Id, new("Foreign"));
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            var customEmoji = await owner.UploadCommunityEmojiAsync(community.Id, new MemoryStream(png),
                "forum-tag.png", "image/png", "forum_tag");
            var customTag = await owner.CreateForumTagAsync(community.Id, forum.Id,
                new("Custom", ReactionEmojiKind.Custom, CustomEmojiId: customEmoji.Id));
            Assert.Equal([bug.Id, feature.Id, confirmed.Id, customTag.Id], (await owner.GetForumTagsAsync(community.Id, forum.Id))
                .Select(value => value.Id));
            Assert.Equal("🐛", bug.StandardEmoji);
            Assert.Equal(customEmoji.Id, customTag.CustomEmojiId);
            var textChannel = Assert.Single((await owner.GetCommunityStructureAsync(community.Id)).Channels,
                value => value.Kind == CommunityChannelKind.Text);
            var customMessage = await memberHub.InvokeAsync<ChannelMessageDto>(ChatHubContract.SendMessage,
                community.Id, textChannel.Id, new SendChannelMessageRequest(
                    CommunityEmojiNames.Token(customEmoji.Id, customEmoji.Name), null,
                    ClientMessageId: Guid.NewGuid()));
            Assert.Equal(CommunityEmojiNames.Token(customEmoji.Id, customEmoji.Name), customMessage.Content);
            var externalCommunity = await owner.CreateCommunityAsync(new("External Emoji Server", null));
            var externalInvite = await owner.CreateCommunityInviteAsync(externalCommunity.Id, new(null, null));
            await member.JoinCommunityInviteAsync(CommunityInviteLink.Find(externalInvite.InviteUrl!)!.Token);
            var externalEmoji = await owner.UploadCommunityEmojiAsync(externalCommunity.Id, new MemoryStream(png),
                "external.png", "image/png", "external");
            var externalMessage = await memberHub.InvokeAsync<ChannelMessageDto>(ChatHubContract.SendMessage,
                community.Id, textChannel.Id, new SendChannelMessageRequest(
                    CommunityEmojiNames.Token(externalEmoji.Id, externalEmoji.Name), null,
                    ClientMessageId: Guid.NewGuid()));
            Assert.Equal(CommunityEmojiNames.Token(externalEmoji.Id, externalEmoji.Name), externalMessage.Content);
            var externalTag = await owner.CreateForumTagAsync(community.Id, forum.Id,
                new("External", ReactionEmojiKind.Custom, CustomEmojiId: externalEmoji.Id));
            Assert.Equal(externalEmoji.Id, externalTag.CustomEmojiId);
            await owner.SetPermissionOverwriteAsync(community.Id, PermissionOverwriteScopeType.Channel,
                textChannel.Id, new(PermissionOverwriteTargetType.Everyone, null, CommunityPermission.None,
                    CommunityPermission.UseExternalEmoji));
            await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => memberHub.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.SendMessage, community.Id, textChannel.Id, new SendChannelMessageRequest(
                    CommunityEmojiNames.Token(externalEmoji.Id, externalEmoji.Name), null,
                    ClientMessageId: Guid.NewGuid())));
            await owner.DeleteCommunityEmojiAsync(community.Id, customEmoji.Id);
            var afterEmojiDelete = Assert.Single(await owner.GetForumTagsAsync(community.Id, forum.Id),
                value => value.Id == customTag.Id);
            Assert.Equal("Custom", afterEmojiDelete.Name);
            Assert.Null(afterEmojiDelete.CustomEmojiId);

            var duplicate = await Assert.ThrowsAsync<NodeApiException>(() => owner.CreateForumTagAsync(
                community.Id, forum.Id, new(" bug ")));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, duplicate.StatusCode);
            var unauthorizedDefinition = await Assert.ThrowsAsync<NodeApiException>(() => member.CreateForumTagAsync(
                community.Id, forum.Id, new("Member tag")));
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, unauthorizedDefinition.StatusCode);

            forum = await owner.UpdateChannelAsync(community.Id, forum.Id, forum.Name, forum.CategoryId,
                forum.Kind, requireTag: true);
            Assert.True(forum.RequireTag);
            await Assert.ThrowsAsync<NodeApiException>(() => member.CreateForumPostAsync(community.Id, forum.Id,
                new("Missing tag", new("body", null, ClientMessageId: Guid.NewGuid()))));
            await Assert.ThrowsAsync<NodeApiException>(() => member.CreateForumPostAsync(community.Id, forum.Id,
                new("Wrong Forum", new("body", null, ClientMessageId: Guid.NewGuid()), [foreign.Id])));
            await Assert.ThrowsAsync<NodeApiException>(() => member.CreateForumPostAsync(community.Id, forum.Id,
                new("Moderated", new("body", null, ClientMessageId: Guid.NewGuid()), [confirmed.Id])));

            var memberPost = await member.CreateForumPostAsync(community.Id, forum.Id,
                new("Crash on launch", new("crash body", null, ClientMessageId: Guid.NewGuid()), [bug.Id]));
            Assert.Equal([bug.Id], memberPost.Tags!.Select(value => value.Id));
            var moderatedPost = await owner.CreateForumPostAsync(community.Id, forum.Id,
                new("Confirmed crash", new("known crash", null, ClientMessageId: Guid.NewGuid()),
                    [bug.Id, confirmed.Id]));

            var postTagsChanged = new TaskCompletionSource<CommunityForumPostChangedEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            memberHub.On<CommunityForumPostChangedEvent>(CommunityForumHubContract.PostChanged, value =>
            {
                if (value.PostId == memberPost.Id && value.Change == "tags-updated") postTagsChanged.TrySetResult(value);
            });
            await owner.UpdateForumPostTagsAsync(community.Id, forum.Id, memberPost.Id, [bug.Id, confirmed.Id]);
            Assert.Contains((await postTagsChanged.Task.WaitAsync(TimeSpan.FromSeconds(15))).Post!.Tags!,
                value => value.Id == confirmed.Id);
            var preserved = await member.UpdateForumPostTagsAsync(community.Id, forum.Id, memberPost.Id, [feature.Id]);
            Assert.Equal([feature.Id, confirmed.Id], preserved.Tags!.Select(value => value.Id));

            await owner.SetPermissionOverwriteAsync(community.Id, PermissionOverwriteScopeType.Channel, forum.Id,
                new(PermissionOverwriteTargetType.Everyone, null, CommunityPermission.ManageMessages,
                    CommunityPermission.None));
            var moderatorEdited = await member.UpdateForumPostTagsAsync(community.Id, forum.Id, moderatedPost.Id,
                [bug.Id]);
            Assert.Equal([bug.Id], moderatorEdited.Tags!.Select(value => value.Id));

            var tag4 = await owner.CreateForumTagAsync(community.Id, forum.Id, new("One"));
            var tag5 = await owner.CreateForumTagAsync(community.Id, forum.Id, new("Two"));
            var tag6 = await owner.CreateForumTagAsync(community.Id, forum.Id, new("Three"));
            var overLimit = await Assert.ThrowsAsync<NodeApiException>(() => owner.UpdateForumPostTagsAsync(
                community.Id, forum.Id, memberPost.Id, [bug.Id, feature.Id, tag4.Id, tag5.Id, tag6.Id, confirmed.Id]));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, overLimit.StatusCode);

            var anyTag = await owner.QueryForumPostsAsync(community.Id, forum.Id, null, [bug.Id, feature.Id]);
            Assert.Contains(anyTag.Posts, value => value.Id == memberPost.Id);
            Assert.Contains(anyTag.Posts, value => value.Id == moderatedPost.Id);
            var searchAndTag = await owner.QueryForumPostsAsync(community.Id, forum.Id, "launch", [feature.Id]);
            Assert.Equal([memberPost.Id], searchAndTag.Posts.Select(value => value.Id));

            await owner.DeleteForumTagAsync(community.Id, forum.Id, feature.Id);
            var surviving = await owner.GetForumPostAsync(community.Id, forum.Id, memberPost.Id);
            Assert.Equal([confirmed.Id], surviving.Tags!.Select(value => value.Id));
            Assert.Equal("Crash on launch", surviving.Title);

            var renamed = await owner.UpdateForumTagAsync(community.Id, forum.Id, bug.Id,
                new("Defect", ReactionEmojiKind.Standard, "🐛"));
            Assert.Equal("Defect", renamed.Name);
            Assert.Equal("Defect", (await owner.GetForumPostAsync(community.Id, forum.Id, moderatedPost.Id))
                .Tags!.Single().Name);
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
    public void ForumTagFilteringUsesDiscordUnionSemanticsAndComposesWithSearch()
    {
        var bug = new CommunityForumTagDto(Guid.NewGuid(), Guid.NewGuid(), "Bug");
        var feature = new CommunityForumTagDto(Guid.NewGuid(), bug.ChannelId, "Feature", SortOrder: 1);
        var first = Post("Crash", "Startup issue", "Skye") with { Tags = [bug] };
        var second = Post("Suggestion", "Rendering feature", "Entity") with { Tags = [feature] };
        var third = Post("Both", "Crash feature", "User") with { Tags = [bug, feature] };

        Assert.Equal([first.Id, third.Id], CommunityForumPostSearch.Filter([first, second, third], null, [bug.Id])
            .Select(value => value.Id));
        Assert.Equal([first.Id, second.Id, third.Id], CommunityForumPostSearch.Filter(
            [first, second, third], null, [bug.Id, feature.Id]).Select(value => value.Id));
        Assert.Equal([third.Id], CommunityForumPostSearch.Filter([first, second, third], "feature", [bug.Id])
            .Select(value => value.Id));
    }

    [Fact]
    public void ForumTagUiAndQueryContractsCoverPickerChipsAccessibilityRealtimeAndBoundedLoading()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var picker = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumTagPicker.razor"));
        var forum = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumChannelView.razor"));
        var browser = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumPostBrowser.razor"));
        var endpoint = File.ReadAllText(Path.Combine(root, "Iridium.Server", "Api", "CommunityForumEndpoints.cs"));

        Assert.Contains("role=\"checkbox\"", picker);
        Assert.Contains("aria-checked", picker);
        Assert.Contains("disabled=\"@disabled\"", picker);
        Assert.Contains("ForumTagPicker", forum);
        Assert.Contains("Edit tags", forum);
        Assert.Contains("ForumTagChip", browser);
        Assert.Contains("TagFilterChanged", browser);
        Assert.Contains("LoadPostTagsAsync(posts.Select", endpoint);
        Assert.DoesNotContain("foreach (var post in posts)\n            await LoadPostTagsAsync", endpoint);
    }

    [Fact]
    public void ForumTagInteractionKeepsSelectedHoverAndOnlyRendersRealDraftChecks()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var chipStyles = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumTagChip.razor.css"));
        var browserStyles = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumPostBrowser.razor.css"));
        var picker = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumTagPicker.razor"));
        var forum = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumChannelView.razor"));

        Assert.Contains(".forum-tag-chip.interactive.selected:hover", chipStyles);
        Assert.Contains(".filter-chip-button:hover .all-filter-chip.selected", browserStyles);
        Assert.Contains("@if (selected) { <Icon Name=\"check\" /> }", picker);
        Assert.DoesNotContain("opacity:0", File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumTagPicker.razor.css")));
        Assert.Contains("Selected.Contains(tag.Id)", picker);
        Assert.Contains("if (!selected.Remove(id))", picker);
        Assert.Contains("(_selectedPost.Tags ?? []).Select(value => value.Id).ToArray()", forum);
        Assert.Contains("CancelEditTags() { _editingTags = false; _editTagIds = []; }", forum);
    }

    [Fact]
    public void ForumTagPickerReusesSharedClickAwayAndEscapeDismissalWithoutClosingOnSelection()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var picker = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumTagPicker.razor"));
        var dismissal = File.ReadAllText(Path.Combine(root, "Iridium.Web", "wwwroot", "js", "emojiPicker.js"));

        Assert.Contains("@ref=\"_trigger\"", picker);
        Assert.Contains("@ref=\"_popover\"", picker);
        Assert.Contains("wireDismiss", picker);
        Assert.Contains("disposeDismiss", picker);
        Assert.Contains("[JSInvokable]", picker);
        Assert.Contains("public Task DismissAsync()", picker);
        Assert.Contains("if (_open) await ClosePopupAsync()", picker);
        Assert.DoesNotContain("ClosePopupAsync();\n        await SelectedTagIdsChanged", picker);
        Assert.Contains("!element.contains(event.target) && !anchor?.contains(event.target)", dismissal);
        Assert.Contains("event.key === \"Escape\"", dismissal);
        Assert.Contains("document.removeEventListener(\"pointerdown\"", dismissal);
    }

    [Fact]
    public void ForumPostContextMenuUsesCustomViewportSafePermissionGatedDesktopAndMobileActions()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var card = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumPostCard.razor"));
        var forum = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumChannelView.razor"));
        var menu = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumPostContextMenu.razor"));
        var styles = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumPostContextMenu.razor.css"));
        var javascript = File.ReadAllText(Path.Combine(root, "Iridium.Web", "wwwroot", "js", "chat.js"));

        Assert.Contains("@oncontextmenu:preventDefault=\"true\"", card);
        Assert.Contains("ForumPostContextMenu", forum);
        Assert.Contains("CanManage=\"CanManageMessages\"", forum);
        Assert.Contains("CanEditPost(context.Post)", forum);
        Assert.Contains("role=\"menu\"", menu);
        Assert.Contains("args.Key == \"Escape\"", menu);
        Assert.Contains("class=\"destructive\"", menu);
        Assert.Contains("<Icon Name=\"edit\"", menu);
        Assert.Contains("<Icon Name=\"tag\"", menu);
        Assert.Contains("clamp(", styles);
        Assert.Contains("@media(max-width:860px)", styles);
        Assert.Contains("wireForumPostLongPress", javascript);
        Assert.Contains("ignoreSelector", javascript);
        Assert.Contains("unwireForumPostLongPress", javascript);
    }

    [Fact]
    public void ForumTagSettingsUseSharedPolishedControlsAcrossSettingsSurfaces()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var settings = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumTagSettings.razor"));
        var styles = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumTagSettings.razor.css"));
        var compactDialog = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ChannelSettingsDialog.razor"));
        var chip = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumTagChip.razor"));
        var browser = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumPostBrowser.razor"));
        var forum = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumChannelView.razor"));

        Assert.Contains("<Icon Name=\"plus\"", settings);
        Assert.Equal(2, settings.Split("role=\"switch\"").Length - 1);
        Assert.Contains("aria-checked", settings);
        Assert.Contains("settings-button primary", settings);
        Assert.Contains("settings-button secondary", settings);
        Assert.Contains("<EmojiPicker", settings);
        Assert.Contains("AllowExternalEmoji=\"AllowExternalEmoji\"", settings);
        Assert.Contains("aria-label=\"@EmojiButtonLabel\"", settings);
        Assert.Contains("Remove tag emoji", settings);
        Assert.DoesNotContain("<select", settings);
        Assert.DoesNotContain("type=\"checkbox\"", settings);
        Assert.Contains("appearance: none", styles);
        Assert.Contains(":focus-visible", styles);
        Assert.Contains("@media (max-width: 520px)", styles);
        Assert.Contains("<StandardEmojiArtwork", chip);
        Assert.Contains("<CommunityEmojiImage", chip);
        Assert.Contains("Selected=\"selected\"", browser);
        Assert.Contains("aria-pressed", browser);
        Assert.Contains("\"Edit title\"", forum);
        Assert.Contains("aria-label=\"Edit tags\"", forum);
        Assert.Contains("<Icon Name=\"tag\"", forum);
        Assert.Contains("<ForumTagSettings", compactDialog);
        Assert.DoesNotContain("class=\"forum-tag-settings\"", compactDialog);
    }

    [Fact]
    public void ForumTagEmojiEditorKeepsCanonicalSelectionAndArtworkAcrossEverySurface()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var settings = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumTagSettings.razor"));
        var chip = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumTagChip.razor"));
        var picker = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "EmojiPicker.razor"));
        var browser = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumPostBrowser.razor"));
        var forum = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumChannelView.razor"));
        var tagPicker = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumTagPicker.razor"));
        var emojiService = File.ReadAllText(Path.Combine(root, "Iridium.Client.Core", "CommunityEmojiService.cs"));

        Assert.Contains("private EmojiSelection? _selectedEmoji", settings);
        Assert.Contains("_selectedEmoji = selection", settings);
        Assert.Contains("_selectedEmoji = null", settings);
        Assert.DoesNotContain("customId.ToString()", settings);
        Assert.DoesNotContain("Guid.TryParse(_custom", settings);
        Assert.Contains("[Parameter, EditorRequired] public required CommunityDto Community", settings);
        Assert.Contains("Community=\"Community\"", settings);
        Assert.DoesNotContain("CurrentCommunity", settings);
        Assert.Contains("Emojis.GetAvailableAsync(Community)", settings);
        Assert.Contains("AllowExternalEmoji=\"AllowExternalEmoji\"", settings);
        Assert.Contains("value.Community.Id == Community?.Id", picker);
        Assert.Contains("Emojis.GetAvailableAsync(Community)", picker);
        Assert.Contains("requiredCommunity", emojiService);
        Assert.Contains("<StandardEmojiArtwork ArtworkKey=\"@standardEmoji.ArtworkKey\" Glyph=\"@standardEmoji.Glyph\"", settings);
        Assert.Contains("<CommunityEmojiImage Emoji=\"customEmoji\"", settings);
        Assert.Contains("Unavailable emoji", settings);
        Assert.Contains("ArtworkKey=\"@_standard.ArtworkKey\"", chip);
        Assert.Contains("Glyph=\"@_standard.Glyph\"", chip);
        Assert.DoesNotContain("Glyph=\"_standard", chip);
        Assert.Contains("<CommunityEmojiImage", chip);
        Assert.Contains("<ForumTagChip", browser);
        Assert.Contains("<ForumTagChip", forum);
        Assert.Contains("<ForumTagChip", tagPicker);
    }

    [Fact]
    public void ForumSettingsPassAuthoritativeCommunityAndCardsPlaceTagsAboveTitles()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var permissions = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "CommunityPermissionEditor.razor"));
        var settingsDialog = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ChannelSettingsDialog.razor"));
        var sidebar = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "CommunitySidebar.razor"));
        var card = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumPostCard.razor"));
        var cardStyles = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumPostCard.razor.css"));
        var forum = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ForumChannelView.razor"));

        Assert.Contains("Community=\"Community\"", permissions);
        Assert.Contains("Community=\"Community\"", sidebar);
        Assert.Contains("Community=\"Community\"", settingsDialog);
        Assert.True(card.IndexOf("class=\"forum-post-tags\"", StringComparison.Ordinal) <
                    card.IndexOf("class=\"forum-post-title\"", StringComparison.Ordinal));
        Assert.Contains("@if (Post.Tags is { Count: > 0 })", card);
        Assert.Contains("flex-wrap:wrap", cardStyles);
        Assert.True(forum.IndexOf("class=\"forum-header-tags\"", StringComparison.Ordinal) <
                    forum.IndexOf("<strong>@selectedPost.Title</strong>", StringComparison.Ordinal));
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
        var prebuiltServer = Environment.GetEnvironmentVariable("IRIDIUM_TEST_SERVER_PATH");
        if (!string.IsNullOrWhiteSpace(prebuiltServer)) start.ArgumentList.Add(prebuiltServer);
        else
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
