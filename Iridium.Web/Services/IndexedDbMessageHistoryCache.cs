using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.JSInterop;

namespace Iridium.Web.Services;

public sealed class IndexedDbMessageHistoryCache(IJSRuntime js) : IMessageHistoryCache, IAsyncDisposable
{
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private IJSObjectReference? _module;

    public async Task<MessageHistoryPage<ChannelMessageDto>?> GetRecentChannelAsync(
        MessageHistoryCacheScope scope, CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken)).InvokeAsync<MessageHistoryPage<ChannelMessageDto>?>(
            "getRecent", cancellationToken, scope);

    public async Task<MessageHistoryPage<DirectMessageDto>?> GetRecentDirectAsync(
        MessageHistoryCacheScope scope, CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken)).InvokeAsync<MessageHistoryPage<DirectMessageDto>?>(
            "getRecent", cancellationToken, scope);

    public async Task ReconcileRecentChannelAsync(MessageHistoryCacheScope scope,
        MessageHistoryPage<ChannelMessageDto> page, CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken)).InvokeVoidAsync("reconcileRecent", cancellationToken, scope, page);

    public async Task ReconcileRecentDirectAsync(MessageHistoryCacheScope scope,
        MessageHistoryPage<DirectMessageDto> page, CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken)).InvokeVoidAsync("reconcileRecent", cancellationToken, scope, page);

    public async Task UpsertChannelAsync(MessageHistoryCacheScope scope, IReadOnlyList<ChannelMessageDto> messages,
        CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken)).InvokeVoidAsync("upsertMessages", cancellationToken, scope, messages);

    public async Task UpsertDirectAsync(MessageHistoryCacheScope scope, IReadOnlyList<DirectMessageDto> messages,
        CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken)).InvokeVoidAsync("upsertMessages", cancellationToken, scope, messages);

    public async Task RemoveMessageAsync(MessageHistoryCacheScope scope, Guid messageId,
        CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken)).InvokeVoidAsync("removeMessage", cancellationToken, scope, messageId);

    public async Task ClearConversationAsync(MessageHistoryCacheScope scope, CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken)).InvokeVoidAsync("clearConversation", cancellationToken, scope);

    public async Task ClearCommunityAsync(string nodeKey, Guid accountId, Guid communityId,
        CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken)).InvokeVoidAsync("clearCommunity", cancellationToken,
            nodeKey, accountId, communityId);

    public async Task ClearAccountAsync(string nodeKey, Guid accountId, CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken)).InvokeVoidAsync("clearAccount", cancellationToken, nodeKey, accountId);

    public async Task ClearNodeAsync(string nodeKey, CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken)).InvokeVoidAsync("clearNode", cancellationToken, nodeKey);

    public async Task PruneAsync(CancellationToken cancellationToken = default) =>
        await (await ModuleAsync(cancellationToken)).InvokeVoidAsync("prune", cancellationToken);

    private async Task<IJSObjectReference> ModuleAsync(CancellationToken cancellationToken)
    {
        if (_module is not null) return _module;
        await _moduleGate.WaitAsync(cancellationToken);
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken,
                "./js/messageHistoryCache.js");
            return _module;
        }
        finally { _moduleGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync();
        _moduleGate.Dispose();
    }
}
