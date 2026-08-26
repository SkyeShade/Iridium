using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace Iridium.Web.Services;

public sealed class WebRtcCallMediaService(
    IJSRuntime js,
    IWebAssemblyHostEnvironment environment,
    ILogger<WebRtcCallMediaService> logger,
    VoiceParticipantPreferencesService preferences,
    IWebRtcConfigurationProvider webRtcConfiguration) : ICallMediaService
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<WebRtcCallMediaService>? _callback;
    private string? _sessionId;
    private CallMediaSessionContext? _context;
    private bool _preferenceSubscribed;

    public bool DiagnosticsEnabled => environment.IsDevelopment();

    public event Func<LocalIceCandidateSignal, Task>? IceCandidateGenerated;
    public event Func<CallConnectionState, Task>? ConnectionStateChanged;
    public event Func<string, Task>? IceConnectionStateChanged;
    public event Func<bool, Task>? SpeakingChanged;
    public event Func<string, Task>? ScreenShareEnded;
    public event Func<string, Task>? Error;
    public event Func<VoiceDiagnosticReport, Task>? DiagnosticGenerated;

    public async Task InitializeAsync(CallMediaConfigurationDto configuration, CallMediaSessionContext context,
        CancellationToken cancellationToken = default)
    {
        await CleanupAsync("peer replacement during initialization", cancellationToken);
        var modulePath = $"./js/voiceCall.js?build={Uri.EscapeDataString(MediaBuildInfo.Id)}";
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, modulePath);
        _callback = DotNetObjectReference.Create(this);
        _context = context;
        var iceConfiguration = await webRtcConfiguration.GetAsync(cancellationToken);
        if (environment.IsDevelopment()) WebRtcConfigurationDiagnostics.LogLoaded(logger, iceConfiguration);
        var preference = context.RemoteAccountId is { } remote
            ? await preferences.GetAsync(remote, cancellationToken) : null;
        try
        {
            _sessionId = await _module.InvokeAsync<string>("initialize", cancellationToken,
                MediaBuildInfo.Id, _callback, iceConfiguration.IceServers, iceConfiguration.IceTransportPolicy,
                environment.IsDevelopment(), context.CallId, context.LocalAccountId,
                context.Role, context.PeerGeneration, context.NegotiationId, context.NegotiationGeneration,
                context.RemoteAccountId, preference,
                context.RemoteAccountId is { } remoteAccountId && context.LocalAccountId.CompareTo(remoteAccountId) > 0);
        }
        catch (JSException exception) when (IsBuildMismatch(exception))
        {
            var updates = await js.InvokeAsync<IJSObjectReference>("import", cancellationToken,
                $"./js/clientUpdate.js?build={Uri.EscapeDataString(MediaBuildInfo.Id)}");
            if (!await updates.InvokeAsync<bool>("recoverMediaMismatch", cancellationToken, MediaBuildInfo.Id))
                throw new InvalidOperationException(
                    "Iridium was updated, but this tab is still using older client files. Close and reopen this tab.",
                    exception);
            throw;
        }
        if (!_preferenceSubscribed) { preferences.Changed += PreferenceChanged; _preferenceSubscribed = true; }
    }

    public Task<WebRtcSessionDescription> CreateOfferAsync(Guid negotiationId, Guid signalId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<WebRtcSessionDescription>("createOffer", cancellationToken, negotiationId, signalId);

    public Task<WebRtcSessionDescription> AcceptOfferAsync(Guid negotiationId, Guid offerSignalId, Guid answerSignalId,
        WebRtcSessionDescription offer,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<WebRtcSessionDescription>("acceptOffer", cancellationToken, negotiationId,
            offerSignalId, answerSignalId, offer);

    public Task<RemoteAnswerApplyResult> ApplyAnswerAsync(Guid negotiationId, Guid signalId, WebRtcSessionDescription answer,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<RemoteAnswerApplyResult>("applyAnswer", cancellationToken, negotiationId, signalId, answer);

    public Task AddIceCandidateAsync(Guid signalId, WebRtcIceCandidate candidate,
        CancellationToken cancellationToken = default) =>
        InvokeVoidAsync("addIceCandidate", cancellationToken, signalId, candidate);

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
        InvokeVoidAsync("setMuted", cancellationToken, muted);

    public Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken = default) =>
        InvokeVoidAsync("setDeafened", cancellationToken, deafened);

    public Task<LocalVoiceStreamPublication> StartScreenShareAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<LocalVoiceStreamPublication>("startScreenShare", cancellationToken);

    public Task<LocalVoiceStreamPublication> SwitchScreenShareAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<LocalVoiceStreamPublication>("switchScreenShare", cancellationToken);

    public Task StopScreenShareAsync(string reason, CancellationToken cancellationToken = default) =>
        InvokeVoidAsync("stopScreenShare", cancellationToken, reason);

    public Task AttachStreamViewerAsync(string mediaStreamId, string elementId, bool audioMuted, int volumePercent,
        CancellationToken cancellationToken = default) =>
        InvokeVoidAsync("attachStreamViewer", cancellationToken, mediaStreamId, elementId, audioMuted, volumePercent);

    public Task DetachStreamViewerAsync(string elementId, CancellationToken cancellationToken = default) =>
        InvokeVoidAsync("detachStreamViewer", cancellationToken, elementId);

    public Task SetStreamAudioMutedAsync(string elementId, bool muted,
        CancellationToken cancellationToken = default) =>
        InvokeVoidAsync("setStreamAudioMuted", cancellationToken, elementId, muted);

    public Task SetStreamAudioVolumeAsync(string elementId, int volumePercent,
        CancellationToken cancellationToken = default) =>
        InvokeVoidAsync("setStreamAudioVolume", cancellationToken, elementId, volumePercent);

    public Task RequestStreamFullscreenAsync(string elementId, CancellationToken cancellationToken = default) =>
        InvokeVoidAsync("requestStreamFullscreen", cancellationToken, elementId);

    public Task<string?> CaptureStreamThumbnailAsync(string mediaStreamId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<string?>("captureStreamThumbnail", cancellationToken, mediaStreamId);

    public Task<WebRtcDiagnosticSnapshot?> GetDiagnosticSnapshotAsync(CancellationToken cancellationToken = default) =>
        _sessionId is null ? Task.FromResult<WebRtcDiagnosticSnapshot?>(null) :
            InvokeAsync<WebRtcDiagnosticSnapshot?>("getDiagnosticSnapshot", cancellationToken);

    public async Task CleanupAsync(string reason, CancellationToken cancellationToken = default)
    {
        var id = _sessionId;
        _sessionId = null;
        if (id is not null && _module is not null)
        {
            try { await _module.InvokeVoidAsync("cleanup", cancellationToken, id, reason); }
            catch (JSDisconnectedException) { }
        }
        _callback?.Dispose();
        _callback = null;
        _context = null;
    }

    [JSInvokable]
    public async Task OnIceCandidate(int peerGeneration, int negotiationGeneration, int sequence, Guid signalId,
        WebRtcIceCandidate candidate)
    {
        if (!IsCurrent(peerGeneration, "ICE candidate"))
        {
            if (DiagnosticsEnabled && _context is { } staleContext)
                await InvokeHandlersAsync(DiagnosticGenerated, new VoiceDiagnosticReport(staleContext.CallId,
                    "StaleSignalIgnored", peerGeneration, negotiationGeneration, signalId, sequence,
                    Reason: "PeerGenerationMismatch"));
            return;
        }
        // TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
        if (environment.IsDevelopment())
            logger.LogDebug(
                "VOICE DIAGNOSTIC DOTNET RECEIVED LOCAL ICE #{Sequence}: CallId={CallId} AccountId={AccountId} " +
                "Role={Role} PeerGeneration={PeerGeneration} NegotiationGeneration={NegotiationGeneration} " +
                "SignalId={SignalId} HasCandidate={HasCandidate} HasSdpMid={HasSdpMid} " +
                "HasSdpMLineIndex={HasSdpMLineIndex} HasUsernameFragment={HasUsernameFragment}",
                sequence, _context?.CallId, _context?.LocalAccountId, _context?.Role, peerGeneration,
                negotiationGeneration, signalId, !string.IsNullOrWhiteSpace(candidate.Candidate),
                candidate.SdpMid is not null, candidate.SdpMLineIndex is not null,
                candidate.UsernameFragment is not null);
        if (DiagnosticsEnabled && _context is { } context)
            await InvokeHandlersAsync(DiagnosticGenerated, new VoiceDiagnosticReport(context.CallId,
                "LocalIceReceivedFromJs", peerGeneration, negotiationGeneration, signalId, sequence));
        await InvokeHandlersAsync(IceCandidateGenerated,
            new LocalIceCandidateSignal(sequence, signalId, peerGeneration, negotiationGeneration, candidate));
    }

    [JSInvokable]
    public Task OnConnectionStateChanged(int peerGeneration, string state)
    {
        if (!IsCurrent(peerGeneration, $"connectionState={state}")) return Task.CompletedTask;
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
    public Task OnIceConnectionStateChanged(int peerGeneration, string state) =>
        IsCurrent(peerGeneration, $"iceConnectionState={state}")
            ? InvokeHandlersAsync(IceConnectionStateChanged, state)
            : Task.CompletedTask;

    [JSInvokable]
    public Task OnSpeakingChanged(int peerGeneration, bool isSpeaking) =>
        IsCurrent(peerGeneration, "speaking state") ? InvokeHandlersAsync(SpeakingChanged, isSpeaking) : Task.CompletedTask;

    [JSInvokable]
    public Task OnScreenShareEnded(int peerGeneration, string reason) =>
        IsCurrent(peerGeneration, "screen share ended")
            ? InvokeHandlersAsync(ScreenShareEnded, reason)
            : Task.CompletedTask;

    [JSInvokable]
    public Task OnMediaError(int peerGeneration, string message) =>
        IsCurrent(peerGeneration, "media error") ? InvokeHandlersAsync(Error, message) : Task.CompletedTask;

    [JSInvokable]
    public Task OnVoiceDiagnostic(int peerGeneration, VoiceDiagnosticReport report)
    {
        if (!DiagnosticsEnabled || !IsCurrent(peerGeneration, $"voice diagnostic {report.Event}"))
            return Task.CompletedTask;
        return InvokeHandlersAsync(DiagnosticGenerated, report);
    }

    // TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
    private bool IsCurrent(int peerGeneration, string eventName)
    {
        var current = _context?.PeerGeneration == peerGeneration;
        if (!current && environment.IsDevelopment())
            logger.LogDebug(
                "VOICE DIAGNOSTIC stale JS callback ignored: Event={Event} CallId={CallId} AccountId={AccountId} " +
                "Role={Role} CallbackPeerGeneration={CallbackPeerGeneration} CurrentPeerGeneration={CurrentPeerGeneration}",
                eventName, _context?.CallId, _context?.LocalAccountId, _context?.Role,
                peerGeneration, _context?.PeerGeneration);
        return current;
    }

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

    private void PreferenceChanged(VoiceParticipantPreference preference)
    {
        if (_context?.RemoteAccountId != preference.RemoteAccountId || _sessionId is null) return;
        _ = InvokeVoidAsync("setParticipantPreference", CancellationToken.None, preference);
    }

    private static bool IsBuildMismatch(JSException exception) =>
        exception.Message.Contains("VersionMismatch", StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        if (_preferenceSubscribed) preferences.Changed -= PreferenceChanged;
        await CleanupAsync("media service disposed");
        if (_module is not null) await _module.DisposeAsync();
        _module = null;
    }
}
