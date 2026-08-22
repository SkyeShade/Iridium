using Iridium.Client.Core;
using Microsoft.JSInterop;

namespace Iridium.Web.Services;

public sealed class BrowserClientStorage(IJSRuntime js) : ISavedNodeStore, INodeTokenStore, ISavedAccountStore,
    IActiveAccountSelectionStore, ICategoryCollapseStore, ILastCommunityChannelStore,
    IVoiceParticipantPreferenceStore, IAsyncDisposable
{
    private IJSObjectReference? _module;

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

    private async Task<IJSObjectReference> ModuleAsync(CancellationToken cancellationToken)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./js/savedServers.js");
        return _module;
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync();
    }
}
