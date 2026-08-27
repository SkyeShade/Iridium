namespace Iridium.Client.Core;

public enum ComposerActionMode
{
    Attachment,
    Avatar
}

public readonly record struct ComposerActionModeScope(string NodeAuthority, Guid AccountId)
{
    public string StorageKey => StorageKeyFor(ComposerActionModePreferencesService.StorageNamespace);

    public string StorageKeyFor(string storageNamespace) =>
        $"{storageNamespace}:{Uri.EscapeDataString(NormalizedAuthority)}:{AccountId:N}";

    private string NormalizedAuthority => NodeAuthority.Trim().TrimEnd('/').ToLowerInvariant();
}

public interface IComposerActionModeStore
{
    Task<ComposerActionMode?> LoadAsync(ComposerActionModeScope scope,
        CancellationToken cancellationToken = default);
    Task SaveAsync(ComposerActionModeScope scope, ComposerActionMode mode,
        CancellationToken cancellationToken = default);
}

public sealed class ComposerActionModePreferencesService(IComposerActionModeStore store)
{
    public const string StorageNamespace = "iridium.composerActionMode.v1";

    public static ComposerActionMode EffectiveMode(ComposerActionMode savedMode, bool isDirectMessage) =>
        isDirectMessage ? ComposerActionMode.Attachment : savedMode;

    public async Task<ComposerActionMode> GetAsync(ComposerActionModeScope scope,
        CancellationToken cancellationToken = default)
    {
        var stored = await store.LoadAsync(scope, cancellationToken);
        return stored is { } mode && Enum.IsDefined(mode) ? mode : ComposerActionMode.Attachment;
    }

    public Task SetAsync(ComposerActionModeScope scope, ComposerActionMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return store.SaveAsync(scope, mode, cancellationToken);
    }
}

public sealed record ComposerAvatarUsageData
{
    public Dictionary<Guid, long> LastUsedAtUnixMilliseconds { get; init; } = [];
}

public interface IComposerAvatarUsageStore
{
    Task<ComposerAvatarUsageData> LoadAsync(ComposerActionModeScope scope,
        CancellationToken cancellationToken = default);
    Task SaveAsync(ComposerActionModeScope scope, ComposerAvatarUsageData usage,
        CancellationToken cancellationToken = default);
}

public sealed class ComposerAvatarUsageService(IComposerAvatarUsageStore store, TimeProvider timeProvider)
{
    public const string StorageNamespace = "iridium.composerAvatarUsage.v1";
    public static string StorageKey(ComposerActionModeScope scope) => scope.StorageKeyFor(StorageNamespace);

    public Task<ComposerAvatarUsageData> GetAsync(ComposerActionModeScope scope,
        CancellationToken cancellationToken = default) => store.LoadAsync(scope, cancellationToken);

    public async Task<ComposerAvatarUsageData> RecordAsync(ComposerActionModeScope scope, Guid presetId,
        CancellationToken cancellationToken = default)
    {
        var usage = await store.LoadAsync(scope, cancellationToken);
        var values = new Dictionary<Guid, long>(usage.LastUsedAtUnixMilliseconds)
        {
            [presetId] = timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };
        var updated = new ComposerAvatarUsageData { LastUsedAtUnixMilliseconds = values };
        await store.SaveAsync(scope, updated, cancellationToken);
        return updated;
    }

    public static IReadOnlyList<T> MostRecentlyUsedFirst<T>(IEnumerable<T> presets,
        ComposerAvatarUsageData usage, Func<T, Guid> id, Func<T, DateTimeOffset> updatedAt) => presets
        .OrderByDescending(value => usage.LastUsedAtUnixMilliseconds.ContainsKey(id(value)))
        .ThenByDescending(value => usage.LastUsedAtUnixMilliseconds.GetValueOrDefault(id(value)))
        .ThenByDescending(updatedAt)
        .ToArray();
}
