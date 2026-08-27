using Iridium.Client.Core;
using Iridium.Protocol;
using System.Text.Json;

namespace Iridium.Tests;

public sealed class VoiceParticipantPreferencesTests
{
    [Fact]
    public async Task ScreenShareVolumeIsIndependentFromVoiceAndPersists()
    {
        var store = new MemoryStore();
        var accountId = Guid.NewGuid();
        var service = new VoiceParticipantPreferencesService(store);
        await service.SetVolumeAsync(accountId, 175);
        await service.SetScreenShareVolumeAsync(accountId, 35);

        var reloaded = await new VoiceParticipantPreferencesService(store).GetAsync(accountId);

        Assert.Equal(175, reloaded.VolumePercent);
        Assert.Equal(35, reloaded.ScreenShareVolumePercent);
        Assert.False(reloaded.LocallyMuted);
    }

    [Fact]
    public async Task EachViewerPersistsAnIndependentStreamOwnerVolume()
    {
        var alice = Guid.NewGuid();
        var viewerA = new VoiceParticipantPreferencesService(new MemoryStore());
        var viewerB = new VoiceParticipantPreferencesService(new MemoryStore());
        await viewerA.SetScreenShareVolumeAsync(alice, 40);
        await viewerB.SetScreenShareVolumeAsync(alice, 130);

        Assert.Equal(40, (await viewerA.GetAsync(alice)).ScreenShareVolumePercent);
        Assert.Equal(130, (await viewerB.GetAsync(alice)).ScreenShareVolumePercent);
    }

    [Fact]
    public async Task ScreenShareVolumeAllowsZeroAndClampsToSharedMaximum()
    {
        var service = new VoiceParticipantPreferencesService(new MemoryStore());
        var accountId = Guid.NewGuid();
        await service.SetScreenShareVolumeAsync(accountId, -1);
        Assert.Equal(0, (await service.GetAsync(accountId)).ScreenShareVolumePercent);
        await service.SetScreenShareVolumeAsync(accountId, 500);
        Assert.Equal(300, (await service.GetAsync(accountId)).ScreenShareVolumePercent);
    }

    [Fact]
    public void LegacyPreferenceWithoutScreenVolumeUsesCanonicalDefault()
    {
        var accountId = Guid.NewGuid();
        var json = $$"""{"remoteAccountId":"{{accountId}}","volumePercent":140,"locallyMuted":true}""";

        var preference = JsonSerializer.Deserialize<VoiceParticipantPreference>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(preference);
        Assert.Equal(100, preference.ScreenShareVolumePercent);
        Assert.Equal(140, preference.VolumePercent);
    }

    [Fact]
    public async Task UnknownParticipantDefaultsToOneHundredPercentAndAudible()
    {
        var service = new VoiceParticipantPreferencesService(new MemoryStore());
        var preference = await service.GetAsync(Guid.NewGuid());
        Assert.Equal(100, preference.VolumePercent);
        Assert.False(preference.LocallyMuted);
        Assert.Equal(1, preference.EffectiveGain(false));
    }

    [Fact]
    public async Task VolumeClampsAndPersistedPreferenceReloads()
    {
        var accountId = Guid.NewGuid();
        var store = new MemoryStore();
        var first = new VoiceParticipantPreferencesService(store);
        await first.SetVolumeAsync(accountId, 1);
        Assert.Equal(10, (await first.GetAsync(accountId)).VolumePercent);
        await first.SetVolumeAsync(accountId, 999);
        await first.SetLocallyMutedAsync(accountId, true);

        var reloaded = await new VoiceParticipantPreferencesService(store).GetAsync(accountId);
        Assert.Equal(300, reloaded.VolumePercent);
        Assert.True(reloaded.LocallyMuted);
    }

    [Fact]
    public async Task LocalMuteAndDeafenSuppressGainWithoutChangingRemoteSpeakingOrSavedVolume()
    {
        var accountId = Guid.NewGuid();
        var service = new VoiceParticipantPreferencesService(new MemoryStore());
        await service.SetVolumeAsync(accountId, 175);
        var audible = await service.GetAsync(accountId);
        Assert.Equal(1.75, audible.EffectiveGain(false));
        Assert.Equal(0, audible.EffectiveGain(true));
        Assert.Equal(1.75, audible.EffectiveGain(false));

        await service.SetLocallyMutedAsync(accountId, true);
        var locallyMuted = await service.GetAsync(accountId);
        var serverParticipant = new VoiceParticipantDto(accountId, "connection", "Skye", PublicPresence.Online,
            DateTimeOffset.UtcNow, false, false, true, CommunityVoiceMediaStatus.Connected);
        Assert.Equal(0, locallyMuted.EffectiveGain(false));
        Assert.Equal(175, locallyMuted.VolumePercent);
        Assert.True(serverParticipant.Speaking);
        Assert.False(serverParticipant.Muted);
    }

    [Fact]
    public async Task PreferenceChangeIsLocalAndRaisesOnlyLocalChangeNotification()
    {
        var accountId = Guid.NewGuid();
        var service = new VoiceParticipantPreferencesService(new MemoryStore());
        VoiceParticipantPreference? changed = null;
        service.Changed += value => changed = value;
        await service.SetVolumeAsync(accountId, 140);
        Assert.Equal(140, changed?.VolumePercent);
    }

    private sealed class MemoryStore : IVoiceParticipantPreferenceStore
    {
        private IReadOnlyList<VoiceParticipantPreference> _values = [];
        public Task<IReadOnlyList<VoiceParticipantPreference>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_values);
        public Task SaveAsync(IReadOnlyList<VoiceParticipantPreference> preferences,
            CancellationToken cancellationToken = default)
        {
            _values = preferences.Select(value => value with { }).ToArray();
            return Task.CompletedTask;
        }
    }
}
