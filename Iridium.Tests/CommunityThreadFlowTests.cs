using System.Diagnostics;
using System.Net.Sockets;
using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR.Client;

namespace Iridium.Tests;

[Collection(ForumIntegrationCollection.Name)]
public sealed class CommunityThreadFlowTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public CommunityThreadFlowTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;
    [Fact(Timeout = 90_000)]
    public async Task ThreadsSeparateSendingEnforcePrivacyAndReuseMessagePipeline()
    {
        var root = RepositoryRoot();
        var project = Path.Combine(root, "Iridium.Server", "Iridium.Server.csproj");
        var temp = Path.Combine(Path.GetTempPath(), $"iridium-threads-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var address = new Uri($"http://127.0.0.1:{FreePort()}/");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        using var server = StartServer(project, address, Path.Combine(temp, "threads.db"),
            Path.Combine(temp, "objects"), configuration);
        var output = server.StandardOutput.ReadToEndAsync(); var error = server.StandardError.ReadToEndAsync();
        try
        {
            await WaitForServerAsync(address, server, output, error);
            _output.WriteLine("server-ready");
            var owner = new NodeClient(address);
            var ownerAuth = await owner.RegisterAsync(new("thread-owner", "Owner", "test-password"));
            var member = new NodeClient(address);
            var memberAuth = await member.RegisterAsync(new("thread-member", "Member", "test-password"));
            var outsider = new NodeClient(address);
            await outsider.RegisterAsync(new("thread-outsider", "Outsider", "test-password"));
            var community = await owner.CreateCommunityAsync(new("Thread Server", null));
            _output.WriteLine("accounts-community-ready");
            var invite = await owner.CreateCommunityInviteAsync(community.Id, new(null, null));
            var token = CommunityInviteLink.Find(invite.InviteUrl!)!.Token;
            await member.JoinCommunityInviteAsync(token); await outsider.JoinCommunityInviteAsync(token);
            var channel = await owner.CreateChannelAsync(community.Id, "general", null, CommunityChannelKind.Text);
            _output.WriteLine("members-channel-ready");

            var rootMessage = await SendAsync(address, ownerAuth.AccessToken, community.Id, channel.Id, "root message");
            var thread = await member.CreateThreadAsync(community.Id, channel.Id,
                new("public discussion", CreatedFromMessageId: rootMessage.Id));
            _output.WriteLine("public-thread-ready");
            Assert.True(thread.IsJoined); Assert.False(thread.IsPrivate);
            await owner.SetPermissionOverwriteAsync(community.Id, PermissionOverwriteScopeType.Channel, channel.Id,
                new(PermissionOverwriteTargetType.Everyone, null, CommunityPermission.SendMessagesInThreads,
                    CommunityPermission.SendMessages));

            var memberChannel = (await member.GetCommunityStructureAsync(community.Id)).Channels
                .Single(value => value.Id == channel.Id);
            Assert.Equal(CommunityPermission.None,
                memberChannel.EffectivePermissions & CommunityPermission.SendMessages);
            Assert.Equal(CommunityPermission.SendMessagesInThreads,
                memberChannel.EffectivePermissions & CommunityPermission.SendMessagesInThreads);
            var reply = await SendAsync(address, memberAuth.AccessToken, community.Id,
                thread.DiscussionChannelId, "thread allowed");
            _output.WriteLine("permission-split-ready");
            Assert.Equal("thread allowed", reply.Content);
            var refreshed = await member.GetThreadAsync(community.Id, channel.Id, thread.Id);
            Assert.Equal(1, refreshed.ReplyCount);
            Assert.Contains((await member.GetCommunityJoinedThreadsAsync(community.Id)).Threads,
                value => value.Id == thread.Id);

            var parentHistory = await owner.GetChannelMessagePageAsync(community.Id, channel.Id);
            var sourceSummary = parentHistory.Messages.Single(value => value.Id == rootMessage.Id).Thread;
            Assert.Equal(thread.Id, sourceSummary?.ThreadId);
            Assert.Equal(1, sourceSummary?.ReplyCount);
            Assert.Equal("public discussion", sourceSummary?.Name);
            thread = await member.UpdateThreadAsync(community.Id, channel.Id, thread.Id,
                new(Name: "renamed discussion"));
            parentHistory = await owner.GetChannelMessagePageAsync(community.Id, channel.Id);
            Assert.Equal("renamed discussion",
                parentHistory.Messages.Single(value => value.Id == rootMessage.Id).Thread?.Name);
            var search = await member.SearchCommunityMessagesAsync(community.Id, "thread allowed", null, null);
            Assert.Equal(thread.Id, Assert.Single(search.Results).ThreadId);

            var privateRoot = await SendAsync(address, ownerAuth.AccessToken, community.Id, channel.Id,
                "private root");
            var privateThread = await owner.CreateThreadAsync(community.Id, channel.Id,
                new("private planning", true, privateRoot.Id));
            _output.WriteLine("private-thread-ready");
            await AssertNotFound(() => outsider.GetThreadAsync(community.Id, channel.Id, privateThread.Id));
            Assert.Null((await outsider.GetChannelMessagePageAsync(community.Id, channel.Id)).Messages
                .Single(value => value.Id == privateRoot.Id).Thread);
            await owner.AddThreadMemberAsync(community.Id, channel.Id, privateThread.Id, memberAuth.Account.Id);
            Assert.Equal(privateThread.Id,
                (await member.GetThreadAsync(community.Id, channel.Id, privateThread.Id)).Id);
            Assert.Equal(privateThread.Id, (await member.GetChannelMessagePageAsync(community.Id, channel.Id)).Messages
                .Single(value => value.Id == privateRoot.Id).Thread?.ThreadId);

            privateThread = await owner.UpdateThreadAsync(community.Id, channel.Id, privateThread.Id,
                new(IsLocked: true));
            Assert.True(privateThread.IsLocked); Assert.True(privateThread.IsArchived);
            privateThread = await owner.UpdateThreadAsync(community.Id, channel.Id, privateThread.Id,
                new(IsLocked: false, IsArchived: false));
            Assert.False(privateThread.IsLocked); Assert.False(privateThread.IsArchived);

            await owner.DeleteThreadAsync(community.Id, channel.Id, thread.Id);
            parentHistory = await owner.GetChannelMessagePageAsync(community.Id, channel.Id);
            Assert.Null(parentHistory.Messages.Single(value => value.Id == rootMessage.Id).Thread);
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            _output.WriteLine(await output);
            _output.WriteLine(await error);
            for (var attempt = 0; attempt < 20; attempt++)
                try { Directory.Delete(temp, true); break; }
                catch (IOException) when (attempt < 19) { await Task.Delay(100); }
        }
    }

    private static async Task<ChannelMessageDto> SendAsync(Uri address, string token, Guid communityId,
        Guid channelId, string content)
    {
        var hub = new HubConnectionBuilder().WithUrl(new Uri(address, "hubs/chat"), options =>
            options.AccessTokenProvider = () => Task.FromResult<string?>(token)).Build();
        try
        {
            await hub.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return await hub.InvokeAsync<ChannelMessageDto>(ChatHubContract.SendMessage, communityId, channelId,
                new SendChannelMessageRequest(content, null, ClientMessageId: Guid.NewGuid()))
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            try { await hub.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            try { await hub.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    private static async Task AssertNotFound(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<NodeApiException>(action);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, exception.StatusCode);
    }

    private static Process StartServer(string project, Uri address, string database, string storage,
        string configuration)
    {
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(project)! };
        var prebuilt = Environment.GetEnvironmentVariable("IRIDIUM_TEST_SERVER_PATH");
        if (!string.IsNullOrWhiteSpace(prebuilt)) start.ArgumentList.Add(prebuilt);
        else foreach (var argument in new[] { "run", "--project", project, "--no-build", "--configuration",
                         configuration, "--no-launch-profile" }) start.ArgumentList.Add(argument);
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
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Iridium.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate Iridium.sln.");
    }
}
