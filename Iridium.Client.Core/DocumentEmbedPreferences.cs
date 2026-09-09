namespace Iridium.Client.Core;

public readonly record struct DocumentEmbedPreferenceScope(string NodeAuthority, Guid AccountId)
{
    public string StorageKey =>
        $"{DocumentEmbedPreferencesService.StorageNamespace}:{Uri.EscapeDataString(NodeAuthority.Trim().TrimEnd('/').ToLowerInvariant())}:{AccountId:N}";
}

public interface IDocumentEmbedPreferenceStore
{
    Task<bool?> LoadAsync(DocumentEmbedPreferenceScope scope, CancellationToken cancellationToken = default);
    Task SaveAsync(DocumentEmbedPreferenceScope scope, bool autoLoad, CancellationToken cancellationToken = default);
}

public sealed class DocumentEmbedPreferencesService(IDocumentEmbedPreferenceStore store)
{
    public const string StorageNamespace = "iridium.documentEmbedAutoLoad.v1";
    private readonly Dictionary<DocumentEmbedPreferenceScope, bool?> _cache = [];
    private readonly Dictionary<DocumentEmbedPreferenceScope, Task<bool?>> _loads = [];
    public event Action<DocumentEmbedPreferenceScope, bool?>? Changed;

    public static bool EffectiveAutoLoad(bool? storedPreference, bool isMobileLayout) =>
        storedPreference ?? !isMobileLayout;

    public Task<bool?> GetStoredAsync(DocumentEmbedPreferenceScope scope,
        CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(scope, out var cached)) return Task.FromResult(cached);
        if (_loads.TryGetValue(scope, out var load)) return load;
        return _loads[scope] = LoadAsync(scope, cancellationToken);
    }

    public async Task SetAsync(DocumentEmbedPreferenceScope scope, bool autoLoad,
        CancellationToken cancellationToken = default)
    {
        await store.SaveAsync(scope, autoLoad, cancellationToken);
        _cache[scope] = autoLoad;
        _loads.Remove(scope);
        Changed?.Invoke(scope, autoLoad);
    }

    private async Task<bool?> LoadAsync(DocumentEmbedPreferenceScope scope, CancellationToken cancellationToken)
    {
        try
        {
            var value = await store.LoadAsync(scope, cancellationToken);
            if (!_cache.ContainsKey(scope)) _cache[scope] = value;
            return _cache[scope];
        }
        finally { _loads.Remove(scope); }
    }
}
