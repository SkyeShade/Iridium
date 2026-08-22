using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR.Client;

namespace Iridium.Tests;

public sealed class CommunityMediaFlowTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact(Timeout = 60_000)]
    public async Task CommunityAvatarAndBannerPresetsEnforceLimitsActivateAndPublishIdentityChanges()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"iridium-community-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var address = new Uri($"http://127.0.0.1:{FreePort()}/");
        using var server = StartServer(address, Path.Combine(temp, "media.db"), Path.Combine(temp, "objects"));
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();
        try
        {
            await WaitForServerAsync(address, server, output, error);
            var owner = new NodeClient(address);
            var authentication = await owner.RegisterAsync(new("community-media-owner", "Owner", "test-password"));
            var community = await owner.CreateCommunityAsync(new("Media Community", null));
            var outsider = new NodeClient(address);
            await outsider.RegisterAsync(new("community-media-outsider", "Outsider", "test-password"));
            var forbidden = await Assert.ThrowsAsync<NodeApiException>(() => outsider.GetCommunityAvatarPresetsAsync(community.Id));
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            await using var hub = new HubConnectionBuilder().WithUrl(new Uri(address, "hubs/chat"), options =>
                options.AccessTokenProvider = () => Task.FromResult<string?>(authentication.AccessToken)).Build();
            var identityChanged = new TaskCompletionSource<CommunityStateChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            hub.On<CommunityStateChangedEvent>(CommunityHubContract.StateChanged, change =>
            {
                if (change.CommunityId == community.Id && change.Change == "identity-updated") identityChanged.TrySetResult(change);
            });
            await hub.StartAsync();

            AccountAvatarPresetsDto avatars = null!;
            for (var slot = 0; slot < CommunityMediaLimits.MaximumAvatarPresets; slot++)
                avatars = await owner.UploadCommunityAvatarPresetAsync(community.Id, slot, new MemoryStream(Png),
                    $"icon-{slot}.png", "image/png", 0, 0, 1);
            Assert.Equal(10, avatars.Presets.Count);
            Assert.Equal(avatars.Presets.Single(value => value.SlotIndex == 9).Id, avatars.ActiveAvatarPresetId);
            await Assert.ThrowsAsync<NodeApiException>(() => owner.UploadCommunityAvatarPresetAsync(community.Id, 10,
                new MemoryStream(Png), "eleventh.png", "image/png", 0, 0, 1));
            Assert.True((await identityChanged.Task.WaitAsync(TimeSpan.FromSeconds(5))).Revision > 0);

            AccountBannerPresetsDto banners = null!;
            for (var slot = 0; slot < CommunityMediaLimits.MaximumBannerPresets; slot++)
                banners = await owner.UploadCommunityBannerPresetAsync(community.Id, slot, new MemoryStream(Png),
                    $"banner-{slot}.png", "image/png", 0, 0, 1);
            Assert.Equal(4, banners.Presets.Count);
            Assert.Equal(banners.Presets.Single(value => value.SlotIndex == 3).Id, banners.ActiveBannerPresetId);
            await Assert.ThrowsAsync<NodeApiException>(() => owner.UploadCommunityBannerPresetAsync(community.Id, 4,
                new MemoryStream(Png), "fifth.png", "image/png", 0, 0, 1));

            var listed = (await owner.GetCommunitiesAsync()).Single(value => value.Id == community.Id);
            Assert.NotNull(listed.AvatarUrl);
            Assert.NotNull(listed.BannerUrl);
            Assert.True(listed.AvatarRevision > 0);
            Assert.True(listed.BannerRevision > 0);

            var offCenter = avatars.Presets.Single(value => value.SlotIndex == 9);
            await owner.UpdateCommunityAvatarCropAsync(community.Id, offCenter.Id, new(.88, -.72, 2.25, true));
            listed = (await owner.GetCommunitiesAsync()).Single(value => value.Id == community.Id);
            Assert.Equal(.88, listed.AvatarCropX, 6);
            Assert.Equal(-.72, listed.AvatarCropY, 6);
            Assert.Equal(2.25, listed.AvatarZoom, 6);

            await owner.DeleteCommunityMediaPresetAsync(community.Id, "avatar", avatars.ActiveAvatarPresetId!.Value);
            avatars = await owner.GetCommunityAvatarPresetsAsync(community.Id);
            Assert.Equal(9, avatars.Presets.Count);
            Assert.Equal(avatars.Presets.OrderBy(value => value.SlotIndex).First().Id, avatars.ActiveAvatarPresetId);
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
    public async Task CommunityEmojiLimitsAuthorizationNamingMediaAndRealtimeWork()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"iridium-community-emojis-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var address = new Uri($"http://127.0.0.1:{FreePort()}/");
        using var server = StartServer(address, Path.Combine(temp, "emoji.db"), Path.Combine(temp, "objects"));
        var output = server.StandardOutput.ReadToEndAsync(); var error = server.StandardError.ReadToEndAsync();
        try
        {
            await WaitForServerAsync(address, server, output, error);
            var owner = new NodeClient(address);
            var authentication = await owner.RegisterAsync(new("emoji-owner", "Emoji Owner", "test-password"));
            var community = await owner.CreateCommunityAsync(new("Emoji Community", null));
            var outsider = new NodeClient(address); await outsider.RegisterAsync(new("emoji-outsider", "Outsider", "test-password"));
            var denied = await Assert.ThrowsAsync<NodeApiException>(() => outsider.UploadCommunityEmojiAsync(community.Id,
                new MemoryStream(Png), "nope.png", "image/png", "nope"));
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            var tooLarge = await Assert.ThrowsAsync<NodeApiException>(() => owner.UploadCommunityEmojiAsync(community.Id,
                new MemoryStream(new byte[CommunityEmojiLimits.MaximumUploadBytes + 1]), "large.png", "image/png", "large"));
            Assert.Contains("0.50 MB", tooLarge.Message);

            await using var hub = new HubConnectionBuilder().WithUrl(new Uri(address, "hubs/chat"), options =>
                options.AccessTokenProvider = () => Task.FromResult<string?>(authentication.AccessToken)).Build();
            var changed = new TaskCompletionSource<CommunityStateChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            hub.On<CommunityStateChangedEvent>(CommunityHubContract.StateChanged, value =>
            { if (value.Change == "expressions-updated") changed.TrySetResult(value); });
            await hub.StartAsync();

            CommunityEmojiDto? first = null;
            for (var index = 0; index < CommunityEmojiLimits.MaximumPerCommunity; index++)
            {
                var emoji = await owner.UploadCommunityEmojiAsync(community.Id, new MemoryStream(Png),
                    $"custom-{index}.png", "image/png", $"custom_{index}");
                Assert.Equal("image/webp", emoji.ContentType);
                first ??= emoji;
            }
            Assert.Equal(100, (await owner.GetCommunityEmojisAsync(community.Id)).Count);
            await Assert.ThrowsAsync<NodeApiException>(() => owner.UploadCommunityEmojiAsync(community.Id,
                new MemoryStream(Png), "overflow.png", "image/png", "overflow"));
            Assert.Equal("renamed", (await owner.RenameCommunityEmojiAsync(community.Id, first!.Id, "Renamed!")).Name);
            Assert.NotEmpty(await owner.DownloadCommunityEmojiAsync(community.Id, first.Id));
            Assert.True((await changed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Revision > 0);
            await owner.DeleteCommunityEmojiAsync(community.Id, first.Id);
            Assert.Equal(99, (await owner.GetCommunityEmojisAsync(community.Id)).Count);
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            for (var attempt = 0; attempt < 20; attempt++)
            { try { Directory.Delete(temp, true); break; } catch (IOException) when (attempt < 19) { await Task.Delay(100); } }
        }
    }

    private static Process StartServer(Uri address, string database, string storage)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Iridium.Server.dll"));
        start.Environment["ASPNETCORE_URLS"] = address.ToString().TrimEnd('/');
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__Iridium"] = $"Data Source={database}";
        start.Environment["Node__AttachmentStoragePath"] = storage;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the test node.");
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
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
