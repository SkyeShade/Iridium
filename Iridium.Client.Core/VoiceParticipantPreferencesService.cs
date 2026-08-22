namespace Iridium.Client.Core;

public sealed record VoiceParticipantPreference(Guid RemoteAccountId, int VolumePercent = 100,
    bool LocallyMuted = false)
{
    public const int MinimumVolumePercent = 10;
    public const int MaximumVolumePercent = 300;
    public VoiceParticipantPreference Normalize() => this with
    {
        VolumePercent = Math.Clamp(VolumePercent, MinimumVolumePercent, MaximumVolumePercent)
    };
    public double EffectiveGain(bool globallyDeafened) =>
        globallyDeafened || LocallyMuted ? 0 : VolumePercent / 100d;
}

public interface IVoiceParticipantPreferenceStore
{
    Task<IReadOnlyList<VoiceParticipantPreference>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<VoiceParticipantPreference> preferences,
        CancellationToken cancellationToken = default);
}

public sealed class VoiceParticipantPreferencesService(IVoiceParticipantPreferenceStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, VoiceParticipantPreference> _preferences = [];
    private bool _loaded;
    public event Action<VoiceParticipantPreference>? Changed;

    public async Task<VoiceParticipantPreference> GetAsync(Guid remoteAccountId,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _preferences.GetValueOrDefault(remoteAccountId) ?? new(remoteAccountId);
    }

    public Task SetVolumeAsync(Guid remoteAccountId, int volumePercent,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(remoteAccountId, value => value with { VolumePercent = volumePercent }, cancellationToken);

    public Task SetLocallyMutedAsync(Guid remoteAccountId, bool muted,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(remoteAccountId, value => value with { LocallyMuted = muted }, cancellationToken);

    private async Task UpdateAsync(Guid remoteAccountId,
        Func<VoiceParticipantPreference, VoiceParticipantPreference> update, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        VoiceParticipantPreference preference;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            preference = update(_preferences.GetValueOrDefault(remoteAccountId) ?? new(remoteAccountId)).Normalize();
            _preferences[remoteAccountId] = preference;
            await store.SaveAsync(_preferences.Values.OrderBy(value => value.RemoteAccountId).ToArray(), cancellationToken);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(preference);
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded) return;
            foreach (var value in await store.LoadAsync(cancellationToken))
                _preferences[value.RemoteAccountId] = value.Normalize();
            _loaded = true;
        }
        finally { _gate.Release(); }
    }
}
