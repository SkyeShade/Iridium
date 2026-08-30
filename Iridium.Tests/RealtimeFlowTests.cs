using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;

namespace Iridium.Tests;

public sealed class RealtimeFlowTests
{
    [Fact(Timeout = 30_000)]
    public async Task CleanNodeSupportsChannelOrganizationRealtimeMessagingAndScopedAuthorization()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var serverProject = Path.Combine(repoRoot, "Iridium.Server", "Iridium.Server.csproj");
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"iridium-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var databasePath = Path.Combine(tempDirectory, "flow.db");
        var port = FreePort();
        var nodeAddress = new Uri($"http://127.0.0.1:{port}/");
        var buildConfiguration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";

        using var server = StartServer(serverProject, nodeAddress, databasePath, buildConfiguration);
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();
        try
        {
            await WaitForServerAsync(nodeAddress, server, output, error);
            using (var http = new HttpClient { BaseAddress = nodeAddress })
            {
                using var openApi = await http.GetAsync("openapi/v1.json");
                Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
                using var unauthenticatedSearch = await http.GetAsync("api/accounts/search?q=sky");
                Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedSearch.StatusCode);
            }

            var owner = new NodeClient(nodeAddress);
            var ownerAuth = await owner.RegisterAsync(new RegisterAccountRequest("owner", "Owner", "test-password"));
            var secondOwnerSession = new NodeClient(nodeAddress);
            var secondOwnerAuth = await secondOwnerSession.LoginAsync(new LoginRequest("owner", "test-password"));
            var outsider = new NodeClient(nodeAddress);
            var outsiderAuth = await outsider.RegisterAsync(new RegisterAccountRequest("outsider", "Outsider", "test-password"));
            var intruder = new NodeClient(nodeAddress);
            var intruderAuth = await intruder.RegisterAsync(new RegisterAccountRequest("intruder", "Intruder", "test-password"));

            var needle = new NodeClient(nodeAddress);
            await needle.RegisterAsync(new RegisterAccountRequest("needle", "Exact", "test-password"));
            var needlePrefix = new NodeClient(nodeAddress);
            await needlePrefix.RegisterAsync(new RegisterAccountRequest("needle-prefix", "Prefix", "test-password"));
            var displayPrefix = new NodeClient(nodeAddress);
            await displayPrefix.RegisterAsync(new RegisterAccountRequest("alpha-search", "Needle Display", "test-password"));
            var usernameContains = new NodeClient(nodeAddress);
            await usernameContains.RegisterAsync(new RegisterAccountRequest("hasneedle", "Contains", "test-password"));
            var displayContains = new NodeClient(nodeAddress);
            await displayContains.RegisterAsync(new RegisterAccountRequest("omega-search", "Has Needle Inside", "test-password"));
            var sixthMatch = new NodeClient(nodeAddress);
            await sixthMatch.RegisterAsync(new RegisterAccountRequest("xneedle", "Sixth", "test-password"));

            var rankedSearch = await owner.SearchAccountsAsync("NEEDLE");
            Assert.Equal(5, rankedSearch.Count);
            Assert.Equal("needle", rankedSearch[0].Username);
            Assert.True(rankedSearch.FindIndex(value => value.Username == "needle-prefix") <
                        rankedSearch.FindIndex(value => value.Username == "alpha-search"));
            Assert.True(rankedSearch.FindIndex(value => value.Username == "alpha-search") <
                        rankedSearch.FindIndex(value => value.Username == "hasneedle"));
            Assert.Empty(await owner.SearchAccountsAsync("owner"));

            var searchRequest = await owner.SendFriendRequestAsync("needle");
            var outgoingSearch = (await owner.SearchAccountsAsync("needle")).Single(value => value.Username == "needle");
            Assert.Equal(ProfileRelationshipStatus.OutgoingPending, outgoingSearch.Relationship);
            Assert.Equal(searchRequest.FriendshipId, outgoingSearch.FriendshipId);
            await owner.RemoveFriendshipAsync(searchRequest.FriendshipId);

            var incomingRequest = await needle.SendFriendRequestAsync("owner");
            var incomingSearch = (await owner.SearchAccountsAsync("needle")).Single(value => value.Username == "needle");
            Assert.Equal(ProfileRelationshipStatus.IncomingPending, incomingSearch.Relationship);
            Assert.Equal(incomingRequest.FriendshipId, incomingSearch.FriendshipId);
            await owner.RemoveFriendshipAsync(incomingRequest.FriendshipId);

            var selfProfile = await owner.ResolveProfileAsync("owner");
            Assert.Equal(ProfileRelationshipStatus.Self, selfProfile.Relationship);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await Assert.ThrowsAsync<NodeApiException>(() => owner.SendFriendRequestAsync("owner"))).StatusCode);
            var outgoingFriendRequest = await owner.SendFriendRequestAsync("outsider");
            Assert.Equal(FriendshipStatus.Pending, outgoingFriendRequest.Status);
            var reverseRequest = await outsider.SendFriendRequestAsync("owner");
            Assert.Equal(FriendshipStatus.Accepted, reverseRequest.Status);
            Assert.Equal(FriendshipStatus.Accepted, Assert.Single(await owner.GetFriendsAsync()).Status);
            Assert.Equal(FriendshipStatus.Accepted, Assert.Single(await outsider.GetFriendsAsync()).Status);
            Assert.Empty(await owner.SearchAccountsAsync("outsider"));
            Assert.Equal(HttpStatusCode.Conflict,
                (await Assert.ThrowsAsync<NodeApiException>(() => owner.SendFriendRequestAsync("outsider"))).StatusCode);

            var directConversation = await owner.OpenDirectConversationAsync(outsiderAuth.Account.Id);
            Assert.Empty(await owner.GetDirectConversationsAsync());
            Assert.Empty(await outsider.GetDirectConversationsAsync());

            var communityA = await owner.CreateCommunityAsync(new CreateCommunityRequest("Alpha", null));
            var initialManagement = await owner.GetCommunityManagementAsync(communityA.Id);
            Assert.Empty(initialManagement.Invites);
            Assert.Empty(initialManagement.Bans);
            var ownerMembership = Assert.Single(initialManagement.Members);
            Assert.Equal(ownerAuth.Account.Id, ownerMembership.AccountId);
            Assert.True(ownerMembership.IsOwner);
            var initialRole = Assert.Single(initialManagement.Roles);
            Assert.True(initialRole.IsDefault);
            Assert.Equal("@everyone", initialRole.Name);
            var defaults = await owner.GetCommunityStructureAsync(communityA.Id);
            var defaultCategory = Assert.Single(defaults.Categories);
            Assert.Equal("TEXT CHANNELS", defaultCategory.Name);
            var defaultChannel = Assert.Single(defaults.Channels);
            Assert.Equal("general", defaultChannel.Name);
            Assert.Equal(CommunityChannelKind.Text, defaultChannel.Kind);
            Assert.Equal(defaultCategory.Id, defaultChannel.CategoryId);
            Assert.True(defaultChannel.PermissionsSyncedToCategory);

            await using (var failureConnection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await failureConnection.OpenAsync();
                await using var createFailureTrigger = failureConnection.CreateCommand();
                createFailureTrigger.CommandText = """
                    CREATE TRIGGER FailCommunityGeneral
                    BEFORE INSERT ON CommunityChannels
                    WHEN NEW.Name = 'general'
                    BEGIN
                        SELECT RAISE(ABORT, 'forced community creation failure');
                    END;
                    """;
                await createFailureTrigger.ExecuteNonQueryAsync();
            }

            try
            {
                var failedCreation = await Assert.ThrowsAsync<NodeApiException>(() =>
                    intruder.CreateCommunityAsync(new CreateCommunityRequest("Rollback Check", null)));
                Assert.Equal(HttpStatusCode.InternalServerError, failedCreation.StatusCode);
            }
            finally
            {
                await using var failureConnection = new SqliteConnection($"Data Source={databasePath}");
                await failureConnection.OpenAsync();
                await using var dropFailureTrigger = failureConnection.CreateCommand();
                dropFailureTrigger.CommandText = "DROP TRIGGER IF EXISTS FailCommunityGeneral;";
                await dropFailureTrigger.ExecuteNonQueryAsync();
            }

            await using (var verificationConnection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await verificationConnection.OpenAsync();
                await using var verifyRollback = verificationConnection.CreateCommand();
                verifyRollback.CommandText = "SELECT COUNT(*) FROM Communities WHERE OwnerAccountId = $ownerId OR Name = 'Rollback Check';";
                verifyRollback.Parameters.AddWithValue("$ownerId", intruderAuth.Account.Id.ToString());
                Assert.Equal(0L, (long)(await verifyRollback.ExecuteScalarAsync())!);
            }
            var loadedAgain = await owner.GetCommunityStructureAsync(communityA.Id);
            Assert.Equal(defaultCategory.Id, Assert.Single(loadedAgain.Categories).Id);
            Assert.Equal(defaultChannel.Id, Assert.Single(loadedAgain.Channels).Id);
            await owner.SetPermissionOverwriteAsync(communityA.Id, PermissionOverwriteScopeType.Channel,
                defaultChannel.Id, new(PermissionOverwriteTargetType.Everyone, null,
                    CommunityPermission.None, CommunityPermission.AddReactions));
            Assert.False((await owner.GetPermissionScopeAsync(communityA.Id,
                PermissionOverwriteScopeType.Channel, defaultChannel.Id)).PermissionsSyncedToCategory);
            await owner.SyncChannelPermissionsAsync(communityA.Id, defaultChannel.Id);
            var resyncedDefault = await owner.GetPermissionScopeAsync(communityA.Id,
                PermissionOverwriteScopeType.Channel, defaultChannel.Id);
            Assert.True(resyncedDefault.PermissionsSyncedToCategory);
            Assert.Empty(resyncedDefault.Overwrites);
            var membershipInvite = await owner.CreateCommunityInviteAsync(communityA.Id, new(null, null));
            var membershipToken = CommunityInviteLink.Find(membershipInvite.InviteUrl!)?.Token;
            Assert.NotNull(membershipToken);
            await outsider.JoinCommunityInviteAsync(membershipToken);

            var channelManagerRole = await owner.CreateCommunityRoleAsync(communityA.Id,
                new("Channel Manager", CommunityPermission.ManageChannels, "#4F8EF7"));
            var neutralOverwriteSave = await owner.ReplacePermissionOverwritesAsync(communityA.Id,
                PermissionOverwriteScopeType.Channel, defaultChannel.Id, new([
                    new(PermissionOverwriteTargetType.Role, channelManagerRole.Id,
                        CommunityPermission.None, CommunityPermission.None)
                ]));
            Assert.True(neutralOverwriteSave.Revision > 0);
            Assert.Contains(neutralOverwriteSave.Scope.Overwrites, value =>
                value.TargetType == PermissionOverwriteTargetType.Role && value.TargetId == channelManagerRole.Id &&
                value.Allow == CommunityPermission.None && value.Deny == CommunityPermission.None);
            var editedOverwriteSave = await owner.ReplacePermissionOverwritesAsync(communityA.Id,
                PermissionOverwriteScopeType.Channel, defaultChannel.Id, new([
                    new(PermissionOverwriteTargetType.Role, channelManagerRole.Id,
                        CommunityPermission.AddReactions, CommunityPermission.None)
                ]));
            Assert.True(editedOverwriteSave.Revision > neutralOverwriteSave.Revision);
            Assert.Equal(CommunityPermission.AddReactions,
                Assert.Single(editedOverwriteSave.Scope.Overwrites).Allow);
            var removedOverwriteSave = await owner.ReplacePermissionOverwritesAsync(communityA.Id,
                PermissionOverwriteScopeType.Channel, defaultChannel.Id, new([]));
            Assert.True(removedOverwriteSave.Revision > editedOverwriteSave.Revision);
            Assert.Empty(removedOverwriteSave.Scope.Overwrites);
            await owner.SetCommunityMemberRolesAsync(communityA.Id, outsiderAuth.Account.Id, [channelManagerRole.Id]);
            await outsider.UpdateChannelAsync(communityA.Id, defaultChannel.Id, "general-managed", defaultCategory.Id);
            Assert.Equal(HttpStatusCode.Forbidden, (await Assert.ThrowsAsync<NodeApiException>(() =>
                outsider.ReplacePermissionOverwritesAsync(communityA.Id, PermissionOverwriteScopeType.Channel,
                    defaultChannel.Id, new([])))).StatusCode);

            var permissionManagerRole = await owner.CreateCommunityRoleAsync(communityA.Id,
                new("Permission Manager", CommunityPermission.ManagePermissions, "#45B97C"));
            await owner.SetCommunityMemberRolesAsync(communityA.Id, outsiderAuth.Account.Id, [permissionManagerRole.Id]);
            Assert.Equal(HttpStatusCode.Forbidden, (await Assert.ThrowsAsync<NodeApiException>(() =>
                outsider.UpdateChannelAsync(communityA.Id, defaultChannel.Id, "should-not-save", defaultCategory.Id))).StatusCode);
            await outsider.ReplacePermissionOverwritesAsync(communityA.Id, PermissionOverwriteScopeType.Channel,
                defaultChannel.Id, new([
                    new(PermissionOverwriteTargetType.Everyone, null, CommunityPermission.None,
                        CommunityPermission.AddReactions)
                ]));
            await owner.SetCommunityMemberRolesAsync(communityA.Id, outsiderAuth.Account.Id, [permissionManagerRole.Id]);
            await owner.SyncChannelPermissionsAsync(communityA.Id, defaultChannel.Id);
            await owner.UpdateChannelAsync(communityA.Id, defaultChannel.Id, "general", defaultCategory.Id);
            var embeddedChannel = await owner.UpdateChannelAsync(communityA.Id, defaultChannel.Id, "general",
                defaultCategory.Id, CommunityChannelKind.Text, embed: new(
                    CommunityChannelEmbedProvider.GoogleDocs,
                    "https://docs.google.com/document/d/abcdefghij/edit?tab=t.0"));
            Assert.Equal(CommunityChannelEmbedProvider.GoogleDocs, embeddedChannel.EmbedProvider);
            Assert.Equal("https://docs.google.com/document/d/abcdefghij/view", embeddedChannel.EmbedUrl);
            Assert.Equal(embeddedChannel.EmbedUrl, (await owner.GetCommunityStructureAsync(communityA.Id)).Channels
                .Single(value => value.Id == defaultChannel.Id).EmbedUrl);
            Assert.Equal(HttpStatusCode.BadRequest, (await Assert.ThrowsAsync<NodeApiException>(() =>
                owner.UpdateChannelAsync(communityA.Id, defaultChannel.Id, "general", defaultCategory.Id,
                    CommunityChannelKind.Text, embed: new(CommunityChannelEmbedProvider.GoogleDocs,
                        "https://example.com/document")))).StatusCode);
            var publishedChannel = await owner.UpdateChannelAsync(communityA.Id, defaultChannel.Id, "general",
                defaultCategory.Id, CommunityChannelKind.Text, embed: new(
                    CommunityChannelEmbedProvider.GoogleDocs,
                    "https://docs.google.com/document/d/e/2PACX-abcdefghij_123/pub?embedded=true"));
            Assert.Equal("https://docs.google.com/document/d/e/2PACX-abcdefghij_123/pub", publishedChannel.EmbedUrl);
            var clearedEmbed = await owner.UpdateChannelAsync(communityA.Id, defaultChannel.Id, "general",
                defaultCategory.Id, CommunityChannelKind.Text, embed: new(null, null));
            Assert.Null(clearedEmbed.EmbedProvider);
            Assert.Null(clearedEmbed.EmbedUrl);

            var firstCategory = await owner.CreateCategoryAsync(communityA.Id, "Rooms");
            var secondCategory = await owner.CreateCategoryAsync(communityA.Id, "Projects");
            var treeDepth2 = await owner.CreateCategoryAsync(communityA.Id, "Depth 2", firstCategory.Id);
            var treeDepth3 = await owner.CreateCategoryAsync(communityA.Id, "Depth 3", treeDepth2.Id);
            var treeDepth4 = await owner.CreateCategoryAsync(communityA.Id, "Depth 4", treeDepth3.Id);
            var treeDepth5 = await owner.CreateCategoryAsync(communityA.Id, "Depth 5", treeDepth4.Id);
            Assert.Equal(treeDepth4.Id, treeDepth5.ParentCategoryId);
            Assert.Equal(HttpStatusCode.BadRequest, (await Assert.ThrowsAsync<NodeApiException>(() =>
                owner.CreateCategoryAsync(communityA.Id, "Depth 6", treeDepth5.Id))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Assert.ThrowsAsync<NodeApiException>(() =>
                owner.MoveCategoryAsync(communityA.Id, firstCategory.Id, treeDepth5.Id, 0))).StatusCode);
            await owner.MoveCategoryAsync(communityA.Id, treeDepth5.Id, null, 0);
            Assert.Null((await owner.GetCommunityStructureAsync(communityA.Id)).Categories
                .Single(value => value.Id == treeDepth5.Id).ParentCategoryId);
            await owner.MoveCategoryAsync(communityA.Id, treeDepth5.Id, treeDepth4.Id, 0);
            // A subtree is moved as one node; its descendants retain their parent links.
            await owner.MoveCategoryAsync(communityA.Id, treeDepth2.Id, secondCategory.Id, 0);
            var movedSubtree = await owner.GetCommunityStructureAsync(communityA.Id);
            Assert.Equal(secondCategory.Id, movedSubtree.Categories.Single(value => value.Id == treeDepth2.Id).ParentCategoryId);
            Assert.Equal(treeDepth2.Id, movedSubtree.Categories.Single(value => value.Id == treeDepth3.Id).ParentCategoryId);
            await owner.MoveCategoryAsync(communityA.Id, treeDepth2.Id, firstCategory.Id, 0);

            var nestedA = await owner.CreateCategoryAsync(communityA.Id, "Nested A", firstCategory.Id);
            var nestedB = await owner.CreateCategoryAsync(communityA.Id, "Nested B", firstCategory.Id);
            var nestedC = await owner.CreateCategoryAsync(communityA.Id, "Nested C", firstCategory.Id);
            await owner.MoveCategoryAsync(communityA.Id, nestedC.Id, firstCategory.Id, 1);
            var nestedOrder = (await owner.GetCommunityStructureAsync(communityA.Id)).Categories
                .Where(value => value.ParentCategoryId == firstCategory.Id).OrderBy(value => value.Position).ToArray();
            Assert.Equal(Enumerable.Range(0, nestedOrder.Length), nestedOrder.Select(value => value.Position));
            Assert.True(Array.IndexOf(nestedOrder.Select(value => value.Id).ToArray(), nestedC.Id) <
                        Array.IndexOf(nestedOrder.Select(value => value.Id).ToArray(), nestedB.Id));

            await owner.UpdateCategoryAsync(communityA.Id, secondCategory.Id, "Topics");
            await owner.MoveCategoryAsync(communityA.Id, secondCategory.Id, null, 0);
            // Repeated and rapid moves must either serialize or report a conflict, never
            // leave duplicate/gapped sibling positions behind.
            for (var iteration = 0; iteration < 3; iteration++)
            {
                await owner.MoveCategoryAsync(communityA.Id, firstCategory.Id, null, 0);
                await owner.MoveCategoryAsync(communityA.Id, secondCategory.Id, null, 0);
            }
            var rapidMoves = await Task.WhenAll(
                CaptureAsync(() => owner.MoveCategoryAsync(communityA.Id, defaultCategory.Id, null, 0)),
                CaptureAsync(() => secondOwnerSession.MoveCategoryAsync(communityA.Id, firstCategory.Id, null, 0)));
            Assert.All(rapidMoves, exception => Assert.True(exception is null or NodeApiException
                { StatusCode: HttpStatusCode.Conflict }));
            var rootsAfterRapidMoves = (await owner.GetCommunityStructureAsync(communityA.Id)).Categories
                .Where(value => value.ParentCategoryId is null).OrderBy(value => value.Position).ToArray();
            Assert.Equal(Enumerable.Range(0, rootsAfterRapidMoves.Length), rootsAfterRapidMoves.Select(value => value.Position));
            Assert.Equal(rootsAfterRapidMoves.Length, rootsAfterRapidMoves.Select(value => value.Id).Distinct().Count());
            await owner.MoveCategoryAsync(communityA.Id, firstCategory.Id, null, int.MaxValue);
            await owner.MoveCategoryAsync(communityA.Id, defaultCategory.Id, null, 1);
            await owner.MoveCategoryAsync(communityA.Id, secondCategory.Id, null, 0);
            var welcome = await owner.CreateChannelAsync(communityA.Id, "welcome", defaultCategory.Id);
            await owner.MoveChannelAsync(communityA.Id, welcome.Id, defaultCategory.Id, 0);
            var tail = await owner.CreateChannelAsync(communityA.Id, "tail", defaultCategory.Id);
            var unified = await owner.GetCommunityStructureAsync(communityA.Id);
            Assert.Equal([secondCategory.Id, defaultCategory.Id, firstCategory.Id], unified.Categories
                .Where(value => value.ParentCategoryId is null).OrderBy(value => value.Position).Select(value => value.Id));
            Assert.Equal([welcome.Id, defaultChannel.Id, tail.Id], unified.Channels
                .Where(value => value.CategoryId == defaultCategory.Id).OrderBy(value => value.Position).Select(value => value.Id));
            var chatChannel = await owner.CreateChannelAsync(communityA.Id, "General Chat", firstCategory.Id);
            var rootVoice = await owner.CreateChannelAsync(communityA.Id, "Root Voice", null,
                CommunityChannelKind.Voice);
            var categoryVoice = await owner.CreateChannelAsync(communityA.Id, "Lounge", firstCategory.Id,
                CommunityChannelKind.Voice);
            var nestedVoice = await owner.CreateChannelAsync(communityA.Id, "Studio", nestedA.Id,
                CommunityChannelKind.Voice);
            Assert.All([rootVoice, categoryVoice, nestedVoice], value =>
                Assert.Equal(CommunityChannelKind.Voice, value.Kind));
            Assert.Null(rootVoice.CategoryId);
            Assert.Equal(firstCategory.Id, categoryVoice.CategoryId);
            Assert.Equal(nestedA.Id, nestedVoice.CategoryId);
            Assert.Equal(HttpStatusCode.Forbidden, (await Assert.ThrowsAsync<NodeApiException>(() =>
                outsider.CreateChannelAsync(communityA.Id, "not-allowed", null, CommunityChannelKind.Voice))).StatusCode);
            var disposable = await owner.CreateChannelAsync(communityA.Id, "Archive", firstCategory.Id);
            Assert.Equal(HttpStatusCode.Conflict, (await Assert.ThrowsAsync<NodeApiException>(() =>
                owner.DeleteCategoryAsync(communityA.Id, firstCategory.Id))).StatusCode);
            await owner.UpdateChannelAsync(communityA.Id, chatChannel.Id, "lounge", secondCategory.Id);
            await owner.MoveChannelAsync(communityA.Id, chatChannel.Id, secondCategory.Id, 0);
            await owner.MoveChannelAsync(communityA.Id, disposable.Id, secondCategory.Id, 1);
            Assert.Equal(HttpStatusCode.Conflict, (await Assert.ThrowsAsync<NodeApiException>(() =>
                owner.DeleteCategoryAsync(communityA.Id, firstCategory.Id))).StatusCode);
            var organized = await owner.GetCommunityStructureAsync(communityA.Id);
            Assert.Equal(secondCategory.Id, organized.Categories[0].Id);
            Assert.Equal("Topics", organized.Categories[0].Name);
            Assert.Equal(secondCategory.Id, organized.Channels.Single(value => value.Id == disposable.Id).CategoryId);
            Assert.Equal(secondCategory.Id, organized.Channels.Single(value => value.Id == chatChannel.Id).CategoryId);

            var rootA = await owner.CreateChannelAsync(communityA.Id, "root-a", null);
            var rootB = await owner.CreateChannelAsync(communityA.Id, "root-b", null);
            await owner.MoveChannelAsync(communityA.Id, rootA.Id, new(null, firstCategory.Id,
                CommunitySidebarItemType.Category, CommunitySidebarDropIntent.Before));
            var mixed = await owner.GetCommunityStructureAsync(communityA.Id);
            Assert.Null(mixed.Channels.Single(value => value.Id == rootA.Id).CategoryId);
            AssertMixedPositions(mixed, null);

            await owner.MoveChannelAsync(communityA.Id, rootA.Id, new(firstCategory.Id, firstCategory.Id,
                CommunitySidebarItemType.Category, CommunitySidebarDropIntent.InsideAtStart));
            mixed = await owner.GetCommunityStructureAsync(communityA.Id);
            Assert.Equal(firstCategory.Id, mixed.Channels.Single(value => value.Id == rootA.Id).CategoryId);
            Assert.Equal(0, mixed.Channels.Single(value => value.Id == rootA.Id).Position);
            AssertMixedPositions(mixed, firstCategory.Id);

            await owner.MoveChannelAsync(communityA.Id, rootA.Id, new(firstCategory.Id, null, null,
                CommunitySidebarDropIntent.End));
            mixed = await owner.GetCommunityStructureAsync(communityA.Id);
            Assert.Equal(firstCategory.Id, mixed.Channels.Single(value => value.Id == rootA.Id).CategoryId);
            Assert.Equal(mixed.Categories.Where(value => value.ParentCategoryId == firstCategory.Id).Select(value => value.Position)
                    .Concat(mixed.Channels.Where(value => value.CategoryId == firstCategory.Id).Select(value => value.Position)).Max(),
                mixed.Channels.Single(value => value.Id == rootA.Id).Position);

            await owner.MoveChannelAsync(communityA.Id, rootA.Id, new(null, firstCategory.Id,
                CommunitySidebarItemType.Category, CommunitySidebarDropIntent.After));
            await owner.MoveCategoryAsync(communityA.Id, secondCategory.Id, new(null, rootA.Id,
                CommunitySidebarItemType.Channel, CommunitySidebarDropIntent.Before));
            mixed = await owner.GetCommunityStructureAsync(communityA.Id);
            Assert.Null(mixed.Channels.Single(value => value.Id == rootA.Id).CategoryId);
            Assert.True(mixed.Categories.Single(value => value.Id == secondCategory.Id).Position <
                        mixed.Channels.Single(value => value.Id == rootA.Id).Position);
            AssertMixedPositions(mixed, null);

            await owner.MoveCategoryAsync(communityA.Id, secondCategory.Id, new(firstCategory.Id, firstCategory.Id,
                CommunitySidebarItemType.Category, CommunitySidebarDropIntent.InsideAtStart));
            mixed = await owner.GetCommunityStructureAsync(communityA.Id);
            Assert.Equal(firstCategory.Id, mixed.Categories.Single(value => value.Id == secondCategory.Id).ParentCategoryId);
            Assert.Equal(0, mixed.Categories.Single(value => value.Id == secondCategory.Id).Position);
            await owner.MoveCategoryAsync(communityA.Id, secondCategory.Id, new(firstCategory.Id, null, null,
                CommunitySidebarDropIntent.End));
            mixed = await owner.GetCommunityStructureAsync(communityA.Id);
            Assert.Equal(mixed.Categories.Where(value => value.ParentCategoryId == firstCategory.Id).Select(value => value.Position)
                    .Concat(mixed.Channels.Where(value => value.CategoryId == firstCategory.Id).Select(value => value.Position)).Max(),
                mixed.Categories.Single(value => value.Id == secondCategory.Id).Position);
            await owner.MoveCategoryAsync(communityA.Id, secondCategory.Id,
                new(null, rootB.Id, CommunitySidebarItemType.Channel, CommunitySidebarDropIntent.Before));
            await owner.MoveChannelAsync(communityA.Id, disposable.Id, secondCategory.Id, 0);
            Assert.Equal(disposable.Id, (await owner.GetCommunityStructureAsync(communityA.Id)).Channels
                .Where(value => value.CategoryId == secondCategory.Id).OrderBy(value => value.Position).First().Id);
            await owner.DeleteChannelAsync(communityA.Id, disposable.Id);
            Assert.DoesNotContain((await owner.GetCommunityStructureAsync(communityA.Id)).Channels, value => value.Id == disposable.Id);

            var communityB = await outsider.CreateCommunityAsync(new CreateCommunityRequest("Beta", null));
            var outsiderDefault = Assert.Single((await outsider.GetCommunityStructureAsync(communityB.Id)).Categories);
            var outsiderChannel = await outsider.CreateChannelAsync(communityB.Id, "private", outsiderDefault.Id);
            var forbiddenMove = await Assert.ThrowsAsync<NodeApiException>(() =>
                outsider.MoveChannelAsync(communityA.Id, chatChannel.Id, secondCategory.Id, 0));
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenMove.StatusCode);

            await using var firstConnection = Connection(nodeAddress, ownerAuth.AccessToken);
            await using var secondConnection = Connection(nodeAddress, secondOwnerAuth.AccessToken);
            await using var outsiderConnection = Connection(nodeAddress, outsiderAuth.AccessToken);
            await using var secondOutsiderConnection = Connection(nodeAddress, outsiderAuth.AccessToken);
            await using var intruderConnection = Connection(nodeAddress, intruderAuth.AccessToken);
            var createdOnSecond = Completion<ChannelMessageDto>();
            var secondCreatedOnSecond = Completion<ChannelMessageDto>();
            var thirdCreatedOnSecond = Completion<ChannelMessageDto>();
            var updatedOnSecond = Completion<ChannelMessageDto>();
            var replyOnFirst = Completion<ChannelMessageDto>();
            var secondCreatedOnFirst = Completion<ChannelMessageDto>();
            var thirdCreatedOnFirst = Completion<ChannelMessageDto>();
            var deletedOnSecond = Completion<ChannelMessageDeletedEvent>();
            var directOnOutsider = Completion<DirectMessageDto>();
            var secondDirectOnOutsider = Completion<DirectMessageDto>();
            var callStartedOnCaller = Completion<DirectMessageDto>();
            var callStartedOnCallee = Completion<DirectMessageDto>();
            var friendRequestOnOwner = Completion<FriendshipChangedEvent>();
            var friendAcceptedOnIntruder = Completion<FriendshipChangedEvent>();
            TaskCompletionSource<PresenceChangedEvent>? outsiderPresenceOnOwner = null;
            var mentionReceived = Completion<CommunityMentionReceivedEvent>();
            var incomingCall = Completion<IncomingCallEvent>();
            var incomingCallOnSecondCallee = Completion<IncomingCallEvent>();
            var answeredElsewhere = Completion<CallStateEvent>();
            var callAccepted = Completion<CallStateEvent>();
            var receivedOffer = Completion<WebRtcDescriptionEvent>();
            var receivedAnswer = Completion<WebRtcDescriptionEvent>();
            var receivedIce = Completion<WebRtcIceCandidateEvent>();
            var receivedCommunityOffer = Completion<CommunityVoiceMediaDescriptionEvent>();
            var receivedCommunityAnswer = Completion<CommunityVoiceMediaDescriptionEvent>();
            var receivedCommunityIce = Completion<CommunityVoiceMediaIceCandidateEvent>();
            var structureOnFirstOwnerTab = Completion<CommunityStateChangedEvent>();
            var structureOnSecondOwnerTab = Completion<CommunityStateChangedEvent>();
            var structureOnMember = Completion<CommunityStateChangedEvent>();
            var activeVoiceMovedOnMember = Completion<CommunityStateChangedEvent>();
            var permissionsChangedOnMember = Completion<CommunityStateChangedEvent>();
            var categoryCreatedOnMember = Completion<CommunityStateChangedEvent>();
            var categoryDeletedOnMember = Completion<CommunityStateChangedEvent>();
            var channelDeletedOnMember = Completion<CommunityStateChangedEvent>();
            var overwriteChangedOnMember = Completion<CommunityStateChangedEvent>();
            var intruderCommunityChangeCount = 0;
            var participantChanged = Completion<CallParticipantStateEvent>();
            var participantStateChangeCount = 0;
            var speakingChanged = Completion<CallParticipantSpeakingEvent>();
            var mediaRetryRequested = Completion<CallStateEvent>();
            var callEnded = Completion<CallStateEvent>();
            var mentionNotificationCount = 0;
            var answerDeliveryCount = 0;
            var answerDeliveryOnSecondCallerCount = 0;
            var acceptedOnSecondCallerCount = 0;
            var offerDeliveryOnSecondCalleeCount = 0;
            var iceDeliveryOnSecondCalleeCount = 0;
            secondConnection.On<ChannelMessageDto>(ChatHubContract.MessageCreated, message =>
            {
                if (message.Content == "hello from tab one") createdOnSecond.TrySetResult(message);
                if (message.Content == "second from tab one") secondCreatedOnSecond.TrySetResult(message);
                if (message.Content == "third from tab one") thirdCreatedOnSecond.TrySetResult(message);
            });
            secondConnection.On<ChannelMessageDto>(ChatHubContract.MessageUpdated, message =>
            {
                if (message.Content == "edited hello") updatedOnSecond.TrySetResult(message);
            });
            firstConnection.On<ChannelMessageDto>(ChatHubContract.MessageCreated, message =>
            {
                if (message.Content == "a reply") replyOnFirst.TrySetResult(message);
                if (message.Content == "second from tab two") secondCreatedOnFirst.TrySetResult(message);
                if (message.Content == "third from tab two") thirdCreatedOnFirst.TrySetResult(message);
            });
            secondConnection.On<ChannelMessageDeletedEvent>(ChatHubContract.MessageDeleted, message => deletedOnSecond.TrySetResult(message));
            outsiderConnection.On<DirectMessageDto>(DirectMessageHubContract.MessageCreated, message =>
            {
                if (message.Content == "private hello") directOnOutsider.TrySetResult(message);
                if (message.Content == "private again") secondDirectOnOutsider.TrySetResult(message);
                if (message.Kind == MessageKind.CallStarted) callStartedOnCallee.TrySetResult(message);
            });
            firstConnection.On<DirectMessageDto>(DirectMessageHubContract.MessageCreated, message =>
            {
                if (message.Kind == MessageKind.CallStarted) callStartedOnCaller.TrySetResult(message);
            });
            firstConnection.On<FriendshipChangedEvent>(FriendshipHubContract.RequestReceived,
                change => friendRequestOnOwner.TrySetResult(change));
            intruderConnection.On<FriendshipChangedEvent>(FriendshipHubContract.RequestAccepted,
                change => friendAcceptedOnIntruder.TrySetResult(change));
            firstConnection.On<PresenceChangedEvent>(PresenceHubContract.PresenceChanged, change =>
            {
                if (change.AccountId == outsiderAuth.Account.Id) outsiderPresenceOnOwner?.TrySetResult(change);
            });
            outsiderConnection.On<CommunityMentionReceivedEvent>(CommunityMentionHubContract.Received, mention =>
            {
                Interlocked.Increment(ref mentionNotificationCount);
                mentionReceived.TrySetResult(mention);
            });
            outsiderConnection.On<IncomingCallEvent>(VoiceCallHubContract.Incoming, value => incomingCall.TrySetResult(value));
            secondOutsiderConnection.On<IncomingCallEvent>(VoiceCallHubContract.Incoming,
                value => incomingCallOnSecondCallee.TrySetResult(value));
            secondOutsiderConnection.On<CallStateEvent>(VoiceCallHubContract.Cancelled,
                value => answeredElsewhere.TrySetResult(value));
            firstConnection.On<CallStateEvent>(VoiceCallHubContract.Accepted, value => callAccepted.TrySetResult(value));
            secondConnection.On<CallStateEvent>(VoiceCallHubContract.Accepted,
                _ => Interlocked.Increment(ref acceptedOnSecondCallerCount));
            outsiderConnection.On<WebRtcDescriptionEvent>(VoiceCallHubContract.Offer, value => receivedOffer.TrySetResult(value));
            secondOutsiderConnection.On<WebRtcDescriptionEvent>(VoiceCallHubContract.Offer,
                _ => Interlocked.Increment(ref offerDeliveryOnSecondCalleeCount));
            firstConnection.On<WebRtcDescriptionEvent>(VoiceCallHubContract.Answer, value =>
            {
                Interlocked.Increment(ref answerDeliveryCount);
                receivedAnswer.TrySetResult(value);
            });
            secondConnection.On<WebRtcDescriptionEvent>(VoiceCallHubContract.Answer,
                _ => Interlocked.Increment(ref answerDeliveryOnSecondCallerCount));
            outsiderConnection.On<WebRtcIceCandidateEvent>(VoiceCallHubContract.IceCandidate, value => receivedIce.TrySetResult(value));
            secondOutsiderConnection.On<WebRtcIceCandidateEvent>(VoiceCallHubContract.IceCandidate,
                _ => Interlocked.Increment(ref iceDeliveryOnSecondCalleeCount));
            outsiderConnection.On<CallParticipantStateEvent>(VoiceCallHubContract.ParticipantStateChanged,
                value => { Interlocked.Increment(ref participantStateChangeCount); participantChanged.TrySetResult(value); });
            outsiderConnection.On<CallParticipantSpeakingEvent>(VoiceCallHubContract.ParticipantSpeakingChanged,
                value => speakingChanged.TrySetResult(value));
            firstConnection.On<CallStateEvent>(VoiceCallHubContract.MediaRetryRequested,
                value => mediaRetryRequested.TrySetResult(value));
            outsiderConnection.On<CallStateEvent>(VoiceCallHubContract.Ended, value => callEnded.TrySetResult(value));
            secondConnection.On<CommunityVoiceMediaDescriptionEvent>(CommunityVoiceHubContract.MediaOffer,
                value => receivedCommunityOffer.TrySetResult(value));
            firstConnection.On<CommunityVoiceMediaDescriptionEvent>(CommunityVoiceHubContract.MediaAnswer,
                value => receivedCommunityAnswer.TrySetResult(value));
            secondConnection.On<CommunityVoiceMediaIceCandidateEvent>(CommunityVoiceHubContract.MediaIceCandidate,
                value => receivedCommunityIce.TrySetResult(value));
            firstConnection.On<CommunityStateChangedEvent>(CommunityHubContract.StateChanged, value =>
            {
                if (value.CommunityId == communityA.Id && value.Change == "channel-created")
                    structureOnFirstOwnerTab.TrySetResult(value);
            });
            secondConnection.On<CommunityStateChangedEvent>(CommunityHubContract.StateChanged, value =>
            {
                if (value.CommunityId == communityA.Id && value.Change == "channel-created")
                    structureOnSecondOwnerTab.TrySetResult(value);
            });
            outsiderConnection.On<CommunityStateChangedEvent>(CommunityHubContract.StateChanged, value =>
            {
                if (value.CommunityId != communityA.Id) return;
                if (value.Change == "channel-created") structureOnMember.TrySetResult(value);
                if (value.Change == "channel-moved") activeVoiceMovedOnMember.TrySetResult(value);
                if (value.Change == "role-updated") permissionsChangedOnMember.TrySetResult(value);
                if (value.Change == "category-created") categoryCreatedOnMember.TrySetResult(value);
                if (value.Change == "category-deleted") categoryDeletedOnMember.TrySetResult(value);
                if (value.Change == "channel-deleted") channelDeletedOnMember.TrySetResult(value);
                if (value.Change == "permissions-updated") overwriteChangedOnMember.TrySetResult(value);
            });
            intruderConnection.On<CommunityStateChangedEvent>(CommunityHubContract.StateChanged, _ =>
                Interlocked.Increment(ref intruderCommunityChangeCount));

            await Task.WhenAll(firstConnection.StartAsync(), secondConnection.StartAsync(), outsiderConnection.StartAsync(),
                secondOutsiderConnection.StartAsync(), intruderConnection.StartAsync());

            var syncedSession = new NodeSession(new MemoryAccountStore(), new MemorySelectionStore(),
                new EmptyLegacyTokenStore());
            syncedSession.BeginAuthentication(new SavedNode(nodeAddress.ToString(), "Realtime client", true));
            var syncedActivation = await syncedSession.PrepareAuthenticationAsync(outsiderAuth);
            await syncedSession.AcceptAuthenticationAsync(syncedActivation);
            using var syncedCommunity = new CommunitySession(syncedSession);
            await using var syncedRealtime = new RealtimeConnectionService(syncedSession,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RealtimeConnectionService>.Instance);
            await using var syncedMessaging = new ChannelMessagingSession(syncedSession, syncedRealtime,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ChannelMessagingSession>.Instance);
            await syncedMessaging.ConnectAsync();
            await syncedCommunity.LoadAsync(communityA.Id);
            var sharedStateRefreshed = Completion<bool>();
            syncedCommunity.Changed += () => sharedStateRefreshed.TrySetResult(true);

            var realtimeChannel = await owner.CreateChannelAsync(communityA.Id, "realtime-sync", null,
                CommunityChannelKind.Text);
            var structureEvents = new[]
            {
                await structureOnFirstOwnerTab.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                await structureOnSecondOwnerTab.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                await structureOnMember.Task.WaitAsync(TimeSpan.FromSeconds(5))
            };
            Assert.All(structureEvents, value => Assert.True(value.Revision > 0));
            Assert.Single(structureEvents.Select(value => value.Revision).Distinct());
            Assert.Contains((await outsider.GetCommunityStructureAsync(communityA.Id)).Channels,
                value => value.Id == realtimeChannel.Id);
            await sharedStateRefreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(syncedCommunity.Channels, value => value.Id == realtimeChannel.Id);

            var initiatingEcho = Completion<CommunityStateChangedEvent>();
            syncedSession.CommunityChanged += change =>
            {
                if (change.CommunityId == communityA.Id && change.Change == "permissions-updated")
                    initiatingEcho.TrySetResult(change);
            };
            var initiatingSave = await syncedCommunity.ReplacePermissionOverwritesAsync(
                PermissionOverwriteScopeType.Channel, realtimeChannel.Id, [
                    new(PermissionOverwriteTargetType.Member, outsiderAuth.Account.Id,
                        CommunityPermission.None, CommunityPermission.None)
                ]);
            var echoedSave = await initiatingEcho.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(initiatingSave.Revision, echoedSave.Revision);
            Assert.True(syncedCommunity.AppliedRevision >= initiatingSave.Revision);
            Assert.Contains(initiatingSave.Scope.Overwrites, value =>
                value.TargetType == PermissionOverwriteTargetType.Member &&
                value.TargetId == outsiderAuth.Account.Id);
            var initiatingRemoval = await syncedCommunity.ReplacePermissionOverwritesAsync(
                PermissionOverwriteScopeType.Channel, realtimeChannel.Id, []);
            Assert.True(initiatingRemoval.Revision > initiatingSave.Revision);
            Assert.Empty(initiatingRemoval.Scope.Overwrites);

            overwriteChangedOnMember = Completion<CommunityStateChangedEvent>();
            sharedStateRefreshed = Completion<bool>();
            var privatePermissionSave = await owner.ReplacePermissionOverwritesAsync(communityA.Id, PermissionOverwriteScopeType.Channel,
                realtimeChannel.Id, new([
                    new(PermissionOverwriteTargetType.Everyone, null,
                        CommunityPermission.None, CommunityPermission.ViewChannels)
                ]));
            await overwriteChangedOnMember.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await sharedStateRefreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(syncedCommunity.AppliedRevision >= privatePermissionSave.Revision);
            Assert.True((await owner.GetCommunityStructureAsync(communityA.Id)).Channels
                .Single(value => value.Id == realtimeChannel.Id).IsPrivate);
            Assert.DoesNotContain((await outsider.GetCommunityStructureAsync(communityA.Id)).Channels,
                value => value.Id == realtimeChannel.Id);
            await Assert.ThrowsAsync<HubException>(() => outsiderConnection.InvokeAsync(
                ChatHubContract.JoinChannel, communityA.Id, realtimeChannel.Id));
            overwriteChangedOnMember = Completion<CommunityStateChangedEvent>();
            await owner.ReplacePermissionOverwritesAsync(communityA.Id, PermissionOverwriteScopeType.Channel,
                realtimeChannel.Id, new([]));
            await overwriteChangedOnMember.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains((await outsider.GetCommunityStructureAsync(communityA.Id)).Channels,
                value => value.Id == realtimeChannel.Id);
            await Task.Delay(100);
            Assert.Equal(0, Volatile.Read(ref intruderCommunityChangeCount));
            var realtimeCategory = await owner.CreateCategoryAsync(communityA.Id, "Realtime Category");
            Assert.True((await categoryCreatedOnMember.Task.WaitAsync(TimeSpan.FromSeconds(5))).Revision >
                        structureEvents[0].Revision);
            await owner.DeleteCategoryAsync(communityA.Id, realtimeCategory.Id);
            await categoryDeletedOnMember.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await owner.DeleteChannelAsync(communityA.Id, realtimeChannel.Id);
            await channelDeletedOnMember.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain((await outsider.GetCommunityStructureAsync(communityA.Id)).Channels,
                value => value.Id == realtimeChannel.Id);
            await syncedMessaging.DisconnectAsync();

            var firstVoiceJoin = await firstConnection.InvokeAsync<ActiveVoiceRoomDto>(
                CommunityVoiceHubContract.Join, communityA.Id, categoryVoice.Id);
            Assert.Single(firstVoiceJoin.Participants);
            var duplicateVoiceJoin = await firstConnection.InvokeAsync<ActiveVoiceRoomDto>(
                CommunityVoiceHubContract.Join, communityA.Id, categoryVoice.Id);
            Assert.Single(duplicateVoiceJoin.Participants);
            Assert.Equal(firstVoiceJoin.StartedAt, duplicateVoiceJoin.StartedAt);
            var sameAccountSecondConnection = await secondConnection.InvokeAsync<ActiveVoiceRoomDto>(
                CommunityVoiceHubContract.Join, communityA.Id, categoryVoice.Id);
            await owner.MoveChannelAsync(communityA.Id, categoryVoice.Id, secondCategory.Id, 0);
            var activeVoiceMove = await activeVoiceMovedOnMember.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(activeVoiceMove.Revision > structureEvents[0].Revision);
            Assert.Equal(secondCategory.Id, (await outsider.GetCommunityStructureAsync(communityA.Id)).Channels
                .Single(value => value.Id == categoryVoice.Id).CategoryId);
            var roomAfterStructureRefresh = (await firstConnection.InvokeAsync<IReadOnlyList<ActiveVoiceRoomDto>>(
                CommunityVoiceHubContract.GetRooms, communityA.Id)).Single(value => value.ChannelId == categoryVoice.Id);
            Assert.Equal(2, roomAfterStructureRefresh.Participants.Count);
            Assert.Equal(firstVoiceJoin.StartedAt, roomAfterStructureRefresh.StartedAt);
            Assert.Equal(2, sameAccountSecondConnection.Participants.Count);
            Assert.Equal(2, sameAccountSecondConnection.Participants.Select(value => value.ParticipantId).Distinct().Count());
            var firstCommunityMedia = await firstConnection.InvokeAsync<CommunityVoiceMediaSessionDto>(
                CommunityVoiceHubContract.GetMediaSession);
            var secondCommunityMedia = await secondConnection.InvokeAsync<CommunityVoiceMediaSessionDto>(
                CommunityVoiceHubContract.GetMediaSession);
            Assert.Equal("development-peer-mesh", firstCommunityMedia.Provider);
            Assert.Equal(firstConnection.ConnectionId, firstCommunityMedia.ParticipantId);
            Assert.Equal(secondConnection.ConnectionId, secondCommunityMedia.ParticipantId);
            var communityNegotiation = Guid.NewGuid();
            var communityOffer = new WebRtcSessionDescription("offer", "safe-community-offer-sdp");
            await firstConnection.InvokeAsync(CommunityVoiceHubContract.SendMediaOffer,
                secondConnection.ConnectionId!, communityNegotiation, communityOffer);
            var routedCommunityOffer = await receivedCommunityOffer.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(firstConnection.ConnectionId, routedCommunityOffer.SourceParticipantId);
            Assert.Equal(communityOffer, routedCommunityOffer.Description);
            var communityAnswer = new WebRtcSessionDescription("answer", "safe-community-answer-sdp");
            await secondConnection.InvokeAsync(CommunityVoiceHubContract.SendMediaAnswer,
                firstConnection.ConnectionId!, communityNegotiation, communityAnswer);
            Assert.Equal(communityAnswer,
                (await receivedCommunityAnswer.Task.WaitAsync(TimeSpan.FromSeconds(5))).Description);
            var communityIce = new WebRtcIceCandidate("candidate:test", "audio", 0, null);
            await firstConnection.InvokeAsync(CommunityVoiceHubContract.SendMediaIceCandidate,
                secondConnection.ConnectionId!, communityNegotiation, communityIce);
            Assert.Equal(communityIce,
                (await receivedCommunityIce.Task.WaitAsync(TimeSpan.FromSeconds(5))).Candidate);
            await Assert.ThrowsAsync<HubException>(() => intruderConnection.InvokeAsync(
                CommunityVoiceHubContract.SendMediaOffer, firstConnection.ConnectionId!, communityNegotiation,
                communityOffer));
            await owner.UpdateCommunityRoleAsync(communityA.Id, initialRole.Id, new UpdateCommunityRoleRequest(
                "@everyone", initialRole.Permissions & ~CommunityPermission.SpeakVoice, initialRole.Color));
            var permissionChange = await permissionsChangedOnMember.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(permissionChange.Revision > activeVoiceMove.Revision);
            Assert.False(((await outsider.GetCommunityStructureAsync(communityA.Id)).EffectivePermissions &
                          CommunityPermission.SpeakVoice) != 0);
            var thirdVoiceJoin = await outsiderConnection.InvokeAsync<ActiveVoiceRoomDto>(
                CommunityVoiceHubContract.Join, communityA.Id, categoryVoice.Id);
            Assert.Equal(3, thirdVoiceJoin.Participants.Count);
            Assert.Equal(firstVoiceJoin.StartedAt, thirdVoiceJoin.StartedAt);
            await outsiderConnection.InvokeAsync(CommunityVoiceHubContract.SetState, true, false);
            await Assert.ThrowsAsync<HubException>(() => outsiderConnection.InvokeAsync(
                CommunityVoiceHubContract.SetState, false, false));
            await owner.UpdateCommunityRoleAsync(communityA.Id, initialRole.Id, new UpdateCommunityRoleRequest(
                "@everyone", initialRole.Permissions | CommunityPermission.ConnectVoice |
                CommunityPermission.SpeakVoice, initialRole.Color));
            await outsiderConnection.InvokeAsync(CommunityVoiceHubContract.SetSpeaking, true);
            var activeRooms = await firstConnection.InvokeAsync<IReadOnlyList<ActiveVoiceRoomDto>>(
                CommunityVoiceHubContract.GetRooms, communityA.Id);
            var outsiderVoice = activeRooms.Single(value => value.ChannelId == categoryVoice.Id).Participants
                .Single(value => value.ParticipantId == outsiderConnection.ConnectionId);
            Assert.True(outsiderVoice.Muted);
            Assert.False(outsiderVoice.Speaking);
            var switchedVoice = await firstConnection.InvokeAsync<ActiveVoiceRoomDto>(
                CommunityVoiceHubContract.Join, communityA.Id, nestedVoice.Id);
            Assert.Equal(nestedVoice.Id, switchedVoice.ChannelId);
            Assert.Single(switchedVoice.Participants);
            await Assert.ThrowsAsync<HubException>(() => intruderConnection.InvokeAsync<ActiveVoiceRoomDto>(
                CommunityVoiceHubContract.Join, communityA.Id, rootVoice.Id));
            await firstConnection.InvokeAsync(CommunityVoiceHubContract.Leave);
            await secondConnection.InvokeAsync(CommunityVoiceHubContract.Leave);
            await outsiderConnection.InvokeAsync(CommunityVoiceHubContract.Leave);
            Assert.Empty(await firstConnection.InvokeAsync<IReadOnlyList<ActiveVoiceRoomDto>>(
                CommunityVoiceHubContract.GetRooms, communityA.Id));
            await using (var disconnectProbe = Connection(nodeAddress, ownerAuth.AccessToken))
            {
                await disconnectProbe.StartAsync();
                await disconnectProbe.InvokeAsync<ActiveVoiceRoomDto>(CommunityVoiceHubContract.Join,
                    communityA.Id, rootVoice.Id);
            }
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if ((await firstConnection.InvokeAsync<IReadOnlyList<ActiveVoiceRoomDto>>(
                        CommunityVoiceHubContract.GetRooms, communityA.Id)).Count == 0) break;
                await Task.Delay(50);
            }
            Assert.Empty(await firstConnection.InvokeAsync<IReadOnlyList<ActiveVoiceRoomDto>>(
                CommunityVoiceHubContract.GetRooms, communityA.Id));
            await firstConnection.InvokeAsync(ChatHubContract.JoinChannel, communityA.Id, chatChannel.Id);
            await secondConnection.InvokeAsync(ChatHubContract.JoinChannel, communityA.Id, chatChannel.Id);
            await outsiderConnection.InvokeAsync(ChatHubContract.JoinChannel, communityB.Id, outsiderChannel.Id);
            var liveFriendRequest = await intruder.SendFriendRequestAsync("owner");
            Assert.Equal(liveFriendRequest.FriendshipId,
                (await friendRequestOnOwner.Task.WaitAsync(TimeSpan.FromSeconds(5))).FriendshipId);
            await owner.AcceptFriendRequestAsync(liveFriendRequest.FriendshipId);
            Assert.Equal(liveFriendRequest.FriendshipId,
                (await friendAcceptedOnIntruder.Task.WaitAsync(TimeSpan.FromSeconds(5))).FriendshipId);
            outsiderPresenceOnOwner = Completion<PresenceChangedEvent>();
            await outsiderConnection.InvokeAsync(PresenceHubContract.SetPresence, UserPresence.DoNotDisturb);
            Assert.Equal(PublicPresence.DoNotDisturb,
                (await outsiderPresenceOnOwner.Task.WaitAsync(TimeSpan.FromSeconds(5))).Presence);
            Assert.Equal(PublicPresence.DoNotDisturb,
                (await owner.GetFriendsAsync()).Single(value => value.AccountId == outsiderAuth.Account.Id).Presence);
            outsiderPresenceOnOwner = Completion<PresenceChangedEvent>();
            await outsiderConnection.InvokeAsync(PresenceHubContract.SetPresence, UserPresence.Invisible);
            Assert.Equal(PublicPresence.Offline,
                (await outsiderPresenceOnOwner.Task.WaitAsync(TimeSpan.FromSeconds(5))).Presence);
            Assert.Equal(PublicPresence.Offline, (await owner.ResolveProfileAsync("outsider")).Presence);
            Assert.Equal(UserPresence.Invisible, (await outsider.GetCurrentAccountAsync()).PreferredPresence);
            await firstConnection.InvokeAsync(DirectMessageHubContract.JoinConversation, directConversation.Id);
            await outsiderConnection.InvokeAsync(DirectMessageHubContract.JoinConversation, directConversation.Id);
            var forbiddenHistory = await Assert.ThrowsAsync<NodeApiException>(() => intruder.GetDirectMessagesAsync(directConversation.Id));
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenHistory.StatusCode);
            await Assert.ThrowsAsync<HubException>(() => intruderConnection.InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.SendMessage, directConversation.Id,
                new SendDirectMessageRequest("intrusion", null)));
            var directClientMessageId = Guid.NewGuid();
            var directMessage = await firstConnection.InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.SendMessage, directConversation.Id,
                new SendDirectMessageRequest("private hello", null, directClientMessageId));
            Assert.Equal(directMessage.Id, (await directOnOutsider.Task.WaitAsync(TimeSpan.FromSeconds(5))).Id);
            var directIdempotentRetry = await firstConnection.InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.SendMessage, directConversation.Id,
                new SendDirectMessageRequest("private hello", null, directClientMessageId));
            Assert.Equal(directMessage.Id, directIdempotentRetry.Id);
            Assert.Equal(directClientMessageId, directIdempotentRetry.ClientMessageId);
            Assert.Single(await owner.GetDirectMessagesAsync(directConversation.Id),
                value => value.ClientMessageId == directClientMessageId);
            Assert.Equal(0, Assert.Single(await owner.GetDirectConversationsAsync()).UnreadCount);
            Assert.Equal(1, Assert.Single(await outsider.GetDirectConversationsAsync()).UnreadCount);
            await firstConnection.InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.SendMessage, directConversation.Id,
                new SendDirectMessageRequest("private second", null));
            Assert.Equal(2, Assert.Single(await outsider.GetDirectConversationsAsync()).UnreadCount);
            await outsider.MarkDirectConversationReadAsync(directConversation.Id);
            Assert.Equal(0, Assert.Single(await outsider.GetDirectConversationsAsync()).UnreadCount);
            await outsider.HideDirectConversationAsync(directConversation.Id);
            Assert.Empty(await outsider.GetDirectConversationsAsync());
            Assert.Equal(2, (await outsider.GetDirectMessagesAsync(directConversation.Id)).Count);
            await Assert.ThrowsAsync<HubException>(() => outsiderConnection.InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.EditMessage, directConversation.Id, directMessage.Id,
                new EditDirectMessageRequest("not mine")));
            await firstConnection.InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.EditMessage, directConversation.Id, directMessage.Id,
                new EditDirectMessageRequest("private hello edited"));
            await firstConnection.InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.SendMessage, directConversation.Id,
                new SendDirectMessageRequest("private again", directMessage.Id));
            await secondDirectOnOutsider.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Assert.Single(await outsider.GetDirectConversationsAsync()).UnreadCount);
            await outsider.MarkDirectConversationReadAsync(directConversation.Id);
            Assert.Equal(0, Assert.Single(await outsider.GetDirectConversationsAsync()).UnreadCount);
            await outsiderConnection.InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.SendMessage, directConversation.Id,
                new SendDirectMessageRequest("reply from outsider", null));
            Assert.Equal(0, Assert.Single(await outsider.GetDirectConversationsAsync()).UnreadCount);
            Assert.Equal(1, Assert.Single(await owner.GetDirectConversationsAsync()).UnreadCount);
            Assert.Equal(4, (await outsider.GetDirectMessagesAsync(directConversation.Id)).Count);

            await owner.MarkDirectConversationReadAsync(directConversation.Id);
            await outsider.MarkDirectConversationReadAsync(directConversation.Id);
            Assert.Equal(0, Assert.Single(await owner.GetDirectConversationsAsync()).UnreadCount);
            Assert.Equal(0, Assert.Single(await outsider.GetDirectConversationsAsync()).UnreadCount);

            var call = await firstConnection.InvokeAsync<CallSessionDto>(VoiceCallHubContract.Start, directConversation.Id);
            var callerCallEvent = await callStartedOnCaller.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var calleeCallEvent = await callStartedOnCallee.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(callerCallEvent.Id, calleeCallEvent.Id);
            Assert.Equal(MessageKind.CallStarted, callerCallEvent.Kind);
            Assert.Equal(call.Id, callerCallEvent.RelatedCallId);
            Assert.Equal(directConversation.Id, callerCallEvent.ConversationId);
            Assert.Equal(ownerAuth.Account.Id, callerCallEvent.Author.AccountId);
            Assert.Empty(callerCallEvent.Content);
            Assert.Equal(0, Assert.Single(await owner.GetDirectConversationsAsync()).UnreadCount);
            Assert.Equal(1, Assert.Single(await outsider.GetDirectConversationsAsync()).UnreadCount);
            Assert.Single(await owner.GetDirectMessagesAsync(directConversation.Id),
                value => value.Kind == MessageKind.CallStarted && value.RelatedCallId == call.Id);
            await Assert.ThrowsAsync<HubException>(() => firstConnection.InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.EditMessage, directConversation.Id, callerCallEvent.Id,
                new EditDirectMessageRequest("spoofed system text")));
            await Assert.ThrowsAsync<HubException>(() => firstConnection.InvokeAsync(
                DirectMessageHubContract.DeleteMessage, directConversation.Id, callerCallEvent.Id));
            await Assert.ThrowsAsync<HubException>(() => outsiderConnection.InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.SendMessage, directConversation.Id,
                new SendDirectMessageRequest("reply to system", callerCallEvent.Id)));
            Assert.DoesNotContain(typeof(SendDirectMessageRequest).GetProperties(),
                property => property.Name.Contains("Kind", StringComparison.OrdinalIgnoreCase));
            var incoming = await incomingCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(call.Id, incoming.CallId);
            Assert.Equal(ownerAuth.Account.Id, incoming.CallerAccountId);
            Assert.Equal("Owner", incoming.CallerDisplayName);
            Assert.Equal(call.Id, (await incomingCallOnSecondCallee.Task.WaitAsync(TimeSpan.FromSeconds(5))).CallId);
            await Assert.ThrowsAsync<HubException>(() => secondConnection.InvokeAsync<CallSessionDto>(
                VoiceCallHubContract.Start, directConversation.Id));
            await Assert.ThrowsAsync<HubException>(() => intruderConnection.InvokeAsync(
                VoiceCallHubContract.SendAnswer, call.Id, Guid.NewGuid(), 1, 1, Guid.NewGuid(),
                new WebRtcSessionDescription("answer", "intrusion")));
            var mediaConfiguration = await firstConnection.InvokeAsync<CallMediaConfigurationDto>(
                VoiceCallHubContract.GetMediaConfiguration, call.Id);
            Assert.Equal(MediaMode.DirectWebRtc, mediaConfiguration.Mode);
            await firstConnection.InvokeAsync(VoiceCallHubContract.ReportDiagnostic,
                new VoiceDiagnosticReport(call.Id, "PeerCreated", PeerGeneration: 1,
                    NegotiationGeneration: 1, IceServerCount: 1, IceTransportPolicy: "all"));
            await Assert.ThrowsAsync<HubException>(() => intruderConnection.InvokeAsync<CallMediaConfigurationDto>(
                VoiceCallHubContract.GetMediaConfiguration, call.Id));
            await Assert.ThrowsAsync<HubException>(() => intruderConnection.InvokeAsync(
                VoiceCallHubContract.ReportDiagnostic,
                new VoiceDiagnosticReport(call.Id, "PeerCreated", PeerGeneration: 1)));

            await outsiderConnection.InvokeAsync(VoiceCallHubContract.Accept, call.Id);
            Assert.Equal(CallState.Active, (await callAccepted.Task.WaitAsync(TimeSpan.FromSeconds(5))).State);
            var elsewhere = await answeredElsewhere.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(call.Id, elsewhere.CallId);
            Assert.Equal("Answered in another tab", elsewhere.Reason);
            await Assert.ThrowsAsync<HubException>(() => secondOutsiderConnection.InvokeAsync(
                VoiceCallHubContract.Accept, call.Id));

            var offer = new WebRtcSessionDescription("offer", "test-offer-sdp");
            var negotiationId = Guid.NewGuid();
            var offerSignalId = Guid.NewGuid();
            await firstConnection.InvokeAsync(VoiceCallHubContract.SendOffer, call.Id, negotiationId,
                1, 1, offerSignalId, WebRtcNegotiationKind.Initial, offer);
            var forwardedOffer = await receivedOffer.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(negotiationId, forwardedOffer.NegotiationId);
            Assert.Equal(offerSignalId, forwardedOffer.SignalId);
            Assert.Equal(offer, forwardedOffer.Description);
            var answer = new WebRtcSessionDescription("answer", "test-answer-sdp");
            var answerSignalId = Guid.NewGuid();
            await outsiderConnection.InvokeAsync(VoiceCallHubContract.SendAnswer, call.Id, negotiationId,
                1, 1, answerSignalId, answer);
            var forwardedAnswer = await receivedAnswer.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(negotiationId, forwardedAnswer.NegotiationId);
            Assert.Equal(answerSignalId, forwardedAnswer.SignalId);
            Assert.Equal(answer, forwardedAnswer.Description);
            await outsiderConnection.InvokeAsync(VoiceCallHubContract.SendAnswer, call.Id, negotiationId,
                1, 1, answerSignalId, answer);
            await outsiderConnection.InvokeAsync(VoiceCallHubContract.SendAnswer, call.Id, Guid.NewGuid(),
                2, 1, Guid.NewGuid(), answer);
            await Task.Delay(250);
            Assert.Equal(1, Volatile.Read(ref answerDeliveryCount));
            Assert.Equal(0, Volatile.Read(ref answerDeliveryOnSecondCallerCount));
            Assert.Equal(0, Volatile.Read(ref acceptedOnSecondCallerCount));
            Assert.Equal(0, Volatile.Read(ref offerDeliveryOnSecondCalleeCount));
            var candidate = new WebRtcIceCandidate("candidate:test", "audio", 0, "test-fragment");
            var candidateSignalId = Guid.NewGuid();
            await firstConnection.InvokeAsync(VoiceCallHubContract.SendIceCandidate, call.Id, negotiationId,
                1, 1, candidateSignalId, candidate);
            var forwardedCandidate = await receivedIce.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(negotiationId, forwardedCandidate.NegotiationId);
            Assert.Equal(candidateSignalId, forwardedCandidate.SignalId);
            Assert.Equal(candidate, forwardedCandidate.Candidate);
            await Task.Delay(100);
            Assert.Equal(0, Volatile.Read(ref iceDeliveryOnSecondCalleeCount));
            await firstConnection.InvokeAsync(VoiceCallHubContract.SetParticipantState, call.Id,
                true, false, CallConnectionState.Connected);
            var participantState = await participantChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(participantState.IsMuted);
            Assert.Equal(ownerAuth.Account.Id, participantState.AccountId);
            await firstConnection.InvokeAsync(VoiceCallHubContract.SetParticipantState, call.Id,
                true, false, CallConnectionState.Connected);
            await Task.Delay(100);
            Assert.Equal(1, Volatile.Read(ref participantStateChangeCount));
            await firstConnection.InvokeAsync(VoiceCallHubContract.SetSpeaking, call.Id, true);
            var speakingState = await speakingChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(speakingState.IsSpeaking);
            Assert.Equal(ownerAuth.Account.Id, speakingState.AccountId);
            await Assert.ThrowsAsync<HubException>(() => intruderConnection.InvokeAsync(
                VoiceCallHubContract.SetSpeaking, call.Id, true));
            await outsiderConnection.InvokeAsync(VoiceCallHubContract.RequestMediaRetry, call.Id);
            Assert.Equal(call.Id, (await mediaRetryRequested.Task.WaitAsync(TimeSpan.FromSeconds(5))).CallId);
            Assert.Single(await owner.GetDirectMessagesAsync(directConversation.Id),
                value => value.Kind == MessageKind.CallStarted && value.RelatedCallId == call.Id);
            await firstConnection.InvokeAsync(VoiceCallHubContract.HangUp, call.Id);
            Assert.Equal(CallState.Ended, (await callEnded.Task.WaitAsync(TimeSpan.FromSeconds(5))).State);
            await firstConnection.InvokeAsync(DirectMessageHubContract.DeleteMessage,
                directConversation.Id, directMessage.Id);
            Assert.DoesNotContain(await owner.GetDirectMessagesAsync(directConversation.Id),
                value => value.Id == directMessage.Id);
            // Wait until the server has processed the first disconnect before stopping the
            // final connection; otherwise either disconnect notification can win the TCS race.
            outsiderPresenceOnOwner = Completion<PresenceChangedEvent>();
            await secondOutsiderConnection.StopAsync();
            await outsiderPresenceOnOwner.Task.WaitAsync(TimeSpan.FromSeconds(5));

            outsiderPresenceOnOwner = Completion<PresenceChangedEvent>();
            await outsiderConnection.InvokeAsync(PresenceHubContract.SetPresence, UserPresence.DoNotDisturb);
            await outsiderPresenceOnOwner.Task.WaitAsync(TimeSpan.FromSeconds(5));
            outsiderPresenceOnOwner = Completion<PresenceChangedEvent>();
            await outsiderConnection.StopAsync();
            Assert.Equal(PublicPresence.Offline,
                (await outsiderPresenceOnOwner.Task.WaitAsync(TimeSpan.FromSeconds(5))).Presence);
            outsiderPresenceOnOwner = Completion<PresenceChangedEvent>();
            await outsiderConnection.StartAsync();
            Assert.Equal(PublicPresence.DoNotDisturb,
                (await outsiderPresenceOnOwner.Task.WaitAsync(TimeSpan.FromSeconds(5))).Presence);

            var channelClientMessageId = Guid.NewGuid();
            var sent = await firstConnection.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.SendMessage, communityA.Id, chatChannel.Id,
                new SendChannelMessageRequest("hello from tab one", null, null, channelClientMessageId));
            Assert.Equal(sent.Id, (await createdOnSecond.Task.WaitAsync(TimeSpan.FromSeconds(5))).Id);
            var idempotentRetry = await firstConnection.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.SendMessage, communityA.Id, chatChannel.Id,
                new SendChannelMessageRequest("hello from tab one", null, null, channelClientMessageId));
            Assert.Equal(sent.Id, idempotentRetry.Id);
            Assert.Equal(channelClientMessageId, idempotentRetry.ClientMessageId);
            Assert.Single(await owner.GetChannelMessagesAsync(communityA.Id, chatChannel.Id),
                value => value.ClientMessageId == channelClientMessageId);
            var mentioned = await firstConnection.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.SendMessage, communityA.Id, chatChannel.Id,
                new SendChannelMessageRequest("@Outsider @everyone", null,
                [
                    new(CommunityMentionKind.Account, outsiderAuth.Account.Id, 0, 9),
                    new(CommunityMentionKind.Everyone, null, 10, 9)
                ]));
            var mentionEvent = await mentionReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(mentioned.Id, mentionEvent.MessageId);
            Assert.Equal(2, mentioned.Mentions?.Count);
            await Task.Delay(150);
            Assert.Equal(1, Volatile.Read(ref mentionNotificationCount));
            var mentionStructure = await outsider.GetCommunityStructureAsync(communityA.Id);
            Assert.Equal(1, mentionStructure.Channels.Single(value => value.Id == chatChannel.Id).MentionCount);
            Assert.Equal(1, (await outsider.GetCommunitiesAsync()).Single(value => value.Id == communityA.Id).MentionCount);

            await firstConnection.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.SendMessage, communityA.Id, welcome.Id,
                new SendChannelMessageRequest("@Outsider", null,
                [new(CommunityMentionKind.Account, outsiderAuth.Account.Id, 0, 9)]));
            mentionStructure = await outsider.GetCommunityStructureAsync(communityA.Id);
            Assert.Equal(1, mentionStructure.Channels.Single(value => value.Id == chatChannel.Id).MentionCount);
            Assert.Equal(1, mentionStructure.Channels.Single(value => value.Id == welcome.Id).MentionCount);
            Assert.Equal(2, (await outsider.GetCommunitiesAsync()).Single(value => value.Id == communityA.Id).MentionCount);

            await outsider.MarkCommunityChannelReadAsync(communityA.Id, chatChannel.Id);
            mentionStructure = await outsider.GetCommunityStructureAsync(communityA.Id);
            Assert.Equal(0, mentionStructure.Channels.Single(value => value.Id == chatChannel.Id).MentionCount);
            Assert.Equal(1, mentionStructure.Channels.Single(value => value.Id == welcome.Id).MentionCount);
            Assert.Equal(1, (await outsider.GetCommunitiesAsync()).Single(value => value.Id == communityA.Id).MentionCount);
            var secondFromFirst = await firstConnection.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.SendMessage, communityA.Id, chatChannel.Id,
                new SendChannelMessageRequest("second from tab one", null));
            var thirdFromFirst = await firstConnection.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.SendMessage, communityA.Id, chatChannel.Id,
                new SendChannelMessageRequest("third from tab one", null));
            Assert.Equal(secondFromFirst.Id, (await secondCreatedOnSecond.Task.WaitAsync(TimeSpan.FromSeconds(5))).Id);
            Assert.Equal(thirdFromFirst.Id, (await thirdCreatedOnSecond.Task.WaitAsync(TimeSpan.FromSeconds(5))).Id);

            await firstConnection.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.EditMessage, communityA.Id, chatChannel.Id, sent.Id,
                new EditChannelMessageRequest("edited hello"));
            var edited = await updatedOnSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(edited.EditedAt);

            var reply = await secondConnection.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.SendMessage, communityA.Id, chatChannel.Id,
                new SendChannelMessageRequest("a reply", sent.Id));
            Assert.Equal(reply.Id, (await replyOnFirst.Task.WaitAsync(TimeSpan.FromSeconds(5))).Id);
            Assert.Equal(sent.Id, reply.ReplyTo?.MessageId);
            var secondFromSecond = await secondConnection.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.SendMessage, communityA.Id, chatChannel.Id,
                new SendChannelMessageRequest("second from tab two", null));
            var thirdFromSecond = await secondConnection.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.SendMessage, communityA.Id, chatChannel.Id,
                new SendChannelMessageRequest("third from tab two", null));
            Assert.Equal(secondFromSecond.Id, (await secondCreatedOnFirst.Task.WaitAsync(TimeSpan.FromSeconds(5))).Id);
            Assert.Equal(thirdFromSecond.Id, (await thirdCreatedOnFirst.Task.WaitAsync(TimeSpan.FromSeconds(5))).Id);

            await firstConnection.InvokeAsync(ChatHubContract.DeleteMessage, communityA.Id, chatChannel.Id, sent.Id);
            Assert.Equal(sent.Id, (await deletedOnSecond.Task.WaitAsync(TimeSpan.FromSeconds(5))).MessageId);
            var history = await owner.GetChannelMessagesAsync(communityA.Id, chatChannel.Id);
            Assert.DoesNotContain(history, value => value.Id == sent.Id);
            Assert.True(history.Single(value => value.Id == reply.Id).ReplyTo?.IsDeleted);
            Assert.Null(history.Single(value => value.Id == reply.Id).ReplyTo?.Excerpt);

            var pagedMessages = new List<ChannelMessageDto>();
            for (var index = 0; index < 55; index++)
                pagedMessages.Add(await firstConnection.InvokeAsync<ChannelMessageDto>(
                    ChatHubContract.SendMessage, communityA.Id, chatChannel.Id,
                    new SendChannelMessageRequest($"paged marker {index:D2}", null)));
            var latestPage = await owner.GetChannelMessagePageAsync(communityA.Id, chatChannel.Id);
            Assert.Equal(MessageHistoryDefaults.PageSize, latestPage.Messages.Count);
            Assert.True(latestPage.HasOlder);
            Assert.Contains(latestPage.Messages, value => value.Id == pagedMessages[^1].Id);
            Assert.DoesNotContain(latestPage.Messages, value => value.Id == pagedMessages[0].Id);
            var olderPage = await owner.GetChannelMessagePageAsync(
                communityA.Id, chatChannel.Id, before: latestPage.OlderCursor);
            Assert.DoesNotContain(olderPage.Messages, value => latestPage.Messages.Any(latest => latest.Id == value.Id));
            Assert.Contains(olderPage.Messages, value => value.Id == pagedMessages[0].Id);
            var aroundPage = await owner.GetChannelMessagePageAsync(
                communityA.Id, chatChannel.Id, around: pagedMessages[5].Id);
            Assert.True(aroundPage.IsAroundWindow);
            Assert.Contains(aroundPage.Messages, value => value.Id == pagedMessages[5].Id);
            var searchPage = await owner.SearchCommunityMessagesAsync(communityA.Id, "paged marker 00", null, null);
            Assert.Equal(pagedMessages[0].Id, Assert.Single(searchPage.Results).MessageId);
            var typedSearch = await owner.SearchCommunityMessagesAsync(communityA.Id,
                new MessageSearchRequest(new("paged marker", ownerAuth.Account.Id, chatChannel.Id, null, [],
                    null, null, null, null, MessageAuthorType.User, MessageSearchSort.Oldest), Limit: 3));
            Assert.Equal(3, typedSearch.Results.Count);
            Assert.True(typedSearch.HasMore);
            Assert.Equal(pagedMessages[0].Id, typedSearch.Results[0].MessageId);
            Assert.All(typedSearch.Results, value => Assert.Equal(chatChannel.Id, value.ChannelId));
            var typedNext = await owner.SearchCommunityMessagesAsync(communityA.Id,
                new MessageSearchRequest(
                    new("paged marker", ownerAuth.Account.Id, chatChannel.Id, null, [], null, null, null, null,
                        MessageAuthorType.User, MessageSearchSort.Oldest),
                    typedSearch.OlderCursor, 3));
            Assert.DoesNotContain(typedNext.Results,
                value => typedSearch.Results.Any(firstPage => firstPage.MessageId == value.MessageId));
            var mentionSearch = await owner.SearchCommunityMessagesAsync(communityA.Id,
                new MessageSearchRequest(new(null, null, chatChannel.Id, outsiderAuth.Account.Id, [],
                    null, null, null, null)));
            Assert.Contains(mentionSearch.Results, value => value.MessageId == mentioned.Id);
            var dateSearch = await owner.SearchCommunityMessagesAsync(communityA.Id,
                new MessageSearchRequest(new("paged marker 00", null, chatChannel.Id, null, [],
                    pagedMessages[0].CreatedAt.AddTicks(1), pagedMessages[0].CreatedAt.AddTicks(-1), null, null)));
            Assert.Equal(pagedMessages[0].Id, Assert.Single(dateSearch.Results).MessageId);
            await Assert.ThrowsAsync<NodeApiException>(() =>
                intruder.SearchCommunityMessagesAsync(communityA.Id, "paged", null, null));
            await Assert.ThrowsAsync<NodeApiException>(() =>
                intruder.SearchCommunityMessagesAsync(communityA.Id,
                    new MessageSearchRequest(new("paged", null, null, null, [], null, null, null, null))));

            var directLatest = await outsider.GetDirectMessagePageAsync(directConversation.Id, 2);
            Assert.Equal(2, directLatest.Messages.Count);
            Assert.True(directLatest.HasOlder);
            var directOlder = await outsider.GetDirectMessagePageAsync(
                directConversation.Id, 2, before: directLatest.OlderCursor);
            Assert.DoesNotContain(directOlder.Messages, value => directLatest.Messages.Any(latest => latest.Id == value.Id));
            var directAround = await outsider.GetDirectMessagePageAsync(
                directConversation.Id, around: directLatest.Messages[0].Id);
            Assert.Contains(directAround.Messages, value => value.Id == directLatest.Messages[0].Id);

            var outsiderMessage = await outsiderConnection.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.SendMessage, communityB.Id, outsiderChannel.Id,
                new SendChannelMessageRequest("not yours", null));
            var crossCommunityEdit = await Assert.ThrowsAsync<HubException>(() => firstConnection.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.EditMessage, communityB.Id, outsiderChannel.Id, outsiderMessage.Id,
                new EditChannelMessageRequest("tampered")));
            Assert.Contains("not a member", crossCommunityEdit.Message, StringComparison.OrdinalIgnoreCase);

            await Assert.ThrowsAsync<HubException>(() => outsiderConnection.InvokeAsync(
                ChatHubContract.DeleteMessage, communityA.Id, chatChannel.Id, reply.Id));
            await Assert.ThrowsAsync<HubException>(() => firstConnection.InvokeAsync(
                ChatHubContract.DeleteMessage, communityA.Id, outsiderChannel.Id, outsiderMessage.Id));

            var selfTargeted = await firstConnection.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.SendMessage, communityA.Id, chatChannel.Id,
                new SendChannelMessageRequest("@self @everyone", null,
                [
                    new(CommunityMentionKind.Account, ownerAuth.Account.Id, 0, 5),
                    new(CommunityMentionKind.Everyone, null, 6, 9)
                ]));
            Assert.True(CommunityMentionPresentation.IsTargetedAt(selfTargeted, ownerAuth.Account.Id));
            Assert.False(CommunityMentionPresentation.ShouldNotify(selfTargeted, ownerAuth.Account.Id));
            Assert.Equal(0, (await owner.GetCommunitiesAsync()).Single(value => value.Id == communityA.Id).MentionCount);
            Assert.Equal(0, (await owner.GetCommunityStructureAsync(communityA.Id)).Channels
                .Single(value => value.Id == chatChannel.Id).MentionCount);

            await owner.DeleteChannelAsync(communityA.Id, chatChannel.Id);
            Assert.DoesNotContain((await owner.GetCommunityStructureAsync(communityA.Id)).Channels, value => value.Id == chatChannel.Id);
            await owner.RemoveFriendshipAsync(outgoingFriendRequest.FriendshipId);
            await owner.RemoveFriendshipAsync(liveFriendRequest.FriendshipId);
            Assert.Empty(await owner.GetFriendsAsync());
            Assert.Empty(await outsider.GetFriendsAsync());
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryAsync(tempDirectory);
        }
    }

    private static HubConnection Connection(Uri nodeAddress, string token) => new HubConnectionBuilder()
        .WithUrl(new Uri(nodeAddress, "hubs/chat"), options => options.AccessTokenProvider = () => Task.FromResult<string?>(token))
        .Build();

    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class MemoryAccountStore : ISavedAccountStore
    {
        private SavedAccountStoreData _data = SavedAccountStoreData.Empty;
        public Task<SavedAccountStoreData> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_data);
        public Task SaveAsync(SavedAccountStoreData data, CancellationToken cancellationToken = default)
        {
            _data = data;
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySelectionStore : IActiveAccountSelectionStore
    {
        private SavedAccountKey? _key;
        public Task<SavedAccountKey?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_key);
        public Task SaveAsync(SavedAccountKey? key, CancellationToken cancellationToken = default)
        {
            _key = key;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyLegacyTokenStore : INodeTokenStore
    {
        public Task<string?> LoadAsync(string nodeAddress, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
        public Task SaveAsync(string nodeAddress, string token, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RemoveAsync(string nodeAddress, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void AssertMixedPositions(CommunityStructureDto structure, Guid? parentCategoryId)
    {
        var positions = structure.Categories.Where(value => value.ParentCategoryId == parentCategoryId)
            .Select(value => value.Position)
            .Concat(structure.Channels.Where(value => value.CategoryId == parentCategoryId)
                .Select(value => value.Position)).Order().ToArray();
        Assert.Equal(Enumerable.Range(0, positions.Length), positions);
    }

    private static async Task DeleteDirectoryAsync(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 19) { await Task.Delay(100); }
        }
    }

    private static Process StartServer(string project, Uri address, string databasePath, string buildConfiguration)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add(buildConfiguration);
        start.ArgumentList.Add("--no-launch-profile");
        start.Environment["ASPNETCORE_URLS"] = address.ToString().TrimEnd('/');
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__Iridium"] = $"Data Source={databasePath}";
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the Iridium test node.");
    }

    private static async Task WaitForServerAsync(Uri address, Process server, Task<string> output, Task<string> error)
    {
        using var http = new HttpClient { BaseAddress = address };
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (server.HasExited)
                throw new InvalidOperationException($"The test node stopped early.\n{await output}\n{await error}");
            try
            {
                using var response = await http.GetAsync("api/server-info");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("The test node did not become ready.");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
