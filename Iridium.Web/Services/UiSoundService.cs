using Iridium.Client.Core;
using Microsoft.JSInterop;

namespace Iridium.Web.Services;

public sealed class UiSoundService(ActiveVoiceSessionCoordinator voice, IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private Guid? _incomingCallId;
    private Guid? _activeSessionId;
    private bool _initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        _module = await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./js/uiSounds.js");
        await _module.InvokeVoidAsync("initialize", cancellationToken);
        voice.Changed += VoiceChanged;
        _initialized = true;
        await SynchronizeAsync();
    }

    private void VoiceChanged() => _ = SynchronizeAsync();

    private async Task SynchronizeAsync()
    {
        var module = _module;
        if (module is null) return;
        var incoming = voice.IncomingCall?.CallId;
        if (incoming != _incomingCallId)
        {
            _incomingCallId = incoming;
            if (incoming.HasValue) await SafeInvokeAsync(module, "playIncomingCallLoop");
            else await SafeInvokeAsync(module, "stopIncomingCallLoop");
        }

        var active = voice.Current?.SessionId;
        if (active != _activeSessionId)
        {
            if (_activeSessionId.HasValue) await SafeInvokeAsync(module, "playVoiceLeave");
            _activeSessionId = active;
            if (active.HasValue) await SafeInvokeAsync(module, "playVoiceJoin");
        }
    }

    private static async Task SafeInvokeAsync(IJSObjectReference module, string method)
    {
        try { await module.InvokeVoidAsync(method); }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }

    public async ValueTask DisposeAsync()
    {
        voice.Changed -= VoiceChanged;
        if (_module is not null)
        {
            await SafeInvokeAsync(_module, "dispose");
            await _module.DisposeAsync();
        }
        _module = null;
    }
}
