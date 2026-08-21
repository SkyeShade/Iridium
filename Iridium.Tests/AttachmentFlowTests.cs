using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using SkiaSharp;

namespace Iridium.Tests;

public sealed class AttachmentFlowTests
{
    [Fact(Timeout = 30_000)]
    public async Task AttachmentsAreLinkedRealtimeAndDownloadsAreConversationAuthorized()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var project = Path.Combine(root, "Iridium.Server", "Iridium.Server.csproj");
        var temp = Path.Combine(Path.GetTempPath(), $"iridium-attachments-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var address = new Uri($"http://127.0.0.1:{FreePort()}/");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        using var server = StartServer(project, address, Path.Combine(temp, "attachments.db"),
            Path.Combine(temp, "objects"), configuration);
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();
        try
        {
            await WaitForServerAsync(address, server, output, error);
            var owner = new NodeClient(address);
            var ownerAuth = await owner.RegisterAsync(new("attachment-owner", "Owner", "test-password"));
            var member = new NodeClient(address);
            var memberAuth = await member.RegisterAsync(new("attachment-member", "Member", "test-password"));
            var intruder = new NodeClient(address);
            var intruderAuth = await intruder.RegisterAsync(new("attachment-intruder", "Intruder", "test-password"));

            var community = await owner.CreateCommunityAsync(new("Files", null));
            var channel = Assert.Single((await owner.GetCommunityStructureAsync(community.Id)).Channels);
            var invite = await owner.CreateCommunityInviteAsync(community.Id, new(null, null));
            await member.JoinCommunityInviteAsync(CommunityInviteLink.Find(invite.InviteUrl!)!.Token);

            var imageBytes = CreateTransparentPng();
            var uploaded = await owner.UploadAttachmentAsync(new MemoryStream(imageBytes), "reference.png", "image/png",
                true, 640, 480, "#336699");
            Assert.Equal("image/png", uploaded.PreviewContentType);
            Assert.True(uploaded.PreviewSizeBytes > 0);
            Assert.Equal(16, uploaded.Width);
            Assert.Equal(8, uploaded.Height);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(temp, "objects")).Length);
            await using var ownerHub = Connection(address, ownerAuth.AccessToken);
            await using var memberHub = Connection(address, memberAuth.AccessToken);
            var received = new TaskCompletionSource<ChannelMessageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            memberHub.On<ChannelMessageDto>(ChatHubContract.MessageCreated, value => received.TrySetResult(value));
            await Task.WhenAll(ownerHub.StartAsync(), memberHub.StartAsync());
            await memberHub.InvokeAsync(ChatHubContract.JoinChannel, community.Id, channel.Id);
            var sent = await ownerHub.InvokeAsync<ChannelMessageDto>(ChatHubContract.SendMessage, community.Id, channel.Id,
                new SendChannelMessageRequest(string.Empty, null, ClientMessageId: Guid.NewGuid(), AttachmentIds: [uploaded.Id]));
            Assert.Equal(uploaded.Id, Assert.Single(sent.Attachments!).Id);
            Assert.True(Assert.Single(sent.Attachments!).IsSpoiler);
            Assert.Equal(16, Assert.Single(sent.Attachments!).Width);
            Assert.Equal(8, Assert.Single(sent.Attachments!).Height);
            Assert.Equal("image/png", Assert.Single(sent.Attachments!).PreviewContentType);
            Assert.Matches("^#[0-9A-F]{6}$", Assert.Single(sent.Attachments!).AverageColor!);
            var realtimeAttachment = Assert.Single((await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Attachments!);
            Assert.Equal(uploaded.Id, realtimeAttachment.Id);
            Assert.True(realtimeAttachment.IsSpoiler);
            Assert.Equal(imageBytes, await member.DownloadAttachmentAsync(uploaded.Id));
            var previewBytes = await member.DownloadAttachmentPreviewAsync(uploaded.Id);
            using (var previewBitmap = SKBitmap.Decode(previewBytes))
            {
                Assert.NotNull(previewBitmap);
                Assert.Equal(16, previewBitmap.Width);
                Assert.Equal(8, previewBitmap.Height);
                Assert.Contains(previewBitmap.Pixels, color => color.Alpha < byte.MaxValue);
            }
            Assert.Equal(HttpStatusCode.Forbidden,
                (await Assert.ThrowsAsync<NodeApiException>(() => intruder.DownloadAttachmentAsync(uploaded.Id))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await Assert.ThrowsAsync<NodeApiException>(() => intruder.DownloadAttachmentPreviewAsync(uploaded.Id))).StatusCode);

            var conversation = await owner.OpenDirectConversationAsync(memberAuth.Account.Id);
            var fileBytes = "private file"u8.ToArray();
            var directUpload = await owner.UploadAttachmentAsync(new MemoryStream(fileBytes), "notes.txt", "text/plain");
            var direct = await ownerHub.InvokeAsync<DirectMessageDto>(DirectMessageHubContract.SendMessage, conversation.Id,
                new SendDirectMessageRequest(string.Empty, null, Guid.NewGuid(), [directUpload.Id]));
            Assert.Equal(directUpload.Id, Assert.Single(direct.Attachments!).Id);
            Assert.Equal(fileBytes, await member.DownloadAttachmentAsync(directUpload.Id));
            Assert.Equal(HttpStatusCode.Forbidden,
                (await Assert.ThrowsAsync<NodeApiException>(() => intruder.DownloadAttachmentAsync(directUpload.Id))).StatusCode);

            var limits = await owner.GetServerInfoAsync();
            Assert.True(limits.MaxAttachmentBytes > 0);
            Assert.True(limits.MaxAttachmentsPerMessage > 0);
            var tooMany = new List<Guid>();
            for (var index = 0; index <= limits.MaxAttachmentsPerMessage; index++)
                tooMany.Add((await owner.UploadAttachmentAsync(new MemoryStream([1]), $"small-{index}.bin",
                    "application/octet-stream")).Id);
            await Assert.ThrowsAsync<HubException>(() => ownerHub.InvokeAsync<ChannelMessageDto>(
                ChatHubContract.SendMessage, community.Id, channel.Id,
                new SendChannelMessageRequest(string.Empty, null, ClientMessageId: Guid.NewGuid(), AttachmentIds: tooMany)));
            await Assert.ThrowsAsync<NodeApiException>(() => owner.UploadAttachmentAsync(
                new MemoryStream(new byte[limits.MaxAttachmentBytes + 1]), "oversized.bin", "application/octet-stream"));
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
        .WithUrl(new Uri(address, "hubs/chat"), options => options.AccessTokenProvider = () => Task.FromResult<string?>(token)).Build();

    private static Process StartServer(string project, Uri address, string database, string storage, string configuration)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "run", "--project", project, "--no-build", "--configuration", configuration, "--no-launch-profile" }) start.ArgumentList.Add(argument);
        start.Environment["ASPNETCORE_URLS"] = address.ToString().TrimEnd('/');
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__Iridium"] = $"Data Source={database}";
        start.Environment["Node__AttachmentStoragePath"] = storage;
        start.Environment["Node__MaxAttachmentBytes"] = (1024 * 1024).ToString();
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the Iridium test node.");
    }

    private static byte[] CreateTransparentPng()
    {
        using var bitmap = new SKBitmap(16, 8, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.Transparent);
        for (var y = 1; y < 7; y++)
        for (var x = 1; x < 15; x++)
            bitmap.SetPixel(x, y, new SKColor(51, 102, 153, 210));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static async Task WaitForServerAsync(Uri address, Process server, Task<string> output, Task<string> error)
    {
        using var http = new HttpClient { BaseAddress = address };
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (server.HasExited) throw new InvalidOperationException($"The test node stopped early.\n{await output}\n{await error}");
            try { if ((await http.GetAsync("api/server-info")).IsSuccessStatusCode) return; }
            catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("The test node did not become ready.");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
}
