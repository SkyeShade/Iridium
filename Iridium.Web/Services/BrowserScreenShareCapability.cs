using Microsoft.JSInterop;

namespace Iridium.Web.Services;

public sealed class BrowserScreenShareCapability(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private Task<bool>? _detection;

    public bool IsKnown { get; private set; }
    public bool IsSupported { get; private set; }

    public Task<bool> DetectAsync() => _detection ??= DetectCoreAsync();

    private async Task<bool> DetectCoreAsync()
    {
        try
        {
            _module = await js.InvokeAsync<IJSObjectReference>("import", "./js/screenShareCapability.js");
            IsSupported = await _module.InvokeAsync<bool>("isDisplayCaptureSupported");
        }
        catch (JSException) { IsSupported = false; }
        finally { IsKnown = true; }
        return IsSupported;
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync();
    }
}
