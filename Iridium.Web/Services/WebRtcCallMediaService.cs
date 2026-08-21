using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace Iridium.Web.Services;

public sealed class WebRtcCallMediaService(IJSRuntime js, IWebAssemblyHostEnvironment environment) : ICallMediaService
{
    private const string ModuleDirectoryAndName = "./js/voiceCall";
    private IJSObjectReference? _module;
    private DotNetObjectReference<WebRtcCallMediaService>? _callback;
    private string? _sessionId;

    public event Func<WebRtcIceCandidate, Task>? IceCandidateGenerated;
    public event Func<CallConnectionState, Task>? ConnectionStateChanged;
    public event Func<bool, Task>? SpeakingChanged;
    public event Func<string, Task>? Error;

    public async Task InitializeAsync(CallMediaConfigurationDto configuration, CallMediaSessionContext context,
        CancellationToken cancellationToken = default)
    {
        await CleanupAsync(cancellationToken);
        // Compose at runtime so the WebAssembly static-asset fingerprint rewriter does not
        // turn this dynamic import into a precompressed development asset URL. Chromium can
        // fetch that URL but rejects it as an ES module in the dev server.
        var modulePath = string.Concat(ModuleDirectoryAndName, ".js?module=1");
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, modulePath);
        _callback = DotNetObjectReference.Create(this);
        _sessionId = await _module.InvokeAsync<string>("initialize", cancellationToken,
            _callback, configuration.IceServers, environment.IsDevelopment(), context.CallId, context.LocalAccountId,
            context.Role, context.PeerGeneration, context.NegotiationId);
    }

    public Task<WebRtcSessionDescription> CreateOfferAsync(Guid negotiationId, CancellationToken cancellationToken = default) =>
        InvokeAsync<WebRtcSessionDescription>("createOffer", cancellationToken, negotiationId);

    public Task<WebRtcSessionDescription> AcceptOfferAsync(Guid negotiationId, WebRtcSessionDescription offer,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<WebRtcSessionDescription>("acceptOffer", cancellationToken, negotiationId, offer);

    public Task<RemoteAnswerApplyResult> ApplyAnswerAsync(Guid negotiationId, WebRtcSessionDescription answer,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<RemoteAnswerApplyResult>("applyAnswer", cancellationToken, negotiationId, answer);

    public Task AddIceCandidateAsync(WebRtcIceCandidate candidate, CancellationToken cancellationToken = default) =>
        InvokeVoidAsync("addIceCandidate", cancellationToken, candidate);

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
        InvokeVoidAsync("setMuted", cancellationToken, muted);

    public Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken = default) =>
        InvokeVoidAsync("setDeafened", cancellationToken, deafened);

    public Task<WebRtcDiagnosticSnapshot?> GetDiagnosticSnapshotAsync(CancellationToken cancellationToken = default) =>
        _sessionId is null ? Task.FromResult<WebRtcDiagnosticSnapshot?>(null) :
            InvokeAsync<WebRtcDiagnosticSnapshot?>("getDiagnosticSnapshot", cancellationToken);

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        var id = _sessionId;
        _sessionId = null;
        if (id is not null && _module is not null)
        {
            try { await _module.InvokeVoidAsync("cleanup", cancellationToken, id); }
            catch (JSDisconnectedException) { }
        }
        _callback?.Dispose();
        _callback = null;
    }

    [JSInvokable]
    public Task OnIceCandidate(WebRtcIceCandidate candidate) => InvokeHandlersAsync(IceCandidateGenerated, candidate);

    [JSInvokable]
    public Task OnConnectionStateChanged(string state)
    {
        var parsed = state switch
        {
            "new" => CallConnectionState.New,
            "connecting" => CallConnectionState.Connecting,
            "connected" => CallConnectionState.Connected,
            "disconnected" => CallConnectionState.Disconnected,
            "failed" => CallConnectionState.Failed,
            "closed" => CallConnectionState.Closed,
            _ => CallConnectionState.New
        };
        return InvokeHandlersAsync(ConnectionStateChanged, parsed);
    }

    [JSInvokable]
    public Task OnSpeakingChanged(bool isSpeaking) => InvokeHandlersAsync(SpeakingChanged, isSpeaking);

    [JSInvokable]
    public Task OnMediaError(string message) => InvokeHandlersAsync(Error, message);

    private async Task<T> InvokeAsync<T>(string method, CancellationToken cancellationToken, params object?[] arguments)
    {
        var module = _module ?? throw new InvalidOperationException("WebRTC media is not initialized.");
        var id = _sessionId ?? throw new InvalidOperationException("WebRTC media is not initialized.");
        return await module.InvokeAsync<T>(method, cancellationToken, [id, .. arguments]);
    }

    private async Task InvokeVoidAsync(string method, CancellationToken cancellationToken, params object?[] arguments)
    {
        var module = _module ?? throw new InvalidOperationException("WebRTC media is not initialized.");
        var id = _sessionId ?? throw new InvalidOperationException("WebRTC media is not initialized.");
        await module.InvokeVoidAsync(method, cancellationToken, [id, .. arguments]);
    }

    private static async Task InvokeHandlersAsync<T>(Func<T, Task>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Func<T, Task> handler in handlers.GetInvocationList()) await handler(value);
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
        if (_module is not null) await _module.DisposeAsync();
        _module = null;
    }
}
