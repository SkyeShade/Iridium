using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;

namespace Iridium.Web.Services;

/// <summary>Production DM-call adapter. LiveKit owns the browser-to-SFU peer connection.</summary>
public sealed class LiveKitCallMediaService(
    IJSRuntime js,
    IWebAssemblyHostEnvironment environment,
    VoiceParticipantPreferencesService preferences,
    LocalVoicePreferenceService localVoicePreferences) : ICallMediaService
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<LiveKitCallMediaService>? _callback;
    private string? _sessionId;
    private CallMediaSessionContext? _context;
    private bool _preferenceSubscribed;

    public bool DiagnosticsEnabled => environment.IsDevelopment();
    public event Func<LocalIceCandidateSignal, Task>? IceCandidateGenerated { add { } remove { } }
    public event Func<CallConnectionState, Task>? ConnectionStateChanged;
    public event Func<string, Task>? IceConnectionStateChanged { add { } remove { } }
    public event Func<bool, Task>? SpeakingChanged;
    public event Func<string, Task>? ScreenShareEnded;
    public event Func<string, Task>? Error;
    public event Func<VoiceDiagnosticReport, Task>? DiagnosticGenerated { add { } remove { } }

    public async Task InitializeAsync(CallMediaConfigurationDto configuration, CallMediaSessionContext context,
        CancellationToken cancellationToken = default)
    {
        if (configuration.Mode != MediaMode.NodeSfu || configuration.NodeSession is null)
            throw new InvalidOperationException("This production media adapter requires a Node SFU session.");
        await CleanupAsync("SFU session replacement", cancellationToken);
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken,
            $"./js/liveKitMedia.js?build={Uri.EscapeDataString(MediaBuildInfo.Id)}");
        _context = context;
        _callback = DotNetObjectReference.Create(this);
        var remotePreferences = context.RemoteAccountId is { } remote
            ? new[] { await preferences.GetAsync(remote, cancellationToken) } : [];
        _sessionId = await _module.InvokeAsync<string>("connectCall", cancellationToken,
            _callback, configuration, context, remotePreferences, localVoicePreferences.Current);
        if (!_preferenceSubscribed)
        {
            preferences.Changed += PreferenceChanged;
            localVoicePreferences.Changed += LocalVoicePreferenceChanged;
            _preferenceSubscribed = true;
        }
    }

    public Task<WebRtcSessionDescription> CreateOfferAsync(Guid negotiationId, Guid signalId,
        CancellationToken cancellationToken = default) => throw LegacySignaling();
    public Task<WebRtcSessionDescription> AcceptOfferAsync(Guid negotiationId, Guid offerSignalId, Guid answerSignalId,
        WebRtcSessionDescription offer, CancellationToken cancellationToken = default) => throw LegacySignaling();
    public Task<RemoteAnswerApplyResult> ApplyAnswerAsync(Guid negotiationId, Guid signalId,
        WebRtcSessionDescription answer, CancellationToken cancellationToken = default) => throw LegacySignaling();
    public Task AddIceCandidateAsync(Guid signalId, WebRtcIceCandidate candidate,
        CancellationToken cancellationToken = default) => throw LegacySignaling();

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) => Invoke("setMuted", cancellationToken, muted);
    public Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken = default) => Invoke("setDeafened", cancellationToken, deafened);
    public Task<LocalVoiceStreamPublication> StartScreenShareAsync(CancellationToken cancellationToken = default) =>
        InvokeResult<LocalVoiceStreamPublication>("startScreenShare", cancellationToken);
    public Task<LocalVoiceStreamPublication> SwitchScreenShareAsync(CancellationToken cancellationToken = default) =>
        InvokeResult<LocalVoiceStreamPublication>("switchScreenShare", cancellationToken);
    public Task StopScreenShareAsync(string reason, CancellationToken cancellationToken = default) => Invoke("stopScreenShare", cancellationToken, reason);
    public Task AttachStreamViewerAsync(string mediaStreamId, string elementId, bool audioMuted, int volumePercent,
        CancellationToken cancellationToken = default) => Invoke("attachStreamViewer", cancellationToken,
            mediaStreamId, elementId, audioMuted, volumePercent);
    public Task DetachStreamViewerAsync(string elementId, CancellationToken cancellationToken = default) => Invoke("detachStreamViewer", cancellationToken, elementId);
    public Task SetStreamSubscriptionAsync(string mediaStreamId, bool subscribed,
        CancellationToken cancellationToken = default) => Invoke("setStreamSubscription", cancellationToken, mediaStreamId, subscribed);
    public Task SetStreamAudioMutedAsync(string elementId, bool muted, CancellationToken cancellationToken = default) => Invoke("setStreamAudioMuted", cancellationToken, elementId, muted);
    public Task SetStreamAudioVolumeAsync(string elementId, int volumePercent, CancellationToken cancellationToken = default) => Invoke("setStreamAudioVolume", cancellationToken, elementId, volumePercent);
    public Task RequestStreamFullscreenAsync(string elementId, CancellationToken cancellationToken = default) => Invoke("requestStreamFullscreen", cancellationToken, elementId);
    public Task<string?> CaptureStreamThumbnailAsync(string mediaStreamId, CancellationToken cancellationToken = default) => InvokeResult<string?>("captureStreamThumbnail", cancellationToken, mediaStreamId);
    public Task<WebRtcDiagnosticSnapshot?> GetDiagnosticSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult<WebRtcDiagnosticSnapshot?>(null);

    public async Task CleanupAsync(string reason, CancellationToken cancellationToken = default)
    {
        var id = _sessionId; _sessionId = null;
        if (id is not null && _module is not null)
            try { await _module.InvokeVoidAsync("disconnect", cancellationToken, id, reason); } catch (JSDisconnectedException) { }
        _callback?.Dispose(); _callback = null; _context = null;
    }

    [JSInvokable] public Task OnConnectionStateChanged(int generation, string state) =>
        generation == _context?.PeerGeneration ? InvokeHandlers(ConnectionStateChanged, ParseState(state)) : Task.CompletedTask;
    [JSInvokable] public Task OnSpeakingChanged(int generation, bool speaking) =>
        generation == _context?.PeerGeneration ? InvokeHandlers(SpeakingChanged, speaking) : Task.CompletedTask;
    [JSInvokable] public Task OnScreenShareEnded(int generation, string reason) =>
        generation == _context?.PeerGeneration ? InvokeHandlers(ScreenShareEnded, reason) : Task.CompletedTask;
    [JSInvokable] public Task OnMediaError(int generation, string message) =>
        generation == _context?.PeerGeneration ? InvokeHandlers(Error, message) : Task.CompletedTask;

    private Task Invoke(string method, CancellationToken token, params object?[] args) =>
        _module is not null && _sessionId is not null
            ? _module.InvokeVoidAsync(method, token, [_sessionId, .. args]).AsTask() : Task.CompletedTask;
    private async Task<T> InvokeResult<T>(string method, CancellationToken token, params object?[] args) =>
        _module is not null && _sessionId is not null
            ? await _module.InvokeAsync<T>(method, token, [_sessionId, .. args])
            : throw new InvalidOperationException("SFU media is not connected.");
    private void PreferenceChanged(VoiceParticipantPreference value) { if (_sessionId is not null) _ = Invoke("setParticipantPreference", CancellationToken.None, value); }
    private void LocalVoicePreferenceChanged() { if (_sessionId is not null) _ = Invoke("setInputSensitivity", CancellationToken.None, localVoicePreferences.Current); }
    private static Exception LegacySignaling() => new InvalidOperationException("Peer-to-peer signaling is not used by SFU media.");
    private static CallConnectionState ParseState(string state) => state switch { "connected" => CallConnectionState.Connected, "connecting" => CallConnectionState.Connecting, "disconnected" => CallConnectionState.Disconnected, "failed" => CallConnectionState.Failed, _ => CallConnectionState.New };
    private static async Task InvokeHandlers<T>(Func<T, Task>? handlers, T value) { if (handlers is null) return; foreach (Func<T, Task> handler in handlers.GetInvocationList()) await handler(value); }

    public async ValueTask DisposeAsync()
    {
        if (_preferenceSubscribed)
        {
            preferences.Changed -= PreferenceChanged;
            localVoicePreferences.Changed -= LocalVoicePreferenceChanged;
        }
        await CleanupAsync("SFU media service disposed");
        if (_module is not null) await _module.DisposeAsync();
    }
}
