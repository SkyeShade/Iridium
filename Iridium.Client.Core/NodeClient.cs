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

    public Task<List<DirectMessageDto>> GetDirectMessagesAsync(
        Guid conversationId, int limit = 75, CancellationToken cancellationToken = default) =>
        SendAsync<List<DirectMessageDto>>(HttpMethod.Get,
            $"api/direct-messages/{conversationId}/messages?limit={Math.Clamp(limit, 1, 100)}", null, cancellationToken);

    public Task HideDirectConversationAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"api/direct-messages/{conversationId}/hide", null, cancellationToken);

    public Task<CommunityStructureDto> GetCommunityStructureAsync(Guid communityId, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityStructureDto>(HttpMethod.Get, $"api/communities/{communityId}/structure", null, cancellationToken);

    public Task<CommunityCategoryDto> CreateCategoryAsync(Guid communityId, string name, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityCategoryDto>(HttpMethod.Post, $"api/communities/{communityId}/categories", new CreateCategoryRequest(name), cancellationToken);

    public Task<CommunityCategoryDto> UpdateCategoryAsync(Guid communityId, Guid categoryId, string name, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityCategoryDto>(HttpMethod.Patch, $"api/communities/{communityId}/categories/{categoryId}", new UpdateCategoryRequest(name), cancellationToken);

    public Task MoveCategoryAsync(Guid communityId, Guid categoryId, int position, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"api/communities/{communityId}/categories/{categoryId}/move", new MoveCategoryRequest(position), cancellationToken);

    public Task DeleteCategoryAsync(Guid communityId, Guid categoryId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/communities/{communityId}/categories/{categoryId}", null, cancellationToken);

    public Task<CommunityChannelDto> CreateChannelAsync(Guid communityId, string name, Guid? categoryId, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityChannelDto>(HttpMethod.Post, $"api/communities/{communityId}/channels", new CreateChannelRequest(name, categoryId), cancellationToken);

    public Task<CommunityChannelDto> UpdateChannelAsync(Guid communityId, Guid channelId, string name, Guid? categoryId, CancellationToken cancellationToken = default) =>
        SendAsync<CommunityChannelDto>(HttpMethod.Patch, $"api/communities/{communityId}/channels/{channelId}", new UpdateChannelRequest(name, categoryId), cancellationToken);

    public Task MoveChannelAsync(Guid communityId, Guid channelId, Guid? categoryId, int position, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"api/communities/{communityId}/channels/{channelId}/move", new MoveChannelRequest(categoryId, position), cancellationToken);

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

    public Task<List<ChannelMessageDto>> GetChannelMessagesAsync(
        Guid communityId, Guid channelId, int limit = 75, CancellationToken cancellationToken = default) =>
        SendAsync<List<ChannelMessageDto>>(
            HttpMethod.Get,
            $"api/communities/{communityId}/channels/{channelId}/messages?limit={Math.Clamp(limit, 1, 100)}",
            null,
            cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, path, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new NodeApiException(response.StatusCode, await ReadErrorAsync(response, cancellationToken));
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new NodeApiException(response.StatusCode, "The node returned an empty response.");
    }

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
