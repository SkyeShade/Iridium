namespace Iridium.Client.Core;

public sealed record EmojiUsageEntry(string EmojiKey, int UseCount, DateTimeOffset LastUsedAt);

public sealed record EmojiPickerPreferenceData
{
    public EmojiPickerPreferenceData() { }
    public EmojiPickerPreferenceData(Dictionary<string, bool> categories, IReadOnlyList<EmojiUsageEntry> usageHistory)
    {
        Categories = categories;
        UsageHistory = usageHistory;
    }
    public Dictionary<string, bool> Categories { get; init; } = new(StringComparer.Ordinal);
    public IReadOnlyList<EmojiUsageEntry> UsageHistory { get; init; } = [];
}

public interface IEmojiPickerPreferenceStore
{
    Task<EmojiPickerPreferenceData> LoadAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task SaveAsync(Guid accountId, EmojiPickerPreferenceData preferences,
        CancellationToken cancellationToken = default);
}

public sealed class EmojiPickerPreferencesService(IEmojiPickerPreferenceStore store)
{
    public const int MaximumUsageHistory = 200;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, EmojiPickerPreferenceData> _preferences = [];
    private readonly HashSet<Guid> _loadedAccounts = [];
    public event Action<Guid>? Changed;

    public async Task<EmojiPickerPreferenceData> GetAsync(Guid accountId,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(accountId, cancellationToken);
        return Copy(_preferences[accountId]);
    }

    public async Task SetCategoryCollapsedAsync(Guid accountId, string categoryKey, bool collapsed,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryKey);
        await EnsureLoadedAsync(accountId, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = _preferences[accountId];
            var categories = new Dictionary<string, bool>(current.Categories, StringComparer.Ordinal)
            {
                [categoryKey] = collapsed
            };
            _preferences[accountId] = new(categories, current.UsageHistory);
            await store.SaveAsync(accountId, Copy(_preferences[accountId]), cancellationToken);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(accountId);
    }

    public async Task RecordUsageAsync(Guid accountId, string emojiKey, DateTimeOffset? usedAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emojiKey);
        await EnsureLoadedAsync(accountId, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = _preferences[accountId];
            var history = current.UsageHistory.ToDictionary(value => value.EmojiKey, StringComparer.Ordinal);
            var now = usedAt ?? DateTimeOffset.UtcNow;
            var previous = history.GetValueOrDefault(emojiKey);
            history[emojiKey] = new(emojiKey, (previous?.UseCount ?? 0) + 1, now);
            var pruned = history.Values.OrderByDescending(value => value.UseCount)
                .ThenByDescending(value => value.LastUsedAt).Take(MaximumUsageHistory).ToArray();
            _preferences[accountId] = new(new(current.Categories, StringComparer.Ordinal), pruned);
            await store.SaveAsync(accountId, Copy(_preferences[accountId]), cancellationToken);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(accountId);
    }

    public static string StandardKey(string artworkKey) => $"standard:{artworkKey}";
    public static string CustomKey(Guid emojiId) => $"custom:{emojiId:N}";

    private async Task EnsureLoadedAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (_loadedAccounts.Contains(accountId)) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loadedAccounts.Contains(accountId)) return;
            var loaded = await store.LoadAsync(accountId, cancellationToken);
            _preferences[accountId] = Normalize(loaded);
            _loadedAccounts.Add(accountId);
        }
        finally { _gate.Release(); }
    }

    private static EmojiPickerPreferenceData Normalize(EmojiPickerPreferenceData value)
    {
        var categories = value.Categories ?? new(StringComparer.Ordinal);
        var history = value.UsageHistory ?? [];
        var usage = history.Where(entry => !string.IsNullOrWhiteSpace(entry.EmojiKey) && entry.UseCount > 0)
            .GroupBy(entry => entry.EmojiKey, StringComparer.Ordinal).Select(group => group
                .OrderByDescending(entry => entry.UseCount).ThenByDescending(entry => entry.LastUsedAt).First())
            .OrderByDescending(entry => entry.UseCount).ThenByDescending(entry => entry.LastUsedAt)
            .Take(MaximumUsageHistory).ToArray();
        return new(new(categories, StringComparer.Ordinal), usage);
    }

    private static EmojiPickerPreferenceData Copy(EmojiPickerPreferenceData value) =>
        new(new(value.Categories, StringComparer.Ordinal), value.UsageHistory.Select(entry => entry with { }).ToArray());
}
