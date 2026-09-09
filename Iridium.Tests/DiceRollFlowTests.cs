using System.Diagnostics;
using System.Net.Sockets;
using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;

namespace Iridium.Tests;

[Collection(ForumIntegrationCollection.Name)]
public sealed class DiceRollFlowTests
{
    [Fact(Timeout = 90_000)]
    public async Task ServerRollsPersistBroadcastAndUseExistingAuthorizationAcrossEveryMessageHost()
    {
        var root = RepositoryRoot();
        var project = Path.Combine(root, "Iridium.Server", "Iridium.Server.csproj");
        var temp = Path.Combine(Path.GetTempPath(), $"iridium-dice-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var database = Path.Combine(temp, "dice.db");
        var address = new Uri($"http://127.0.0.1:{FreePort()}/");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        using var server = StartServer(project, address, database, Path.Combine(temp, "objects"), configuration);
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();
        try
        {
            await WaitForServerAsync(address, server, output, error);
            var owner = new NodeClient(address);
            var ownerAuth = await owner.RegisterAsync(new("dice-owner", "Dice Owner", "test-password"));
            var member = new NodeClient(address);
            var memberAuth = await member.RegisterAsync(new("dice-member", "Dice Member", "test-password"));
            var outsider = new NodeClient(address);
            var outsiderAuth = await outsider.RegisterAsync(new("dice-outsider", "Dice Outsider", "test-password"));
            var community = await owner.CreateCommunityAsync(new("Dice Server", null));
            var invite = await owner.CreateCommunityInviteAsync(community.Id, new(null, null));
            await member.JoinCommunityInviteAsync(CommunityInviteLink.Find(invite.InviteUrl!)!.Token);
            var channel = await owner.CreateChannelAsync(community.Id, "dice-table", null, CommunityChannelKind.Text);

            await using var ownerHub = Connection(address, ownerAuth.AccessToken);
            await using var memberHub = Connection(address, memberAuth.AccessToken);
            await using var outsiderHub = Connection(address, outsiderAuth.AccessToken);
            await Task.WhenAll(ownerHub.StartAsync(), memberHub.StartAsync(), outsiderHub.StartAsync());
            await memberHub.InvokeAsync(ChatHubContract.JoinChannel, community.Id, channel.Id);
            var broadcast = new TaskCompletionSource<ChannelMessageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            memberHub.On<ChannelMessageDto>(ChatHubContract.MessageCreated, value =>
            {
                if (value.Kind == MessageKind.DiceRoll) broadcast.TrySetResult(value);
            });

            var channelRoll = await RollChannelAsync(ownerHub, community.Id, channel.Id, "/roll 2D20");
            var witnessed = await broadcast.Task.WaitAsync(TimeSpan.FromSeconds(10));
            AssertRoll(channelRoll, ownerAuth.Account.Id, 2, 20);
            Assert.Equal(channelRoll.Id, witnessed.Id);
            Assert.Equal(channelRoll.DiceRoll!.Rolls, witnessed.DiceRoll!.Rolls);
            Assert.DoesNotContain("/roll", channelRoll.Content, StringComparison.OrdinalIgnoreCase);

            var historyRoll = Assert.Single((await owner.GetChannelMessagePageAsync(community.Id, channel.Id)).Messages,
                value => value.Id == channelRoll.Id);
            Assert.Equal(channelRoll.DiceRoll.Rolls, historyRoll.DiceRoll!.Rolls);
            Assert.Equal(channelRoll.DiceRoll.Total, historyRoll.DiceRoll.Total);
            var beforeInvalid = (await owner.GetChannelMessagePageAsync(community.Id, channel.Id)).Messages.Count;
            await Assert.ThrowsAsync<HubException>(() => RollChannelAsync(ownerHub, community.Id, channel.Id, "/roll 0d20"));
            Assert.Equal(beforeInvalid, (await owner.GetChannelMessagePageAsync(community.Id, channel.Id)).Messages.Count);
            var reaction = await ownerHub.InvokeAsync<ReactionSummaryDto>(ChatHubContract.AddReaction,
                channelRoll.Id, new ReactionEmojiRequest(ReactionEmojiKind.Standard, "👍"));
            Assert.Equal(1, reaction.Count);

            var rootMessage = await ownerHub.InvokeAsync<ChannelMessageDto>(ChatHubContract.SendMessage,
                community.Id, channel.Id, new SendChannelMessageRequest("thread root", null, ClientMessageId: Guid.NewGuid()));
            var thread = await owner.CreateThreadAsync(community.Id, channel.Id,
                new("dice thread", CreatedFromMessageId: rootMessage.Id));
            await owner.SetPermissionOverwriteAsync(community.Id, PermissionOverwriteScopeType.Channel, channel.Id,
                new(PermissionOverwriteTargetType.Member, memberAuth.Account.Id,
                    CommunityPermission.SendMessagesInThreads, CommunityPermission.SendMessages));
            await Assert.ThrowsAsync<HubException>(() => RollChannelAsync(memberHub, community.Id, channel.Id, "/roll 1d6"));
            var threadRoll = await RollChannelAsync(memberHub, community.Id, thread.DiscussionChannelId, "/roll 1d6");
            AssertRoll(threadRoll, memberAuth.Account.Id, 1, 6);

            var forum = await owner.CreateChannelAsync(community.Id, "dice-forum", null, CommunityChannelKind.Forum);
            var post = await owner.CreateForumPostAsync(community.Id, forum.Id,
                new("Dice topic", new("opening post", null, ClientMessageId: Guid.NewGuid())));
            var forumRoll = await RollChannelAsync(ownerHub, community.Id, post.DiscussionChannelId, "/roll 3d8");
            AssertRoll(forumRoll, ownerAuth.Account.Id, 3, 8);

            var direct = await owner.OpenDirectConversationAsync(memberAuth.Account.Id);
            var directRoll = await ownerHub.InvokeAsync<DirectMessageDto>(DirectMessageHubContract.SendMessage,
                direct.Id, new SendDirectMessageRequest("/roll 4d10", null, Guid.NewGuid()));
            AssertRoll(directRoll, ownerAuth.Account.Id, 4, 10);
            var directHistory = Assert.Single((await owner.GetDirectMessagePageAsync(direct.Id)).Messages,
                value => value.Id == directRoll.Id);
            Assert.Equal(directRoll.DiceRoll!.Rolls, directHistory.DiceRoll!.Rolls);
            var forwarded = await ownerHub.InvokeAsync<ForwardMessagesResultDto>(ChatHubContract.ForwardMessage,
                new ForwardMessageRequest(new(MessageLocationKind.CommunityChannel, channelRoll.Id,
                        community.Id, channel.Id),
                    [new(MessageLocationKind.DirectConversation, ConversationId: direct.Id)]));
            var rollSnapshot = Assert.Single(forwarded.DirectMessages).Forwarded!;
            Assert.Equal(MessageKind.DiceRoll, rollSnapshot.Kind);
            Assert.Equal(channelRoll.DiceRoll.Rolls, rollSnapshot.DiceRoll!.Rolls);
            Assert.Equal(channelRoll.DiceRoll.Total, rollSnapshot.DiceRoll.Total);
            await Assert.ThrowsAsync<HubException>(() => outsiderHub.InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.SendMessage, direct.Id,
                new SendDirectMessageRequest("/roll 1d20", null, Guid.NewGuid())));

            await Assert.ThrowsAsync<HubException>(() => ownerHub.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.EditMessage, community.Id, channel.Id, channelRoll.Id,
                new EditChannelMessageRequest("changed")));
            await ownerHub.InvokeAsync(ChatHubContract.DeleteMessage, community.Id, channel.Id, channelRoll.Id);
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            SqliteConnection.ClearAllPools();
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try { Directory.Delete(temp, true); break; }
                catch (IOException) when (attempt < 19) { await Task.Delay(100); }
            }
        }
    }

    private static Task<ChannelMessageDto> RollChannelAsync(HubConnection hub, Guid communityId, Guid channelId,
        string command) => hub.InvokeAsync<ChannelMessageDto>(ChatHubContract.SendMessage, communityId, channelId,
        new SendChannelMessageRequest(command, null, ClientMessageId: Guid.NewGuid()));

    private static void AssertRoll(ChannelMessageDto message, Guid authorId, int count, int sides)
    {
        Assert.Equal(MessageKind.DiceRoll, message.Kind);
        Assert.Equal(authorId, message.Author.AccountId);
        Assert.NotNull(message.DiceRoll);
        Assert.Equal(count, message.DiceRoll.DiceCount);
        Assert.Equal(sides, message.DiceRoll.Sides);
        Assert.Equal(count, message.DiceRoll.Rolls.Count);
        Assert.All(message.DiceRoll.Rolls, value => Assert.InRange(value, 1, sides));
        Assert.Equal(message.DiceRoll.Rolls.Sum(value => (long)value), message.DiceRoll.Total);
        Assert.Equal(message.DiceRoll.Rolls.Min(), message.DiceRoll.Minimum);
        Assert.Equal(message.DiceRoll.Rolls.Max(), message.DiceRoll.Maximum);
    }

    private static void AssertRoll(DirectMessageDto message, Guid authorId, int count, int sides)
    {
        Assert.Equal(MessageKind.DiceRoll, message.Kind);
        Assert.Equal(authorId, message.Author.AccountId);
        Assert.NotNull(message.DiceRoll);
        Assert.Equal(count, message.DiceRoll.DiceCount);
        Assert.Equal(sides, message.DiceRoll.Sides);
        Assert.Equal(count, message.DiceRoll.Rolls.Count);
        Assert.All(message.DiceRoll.Rolls, value => Assert.InRange(value, 1, sides));
        Assert.Equal(message.DiceRoll.Rolls.Sum(value => (long)value), message.DiceRoll.Total);
    }

    private static HubConnection Connection(Uri address, string token) => new HubConnectionBuilder()
        .WithUrl(new Uri(address, "hubs/chat"), options =>
            options.AccessTokenProvider = () => Task.FromResult<string?>(token)).Build();

    private static Process StartServer(string project, Uri address, string database, string storage, string configuration)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(project)!
        };
        foreach (var argument in new[] { "run", "--project", project, "--no-build", "--configuration", configuration,
                     "--no-launch-profile" }) start.ArgumentList.Add(argument);
        start.Environment["ASPNETCORE_URLS"] = address.ToString().TrimEnd('/');
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__Iridium"] = $"Data Source={database}";
        start.Environment["Node__AttachmentStoragePath"] = storage;
        start.Environment["Deployment__UseHttpsRedirection"] = "false";
        start.Environment["Logging__EventLog__LogLevel__Default"] = "None";
        return Process.Start(start)!;
    }

    private static async Task WaitForServerAsync(Uri address, Process server, Task<string> output, Task<string> error)
    {
        using var http = new HttpClient { BaseAddress = address };
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (server.HasExited) throw new InvalidOperationException($"Server exited.\n{await output}\n{await error}");
            try { if ((await http.GetAsync("health")).IsSuccessStatusCode) return; } catch (HttpRequestException) { }
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

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Iridium.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate Iridium.sln.");
    }
}
