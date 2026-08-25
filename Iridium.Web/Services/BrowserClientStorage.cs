using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.JSInterop;

namespace Iridium.Web.Services;

public sealed class BrowserClientStorage(IJSRuntime js) : ISavedNodeStore, INodeTokenStore, ISavedAccountStore,
    IActiveAccountSelectionStore, ICategoryCollapseStore, ILastCommunityChannelStore,
    IVoiceParticipantPreferenceStore, IEmojiPickerPreferenceStore, IMessageDraftStore,
    ICommunityForumPostCache, IAsyncDisposable
{
    private const string MessageDraftNamespace = "iridium.messageDrafts.v1";
    private const int MaximumMessageDrafts = 500;
    private IJSObjectReference? _module;
    private readonly SemaphoreSlim _messageDraftGate = new(1, 1);

    public async Task<IReadOnlyList<SavedNode>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var module = await ModuleAsync(cancellationToken);
        var nodes = await module.InvokeAsync<SavedNode[]>("load", cancellationToken, "iridium.savedNodes") ?? [];
        if (nodes.Length != 0) return nodes;

        var legacy = await module.InvokeAsync<SavedNode[]>("load", cancellationToken, "iridium.savedServers") ?? [];
        if (legacy.Length != 0)
            await module.InvokeVoidAsync("save", cancellationToken, "iridium.savedNodes", legacy);
        return legacy;
    }

    public async Task SaveAsync(IReadOnlyList<SavedNode> nodes, CancellationToken cancellationToken = default)
    {
        var module = await ModuleAsync(cancellationToken);
        await module.InvokeVoidAsync("save", cancellationToken, "iridium.savedNodes", nodes);
    }

    public async Task<string?> LoadAsync(string nodeAddress, CancellationToken cancellationToken = default)
    {
        var module = await ModuleAsync(cancellationToken);
        return await module.InvokeAsync<string?>("loadToken", cancellationToken, nodeAddress);
    }

    public async Task SaveAsync(string nodeAddress, string token, CancellationToken cancellationToken = default)
    {
        var module = await ModuleAsync(cancellationToken);
        await module.InvokeVoidAsync("saveToken", cancellationToken, nodeAddress, token);
    }

    public async Task RemoveAsync(string nodeAddress, CancellationToken cancellationToken = default)
    {
        var module = await ModuleAsync(cancellationToken);
        await module.InvokeVoidAsync("removeToken", cancellationToken, nodeAddress);
    }

    async Task<SavedAccountStoreData> ISavedAccountStore.LoadAsync(CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        return await module.InvokeAsync<SavedAccountStoreData?>(
                   "loadValue", cancellationToken, "iridium.savedAccounts")
               ?? SavedAccountStoreData.Empty;
    }

    async Task ISavedAccountStore.SaveAsync(SavedAccountStoreData data, CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        await module.InvokeVoidAsync("save", cancellationToken, "iridium.savedAccounts", data);
    }

    async Task<SavedAccountKey?> IActiveAccountSelectionStore.LoadAsync(CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        return await module.InvokeAsync<SavedAccountKey?>(
            "loadSessionValue", cancellationToken, "iridium.activeAccount");
    }

    async Task IActiveAccountSelectionStore.SaveAsync(SavedAccountKey? key, CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        await module.InvokeVoidAsync("saveSessionValue", cancellationToken, "iridium.activeAccount", key);
    }

    public async Task<IReadOnlySet<Guid>> LoadAsync(Guid accountId, Guid communityId, CancellationToken cancellationToken = default)
    {
        var module = await ModuleAsync(cancellationToken);
        var values = await module.InvokeAsync<Guid[]>("load", cancellationToken, $"iridium.collapsed:{accountId}:{communityId}") ?? [];
        return values.ToHashSet();
    }

    public async Task SaveAsync(Guid accountId, Guid communityId, IReadOnlySet<Guid> collapsed, CancellationToken cancellationToken = default)
    {
        var module = await ModuleAsync(cancellationToken);
        await module.InvokeVoidAsync("save", cancellationToken, $"iridium.collapsed:{accountId}:{communityId}", collapsed);
    }

    async Task<Guid?> ILastCommunityChannelStore.LoadAsync(Guid accountId, Guid communityId, CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        var value = await module.InvokeAsync<string?>(
            "loadGuidValue", cancellationToken, $"iridium.last-channel:{accountId}:{communityId}");
        return Guid.TryParse(value, out var channelId) ? channelId : null;
    }

    async Task ILastCommunityChannelStore.SaveAsync(Guid accountId, Guid communityId, Guid channelId, CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        await module.InvokeVoidAsync(
            "saveGuidValue", cancellationToken, $"iridium.last-channel:{accountId}:{communityId}", channelId.ToString("D"));
    }

    async Task<IReadOnlyList<VoiceParticipantPreference>> IVoiceParticipantPreferenceStore.LoadAsync(
        CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        return await module.InvokeAsync<VoiceParticipantPreference[]>("load", cancellationToken,
            "iridium.voiceParticipantPreferences") ?? [];
    }

    async Task IVoiceParticipantPreferenceStore.SaveAsync(IReadOnlyList<VoiceParticipantPreference> preferences,
        CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        await module.InvokeVoidAsync("save", cancellationToken, "iridium.voiceParticipantPreferences", preferences);
    }

    async Task<EmojiPickerPreferenceData> IEmojiPickerPreferenceStore.LoadAsync(Guid accountId,
        CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        return await module.InvokeAsync<EmojiPickerPreferenceData?>("loadValue", cancellationToken,
                   $"iridium.emoji-picker:{accountId:N}") ?? new();
    }

    async Task IEmojiPickerPreferenceStore.SaveAsync(Guid accountId, EmojiPickerPreferenceData preferences,
        CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        await module.InvokeVoidAsync("save", cancellationToken, $"iridium.emoji-picker:{accountId:N}", preferences);
    }

    async Task<string?> IMessageDraftStore.LoadAsync(MessageDraftScope scope, CancellationToken cancellationToken)
    {
        await _messageDraftGate.WaitAsync(cancellationToken);
        try
        {
            var drafts = await LoadMessageDraftsAsync(cancellationToken);
            return drafts.TryGetValue(scope.StorageKey, out var draft) && !string.IsNullOrWhiteSpace(draft.Content)
                ? draft.Content
                : null;
        }
        finally { _messageDraftGate.Release(); }
    }

    async Task IMessageDraftStore.SaveAsync(MessageDraftScope scope, string content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            await ((IMessageDraftStore)this).RemoveAsync(scope, cancellationToken);
            return;
        }

        await _messageDraftGate.WaitAsync(cancellationToken);
        try
        {
            var drafts = await LoadMessageDraftsAsync(cancellationToken);
            drafts[scope.StorageKey] = new(content, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            foreach (var stale in drafts.OrderByDescending(value => value.Value.UpdatedAtUnixMilliseconds)
                         .Skip(MaximumMessageDrafts).Select(value => value.Key).ToArray()) drafts.Remove(stale);
            await SaveMessageDraftsAsync(drafts, cancellationToken);
        }
        finally { _messageDraftGate.Release(); }
    }

    async Task IMessageDraftStore.RemoveAsync(MessageDraftScope scope, CancellationToken cancellationToken)
    {
        await _messageDraftGate.WaitAsync(cancellationToken);
        try
        {
            var drafts = await LoadMessageDraftsAsync(cancellationToken);
            if (drafts.Remove(scope.StorageKey)) await SaveMessageDraftsAsync(drafts, cancellationToken);
        }
        finally { _messageDraftGate.Release(); }
    }

    private async Task<Dictionary<string, MessageDraftEntry>> LoadMessageDraftsAsync(CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        return await module.InvokeAsync<Dictionary<string, MessageDraftEntry>?>(
                   "loadValue", cancellationToken, MessageDraftNamespace)
               ?? new(StringComparer.Ordinal);
    }

    private async Task SaveMessageDraftsAsync(Dictionary<string, MessageDraftEntry> drafts,
        CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        await module.InvokeVoidAsync("save", cancellationToken, MessageDraftNamespace, drafts);
    }

    async Task<CommunityForumPostPageDto?> ICommunityForumPostCache.LoadAsync(
        CommunityForumPostCacheScope scope, CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        return await module.InvokeAsync<CommunityForumPostPageDto?>(
            "loadValue", cancellationToken, scope.StorageKey);
    }

    async Task ICommunityForumPostCache.SaveAsync(CommunityForumPostCacheScope scope,
        CommunityForumPostPageDto page, CancellationToken cancellationToken)
    {
        var module = await ModuleAsync(cancellationToken);
        await module.InvokeVoidAsync("save", cancellationToken, scope.StorageKey, page);
    }

    private async Task<IJSObjectReference> ModuleAsync(CancellationToken cancellationToken)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./js/savedServers.js");
        return _module;
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync();
        _messageDraftGate.Dispose();
    }
}
