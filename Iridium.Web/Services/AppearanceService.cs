using Microsoft.JSInterop;

namespace Iridium.Web.Services;

public sealed record AppearancePreferences(
    string AccentColor,
    string BaseBackgroundColor,
    string SurfaceColor);

public sealed class AppearanceService(IJSRuntime js)
{
    public AppearancePreferences? Current { get; private set; }

    public async Task<AppearancePreferences> InitializeAsync()
    {
        Current ??= await js.InvokeAsync<AppearancePreferences>("iridiumAppearance.load");
        return Current;
    }

    public async Task<AppearancePreferences> UpdateAsync(AppearancePreferences preferences)
    {
        Current = await js.InvokeAsync<AppearancePreferences>("iridiumAppearance.save", preferences);
        return Current;
    }

    public async Task<AppearancePreferences> ResetAsync()
    {
        Current = await js.InvokeAsync<AppearancePreferences>("iridiumAppearance.reset");
        return Current;
    }
}
