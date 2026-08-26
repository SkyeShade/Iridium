namespace Iridium.Client.Core;

public sealed record LocalVoicePreference(bool PreferredMuted = false, bool PreferredDeafened = false)
{
    public bool EffectiveMuted => PreferredMuted || PreferredDeafened;
}

public readonly record struct LocalVoicePreferenceScope(string NodeAuthority, Guid AccountId);

public interface ILocalVoicePreferenceStore
{
    Task<LocalVoicePreference?> LoadAsync(LocalVoicePreferenceScope scope,
        CancellationToken cancellationToken = default);
    Task SaveAsync(LocalVoicePreferenceScope scope, LocalVoicePreference preference,
        CancellationToken cancellationToken = default);
}

public sealed class LocalVoicePreferenceService(ILocalVoicePreferenceStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LocalVoicePreferenceScope? _scope;

    public event Action? Changed;
    public LocalVoicePreference Current { get; private set; } = new();
    public bool PreferredMuted => Current.PreferredMuted;
    public bool PreferredDeafened => Current.PreferredDeafened;
    public bool EffectiveMuted => Current.EffectiveMuted;

    public async Task SetScopeAsync(string nodeAuthority, Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var scope = new LocalVoicePreferenceScope(nodeAuthority.Trim(), accountId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_scope == scope) return;
            var preference = await store.LoadAsync(scope, cancellationToken) ?? new();
            _scope = scope;
            Current = preference;
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public Task SetPreferredMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
        UpdateAsync(Current with { PreferredMuted = muted }, cancellationToken);

    public Task SetPreferredDeafenedAsync(bool deafened, CancellationToken cancellationToken = default) =>
        UpdateAsync(Current with { PreferredDeafened = deafened }, cancellationToken);

    private async Task UpdateAsync(LocalVoicePreference preference, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Current = preference;
            if (_scope is { } scope) await store.SaveAsync(scope, preference, cancellationToken);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }
}

internal sealed class TransientLocalVoicePreferenceStore : ILocalVoicePreferenceStore
{
    public Task<LocalVoicePreference?> LoadAsync(LocalVoicePreferenceScope scope,
        CancellationToken cancellationToken = default) => Task.FromResult<LocalVoicePreference?>(null);
    public Task SaveAsync(LocalVoicePreferenceScope scope, LocalVoicePreference preference,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
