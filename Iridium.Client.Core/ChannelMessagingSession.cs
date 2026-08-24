using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Iridium.Client.Core;

public sealed class ChannelMessagingSession(
    NodeSession nodeSession,
    RealtimeConnectionService realtime,
    ILogger<ChannelMessagingSession> logger,
    IMessageHistoryCache? historyCache = null) : IAsyncDisposable
{
    private readonly IMessageHistoryCache _historyCache = historyCache ?? NullMessageHistoryCache.Instance;
    private readonly List<ChannelMessageDto> _messages = [];
    private readonly List<DirectMessageDto> _directMessages = [];
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _messageSync = new();
    private readonly Dictionary<Guid, Func<CancellationToken, Task<IReadOnlyList<AttachmentDto>>>> _channelAttachmentUploads = [];
    private readonly Dictionary<Guid, Func<CancellationToken, Task<IReadOnlyList<AttachmentDto>>>> _directAttachmentUploads = [];
    private HubConnection? _connection;
    private readonly List<IDisposable> _handlerRegistrations = [];
    private Uri? _connectedNode;
    private Guid? _connectedAccountId;
    private bool _channelReady;
    private bool _directReady;
    private string? _channelOlderCursor;
    private string? _directOlderCursor;
    private bool _channelHasOlder;
    private bool _directHasOlder;
    private bool _loadingOlder;
    private long _conversationLoadGeneration;
    private long _hotAccessRevision;
    private MessageHistoryCacheScope? _channelStateScope;
    private MessageHistoryCacheScope? _directStateScope;
    private readonly Dictionary<MessageHistoryCacheScope, ChannelHotState> _channelHotStates = [];
    private readonly Dictionary<MessageHistoryCacheScope, DirectHotState> _directHotStates = [];
    private const int HotConversationLimit = 8;
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

    public IReadOnlyList<ChannelMessageDto> MessagesFor(Guid communityId, Guid channelId) =>
        CommunityId == communityId && ChannelId == channelId &&
        _channelStateScope is { Kind: MessageHistoryConversationKind.Channel, ConversationId: var stateChannelId } &&
        stateChannelId == channelId ? _messages : [];

    public IReadOnlyList<DirectMessageDto> DirectMessagesFor(Guid conversationId) =>
        DirectConversationId == conversationId &&
        _directStateScope is { Kind: MessageHistoryConversationKind.Direct, ConversationId: var stateConversationId } &&
        stateConversationId == conversationId ? _directMessages : [];

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
        var generation = Interlocked.Increment(ref _conversationLoadGeneration);
        CancelHistoryRequests();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsCurrentLoad(generation)) return;
            if (_channelReady && CommunityId == communityId && ChannelId == channelId) return;

            SaveActiveHotStates();
            var previousCommunityId = CommunityId;
            var previousChannelId = ChannelId;
            var previousConversationId = DirectConversationId;
            ClearDirectState();

            var scope = ChannelScope(channelId);
            var hot = AttachChannelState(communityId, channelId, scope);
            NotifyChanged();
            logger.LogDebug("Conversation switch to channel {ChannelId}; generation={Generation}, hot={Hot}.",
                channelId, generation, hot);

            await LeaveChannelAsync(previousCommunityId, previousChannelId, cancellationToken);
            await LeaveDirectConversationAsync(previousConversationId, cancellationToken);
            if (!IsCurrentChannelLoad(generation, scope)) return;

            MessageHistoryPage<ChannelMessageDto>? cached = null;
            if (!hot)
            {
                cached = await GetCachedChannelSafelyAsync(scope, cancellationToken);
                if (!IsCurrentChannelLoad(generation, scope))
                {
                    logger.LogDebug("Discarded stale channel cache result for {ChannelId}; generation={Generation}.",
                        channelId, generation);
                    return;
                }
                if (cached is not null)
                {
                    ApplyChannelPage(cached, replace: true);
                    SaveActiveHotStates();
                    NotifyChanged();
                }
            }

            await EnsureConnectionAsync(cancellationToken);
            if (!IsCurrentChannelLoad(generation, scope)) return;
            await _connection!.InvokeAsync(ChatHubContract.JoinChannel, communityId, channelId, cancellationToken);
            if (!IsCurrentChannelLoad(generation, scope)) return;
            var history = await nodeSession.AuthorizedClient.GetChannelMessagePageAsync(
                communityId, channelId, cancellationToken: cancellationToken);
            if (!IsCurrentChannelLoad(generation, scope))
            {
                logger.LogDebug("Discarded stale server channel history for {ChannelId}; generation={Generation}.",
                    channelId, generation);
                return;
            }
            ReconcileChannelRecent(history, incrementRevision: !hot && cached is null);
            SaveActiveHotStates();
            CacheSafely(_historyCache.ReconcileRecentChannelAsync(scope, history), "reconcile channel history");
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
            if (IsAccessFailure(exception))
            {
                var scope = ChannelScope(channelId);
                _channelHotStates.Remove(scope);
                ClearChannelState();
                await ClearConversationCacheSafelyAsync(scope);
                NotifyChanged();
            }
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
        var generation = Interlocked.Increment(ref _conversationLoadGeneration);
        CancelHistoryRequests();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsCurrentLoad(generation)) return;
            if (_directReady && DirectConversationId == conversationId) return;
            SaveActiveHotStates();
            var previousCommunityId = CommunityId;
            var previousChannelId = ChannelId;
            var previousConversationId = DirectConversationId;
            ClearChannelState();
            var scope = DirectScope(conversationId);
            var hot = AttachDirectState(conversationId, scope);
            NotifyChanged();
            logger.LogDebug("Conversation switch to Direct Message {ConversationId}; generation={Generation}, hot={Hot}.",
                conversationId, generation, hot);
            await LeaveChannelAsync(previousCommunityId, previousChannelId, cancellationToken);
            await LeaveDirectConversationAsync(previousConversationId, cancellationToken);
            if (!IsCurrentDirectLoad(generation, scope)) return;
            MessageHistoryPage<DirectMessageDto>? cached = null;
            if (!hot)
            {
                cached = await GetCachedDirectSafelyAsync(scope, cancellationToken);
                if (!IsCurrentDirectLoad(generation, scope))
                {
                    logger.LogDebug("Discarded stale Direct Message cache result for {ConversationId}; generation={Generation}.",
                        conversationId, generation);
                    return;
                }
                if (cached is not null)
                {
                    ApplyDirectPage(cached, replace: true);
                    SaveActiveHotStates();
                    NotifyChanged();
                }
            }
            await EnsureConnectionAsync(cancellationToken);
            if (!IsCurrentDirectLoad(generation, scope)) return;
            await _connection!.InvokeAsync(DirectMessageHubContract.JoinConversation, conversationId, cancellationToken);
            if (!IsCurrentDirectLoad(generation, scope)) return;
            var history = await nodeSession.AuthorizedClient.GetDirectMessagePageAsync(conversationId, cancellationToken: cancellationToken);
            if (!IsCurrentDirectLoad(generation, scope))
            {
                logger.LogDebug("Discarded stale server Direct Message history for {ConversationId}; generation={Generation}.",
                    conversationId, generation);
                return;
            }
            ReconcileDirectRecent(history, incrementRevision: !hot && cached is null);
            SaveActiveHotStates();
            CacheSafely(_historyCache.ReconcileRecentDirectAsync(scope, history), "reconcile Direct Message history");
            await nodeSession.MarkDirectConversationReadAsync(conversationId, cancellationToken);
            _directReady = true;
            NotifyChanged();
        }
        catch (Exception exception)
        {
            if (IsAccessFailure(exception))
            {
                var scope = DirectScope(conversationId);
                _directHotStates.Remove(scope);
                ClearDirectState();
                await ClearConversationCacheSafelyAsync(scope);
                NotifyChanged();
            }
            throw;
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
                CacheSafely(_historyCache.ReconcileRecentChannelAsync(ChannelScope(channelId), page),
                    "cache refreshed channel history");
            }
            else if (DirectConversationId is { } conversationId)
            {
                var page = await nodeSession.AuthorizedClient.GetDirectMessagePageAsync(
                    conversationId, cancellationToken: cancellationToken);
                ApplyDirectPage(page, replace: true);
                CacheSafely(_historyCache.ReconcileRecentDirectAsync(DirectScope(conversationId), page),
                    "cache refreshed Direct Message history");
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
            CacheSafely(_historyCache.UpsertChannelAsync(ChannelScope(channelId), page.Messages),
                "cache channel search window");
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
            CacheSafely(_historyCache.UpsertDirectAsync(DirectScope(conversationId), page.Messages),
                "cache Direct Message search window");
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
            CacheSafely(_historyCache.UpsertDirectAsync(DirectScope(pending.ConversationId), [result]),
                "cache confirmed Direct Message");
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
            CacheSafely(_historyCache.UpsertDirectAsync(DirectScope(conversationId), [result]),
                "cache confirmed Direct Message");
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
        if (_directMessages.FirstOrDefault(value => value.Id == messageId) is { Kind: not MessageKind.User })
            throw new InvalidOperationException("System messages cannot be edited.");
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var conversationId = RequireDirectConversation();
            var result = await RequireConnection().InvokeAsync<DirectMessageDto>(
                DirectMessageHubContract.EditMessage, conversationId, messageId,
                new EditDirectMessageRequest(content), cancellationToken);
            UpsertDirect(result);
            CacheSafely(_historyCache.UpsertDirectAsync(DirectScope(conversationId), [result]),
                "cache edited Direct Message");
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task DeleteDirectAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_directMessages.FirstOrDefault(value => value.Id == messageId) is { Kind: not MessageKind.User })
            throw new InvalidOperationException("System messages cannot be deleted.");
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
                CacheSafely(_historyCache.UpsertChannelAsync(ChannelScope(pending.ChannelId), [result]),
                    "cache confirmed channel message");
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
                CacheSafely(_historyCache.UpsertChannelAsync(ChannelScope(channelId), [result]),
                    "cache edited channel message");
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
        Interlocked.Increment(ref _conversationLoadGeneration);
        CancelHistoryRequests();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            SaveActiveHotStates();
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
        Interlocked.Increment(ref _conversationLoadGeneration);
        CancelHistoryRequests();
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            SaveActiveHotStates();
            await LeaveActiveChannelAsync(cancellationToken);
            await LeaveActiveDirectConversationAsync(cancellationToken);
            ClearChannelState();
            ClearDirectState();
            if (_connection is not null)
            {
                logger.LogDebug("Disconnecting realtime client from {NodeAddress}.", _connectedNode);
                DisposeHandlerRegistrations();
            }
            _connection = null;
            await realtime.DisconnectAsync("account context reset", cancellationToken);
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
        var connection = await realtime.EnsureConnectedAsync("ChannelMessagingSession requested realtime", cancellationToken);
        if (ReferenceEquals(_connection, connection)) return;
        DisposeHandlerRegistrations();
        var contextChanged = _connectedNode is not null &&
            (!SameNode(_connectedNode, client.NodeAddress) || _connectedAccountId != accountId);
        if (contextChanged)
        {
            ClearChannelState();
            ClearDirectState();
        }
        _connectedNode = client.NodeAddress;
        _connectedAccountId = accountId;
        _connection = connection;

        _handlerRegistrations.Add(connection.On<ChannelMessageDto>(ChatHubContract.MessageCreated,
            message => ReceiveSafely(ChatHubContract.MessageCreated, () => ReceiveCreated(message))));
        _handlerRegistrations.Add(connection.On<ChannelMessageDto>(ChatHubContract.MessageUpdated,
            message => ReceiveSafely(ChatHubContract.MessageUpdated, () => ReceiveUpdated(message))));
        _handlerRegistrations.Add(connection.On<ChannelMessageDeletedEvent>(ChatHubContract.MessageDeleted,
            deleted => ReceiveSafely(ChatHubContract.MessageDeleted, () => ReceiveDeleted(deleted))));
        _handlerRegistrations.Add(connection.On<DirectMessageDto>(DirectMessageHubContract.MessageCreated,
            message => ReceiveSafely(DirectMessageHubContract.MessageCreated, () => ReceiveDirectCreated(message))));
        _handlerRegistrations.Add(connection.On<DirectMessageDto>(DirectMessageHubContract.MessageUpdated,
            message => ReceiveSafely(DirectMessageHubContract.MessageUpdated, () => ReceiveDirectUpdated(message))));
        _handlerRegistrations.Add(connection.On<DirectMessageDeletedEvent>(DirectMessageHubContract.MessageDeleted,
            deleted => ReceiveSafely(DirectMessageHubContract.MessageDeleted, () => ReceiveDirectDeleted(deleted))));
        _handlerRegistrations.Add(connection.On<FriendshipChangedEvent>(FriendshipHubContract.RequestReceived,
            _event => _ = RefreshFriendsSafelyAsync(FriendshipHubContract.RequestReceived)));
        _handlerRegistrations.Add(connection.On<FriendshipChangedEvent>(FriendshipHubContract.RequestAccepted,
            _event => _ = RefreshFriendsSafelyAsync(FriendshipHubContract.RequestAccepted)));
        _handlerRegistrations.Add(connection.On<FriendshipChangedEvent>(FriendshipHubContract.RequestDeclined,
            _event => _ = RefreshFriendsSafelyAsync(FriendshipHubContract.RequestDeclined)));
        _handlerRegistrations.Add(connection.On<FriendshipChangedEvent>(FriendshipHubContract.FriendshipRemoved,
            _event => _ = RefreshFriendsSafelyAsync(FriendshipHubContract.FriendshipRemoved)));
        _handlerRegistrations.Add(connection.On<PresenceChangedEvent>(PresenceHubContract.PresenceChanged,
            change => ReceiveSafely(PresenceHubContract.PresenceChanged, () => nodeSession.ApplyPresence(change))));
        _handlerRegistrations.Add(connection.On<CommunityStateChangedEvent>(CommunityHubContract.StateChanged,
            change => _ = ApplyCommunityChangeSafelyAsync(change)));
        _handlerRegistrations.Add(connection.On<CommunityAccessRevokedEvent>(CommunityHubContract.AccessRevoked,
            change => ReceiveSafely(CommunityHubContract.AccessRevoked, () =>
            {
                if (CommunityId == change.CommunityId) ClearChannelState();
                foreach (var scope in _channelHotStates.Where(pair =>
                             pair.Value.Messages.Any(message => message.CommunityId == change.CommunityId))
                         .Select(pair => pair.Key).ToArray())
                    _channelHotStates.Remove(scope);
                if (_connectedNode is not null && _connectedAccountId is { } connectedAccountId)
                    CacheSafely(_historyCache.ClearCommunityAsync(
                        MessageHistoryCacheScope.NormalizeNode(_connectedNode), connectedAccountId, change.CommunityId),
                        "clear revoked Community history");
                nodeSession.ApplyCommunityAccessRevoked(change);
                NotifyChanged();
            })));
        _handlerRegistrations.Add(connection.On<CommunityMentionReceivedEvent>(CommunityMentionHubContract.Received,
            mention => ReceiveSafely(CommunityMentionHubContract.Received, () => nodeSession.ApplyCommunityMention(mention))));
        _handlerRegistrations.Add(connection.On<CommunityChannelActivityEvent>(CommunityHubContract.ChannelActivity,
            activity => _ = ApplyCommunityActivitySafelyAsync(activity)));
        _handlerRegistrations.Add(connection.On<ProfileUpdatedEvent>(ProfileHubContract.Updated,
            change => ReceiveSafely(ProfileHubContract.Updated, () => nodeSession.ApplyProfileUpdated(change))));
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
                nodeSession.ApplyRealtimeReconnected();
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

        logger.LogInformation("Shared realtime connection to {NodeAddress} is active for messaging.", client.NodeAddress);
    }

    private void DisposeHandlerRegistrations()
    {
        foreach (var registration in _handlerRegistrations) registration.Dispose();
        _handlerRegistrations.Clear();
    }

    private async Task LeaveActiveChannelAsync(CancellationToken cancellationToken)
        => await LeaveChannelAsync(CommunityId, ChannelId, cancellationToken);

    private async Task LeaveChannelAsync(Guid? communityId, Guid? channelId, CancellationToken cancellationToken)
    {
        if (!IsConnected || communityId is not { } activeCommunityId || channelId is not { } activeChannelId) return;
        try
        {
            await _connection!.InvokeAsync(ChatHubContract.LeaveChannel, activeCommunityId, activeChannelId, cancellationToken);
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
        => await LeaveDirectConversationAsync(DirectConversationId, cancellationToken);

    private async Task LeaveDirectConversationAsync(Guid? conversationId, CancellationToken cancellationToken)
    {
        if (!IsConnected || conversationId is not { } activeConversationId) return;
        try { await _connection!.InvokeAsync(DirectMessageHubContract.LeaveConversation, activeConversationId, cancellationToken); }
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
            CacheSafely(_historyCache.UpsertChannelAsync(ChannelScope(channelId), page.Messages),
                "cache older channel history");
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
            CacheSafely(_historyCache.UpsertDirectAsync(DirectScope(conversationId), page.Messages),
                "cache older Direct Message history");
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
        foreach (var message in page.Messages.Where(value => !value.IsDeleted)) Upsert(message, notify: false);
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

    private bool AttachChannelState(Guid communityId, Guid channelId, MessageHistoryCacheScope scope)
    {
        CommunityId = communityId;
        ChannelId = channelId;
        _channelStateScope = scope;
        _channelReady = false;
        _messages.Clear();
        _channelOlderCursor = null;
        _channelHasOlder = false;
        WindowMode = MessageWindowMode.Latest;
        WindowRevision++;
        if (!_channelHotStates.TryGetValue(scope, out var hot)) return false;
        _messages.AddRange(hot.Messages.Where(value => !value.IsDeleted));
        _channelOlderCursor = hot.OlderCursor;
        _channelHasOlder = hot.HasOlder;
        hot.LastAccess = ++_hotAccessRevision;
        return true;
    }

    private bool AttachDirectState(Guid conversationId, MessageHistoryCacheScope scope)
    {
        DirectConversationId = conversationId;
        _directStateScope = scope;
        _directReady = false;
        _directMessages.Clear();
        _directOlderCursor = null;
        _directHasOlder = false;
        WindowMode = MessageWindowMode.Latest;
        WindowRevision++;
        if (!_directHotStates.TryGetValue(scope, out var hot)) return false;
        _directMessages.AddRange(hot.Messages.Where(value => !value.IsDeleted));
        _directOlderCursor = hot.OlderCursor;
        _directHasOlder = hot.HasOlder;
        hot.LastAccess = ++_hotAccessRevision;
        return true;
    }

    private void SaveActiveHotStates()
    {
        if (_channelStateScope is { } channelScope && ChannelId == channelScope.ConversationId)
            _channelHotStates[channelScope] = new(_messages.Where(value => !value.IsDeleted).ToArray(),
                _channelOlderCursor, _channelHasOlder, ++_hotAccessRevision);
        if (_directStateScope is { } directScope && DirectConversationId == directScope.ConversationId)
            _directHotStates[directScope] = new(_directMessages.Where(value => !value.IsDeleted).ToArray(),
                _directOlderCursor, _directHasOlder, ++_hotAccessRevision);
        PruneHotStates(_channelHotStates);
        PruneHotStates(_directHotStates);
    }

    private static void PruneHotStates<T>(Dictionary<MessageHistoryCacheScope, T> states) where T : HotState
    {
        while (states.Count > HotConversationLimit)
        {
            var oldest = states.MinBy(pair => pair.Value.LastAccess).Key;
            states.Remove(oldest);
        }
    }

    private bool IsCurrentLoad(long generation) => Volatile.Read(ref _conversationLoadGeneration) == generation;
    private bool IsCurrentChannelLoad(long generation, MessageHistoryCacheScope scope) =>
        IsCurrentLoad(generation) && _channelStateScope == scope && ChannelId == scope.ConversationId;
    private bool IsCurrentDirectLoad(long generation, MessageHistoryCacheScope scope) =>
        IsCurrentLoad(generation) && _directStateScope == scope && DirectConversationId == scope.ConversationId;

    private void UpdateHotChannel(MessageHistoryCacheScope scope, ChannelMessageDto message)
    {
        if (!_channelHotStates.TryGetValue(scope, out var hot) || _channelStateScope == scope) return;
        hot.Messages = MessageHistoryReconciliation.Channel(hot.Messages, [message]);
        hot.LastAccess = ++_hotAccessRevision;
    }

    private void UpdateHotDirect(MessageHistoryCacheScope scope, DirectMessageDto message)
    {
        if (!_directHotStates.TryGetValue(scope, out var hot) || _directStateScope == scope) return;
        hot.Messages = MessageHistoryReconciliation.Direct(hot.Messages, [message]);
        hot.LastAccess = ++_hotAccessRevision;
    }

    private void DeleteFromHotChannel(MessageHistoryCacheScope scope, Guid messageId)
    {
        if (!_channelHotStates.TryGetValue(scope, out var hot) || _channelStateScope == scope) return;
        var messages = hot.Messages.ToList();
        MessageTimeline.ApplyDeletion(messages, messageId);
        hot.Messages = messages;
        hot.LastAccess = ++_hotAccessRevision;
    }

    private void DeleteFromHotDirect(MessageHistoryCacheScope scope, Guid messageId)
    {
        if (!_directHotStates.TryGetValue(scope, out var hot) || _directStateScope == scope) return;
        var messages = hot.Messages.ToList();
        MessageTimeline.ApplyDeletion(messages, messageId);
        hot.Messages = messages;
        hot.LastAccess = ++_hotAccessRevision;
    }

    private void ApplyDirectPage(MessageHistoryPage<DirectMessageDto> page, bool replace)
    {
        var local = replace
            ? _directMessages.Where(value => value.DeliveryState != MessageDeliveryState.Confirmed).ToArray()
            : [];
        if (replace) _directMessages.Clear();
        foreach (var message in page.Messages.Where(value => !value.IsDeleted)) UpsertDirect(message, notify: false);
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

    private void ReconcileChannelRecent(MessageHistoryPage<ChannelMessageDto> page, bool incrementRevision)
    {
        var serverMessages = page.Messages.Where(value => !value.IsDeleted).ToArray();
        var oldest = serverMessages.Length == 0 ? (DateTimeOffset?)null : serverMessages.Min(value => value.CreatedAt);
        lock (_messageSync)
        {
            _messages.RemoveAll(value => value.DeliveryState == MessageDeliveryState.Confirmed &&
                (oldest is null || value.CreatedAt >= oldest) && serverMessages.All(server => server.Id != value.Id));
        }
        foreach (var message in serverMessages) Upsert(message, notify: false);
        _channelOlderCursor = page.OlderCursor;
        _channelHasOlder = page.HasOlder;
        WindowMode = MessageWindowMode.Latest;
        if (incrementRevision) WindowRevision++;
    }

    private void ReconcileDirectRecent(MessageHistoryPage<DirectMessageDto> page, bool incrementRevision)
    {
        var serverMessages = page.Messages.Where(value => !value.IsDeleted).ToArray();
        var oldest = serverMessages.Length == 0 ? (DateTimeOffset?)null : serverMessages.Min(value => value.CreatedAt);
        lock (_messageSync)
        {
            _directMessages.RemoveAll(value => value.DeliveryState == MessageDeliveryState.Confirmed &&
                (oldest is null || value.CreatedAt >= oldest) && serverMessages.All(server => server.Id != value.Id));
        }
        foreach (var message in serverMessages) UpsertDirect(message, notify: false);
        _directOlderCursor = page.OlderCursor;
        _directHasOlder = page.HasOlder;
        WindowMode = MessageWindowMode.Latest;
        if (incrementRevision) WindowRevision++;
    }

    private MessageHistoryCacheScope ChannelScope(Guid channelId)
    {
        var node = nodeSession.AuthorizedClient.NodeAddress;
        var accountId = nodeSession.Account?.Id
            ?? throw new InvalidOperationException("An authenticated account is required for message cache scope.");
        return MessageHistoryCacheScope.Channel(node, accountId, channelId);
    }

    private MessageHistoryCacheScope DirectScope(Guid conversationId)
    {
        var node = nodeSession.AuthorizedClient.NodeAddress;
        var accountId = nodeSession.Account?.Id
            ?? throw new InvalidOperationException("An authenticated account is required for message cache scope.");
        return MessageHistoryCacheScope.Direct(node, accountId, conversationId);
    }

    private async Task<MessageHistoryPage<ChannelMessageDto>?> GetCachedChannelSafelyAsync(
        MessageHistoryCacheScope scope, CancellationToken cancellationToken)
    {
        try { return await _historyCache.GetRecentChannelAsync(scope, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read cached channel history for {ConversationKey}.", scope.ConversationKey);
            return null;
        }
    }

    private async Task<MessageHistoryPage<DirectMessageDto>?> GetCachedDirectSafelyAsync(
        MessageHistoryCacheScope scope, CancellationToken cancellationToken)
    {
        try { return await _historyCache.GetRecentDirectAsync(scope, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read cached Direct Message history for {ConversationKey}.", scope.ConversationKey);
            return null;
        }
    }

    private async Task ClearConversationCacheSafelyAsync(MessageHistoryCacheScope scope)
    {
        try { await _historyCache.ClearConversationAsync(scope); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not invalidate inaccessible cached history for {ConversationKey}.",
                scope.ConversationKey);
        }
    }

    private void CacheSafely(Task operation, string action) => _ = CacheSafelyAsync(operation, action);

    private async Task CacheSafelyAsync(Task operation, string action)
    {
        try { await operation; }
        catch (Exception exception) { logger.LogWarning(exception, "Could not {CacheAction}.", action); }
    }

    private static bool IsAccessFailure(Exception exception) => exception is NodeApiException api &&
        api.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or
            System.Net.HttpStatusCode.NotFound || exception is HubException &&
        (exception.Message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
         exception.Message.Contains("not a member", StringComparison.OrdinalIgnoreCase) ||
         exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
         exception.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase));

    private static bool SameNode(Uri left, Uri right) => string.Equals(
        MessageHistoryCacheScope.NormalizeNode(left), MessageHistoryCacheScope.NormalizeNode(right),
        StringComparison.Ordinal);

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
        _channelStateScope = null;
        _channelReady = false;
        _messages.Clear();
        _channelOlderCursor = null;
        _channelHasOlder = false;
    }

    private void ClearDirectState()
    {
        DirectConversationId = null;
        _directStateScope = null;
        _directReady = false;
        _directMessages.Clear();
        _directOlderCursor = null;
        _directHasOlder = false;
    }

    private void ReceiveCreated(ChannelMessageDto message)
    {
        UpdateHotChannel(ChannelScope(message.ChannelId), message);
        CacheSafely(_historyCache.UpsertChannelAsync(ChannelScope(message.ChannelId), [message]),
            "cache realtime channel message");
        if (message.CommunityId != CommunityId || message.ChannelId != ChannelId) return;
        if (WindowMode == MessageWindowMode.SearchTarget) return;
        Upsert(message);
    }

    private void ReceiveUpdated(ChannelMessageDto message)
    {
        UpdateHotChannel(ChannelScope(message.ChannelId), message);
        CacheSafely(_historyCache.UpsertChannelAsync(ChannelScope(message.ChannelId), [message]),
            "cache realtime channel edit");
        if (message.CommunityId != CommunityId || message.ChannelId != ChannelId ||
            _messages.All(value => value.Id != message.Id)) return;
        Upsert(message);
    }

    private void ReceiveDeleted(ChannelMessageDeletedEvent deleted)
    {
        DeleteFromHotChannel(ChannelScope(deleted.ChannelId), deleted.MessageId);
        CacheSafely(_historyCache.RemoveMessageAsync(ChannelScope(deleted.ChannelId), deleted.MessageId),
            "remove realtime-deleted channel message from cache");
        if (deleted.CommunityId != CommunityId || deleted.ChannelId != ChannelId) return;
        MessageTimeline.ApplyDeletion(_messages, deleted.MessageId);
        NotifyChanged();
    }

    private void ReceiveDirectCreated(DirectMessageDto message)
    {
        UpdateHotDirect(DirectScope(message.ConversationId), message);
        CacheSafely(_historyCache.UpsertDirectAsync(DirectScope(message.ConversationId), [message]),
            "cache realtime Direct Message");
        if (message.ConversationId == DirectConversationId && WindowMode != MessageWindowMode.SearchTarget) UpsertDirect(message);
        if (message.ConversationId == DirectConversationId && message.Author.AccountId != nodeSession.Account?.Id)
            _ = MarkDirectReadSafelyAsync(message.ConversationId);
        _ = RefreshDirectListSafelyAsync();
    }

    private void ReceiveDirectUpdated(DirectMessageDto message)
    {
        UpdateHotDirect(DirectScope(message.ConversationId), message);
        CacheSafely(_historyCache.UpsertDirectAsync(DirectScope(message.ConversationId), [message]),
            "cache realtime Direct Message edit");
        if (message.ConversationId != DirectConversationId || _directMessages.All(value => value.Id != message.Id)) return;
        UpsertDirect(message);
    }

    private void ReceiveDirectDeleted(DirectMessageDeletedEvent deleted)
    {
        DeleteFromHotDirect(DirectScope(deleted.ConversationId), deleted.MessageId);
        CacheSafely(_historyCache.RemoveMessageAsync(DirectScope(deleted.ConversationId), deleted.MessageId),
            "remove realtime-deleted Direct Message from cache");
        if (deleted.ConversationId != DirectConversationId) return;
        MessageTimeline.ApplyDeletion(_directMessages, deleted.MessageId);
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
            if (authoritative.IsDeleted)
                MessageTimeline.ApplyDeletion(_messages, authoritative.Id);
            else if (index < 0) _messages.Add(authoritative);
            else _messages[index] = authoritative;
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
            if (authoritative.IsDeleted)
                MessageTimeline.ApplyDeletion(_directMessages, authoritative.Id);
            else if (index < 0) _directMessages.Add(authoritative);
            else _directMessages[index] = authoritative;
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
    private abstract class HotState(long lastAccess)
    {
        public long LastAccess { get; set; } = lastAccess;
    }
    private sealed class ChannelHotState(IReadOnlyList<ChannelMessageDto> messages, string? olderCursor,
        bool hasOlder, long lastAccess) : HotState(lastAccess)
    {
        public IReadOnlyList<ChannelMessageDto> Messages { get; set; } = messages;
        public string? OlderCursor { get; } = olderCursor;
        public bool HasOlder { get; } = hasOlder;
    }
    private sealed class DirectHotState(IReadOnlyList<DirectMessageDto> messages, string? olderCursor,
        bool hasOlder, long lastAccess) : HotState(lastAccess)
    {
        public IReadOnlyList<DirectMessageDto> Messages { get; set; } = messages;
        public string? OlderCursor { get; } = olderCursor;
        public bool HasOlder { get; } = hasOlder;
    }

    private static string Excerpt(string content)
        => content;

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
            DisposeHandlerRegistrations();
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
