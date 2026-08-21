using System.Diagnostics;
using System.Net.Sockets;
using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Iridium.Tests;

public sealed class MessageLimitFlowTests
{
    [Fact(Timeout = 30_000)]
    public async Task NodeRuneLimitIsExposedAndEnforcedForCommunityAndDirectMessages()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var project = Path.Combine(root, "Iridium.Server", "Iridium.Server.csproj");
        var temp = Path.Combine(Path.GetTempPath(), $"iridium-message-limit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var address = new Uri($"http://127.0.0.1:{FreePort()}/");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        using var server = StartServer(project, address, Path.Combine(temp, "limits.db"), configuration);
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();
        try
        {
            await WaitForServerAsync(address, server, output, error);
            var owner = new NodeClient(address);
            var ownerAuth = await owner.RegisterAsync(new("limit-owner", "Owner", "test-password"));
            var other = new NodeClient(address);
            var otherAuth = await other.RegisterAsync(new("limit-other", "Other", "test-password"));
            var community = await owner.CreateCommunityAsync(new("Limits", null));
            var channel = Assert.Single((await owner.GetCommunityStructureAsync(community.Id)).Channels);
            var conversation = await owner.OpenDirectConversationAsync(otherAuth.Account.Id);

            var info = await owner.GetServerInfoAsync();
            Assert.Equal(4, info.MaxMessageCharacters);
            Assert.Equal(200L * 1024 * 1024, info.MaxAttachmentBytes);
            var management = await owner.GetCommunityManagementAsync(community.Id);
            Assert.Equal(4, management.Limits.MaxMessageCharacters);
            Assert.Equal(info.MaxAttachmentBytes, management.Limits.MaxAttachmentBytes);
            Assert.Equal(info.MaxAttachmentsPerMessage, management.Limits.MaxAttachmentsPerMessage);

            await using var hub = new HubConnectionBuilder().WithUrl(new Uri(address, "hubs/chat"), options =>
                options.AccessTokenProvider = () => Task.FromResult<string?>(ownerAuth.AccessToken)).Build();
            await hub.StartAsync();

            const string exactlyFourRunes = "A😀BC";
            Assert.Equal(4, MessageText.CountCharacters(exactlyFourRunes));
            await hub.InvokeAsync<ChannelMessageDto>(ChatHubContract.SendMessage, community.Id, channel.Id,
                new SendChannelMessageRequest(exactlyFourRunes, null, ClientMessageId: Guid.NewGuid()));
            await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<ChannelMessageDto>(ChatHubContract.SendMessage,
                community.Id, channel.Id, new SendChannelMessageRequest(exactlyFourRunes + "D", null,
                    ClientMessageId: Guid.NewGuid())));

            await hub.InvokeAsync<DirectMessageDto>(DirectMessageHubContract.SendMessage, conversation.Id,
                new SendDirectMessageRequest(exactlyFourRunes, null, Guid.NewGuid()));
            await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.SendMessage, conversation.Id,
                new SendDirectMessageRequest(exactlyFourRunes + "D", null, Guid.NewGuid())));

            Assert.Single(await owner.GetChannelMessagesAsync(community.Id, channel.Id));
            Assert.Single(await owner.GetDirectMessagesAsync(conversation.Id));
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

    private static Process StartServer(string project, Uri address, string database, string configuration)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "run", "--project", project, "--no-build", "--configuration", configuration, "--no-launch-profile" })
            start.ArgumentList.Add(argument);
        start.Environment["ASPNETCORE_URLS"] = address.ToString().TrimEnd('/');
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__Iridium"] = $"Data Source={database}";
        start.Environment["Node__MaxMessageCharacters"] = "4";
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the Iridium test Node.");
    }

    private static async Task WaitForServerAsync(Uri address, Process server, Task<string> output, Task<string> error)
    {
        using var http = new HttpClient { BaseAddress = address };
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (server.HasExited) throw new InvalidOperationException($"The test Node stopped early.\n{await output}\n{await error}");
            try { if ((await http.GetAsync("api/server-info")).IsSuccessStatusCode) return; }
            catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("The test Node did not become ready.");
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
