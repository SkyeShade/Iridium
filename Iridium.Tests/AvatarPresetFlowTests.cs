using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using SkiaSharp;

namespace Iridium.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AvatarPresetIntegrationCollection
{
    public const string Name = "Avatar preset integration";
}

[Collection(AvatarPresetIntegrationCollection.Name)]
public sealed class AvatarPresetFlowTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static readonly byte[] AnimatedGif = Convert.FromHexString(
        "47494638396101000100800000000000FFFFFF" +
        "21F904000A0000002C0000000001000100000202440100" +
        "21F904000A0000002C00000000010001000002024401003B");

    [Fact(Timeout = 60_000)]
    public async Task DynamicProfilePresetsAssignmentOwnershipGifAndActiveDeleteFallbackWork()
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
            await other.SetActiveAvatarPresetAsync(null);
            var emptyDefault = await other.GetAvatarPresetsAsync();
            Assert.Null(emptyDefault.BaseAvatarPresetId);
            Assert.Null(emptyDefault.ActiveAvatarPresetId);
            Assert.False((await other.GetProfileAvatarAsync(otherAuthentication.Account.Id)).HasAvatar);

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
            Assert.Null(state.ActiveAvatarPresetId);
            Assert.Equal(state.Presets.Single(value => value.SlotIndex == 0).Id, state.BaseAvatarPresetId);
            state = await owner.UploadAvatarPresetAsync(10,
                new MemoryStream(Png), "eleventh.png", "image/png", 0, 0, 1, false);
            Assert.Equal(11, state.Presets.Count);

            var community = await owner.CreateCommunityAsync(new("Profile Presets", null));
            var historyChannel = await owner.CreateChannelAsync(community.Id, "avatar-history", null);
            var communityAvatarMedia = state.Presets.Single(value => value.SlotIndex == 10);
            var communityPreset = await owner.CreateProfilePresetAsync(community.Id, "GM Skye");
            Assert.Equal(community.Id, communityPreset.CommunityId);
            Assert.Single(await owner.GetProfilePresetsAsync(community.Id));
            var secondCommunity = await owner.CreateCommunityAsync(new("Second Profiles", null));
            Assert.Empty(await owner.GetProfilePresetsAsync(secondCommunity.Id));
            var secondPreset = await owner.CreateProfilePresetAsync(secondCommunity.Id, "GM Skye");
            Assert.Equal(secondCommunity.Id, secondPreset.CommunityId);
            Assert.Equal(communityPreset.Id, Assert.Single(await owner.GetProfilePresetsAsync(community.Id)).Id);
            Assert.Equal(secondPreset.Id, Assert.Single(await owner.GetProfilePresetsAsync(secondCommunity.Id)).Id);
            var crossCommunityAssignment = await Assert.ThrowsAsync<NodeApiException>(() =>
                owner.SetCommunityProfileAsync(secondCommunity.Id, communityPreset.Id));
            Assert.Equal(HttpStatusCode.BadRequest, crossCommunityAssignment.StatusCode);
            var crossCommunityPresetAccess = await Assert.ThrowsAsync<NodeApiException>(() =>
                owner.UpdateProfilePresetAsync(secondCommunity.Id, communityPreset.Id, new("Wrong Server")));
            Assert.Equal(HttpStatusCode.NotFound, crossCommunityPresetAccess.StatusCode);
            Assert.Null(communityPreset.Avatar);
            var assignment = await owner.SetCommunityProfileAsync(community.Id, communityPreset.Id);
            Assert.Equal("GM Skye", assignment.DisplayName);
            Assert.Equal(state.BaseAvatarPresetId, assignment.AvatarPresetId);
            var management = await owner.GetCommunityManagementAsync(community.Id);
            var ownerMember = Assert.Single(management.Members);
            Assert.Equal("Avatar Owner", ownerMember.DisplayName);
            Assert.Equal("GM Skye", ownerMember.ActiveChatDisplayName);
            Assert.Equal(communityPreset.Id, ownerMember.ProfilePresetId);
            Assert.Null(ownerMember.AvatarPresetId);
            Assert.Equal(state.BaseAvatarPresetId, ownerMember.ActiveChatAvatarPresetId);
            Assert.Equal(state.AvatarRevision, ownerMember.AvatarRevision);
            Assert.Equal(authentication.Account.DisplayName,
                (await other.ResolveProfileAsync("avatar-owner")).DisplayName);

            communityPreset = await owner.SetProfilePresetAvatarAsync(community.Id, communityPreset.Id, communityAvatarMedia.Id);
            Assert.Equal(communityAvatarMedia.Id, communityPreset.Avatar?.Id);
            var alternateCommunityMedia = state.Presets.Single(value => value.SlotIndex == 9);
            var alternateCommunityPreset = await owner.CreateProfilePresetAsync(community.Id, "Alternate Skye");
            alternateCommunityPreset = await owner.SetProfilePresetAvatarAsync(community.Id,
                alternateCommunityPreset.Id, alternateCommunityMedia.Id);
            Assert.NotEqual(alternateCommunityPreset.Id, alternateCommunityPreset.Avatar!.Id);

            var canonicalFirstSelection = await owner.SetCommunityProfileAsync(community.Id, communityPreset.Id);
            Assert.Equal(communityPreset.Id, canonicalFirstSelection.ProfilePresetId);
            Assert.Equal(communityAvatarMedia.Id, canonicalFirstSelection.AvatarPresetId);
            var composerEquivalentSelection = await owner.SetCommunityProfileAsync(community.Id,
                alternateCommunityPreset.Id);
            Assert.Equal(alternateCommunityPreset.Id, composerEquivalentSelection.ProfilePresetId);
            Assert.Equal(alternateCommunityMedia.Id, composerEquivalentSelection.AvatarPresetId);
            var defaultSelection = await owner.SetCommunityProfileAsync(community.Id, null);
            Assert.Null(defaultSelection.ProfilePresetId);
            Assert.Equal(state.BaseAvatarPresetId, defaultSelection.AvatarPresetId);
            await owner.SetCommunityProfileAsync(community.Id, communityPreset.Id);

            management = await owner.GetCommunityManagementAsync(community.Id);
            ownerMember = Assert.Single(management.Members);
            Assert.Equal(communityPreset.Id, ownerMember.ProfilePresetId);
            Assert.Null(ownerMember.AvatarPresetId);
            Assert.Equal(communityAvatarMedia.Id, ownerMember.ActiveChatAvatarPresetId);
            await using var hub = new HubConnectionBuilder().WithUrl(new Uri(address, "hubs/chat"), options =>
                options.AccessTokenProvider = () => Task.FromResult<string?>(authentication.AccessToken)).Build();
            var communityProfileChanged = new TaskCompletionSource<CommunityStateChangedEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            hub.On<CommunityStateChangedEvent>(CommunityHubContract.StateChanged, value =>
            {
                if (value.CommunityId == community.Id && value.Change == "member-profile-updated")
                    communityProfileChanged.TrySetResult(value);
            });
            await hub.StartAsync();
            await hub.InvokeAsync(ChatHubContract.JoinChannel, community.Id, historyChannel.Id);
            communityPreset = await owner.UpdateProfilePresetAsync(community.Id, communityPreset.Id, new("Aria"));
            var ariaMessage = await hub.InvokeAsync<ChannelMessageDto>(ChatHubContract.SendMessage,
                community.Id, historyChannel.Id, new SendChannelMessageRequest("sent as Aria", null));
            Assert.Equal("Aria", ariaMessage.Author.DisplayName);
            Assert.True(ariaMessage.Author.HasHistoricalSnapshot);
            Assert.Equal(ariaMessage.Id, ariaMessage.Author.AvatarSnapshotMessageId);
            Assert.NotNull(ariaMessage.Author.AvatarSnapshot);
            Assert.Equal(ariaMessage.Author.AvatarRevision, ariaMessage.Author.AvatarSnapshot!.Revision);
            var ariaAvatar = await owner.GetMessageAuthorAvatarSnapshotAsync(ariaMessage.Id);
            Assert.True(ariaAvatar.HasAvatar);
            Assert.Equal(0, ariaAvatar.CropX);
            Assert.Equal(ariaAvatar.CropX, ariaMessage.Author.AvatarSnapshot.CropX);
            Assert.Equal(ariaAvatar.Zoom, ariaMessage.Author.AvatarSnapshot.Zoom);

            var activeBeforeCustomCrop = (await owner.GetAvatarPresetsAsync()).ActiveAvatarPresetId;
            communityAvatarMedia = await owner.UpdateAvatarCropAsync(communityAvatarMedia.Id,
                new(.15, -.1, 1.25, false));
            await communityProfileChanged.Task.WaitAsync(TimeSpan.FromSeconds(20));
            management = await owner.GetCommunityManagementAsync(community.Id);
            ownerMember = Assert.Single(management.Members);
            Assert.Equal(state.AvatarRevision, ownerMember.AvatarRevision);
            Assert.Equal(communityAvatarMedia.Revision, ownerMember.ActiveChatAvatarRevision);
            Assert.Equal(activeBeforeCustomCrop, (await owner.GetAvatarPresetsAsync()).ActiveAvatarPresetId);
            communityPreset = await owner.UpdateProfilePresetAsync(community.Id, communityPreset.Id, new("GM Skye"));
            var gmMessage = await hub.InvokeAsync<ChannelMessageDto>(ChatHubContract.SendMessage,
                community.Id, historyChannel.Id, new SendChannelMessageRequest("sent as GM", null));
            Assert.Equal("GM Skye", gmMessage.Author.DisplayName);
            Assert.Equal(gmMessage.Id, gmMessage.Author.AvatarSnapshotMessageId);
            Assert.Equal(.15, (await owner.GetMessageAuthorAvatarSnapshotAsync(gmMessage.Id)).CropX, 5);
            var editedAria = await hub.InvokeAsync<ChannelMessageDto>(ChatHubContract.EditMessage,
                community.Id, historyChannel.Id, ariaMessage.Id, new EditChannelMessageRequest("Aria edited"));
            Assert.Equal("Aria", editedAria.Author.DisplayName);
            Assert.Equal(ariaMessage.Id, editedAria.Author.AvatarSnapshotMessageId);
            var replyToAria = await hub.InvokeAsync<ChannelMessageDto>(ChatHubContract.SendMessage,
                community.Id, historyChannel.Id, new SendChannelMessageRequest("reply", ariaMessage.Id));
            Assert.Equal("Aria", replyToAria.ReplyTo?.AuthorDisplayName);
            Assert.Equal(ariaMessage.Id, replyToAria.ReplyTo?.AvatarSnapshotMessageId);

            var selected = state.Presets.Single(value => value.SlotIndex == 6);
            await owner.UpdateAvatarCropAsync(selected.Id, new(0, 0, 1.2, true));
            state = await owner.GetAvatarPresetsAsync();
            Assert.Null(state.ActiveAvatarPresetId);
            Assert.Equal(selected.Id, state.BaseAvatarPresetId);
            var alternate = state.Presets.Single(value => value.SlotIndex == 7);
            var secondAlternate = state.Presets.Single(value => value.SlotIndex == 8);
            var presetCount = state.Presets.Count;
            var mediaBeforeSwap = state.Presets.OrderBy(value => value.Id).ToArray();
            var alternateRealtime = new TaskCompletionSource<ProfileUpdatedEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var defaultRealtime = new TaskCompletionSource<ProfileUpdatedEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var avatarSelectionSubscription = hub.On<ProfileUpdatedEvent>(ProfileHubContract.Updated, value =>
            {
                if (value.AccountId != authentication.Account.Id) return;
                if (value.ActiveAvatarPresetId == alternate.Id) alternateRealtime.TrySetResult(value);
                if (value.ActiveAvatarPresetId is null && value.BaseAvatarPresetId == selected.Id)
                    defaultRealtime.TrySetResult(value);
            });
            await owner.SetActiveAvatarPresetAsync(alternate.Id);
            await alternateRealtime.Task.WaitAsync(TimeSpan.FromSeconds(10));
            state = await owner.GetAvatarPresetsAsync();
            Assert.Equal(alternate.Id, state.ActiveAvatarPresetId);
            Assert.Equal(selected.Id, state.BaseAvatarPresetId);
            Assert.Equal(1.2, (await owner.GetProfileAvatarAsync(authentication.Account.Id)).Zoom, 5);
            await owner.SetActiveAvatarPresetAsync(null);
            await defaultRealtime.Task.WaitAsync(TimeSpan.FromSeconds(10));
            state = await owner.GetAvatarPresetsAsync();
            Assert.Null(state.ActiveAvatarPresetId);
            Assert.Equal(selected.Id, state.BaseAvatarPresetId);

            await owner.SetActiveAvatarPresetAsync(alternate.Id);
            Assert.Equal(alternate.Id, (await owner.GetAvatarPresetsAsync()).ActiveAvatarPresetId);
            await owner.SetActiveAvatarPresetAsync(secondAlternate.Id);
            Assert.Equal(secondAlternate.Id, (await owner.GetAvatarPresetsAsync()).ActiveAvatarPresetId);
            await owner.SetActiveAvatarPresetAsync(null);
            Assert.Null((await owner.GetAvatarPresetsAsync()).ActiveAvatarPresetId);
            await owner.SetActiveAvatarPresetAsync(alternate.Id);
            Assert.Equal(alternate.Id, (await owner.GetAvatarPresetsAsync()).ActiveAvatarPresetId);
            await owner.SetActiveAvatarPresetAsync(secondAlternate.Id);
            Assert.Equal(secondAlternate.Id, (await owner.GetAvatarPresetsAsync()).ActiveAvatarPresetId);
            await owner.SetActiveAvatarPresetAsync(null);
            state = await owner.GetAvatarPresetsAsync();
            Assert.Equal(presetCount, state.Presets.Count);
            Assert.Equal(mediaBeforeSwap, state.Presets.OrderBy(value => value.Id).ToArray());
            Assert.True((await owner.GetProfileAvatarAsync(authentication.Account.Id)).HasAvatar);

            communityPreset = await owner.ClearProfilePresetAvatarAsync(community.Id, communityPreset.Id);
            Assert.Null(communityPreset.Avatar);
            management = await owner.GetCommunityManagementAsync(community.Id);
            ownerMember = Assert.Single(management.Members);
            Assert.Equal(communityPreset.Id, ownerMember.ProfilePresetId);
            Assert.Null(ownerMember.AvatarPresetId);
            Assert.Equal(state.BaseAvatarPresetId, ownerMember.ActiveChatAvatarPresetId);
            Assert.Equal(state.AvatarRevision, ownerMember.AvatarRevision);
            var fallbackMessage = await hub.InvokeAsync<ChannelMessageDto>(ChatHubContract.SendMessage,
                community.Id, historyChannel.Id, new SendChannelMessageRequest("account PFP fallback", null));
            Assert.Equal(fallbackMessage.Id, fallbackMessage.Author.AvatarSnapshotMessageId);
            var fallbackAvatar = await owner.GetMessageAuthorAvatarSnapshotAsync(fallbackMessage.Id);
            Assert.Equal(1.2, fallbackAvatar.Zoom, 5);
            communityPreset = await owner.SetProfilePresetAvatarAsync(community.Id, communityPreset.Id, communityAvatarMedia.Id);

            await using var otherHub = new HubConnectionBuilder().WithUrl(new Uri(address, "hubs/chat"), options =>
                options.AccessTokenProvider = () => Task.FromResult<string?>(otherAuthentication.AccessToken)).Build();
            var profileChanged = new TaskCompletionSource<ProfileUpdatedEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            otherHub.On<ProfileUpdatedEvent>(ProfileHubContract.Updated,
                value => profileChanged.TrySetResult(value));
            await otherHub.StartAsync();
            await owner.UpdateProfileAsync(new("Avatar Owner Updated", "they/them", "Realtime profile details"));
            var profileEvent = await profileChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(authentication.Account.Id, profileEvent.AccountId);
            Assert.Equal("Avatar Owner Updated", profileEvent.DisplayName);
            Assert.Equal("they/them", profileEvent.Pronouns);
            Assert.Equal("Realtime profile details", profileEvent.Description);
            var historyAfterProfileChange = await owner.GetChannelMessagesAsync(community.Id, historyChannel.Id);
            Assert.Equal("Aria", historyAfterProfileChange.Single(value => value.Id == ariaMessage.Id).Author.DisplayName);

            var ownerPreset = state.Presets[0];
            var ownershipFailure = await Assert.ThrowsAsync<NodeApiException>(() => other.UpdateAvatarCropAsync(
                ownerPreset.Id, new(0, 0, 1.5, false)));
            Assert.Equal(HttpStatusCode.NotFound, ownershipFailure.StatusCode);
            await Assert.ThrowsAsync<NodeApiException>(() => other.UpdateProfilePresetAsync(
                community.Id, communityPreset.Id, new("Not mine")));
            var otherCommunity = await other.CreateCommunityAsync(new("Other Community", null));
            var otherPreset = await other.CreateProfilePresetAsync(otherCommunity.Id, "GM Skye");
            var foreignAssignment = await Assert.ThrowsAsync<NodeApiException>(() =>
                owner.SetCommunityProfileAsync(community.Id, otherPreset.Id));
            Assert.Equal(HttpStatusCode.BadRequest, foreignAssignment.StatusCode);
            var crossCommunityEdit = await Assert.ThrowsAsync<NodeApiException>(() =>
                other.UpdateProfilePresetAsync(community.Id, otherPreset.Id, new("Cross Community")));
            Assert.Equal(HttpStatusCode.Forbidden, crossCommunityEdit.StatusCode);

            await owner.DeleteAvatarPresetAsync(state.Presets.Single(value => value.SlotIndex == 9).Id);
            state = await owner.UploadAvatarPresetAsync(9, new MemoryStream(AnimatedGif), "animated.gif",
                "image/gif", 0, 0, 1, false);
            var gif = state.Presets.Single(value => value.SlotIndex == 9);
            Assert.Equal("image/gif", gif.ContentType);
            using (var http = new HttpClient())
                Assert.Equal(AnimatedGif, await http.GetByteArrayAsync(gif.AvatarUrl));

            await owner.DeleteAvatarPresetAsync(selected.Id);
            Assert.True((await owner.GetMessageAuthorAvatarSnapshotAsync(fallbackMessage.Id)).HasAvatar);
            state = await owner.GetAvatarPresetsAsync();
            Assert.Equal(10, state.Presets.Count);
            Assert.Null(state.ActiveAvatarPresetId);
            Assert.Equal(state.Presets.OrderBy(value => value.SlotIndex).First().Id, state.BaseAvatarPresetId);
            Assert.True((await owner.GetProfileAvatarAsync(authentication.Account.Id)).HasAvatar);

            await owner.DeleteAvatarPresetAsync(communityAvatarMedia.Id);
            var retainedAvatar = await owner.GetMessageAuthorAvatarSnapshotAsync(ariaMessage.Id);
            Assert.True(retainedAvatar.HasAvatar);
            using (var http = new HttpClient())
                Assert.NotEmpty(await http.GetByteArrayAsync(retainedAvatar.AvatarUrl));
            management = await owner.GetCommunityManagementAsync(community.Id);
            ownerMember = Assert.Single(management.Members);
            Assert.Equal(communityPreset.Id, ownerMember.ProfilePresetId);
            Assert.Null(ownerMember.AvatarPresetId);
            Assert.Equal("Avatar Owner Updated", ownerMember.DisplayName);
            Assert.Equal("GM Skye", ownerMember.ActiveChatDisplayName);
            Assert.Equal(state.BaseAvatarPresetId, ownerMember.ActiveChatAvatarPresetId);

            await owner.DeleteProfilePresetAsync(community.Id, communityPreset.Id);
            Assert.Equal(secondPreset.Id, Assert.Single(await owner.GetProfilePresetsAsync(secondCommunity.Id)).Id);
            management = await owner.GetCommunityManagementAsync(community.Id);
            ownerMember = Assert.Single(management.Members);
            Assert.Null(ownerMember.ProfilePresetId);
            Assert.Equal("Avatar Owner Updated", ownerMember.DisplayName);
            Assert.Equal("Avatar Owner Updated", ownerMember.ActiveChatDisplayName);
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
            var update = await changed.Task.WaitAsync(TimeSpan.FromSeconds(20));
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
