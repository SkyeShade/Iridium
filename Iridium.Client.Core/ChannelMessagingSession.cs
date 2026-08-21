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
    private readonly Dictionary<Guid, Func<CancellationToken, Task<IReadOnlyList<AttachmentDto>>>> _channelAttachmentUploads = [];
    private readonly Dictionary<Guid, Func<CancellationToken, Task<IReadOnlyList<AttachmentDto>>>> _directAttachmentUploads = [];
    private HubConnection? _connection;
    private Uri? _connectedNode;
    private Guid? _connectedAccountId;
    private bool _channelReady;
    private bool _directReady;
    private string? _channelOlderCursor;
    private string? _directOlderCursor;
    private bool _channelHasOlder;
    private bool _directHasOlder;
    private bool _loadingOlder;
    private CancellationTokenSource _historyCancellation = new();
    private bool _disposed;

    public Guid? CommunityId { get; private set; }
    public Guid? ChannelId { get; private set; }
    public IReadOnlyList<ChannelMessageDto> Messages => _messages;
    public Guid? DirectConversationId { get; private set; }
    public IReadOnlyList<DirectMessageDto> DirectMessages => _directMessages;
    public bool HasOlder => CommunityId is not null ? _channelHasOlder : _directHasOlder;
    public bool IsLoadingOlder => _loadingOlder;
    public MessageWindowMode WindowMode { get; private set; } = MessageWindowMode.Latest;
    public int WindowRevision { get; private set; }
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
        CancelHistoryRequests();
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
            var history = await nodeSession.AuthorizedClient.GetChannelMessagePageAsync(
                communityId, channelId, cancellationToken: cancellationToken);
            ApplyChannelPage(history, replace: true);
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
        CancelHistoryRequests();
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
            var history = await nodeSession.AuthorizedClient.GetDirectMessagePageAsync(conversationId, cancellationToken: cancellationToken);
            ApplyDirectPage(history, replace: true);
            await nodeSession.MarkDirectConversationReadAsync(conversationId, cancellationToken);
            _directReady = true;
            NotifyChanged();
        }
        finally { _lifecycleGate.Release(); }
    }

    public Task LoadOlderAsync(CancellationToken cancellationToken = default) =>
        CommunityId is not null ? LoadOlderChannelAsync(cancellationToken) : LoadOlderDirectAsync(cancellationToken);

    public async Task ResetToLatestAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CancelHistoryRequests();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (CommunityId is { } communityId && ChannelId is { } channelId)
            {
                var page = await nodeSession.AuthorizedClient.GetChannelMessagePageAsync(
                    communityId, channelId, cancellationToken: cancellationToken);
                ApplyChannelPage(page, replace: true);
            }
            else if (DirectConversationId is { } conversationId)
            {
                var page = await nodeSession.AuthorizedClient.GetDirectMessagePageAsync(
                    conversationId, cancellationToken: cancellationToken);
                ApplyDirectPage(page, replace: true);
            }
            WindowMode = MessageWindowMode.Latest;
            WindowRevision++;
            NotifyChanged();
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task OpenChannelAroundAsync(Guid communityId, Guid channelId, Guid messageId,
        CancellationToken cancellationToken = default)
    {
        if (CommunityId != communityId || ChannelId != channelId) await OpenChannelAsync(communityId, channelId, cancellationToken);
        CancelHistoryRequests();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var page = await nodeSession.AuthorizedClient.GetChannelMessagePageAsync(
                communityId, channelId, around: messageId, cancellationToken: cancellationToken);
            ApplyChannelPage(page, replace: true);
            WindowMode = MessageWindowMode.SearchTarget;
            WindowRevision++;
            NotifyChanged();
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task OpenDirectAroundAsync(Guid conversationId, Guid messageId,
        CancellationToken cancellationToken = default)
    {
        if (DirectConversationId != conversationId) await OpenDirectConversationAsync(conversationId, cancellationToken);
        CancelHistoryRequests();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var page = await nodeSession.AuthorizedClient.GetDirectMessagePageAsync(
                conversationId, around: messageId, cancellationToken: cancellationToken);
            ApplyDirectPage(page, replace: true);
            WindowMode = MessageWindowMode.SearchTarget;
            WindowRevision++;
            NotifyChanged();
        }
        finally { _lifecycleGate.Release(); }
    }

    public Guid QueueDirectMessage(string content, Guid? replyToMessageId = null,
        IReadOnlyList<AttachmentDto>? attachments = null,
        Func<CancellationToken, Task<IReadOnlyList<AttachmentDto>>>? uploadAttachments = null) =>
        BeginDirectMessage(content, replyToMessageId, attachments: attachments,
            uploadAttachments: uploadAttachments).ClientMessageId;

    public Task SendDirectAsync(string content, Guid? replyToMessageId = null,
        CancellationToken cancellationToken = default) =>
        BeginDirectMessage(content, replyToMessageId, cancellationToken: cancellationToken).Completion;

    private OutgoingOperation BeginDirectMessage(
        string content, Guid? replyToMessageId, IReadOnlyList<AttachmentDto>? attachments = null,
        Func<CancellationToken, Task<IReadOnlyList<AttachmentDto>>>? uploadAttachments = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var conversationId = RequireDirectConversation();
        var account = nodeSession.Account ?? throw new InvalidOperationException("An authenticated account is required.");
        var clientMessageId = Guid.NewGuid();
        var pending = new DirectMessageDto(
            clientMessageId, conversationId,
            new(account.Id, account.Username, account.DisplayName), content, DateTimeOffset.UtcNow,
            null, false, DirectReply(replyToMessageId), clientMessageId, MessageDeliveryState.Pending,
            Attachments: attachments);
        var reloadLatest = AddOptimisticDirect(pending);
        if (uploadAttachments is not null) _directAttachmentUploads[clientMessageId] = uploadAttachments;
        return new(clientMessageId, CompleteDirectSendAsync(pending, reloadLatest, cancellationToken));
    }

    private async Task CompleteDirectSendAsync(
        DirectMessageDto pending, bool reloadLatest = false, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (pending.ClientMessageId is { } clientId && _directAttachmentUploads.TryGetValue(clientId, out var upload))
            {
                pending = pending with { Attachments = await upload(cancellationToken) };
                if (DirectConversationId == pending.ConversationId) UpsertDirect(pending);
            }
            var result = await RequireConnection().InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.SendMessage, pending.ConversationId,
                new SendDirectMessageRequest(pending.Content, pending.ReplyTo?.MessageId, pending.ClientMessageId,
                    pending.Attachments?.Select(value => value.Id).ToArray()), cancellationToken);
            if (DirectConversationId == pending.ConversationId) UpsertDirect(result);
            if (pending.ClientMessageId is { } completedId) _directAttachmentUploads.Remove(completedId);
            if (reloadLatest && DirectConversationId == pending.ConversationId)
            {
                var page = await nodeSession.AuthorizedClient.GetDirectMessagePageAsync(
                    pending.ConversationId, cancellationToken: cancellationToken);
                ApplyDirectPage(page, replace: true);
                NotifyChanged();
            }
            await nodeSession.RefreshDirectConversationsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Direct Message {ClientMessageId} failed to send.", pending.ClientMessageId);
            MarkDirectFailed(pending.ConversationId, pending.ClientMessageId!.Value, exception);
        }
        finally { _lifecycleGate.Release(); }
    }

    public Task RetryDirectAsync(Guid clientMessageId, CancellationToken cancellationToken = default)
    {
        DirectMessageDto? pending;
        lock (_messageSync)
        {
            var index = _directMessages.FindIndex(value => value.ClientMessageId == clientMessageId &&
                value.DeliveryState == MessageDeliveryState.Failed);
            if (index < 0) return Task.CompletedTask;
            pending = _directMessages[index] with
            {
                DeliveryState = MessageDeliveryState.Pending, DeliveryError = null, CanRetry = false
            };
            _directMessages[index] = pending;
        }
        NotifyChanged();
        return CompleteDirectSendAsync(pending, cancellationToken: cancellationToken);
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
                new SendDirectMessageRequest(content, null, Guid.NewGuid()), cancellationToken);
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

    public Guid QueueMessage(string content, Guid? replyToMessageId = null,
        IReadOnlyList<CommunityMentionInput>? mentions = null, IReadOnlyList<AttachmentDto>? attachments = null,
        Func<CancellationToken, Task<IReadOnlyList<AttachmentDto>>>? uploadAttachments = null) =>
        BeginMessage(content, replyToMessageId, mentions, attachments: attachments,
            uploadAttachments: uploadAttachments).ClientMessageId;

    public Task SendAsync(string content, Guid? replyToMessageId = null,
        IReadOnlyList<CommunityMentionInput>? mentions = null, CancellationToken cancellationToken = default) =>
        BeginMessage(content, replyToMessageId, mentions, cancellationToken: cancellationToken).Completion;

    private OutgoingOperation BeginMessage(
        string content, Guid? replyToMessageId, IReadOnlyList<CommunityMentionInput>? mentions,
        IReadOnlyList<AttachmentDto>? attachments = null,
        Func<CancellationToken, Task<IReadOnlyList<AttachmentDto>>>? uploadAttachments = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var (communityId, channelId) = RequireChannel();
        var account = nodeSession.Account ?? throw new InvalidOperationException("An authenticated account is required.");
        var clientMessageId = Guid.NewGuid();
        var pending = new ChannelMessageDto(
            clientMessageId, communityId, channelId,
            new(account.Id, account.Username, account.DisplayName), content, DateTimeOffset.UtcNow,
            null, false, ChannelReply(replyToMessageId), OptimisticMentions(content, mentions),
            clientMessageId, MessageDeliveryState.Pending, Attachments: attachments);
        var reloadLatest = AddOptimisticChannel(pending);
        if (uploadAttachments is not null) _channelAttachmentUploads[clientMessageId] = uploadAttachments;
        return new(clientMessageId, CompleteChannelSendAsync(pending, reloadLatest, cancellationToken));
    }

    private async Task CompleteChannelSendAsync(
        ChannelMessageDto pending, bool reloadLatest = false, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                if (pending.ClientMessageId is { } clientId && _channelAttachmentUploads.TryGetValue(clientId, out var upload))
                {
                    pending = pending with { Attachments = await upload(cancellationToken) };
                    if (CommunityId == pending.CommunityId && ChannelId == pending.ChannelId) Upsert(pending);
                }
                var result = await RequireConnection().InvokeAsync<ChannelMessageDto>(
                    ChatHubContract.SendMessage,
                    pending.CommunityId,
                    pending.ChannelId,
                    new SendChannelMessageRequest(pending.Content, pending.ReplyTo?.MessageId,
                        pending.Mentions?.Select(value => new CommunityMentionInput(value.Kind, value.TargetId, value.Start, value.Length)).ToArray(),
                        pending.ClientMessageId, pending.Attachments?.Select(value => value.Id).ToArray()),
                    cancellationToken);
                if (CommunityId == pending.CommunityId && ChannelId == pending.ChannelId) Upsert(result);
                if (pending.ClientMessageId is { } completedId) _channelAttachmentUploads.Remove(completedId);
                if (reloadLatest && CommunityId == pending.CommunityId && ChannelId == pending.ChannelId)
                {
                    var page = await nodeSession.AuthorizedClient.GetChannelMessagePageAsync(
                        pending.CommunityId, pending.ChannelId, cancellationToken: cancellationToken);
                    ApplyChannelPage(page, replace: true);
                    NotifyChanged();
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Realtime send failed in Community {CommunityId} channel {ChannelId}.",
                    pending.CommunityId, pending.ChannelId);
                MarkChannelFailed(pending.CommunityId, pending.ChannelId, pending.ClientMessageId!.Value, exception);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task RetryAsync(Guid clientMessageId, CancellationToken cancellationToken = default)
    {
        ChannelMessageDto? pending;
        lock (_messageSync)
        {
            var index = _messages.FindIndex(value => value.ClientMessageId == clientMessageId &&
                value.DeliveryState == MessageDeliveryState.Failed);
            if (index < 0) return Task.CompletedTask;
            pending = _messages[index] with
            {
                DeliveryState = MessageDeliveryState.Pending, DeliveryError = null, CanRetry = false
            };
            _messages[index] = pending;
        }
        NotifyChanged();
        return CompleteChannelSendAsync(pending, cancellationToken: cancellationToken);
    }

    public void RemoveFailedMessage(Guid clientMessageId)
    {
        lock (_messageSync)
        {
            _messages.RemoveAll(value => value.ClientMessageId == clientMessageId &&
                value.DeliveryState == MessageDeliveryState.Failed);
            _directMessages.RemoveAll(value => value.ClientMessageId == clientMessageId &&
                value.DeliveryState == MessageDeliveryState.Failed);
        }
        _channelAttachmentUploads.Remove(clientMessageId);
        _directAttachmentUploads.Remove(clientMessageId);
        NotifyChanged();
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
        CancelHistoryRequests();
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
        CancelHistoryRequests();
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
            message => ReceiveSafely(ChatHubContract.MessageCreated, () => ReceiveCreated(message)));
        connection.On<ChannelMessageDto>(ChatHubContract.MessageUpdated,
            message => ReceiveSafely(ChatHubContract.MessageUpdated, () => ReceiveUpdated(message)));
        connection.On<ChannelMessageDeletedEvent>(ChatHubContract.MessageDeleted,
            deleted => ReceiveSafely(ChatHubContract.MessageDeleted, () => ReceiveDeleted(deleted)));
        connection.On<DirectMessageDto>(DirectMessageHubContract.MessageCreated,
            message => ReceiveSafely(DirectMessageHubContract.MessageCreated, () => ReceiveDirectCreated(message)));
        connection.On<DirectMessageDto>(DirectMessageHubContract.MessageUpdated,
            message => ReceiveSafely(DirectMessageHubContract.MessageUpdated, () => ReceiveDirectUpdated(message)));
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
        connection.On<CommunityChannelActivityEvent>(CommunityHubContract.ChannelActivity,
            activity => _ = ApplyCommunityActivitySafelyAsync(activity));
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

    private async Task LoadOlderChannelAsync(CancellationToken cancellationToken)
    {
        if (_loadingOlder || !_channelHasOlder || CommunityId is not { } communityId || ChannelId is not { } channelId) return;
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_loadingOlder || !_channelHasOlder) return;
            _loadingOlder = true;
            NotifyChanged();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _historyCancellation.Token);
            var page = await nodeSession.AuthorizedClient.GetChannelMessagePageAsync(
                communityId, channelId, before: _channelOlderCursor, cancellationToken: linked.Token);
            if (CommunityId != communityId || ChannelId != channelId) return;
            ApplyChannelPage(page, replace: false);
            WindowMode = MessageWindowMode.Historical;
            NotifyChanged();
        }
        catch (OperationCanceledException) { }
        finally
        {
            _loadingOlder = false;
            _lifecycleGate.Release();
            NotifyChanged();
        }
    }

    private async Task LoadOlderDirectAsync(CancellationToken cancellationToken)
    {
        if (_loadingOlder || !_directHasOlder || DirectConversationId is not { } conversationId) return;
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_loadingOlder || !_directHasOlder) return;
            _loadingOlder = true;
            NotifyChanged();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _historyCancellation.Token);
            var page = await nodeSession.AuthorizedClient.GetDirectMessagePageAsync(
                conversationId, before: _directOlderCursor, cancellationToken: linked.Token);
            if (DirectConversationId != conversationId) return;
            ApplyDirectPage(page, replace: false);
            WindowMode = MessageWindowMode.Historical;
            NotifyChanged();
        }
        catch (OperationCanceledException) { }
        finally
        {
            _loadingOlder = false;
            _lifecycleGate.Release();
            NotifyChanged();
        }
    }

    private bool AddOptimisticChannel(ChannelMessageDto pending)
    {
        var reloadLatest = WindowMode == MessageWindowMode.SearchTarget;
        lock (_messageSync)
        {
            if (reloadLatest) _messages.Clear();
            TrimConfirmed(_messages);
            _messages.Add(pending);
            SortMessages(_messages);
        }
        WindowMode = MessageWindowMode.Latest;
        WindowRevision++;
        NotifyChanged();
        return reloadLatest;
    }

    private bool AddOptimisticDirect(DirectMessageDto pending)
    {
        var reloadLatest = WindowMode == MessageWindowMode.SearchTarget;
        lock (_messageSync)
        {
            if (reloadLatest) _directMessages.Clear();
            TrimConfirmed(_directMessages);
            _directMessages.Add(pending);
            SortMessages(_directMessages);
        }
        WindowMode = MessageWindowMode.Latest;
        WindowRevision++;
        NotifyChanged();
        return reloadLatest;
    }

    private static void TrimConfirmed<T>(List<T> messages) where T : notnull
    {
        while (messages.Count >= MessageHistoryDefaults.PageSize)
        {
            var index = messages.FindIndex(value => value switch
            {
                ChannelMessageDto channel => channel.DeliveryState == MessageDeliveryState.Confirmed,
                DirectMessageDto direct => direct.DeliveryState == MessageDeliveryState.Confirmed,
                _ => false
            });
            if (index < 0) return;
            messages.RemoveAt(index);
        }
    }

    private MessageReplyDto? ChannelReply(Guid? messageId)
    {
        if (messageId is not { } id) return null;
        var original = _messages.FirstOrDefault(value => value.Id == id);
        return original is null ? null : new(id, original.Author.AccountId, original.Author.DisplayName,
            original.IsDeleted ? null : Excerpt(original.Content), original.IsDeleted);
    }

    private MessageReplyDto? DirectReply(Guid? messageId)
    {
        if (messageId is not { } id) return null;
        var original = _directMessages.FirstOrDefault(value => value.Id == id);
        return original is null ? null : new(id, original.Author.AccountId, original.Author.DisplayName,
            original.IsDeleted ? null : Excerpt(original.Content), original.IsDeleted);
    }

    private static IReadOnlyList<CommunityMentionDto> OptimisticMentions(
        string content, IReadOnlyList<CommunityMentionInput>? mentions) =>
        mentions?.Where(value => value.Start >= 0 && value.Length > 0 && value.Start + value.Length <= content.Length)
            .Select(value => new CommunityMentionDto(value.Kind, value.TargetId, value.Start, value.Length,
                content.Substring(value.Start, value.Length))).ToArray() ?? [];

    private void MarkChannelFailed(Guid communityId, Guid channelId, Guid clientMessageId, Exception exception)
    {
        if (CommunityId != communityId || ChannelId != channelId) return;
        var failure = DeliveryFailure(exception);
        lock (_messageSync)
        {
            var index = _messages.FindIndex(value => value.ClientMessageId == clientMessageId &&
                value.DeliveryState != MessageDeliveryState.Confirmed);
            if (index < 0) return;
            _messages[index] = _messages[index] with
            {
                DeliveryState = MessageDeliveryState.Failed,
                DeliveryError = failure.Message,
                CanRetry = failure.CanRetry
            };
        }
        NotifyChanged();
    }

    private void MarkDirectFailed(Guid conversationId, Guid clientMessageId, Exception exception)
    {
        if (DirectConversationId != conversationId) return;
        var failure = DeliveryFailure(exception);
        lock (_messageSync)
        {
            var index = _directMessages.FindIndex(value => value.ClientMessageId == clientMessageId &&
                value.DeliveryState != MessageDeliveryState.Confirmed);
            if (index < 0) return;
            _directMessages[index] = _directMessages[index] with
            {
                DeliveryState = MessageDeliveryState.Failed,
                DeliveryError = failure.Message,
                CanRetry = failure.CanRetry
            };
        }
        NotifyChanged();
    }

    private static DeliveryFailureInfo DeliveryFailure(Exception exception)
    {
        var detail = exception.GetBaseException().Message;
        if (detail.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not a member", StringComparison.OrdinalIgnoreCase))
            return new("No permission to send", false);
        if (detail.Contains("channel not found", StringComparison.OrdinalIgnoreCase))
            return new("Channel no longer exists", false);
        if (detail.Contains("conversation not found", StringComparison.OrdinalIgnoreCase))
            return new("Conversation no longer exists", false);
        if (detail.Contains("authenticated", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            return new("Login required", false);
        if (detail.Contains("replied to", StringComparison.OrdinalIgnoreCase))
            return new("Reply target is no longer available", false);
        return new("Failed to send", true);
    }

    private void ApplyChannelPage(MessageHistoryPage<ChannelMessageDto> page, bool replace)
    {
        var local = replace
            ? _messages.Where(value => value.DeliveryState != MessageDeliveryState.Confirmed).ToArray()
            : [];
        if (replace) _messages.Clear();
        foreach (var message in page.Messages) Upsert(message, notify: false);
        foreach (var message in local)
            if (_messages.All(value => value.ClientMessageId != message.ClientMessageId)) Upsert(message, notify: false);
        _channelOlderCursor = page.OlderCursor;
        _channelHasOlder = page.HasOlder;
        if (replace)
        {
            WindowMode = page.IsAroundWindow ? MessageWindowMode.SearchTarget : MessageWindowMode.Latest;
            WindowRevision++;
        }
    }

    private void ApplyDirectPage(MessageHistoryPage<DirectMessageDto> page, bool replace)
    {
        var local = replace
            ? _directMessages.Where(value => value.DeliveryState != MessageDeliveryState.Confirmed).ToArray()
            : [];
        if (replace) _directMessages.Clear();
        foreach (var message in page.Messages) UpsertDirect(message, notify: false);
        foreach (var message in local)
            if (_directMessages.All(value => value.ClientMessageId != message.ClientMessageId)) UpsertDirect(message, notify: false);
        _directOlderCursor = page.OlderCursor;
        _directHasOlder = page.HasOlder;
        if (replace)
        {
            WindowMode = page.IsAroundWindow ? MessageWindowMode.SearchTarget : MessageWindowMode.Latest;
            WindowRevision++;
        }
    }

    private void CancelHistoryRequests()
    {
        _historyCancellation.Cancel();
        _historyCancellation.Dispose();
        _historyCancellation = new();
    }

    private void ClearChannelState()
    {
        CommunityId = null;
        ChannelId = null;
        _channelReady = false;
        _messages.Clear();
        _channelOlderCursor = null;
        _channelHasOlder = false;
    }

    private void ClearDirectState()
    {
        DirectConversationId = null;
        _directReady = false;
        _directMessages.Clear();
        _directOlderCursor = null;
        _directHasOlder = false;
    }

    private void ReceiveCreated(ChannelMessageDto message)
    {
        if (message.CommunityId != CommunityId || message.ChannelId != ChannelId) return;
        if (WindowMode == MessageWindowMode.SearchTarget) return;
        Upsert(message);
    }

    private void ReceiveUpdated(ChannelMessageDto message)
    {
        if (message.CommunityId != CommunityId || message.ChannelId != ChannelId ||
            _messages.All(value => value.Id != message.Id)) return;
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

    private void ReceiveDirectCreated(DirectMessageDto message)
    {
        if (message.ConversationId == DirectConversationId && WindowMode != MessageWindowMode.SearchTarget) UpsertDirect(message);
        if (message.ConversationId == DirectConversationId && message.Author.AccountId != nodeSession.Account?.Id)
            _ = MarkDirectReadSafelyAsync(message.ConversationId);
        _ = RefreshDirectListSafelyAsync();
    }

    private void ReceiveDirectUpdated(DirectMessageDto message)
    {
        if (message.ConversationId != DirectConversationId || _directMessages.All(value => value.Id != message.Id)) return;
        UpsertDirect(message);
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

    private async Task ApplyCommunityActivitySafelyAsync(CommunityChannelActivityEvent activity)
    {
        try { await nodeSession.ApplyCommunityChannelActivityAsync(activity); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not apply Community channel activity for {CommunityId}/{ChannelId}.",
                activity.CommunityId, activity.ChannelId);
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
            if (index < 0 && message.ClientMessageId is { } clientMessageId)
                index = _messages.FindIndex(value => value.ClientMessageId == clientMessageId &&
                    value.Author.AccountId == message.Author.AccountId);
            var authoritative = message.DeliveryState == MessageDeliveryState.Confirmed
                ? message with { DeliveryError = null, CanRetry = false }
                : message;
            if (index < 0) _messages.Add(authoritative); else _messages[index] = authoritative;
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
            SortMessages(_messages);
        }
        if (notify) NotifyChanged();
    }

    private void UpsertDirect(DirectMessageDto message, bool notify = true)
    {
        lock (_messageSync)
        {
            var index = _directMessages.FindIndex(value => value.Id == message.Id);
            if (index < 0 && message.ClientMessageId is { } clientMessageId)
                index = _directMessages.FindIndex(value => value.ClientMessageId == clientMessageId &&
                    value.Author.AccountId == message.Author.AccountId);
            var authoritative = message.DeliveryState == MessageDeliveryState.Confirmed
                ? message with { DeliveryError = null, CanRetry = false }
                : message;
            if (index < 0) _directMessages.Add(authoritative); else _directMessages[index] = authoritative;
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
            SortMessages(_directMessages);
        }
        if (notify) NotifyChanged();
    }

    private static void SortMessages(List<ChannelMessageDto> messages) => messages.Sort((left, right) =>
    {
        var order = left.CreatedAt.CompareTo(right.CreatedAt);
        return order != 0 ? order : left.Id.CompareTo(right.Id);
    });

    private static void SortMessages(List<DirectMessageDto> messages) => messages.Sort((left, right) =>
    {
        var order = left.CreatedAt.CompareTo(right.CreatedAt);
        return order != 0 ? order : left.Id.CompareTo(right.Id);
    });

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

    private sealed record DeliveryFailureInfo(string Message, bool CanRetry);
    private sealed record OutgoingOperation(Guid ClientMessageId, Task Completion);

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
        _historyCancellation.Cancel();
        _historyCancellation.Dispose();
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
