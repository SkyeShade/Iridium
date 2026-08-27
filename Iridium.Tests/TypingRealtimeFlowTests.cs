using System.Diagnostics;
using System.Net.Sockets;
using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Iridium.Tests;

public sealed class TypingRealtimeFlowTests
{
    [Fact(Timeout = 30_000)]
    public async Task CommunityAndDirectTypingAreAuthorizedScopedAndUseBaseDisplayName()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var project = Path.Combine(root, "Iridium.Server", "Iridium.Server.csproj");
        var temp = Path.Combine(Path.GetTempPath(), $"iridium-typing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var address = new Uri($"http://127.0.0.1:{FreePort()}/");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        using var server = StartServer(project, address, Path.Combine(temp, "typing.db"),
            Path.Combine(temp, "objects"), configuration);
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();
        try
        {
            await WaitForServerAsync(address, server, output, error);
            var owner = new NodeClient(address);
            var ownerAuth = await owner.RegisterAsync(new("typing-owner", "Skye", "test-password"));
            var member = new NodeClient(address);
            var memberAuth = await member.RegisterAsync(new("typing-member", "Alice", "test-password"));
            var intruder = new NodeClient(address);
            var intruderAuth = await intruder.RegisterAsync(new("typing-intruder", "Mallory", "test-password"));
            var community = await owner.CreateCommunityAsync(new("Typing", null));
            var channel = Assert.Single((await owner.GetCommunityStructureAsync(community.Id)).Channels);
            var invite = await owner.CreateCommunityInviteAsync(community.Id, new(null, null));
            await member.JoinCommunityInviteAsync(CommunityInviteLink.Find(invite.InviteUrl!)!.Token);
            var direct = await owner.OpenDirectConversationAsync(memberAuth.Account.Id);

            await using var ownerHub = Connection(address, ownerAuth.AccessToken);
            await using var memberHub = Connection(address, memberAuth.AccessToken);
            await using var intruderHub = Connection(address, intruderAuth.AccessToken);
            var received = System.Threading.Channels.Channel.CreateUnbounded<TypingActivityEvent>();
            memberHub.On<TypingActivityEvent>(TypingHubContract.Changed, value => received.Writer.TryWrite(value));
            await Task.WhenAll(ownerHub.StartAsync(), memberHub.StartAsync(), intruderHub.StartAsync());
            await Task.WhenAll(
                ownerHub.InvokeAsync(ChatHubContract.JoinChannel, community.Id, channel.Id),
                memberHub.InvokeAsync(ChatHubContract.JoinChannel, community.Id, channel.Id));

            var channelTarget = new TypingConversationDto(TypingConversationKind.CommunityChannel,
                channel.Id, community.Id);
            var sessionId = Guid.NewGuid();
            await ownerHub.InvokeAsync(TypingHubContract.SetActivity,
                new SetTypingActivityRequest(channelTarget, sessionId, true));
            var started = await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ownerAuth.Account.Id, started.AccountId);
            Assert.Equal("Skye", started.DisplayName);
            Assert.True(started.IsTyping);
            Assert.Equal(channelTarget, started.Conversation);

            await ownerHub.InvokeAsync(TypingHubContract.SetActivity,
                new SetTypingActivityRequest(channelTarget, sessionId, false));
            Assert.False((await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsTyping);
            await Assert.ThrowsAsync<HubException>(() => intruderHub.InvokeAsync(TypingHubContract.SetActivity,
                new SetTypingActivityRequest(channelTarget, Guid.NewGuid(), true)));

            await Task.WhenAll(
                ownerHub.InvokeAsync(DirectMessageHubContract.JoinConversation, direct.Id),
                memberHub.InvokeAsync(DirectMessageHubContract.JoinConversation, direct.Id));
            var directTarget = new TypingConversationDto(TypingConversationKind.DirectConversation, direct.Id);
            await ownerHub.InvokeAsync(TypingHubContract.SetActivity,
                new SetTypingActivityRequest(directTarget, sessionId, true));
            var directStarted = await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(directTarget, directStarted.Conversation);
            Assert.True(directStarted.IsTyping);
            await ownerHub.InvokeAsync<DirectMessageDto>(DirectMessageHubContract.SendMessage, direct.Id,
                new SendDirectMessageRequest("sent", null, Guid.NewGuid()));
            var stoppedBySend = await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(directTarget, stoppedBySend.Conversation);
            Assert.False(stoppedBySend.IsTyping);
            await Assert.ThrowsAsync<HubException>(() => intruderHub.InvokeAsync(TypingHubContract.SetActivity,
                new SetTypingActivityRequest(directTarget, Guid.NewGuid(), true)));
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

    private static HubConnection Connection(Uri address, string token) => new HubConnectionBuilder()
        .WithUrl(new Uri(address, "hubs/chat"), options =>
            options.AccessTokenProvider = () => Task.FromResult<string?>(token)).Build();

    private static Process StartServer(string project, Uri address, string database, string storage,
        string configuration)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "run", "--project", project, "--no-build", "--configuration",
                     configuration, "--no-launch-profile" })
            start.ArgumentList.Add(argument);
        start.Environment["ASPNETCORE_URLS"] = address.ToString().TrimEnd('/');
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__Iridium"] = $"Data Source={database}";
        start.Environment["Node__AttachmentStoragePath"] = storage;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the Iridium test node.");
    }

    private static async Task WaitForServerAsync(Uri address, Process server, Task<string> output, Task<string> error)
    {
        using var http = new HttpClient { BaseAddress = address };
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (server.HasExited)
                throw new InvalidOperationException($"The test node stopped early.\n{await output}\n{await error}");
            try { if ((await http.GetAsync("api/server-info")).IsSuccessStatusCode) return; }
            catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("The test node did not become ready.");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
