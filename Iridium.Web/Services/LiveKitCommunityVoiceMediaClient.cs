using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.JSInterop;

namespace Iridium.Web.Services;

/// <summary>Production community-voice adapter: one LiveKit room, never one peer connection per participant.</summary>
public sealed class LiveKitCommunityVoiceMediaClient(IJSRuntime js, VoiceParticipantPreferencesService preferences,
    LocalVoicePreferenceService localVoicePreferences)
    : ICommunityVoiceMediaClient
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<LiveKitCommunityVoiceMediaClient>? _callback;
    private string? _sessionId;
    private bool _preferenceSubscribed;
    public event Func<bool, Task>? SpeakingChanged;
    public event Func<string, Task>? ScreenShareEnded;
    public event Func<string, Task>? Error;
    public event Func<string, Guid, WebRtcSessionDescription, Task>? OfferCreated { add { } remove { } }
    public event Func<string, Guid, WebRtcSessionDescription, Task>? AnswerCreated { add { } remove { } }
    public event Func<string, Guid, WebRtcIceCandidate, Task>? IceCandidateGenerated { add { } remove { } }

    public async Task ConnectAsync(CommunityVoiceMediaSessionDto mediaSession, ActiveVoiceRoomDto room,
        Guid localAccountId, bool muted = false, bool deafened = false,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(mediaSession.Provider, "livekit", StringComparison.OrdinalIgnoreCase) || mediaSession.NodeSession is null)
            throw new InvalidOperationException("This production media adapter requires a LiveKit session.");
        await DisconnectAsync("SFU session replacement", cancellationToken);
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken,
            $"./js/liveKitMedia.js?build={Uri.EscapeDataString(MediaBuildInfo.Id)}");
        _callback = DotNetObjectReference.Create(this);
        var remotePreferences = new List<VoiceParticipantPreference>();
        foreach (var accountId in room.Participants.Where(p => p.AccountId != localAccountId).Select(p => p.AccountId).Distinct())
            remotePreferences.Add(await preferences.GetAsync(accountId, cancellationToken));
        _sessionId = await _module.InvokeAsync<string>("connectCommunity", cancellationToken,
            _callback, mediaSession, remotePreferences, muted, deafened, localVoicePreferences.Current);
        if (!_preferenceSubscribed)
        {
            preferences.Changed += PreferenceChanged;
            localVoicePreferences.Changed += LocalVoicePreferenceChanged;
            _preferenceSubscribed = true;
        }
    }

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) => Invoke("setMuted", cancellationToken, muted);
    public Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken = default) => Invoke("setDeafened", cancellationToken, deafened);
    public Task ParticipantJoinedAsync(VoiceParticipantDto participant, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ParticipantLeftAsync(string participantId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task HandleOfferAsync(CommunityVoiceMediaDescriptionEvent offer, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task HandleAnswerAsync(CommunityVoiceMediaDescriptionEvent answer, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task HandleIceCandidateAsync(CommunityVoiceMediaIceCandidateEvent candidate, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<LocalVoiceStreamPublication> StartScreenShareAsync(CancellationToken cancellationToken = default) => InvokeResult<LocalVoiceStreamPublication>("startScreenShare", cancellationToken);
    public Task<LocalVoiceStreamPublication> SwitchScreenShareAsync(CancellationToken cancellationToken = default) => InvokeResult<LocalVoiceStreamPublication>("switchScreenShare", cancellationToken);
    public Task StopScreenShareAsync(string reason, CancellationToken cancellationToken = default) => Invoke("stopScreenShare", cancellationToken, reason);
    public Task AttachStreamViewerAsync(string mediaStreamId, string elementId, bool audioMuted, int volumePercent, CancellationToken cancellationToken = default) => Invoke("attachStreamViewer", cancellationToken, mediaStreamId, elementId, audioMuted, volumePercent);
    public Task DetachStreamViewerAsync(string elementId, CancellationToken cancellationToken = default) => Invoke("detachStreamViewer", cancellationToken, elementId);
    public Task SetStreamSubscriptionAsync(string mediaStreamId, bool subscribed, CancellationToken cancellationToken = default) => Invoke("setStreamSubscription", cancellationToken, mediaStreamId, subscribed);
    public Task SetStreamAudioMutedAsync(string elementId, bool muted, CancellationToken cancellationToken = default) => Invoke("setStreamAudioMuted", cancellationToken, elementId, muted);
    public Task SetStreamAudioVolumeAsync(string elementId, int volumePercent, CancellationToken cancellationToken = default) => Invoke("setStreamAudioVolume", cancellationToken, elementId, volumePercent);
    public Task RequestStreamFullscreenAsync(string elementId, CancellationToken cancellationToken = default) => Invoke("requestStreamFullscreen", cancellationToken, elementId);
    public Task<string?> CaptureStreamThumbnailAsync(string mediaStreamId, CancellationToken cancellationToken = default) => InvokeResult<string?>("captureStreamThumbnail", cancellationToken, mediaStreamId);

    public async Task DisconnectAsync(string reason, CancellationToken cancellationToken = default)
    {
        var id = _sessionId; _sessionId = null;
        if (id is not null && _module is not null)
            try { await _module.InvokeVoidAsync("disconnect", cancellationToken, id, reason); } catch (JSDisconnectedException) { }
        _callback?.Dispose(); _callback = null;
    }

    [JSInvokable] public Task OnSpeakingChanged(bool value) => InvokeHandlers(SpeakingChanged, value);
    [JSInvokable] public Task OnScreenShareEnded(string reason) => InvokeHandlers(ScreenShareEnded, reason);
    [JSInvokable] public Task OnMediaError(string message) => InvokeHandlers(Error, message);

    private Task Invoke(string method, CancellationToken token, params object?[] args) => _module is not null && _sessionId is not null ? _module.InvokeVoidAsync(method, token, [_sessionId, .. args]).AsTask() : Task.CompletedTask;
    private async Task<T> InvokeResult<T>(string method, CancellationToken token, params object?[] args) => _module is not null && _sessionId is not null ? await _module.InvokeAsync<T>(method, token, [_sessionId, .. args]) : throw new InvalidOperationException("SFU media is not connected.");
    private void PreferenceChanged(VoiceParticipantPreference value) { if (_sessionId is not null) _ = Invoke("setParticipantPreference", CancellationToken.None, value); }
    private void LocalVoicePreferenceChanged() { if (_sessionId is not null) _ = Invoke("setInputSensitivity", CancellationToken.None, localVoicePreferences.Current); }
    private static async Task InvokeHandlers<T>(Func<T, Task>? handlers, T value) { if (handlers is null) return; foreach (Func<T, Task> handler in handlers.GetInvocationList()) await handler(value); }

    public async ValueTask DisposeAsync()
    {
        if (_preferenceSubscribed)
        {
            preferences.Changed -= PreferenceChanged;
            localVoicePreferences.Changed -= LocalVoicePreferenceChanged;
        }
        await DisconnectAsync("SFU community media disposed");
        if (_module is not null) await _module.DisposeAsync();
    }
}
