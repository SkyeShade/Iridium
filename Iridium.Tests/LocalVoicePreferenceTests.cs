using Iridium.Client.Core;

namespace Iridium.Tests;

public sealed class LocalVoicePreferenceTests
{
    [Fact]
    public async Task PreferencesAreScopedByNodeAndAccountAndSurviveReload()
    {
        var store = new MemoryStore();
        var firstAccount = Guid.NewGuid();
        var secondAccount = Guid.NewGuid();
        var service = new LocalVoicePreferenceService(store);

        await service.SetScopeAsync("https://node-a.example", firstAccount);
        await service.SetPreferredMutedAsync(true);
        await service.SetPreferredDeafenedAsync(true);

        var reloaded = new LocalVoicePreferenceService(store);
        await reloaded.SetScopeAsync("https://node-a.example", firstAccount);
        Assert.True(reloaded.PreferredMuted);
        Assert.True(reloaded.PreferredDeafened);
        Assert.True(reloaded.EffectiveMuted);

        await reloaded.SetScopeAsync("https://node-a.example", secondAccount);
        Assert.False(reloaded.PreferredMuted);
        Assert.False(reloaded.PreferredDeafened);

        await reloaded.SetScopeAsync("https://node-b.example", firstAccount);
        Assert.False(reloaded.PreferredMuted);
        Assert.False(reloaded.PreferredDeafened);
    }

    [Fact]
    public async Task DeafenForcesOnlyEffectiveMuteAndPreservesPreferredMute()
    {
        var service = new LocalVoicePreferenceService(new MemoryStore());
        await service.SetScopeAsync("node", Guid.NewGuid());

        await service.SetPreferredDeafenedAsync(true);
        Assert.False(service.PreferredMuted);
        Assert.True(service.EffectiveMuted);
        await service.SetPreferredDeafenedAsync(false);
        Assert.False(service.EffectiveMuted);

        await service.SetPreferredMutedAsync(true);
        await service.SetPreferredDeafenedAsync(true);
        await service.SetPreferredDeafenedAsync(false);
        Assert.True(service.PreferredMuted);
        Assert.True(service.EffectiveMuted);

        await service.SetPreferredDeafenedAsync(true);
        await service.SetPreferredMutedAsync(false);
        Assert.True(service.EffectiveMuted);
        await service.SetPreferredDeafenedAsync(false);
        Assert.False(service.EffectiveMuted);
    }

    private sealed class MemoryStore : ILocalVoicePreferenceStore
    {
        private readonly Dictionary<LocalVoicePreferenceScope, LocalVoicePreference> _values = [];
        public Task<LocalVoicePreference?> LoadAsync(LocalVoicePreferenceScope scope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.GetValueOrDefault(scope));
        public Task SaveAsync(LocalVoicePreferenceScope scope, LocalVoicePreference preference,
            CancellationToken cancellationToken = default)
        {
            _values[scope] = preference;
            return Task.CompletedTask;
        }
    }
}
