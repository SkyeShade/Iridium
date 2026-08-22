using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using SkiaSharp;

namespace Iridium.Tests;

public sealed class AvatarPresetFlowTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static readonly byte[] AnimatedGif = Convert.FromHexString(
        "47494638396101000100800000000000FFFFFF" +
        "21F904000A0000002C0000000001000100000202440100" +
        "21F904000A0000002C00000000010001000002024401003B");

    [Fact(Timeout = 30_000)]
    public async Task TenSlotsSaveActivationOwnershipGifAndActiveDeleteFallbackWork()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var project = Path.Combine(root, "Iridium.Server", "Iridium.Server.csproj");
        var temp = Path.Combine(Path.GetTempPath(), $"iridium-avatars-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var address = new Uri($"http://127.0.0.1:{FreePort()}/");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        using var server = StartServer(project, address, Path.Combine(temp, "avatars.db"),
            Path.Combine(temp, "objects"), configuration);
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();
        try
        {
            await WaitForServerAsync(address, server, output, error);
            var owner = new NodeClient(address);
            var authentication = await owner.RegisterAsync(new("avatar-owner", "Avatar Owner", "test-password"));
            var other = new NodeClient(address);
            var otherAuthentication = await other.RegisterAsync(new("avatar-other", "Avatar Other", "test-password"));
            await owner.OpenDirectConversationAsync(otherAuthentication.Account.Id);
            Assert.Empty((await owner.GetAvatarPresetsAsync()).Presets);

            var jpeg = Encode(SKEncodedImageFormat.Jpeg);
            var webp = Encode(SKEncodedImageFormat.Webp);
            var uploads = new[]
            {
                (Bytes: Png, Name: "regular.png", Type: "image/png"),
                (Bytes: jpeg, Name: "photo.jpg", Type: "image/jpeg"),
                (Bytes: webp, Name: "picture.webp", Type: "image/webp"),
                (Bytes: Png, Name: "slot-3.png", Type: "image/png"),
                (Bytes: Png, Name: "slot-4.png", Type: "image/png"),
                (Bytes: Png, Name: "slot-5.png", Type: "image/png"),
                (Bytes: Png, Name: "slot-6.png", Type: "image/png"),
                (Bytes: Png, Name: "slot-7.png", Type: "image/png"),
                (Bytes: Png, Name: "slot-8.png", Type: "image/png"),
                (Bytes: Png, Name: "slot-9.png", Type: "image/png")
            };
            AccountAvatarPresetsDto state = null!;
            for (var slot = 0; slot < uploads.Length; slot++)
                state = await owner.UploadAvatarPresetAsync(slot, new MemoryStream(uploads[slot].Bytes),
                    uploads[slot].Name, uploads[slot].Type, 0, 0, 1, slot == 0);
            Assert.Equal(ProfileAvatarLimits.MaximumPresets, state.Presets.Count);
            Assert.Contains(state.Presets, value => value.ContentType == "image/png");
            Assert.Contains(state.Presets, value => value.ContentType == "image/jpeg");
            Assert.Contains(state.Presets, value => value.ContentType == "image/webp");
            Assert.Equal(state.Presets.Single(value => value.SlotIndex == 0).Id, state.ActiveAvatarPresetId);
            await Assert.ThrowsAsync<NodeApiException>(() => owner.UploadAvatarPresetAsync(10,
                new MemoryStream(Png), "eleventh.png", "image/png", 0, 0, 1, false));

            var selected = state.Presets.Single(value => value.SlotIndex == 6);
            await using var hub = new HubConnectionBuilder().WithUrl(new Uri(address, "hubs/chat"), options =>
                options.AccessTokenProvider = () => Task.FromResult<string?>(authentication.AccessToken)).Build();
            var updated = new TaskCompletionSource<ProfileUpdatedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            hub.On<ProfileUpdatedEvent>(ProfileHubContract.Updated, value => updated.TrySetResult(value));
            await hub.StartAsync();
            await owner.UpdateAvatarCropAsync(selected.Id, new(0, 0, 1.2, true));
            Assert.Equal(authentication.Account.Id,
                (await updated.Task.WaitAsync(TimeSpan.FromSeconds(5))).AccountId);
            state = await owner.GetAvatarPresetsAsync();
            Assert.Equal(selected.Id, state.ActiveAvatarPresetId);

            await using var otherHub = new HubConnectionBuilder().WithUrl(new Uri(address, "hubs/chat"), options =>
                options.AccessTokenProvider = () => Task.FromResult<string?>(otherAuthentication.AccessToken)).Build();
            var profileChanged = new TaskCompletionSource<ProfileUpdatedEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            otherHub.On<ProfileUpdatedEvent>(ProfileHubContract.Updated,
                value => profileChanged.TrySetResult(value));
            await otherHub.StartAsync();
            await owner.UpdateProfileAsync(new("Avatar Owner Updated", "they/them", "Realtime profile details"));
            var profileEvent = await profileChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(authentication.Account.Id, profileEvent.AccountId);
            Assert.Equal("Avatar Owner Updated", profileEvent.DisplayName);
            Assert.Equal("they/them", profileEvent.Pronouns);
            Assert.Equal("Realtime profile details", profileEvent.Description);

            var ownerPreset = state.Presets[0];
            var ownershipFailure = await Assert.ThrowsAsync<NodeApiException>(() => other.UpdateAvatarCropAsync(
                ownerPreset.Id, new(0, 0, 1.5, false)));
            Assert.Equal(HttpStatusCode.NotFound, ownershipFailure.StatusCode);

            await owner.DeleteAvatarPresetAsync(state.Presets.Single(value => value.SlotIndex == 9).Id);
            state = await owner.UploadAvatarPresetAsync(9, new MemoryStream(AnimatedGif), "animated.gif",
                "image/gif", 0, 0, 1, false);
            var gif = state.Presets.Single(value => value.SlotIndex == 9);
            Assert.Equal("image/gif", gif.ContentType);
            using (var http = new HttpClient())
                Assert.Equal(AnimatedGif, await http.GetByteArrayAsync(gif.AvatarUrl));

            await owner.DeleteAvatarPresetAsync(selected.Id);
            state = await owner.GetAvatarPresetsAsync();
            Assert.Equal(9, state.Presets.Count);
            Assert.Equal(state.Presets.OrderBy(value => value.SlotIndex).First().Id, state.ActiveAvatarPresetId);
            Assert.True((await owner.GetProfileAvatarAsync(authentication.Account.Id)).HasAvatar);
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

    [Fact(Timeout = 30_000)]
    public async Task FourBannerSlotsFormatsActivationRealtimeDerivativeAndDeleteFallbackWork()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var project = Path.Combine(root, "Iridium.Server", "Iridium.Server.csproj");
        var temp = Path.Combine(Path.GetTempPath(), $"iridium-banners-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var address = new Uri($"http://127.0.0.1:{FreePort()}/");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        using var server = StartServer(project, address, Path.Combine(temp, "banners.db"),
            Path.Combine(temp, "objects"), configuration);
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();
        try
        {
            await WaitForServerAsync(address, server, output, error);
            var client = new NodeClient(address);
            var authentication = await client.RegisterAsync(new("banner-owner", "Banner Owner", "test-password"));
            var jpeg = Encode(SKEncodedImageFormat.Jpeg);
            var webp = Encode(SKEncodedImageFormat.Webp);
            var uploads = new[]
            {
                (Bytes: Png, Name: "banner.png", Type: "image/png"),
                (Bytes: jpeg, Name: "banner.jpg", Type: "image/jpeg"),
                (Bytes: webp, Name: "banner.webp", Type: "image/webp"),
                (Bytes: Png, Name: "banner-4.png", Type: "image/png")
            };
            AccountBannerPresetsDto state = null!;
            for (var slot = 0; slot < uploads.Length; slot++)
                state = await client.UploadBannerPresetAsync(slot, new MemoryStream(uploads[slot].Bytes),
                    uploads[slot].Name, uploads[slot].Type, 0, 0, 1);
            Assert.Equal(ProfileBannerLimits.MaximumPresets, state.Presets.Count);
            Assert.Equal(state.Presets.Single(value => value.SlotIndex == 3).Id, state.ActiveBannerPresetId);
            await Assert.ThrowsAsync<NodeApiException>(() => client.UploadBannerPresetAsync(4,
                new MemoryStream(Png), "fifth.png", "image/png", 0, 0, 1));

            var activeMetadata = await client.GetProfileBannerAsync(authentication.Account.Id);
            Assert.True(activeMetadata.HasBanner);
            Assert.True(activeMetadata.IsProcessed);
            using (var http = new HttpClient())
            {
                var derivative = await http.GetByteArrayAsync(activeMetadata.BannerUrl);
                using var data = SKData.CreateCopy(derivative);
                using var codec = SKCodec.Create(data);
                Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
                Assert.Equal(ProfileBannerLimits.ProcessedWidth, codec.Info.Width);
                Assert.Equal(ProfileBannerLimits.ProcessedHeight, codec.Info.Height);
            }

            await using var hub = new HubConnectionBuilder().WithUrl(new Uri(address, "hubs/chat"), options =>
                options.AccessTokenProvider = () => Task.FromResult<string?>(authentication.AccessToken)).Build();
            var changed = new TaskCompletionSource<ProfileUpdatedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            hub.On<ProfileUpdatedEvent>(ProfileHubContract.Updated, value => changed.TrySetResult(value));
            await hub.StartAsync();
            var selected = state.Presets.Single(value => value.SlotIndex == 2);
            await client.UpdateBannerCropAsync(selected.Id, new(.45, -.25, 1.7, true));
            var update = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(update.BannerRevision > 0);
            state = await client.GetBannerPresetsAsync();
            Assert.Equal(selected.Id, state.ActiveBannerPresetId);

            state = await client.UploadBannerPresetAsync(3, new MemoryStream(AnimatedGif), "animated.gif",
                "image/gif", 0, 0, 1);
            var gif = state.Presets.Single(value => value.SlotIndex == 3);
            Assert.False(gif.IsProcessed);
            using (var http = new HttpClient()) Assert.Equal(AnimatedGif, await http.GetByteArrayAsync(gif.BannerUrl));
            await client.DeleteBannerPresetAsync(gif.Id);
            state = await client.GetBannerPresetsAsync();
            Assert.Equal(3, state.Presets.Count);
            Assert.Equal(state.Presets.OrderBy(value => value.SlotIndex).First().Id, state.ActiveBannerPresetId);
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

    private static Process StartServer(string project, Uri address, string database, string storage, string configuration)
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
        start.Environment["Node__AttachmentStoragePath"] = storage;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the Iridium test node.");
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

    private static byte[] Encode(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(3, 2);
        bitmap.Erase(SKColors.MediumPurple);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }
}
