using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

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
            }

            var owner = new NodeClient(nodeAddress);
            var ownerAuth = await owner.RegisterAsync(new RegisterAccountRequest("owner", "Owner", "test-password"));
            var secondOwnerSession = new NodeClient(nodeAddress);
            var secondOwnerAuth = await secondOwnerSession.LoginAsync(new LoginRequest("owner", "test-password"));
            var outsider = new NodeClient(nodeAddress);
            var outsiderAuth = await outsider.RegisterAsync(new RegisterAccountRequest("outsider", "Outsider", "test-password"));
            var intruder = new NodeClient(nodeAddress);
            var intruderAuth = await intruder.RegisterAsync(new RegisterAccountRequest("intruder", "Intruder", "test-password"));

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
            Assert.Equal(HttpStatusCode.Conflict,
                (await Assert.ThrowsAsync<NodeApiException>(() => owner.SendFriendRequestAsync("outsider"))).StatusCode);

            var directConversation = await owner.OpenDirectConversationAsync(outsiderAuth.Account.Id);
            Assert.Empty(await owner.GetDirectConversationsAsync());
            Assert.Empty(await outsider.GetDirectConversationsAsync());

            var communityA = await owner.CreateCommunityAsync(new CreateCommunityRequest("Alpha", null));
            var initialManagement = await owner.GetCommunityManagementAsync(communityA.Id);
            Assert.Empty(initialManagement.Invites);
            Assert.Empty(initialManagement.Bans);
            var initialRole = Assert.Single(initialManagement.Roles);
            Assert.True(initialRole.IsDefault);
            Assert.Equal("@everyone", initialRole.Name);
            var defaults = await owner.GetCommunityStructureAsync(communityA.Id);
            var defaultCategory = Assert.Single(defaults.Categories);
            Assert.Equal("TEXT CHANNELS", defaultCategory.Name);
            var defaultChannel = Assert.Single(defaults.Channels);
            Assert.Equal("general", defaultChannel.Name);
            Assert.Equal(defaultCategory.Id, defaultChannel.CategoryId);
            var loadedAgain = await owner.GetCommunityStructureAsync(communityA.Id);
            Assert.Equal(defaultCategory.Id, Assert.Single(loadedAgain.Categories).Id);
            Assert.Equal(defaultChannel.Id, Assert.Single(loadedAgain.Channels).Id);
            var membershipInvite = await owner.CreateCommunityInviteAsync(communityA.Id, new(null, null));
            var membershipToken = CommunityInviteLink.Find(membershipInvite.InviteUrl!)?.Token;
            Assert.NotNull(membershipToken);
            await outsider.JoinCommunityInviteAsync(membershipToken);

            var firstCategory = await owner.CreateCategoryAsync(communityA.Id, "Rooms");
            var secondCategory = await owner.CreateCategoryAsync(communityA.Id, "Projects");
            await owner.UpdateCategoryAsync(communityA.Id, secondCategory.Id, "Topics");
            await owner.MoveCategoryAsync(communityA.Id, secondCategory.Id, 0);
            var welcome = await owner.CreateChannelAsync(communityA.Id, "welcome", null);
            await owner.MoveChannelAsync(communityA.Id, welcome.Id, null, 1);
            var tail = await owner.CreateChannelAsync(communityA.Id, "tail", null);
            var unified = await owner.GetCommunityStructureAsync(communityA.Id);
            var unifiedIds = unified.Categories.Select(value => (value.Position, value.Id))
                .Concat(unified.Channels.Where(value => value.CategoryId is null).Select(value => (value.Position, value.Id)))
                .OrderBy(value => value.Position).Select(value => value.Id).ToArray();
            Assert.Equal([secondCategory.Id, welcome.Id, defaultCategory.Id, firstCategory.Id, tail.Id], unifiedIds);
            var chatChannel = await owner.CreateChannelAsync(communityA.Id, "General Chat", firstCategory.Id);
            var disposable = await owner.CreateChannelAsync(communityA.Id, "Archive", firstCategory.Id);
            await owner.UpdateChannelAsync(communityA.Id, chatChannel.Id, "lounge", secondCategory.Id);
            await owner.MoveChannelAsync(communityA.Id, chatChannel.Id, null, 0);
            await owner.MoveChannelAsync(communityA.Id, chatChannel.Id, secondCategory.Id, 0);
            await owner.MoveChannelAsync(communityA.Id, chatChannel.Id, null, 0);
            await owner.DeleteCategoryAsync(communityA.Id, firstCategory.Id);
            var organized = await owner.GetCommunityStructureAsync(communityA.Id);
            Assert.Equal(secondCategory.Id, organized.Categories[0].Id);
            Assert.Equal("Topics", organized.Categories[0].Name);
            Assert.Null(organized.Channels.Single(value => value.Id == disposable.Id).CategoryId);
            Assert.Null(organized.Channels.Single(value => value.Id == chatChannel.Id).CategoryId);
            await owner.MoveChannelAsync(communityA.Id, disposable.Id, null, 0);
            Assert.Equal(disposable.Id, (await owner.GetCommunityStructureAsync(communityA.Id)).Channels
                .Where(value => value.CategoryId is null).OrderBy(value => value.Position).First().Id);
            await owner.DeleteChannelAsync(communityA.Id, disposable.Id);
            Assert.DoesNotContain((await owner.GetCommunityStructureAsync(communityA.Id)).Channels, value => value.Id == disposable.Id);

            var communityB = await outsider.CreateCommunityAsync(new CreateCommunityRequest("Beta", null));
            var outsiderChannel = await outsider.CreateChannelAsync(communityB.Id, "private", null);
            var forbiddenMove = await Assert.ThrowsAsync<NodeApiException>(() =>
                outsider.MoveChannelAsync(communityA.Id, chatChannel.Id, secondCategory.Id, 0));
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenMove.StatusCode);

            await using var firstConnection = Connection(nodeAddress, ownerAuth.AccessToken);
            await using var secondConnection = Connection(nodeAddress, secondOwnerAuth.AccessToken);
            await using var outsiderConnection = Connection(nodeAddress, outsiderAuth.AccessToken);
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
            var friendRequestOnOwner = Completion<FriendshipChangedEvent>();
            var friendAcceptedOnIntruder = Completion<FriendshipChangedEvent>();
            TaskCompletionSource<PresenceChangedEvent>? outsiderPresenceOnOwner = null;
            var mentionReceived = Completion<CommunityMentionReceivedEvent>();
            var mentionNotificationCount = 0;
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

            await Task.WhenAll(firstConnection.StartAsync(), secondConnection.StartAsync(), outsiderConnection.StartAsync(), intruderConnection.StartAsync());
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
            var directMessage = await firstConnection.InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.SendMessage, directConversation.Id,
                new SendDirectMessageRequest("private hello", null));
            Assert.Equal(directMessage.Id, (await directOnOutsider.Task.WaitAsync(TimeSpan.FromSeconds(5))).Id);
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

            var sent = await firstConnection.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.SendMessage, communityA.Id, chatChannel.Id,
                new SendChannelMessageRequest("hello from tab one", null));
            Assert.Equal(sent.Id, (await createdOnSecond.Task.WaitAsync(TimeSpan.FromSeconds(5))).Id);
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
            Assert.True(history.Single(value => value.Id == sent.Id).IsDeleted);
            Assert.True(history.Single(value => value.Id == reply.Id).ReplyTo?.IsDeleted);

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
            await DeleteDirectoryAsync(tempDirectory);
        }
    }

    private static HubConnection Connection(Uri nodeAddress, string token) => new HubConnectionBuilder()
        .WithUrl(new Uri(nodeAddress, "hubs/chat"), options => options.AccessTokenProvider = () => Task.FromResult<string?>(token))
        .Build();

    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
