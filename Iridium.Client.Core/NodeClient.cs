using System.Net.Http.Headers;
using System.Net.Http.Json;
using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed class NodeClient(Uri nodeAddress)
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(nodeAddress.ToString().TrimEnd('/') + "/") };

    public string? AccessToken { get; set; }
    internal Uri NodeAddress => _http.BaseAddress!;

    public Task<ServerInfoDto> GetServerInfoAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ServerInfoDto>(HttpMethod.Get, "api/server-info", null, cancellationToken);

    public Task<WebRtcIceConfigurationDto> GetWebRtcIceConfigurationAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<WebRtcIceConfigurationDto>(HttpMethod.Get, "api/webrtc/ice-configuration", null, cancellationToken);

    public async Task<AuthenticationResultDto> RegisterAsync(
        RegisterAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<AuthenticationResultDto>(HttpMethod.Post, "api/account/register", request, cancellationToken);
        AccessToken = result.AccessToken;
        return result;
    }

    public async Task<AuthenticationResultDto> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<AuthenticationResultDto>(HttpMethod.Post, "api/account/login", request, cancellationToken);
        AccessToken = result.AccessToken;
        return result;
    }

    public Task<NodeAccountDto> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
        SendAsync<NodeAccountDto>(HttpMethod.Get, "api/account/current", null, cancellationToken);

    public Task<NodeAccountDto> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<NodeAccountDto>(HttpMethod.Patch, "api/account/current", request, cancellationToken);

    public Task<AccountSecurityStatusDto> GetAccountSecurityStatusAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<AccountSecurityStatusDto>(HttpMethod.Get, "api/account/security", null, cancellationToken);

    public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, "api/account/security/password", request, cancellationToken);

    public Task<AccountSecurityStatusDto> UpdateRecoveryEmailAsync(
        UpdateRecoveryEmailRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AccountSecurityStatusDto>(HttpMethod.Put, "api/account/security/recovery-email", request,
            cancellationToken);

    public Task<PasswordRecoveryRequestResultDto> RequestPasswordRecoveryAsync(
        PasswordRecoveryRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<PasswordRecoveryRequestResultDto>(HttpMethod.Post, "api/account/recovery/request", request,
            cancellationToken);

    public Task CompletePasswordRecoveryAsync(
        CompletePasswordRecoveryRequest request,
        CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, "api/account/recovery/complete", request, cancellationToken);

    public Task<PasswordRecoveryValidationResultDto> ValidatePasswordRecoveryAsync(
        ValidatePasswordRecoveryRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<PasswordRecoveryValidationResultDto>(HttpMethod.Post, "api/account/recovery/validate", request,
            cancellationToken);

    public Task<AccountAvatarPresetsDto> GetAvatarPresetsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<AccountAvatarPresetsDto>(HttpMethod.Get, "api/account/avatar-presets", null, cancellationToken);

    public Task<ProfileAvatarDto> GetProfileAvatarAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        SendAsync<ProfileAvatarDto>(HttpMethod.Get, $"api/profiles/{accountId}/avatar-metadata", null, cancellationToken);

    public Task<ProfileAvatarDto> GetProfileAvatarPresetAsync(Guid accountId, Guid presetId,
        CancellationToken cancellationToken = default) => SendAsync<ProfileAvatarDto>(HttpMethod.Get,
          $"api/profiles/{accountId}/avatar/{presetId}/metadata", null, cancellationToken);

    public Task<ProfileAvatarDto> GetMessageAuthorAvatarSnapshotAsync(Guid messageId,
        CancellationToken cancellationToken = default) => SendAsync<ProfileAvatarDto>(HttpMethod.Get,
        $"api/messages/{messageId}/author-avatar/metadata", null, cancellationToken);

    public async Task<AccountAvatarPresetsDto> UploadAvatarPresetAsync(int slotIndex, Stream content,
        string fileName, string contentType, double cropX, double cropY, double zoom, bool setActive,
        CancellationToken cancellationToken = default)
    {
        using var multipart = new MultipartFormDataContent();
        using var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        multipart.Add(streamContent, "file", fileName);
        multipart.Add(new StringContent(cropX.ToString(System.Globalization.CultureInfo.InvariantCulture)), "cropX");
        multipart.Add(new StringContent(cropY.ToString(System.Globalization.CultureInfo.InvariantCulture)), "cropY");
        multipart.Add(new StringContent(zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)), "zoom");
        multipart.Add(new StringContent(setActive.ToString()), "setActive");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/account/avatar-presets/{slotIndex}")
            { Content = multipart };
        if (!string.IsNullOrWhiteSpace(AccessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new NodeApiException(response.StatusCode, await ReadErrorAsync(response, cancellationToken));
        return await response.Content.ReadFromJsonAsync<AccountAvatarPresetsDto>(cancellationToken: cancellationToken)
            ?? throw new NodeApiException(response.StatusCode, "The node returned an empty response.");
    }

    public Task<AccountAvatarPresetDto> UpdateAvatarCropAsync(Guid presetId, UpdateAvatarCropRequest request,
        CancellationToken cancellationToken = default) => SendAsync<AccountAvatarPresetDto>(HttpMethod.Patch,
        $"api/account/avatar-presets/{presetId}", request, cancellationToken);

    public Task<List<UserProfilePresetDto>> GetProfilePresetsAsync(Guid communityId,
        CancellationToken cancellationToken = default) => SendAsync<List<UserProfilePresetDto>>(HttpMethod.Get,
        $"api/communities/{communityId}/profile-presets", null, cancellationToken);

    public Task<UserProfilePresetDto> CreateProfilePresetAsync(Guid communityId, string displayName,
        CancellationToken cancellationToken = default) => SendAsync<UserProfilePresetDto>(HttpMethod.Post,
        $"api/communities/{communityId}/profile-presets", new CreateUserProfilePresetRequest(displayName), cancellationToken);

    public Task<UserProfilePresetDto> UpdateProfilePresetAsync(Guid communityId, Guid presetId, UpdateProfilePresetRequest request,
        CancellationToken cancellationToken = default) => SendAsync<UserProfilePresetDto>(HttpMethod.Patch,
        $"api/communities/{communityId}/profile-presets/{presetId}", request, cancellationToken);

    public Task<UserProfilePresetDto> SetProfilePresetAvatarAsync(Guid communityId, Guid presetId, Guid avatarPresetId,
        CancellationToken cancellationToken = default) => SendAsync<UserProfilePresetDto>(HttpMethod.Put,
        $"api/communities/{communityId}/profile-presets/{presetId}/avatar", new SetUserProfilePresetAvatarRequest(avatarPresetId),
        cancellationToken);

    public Task<UserProfilePresetDto> ClearProfilePresetAvatarAsync(Guid communityId, Guid presetId,
        CancellationToken cancellationToken = default) => SendAsync<UserProfilePresetDto>(HttpMethod.Delete,
        $"api/communities/{communityId}/profile-presets/{presetId}/avatar", null, cancellationToken);

    public Task DeleteProfilePresetAsync(Guid communityId, Guid presetId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/communities/{communityId}/profile-presets/{presetId}", null, cancellationToken);

    public Task SetActiveAvatarPresetAsync(Guid? presetId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Put, "api/account/avatar-presets/active",
            new SetActiveAvatarPresetRequest(presetId), cancellationToken);

    public Task DeleteAvatarPresetAsync(Guid presetId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/account/avatar-presets/{presetId}", null, cancellationToken);

    public Task<AccountBannerPresetsDto> GetBannerPresetsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<AccountBannerPresetsDto>(HttpMethod.Get, "api/account/banner-presets", null, cancellationToken);

    public Task<ProfileBannerDto> GetProfileBannerAsync(Guid accountId,
        CancellationToken cancellationToken = default) =>
        SendAsync<ProfileBannerDto>(HttpMethod.Get, $"api/profiles/{accountId}/banner-metadata", null,
            cancellationToken);

    public async Task<AccountBannerPresetsDto> UploadBannerPresetAsync(int slotIndex, Stream content,
        string fileName, string contentType, double cropX, double cropY, double zoom,
        CancellationToken cancellationToken = default)
    {
        using var multipart = new MultipartFormDataContent();
        using var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        multipart.Add(streamContent, "file", fileName);
        multipart.Add(new StringContent(cropX.ToString(System.Globalization.CultureInfo.InvariantCulture)), "cropX");
        multipart.Add(new StringContent(cropY.ToString(System.Globalization.CultureInfo.InvariantCulture)), "cropY");
        multipart.Add(new StringContent(zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)), "zoom");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/account/banner-presets/{slotIndex}")
            { Content = multipart };
        if (!string.IsNullOrWhiteSpace(AccessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new NodeApiException(response.StatusCode, await ReadErrorAsync(response, cancellationToken));
        return await response.Content.ReadFromJsonAsync<AccountBannerPresetsDto>(cancellationToken: cancellationToken)
            ?? throw new NodeApiException(response.StatusCode, "The node returned an empty response.");
    }

    public Task<AccountBannerPresetDto> UpdateBannerCropAsync(Guid presetId, UpdateBannerCropRequest request,
        CancellationToken cancellationToken = default) => SendAsync<AccountBannerPresetDto>(HttpMethod.Patch,
        $"api/account/banner-presets/{presetId}", request, cancellationToken);

    public Task DeleteBannerPresetAsync(Guid presetId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/account/banner-presets/{presetId}", null, cancellationToken);

    public Task<AccountAvatarPresetsDto> GetCommunityAvatarPresetsAsync(Guid communityId, CancellationToken cancellationToken = default) =>
        SendAsync<AccountAvatarPresetsDto>(HttpMethod.Get, $"api/communities/{communityId}/media/avatar-presets", null, cancellationToken);
    public Task<AccountBannerPresetsDto> GetCommunityBannerPresetsAsync(Guid communityId, CancellationToken cancellationToken = default) =>
        SendAsync<AccountBannerPresetsDto>(HttpMethod.Get, $"api/communities/{communityId}/media/banner-presets", null, cancellationToken);
    public Task<AccountAvatarPresetsDto> UploadCommunityAvatarPresetAsync(Guid communityId,int slot,Stream content,string fileName,string contentType,double x,double y,double zoom,CancellationToken ct=default)=>
        UploadCommunityMediaAsync<AccountAvatarPresetsDto>(communityId,"avatar",slot,content,fileName,contentType,x,y,zoom,ct);
    public Task<AccountBannerPresetsDto> UploadCommunityBannerPresetAsync(Guid communityId,int slot,Stream content,string fileName,string contentType,double x,double y,double zoom,CancellationToken ct=default)=>
        UploadCommunityMediaAsync<AccountBannerPresetsDto>(communityId,"banner",slot,content,fileName,contentType,x,y,zoom,ct);
    public Task<AccountAvatarPresetDto> UpdateCommunityAvatarCropAsync(Guid communityId,Guid presetId,UpdateAvatarCropRequest request,CancellationToken ct=default)=>SendAsync<AccountAvatarPresetDto>(HttpMethod.Patch,$"api/communities/{communityId}/media/avatar/{presetId}",request,ct);
    public Task<AccountBannerPresetDto> UpdateCommunityBannerCropAsync(Guid communityId,Guid presetId,UpdateBannerCropRequest request,CancellationToken ct=default)=>SendAsync<AccountBannerPresetDto>(HttpMethod.Patch,$"api/communities/{communityId}/media/banner/{presetId}",request,ct);
    public Task DeleteCommunityMediaPresetAsync(Guid communityId,string kind,Guid presetId,CancellationToken ct=default)=>SendNoContentAsync(HttpMethod.Delete,$"api/communities/{communityId}/media/{kind}/{presetId}",null,ct);

    private async Task<T> UploadCommunityMediaAsync<T>(Guid communityId,string kind,int slot,Stream content,string fileName,string contentType,double x,double y,double zoom,CancellationToken ct)
    {
        using var multipart=new MultipartFormDataContent();using var stream=new StreamContent(content);stream.Headers.ContentType=new MediaTypeHeaderValue(contentType);multipart.Add(stream,"file",fileName);multipart.Add(new StringContent(x.ToString(System.Globalization.CultureInfo.InvariantCulture)),"cropX");multipart.Add(new StringContent(y.ToString(System.Globalization.CultureInfo.InvariantCulture)),"cropY");multipart.Add(new StringContent(zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)),"zoom");using var request=new HttpRequestMessage(HttpMethod.Post,$"api/communities/{communityId}/media/{kind}/{slot}"){Content=multipart};if(!string.IsNullOrWhiteSpace(AccessToken))request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",AccessToken);using var response=await _http.SendAsync(request,ct);if(!response.IsSuccessStatusCode)throw new NodeApiException(response.StatusCode,await ReadErrorAsync(response,ct));return await response.Content.ReadFromJsonAsync<T>(cancellationToken:ct)??throw new NodeApiException(response.StatusCode,"The node returned an empty response.");
    }

    public Task<IReadOnlyList<CommunityEmojiDto>> GetCommunityEmojisAsync(Guid communityId, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<CommunityEmojiDto>>(HttpMethod.Get, $"api/communities/{communityId}/emojis", null, ct);
    public Task<CommunityEmojiDto> GetCommunityEmojiReferenceAsync(Guid emojiId, CancellationToken ct = default) =>
        SendAsync<CommunityEmojiDto>(HttpMethod.Get, $"api/emojis/{emojiId}", null, ct);
    public async Task<CommunityEmojiDto> UploadCommunityEmojiAsync(Guid communityId, Stream content, string fileName,
        string contentType, string name, CancellationToken ct = default)
    {
        using var multipart = new MultipartFormDataContent();
        using var stream = new StreamContent(content);
        stream.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        multipart.Add(stream, "file", fileName); multipart.Add(new StringContent(name), "name");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/communities/{communityId}/emojis") { Content = multipart };
        if (!string.IsNullOrWhiteSpace(AccessToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new NodeApiException(response.StatusCode, await ReadErrorAsync(response, ct));
        return await response.Content.ReadFromJsonAsync<CommunityEmojiDto>(cancellationToken: ct) ??
               throw new NodeApiException(response.StatusCode, "The node returned an empty response.");
    }
    public Task<CommunityEmojiDto> RenameCommunityEmojiAsync(Guid communityId, Guid emojiId, string name,
        CancellationToken ct = default) => SendAsync<CommunityEmojiDto>(HttpMethod.Patch,
        $"api/communities/{communityId}/emojis/{emojiId}", new RenameCommunityEmojiRequest(name), ct);
    public Task DeleteCommunityEmojiAsync(Guid communityId, Guid emojiId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/communities/{communityId}/emojis/{emojiId}", null, ct);
    public async Task<byte[]> DownloadCommunityEmojiAsync(Guid communityId, Guid emojiId, long? revision = null,
        CancellationToken ct = default)
    {
        var revisionQuery = revision is null ? string.Empty : $"?rev={revision.Value}";
        using var response = await SendAsync(HttpMethod.Get,
            $"api/communities/{communityId}/emojis/{emojiId}/media{revisionQuery}", null, ct);
        if (!response.IsSuccessStatusCode) throw new NodeApiException(response.StatusCode, await ReadErrorAsync(response, ct));
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]> DownloadCommunityEmojiReferenceAsync(Guid emojiId, long? revision = null,
        CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"api/emojis/{emojiId}/media{(revision is null ? string.Empty : $"?rev={revision}")}", null, ct);
        if (!response.IsSuccessStatusCode) throw new NodeApiException(response.StatusCode,
            await ReadErrorAsync(response, ct));
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public Task MarkDirectConversationReadAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"api/direct-messages/{conversationId}/read", null, cancellationToken);

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/account/logout", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new NodeApiException(response.StatusCode, await ReadErrorAsync(response, cancellationToken));
        AccessToken = null;
    }

    public Task<List<CommunityDto>> GetCommunitiesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<CommunityDto>>(HttpMethod.Get, "api/communities", null, cancellationToken);

    public Task<CommunityDto> CreateCommunityAsync(CreateCommunityRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityDto>(HttpMethod.Post, "api/communities", request, cancellationToken);

    public Task<List<FriendDto>> GetFriendsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<FriendDto>>(HttpMethod.Get, "api/friends", null, cancellationToken);

    public Task<ResolvedProfileDto> ResolveProfileAsync(string username, CancellationToken cancellationToken = default) =>
        SendAsync<ResolvedProfileDto>(HttpMethod.Get, $"api/profiles/{Uri.EscapeDataString(username)}", null, cancellationToken);

    public Task<List<FriendSearchResultDto>> SearchAccountsAsync(string query, int limit = 5,
        CancellationToken cancellationToken = default) =>
        SendAsync<List<FriendSearchResultDto>>(HttpMethod.Get,
            $"api/accounts/search?q={Uri.EscapeDataString(query)}&limit={Math.Clamp(limit, 1, 5)}", null,
            cancellationToken);

    public Task BlockAccountAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Put, $"api/profiles/{accountId}/block", null, cancellationToken);

    public Task UnblockAccountAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/profiles/{accountId}/block", null, cancellationToken);

    public Task<FriendDto> SendFriendRequestAsync(string username, CancellationToken cancellationToken = default) =>
        SendAsync<FriendDto>(HttpMethod.Post, "api/friends/requests", new SendFriendRequest(username), cancellationToken);

    public async Task AcceptFriendRequestAsync(Guid friendshipId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"api/friends/requests/{friendshipId}/accept", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new NodeApiException(response.StatusCode, await ReadErrorAsync(response, cancellationToken));
    }

    public Task RemoveFriendshipAsync(Guid friendshipId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/friends/{friendshipId}", null, cancellationToken);

    public Task<List<DirectConversationDto>> GetDirectConversationsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<DirectConversationDto>>(HttpMethod.Get, "api/direct-messages", null, cancellationToken);

    public Task<DirectConversationDto> OpenDirectConversationAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        SendAsync<DirectConversationDto>(HttpMethod.Post, $"api/direct-messages/with/{accountId}", null, cancellationToken);

    public async Task<List<DirectMessageDto>> GetDirectMessagesAsync(
        Guid conversationId, int limit = MessageHistoryDefaults.PageSize, CancellationToken cancellationToken = default) =>
        [.. (await GetDirectMessagePageAsync(conversationId, limit, cancellationToken: cancellationToken)).Messages];

    public Task<MessageHistoryPage<DirectMessageDto>> GetDirectMessagePageAsync(
        Guid conversationId, int limit = MessageHistoryDefaults.PageSize, string? before = null, Guid? around = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<MessageHistoryPage<DirectMessageDto>>(HttpMethod.Get,
            $"api/direct-messages/{conversationId}/messages?limit={Math.Clamp(limit, 1, MessageHistoryDefaults.MaximumPageSize)}{Query("before", before)}{Query("around", around?.ToString())}",
            null, cancellationToken);

    public Task<MessageSearchPageDto> SearchDirectMessagesAsync(
        Guid conversationId, string? text, string? from, string? before = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<MessageSearchPageDto>(HttpMethod.Get,
            $"api/direct-messages/{conversationId}/messages/search?limit={MessageHistoryDefaults.SearchPageSize}{Query("q", text)}{Query("from", from)}{Query("before", before)}",
            null, cancellationToken);

    public Task<MessageSearchPageDto> SearchDirectMessagesAsync(
        Guid conversationId, MessageSearchRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<MessageSearchPageDto>(HttpMethod.Post,
            $"api/direct-messages/{conversationId}/messages/search", request, cancellationToken);

    public Task HideDirectConversationAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"api/direct-messages/{conversationId}/hide", null, cancellationToken);

    public Task<CommunityStructureDto> GetCommunityStructureAsync(Guid communityId, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityStructureDto>(HttpMethod.Get, $"api/communities/{communityId}/structure", null, cancellationToken);

    public Task<CommunityCategoryDto> CreateCategoryAsync(Guid communityId, string name, Guid? parentCategoryId = null, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityCategoryDto>(HttpMethod.Post, $"api/communities/{communityId}/categories", new CreateCategoryRequest(name, parentCategoryId), cancellationToken);

    public Task<CommunityCategoryDto> UpdateCategoryAsync(Guid communityId, Guid categoryId, string name, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityCategoryDto>(HttpMethod.Patch, $"api/communities/{communityId}/categories/{categoryId}", new UpdateCategoryRequest(name), cancellationToken);

    public Task MoveCategoryAsync(Guid communityId, Guid categoryId, CommunitySidebarMoveRequest request, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"api/communities/{communityId}/categories/{categoryId}/move", request, cancellationToken);

    public async Task MoveCategoryAsync(Guid communityId, Guid categoryId, Guid? parentCategoryId, int position,
        CancellationToken cancellationToken = default) => await MoveSidebarItemByPositionAsync(
        communityId, categoryId, CommunitySidebarItemType.Category, parentCategoryId, position, cancellationToken);

    public Task DeleteCategoryAsync(Guid communityId, Guid categoryId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/communities/{communityId}/categories/{categoryId}", null, cancellationToken);

    public Task<CommunityChannelDto> CreateChannelAsync(Guid communityId, string name, Guid? categoryId,
        CommunityChannelKind kind = CommunityChannelKind.Text, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityChannelDto>(HttpMethod.Post, $"api/communities/{communityId}/channels", new CreateChannelRequest(name, categoryId, kind), cancellationToken);

    public Task<CommunityChannelDto> UpdateChannelAsync(Guid communityId, Guid channelId, string name, Guid? categoryId,
        CommunityChannelKind kind = CommunityChannelKind.Text, bool? requireTag = null,
        CommunityChannelEmbedUpdate? embed = null,
        bool? allowDocumentEmbeds = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<CommunityChannelDto>(HttpMethod.Patch, $"api/communities/{communityId}/channels/{channelId}",
            new UpdateChannelRequest(name, categoryId, kind, requireTag, embed, allowDocumentEmbeds), cancellationToken);

    public Task<ChannelEmbedDocumentDto> GetChannelEmbedDocumentAsync(Guid communityId, Guid channelId,
        CancellationToken cancellationToken = default) =>
        SendAsync<ChannelEmbedDocumentDto>(HttpMethod.Get,
            $"api/communities/{communityId}/channels/{channelId}/embed-document", null, cancellationToken);

    public async Task<DownloadedDocumentMedia> DownloadChannelEmbedDocumentMediaAsync(Guid communityId,
        Guid channelId, string mediaId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"api/communities/{communityId}/channels/{channelId}/embed-document/media/{Uri.EscapeDataString(mediaId)}",
            null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new NodeApiException(response.StatusCode, await ReadErrorAsync(response, cancellationToken));
        return new(await response.Content.ReadAsByteArrayAsync(cancellationToken),
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    public Task MoveChannelAsync(Guid communityId, Guid channelId, CommunitySidebarMoveRequest request, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"api/communities/{communityId}/channels/{channelId}/move", request, cancellationToken);

    public async Task MoveChannelAsync(Guid communityId, Guid channelId, Guid? categoryId, int position,
        CancellationToken cancellationToken = default) => await MoveSidebarItemByPositionAsync(
        communityId, channelId, CommunitySidebarItemType.Channel, categoryId, position, cancellationToken);

    public Task DeleteChannelAsync(Guid communityId, Guid channelId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/communities/{communityId}/channels/{channelId}", null, cancellationToken);

    public Task<CommunityForumPostPageDto> GetForumPostsAsync(Guid communityId, Guid channelId, int offset = 0,
        int limit = 30, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityForumPostPageDto>(HttpMethod.Get,
            $"api/communities/{communityId}/forums/{channelId}/posts?offset={Math.Max(0, offset)}&limit={Math.Clamp(limit, 1, 50)}",
            null, cancellationToken);

    public Task<CommunityForumPostPageDto> QueryForumPostsAsync(Guid communityId, Guid channelId,
        string? search, IReadOnlyCollection<Guid>? tagIds, int offset = 0, int limit = 30,
        CancellationToken cancellationToken = default)
    {
        var query = $"offset={Math.Max(0, offset)}&limit={Math.Clamp(limit, 1, 50)}";
        if (!string.IsNullOrWhiteSpace(search)) query += $"&search={Uri.EscapeDataString(search.Trim())}";
        if (tagIds is { Count: > 0 }) query += $"&tags={string.Join(',', tagIds)}";
        return SendAsync<CommunityForumPostPageDto>(HttpMethod.Get,
            $"api/communities/{communityId}/forums/{channelId}/posts?{query}", null, cancellationToken);
    }

    public Task<CommunityForumPostDto> GetForumPostAsync(Guid communityId, Guid channelId, Guid postId,
        CancellationToken cancellationToken = default) => SendAsync<CommunityForumPostDto>(HttpMethod.Get,
        $"api/communities/{communityId}/forums/{channelId}/posts/{postId}", null, cancellationToken);

    public Task<CommunityForumPostDto> CreateForumPostAsync(Guid communityId, Guid channelId,
        CreateCommunityForumPostRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityForumPostDto>(HttpMethod.Post,
            $"api/communities/{communityId}/forums/{channelId}/posts", request, cancellationToken);

    public Task<CommunityForumPostDto> UpdateForumPostAsync(Guid communityId, Guid channelId, Guid postId,
        UpdateCommunityForumPostRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityForumPostDto>(HttpMethod.Patch,
            $"api/communities/{communityId}/forums/{channelId}/posts/{postId}", request, cancellationToken);

    public Task<ChannelEmbedDocumentDto> GetForumPostEmbedDocumentAsync(Guid communityId, Guid channelId,
        Guid postId, CancellationToken cancellationToken = default) =>
        SendAsync<ChannelEmbedDocumentDto>(HttpMethod.Get,
            $"api/communities/{communityId}/forums/{channelId}/posts/{postId}/embed-document", null,
            cancellationToken);

    public async Task<DownloadedDocumentMedia> DownloadForumPostEmbedDocumentMediaAsync(Guid communityId,
        Guid channelId, Guid postId, string mediaId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"api/communities/{communityId}/forums/{channelId}/posts/{postId}/embed-document/media/{Uri.EscapeDataString(mediaId)}",
            null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new NodeApiException(response.StatusCode, await ReadErrorAsync(response, cancellationToken));
        return new(await response.Content.ReadAsByteArrayAsync(cancellationToken),
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    public Task DeleteForumPostAsync(Guid communityId, Guid channelId, Guid postId,
        CancellationToken cancellationToken = default) => SendNoContentAsync(HttpMethod.Delete,
        $"api/communities/{communityId}/forums/{channelId}/posts/{postId}", null, cancellationToken);

    public Task<IReadOnlyList<CommunityForumTagDto>> GetForumTagsAsync(Guid communityId, Guid channelId,
        CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<CommunityForumTagDto>>(
        HttpMethod.Get, $"api/communities/{communityId}/forums/{channelId}/tags", null, cancellationToken);
    public Task<CommunityForumTagDto> CreateForumTagAsync(Guid communityId, Guid channelId,
        CreateCommunityForumTagRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityForumTagDto>(HttpMethod.Post,
            $"api/communities/{communityId}/forums/{channelId}/tags", request, cancellationToken);
    public Task<CommunityForumTagDto> UpdateForumTagAsync(Guid communityId, Guid channelId, Guid tagId,
        UpdateCommunityForumTagRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityForumTagDto>(HttpMethod.Put,
            $"api/communities/{communityId}/forums/{channelId}/tags/{tagId}", request, cancellationToken);
    public Task DeleteForumTagAsync(Guid communityId, Guid channelId, Guid tagId,
        CancellationToken cancellationToken = default) => SendNoContentAsync(HttpMethod.Delete,
        $"api/communities/{communityId}/forums/{channelId}/tags/{tagId}", null, cancellationToken);
    public Task ReorderForumTagsAsync(Guid communityId, Guid channelId, IReadOnlyList<Guid> tagIds,
        CancellationToken cancellationToken = default) => SendNoContentAsync(HttpMethod.Put,
        $"api/communities/{communityId}/forums/{channelId}/tags/order",
        new ReorderCommunityForumTagsRequest(tagIds), cancellationToken);
    public Task<CommunityForumPostDto> UpdateForumPostTagsAsync(Guid communityId, Guid channelId, Guid postId,
        IReadOnlyList<Guid> tagIds, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityForumPostDto>(HttpMethod.Put,
            $"api/communities/{communityId}/forums/{channelId}/posts/{postId}/tags",
            new UpdateCommunityForumPostTagsRequest(tagIds), cancellationToken);

    public Task<PermissionOverwriteScopeDto> GetPermissionScopeAsync(Guid communityId,
        PermissionOverwriteScopeType scopeType, Guid scopeId, CancellationToken cancellationToken = default) =>
        SendAsync<PermissionOverwriteScopeDto>(HttpMethod.Get,
            $"api/communities/{communityId}/permissions/{scopeType}/{scopeId}", null, cancellationToken);

    public Task SetPermissionOverwriteAsync(Guid communityId, PermissionOverwriteScopeType scopeType, Guid scopeId,
        SetPermissionOverwriteRequest request, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Put,
            $"api/communities/{communityId}/permissions/{scopeType}/{scopeId}/overwrites", request, cancellationToken);

    public Task<PermissionOverwriteSaveResultDto> ReplacePermissionOverwritesAsync(Guid communityId, PermissionOverwriteScopeType scopeType, Guid scopeId,
        ReplacePermissionOverwritesRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<PermissionOverwriteSaveResultDto>(HttpMethod.Put,
            $"api/communities/{communityId}/permissions/{scopeType}/{scopeId}", request, cancellationToken);

    public Task RemovePermissionOverwriteAsync(Guid communityId, PermissionOverwriteScopeType scopeType, Guid scopeId,
        RemovePermissionOverwriteRequest request, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post,
            $"api/communities/{communityId}/permissions/{scopeType}/{scopeId}/overwrites/remove", request, cancellationToken);

    public Task SyncChannelPermissionsAsync(Guid communityId, Guid channelId,
        CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"api/communities/{communityId}/channels/{channelId}/permissions/sync",
            null, cancellationToken);

    public Task<CommunityManagementDto> GetCommunityManagementAsync(Guid communityId, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityManagementDto>(HttpMethod.Get, $"api/communities/{communityId}/management", null, cancellationToken);

    public Task<CommunityProfileAssignmentDto> SetCommunityProfileAsync(Guid communityId, Guid? presetId,
        CancellationToken cancellationToken = default) => SendAsync<CommunityProfileAssignmentDto>(HttpMethod.Put,
        $"api/communities/{communityId}/members/@me/profile", new SetCommunityProfileRequest(presetId), cancellationToken);

    public Task<CommunityDto> UpdateCommunityAsync(Guid communityId, UpdateCommunityRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityDto>(HttpMethod.Patch, $"api/communities/{communityId}", request, cancellationToken);

    public Task<CommunityRoleDto> CreateCommunityRoleAsync(Guid communityId, CreateCommunityRoleRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityRoleDto>(HttpMethod.Post, $"api/communities/{communityId}/roles", request, cancellationToken);

    public Task<CommunityRoleDto> UpdateCommunityRoleAsync(Guid communityId, Guid roleId, UpdateCommunityRoleRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityRoleDto>(HttpMethod.Patch, $"api/communities/{communityId}/roles/{roleId}", request, cancellationToken);

    public Task MoveCommunityRoleAsync(Guid communityId, Guid roleId, int position, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"api/communities/{communityId}/roles/{roleId}/move", new MoveCommunityRoleRequest(position), cancellationToken);

    public Task DeleteCommunityRoleAsync(Guid communityId, Guid roleId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/communities/{communityId}/roles/{roleId}", null, cancellationToken);

    public Task SetCommunityMemberRolesAsync(Guid communityId, Guid accountId, IReadOnlyList<Guid> roleIds, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Put, $"api/communities/{communityId}/members/{accountId}/roles", new SetCommunityMemberRolesRequest(roleIds), cancellationToken);

    public Task KickCommunityMemberAsync(Guid communityId, Guid accountId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"api/communities/{communityId}/members/{accountId}/kick", null, cancellationToken);
    public Task LeaveCommunityAsync(Guid communityId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/communities/{communityId}/members/@me", null, cancellationToken);

    public Task BanCommunityMemberAsync(Guid communityId, Guid accountId, string? reason, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"api/communities/{communityId}/bans/{accountId}", new BanCommunityMemberRequest(reason), cancellationToken);

    public Task UnbanCommunityMemberAsync(Guid communityId, Guid accountId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/communities/{communityId}/bans/{accountId}", null, cancellationToken);

    public Task<CommunityInviteDto> CreateCommunityInviteAsync(Guid communityId, CreateCommunityInviteRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityInviteDto>(HttpMethod.Post, $"api/communities/{communityId}/invites", request, cancellationToken);

    public Task RevokeCommunityInviteAsync(Guid communityId, Guid inviteId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/communities/{communityId}/invites/{inviteId}", null, cancellationToken);

    public Task<CommunityInvitePreviewDto> ResolveCommunityInviteAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityInvitePreviewDto>(HttpMethod.Get, $"api/invites/{Uri.EscapeDataString(token)}", null, cancellationToken);

    public Task<JoinCommunityInviteResultDto> JoinCommunityInviteAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync<JoinCommunityInviteResultDto>(HttpMethod.Post, $"api/invites/{Uri.EscapeDataString(token)}/join", null, cancellationToken);

    public async Task<List<ChannelMessageDto>> GetChannelMessagesAsync(
        Guid communityId, Guid channelId, int limit = MessageHistoryDefaults.PageSize, CancellationToken cancellationToken = default) =>
        [.. (await GetChannelMessagePageAsync(communityId, channelId, limit, cancellationToken: cancellationToken)).Messages];

    public Task<MessageHistoryPage<ChannelMessageDto>> GetChannelMessagePageAsync(
        Guid communityId, Guid channelId, int limit = MessageHistoryDefaults.PageSize, string? before = null, Guid? around = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<MessageHistoryPage<ChannelMessageDto>>(HttpMethod.Get,
            $"api/communities/{communityId}/channels/{channelId}/messages?limit={Math.Clamp(limit, 1, MessageHistoryDefaults.MaximumPageSize)}{Query("before", before)}{Query("around", around?.ToString())}",
            null, cancellationToken);

    public Task<ReactionDetailsDto> GetReactionDetailsAsync(Guid messageId, ReactionEmojiRequest emoji,
        Guid? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        SendAsync<ReactionDetailsDto>(HttpMethod.Post,
            $"api/messages/{messageId}/reactions/query?page=1{Query("after", after?.ToString())}{Query("limit", limit?.ToString())}",
            emoji, cancellationToken);

    public Task<ReactionDetailsDto> GetDirectReactionDetailsAsync(Guid conversationId, Guid messageId,
        ReactionEmojiRequest emoji, Guid? after = null, int? limit = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<ReactionDetailsDto>(HttpMethod.Post,
            $"api/direct-messages/{conversationId}/messages/{messageId}/reactions/query?page=1{Query("after", after?.ToString())}{Query("limit", limit?.ToString())}",
            emoji, cancellationToken);

    public Task<MessageSearchPageDto> SearchCommunityMessagesAsync(
        Guid communityId, string? text, string? from, string? channel, string? before = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<MessageSearchPageDto>(HttpMethod.Get,
            $"api/communities/{communityId}/messages/search?limit={MessageHistoryDefaults.SearchPageSize}{Query("q", text)}{Query("from", from)}{Query("in", channel)}{Query("before", before)}",
            null, cancellationToken);

    public Task<MessageSearchPageDto> SearchCommunityMessagesAsync(
        Guid communityId, MessageSearchRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<MessageSearchPageDto>(HttpMethod.Post,
            $"api/communities/{communityId}/messages/search", request, cancellationToken);

    public Task MarkCommunityChannelReadAsync(Guid communityId, Guid channelId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"api/communities/{communityId}/channels/{channelId}/read", null, cancellationToken);

    public async Task<AttachmentUploadDto> UploadAttachmentAsync(Stream content, string fileName, string contentType,
        bool isSpoiler = false, int? width = null, int? height = null, string? averageColor = null,
        CancellationToken cancellationToken = default)
    {
        using var multipart = new MultipartFormDataContent();
        using var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        multipart.Add(streamContent, "file", fileName);
        multipart.Add(new StringContent(isSpoiler.ToString()), "isSpoiler");
        if (width is { } imageWidth) multipart.Add(new StringContent(imageWidth.ToString()), "width");
        if (height is { } imageHeight) multipart.Add(new StringContent(imageHeight.ToString()), "height");
        if (!string.IsNullOrWhiteSpace(averageColor)) multipart.Add(new StringContent(averageColor), "averageColor");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/attachments") { Content = multipart };
        if (!string.IsNullOrWhiteSpace(AccessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new NodeApiException(response.StatusCode, await ReadErrorAsync(response, cancellationToken));
        return await response.Content.ReadFromJsonAsync<AttachmentUploadDto>(cancellationToken: cancellationToken)
            ?? throw new NodeApiException(response.StatusCode, "The node returned an empty response.");
    }

    public async Task<byte[]> DownloadAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/attachments/{attachmentId}", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new NodeApiException(response.StatusCode, await ReadErrorAsync(response, cancellationToken));
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<byte[]> DownloadAttachmentPreviewAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/attachments/{attachmentId}/preview", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new NodeApiException(response.StatusCode, await ReadErrorAsync(response, cancellationToken));
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<AttachmentPlaybackAccessDto> GetAttachmentPlaybackAccessAsync(Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        var access = await SendAsync<AttachmentPlaybackAccessDto>(HttpMethod.Get,
            $"api/attachments/{attachmentId}/playback-access", null, cancellationToken);
        var absolute = new Uri(_http.BaseAddress!, access.Url).AbsoluteUri;
        return access with { Url = absolute };
    }

    private async Task MoveSidebarItemByPositionAsync(Guid communityId, Guid itemId,
        CommunitySidebarItemType itemType, Guid? parentCategoryId, int position, CancellationToken cancellationToken)
    {
        var structure = await GetCommunityStructureAsync(communityId, cancellationToken);
        var siblings = structure.Categories.Where(value => value.ParentCategoryId == parentCategoryId)
            .Select(value => (value.Id, Type: CommunitySidebarItemType.Category, value.Position))
            .Concat(structure.Channels.Where(value => value.CategoryId == parentCategoryId)
                .Select(value => (value.Id, Type: CommunitySidebarItemType.Channel, value.Position)))
            .Where(value => value.Id != itemId || value.Type != itemType)
            .OrderBy(value => value.Position).ThenBy(value => value.Type).ThenBy(value => value.Id).ToList();
        CommunitySidebarMoveRequest request;
        if (position >= siblings.Count)
            request = new(parentCategoryId, null, null, CommunitySidebarDropIntent.End);
        else
        {
            var target = siblings[Math.Clamp(position, 0, siblings.Count - 1)];
            request = new(parentCategoryId, target.Id, target.Type, CommunitySidebarDropIntent.Before);
        }
        if (itemType == CommunitySidebarItemType.Category)
            await MoveCategoryAsync(communityId, itemId, request, cancellationToken);
        else
            await MoveChannelAsync(communityId, itemId, request, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, path, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new NodeApiException(response.StatusCode, await ReadErrorAsync(response, cancellationToken));
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new NodeApiException(response.StatusCode, "The node returned an empty response.");
    }

    private static string Query(string name, string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : $"&{name}={Uri.EscapeDataString(value)}";

    private async Task SendNoContentAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, path, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new NodeApiException(response.StatusCode, await ReadErrorAsync(response, cancellationToken));
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (!string.IsNullOrWhiteSpace(AccessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        return await _http.SendAsync(request, cancellationToken);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content)) return $"The node returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("message", out var message)) return message.GetString() ?? content;
            if (document.RootElement.TryGetProperty("detail", out var detail)) return detail.GetString() ?? content;
            if (document.RootElement.TryGetProperty("title", out var title)) return title.GetString() ?? content;
        }
        catch (System.Text.Json.JsonException) { }
        return content;
    }
}

public sealed class NodeApiException(System.Net.HttpStatusCode statusCode, string message) : Exception(message)
{
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
}
