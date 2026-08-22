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
        CommunityChannelKind kind = CommunityChannelKind.Text, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityChannelDto>(HttpMethod.Patch, $"api/communities/{communityId}/channels/{channelId}", new UpdateChannelRequest(name, categoryId, kind), cancellationToken);

    public Task MoveChannelAsync(Guid communityId, Guid channelId, CommunitySidebarMoveRequest request, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"api/communities/{communityId}/channels/{channelId}/move", request, cancellationToken);

    public async Task MoveChannelAsync(Guid communityId, Guid channelId, Guid? categoryId, int position,
        CancellationToken cancellationToken = default) => await MoveSidebarItemByPositionAsync(
        communityId, channelId, CommunitySidebarItemType.Channel, categoryId, position, cancellationToken);

    public Task DeleteChannelAsync(Guid communityId, Guid channelId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/communities/{communityId}/channels/{channelId}", null, cancellationToken);

    public Task<CommunityManagementDto> GetCommunityManagementAsync(Guid communityId, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityManagementDto>(HttpMethod.Get, $"api/communities/{communityId}/management", null, cancellationToken);

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
