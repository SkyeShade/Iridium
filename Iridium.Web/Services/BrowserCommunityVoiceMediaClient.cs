using Iridium.Client.Core;
using Iridium.Protocol;
using Microsoft.JSInterop;

namespace Iridium.Web.Services;

public sealed class BrowserCommunityVoiceMediaClient(IJSRuntime js, ILogger<BrowserCommunityVoiceMediaClient> logger,
    VoiceParticipantPreferencesService preferences)
    : ICommunityVoiceMediaClient
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<BrowserCommunityVoiceMediaClient>? _callback;
    private string? _sessionId;
    private bool _preferenceSubscribed;

    public event Func<bool, Task>? SpeakingChanged;
    public event Func<string, Task>? Error;
    public event Func<string, Guid, WebRtcSessionDescription, Task>? OfferCreated;
    public event Func<string, Guid, WebRtcSessionDescription, Task>? AnswerCreated;
    public event Func<string, Guid, WebRtcIceCandidate, Task>? IceCandidateGenerated;

    public async Task ConnectAsync(CommunityVoiceMediaSessionDto mediaSession, ActiveVoiceRoomDto room,
        Guid localAccountId, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync("Community media replacement", cancellationToken);
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken,
            "./js/communityVoiceMedia.js?module=mesh-v1");
        _callback = DotNetObjectReference.Create(this);
        var remotePreferences = new List<VoiceParticipantPreference>();
        foreach (var participant in room.Participants.Where(value => value.AccountId != localAccountId)
                     .GroupBy(value => value.AccountId).Select(value => value.First()))
            remotePreferences.Add(await preferences.GetAsync(participant.AccountId, cancellationToken));
        _sessionId = await _module.InvokeAsync<string>("connect", cancellationToken, _callback,
            mediaSession, room, localAccountId, remotePreferences);
        await _module.InvokeVoidAsync("start", cancellationToken, _sessionId);
        if (!_preferenceSubscribed) { preferences.Changed += PreferenceChanged; _preferenceSubscribed = true; }
        logger.LogDebug("Community voice media prepared for Channel={ChannelId}; Provider={Provider}; Status={Status}.",
            room.ChannelId, mediaSession.Provider, mediaSession.Status);
    }

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
        InvokeAsync("setMuted", cancellationToken, muted);

    public Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken = default) =>
        InvokeAsync("setDeafened", cancellationToken, deafened);

    public async Task ParticipantJoinedAsync(VoiceParticipantDto participant,
        CancellationToken cancellationToken = default)
    {
        await InvokeAsync("participantJoined", cancellationToken, participant);
        await InvokeAsync("setParticipantPreference", cancellationToken,
            await preferences.GetAsync(participant.AccountId, cancellationToken));
    }

    public Task ParticipantLeftAsync(string participantId, CancellationToken cancellationToken = default) =>
        InvokeAsync("participantLeft", cancellationToken, participantId);

    public Task HandleOfferAsync(CommunityVoiceMediaDescriptionEvent offer,
        CancellationToken cancellationToken = default) => InvokeAsync("handleOffer", cancellationToken, offer);

    public Task HandleAnswerAsync(CommunityVoiceMediaDescriptionEvent answer,
        CancellationToken cancellationToken = default) => InvokeAsync("handleAnswer", cancellationToken, answer);

    public Task HandleIceCandidateAsync(CommunityVoiceMediaIceCandidateEvent candidate,
        CancellationToken cancellationToken = default) => InvokeAsync("handleIceCandidate", cancellationToken, candidate);

    public async Task DisconnectAsync(string reason, CancellationToken cancellationToken = default)
    {
        var id = _sessionId;
        _sessionId = null;
        if (id is not null && _module is not null)
        {
            try { await _module.InvokeVoidAsync("disconnect", cancellationToken, id, reason); }
            catch (JSDisconnectedException) { }
        }
        _callback?.Dispose();
        _callback = null;
    }

    [JSInvokable]
    public Task OnSpeakingChanged(bool speaking) => InvokeHandlersAsync(SpeakingChanged, speaking);

    [JSInvokable]
    public Task OnMediaError(string message) => InvokeHandlersAsync(Error, message);

    [JSInvokable]
    public Task OnOfferCreated(string targetParticipantId, Guid negotiationId, WebRtcSessionDescription description) =>
        InvokeSignalHandlersAsync(OfferCreated, targetParticipantId, negotiationId, description);

    [JSInvokable]
    public Task OnAnswerCreated(string targetParticipantId, Guid negotiationId, WebRtcSessionDescription description) =>
        InvokeSignalHandlersAsync(AnswerCreated, targetParticipantId, negotiationId, description);

    [JSInvokable]
    public Task OnIceCandidate(string targetParticipantId, Guid negotiationId, WebRtcIceCandidate candidate) =>
        InvokeSignalHandlersAsync(IceCandidateGenerated, targetParticipantId, negotiationId, candidate);

    [JSInvokable]
    public Task OnDiagnostic(CommunityVoiceMediaDiagnosticDto snapshot)
    {
        // TODO: Remove temporary Community voice diagnostics once voice channels are stable.
        logger.LogDebug("COMMUNITY VOICE MEDIA Event={Event} Remote={RemoteParticipant} LocalStream={LocalStream} " +
            "LocalTracks={LocalTracks} Senders={Senders} Connection={Connection} ICE={Ice} LocalIce={LocalIce} " +
            "RemoteIce={RemoteIce} RemoteTracks={RemoteTracks} AudioElements={AudioElements} Play={Play} " +
            "PacketsSent={PacketsSent} PacketsReceived={PacketsReceived} BytesSent={BytesSent} BytesReceived={BytesReceived} " +
            "TrackState={TrackState} TrackMuted={TrackMuted} ElementMuted={ElementMuted} ElementVolume={ElementVolume} " +
            "AudioContext={AudioContext} Gain={Gain} Error={ErrorName}:{ErrorMessage}",
            snapshot.Event, snapshot.RemoteParticipantId, snapshot.LocalStreamPresent, snapshot.LocalAudioTracks,
            snapshot.AttachedSenderCount, snapshot.ConnectionState, snapshot.IceConnectionState,
            snapshot.LocalIceGenerated, snapshot.RemoteIceReceived, snapshot.RemoteTrackCount,
            snapshot.RemoteAudioElements, snapshot.RemoteAudioPlaySucceeded, snapshot.PacketsSent,
            snapshot.PacketsReceived, snapshot.BytesSent, snapshot.BytesReceived, snapshot.RemoteTrackReadyState,
            snapshot.RemoteTrackMuted, snapshot.ElementMuted, snapshot.ElementVolume, snapshot.AudioContextState,
            snapshot.GainValue, snapshot.ErrorName, snapshot.ErrorMessage);
        return Task.CompletedTask;
    }

    private async Task InvokeAsync(string method, CancellationToken cancellationToken, object argument)
    {
        if (_module is null || _sessionId is null) return;
        await _module.InvokeVoidAsync(method, cancellationToken, _sessionId, argument);
    }

    private static async Task InvokeHandlersAsync<T>(Func<T, Task>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Func<T, Task> handler in handlers.GetInvocationList()) await handler(value);
    }

    private static async Task InvokeSignalHandlersAsync<T>(Func<string, Guid, T, Task>? handlers,
        string participantId, Guid negotiationId, T value)
    {
        if (handlers is null) return;
        foreach (Func<string, Guid, T, Task> handler in handlers.GetInvocationList())
            await handler(participantId, negotiationId, value);
    }

    private void PreferenceChanged(VoiceParticipantPreference preference)
    {
        if (_sessionId is null) return;
        _ = InvokeAsync("setParticipantPreference", CancellationToken.None, preference);
    }

    public async ValueTask DisposeAsync()
    {
        if (_preferenceSubscribed) preferences.Changed -= PreferenceChanged;
        await DisconnectAsync("Community media service disposed");
        if (_module is not null) await _module.DisposeAsync();
        _module = null;
    }
}
