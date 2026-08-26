using Microsoft.JSInterop;

namespace Iridium.Web.Services;

public sealed record AppearancePreferences(
    string AccentColor,
    string BaseBackgroundColor,
    string SurfaceColor,
    bool ShowMessageAvatarPresence = false);

public sealed class AppearanceService(IJSRuntime js)
{
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private long _operationVersion;
    public AppearancePreferences? Current { get; private set; }
    public event Action? Changed;

    public async Task<AppearancePreferences> InitializeAsync()
    {
        if (Current is null)
        {
            Current = await js.InvokeAsync<AppearancePreferences>("iridiumAppearance.load");
            Changed?.Invoke();
        }
        return Current;
    }

    public async Task<AppearancePreferences> UpdateAsync(AppearancePreferences preferences)
    {
        var version = Interlocked.Increment(ref _operationVersion);
        await _persistenceGate.WaitAsync();
        try
        {
            if (version != Volatile.Read(ref _operationVersion)) return Current ?? preferences;
            var saved = await js.InvokeAsync<AppearancePreferences>("iridiumAppearance.save", preferences);
            if (version != Volatile.Read(ref _operationVersion)) return Current ?? saved;
            Current = saved;
            Changed?.Invoke();
            return saved;
        }
        finally { _persistenceGate.Release(); }
    }

    public async Task<AppearancePreferences> ResetAsync()
    {
        var version = Interlocked.Increment(ref _operationVersion);
        await _persistenceGate.WaitAsync();
        try
        {
            var defaults = await js.InvokeAsync<AppearancePreferences>("iridiumAppearance.reset");
            if (version != Volatile.Read(ref _operationVersion)) return Current ?? defaults;
            Current = defaults;
            Changed?.Invoke();
            return defaults;
        }
        finally { _persistenceGate.Release(); }
    }
}
