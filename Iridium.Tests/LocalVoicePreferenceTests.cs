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

    [Fact]
    public async Task InputSensitivityAndDevicePersistWithinAccountScope()
    {
        var store = new MemoryStore();
        var accountId = Guid.NewGuid();
        var service = new LocalVoicePreferenceService(store);
        await service.SetScopeAsync("https://node.example", accountId);

        await service.SetAutoInputSensitivityAsync(false);
        await service.SetManualInputSensitivityThresholdAsync(0.73);
        await service.SetInputDeviceAsync("microphone-2");

        var reloaded = new LocalVoicePreferenceService(store);
        await reloaded.SetScopeAsync("https://node.example", accountId);
        Assert.False(reloaded.AutoInputSensitivity);
        Assert.Equal(0.73, reloaded.ManualInputSensitivityThreshold, 3);
        Assert.Equal("microphone-2", reloaded.InputDeviceId);

        await reloaded.SetManualInputSensitivityThresholdAsync(4);
        Assert.Equal(1, reloaded.ManualInputSensitivityThreshold);
        await reloaded.SetManualInputSensitivityThresholdAsync(-2);
        Assert.Equal(0, reloaded.ManualInputSensitivityThreshold);
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
