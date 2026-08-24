using Microsoft.JSInterop;

namespace Iridium.Web.Services;

public sealed class FaviconNotificationService(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private int? _lastCount;
    private long _revision;

    public async ValueTask SetMentionCountAsync(int count)
    {
        count = Math.Max(0, count);
        if (_lastCount == count) return;
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/faviconNotifications.js");
        var revision = ++_revision;
        await _module.InvokeVoidAsync("setMentionCount", count, revision);
        if (revision == _revision) _lastCount = count;
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}
