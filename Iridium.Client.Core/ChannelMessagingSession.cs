using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Iridium.Client.Core;

public sealed class ChannelMessagingSession(
    NodeSession nodeSession,
    ILogger<ChannelMessagingSession> logger) : IAsyncDisposable
{
    private readonly List<ChannelMessageDto> _messages = [];
    private readonly List<DirectMessageDto> _directMessages = [];
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _messageSync = new();
    private HubConnection? _connection;
    private Uri? _connectedNode;
    private Guid? _connectedAccountId;
    private bool _channelReady;
    private bool _directReady;
    private bool _disposed;

    public Guid? CommunityId { get; private set; }
    public Guid? ChannelId { get; private set; }
    public IReadOnlyList<ChannelMessageDto> Messages => _messages;
    public Guid? DirectConversationId { get; private set; }
    public IReadOnlyList<DirectMessageDto> DirectMessages => _directMessages;
    public bool IsConnected => _connection?.State == HubConnectionState.Connected;
    public event Action? Changed;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try { await EnsureConnectionAsync(cancellationToken); }
        finally { _lifecycleGate.Release(); }
    }

    public async Task OpenChannelAsync(Guid communityId, Guid channelId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectionAsync(cancellationToken);
            if (_channelReady && CommunityId == communityId && ChannelId == channelId) return;

            await LeaveActiveChannelAsync(cancellationToken);
            await LeaveActiveDirectConversationAsync(cancellationToken);
            ClearDirectState();

            CommunityId = communityId;
            ChannelId = channelId;
            _channelReady = false;
            _messages.Clear();
            NotifyChanged();

            await _connection!.InvokeAsync(ChatHubContract.JoinChannel, communityId, channelId, cancellationToken);
            var history = await nodeSession.AuthorizedClient.GetChannelMessagesAsync(
                communityId, channelId, cancellationToken: cancellationToken);
            foreach (var message in history) Upsert(message, notify: false);
            _channelReady = true;
            logger.LogDebug("Opened Community {CommunityId} channel {ChannelId} on {NodeAddress}.",
                communityId, channelId, _connectedNode);
            NotifyChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to open Community {CommunityId} channel {ChannelId} on {NodeAddress}.",
                communityId, channelId, _connectedNode);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task OpenDirectConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectionAsync(cancellationToken);
            if (_directReady && DirectConversationId == conversationId) return;
            await LeaveActiveChannelAsync(cancellationToken);
            await LeaveActiveDirectConversationAsync(cancellationToken);
            ClearChannelState();
            DirectConversationId = conversationId;
            _directReady = false;
            _directMessages.Clear();
            NotifyChanged();
            await _connection!.InvokeAsync(DirectMessageHubContract.JoinConversation, conversationId, cancellationToken);
            var history = await nodeSession.AuthorizedClient.GetDirectMessagesAsync(conversationId, cancellationToken: cancellationToken);
            foreach (var message in history) UpsertDirect(message, notify: false);
            await nodeSession.MarkDirectConversationReadAsync(conversationId, cancellationToken);
            _directReady = true;
            NotifyChanged();
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task SendDirectAsync(string content, Guid? replyToMessageId = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var conversationId = RequireDirectConversation();
            var result = await RequireConnection().InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.SendMessage, conversationId,
                new SendDirectMessageRequest(content, replyToMessageId), cancellationToken);
            UpsertDirect(result);
            await nodeSession.RefreshDirectConversationsAsync(cancellationToken);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task SendDirectToAsync(
        Guid conversationId,
        string content,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectionAsync(cancellationToken);
            var result = await RequireConnection().InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.SendMessage, conversationId,
                new SendDirectMessageRequest(content, null), cancellationToken);
            if (DirectConversationId == conversationId) UpsertDirect(result);
            await nodeSession.RefreshDirectConversationsAsync(cancellationToken);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task SetPresenceAsync(UserPresence preferred, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectionAsync(cancellationToken);
            await RequireConnection().InvokeAsync(PresenceHubContract.SetPresence, preferred, cancellationToken);
            await nodeSession.SetPreferredPresenceAsync(preferred, cancellationToken);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task EditDirectAsync(Guid messageId, string content, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var conversationId = RequireDirectConversation();
            var result = await RequireConnection().InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.EditMessage, conversationId, messageId,
                new EditDirectMessageRequest(content), cancellationToken);
            UpsertDirect(result);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task DeleteDirectAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await RequireConnection().InvokeAsync(
                DirectMessageHubContract.DeleteMessage, RequireDirectConversation(), messageId, cancellationToken);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task SendAsync(string content, Guid? replyToMessageId = null,
        IReadOnlyList<CommunityMentionInput>? mentions = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var (communityId, channelId) = RequireChannel();
            try
            {
                var result = await RequireConnection().InvokeAsync<ChannelMessageDto>(
                    ChatHubContract.SendMessage,
                    communityId,
                    channelId,
                    new SendChannelMessageRequest(content, replyToMessageId, mentions),
                    cancellationToken);
                Upsert(result);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Realtime send failed in Community {CommunityId} channel {ChannelId}.",
                    communityId, channelId);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task EditAsync(Guid messageId, string content, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var (communityId, channelId) = RequireChannel();
            try
            {
                var result = await RequireConnection().InvokeAsync<ChannelMessageDto>(
                    ChatHubContract.EditMessage,
                    communityId,
                    channelId,
                    messageId,
                    new EditChannelMessageRequest(content),
                    cancellationToken);
                Upsert(result);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Realtime edit failed for message {MessageId} in Community {CommunityId} channel {ChannelId}.",
                    messageId, communityId, channelId);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task DeleteAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var (communityId, channelId) = RequireChannel();
            try
            {
                await RequireConnection().InvokeAsync(
                    ChatHubContract.DeleteMessage,
                    communityId,
                    channelId,
                    messageId,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Realtime deletion failed for message {MessageId} in Community {CommunityId} channel {ChannelId}.",
                    messageId, communityId, channelId);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await LeaveActiveChannelAsync(cancellationToken);
            await LeaveActiveDirectConversationAsync(cancellationToken);
            ClearChannelState();
            ClearDirectState();
            NotifyChanged();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await LeaveActiveChannelAsync(cancellationToken);
            await LeaveActiveDirectConversationAsync(cancellationToken);
            ClearChannelState();
            ClearDirectState();
            if (_connection is not null)
            {
                logger.LogDebug("Disconnecting realtime client from {NodeAddress}.", _connectedNode);
                await _connection.DisposeAsync();
            }
            _connection = null;
            _connectedNode = null;
            _connectedAccountId = null;
            NotifyChanged();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        var client = nodeSession.AuthorizedClient;
        var accountId = nodeSession.Account?.Id
            ?? throw new InvalidOperationException("Log in before connecting realtime services.");
        if (_connection is not null && _connectedNode == client.NodeAddress && _connectedAccountId == accountId)
        {
            if (_connection.State == HubConnectionState.Connected) return;
            if (_connection.State == HubConnectionState.Disconnected)
            {
                logger.LogDebug("Starting realtime connection to {NodeAddress}.", client.NodeAddress);
                await _connection.StartAsync(cancellationToken);
                return;
            }

            throw new InvalidOperationException($"The realtime connection is currently {_connection.State.ToString().ToLowerInvariant()}.");
        }

        if (_connection is not null) await _connection.DisposeAsync();
        ClearChannelState();
        ClearDirectState();
        _connectedNode = client.NodeAddress;
        _connectedAccountId = accountId;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(client.NodeAddress, "hubs/chat"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(client.AccessToken);
            })
            .WithAutomaticReconnect()
            .Build();
        _connection = connection;

        connection.On<ChannelMessageDto>(ChatHubContract.MessageCreated,
            message => ReceiveSafely(ChatHubContract.MessageCreated, () => Receive(message)));
        connection.On<ChannelMessageDto>(ChatHubContract.MessageUpdated,
            message => ReceiveSafely(ChatHubContract.MessageUpdated, () => Receive(message)));
        connection.On<ChannelMessageDeletedEvent>(ChatHubContract.MessageDeleted,
            deleted => ReceiveSafely(ChatHubContract.MessageDeleted, () => ReceiveDeleted(deleted)));
        connection.On<DirectMessageDto>(DirectMessageHubContract.MessageCreated,
            message => ReceiveSafely(DirectMessageHubContract.MessageCreated, () => ReceiveDirect(message, refreshList: true)));
        connection.On<DirectMessageDto>(DirectMessageHubContract.MessageUpdated,
            message => ReceiveSafely(DirectMessageHubContract.MessageUpdated, () => ReceiveDirect(message, refreshList: false)));
        connection.On<DirectMessageDeletedEvent>(DirectMessageHubContract.MessageDeleted,
            deleted => ReceiveSafely(DirectMessageHubContract.MessageDeleted, () => ReceiveDirectDeleted(deleted)));
        connection.On<FriendshipChangedEvent>(FriendshipHubContract.RequestReceived,
            _event => _ = RefreshFriendsSafelyAsync(FriendshipHubContract.RequestReceived));
        connection.On<FriendshipChangedEvent>(FriendshipHubContract.RequestAccepted,
            _event => _ = RefreshFriendsSafelyAsync(FriendshipHubContract.RequestAccepted));
        connection.On<FriendshipChangedEvent>(FriendshipHubContract.RequestDeclined,
            _event => _ = RefreshFriendsSafelyAsync(FriendshipHubContract.RequestDeclined));
        connection.On<FriendshipChangedEvent>(FriendshipHubContract.FriendshipRemoved,
            _event => _ = RefreshFriendsSafelyAsync(FriendshipHubContract.FriendshipRemoved));
        connection.On<PresenceChangedEvent>(PresenceHubContract.PresenceChanged,
            change => ReceiveSafely(PresenceHubContract.PresenceChanged, () => nodeSession.ApplyPresence(change)));
        connection.On<CommunityStateChangedEvent>(CommunityHubContract.StateChanged,
            change => _ = ApplyCommunityChangeSafelyAsync(change));
        connection.On<CommunityAccessRevokedEvent>(CommunityHubContract.AccessRevoked,
            change => ReceiveSafely(CommunityHubContract.AccessRevoked, () =>
            {
                if (CommunityId == change.CommunityId) ClearChannelState();
                nodeSession.ApplyCommunityAccessRevoked(change);
                NotifyChanged();
            }));
        connection.On<CommunityMentionReceivedEvent>(CommunityMentionHubContract.Received,
            mention => ReceiveSafely(CommunityMentionHubContract.Received, () => nodeSession.ApplyCommunityMention(mention)));
        connection.Reconnecting += exception =>
        {
            logger.LogWarning(exception, "Realtime connection to {NodeAddress} is reconnecting.", _connectedNode);
            NotifyChanged();
            return Task.CompletedTask;
        };
        connection.Reconnected += async _ =>
        {
            try
            {
                if (CommunityId is { } communityId && ChannelId is { } channelId)
                    await connection.InvokeAsync(ChatHubContract.JoinChannel, communityId, channelId);
                if (DirectConversationId is { } conversationId)
                    await connection.InvokeAsync(DirectMessageHubContract.JoinConversation, conversationId);
                logger.LogInformation("Realtime connection to {NodeAddress} reconnected.", _connectedNode);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not restore the active channel subscription after reconnecting to {NodeAddress}.",
                    _connectedNode);
            }
            NotifyChanged();
        };
        connection.Closed += exception =>
        {
            if (exception is null)
                logger.LogInformation("Realtime connection to {NodeAddress} closed.", _connectedNode);
            else
                logger.LogError(exception, "Realtime connection to {NodeAddress} closed unexpectedly.", _connectedNode);
            NotifyChanged();
            return Task.CompletedTask;
        };

        try
        {
            logger.LogDebug("Starting realtime connection to {NodeAddress}.", client.NodeAddress);
            await connection.StartAsync(cancellationToken);
            logger.LogInformation("Realtime connection to {NodeAddress} established.", client.NodeAddress);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not connect the realtime client to {NodeAddress}.", client.NodeAddress);
            throw;
        }
    }

    private async Task LeaveActiveChannelAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected || CommunityId is not { } communityId || ChannelId is not { } channelId) return;
        try
        {
            await _connection!.InvokeAsync(ChatHubContract.LeaveChannel, communityId, channelId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not leave Community {CommunityId} channel {ChannelId} cleanly.",
                communityId, channelId);
        }
    }

    private async Task LeaveActiveDirectConversationAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected || DirectConversationId is not { } conversationId) return;
        try { await _connection!.InvokeAsync(DirectMessageHubContract.LeaveConversation, conversationId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not leave direct conversation {ConversationId} cleanly.", conversationId);
        }
    }

    private void ClearChannelState()
    {
        CommunityId = null;
        ChannelId = null;
        _channelReady = false;
        _messages.Clear();
    }

    private void ClearDirectState()
    {
        DirectConversationId = null;
        _directReady = false;
        _directMessages.Clear();
    }

    private void Receive(ChannelMessageDto message)
    {
        if (message.CommunityId != CommunityId || message.ChannelId != ChannelId) return;
        Upsert(message);
    }

    private void ReceiveDeleted(ChannelMessageDeletedEvent deleted)
    {
        if (deleted.CommunityId != CommunityId || deleted.ChannelId != ChannelId) return;
        var index = _messages.FindIndex(value => value.Id == deleted.MessageId);
        if (index >= 0) _messages[index] = _messages[index] with { Content = string.Empty, IsDeleted = true };
        for (var position = 0; position < _messages.Count; position++)
        {
            if (_messages[position].ReplyTo?.MessageId != deleted.MessageId) continue;
            _messages[position] = _messages[position] with
            {
                ReplyTo = _messages[position].ReplyTo! with { Excerpt = null, IsDeleted = true }
            };
        }
        NotifyChanged();
    }

    private void ReceiveDirect(DirectMessageDto message, bool refreshList)
    {
        if (message.ConversationId == DirectConversationId) UpsertDirect(message);
        if (message.ConversationId == DirectConversationId && message.Author.AccountId != nodeSession.Account?.Id)
            _ = MarkDirectReadSafelyAsync(message.ConversationId);
        if (refreshList) _ = RefreshDirectListSafelyAsync();
    }

    private void ReceiveDirectDeleted(DirectMessageDeletedEvent deleted)
    {
        if (deleted.ConversationId != DirectConversationId) return;
        var index = _directMessages.FindIndex(value => value.Id == deleted.MessageId);
        if (index >= 0) _directMessages[index] = _directMessages[index] with { Content = string.Empty, IsDeleted = true };
        for (var position = 0; position < _directMessages.Count; position++)
        {
            if (_directMessages[position].ReplyTo?.MessageId != deleted.MessageId) continue;
            _directMessages[position] = _directMessages[position] with
            {
                ReplyTo = _directMessages[position].ReplyTo! with { Excerpt = null, IsDeleted = true }
            };
        }
        NotifyChanged();
    }

    private async Task RefreshDirectListSafelyAsync()
    {
        try { await nodeSession.RefreshDirectConversationsAsync(); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not refresh the Direct Message list after a realtime event.");
        }
    }

    private async Task MarkDirectReadSafelyAsync(Guid conversationId)
    {
        try { await nodeSession.MarkDirectConversationReadAsync(conversationId); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not mark direct conversation {ConversationId} as read.", conversationId);
        }
    }

    private async Task RefreshFriendsSafelyAsync(string eventName)
    {
        try { await nodeSession.RefreshFriendsAsync(); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not refresh friends after realtime event {EventName}.", eventName);
        }
    }

    private async Task ApplyCommunityChangeSafelyAsync(CommunityStateChangedEvent change)
    {
        try { await nodeSession.ApplyCommunityChangeAsync(change); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not refresh Community {CommunityId} after realtime change {Change}.",
                change.CommunityId, change.Change);
        }
    }

    private void ReceiveSafely(string eventName, Action receive)
    {
        try { receive(); }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected client error while handling realtime event {EventName}.", eventName);
        }
    }

    private void Upsert(ChannelMessageDto message, bool notify = true)
    {
        lock (_messageSync)
        {
            var index = _messages.FindIndex(value => value.Id == message.Id);
            if (index < 0) _messages.Add(message); else _messages[index] = message;
            var excerpt = message.IsDeleted ? null : Excerpt(message.Content);
            for (var position = 0; position < _messages.Count; position++)
            {
                if (_messages[position].ReplyTo?.MessageId != message.Id) continue;
                _messages[position] = _messages[position] with
                {
                    ReplyTo = _messages[position].ReplyTo! with
                    {
                        AuthorDisplayName = message.Author.DisplayName,
                        Excerpt = excerpt,
                        IsDeleted = message.IsDeleted
                    }
                };
            }
            _messages.Sort((left, right) =>
            {
                var order = left.CreatedAt.CompareTo(right.CreatedAt);
                return order != 0 ? order : left.Id.CompareTo(right.Id);
            });
        }
        if (notify) NotifyChanged();
    }

    private void UpsertDirect(DirectMessageDto message, bool notify = true)
    {
        lock (_messageSync)
        {
            var index = _directMessages.FindIndex(value => value.Id == message.Id);
            if (index < 0) _directMessages.Add(message); else _directMessages[index] = message;
            var excerpt = message.IsDeleted ? null : Excerpt(message.Content);
            for (var position = 0; position < _directMessages.Count; position++)
            {
                if (_directMessages[position].ReplyTo?.MessageId != message.Id) continue;
                _directMessages[position] = _directMessages[position] with
                {
                    ReplyTo = _directMessages[position].ReplyTo! with
                    {
                        AuthorDisplayName = message.Author.DisplayName,
                        Excerpt = excerpt,
                        IsDeleted = message.IsDeleted
                    }
                };
            }
            _directMessages.Sort((left, right) =>
            {
                var order = left.CreatedAt.CompareTo(right.CreatedAt);
                return order != 0 ? order : left.Id.CompareTo(right.Id);
            });
        }
        if (notify) NotifyChanged();
    }

    private void NotifyChanged()
    {
        if (_disposed) return;
        if (Changed is not { } changed) return;
        foreach (Action handler in changed.GetInvocationList())
        {
            try { handler(); }
            catch (Exception exception)
            {
                logger.LogError(exception, "A realtime state subscriber failed while processing a change notification.");
            }
        }
    }

    private static string Excerpt(string content)
    {
        var oneLine = string.Join(' ', content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return oneLine;
    }

    private (Guid CommunityId, Guid ChannelId) RequireChannel() =>
        _channelReady && (CommunityId, ChannelId) is ({ } communityId, { } channelId)
            ? (communityId, channelId)
            : throw new InvalidOperationException("Open a text channel first.");

    private Guid RequireDirectConversation() =>
        _directReady && DirectConversationId is { } conversationId
            ? conversationId
            : throw new InvalidOperationException("Open a Direct Message first.");

    private HubConnection RequireConnection() => IsConnected
        ? _connection!
        : throw new InvalidOperationException("The realtime connection is offline.");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_connection is not null) await _connection.DisposeAsync();
            _connection = null;
            _connectedNode = null;
            _connectedAccountId = null;
            ClearChannelState();
            ClearDirectState();
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }
}
