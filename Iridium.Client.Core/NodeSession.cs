using System.Net;
using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed class NodeSession(
    ISavedAccountStore accountStore,
    IActiveAccountSelectionStore activeSelectionStore,
    INodeTokenStore legacyTokenStore)
{
    private readonly List<SavedAccountRecord> _records = [];
    private readonly List<SavedAccount> _savedAccounts = [];
    private readonly List<CommunityDto> _communities = [];
    private readonly List<FriendDto> _friends = [];
    private readonly List<DirectConversationDto> _directConversations = [];
    private readonly SemaphoreSlim _friendRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _directRefreshGate = new(1, 1);
    private NodeClient? _client;
    private SavedAccountKey? _activeKey;
    private SavedAccountKey? _reauthenticationKey;

    public SavedNode? SelectedNode { get; private set; }
    public SavedNode? AuthenticationNode { get; private set; }
    public NodeAccountDto? Account { get; private set; }
    public SavedAccount? ActiveSavedAccount => _activeKey is { } key
        ? _savedAccounts.FirstOrDefault(value => SameKey(value.Key, key))
        : null;
    public SavedAccount? ReauthenticationAccount => _reauthenticationKey is { } key
        ? _savedAccounts.FirstOrDefault(value => SameKey(value.Key, key))
        : null;
    public IReadOnlyList<SavedAccount> SavedAccounts => _savedAccounts;
    public IReadOnlyList<CommunityDto> Communities => _communities;
    public IReadOnlyList<FriendDto> Friends => _friends;
    public IReadOnlyList<DirectConversationDto> DirectConversations => _directConversations;
    public bool IsAuthenticated => Account is not null;
    public event Action? Changed;
    public event Action<CommunityStateChangedEvent>? CommunityChanged;
    public event Action? RealtimeReconnected;
    public event Action<CommunityAccessRevokedEvent>? CommunityAccessRevoked;
    public event Action<CommunityDto>? CommunityJoined;
    public event Action<PresenceChangedEvent>? PresenceChanged;
    public event Action<CommunityMentionReceivedEvent>? CommunityMentionReceived;
    public event Action<CommunityChannelActivityEvent>? CommunityChannelActivity;
    public event Action<ProfileUpdatedEvent>? ProfileUpdated;

    public Task<ServerInfoDto> GetServerInfoAsync(CancellationToken cancellationToken = default) =>
        AuthorizedClient.GetServerInfoAsync(cancellationToken);

    public Task<AttachmentUploadDto> UploadAttachmentAsync(Stream content, string fileName, string contentType,
        bool isSpoiler = false, int? width = null, int? height = null, string? averageColor = null,
        CancellationToken cancellationToken = default) =>
        AuthorizedClient.UploadAttachmentAsync(content, fileName, contentType, isSpoiler,
            width, height, averageColor, cancellationToken);

    public Task<byte[]> DownloadAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default) =>
        AuthorizedClient.DownloadAttachmentAsync(attachmentId, cancellationToken);

    public Task<byte[]> DownloadAttachmentPreviewAsync(Guid attachmentId, CancellationToken cancellationToken = default) =>
        AuthorizedClient.DownloadAttachmentPreviewAsync(attachmentId, cancellationToken);

    public Task<AccountAvatarPresetsDto> GetAvatarPresetsAsync(CancellationToken cancellationToken = default) =>
        AuthorizedClient.GetAvatarPresetsAsync(cancellationToken);

    public Task<AccountAvatarPresetsDto> UploadAvatarPresetAsync(int slotIndex, Stream content, string fileName,
        string contentType, double cropX, double cropY, double zoom, bool setActive,
        CancellationToken cancellationToken = default) => AuthorizedClient.UploadAvatarPresetAsync(slotIndex,
        content, fileName, contentType, cropX, cropY, zoom, setActive, cancellationToken);

    public Task<AccountAvatarPresetDto> UpdateAvatarCropAsync(Guid presetId, UpdateAvatarCropRequest request,
        CancellationToken cancellationToken = default) =>
        AuthorizedClient.UpdateAvatarCropAsync(presetId, request, cancellationToken);

    public Task ActivateAvatarPresetAsync(Guid presetId, CancellationToken cancellationToken = default) =>
        AuthorizedClient.ActivateAvatarPresetAsync(presetId, cancellationToken);

    public Task DeleteAvatarPresetAsync(Guid presetId, CancellationToken cancellationToken = default) =>
        AuthorizedClient.DeleteAvatarPresetAsync(presetId, cancellationToken);

    internal NodeClient AuthorizedClient
    {
        get { EnsureAuthenticated(); return _client!; }
    }

    public async Task InitializeAsync(IReadOnlyList<SavedNode> nodes, CancellationToken cancellationToken = default)
    {
        var stored = await accountStore.LoadAsync(cancellationToken);
        _records.Clear();
        foreach (var record in stored.Accounts)
        {
            var normalized = SavedNodeState.NormalizeAddress(record.NodeAddress);
            if (_records.Any(value => SameKey(value.NodeAddress, value.AccountId, normalized, record.AccountId))) continue;
            _records.Add(record with { NodeAddress = normalized });
        }

        await MigrateLegacyNodeTokensAsync(nodes, cancellationToken);
        RebuildSummaries();

        var selectedKey = await activeSelectionStore.LoadAsync(cancellationToken);
        if (selectedKey is null && stored.ActiveNodeAddress is not null && stored.ActiveAccountId is { } legacyAccountId)
            selectedKey = new SavedAccountKey(SavedNodeState.NormalizeAddress(stored.ActiveNodeAddress), legacyAccountId);
        if (selectedKey is { } selected)
        {
            var record = FindRecord(selected);
            if (record is not null)
            {
                await TryActivateAsync(record, cancellationToken);
                return;
            }
        }

        var firstReady = _records.FirstOrDefault(value => HasUsableToken(value));
        if (firstReady is not null)
        {
            await TryActivateAsync(firstReady, cancellationToken);
            return;
        }

        BeginAuthentication(nodes.FirstOrDefault() ?? throw new InvalidOperationException("At least one Node is required."));
        NotifyChanged();
    }

    public void BeginAuthentication(SavedNode node, SavedAccountKey? reauthenticationKey = null)
    {
        AuthenticationNode = node;
        _reauthenticationKey = reauthenticationKey;
        NotifyChanged();
    }

    internal async Task<SavedAccountActivation?> PrepareSwitchAsync(
        SavedAccountKey key,
        CancellationToken cancellationToken = default)
    {
        var record = FindRecord(key) ?? throw new InvalidOperationException("That saved account is no longer available.");
        AuthenticationNode = NodeFor(record);
        _reauthenticationKey = key;
        if (!HasUsableToken(record))
        {
            NotifyChanged();
            return null;
        }

        var client = new NodeClient(new Uri(record.NodeAddress)) { AccessToken = record.SessionToken };
        try
        {
            var account = await client.GetCurrentAccountAsync(cancellationToken);
            var communities = await client.GetCommunitiesAsync(cancellationToken);
            var friends = await client.GetFriendsAsync(cancellationToken);
            var directConversations = await client.GetDirectConversationsAsync(cancellationToken);
            return new SavedAccountActivation(record, client, account, communities, friends, directConversations);
        }
        catch (NodeApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            ReplaceRecord(record with { SessionToken = null, Status = SavedAccountStatus.LoginRequired });
            await PersistAsync(cancellationToken);
            NotifyChanged();
            return null;
        }
        catch
        {
            NotifyChanged();
            return null;
        }
    }

    internal async Task ActivateSwitchAsync(SavedAccountActivation activation, CancellationToken cancellationToken = default)
    {
        ClearActiveState();
        _client = activation.Client;
        SelectedNode = NodeFor(activation.Record);
        Account = activation.Account;
        _communities.AddRange(activation.Communities);
        _friends.AddRange(activation.Friends);
        _directConversations.AddRange(activation.DirectConversations);
        SortCollections();
        _activeKey = new SavedAccountKey(activation.Record.NodeAddress, activation.Record.AccountId);
        AuthenticationNode = null;
        _reauthenticationKey = null;
        ReplaceRecord(activation.Record with
        {
            Username = activation.Account.Username,
            DisplayName = activation.Account.DisplayName,
            Pronouns = activation.Account.Pronouns,
            PreferredPresence = activation.Account.PreferredPresence,
            Status = SavedAccountStatus.Ready
        });
        await PersistAsync(cancellationToken);
        NotifyChanged();
    }

    internal async Task<AuthenticationResultDto> AuthenticateLoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var client = AuthenticationClient();
        return await client.LoginAsync(new LoginRequest(username, password), cancellationToken);
    }

    internal async Task<AuthenticationResultDto> AuthenticateRegistrationAsync(
        string username,
        string displayName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var client = AuthenticationClient();
        return await client.RegisterAsync(new RegisterAccountRequest(username, displayName, password), cancellationToken);
    }

    internal async Task<SavedAccountActivation> PrepareAuthenticationAsync(
        AuthenticationResultDto result,
        CancellationToken cancellationToken = default)
    {
        var node = AuthenticationNode ?? throw new InvalidOperationException("Select a Node first.");
        var normalizedAddress = SavedNodeState.NormalizeAddress(node.Address);
        var client = new NodeClient(new Uri(normalizedAddress)) { AccessToken = result.AccessToken };
        var communities = await client.GetCommunitiesAsync(cancellationToken);
        var friends = await client.GetFriendsAsync(cancellationToken);
        var directConversations = await client.GetDirectConversationsAsync(cancellationToken);

        return new SavedAccountActivation(
            new SavedAccountRecord(
                normalizedAddress,
                result.Account.Id,
                result.Account.Username,
                result.Account.DisplayName,
                result.Account.Pronouns,
                result.Account.PreferredPresence,
                result.AccessToken,
                SavedAccountStatus.Ready,
                node.PublicAuthority),
            client,
            result.Account,
            communities,
            friends,
            directConversations);
    }

    internal async Task AcceptAuthenticationAsync(
        SavedAccountActivation activation,
        CancellationToken cancellationToken = default)
    {
        var record = activation.Record;

        ClearActiveState();
        _client = activation.Client;
        SelectedNode = NodeFor(record);
        Account = activation.Account;
        _communities.AddRange(activation.Communities);
        _friends.AddRange(activation.Friends);
        _directConversations.AddRange(activation.DirectConversations);
        SortCollections();

        var key = new SavedAccountKey(record.NodeAddress, record.AccountId);
        _records.RemoveAll(value => SameKey(value.NodeAddress, value.AccountId, key.NodeAddress, key.AccountId));
        _records.Add(record);
        _activeKey = key;
        AuthenticationNode = null;
        _reauthenticationKey = null;
        await PersistAsync(cancellationToken);
        NotifyChanged();
    }

    public async Task UpdateProfileAsync(string displayName, string? pronouns, string? description, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        Account = await _client!.UpdateProfileAsync(new UpdateProfileRequest(displayName, pronouns, description), cancellationToken);
        if (_activeKey is { } key && FindRecord(key) is { } record)
        {
            ReplaceRecord(record with { DisplayName = Account.DisplayName, Pronouns = Account.Pronouns,
                PreferredPresence = Account.PreferredPresence });
            await PersistAsync(cancellationToken);
        }
        NotifyChanged();
    }

    public async Task<CommunityDto> CreateCommunityAsync(
        string name,
        string? description,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var community = await _client!.CreateCommunityAsync(new CreateCommunityRequest(name, description), cancellationToken);
        _communities.Add(community);
        SortCollections();
        NotifyChanged();
        return community;
    }

    public Task<CommunityInvitePreviewDto> ResolveCommunityInviteAsync(string token, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return _client!.ResolveCommunityInviteAsync(token, cancellationToken);
    }

    public async Task<JoinCommunityInviteResultDto> JoinCommunityInviteAsync(string token, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var result = await _client!.JoinCommunityInviteAsync(token, cancellationToken);
        if (_communities.All(value => value.Id != result.Community.Id)) _communities.Add(result.Community);
        SortCollections();
        NotifyChanged();
        CommunityJoined?.Invoke(result.Community);
        return result;
    }

    internal async Task ApplyCommunityChangeAsync(CommunityStateChangedEvent change)
    {
        if (!IsAuthenticated) return;
        await RefreshCommunitiesAsync();
        CommunityChanged?.Invoke(change);
    }

    internal void ApplyRealtimeReconnected() => RealtimeReconnected?.Invoke();

    internal void ApplyProfileUpdated(ProfileUpdatedEvent change) => ProfileUpdated?.Invoke(change);

    internal void ApplyCommunityAccessRevoked(CommunityAccessRevokedEvent change)
    {
        if (Account?.Id != change.AccountId) return;
        _communities.RemoveAll(value => value.Id == change.CommunityId);
        CommunityAccessRevoked?.Invoke(change);
        NotifyChanged();
    }

    internal void ApplyCommunityMention(CommunityMentionReceivedEvent mention) => CommunityMentionReceived?.Invoke(mention);

    internal async Task ApplyCommunityChannelActivityAsync(CommunityChannelActivityEvent activity)
    {
        if (!IsAuthenticated || activity.AuthorAccountId == Account?.Id) return;
        await RefreshCommunitiesAsync();
        CommunityChannelActivity?.Invoke(activity);
    }

    public async Task MarkCommunityChannelReadAsync(Guid communityId, Guid channelId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        await _client!.MarkCommunityChannelReadAsync(communityId, channelId, cancellationToken);
        await RefreshCommunitiesAsync(cancellationToken);
    }

    public Task<MessageSearchPageDto> SearchCommunityMessagesAsync(
        Guid communityId, string? text, string? from, string? channel, string? before = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return _client!.SearchCommunityMessagesAsync(communityId, text, from, channel, before, cancellationToken);
    }

    public Task<MessageSearchPageDto> SearchDirectMessagesAsync(
        Guid conversationId, string? text, string? from, string? before = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return _client!.SearchDirectMessagesAsync(conversationId, text, from, before, cancellationToken);
    }

    public Task<MessageSearchPageDto> SearchCommunityMessagesAsync(
        Guid communityId, MessageSearchRequest request, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return _client!.SearchCommunityMessagesAsync(communityId, request, cancellationToken);
    }

    public Task<MessageSearchPageDto> SearchDirectMessagesAsync(
        Guid conversationId, MessageSearchRequest request, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return _client!.SearchDirectMessagesAsync(conversationId, request, cancellationToken);
    }

    public async Task SendFriendRequestAsync(string username, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        await _client!.SendFriendRequestAsync(username, cancellationToken);
        await RefreshFriendsAsync(cancellationToken);
    }

    public Task BlockAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return _client!.BlockAccountAsync(accountId, cancellationToken);
    }

    public Task UnblockAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return _client!.UnblockAccountAsync(accountId, cancellationToken);
    }

    public async Task AcceptFriendRequestAsync(Guid friendshipId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        await _client!.AcceptFriendRequestAsync(friendshipId, cancellationToken);
        await RefreshFriendsAsync(cancellationToken);
    }

    public async Task<ResolvedProfileDto> ResolveProfileAsync(string username, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return await _client!.ResolveProfileAsync(username, cancellationToken);
    }

    public async Task RemoveFriendshipAsync(Guid friendshipId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        await _client!.RemoveFriendshipAsync(friendshipId, cancellationToken);
        await RefreshFriendsAsync(cancellationToken);
    }

    public async Task<DirectConversationDto> OpenDirectConversationAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return await _client!.OpenDirectConversationAsync(accountId, cancellationToken);
    }

    public async Task HideDirectConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        await _client!.HideDirectConversationAsync(conversationId, cancellationToken);
        _directConversations.RemoveAll(value => value.Id == conversationId);
        NotifyChanged();
    }

    public async Task MarkDirectConversationReadAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        await _client!.MarkDirectConversationReadAsync(conversationId, cancellationToken);
        await RefreshDirectConversationsAsync(cancellationToken);
    }

    internal async Task SetPreferredPresenceAsync(UserPresence preferred, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        Account = Account! with { PreferredPresence = preferred };
        if (_activeKey is { } key && FindRecord(key) is { } record)
        {
            ReplaceRecord(record with { PreferredPresence = preferred });
            await PersistAsync(cancellationToken);
        }
        NotifyChanged();
    }

    internal void ApplyPresence(PresenceChangedEvent change)
    {
        for (var index = 0; index < _friends.Count; index++)
            if (_friends[index].AccountId == change.AccountId)
                _friends[index] = _friends[index] with { Presence = change.Presence };
        for (var index = 0; index < _directConversations.Count; index++)
            if (_directConversations[index].OtherParticipant.AccountId == change.AccountId)
                _directConversations[index] = _directConversations[index] with
                {
                    OtherParticipant = _directConversations[index].OtherParticipant with { Presence = change.Presence }
                };
        PresenceChanged?.Invoke(change);
        NotifyChanged();
    }

    public async Task RefreshDirectConversationsAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        await _directRefreshGate.WaitAsync(cancellationToken);
        try
        {
            var conversations = await _client!.GetDirectConversationsAsync(cancellationToken);
            _directConversations.Clear();
            _directConversations.AddRange(conversations
                .GroupBy(value => value.Id)
                .Select(group => group.OrderByDescending(value => value.LastMessageAt).First()));
            SortCollections();
            NotifyChanged();
        }
        finally { _directRefreshGate.Release(); }
    }

    public async Task RefreshCommunitiesAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var communities = await _client!.GetCommunitiesAsync(cancellationToken);
        _communities.Clear();
        _communities.AddRange(communities);
        SortCollections();
        NotifyChanged();
    }

    internal async Task LogoutActiveAsync(CancellationToken cancellationToken = default)
    {
        if (_activeKey is not { } key || FindRecord(key) is not { } record) return;
        try
        {
            if (_client is not null && Account is not null) await _client.LogoutAsync(cancellationToken);
        }
        catch
        {
            // Local logout must still discard the credential when the Node cannot be reached.
        }

        ReplaceRecord(record with { SessionToken = null, Status = SavedAccountStatus.LoginRequired });
        ClearActiveState();
        AuthenticationNode = NodeFor(record);
        _reauthenticationKey = key;
        await PersistAsync(cancellationToken);
        NotifyChanged();
    }

    internal async Task RemoveAccountAsync(SavedAccountKey key, CancellationToken cancellationToken = default)
    {
        var record = FindRecord(key);
        if (record is null) return;
        if (!string.IsNullOrWhiteSpace(record.SessionToken))
        {
            try
            {
                var client = new NodeClient(new Uri(record.NodeAddress)) { AccessToken = record.SessionToken };
                await client.LogoutAsync(cancellationToken);
            }
            catch
            {
                // Removing from this device is local and must work while a Node is offline.
            }
        }

        var wasActive = _activeKey is { } active && SameKey(active, key);
        _records.Remove(record);
        if (wasActive) ClearActiveState();
        if (_reauthenticationKey is { } pending && SameKey(pending, key)) _reauthenticationKey = null;
        await PersistAsync(cancellationToken);
        NotifyChanged();
    }

    private async Task<bool> TryActivateAsync(SavedAccountRecord record, CancellationToken cancellationToken)
    {
        ClearActiveState();
        AuthenticationNode = NodeFor(record);
        _reauthenticationKey = new SavedAccountKey(record.NodeAddress, record.AccountId);
        if (!HasUsableToken(record))
        {
            NotifyChanged();
            return false;
        }

        var client = new NodeClient(new Uri(record.NodeAddress)) { AccessToken = record.SessionToken };
        try
        {
            var account = await client.GetCurrentAccountAsync(cancellationToken);
            var communities = await client.GetCommunitiesAsync(cancellationToken);
            var friends = await client.GetFriendsAsync(cancellationToken);
            var directConversations = await client.GetDirectConversationsAsync(cancellationToken);
            _client = client;
            SelectedNode = NodeFor(record);
            Account = account;
            _communities.AddRange(communities);
            _friends.AddRange(friends);
            _directConversations.AddRange(directConversations);
            SortCollections();
            _activeKey = new SavedAccountKey(record.NodeAddress, record.AccountId);
            AuthenticationNode = null;
            _reauthenticationKey = null;
            ReplaceRecord(record with
            {
                Username = account.Username,
                DisplayName = account.DisplayName,
                Pronouns = account.Pronouns,
                PreferredPresence = account.PreferredPresence,
                Status = SavedAccountStatus.Ready
            });
            await PersistAsync(cancellationToken);
            NotifyChanged();
            return true;
        }
        catch (NodeApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            ReplaceRecord(record with { SessionToken = null, Status = SavedAccountStatus.LoginRequired });
            await PersistAsync(cancellationToken);
            NotifyChanged();
            return false;
        }
        catch
        {
            NotifyChanged();
            return false;
        }
    }

    private async Task MigrateLegacyNodeTokensAsync(IReadOnlyList<SavedNode> nodes, CancellationToken cancellationToken)
    {
        foreach (var node in nodes)
        {
            var token = await legacyTokenStore.LoadAsync(node.Address, cancellationToken);
            if (string.IsNullOrWhiteSpace(token)) continue;
            try
            {
                var normalized = SavedNodeState.NormalizeAddress(node.Address);
                var client = new NodeClient(new Uri(normalized)) { AccessToken = token };
                var account = await client.GetCurrentAccountAsync(cancellationToken);
                if (_records.All(value => !SameKey(value.NodeAddress, value.AccountId, normalized, account.Id)))
                    _records.Add(new SavedAccountRecord(normalized, account.Id, account.Username, account.DisplayName,
                        account.Pronouns, account.PreferredPresence, token, SavedAccountStatus.Ready, node.PublicAuthority));
            }
            catch
            {
                // An invalid legacy token is discarded; no password or server data is affected.
            }
            finally
            {
                await legacyTokenStore.RemoveAsync(node.Address, cancellationToken);
            }
        }
    }

    private NodeClient AuthenticationClient()
    {
        var node = AuthenticationNode ?? throw new InvalidOperationException("Select a Node first.");
        return new NodeClient(new Uri(node.Address));
    }

    public async Task RefreshFriendsAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        await _friendRefreshGate.WaitAsync(cancellationToken);
        try
        {
            var friends = await _client!.GetFriendsAsync(cancellationToken);
            _friends.Clear();
            _friends.AddRange(friends.GroupBy(value => value.FriendshipId).Select(group => group.First()));
            SortCollections();
            NotifyChanged();
        }
        finally { _friendRefreshGate.Release(); }
    }

    private void SortCollections()
    {
        _communities.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        _friends.Sort((left, right) =>
        {
            var display = string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
            return display != 0 ? display : string.Compare(left.Username, right.Username, StringComparison.OrdinalIgnoreCase);
        });
        _directConversations.Sort((left, right) =>
            Nullable.Compare(right.LastMessageAt, left.LastMessageAt));
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        RebuildSummaries();
        await accountStore.SaveAsync(new SavedAccountStoreData(_records.ToArray(), null, null), cancellationToken);
        await activeSelectionStore.SaveAsync(_activeKey, cancellationToken);
    }

    private void RebuildSummaries()
    {
        _savedAccounts.Clear();
        _savedAccounts.AddRange(_records.Select(value => new SavedAccount(
            value.NodeAddress,
            value.AccountId,
            value.Username,
            value.DisplayName,
            value.Pronouns,
            value.PreferredPresence,
            value.Status,
            value.NodeIdentityAuthority)));
    }

    private void ReplaceRecord(SavedAccountRecord replacement)
    {
        var index = _records.FindIndex(value => SameKey(value.NodeAddress, value.AccountId,
            replacement.NodeAddress, replacement.AccountId));
        if (index >= 0) _records[index] = replacement;
        RebuildSummaries();
    }

    private SavedAccountRecord? FindRecord(SavedAccountKey key) => _records.FirstOrDefault(value =>
        SameKey(value.NodeAddress, value.AccountId, key.NodeAddress, key.AccountId));

    private void ClearActiveState()
    {
        _client = null;
        SelectedNode = null;
        Account = null;
        _activeKey = null;
        _communities.Clear();
        _friends.Clear();
        _directConversations.Clear();
    }

    private void EnsureAuthenticated()
    {
        if (_client is null || SelectedNode is null || Account is null)
            throw new InvalidOperationException("Log in first.");
    }

    private void NotifyChanged() => Changed?.Invoke();
    private static SavedNode NodeFor(SavedAccountRecord record) =>
        new(record.NodeAddress, null, false, record.NodeIdentityAuthority);
    private static bool HasUsableToken(SavedAccountRecord record) =>
        record.Status == SavedAccountStatus.Ready && !string.IsNullOrWhiteSpace(record.SessionToken);
    private static bool SameKey(SavedAccountKey left, SavedAccountKey right) =>
        SameKey(left.NodeAddress, left.AccountId, right.NodeAddress, right.AccountId);
    private static bool SameKey(string leftNode, Guid leftAccount, string rightNode, Guid rightAccount) =>
        leftAccount == rightAccount && string.Equals(leftNode.TrimEnd('/'), rightNode.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    internal sealed record SavedAccountActivation(
        SavedAccountRecord Record,
        NodeClient Client,
        NodeAccountDto Account,
        IReadOnlyList<CommunityDto> Communities,
        IReadOnlyList<FriendDto> Friends,
        IReadOnlyList<DirectConversationDto> DirectConversations);
}
