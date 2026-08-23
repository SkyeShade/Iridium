using Iridium.Client.Core;

namespace Iridium.Tests;

public sealed class EmojiPickerPreferencesTests
{
    [Fact]
    public async Task CategoryStatePersistsPerAccountWithStableKeys()
    {
        var account = Guid.NewGuid();
        var community = Guid.NewGuid();
        var store = new MemoryStore();
        var preferences = new EmojiPickerPreferencesService(store);

        await preferences.SetCategoryCollapsedAsync(account, "standard:smileys_emotion", true);
        await preferences.SetCategoryCollapsedAsync(account, $"server:{community:N}", true);
        await preferences.SetCategoryCollapsedAsync(account, "recent", false);

        var reloaded = await new EmojiPickerPreferencesService(store).GetAsync(account);
        Assert.True(reloaded.Categories["standard:smileys_emotion"]);
        Assert.True(reloaded.Categories[$"server:{community:N}"]);
        Assert.False(reloaded.Categories["recent"]);
        Assert.Empty((await new EmojiPickerPreferencesService(store).GetAsync(Guid.NewGuid())).Categories);
    }

    [Fact]
    public async Task UsageSortsByCountThenRecencyAndUsesStableCustomIds()
    {
        var account = Guid.NewGuid();
        var customId = Guid.NewGuid();
        var service = new EmojiPickerPreferencesService(new MemoryStore());
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var wave = EmojiPickerPreferencesService.StandardKey("1f44b");
        var heart = EmojiPickerPreferencesService.StandardKey("2764");
        var custom = EmojiPickerPreferencesService.CustomKey(customId);

        for (var index = 0; index < 5; index++) await service.RecordUsageAsync(account, wave, start.AddMinutes(index));
        for (var index = 0; index < 3; index++) await service.RecordUsageAsync(account, heart, start.AddMinutes(10 + index));
        for (var index = 0; index < 2; index++) await service.RecordUsageAsync(account, custom, start.AddMinutes(20 + index));

        var usage = (await service.GetAsync(account)).UsageHistory;
        Assert.Equal([wave, heart, custom], usage.Select(value => value.EmojiKey));
        Assert.Equal([5, 3, 2], usage.Select(value => value.UseCount));
        Assert.EndsWith(customId.ToString("N"), custom);
    }

    [Fact]
    public async Task EqualCountsUseMostRecentFirstAndHistoryIsCapped()
    {
        var account = Guid.NewGuid();
        var service = new EmojiPickerPreferencesService(new MemoryStore());
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        await service.RecordUsageAsync(account, "standard:a", start);
        await service.RecordUsageAsync(account, "standard:b", start.AddSeconds(1));
        for (var index = 0; index < EmojiPickerPreferencesService.MaximumUsageHistory; index++)
            await service.RecordUsageAsync(account, $"standard:extra-{index}", start.AddMinutes(index + 1));

        var usage = (await service.GetAsync(account)).UsageHistory;
        Assert.Equal(EmojiPickerPreferencesService.MaximumUsageHistory, usage.Count);
        Assert.Equal("standard:extra-199", usage[0].EmojiKey);
        Assert.DoesNotContain(usage, value => value.EmojiKey == "standard:a");
    }

    private sealed class MemoryStore : IEmojiPickerPreferenceStore
    {
        private readonly Dictionary<Guid, EmojiPickerPreferenceData> _values = [];
        public Task<EmojiPickerPreferenceData> LoadAsync(Guid accountId,
            CancellationToken cancellationToken = default) => Task.FromResult(_values.TryGetValue(accountId, out var value)
            ? Copy(value) : new());
        public Task SaveAsync(Guid accountId, EmojiPickerPreferenceData preferences,
            CancellationToken cancellationToken = default)
        {
            _values[accountId] = Copy(preferences);
            return Task.CompletedTask;
        }
        private static EmojiPickerPreferenceData Copy(EmojiPickerPreferenceData value) =>
            new(new(value.Categories, StringComparer.Ordinal), value.UsageHistory.Select(entry => entry with { }).ToArray());
    }
}
