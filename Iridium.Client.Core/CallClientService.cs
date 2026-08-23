using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Iridium.Client.Core;

public sealed class CallClientService(
    NodeSession session,
    RealtimeConnectionService realtime,
    ICallMediaService media,
    ILogger<CallClientService> logger)
    : IDirectVoiceSession, IAsyncDisposable
{
    // TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
    private static int _nextInstanceId;
    private readonly int _instanceId = CreateDiagnosticInstance(logger, media.DiagnosticsEnabled);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _signalingGate = new(1, 1);
    private readonly List<IDisposable> _handlerRegistrations = [];
    private HubConnection? _connection;
    private Uri? _node;
    private Guid? _accountId;
    private WebRtcDescriptionEvent? _pendingOffer;
    private Guid? _pendingSignalingCallId;
    private readonly List<WebRtcIceCandidateEvent> _pendingIce = [];
    private bool _mediaReady;
    private bool _remoteDescriptionReady;
    private CancellationTokenSource? _heartbeatCancellation;
    private CancellationTokenSource? _negotiationCancellation;
    private readonly Dictionary<string, int> _localCandidateTypes = new(StringComparer.OrdinalIgnoreCase);
    private int _peerGeneration;
    private Guid? _negotiationId;
    private int _negotiationGeneration;
    private bool _negotiationStarted;
    private readonly HashSet<Guid> _processedAnswerNegotiations = [];
    private int _localCandidatesGenerated;
    private int _localCandidatesSent;
    private int _remoteCandidatesReceived;
    private int _remoteCandidatesQueued;
    private int _remoteCandidatesAdded;
    private int _remoteCandidateAddFailures;
    private string _mediaRole = "unknown";
    private string? _appliedOfferSdp;
    private bool _mediaFailureInProgress;
    private int _negotiationTimeoutPeerGeneration;
    private Guid? _negotiationTimeoutId;
    private int _handlerRegistrationCount;
    private int _offerReceivedCount;
    private int _answerReceivedCount;
    private int _iceReceivedCount;
    private int _createOfferCount;
    private int _createAnswerCount;
    private int _acceptInvokedCount;
    private int _callAcceptedReceivedCount;
    private readonly HashSet<Guid> _receivedSignalIds = [];
    private bool _accepting;
    private (bool Muted, bool Deafened, CallConnectionState State)? _lastPublishedParticipantState;
    private bool _disposed;
    private readonly List<PublishedVoiceStreamDto> _publishedStreams = [];

    public CallSessionDto? CurrentCall { get; private set; }
    public IncomingCallEvent? IncomingCall { get; private set; }
    public string? StatusMessage { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool IsMuted { get; private set; }
    public bool IsDeafened { get; private set; }
    public CallConnectionState MediaConnectionState { get; private set; } = CallConnectionState.New;
    public bool CanRetry => CurrentCall?.State == CallState.Active && MediaConnectionState == CallConnectionState.Failed;
    public bool IsAccepting => _accepting;
    public bool IsSignalingConnected => _connection?.State == HubConnectionState.Connected;
    public Guid? AccountId => _accountId;
    public IReadOnlyList<PublishedVoiceStreamDto> PublishedStreams => _publishedStreams;
    public PublishedVoiceStreamDto? WatchedStream { get; private set; }
    public event Action? Changed;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await EnsureConnectionAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task StartAsync(DirectConversationDto conversation, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectionAsync(cancellationToken);
            if (CurrentCall is not null || IncomingCall is not null) throw new InvalidOperationException("A call is already in progress.");
            ResetCallDiagnostics();
            if (conversation.OtherParticipant.AccountId == _accountId) throw new InvalidOperationException("You cannot call yourself.");
            ErrorMessage = null;
            StatusMessage = $"Calling {conversation.OtherParticipant.DisplayName}…";
            CurrentCall = await _connection!.InvokeAsync<CallSessionDto>(VoiceCallHubContract.Start,
                conversation.Id, cancellationToken);
            _pendingSignalingCallId = CurrentCall.Id;
            logger.LogDebug("Call {CallId}: created; original caller is the WebRTC offerer.", CurrentCall.Id);
            NotifyChanged();
            try
            {
                await StartMediaAsync(cancellationToken);
                logger.LogDebug("Call {CallId}: caller media prepared; offer creation will begin exactly once after CallAccepted.",
                    CurrentCall.Id);
            }
            catch (Exception exception)
            {
                ErrorMessage = MediaErrorMessage(exception);
                await TryInvokeAsync(VoiceCallHubContract.Cancel, CurrentCall.Id, cancellationToken);
                await FinishAsync(clearMessage: false);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (_accepting) return;
        _accepting = true;
        _acceptInvokedCount++;
        VoiceDiagnostic("AcceptCall invoked", IncomingCall?.CallId);
        NotifyChanged();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IncomingCall is not { } incoming) return;
            await EnsureConnectionAsync(cancellationToken);
            ErrorMessage = null;
            StatusMessage = "Accepting call…";
            NotifyChanged();
            var accepted = false;
            try
            {
                StatusMessage = "Requesting microphone access…";
                NotifyChanged();
                await StartMediaAsync(cancellationToken);
                await _connection!.InvokeAsync(VoiceCallHubContract.Accept, incoming.CallId, cancellationToken);
                accepted = true;
                VoiceDiagnostic("server accepted", incoming.CallId);
                CurrentCall = await _connection!.InvokeAsync<CallSessionDto?>(VoiceCallHubContract.GetCurrent, cancellationToken)
                    ?? throw new InvalidOperationException("The call ended before it could be accepted.");
                IncomingCall = null;
                MediaConnectionState = CallConnectionState.Connecting;
                StatusMessage = "Connecting media…";
                if (_pendingOffer is not null) await AnswerPendingOfferAsync(cancellationToken);
                NotifyChanged();
            }
            catch (Exception exception)
            {
                var message = MediaErrorMessage(exception);
                if (!accepted)
                {
                    ErrorMessage = message;
                    await TryInvokeAsync(VoiceCallHubContract.Reject, incoming.CallId, cancellationToken);
                    await FinishAsync(clearMessage: false);
                }
                else await FailMediaAsync(message);
            }
        }
        finally
        {
            _accepting = false;
            _gate.Release();
            NotifyChanged();
        }
    }

    public async Task DeclineAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IncomingCall is not { } incoming) return;
            await TryInvokeAsync(VoiceCallHubContract.Reject, incoming.CallId, cancellationToken);
            await FinishAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task HangUpAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (CurrentCall is not { } call)
            {
                if (IncomingCall is { } incoming)
                    await TryInvokeAsync(VoiceCallHubContract.Reject, incoming.CallId, cancellationToken);
                await FinishAsync();
                return;
            }
            var method = call.State == CallState.Ringing && call.CallerAccountId == _accountId
                ? VoiceCallHubContract.Cancel : VoiceCallHubContract.HangUp;
            // Stop microphone transmission before ending server membership so a subsequent
            // voice session can never overlap this PeerConnection's local track.
            await ResetMediaAsync("voice session ended by local user", cancellationToken);
            await TryInvokeAsync(method, call.Id, cancellationToken);
            await FinishAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task ToggleMuteAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentCall is null || !_mediaReady) return;
        IsMuted = !IsMuted;
        await media.SetMutedAsync(IsMuted, cancellationToken);
        await PublishParticipantStateAsync(cancellationToken);
        NotifyChanged();
    }

    public async Task ToggleDeafenAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentCall is null || !_mediaReady) return;
        IsDeafened = !IsDeafened;
        await media.SetDeafenedAsync(IsDeafened, cancellationToken);
        await PublishParticipantStateAsync(cancellationToken);
        NotifyChanged();
    }

    public async Task RetryMediaAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (CurrentCall is not { State: CallState.Active } call) return;
            ErrorMessage = null;
            StatusMessage = "Retrying media…";
            NotifyChanged();
            if (call.CallerAccountId == _accountId) await RestartOffererAsync(cancellationToken);
            else
            {
                await ResetMediaAsync("callee retry replacement", cancellationToken);
                _negotiationId = null;
                _negotiationStarted = false;
                MediaConnectionState = CallConnectionState.Connecting;
                StatusMessage = "Waiting for caller to restart media…";
                await _connection!.InvokeAsync(VoiceCallHubContract.RequestMediaRetry, call.Id, cancellationToken);
                logger.LogDebug("Call {CallId}: callee requested deterministic caller retry.", call.Id);
                NotifyChanged();
            }
        }
        catch (Exception exception) { await FailMediaAsync(MediaErrorMessage(exception)); }
        finally { _gate.Release(); }
    }

    public void DismissStatus()
    {
        if (CurrentCall is not null || IncomingCall is not null) return;
        StatusMessage = null;
        ErrorMessage = null;
        NotifyChanged();
    }

    public async Task EndForAccountSwitchAsync(CancellationToken cancellationToken = default)
    {
        await HangUpAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not null)
            {
                DisposeHandlerRegistrations();
            }
            _connection = null; _node = null; _accountId = null;
        }
        finally { _gate.Release(); }
    }

    public async Task StartScreenShareAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentCall?.State != CallState.Active || !_mediaReady || !IsSignalingConnected) return;
        var publication = await media.StartScreenShareAsync(cancellationToken);
        PublishedVoiceStreamDto? publishedStream = null;
        try
        {
            await _signalingGate.WaitAsync(cancellationToken);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                publishedStream = await _connection!.InvokeAsync<PublishedVoiceStreamDto>(VoiceStreamHubContract.Publish,
                    VoiceMediaSessionKind.DirectCall, CurrentCall.Id,
                    new PublishVoiceStreamRequest(publication.StreamId, publication.Kind, publication.HasAudio,
                        publication.MediaStreamId), cancellationToken);
                ApplyPublishedStream(publishedStream);
                await StartRenegotiationUnsafeAsync("ScreenTrackAdded", cancellationToken);
            }
            finally { _gate.Release(); _signalingGate.Release(); }
        }
        catch
        {
            await media.StopScreenShareAsync("PublicationRejected", cancellationToken);
            throw;
        }
        await WatchStreamAsync(publishedStream.StreamId, cancellationToken);
    }

    public async Task StopScreenShareAsync(string reason = "UserStoppedInIridium",
        CancellationToken cancellationToken = default)
    {
        var stream = _publishedStreams.FirstOrDefault(value => value.OwnerAccountId == _accountId &&
            value.Kind == VoicePublishedStreamKind.ScreenShare);
        await media.StopScreenShareAsync(reason, cancellationToken);
        if (stream is null || CurrentCall is null || !IsSignalingConnected) return;
        await _signalingGate.WaitAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _connection!.InvokeAsync(VoiceStreamHubContract.StopPublishing,
                VoiceMediaSessionKind.DirectCall, CurrentCall.Id, stream.StreamId, reason, cancellationToken);
            ApplyEndedStream(stream.StreamId);
            await StartRenegotiationUnsafeAsync("ScreenTrackRemoved", cancellationToken);
        }
        finally { _gate.Release(); _signalingGate.Release(); }
    }

    public async Task WatchStreamAsync(Guid streamId, CancellationToken cancellationToken = default)
    {
        if (CurrentCall is null || !IsSignalingConnected) return;
        var stream = _publishedStreams.FirstOrDefault(value => value.StreamId == streamId)
            ?? throw new InvalidOperationException("That stream is no longer available.");
        if (WatchedStream is not null) await StopWatchingAsync(cancellationToken);
        // Local self-preview binds directly to the captured MediaStream. It needs no network
        // subscription and must never provoke a second negotiation.
        if (stream.OwnerAccountId != _accountId)
            await _connection!.InvokeAsync(VoiceStreamHubContract.Watch, VoiceMediaSessionKind.DirectCall,
                CurrentCall.Id, streamId, cancellationToken);
        WatchedStream = stream;
        NotifyChanged();
    }

    public async Task StopWatchingAsync(CancellationToken cancellationToken = default)
    {
        var stream = WatchedStream;
        WatchedStream = null;
        if (stream is not null && stream.OwnerAccountId != _accountId && IsSignalingConnected)
            await _connection!.InvokeAsync(VoiceStreamHubContract.StopWatching, stream.StreamId, cancellationToken);
        NotifyChanged();
    }

    public Task AttachWatchedStreamAsync(string elementId, CancellationToken cancellationToken = default) =>
        WatchedStream is { } stream
            ? media.AttachStreamViewerAsync(stream.MediaStreamId, elementId, audioMuted: !stream.HasAudio, cancellationToken)
            : Task.CompletedTask;

    public Task DetachWatchedStreamAsync(string elementId, CancellationToken cancellationToken = default) =>
        media.DetachStreamViewerAsync(elementId, cancellationToken);

    public Task SetStreamAudioMutedAsync(string elementId, bool muted,
        CancellationToken cancellationToken = default) =>
        media.SetStreamAudioMutedAsync(elementId, muted, cancellationToken);

    public Task RequestStreamFullscreenAsync(string elementId, CancellationToken cancellationToken = default) =>
        media.RequestStreamFullscreenAsync(elementId, cancellationToken);

    public Task<string?> CaptureStreamThumbnailAsync(string mediaStreamId,
        CancellationToken cancellationToken = default) =>
        media.CaptureStreamThumbnailAsync(mediaStreamId, cancellationToken);

    private async Task EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var client = session.AuthorizedClient;
        var accountId = session.Account?.Id ?? throw new InvalidOperationException("Log in before using voice calls.");
        var connection = await realtime.EnsureConnectedAsync("CallClientService requested realtime", cancellationToken);
        if (ReferenceEquals(_connection, connection)) return;
        DisposeHandlerRegistrations();
        _node = client.NodeAddress; _accountId = accountId;
        _connection = connection;
        RegisterHandlers(connection);
        await RestoreCurrentCallAsync();
    }

    private void RegisterHandlers(HubConnection connection)
    {
        DisposeHandlerRegistrations();
        _handlerRegistrationCount++;
        VoiceDiagnostic("Registering Offer/Answer/IceCandidate handlers", registrationCount: _handlerRegistrationCount);
        _handlerRegistrations.Add(connection.On<IncomingCallEvent>(VoiceCallHubContract.Incoming, value => RunHandlerAsync(() => ReceiveIncoming(value))));
        _handlerRegistrations.Add(connection.On<CallStateEvent>(VoiceCallHubContract.Accepted, value => RunHandlerAsync(() => ReceiveAcceptedAsync(value))));
        _handlerRegistrations.Add(connection.On<CallStateEvent>(VoiceCallHubContract.Rejected, value => RunHandlerAsync(() => ReceiveTerminalAsync(value, "Call declined"))));
        _handlerRegistrations.Add(connection.On<CallStateEvent>(VoiceCallHubContract.Cancelled, value => RunHandlerAsync(() => ReceiveTerminalAsync(value, value.Reason ?? "Call cancelled"))));
        _handlerRegistrations.Add(connection.On<CallStateEvent>(VoiceCallHubContract.Ended, value => RunHandlerAsync(() => ReceiveTerminalAsync(value, "Call ended"))));
        _handlerRegistrations.Add(connection.On<CallParticipantStateEvent>(VoiceCallHubContract.ParticipantStateChanged,
            value => RunHandlerAsync(() => ReceiveParticipantState(value))));
        _handlerRegistrations.Add(connection.On<CallParticipantSpeakingEvent>(VoiceCallHubContract.ParticipantSpeakingChanged,
            value => RunHandlerAsync(() => ReceiveParticipantSpeaking(value))));
        _handlerRegistrations.Add(connection.On<CallStateEvent>(VoiceCallHubContract.MediaRetryRequested,
            value => RunHandlerAsync(() => ReceiveMediaRetryRequestedAsync(value))));
        _handlerRegistrations.Add(connection.On<WebRtcDescriptionEvent>(VoiceCallHubContract.Offer,
            value => RunHandlerAsync(() => ReceiveOfferAsync(value), value.NegotiationKind != WebRtcNegotiationKind.Initial)));
        _handlerRegistrations.Add(connection.On<WebRtcDescriptionEvent>(VoiceCallHubContract.Answer,
            value => RunHandlerAsync(() => ReceiveAnswerAsync(value), value.NegotiationKind != WebRtcNegotiationKind.Initial)));
        _handlerRegistrations.Add(connection.On<WebRtcIceCandidateEvent>(VoiceCallHubContract.IceCandidate, value => RunHandlerAsync(() => ReceiveIceAsync(value))));
        _handlerRegistrations.Add(connection.On<VoiceStreamPublishedEvent>(VoiceStreamHubContract.Published,
            value => RunHandlerAsync(() => { ApplyPublishedStream(value.Stream); return Task.CompletedTask; })));
        _handlerRegistrations.Add(connection.On<VoiceStreamEndedEvent>(VoiceStreamHubContract.Ended,
            value => RunHandlerAsync(() => { if (value.SessionKind == VoiceMediaSessionKind.DirectCall)
                ApplyEndedStream(value.StreamId); return Task.CompletedTask; })));
        VoiceDiagnostic("subscribed to signaling", registrationCount: _handlerRegistrationCount,
            details: $"activeHandlers={_handlerRegistrations.Count}");
        connection.Reconnecting += exception => { StatusMessage = "Signaling reconnecting; audio may continue…"; NotifyChanged(); return Task.CompletedTask; };
        connection.Reconnected += _ => RunHandlerAsync(RestoreCurrentCallAsync);
        connection.Closed += exception => RunHandlerAsync(async () =>
        {
            if (CurrentCall is not null || IncomingCall is not null)
            {
                ErrorMessage = "The call ended because signaling could not reconnect.";
                await FinishAsync(clearMessage: false);
            }
        });
    }

    private void DisposeHandlerRegistrations()
    {
        if (_handlerRegistrations.Count > 0)
            VoiceDiagnostic("disposing signaling subscriptions", details: $"activeHandlers={_handlerRegistrations.Count}");
        foreach (var registration in _handlerRegistrations) registration.Dispose();
        _handlerRegistrations.Clear();
    }

    private Task ReceiveIncoming(IncomingCallEvent incoming)
    {
        if (CurrentCall is not null || IncomingCall is not null) return Task.CompletedTask;
        ResetCallDiagnostics();
        if (_pendingSignalingCallId is not null && _pendingSignalingCallId != incoming.CallId)
        {
            _pendingOffer = null;
            _pendingIce.Clear();
        }
        _pendingSignalingCallId = incoming.CallId;
        IncomingCall = incoming; StatusMessage = "Incoming voice call"; ErrorMessage = null; NotifyChanged();
        return Task.CompletedTask;
    }

    private async Task ReceiveAcceptedAsync(CallStateEvent value)
    {
        if (CurrentCall?.Id != value.CallId) return;
        _callAcceptedReceivedCount++;
        var duplicateSignal = value.SignalId is { } acceptedSignalId && !_receivedSignalIds.Add(acceptedSignalId);
        VoiceDiagnostic("caller received CallAccepted", value.CallId, value.SignalId,
            details: $"acceptedReceivedCount={_callAcceptedReceivedCount} duplicateSignalId={duplicateSignal}");
        CurrentCall = CurrentCall with { State = CallState.Active };
        var authoritativeCall = await _connection!.InvokeAsync<CallSessionDto?>(VoiceCallHubContract.GetCurrent);
        if (authoritativeCall?.Id == value.CallId)
            CurrentCall = authoritativeCall;
        if (MediaConnectionState != CallConnectionState.Connected)
        {
            MediaConnectionState = CallConnectionState.Connecting;
            StatusMessage = "Connecting media…";
        }
        if (CurrentCall.CallerAccountId == _accountId && !_negotiationStarted)
            await StartOffererNegotiationAsync();
        else if (CurrentCall.CallerAccountId == _accountId)
            logger.LogDebug("Call {CallId} negotiation {NegotiationId}: duplicate CallAccepted ignored; negotiation already started.",
                value.CallId, _negotiationId);
        logger.LogDebug("Call {CallId} account {AccountId}: accepted. MediaConnectionState={MediaConnectionState}, NegotiationId={NegotiationId}.",
            value.CallId, _accountId, MediaConnectionState, _negotiationId);
        NotifyChanged();
    }

    private async Task ReceiveTerminalAsync(CallStateEvent value, string message)
    {
        if (CurrentCall?.Id != value.CallId && IncomingCall?.CallId != value.CallId) return;
        StatusMessage = message;
        await FinishAsync(clearMessage: false);
        NotifyChanged();
        await Task.Delay(1800);
        if (StatusMessage == message) { StatusMessage = null; NotifyChanged(); }
    }

    private Task ReceiveParticipantState(CallParticipantStateEvent value)
    {
        if (CurrentCall?.Id != value.CallId) return Task.CompletedTask;
        CurrentCall = CurrentCall with { Participants = CurrentCall.Participants.Select(participant =>
            participant.AccountId == value.AccountId
                ? participant with { IsMuted = value.IsMuted, IsDeafened = value.IsDeafened, ConnectionState = value.ConnectionState }
                : participant).ToList() };
        NotifyChanged();
        return Task.CompletedTask;
    }

    private Task ReceiveParticipantSpeaking(CallParticipantSpeakingEvent value)
    {
        if (CurrentCall?.Id != value.CallId) return Task.CompletedTask;
        SetParticipantSpeaking(value.AccountId, value.IsSpeaking);
        return Task.CompletedTask;
    }

    private async Task ReceiveMediaRetryRequestedAsync(CallStateEvent value)
    {
        if (CurrentCall?.Id != value.CallId || CurrentCall.CallerAccountId != _accountId) return;
        logger.LogDebug("Call {CallId}: caller received media retry request.", value.CallId);
        try { await RestartOffererAsync(); }
        catch (Exception exception) { await FailMediaAsync(MediaErrorMessage(exception)); }
    }

    private async Task ReceiveOfferAsync(WebRtcDescriptionEvent value)
    {
        _offerReceivedCount++;
        var duplicateSignal = !_receivedSignalIds.Add(value.SignalId);
        VoiceDiagnostic("RECEIVED Offer", value.CallId, value.SignalId, value.NegotiationGeneration,
            details: $"kind={value.NegotiationKind} offerReceivedCount={_offerReceivedCount} " +
                     $"senderPeerGeneration={value.SenderPeerGeneration} duplicateSignalId={duplicateSignal}");
        if (duplicateSignal)
        {
            VoiceDiagnostic("SignalId already processed -> ignoring Offer", value.CallId, value.SignalId,
                value.NegotiationGeneration);
            return;
        }
        if (IncomingCall?.CallId != value.CallId && CurrentCall?.Id != value.CallId)
        {
            // The server has already authorized and targeted this signal. Keep it if SignalR
            // callback scheduling delivered it just ahead of IncomingCall.
            if (_pendingSignalingCallId is not null && _pendingSignalingCallId != value.CallId) _pendingIce.Clear();
            _pendingSignalingCallId = value.CallId;
        }
        logger.LogDebug("Call {CallId} negotiation {NegotiationId} account {AccountId}: offer received from account {SenderAccountId}; active negotiation is {ActiveNegotiationId}.",
            value.CallId, value.NegotiationId, _accountId, value.SenderAccountId, _negotiationId);
        var offerCollision = false;
        if (_mediaReady && _negotiationId != value.NegotiationId)
        {
            var snapshot = await media.GetDiagnosticSnapshotAsync();
            offerCollision = snapshot?.SignalingState == "have-local-offer";
            var polite = _accountId is { } localAccount && localAccount.CompareTo(value.SenderAccountId) > 0;
            VoiceDiagnostic("OfferCollisionEvaluated", value.CallId, value.SignalId, value.NegotiationGeneration,
                details: $"kind={value.NegotiationKind} polite={polite} offerCollision={offerCollision} " +
                         $"signalingState={snapshot?.SignalingState}");
            if (offerCollision && !polite)
            {
                logger.LogDebug("Call {CallId}: glare offer {NegotiationId} ignored by deterministic impolite peer.",
                    value.CallId, value.NegotiationId);
                VoiceDiagnostic("OfferIgnored", value.CallId, value.SignalId, value.NegotiationGeneration,
                    details: "polite=false offerCollision=true");
                return;
            }
        }
        if (!offerCollision && _negotiationId is { } activeNegotiationId &&
            activeNegotiationId != value.NegotiationId && value.NegotiationGeneration < _negotiationGeneration)
        {
            logger.LogDebug("Call {CallId}: stale offer for negotiation {NegotiationId} ignored; active negotiation is {ActiveNegotiationId}.",
                value.CallId, value.NegotiationId, activeNegotiationId);
            return;
        }
        var offer = value.Description;
        logger.LogDebug("Call {CallId} account {AccountId}: offer metadata Type={DescriptionType}, SdpLength={SdpLength}.",
            value.CallId, _accountId, offer.Type, offer.Sdp?.Length ?? 0);
        if (_remoteDescriptionReady && _negotiationId == value.NegotiationId &&
            string.Equals(_appliedOfferSdp, offer.Sdp, StringComparison.Ordinal))
        {
            logger.LogDebug("Call {CallId} account {AccountId}: duplicate offer ignored; peer generation {PeerGeneration} remains active.",
                value.CallId, _accountId, _peerGeneration);
            return;
        }
        _negotiationId = value.NegotiationId;
        _negotiationGeneration = value.NegotiationGeneration;
        _remoteDescriptionReady = false;
        _pendingOffer = value;
        if (CurrentCall?.State == CallState.Active)
        {
            if (!_mediaReady)
            {
                await StartMediaAsync(CancellationToken.None);
                _negotiationId = value.NegotiationId;
                _pendingOffer = value;
                MediaConnectionState = CallConnectionState.Connecting;
                StatusMessage = "Connecting media…";
            }
            StartNegotiationTimeout(value.NegotiationId);
            await AnswerPendingOfferAsync();
        }
    }

    private async Task ReceiveAnswerAsync(WebRtcDescriptionEvent value)
    {
        if (CurrentCall?.Id != value.CallId) return;
        _answerReceivedCount++;
        var duplicateSignal = !_receivedSignalIds.Add(value.SignalId);
        VoiceDiagnostic("RECEIVED Answer", value.CallId, value.SignalId, value.NegotiationGeneration,
            details: $"answerReceivedCount={_answerReceivedCount} senderPeerGeneration={value.SenderPeerGeneration} duplicateSignalId={duplicateSignal}");
        if (duplicateSignal)
        {
            VoiceDiagnostic("SignalId already processed -> ignoring Answer", value.CallId, value.SignalId,
                value.NegotiationGeneration);
            return;
        }
        var alreadyProcessed = _processedAnswerNegotiations.Contains(value.NegotiationId);
        WebRtcDiagnosticSnapshot? before = null;
        if (_mediaReady)
        {
            try { before = await media.GetDiagnosticSnapshotAsync(); }
            catch (Exception exception) { logger.LogDebug(exception, "Call {CallId}: could not read signalingState before answer.", value.CallId); }
        }
        logger.LogDebug(
            "Call {CallId} negotiation {NegotiationId} account {AccountId} PeerGeneration {PeerGeneration}: answer received from account {SenderAccountId}; " +
            "signalingState before={SignalingState}; already processed={AlreadyProcessed}; active negotiation={ActiveNegotiationId}.",
            value.CallId, value.NegotiationId, _accountId, _peerGeneration, value.SenderAccountId,
            before?.SignalingState ?? "unavailable", alreadyProcessed, _negotiationId);
        var disposition = WebRtcSignalingGuard.ClassifyAnswer(
            _negotiationId, value.NegotiationId, alreadyProcessed, before?.SignalingState);
        if (disposition == RemoteAnswerDisposition.StaleNegotiation)
        {
            logger.LogDebug("Call {CallId}: stale answer for negotiation {NegotiationId} ignored; active negotiation is {ActiveNegotiationId}.",
                value.CallId, value.NegotiationId, _negotiationId);
            return;
        }
        if (disposition == RemoteAnswerDisposition.Duplicate)
        {
            VoiceDiagnostic("duplicate answer ignored: negotiation already processed", value.CallId, value.SignalId,
                value.NegotiationGeneration, details: $"duplicateSignalId={duplicateSignal}");
            logger.LogDebug("Call {CallId} negotiation {NegotiationId}: duplicate answer ignored; the current peer remains untouched.",
                value.CallId, value.NegotiationId);
            return;
        }
        if (disposition == RemoteAnswerDisposition.AlreadyApplied)
        {
            _processedAnswerNegotiations.Add(value.NegotiationId);
            VoiceDiagnostic("stale/duplicate answer ignored before setRemoteDescription because signalingState is stable",
                value.CallId, value.SignalId, value.NegotiationGeneration,
                details: $"duplicateSignalId={duplicateSignal} localPeerGeneration={_peerGeneration}");
            logger.LogDebug("Call {CallId} negotiation {NegotiationId}: answer arrived in stable state and was treated as already applied; the peer remains connected.",
                value.CallId, value.NegotiationId);
            return;
        }
        if (disposition == RemoteAnswerDisposition.InvalidState)
            throw new InvalidOperationException(
                $"A current WebRTC answer cannot be applied while signalingState is {before?.SignalingState}.");
        if (!_mediaReady)
        {
            logger.LogDebug("Call {CallId} negotiation {NegotiationId}: answer ignored because its peer generation has already been disposed.",
                value.CallId, value.NegotiationId);
            return;
        }
        logger.LogDebug("Call {CallId} account {AccountId}: answer metadata Type={DescriptionType}, SdpLength={SdpLength}.",
            value.CallId, _accountId, value.Description.Type, value.Description.Sdp?.Length ?? 0);
        var result = await media.ApplyAnswerAsync(value.NegotiationId, value.SignalId, value.Description);
        if (!result.Applied)
        {
            logger.LogDebug("Call {CallId} negotiation {NegotiationId}: answer ignored by peer state guard. SignalingState={SignalingState}, Reason={IgnoreReason}.",
                value.CallId, value.NegotiationId, result.SignalingState, result.IgnoreReason);
            if (result.IgnoreReason is "duplicate-answer" or "answer-already-applied")
                _processedAnswerNegotiations.Add(value.NegotiationId);
            return;
        }
        _processedAnswerNegotiations.Add(value.NegotiationId);
        _remoteDescriptionReady = true;
        logger.LogDebug("Call {CallId} negotiation {NegotiationId}: setRemoteDescription(answer) completed; signalingState after={SignalingState}.",
            value.CallId, value.NegotiationId, result.SignalingState);
        await FlushIceAsync();
    }

    private async Task ReceiveIceAsync(WebRtcIceCandidateEvent value)
    {
        _iceReceivedCount++;
        var duplicateSignal = !_receivedSignalIds.Add(value.SignalId);
        VoiceDiagnostic($"SIGNALR RECEIVED REMOTE ICE #{_iceReceivedCount}", value.CallId, value.SignalId, value.NegotiationGeneration,
            details: $"iceReceivedCount={_iceReceivedCount} senderPeerGeneration={value.SenderPeerGeneration} duplicateSignalId={duplicateSignal}");
        if (duplicateSignal)
        {
            VoiceDiagnostic("SignalId already processed -> ignoring IceCandidate", value.CallId, value.SignalId,
                value.NegotiationGeneration);
            return;
        }
        if (CurrentCall?.Id != value.CallId && IncomingCall?.CallId != value.CallId)
        {
            if (_pendingSignalingCallId is not null && _pendingSignalingCallId != value.CallId) return;
            _pendingSignalingCallId = value.CallId;
        }
        logger.LogDebug("Call {CallId} negotiation {NegotiationId} account {AccountId}: remote ICE candidate received from account {SenderAccountId}.",
            value.CallId, value.NegotiationId, _accountId, value.SenderAccountId);
        _remoteCandidatesReceived++;
        if (_negotiationId is { } currentNegotiationId &&
            (currentNegotiationId != value.NegotiationId || value.NegotiationGeneration != _negotiationGeneration))
        {
            VoiceDiagnostic("stale remote ICE ignored", value.CallId, value.SignalId, value.NegotiationGeneration,
                details: $"activeNegotiationId={currentNegotiationId} activeNegotiationGeneration={_negotiationGeneration} " +
                         $"senderPeerGeneration={value.SenderPeerGeneration}");
            return;
        }
        if (!_mediaReady || CurrentCall is null || !_remoteDescriptionReady || _negotiationId is null)
        {
            _pendingIce.Add(value);
            _remoteCandidatesQueued++;
            VoiceDiagnostic($"REMOTE ICE QUEUED #{_iceReceivedCount}", value.CallId, value.SignalId,
                value.NegotiationGeneration, details: $"queuedCount={_pendingIce.Count}");
        }
        else
        {
            try
            {
                VoiceDiagnostic($"ADDING REMOTE ICE #{_iceReceivedCount}", value.CallId, value.SignalId,
                    value.NegotiationGeneration);
                await media.AddIceCandidateAsync(value.SignalId, value.Candidate);
                _remoteCandidatesAdded++;
                VoiceDiagnostic($"addIceCandidate SUCCESS #{_iceReceivedCount}", value.CallId, value.SignalId,
                    value.NegotiationGeneration, details: $"remoteCandidatesAdded={_remoteCandidatesAdded}");
            }
            catch (Exception exception)
            {
                _remoteCandidateAddFailures++;
                VoiceDiagnostic($"addIceCandidate FAILED #{_iceReceivedCount}", value.CallId, value.SignalId,
                    value.NegotiationGeneration,
                    details: $"failureCount={_remoteCandidateAddFailures} name={exception.GetType().Name} message={exception.Message}");
                throw;
            }
        }
    }

    private async Task StartMediaAsync(CancellationToken cancellationToken)
    {
        var callId = CurrentCall?.Id ?? IncomingCall?.CallId
            ?? throw new InvalidOperationException("There is no call to initialize media for.");
        var configuration = await _connection!.InvokeAsync<CallMediaConfigurationDto>(
            VoiceCallHubContract.GetMediaConfiguration, callId, cancellationToken);
        if (configuration.Mode != MediaMode.DirectWebRtc)
            throw new InvalidOperationException("This client does not yet support the Node's configured media mode.");
        media.IceCandidateGenerated -= SendIceAsync;
        media.ConnectionStateChanged -= MediaConnectionChangedAsync;
        media.IceConnectionStateChanged -= MediaIceConnectionChangedAsync;
        media.SpeakingChanged -= LocalSpeakingChangedAsync;
        media.ScreenShareEnded -= MediaScreenShareEndedAsync;
        media.Error -= MediaErrorAsync;
        media.DiagnosticGenerated -= ForwardVoiceDiagnosticAsync;
        media.IceCandidateGenerated += SendIceAsync;
        media.ConnectionStateChanged += MediaConnectionChangedAsync;
        media.IceConnectionStateChanged += MediaIceConnectionChangedAsync;
        media.SpeakingChanged += LocalSpeakingChangedAsync;
        media.ScreenShareEnded += MediaScreenShareEndedAsync;
        media.Error += MediaErrorAsync;
        media.DiagnosticGenerated += ForwardVoiceDiagnosticAsync;
        var accountId = _accountId ?? throw new InvalidOperationException("The active call account is unavailable.");
        var callerAccountId = CurrentCall?.CallerAccountId ?? IncomingCall?.CallerAccountId;
        _mediaRole = callerAccountId == accountId ? "caller" : "callee";
        _peerGeneration++;
        ResetAttemptDiagnostics(preserveRemoteCandidates: true);
        _mediaFailureInProgress = false;
        var remoteAccountId = CurrentCall?.Participants.FirstOrDefault(value => value.AccountId != accountId)?.AccountId;
        await media.InitializeAsync(configuration,
            new CallMediaSessionContext(callId, accountId, _mediaRole, _peerGeneration, _negotiationId,
                _negotiationGeneration, remoteAccountId), cancellationToken);
        logger.LogDebug("Call {CallId} account {AccountId} role {Role}: PeerGeneration {PeerGeneration} created; getUserMedia succeeded; local audio track attached.",
            callId, accountId, _mediaRole, _peerGeneration);
        _mediaReady = true;
        _remoteDescriptionReady = false;
        MediaConnectionState = CallConnectionState.New;
        _heartbeatCancellation?.Cancel();
        _heartbeatCancellation?.Dispose();
        _heartbeatCancellation = new CancellationTokenSource();
        _ = HeartbeatAsync(_heartbeatCancellation.Token);
    }

    private async Task SendIceAsync(LocalIceCandidateSignal signal)
    {
        var callId = CurrentCall?.Id ?? IncomingCall?.CallId;
        var negotiationId = _negotiationId;
        _localCandidatesGenerated++;
        IncrementCandidateType(_localCandidateTypes, signal.Candidate.Candidate);
        VoiceDiagnostic($"DOTNET RECEIVED LOCAL ICE #{signal.Sequence}", callId, signal.SignalId,
            signal.NegotiationGeneration,
            details: $"callbackPeerGeneration={signal.PeerGeneration} localCandidateCount={_localCandidatesGenerated}");
        if (signal.PeerGeneration != _peerGeneration || signal.NegotiationGeneration != _negotiationGeneration)
        {
            VoiceDiagnostic($"stale local ICE #{signal.Sequence} ignored", callId, signal.SignalId,
                signal.NegotiationGeneration,
                details: $"callbackPeerGeneration={signal.PeerGeneration} currentPeerGeneration={_peerGeneration} " +
                         $"callbackNegotiationGeneration={signal.NegotiationGeneration} currentNegotiationGeneration={_negotiationGeneration}");
            return;
        }
        if (callId is not null && negotiationId is not null && IsSignalingConnected)
        {
            VoiceDiagnostic($"CLIENT SENDING ICE #{signal.Sequence} TO SERVER", callId, signal.SignalId,
                signal.NegotiationGeneration);
            await _connection!.InvokeAsync(VoiceCallHubContract.SendIceCandidate, callId.Value, negotiationId.Value,
                signal.NegotiationGeneration, signal.PeerGeneration, signal.SignalId, signal.Candidate);
            _localCandidatesSent++;
            VoiceDiagnostic($"CLIENT SENT ICE #{signal.Sequence} TO SERVER", callId, signal.SignalId,
                signal.NegotiationGeneration, details: $"localCandidatesSent={_localCandidatesSent}");
        }
        else VoiceDiagnostic($"LOCAL ICE #{signal.Sequence} NOT SENT", callId, signal.SignalId,
            signal.NegotiationGeneration,
            details: $"hasCallId={callId is not null} hasNegotiationId={negotiationId is not null} signalingConnected={IsSignalingConnected}");
    }

    private async Task MediaConnectionChangedAsync(CallConnectionState state)
    {
        MediaConnectionState = state;
        logger.LogDebug("Call {CallId} account {AccountId}: WebRTC connectionState changed to {ConnectionState}.",
            CurrentCall?.Id, _accountId, state);
        StatusMessage = state switch
        {
            CallConnectionState.Connected => "Connected",
            CallConnectionState.Connecting => "Connecting media…",
            CallConnectionState.Disconnected => "Connection interrupted…",
            CallConnectionState.Failed => "Connection failed",
            _ => StatusMessage
        };
        if (state == CallConnectionState.Connected)
        {
            logger.LogDebug("Call {CallId} account {AccountId} role {Role} PeerGeneration {PeerGeneration}: negotiation timeout cancelled because peer connected.",
                CurrentCall?.Id, _accountId, _mediaRole, _peerGeneration);
            CancelNegotiationTimeout("current peer connected");
        }
        if (CurrentCall is not null && IsSignalingConnected) await PublishParticipantStateAsync();
        NotifyChanged();
        if (state == CallConnectionState.Failed) await FailMediaAsync("Unable to establish the media connection.", "WEBRTC FAILED");
    }

    private Task MediaIceConnectionChangedAsync(string state)
    {
        if (state is "connected" or "completed") CancelNegotiationTimeout("IceConnected");
        return Task.CompletedTask;
    }

    // TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
    private async Task ForwardVoiceDiagnosticAsync(VoiceDiagnosticReport report)
    {
        if (!media.DiagnosticsEnabled || !IsSignalingConnected) return;
        var activeCallId = CurrentCall?.Id ?? IncomingCall?.CallId;
        if (activeCallId != report.CallId) return;
        try { await _connection!.InvokeAsync(VoiceCallHubContract.ReportDiagnostic, report); }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Call {CallId}: could not forward temporary voice diagnostic {Event}.",
                report.CallId, report.Event);
        }
    }

    private async Task MediaErrorAsync(string message)
    {
        logger.LogError("Call {CallId}: browser WebRTC error: {MediaError}", CurrentCall?.Id, message);
        // An established audio peer remains authoritative. A failed add/remove visual-media
        // operation is surfaced without disposing that peer; an actual terminal peer failure is
        // still handled by MediaConnectionChangedAsync.
        if (CurrentCall?.State == CallState.Active && MediaConnectionState == CallConnectionState.Connected)
        {
            ErrorMessage = $"Additional media operation failed: {message}";
            NotifyChanged();
        }
        else if (CurrentCall?.State == CallState.Active) await FailMediaAsync(message);
        else { ErrorMessage = message; NotifyChanged(); }
    }

    private async Task LocalSpeakingChangedAsync(bool isSpeaking)
    {
        if (_accountId is not { } accountId || CurrentCall is not { } call) return;
        SetParticipantSpeaking(accountId, isSpeaking);
        if (IsSignalingConnected)
        {
            try { await _connection!.InvokeAsync(VoiceCallHubContract.SetSpeaking, call.Id, isSpeaking); }
            catch (Exception exception) { logger.LogWarning(exception, "Call {CallId}: could not send speaking transition.", call.Id); }
        }
    }

    private Task MediaScreenShareEndedAsync(string reason) => StopScreenShareAsync(reason);

    private async Task PublishParticipantStateAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentCall is null || !IsSignalingConnected) return;
        var state = (IsMuted, IsDeafened, MediaConnectionState);
        if (_lastPublishedParticipantState == state) return;
        await _connection!.InvokeAsync(VoiceCallHubContract.SetParticipantState, CurrentCall.Id,
            IsMuted, IsDeafened, MediaConnectionState, cancellationToken);
        _lastPublishedParticipantState = state;
    }

    private async Task FlushIceAsync(CancellationToken cancellationToken = default)
    {
        if (!_mediaReady || !_remoteDescriptionReady || CurrentCall is null) return;
        var current = _pendingIce.Where(value => value.NegotiationId == _negotiationId &&
            value.NegotiationGeneration == _negotiationGeneration).ToList();
        VoiceDiagnostic($"flushing {current.Count} queued ICE candidates", CurrentCall.Id,
            negotiationGeneration: _negotiationGeneration);
        foreach (var signal in current)
        {
            try
            {
                VoiceDiagnostic("ADDING QUEUED REMOTE ICE", signal.CallId, signal.SignalId,
                    signal.NegotiationGeneration);
                await media.AddIceCandidateAsync(signal.SignalId, signal.Candidate, cancellationToken);
                _remoteCandidatesAdded++;
                VoiceDiagnostic("addIceCandidate SUCCESS (queued)", signal.CallId, signal.SignalId,
                    signal.NegotiationGeneration, details: $"remoteCandidatesAdded={_remoteCandidatesAdded}");
            }
            catch (Exception exception)
            {
                _remoteCandidateAddFailures++;
                VoiceDiagnostic("addIceCandidate FAILED (queued)", signal.CallId, signal.SignalId,
                    signal.NegotiationGeneration,
                    details: $"failureCount={_remoteCandidateAddFailures} name={exception.GetType().Name} message={exception.Message}");
                throw;
            }
        }
        _pendingIce.Clear();
    }

    private async Task AnswerPendingOfferAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingOffer is null || CurrentCall is null) return;
        var callId = CurrentCall.Id;
        var offer = _pendingOffer;
        if (_negotiationId != offer.NegotiationId) return;
        _createAnswerCount++;
        var signalId = Guid.NewGuid();
        VoiceDiagnostic("createAnswer invocation", callId, negotiationGeneration: _negotiationGeneration,
            details: $"createAnswerCount={_createAnswerCount}");
        var answer = await media.AcceptOfferAsync(offer.NegotiationId, offer.SignalId, signalId,
            offer.Description, cancellationToken);
        _remoteDescriptionReady = true;
        _appliedOfferSdp = offer.Description.Sdp;
        _pendingOffer = null;
        logger.LogDebug("Call {CallId} negotiation {NegotiationId}: setRemoteDescription(offer), createAnswer, and setLocalDescription(answer) completed.",
            callId, offer.NegotiationId);
        VoiceDiagnostic("SEND Answer", callId, signalId, _negotiationGeneration,
            details: $"createAnswerCount={_createAnswerCount}");
        await _connection!.InvokeAsync(VoiceCallHubContract.SendAnswer, callId, offer.NegotiationId,
            _negotiationGeneration, _peerGeneration, signalId, answer, cancellationToken);
        logger.LogDebug("Call {CallId} negotiation {NegotiationId}: answer sent exactly once.", callId, offer.NegotiationId);
        await FlushIceAsync(cancellationToken);
    }

    private async Task StartOffererNegotiationAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentCall is not { } call || call.CallerAccountId != _accountId || _negotiationStarted) return;
        if (!_mediaReady) await StartMediaAsync(cancellationToken);
        _negotiationId = Guid.NewGuid();
        _negotiationGeneration++;
        _negotiationStarted = true;
        _remoteDescriptionReady = false;
        MediaConnectionState = CallConnectionState.Connecting;
        StatusMessage = "Connecting media…";
        StartNegotiationTimeout(_negotiationId.Value);
        _createOfferCount++;
        var signalId = Guid.NewGuid();
        VoiceDiagnostic("createOffer invocation", call.Id, negotiationGeneration: _negotiationGeneration,
            details: $"createOfferCount={_createOfferCount}");
        var offer = await media.CreateOfferAsync(_negotiationId.Value, signalId, cancellationToken);
        logger.LogDebug("Call {CallId} negotiation {NegotiationId}: offer created and local description set exactly once.",
            call.Id, _negotiationId);
        VoiceDiagnostic("SEND Offer", call.Id, signalId, _negotiationGeneration);
        await _connection!.InvokeAsync(VoiceCallHubContract.SendOffer, call.Id, _negotiationId.Value,
            _negotiationGeneration, _peerGeneration, signalId, WebRtcNegotiationKind.Initial, offer, cancellationToken);
        logger.LogDebug("Call {CallId} negotiation {NegotiationId}: offer sent exactly once.", call.Id, _negotiationId);
    }

    private async Task StartRenegotiationUnsafeAsync(string reason, CancellationToken cancellationToken)
    {
        if (CurrentCall?.State != CallState.Active || !_mediaReady || !IsSignalingConnected) return;
        _negotiationId = Guid.NewGuid();
        _negotiationGeneration++;
        _remoteDescriptionReady = false;
        var signalId = Guid.NewGuid();
        _createOfferCount++;
        VoiceDiagnostic("RenegotiationRequested", CurrentCall.Id, signalId, _negotiationGeneration,
            details: $"kind=Renegotiation reason={reason} makingOffer=true");
        var offer = await media.CreateOfferAsync(_negotiationId.Value, signalId, cancellationToken);
        await _connection!.InvokeAsync(VoiceCallHubContract.SendOffer, CurrentCall.Id, _negotiationId.Value,
            _negotiationGeneration, _peerGeneration, signalId, WebRtcNegotiationKind.Renegotiation,
            offer, cancellationToken);
        VoiceDiagnostic("RenegotiationSucceeded", CurrentCall.Id, signalId, _negotiationGeneration,
            details: $"kind=Renegotiation reason={reason} makingOffer=false");
    }

    private async Task RestartOffererAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentCall is not { } call || call.CallerAccountId != _accountId) return;
        VoiceDiagnostic("RetryStarted", call.Id, negotiationGeneration: _negotiationGeneration + 1,
            details: $"oldPeerGeneration={_peerGeneration} newPeerGeneration={_peerGeneration + 1}");
        await ResetMediaAsync("retry replacement", cancellationToken);
        _negotiationId = null;
        _negotiationStarted = false;
        await StartMediaAsync(cancellationToken);
        await StartOffererNegotiationAsync(cancellationToken);
        NotifyChanged();
    }

    private async Task ResetMediaAsync(string reason, CancellationToken cancellationToken = default)
    {
        CancelNegotiationTimeout(reason);
        await media.CleanupAsync(reason, cancellationToken);
        _mediaReady = false;
        _remoteDescriptionReady = false;
        _appliedOfferSdp = null;
        _pendingOffer = null;
        _pendingIce.Clear();
        if (_accountId is { } accountId) SetParticipantSpeaking(accountId, false);
    }

    private async Task FailMediaAsync(string message, string summaryEvent = "WEBRTC OPERATION FAILED")
    {
        if (CurrentCall is null) { ErrorMessage = message; NotifyChanged(); return; }
        if (_mediaFailureInProgress) return;
        _mediaFailureInProgress = true;
        await LogWebRtcSummaryAsync(summaryEvent);
        var cleanupReason = summaryEvent switch
        {
            "WEBRTC NEGOTIATION TIMED OUT" => "negotiation timeout",
            "WEBRTC FAILED" => "terminal peer failure",
            _ => "signaling failure"
        };
        await ResetMediaAsync(cleanupReason);
        MediaConnectionState = CallConnectionState.Failed;
        StatusMessage = "Connection failed";
        ErrorMessage = message;
        if (_accountId is { } accountId)
        {
            CurrentCall = CurrentCall with { Participants = CurrentCall.Participants.Select(participant =>
                participant.AccountId == accountId ? participant with { ConnectionState = CallConnectionState.Failed, IsSpeaking = false } : participant).ToList() };
        }
        if (IsSignalingConnected)
        {
            try { await PublishParticipantStateAsync(); }
            catch (Exception exception) { logger.LogWarning(exception, "Call {CallId}: could not publish failed media state.", CurrentCall.Id); }
        }
        NotifyChanged();
    }

    private void StartNegotiationTimeout(Guid negotiationId)
    {
        if (MediaConnectionState == CallConnectionState.Connected) return;
        CancelNegotiationTimeout("new timeout replacing previous timeout");
        _negotiationTimeoutPeerGeneration = _peerGeneration;
        _negotiationTimeoutId = negotiationId;
        var cancellation = _negotiationCancellation = new CancellationTokenSource();
        VoiceDiagnostic("NegotiationTimeoutStarted", negotiationGeneration: _negotiationGeneration,
            details: $"timeoutGeneration={_peerGeneration} timeoutToken={negotiationId}");
        var callId = CurrentCall?.Id ?? IncomingCall?.CallId;
        if (callId is { } id)
            _ = ForwardVoiceDiagnosticAsync(new VoiceDiagnosticReport(id, "NegotiationTimeoutStarted",
                _peerGeneration, _negotiationGeneration, Count: 18, Reason: "InitialNegotiation"));
        _ = WaitForNegotiationAsync(cancellation, negotiationId, _peerGeneration);
    }

    private async Task WaitForNegotiationAsync(CancellationTokenSource cancellation, Guid negotiationId, int peerGeneration)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(18), cancellation.Token);
            await HandleNegotiationTimeoutAsync(cancellation, negotiationId, peerGeneration);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task HandleNegotiationTimeoutAsync(CancellationTokenSource cancellation, Guid negotiationId,
        int peerGeneration)
    {
        await _gate.WaitAsync();
        try
        {
            var current = ReferenceEquals(_negotiationCancellation, cancellation) &&
                          _negotiationTimeoutId == negotiationId && _negotiationId == negotiationId &&
                          _negotiationTimeoutPeerGeneration == peerGeneration && _peerGeneration == peerGeneration &&
                          CurrentCall?.State == CallState.Active && MediaConnectionState == CallConnectionState.Connecting;
            VoiceDiagnostic(current ? "NegotiationTimeoutFired" : "StaleNegotiationTimeoutIgnored",
                negotiationGeneration: _negotiationGeneration,
                details: $"timeoutGeneration={peerGeneration} currentGeneration={_peerGeneration} timeoutToken={negotiationId}");
            if (current)
            {
                WebRtcDiagnosticSnapshot? snapshot = null;
                try { snapshot = await media.GetDiagnosticSnapshotAsync(); }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Call {CallId}: timeout could not collect the current WebRTC snapshot.",
                        CurrentCall?.Id);
                }
                var connected = snapshot is not null && (snapshot.ConnectionState == "connected" ||
                    snapshot.IceConnectionState is "connected" or "completed" || snapshot.MediaTrafficDetected);
                if (connected)
                {
                    VoiceDiagnostic("TimeoutSuppressedMediaIsConnected", negotiationGeneration: _negotiationGeneration,
                        details: $"connectionState={snapshot!.ConnectionState} iceConnectionState={snapshot.IceConnectionState} " +
                                 $"mediaTraffic={snapshot.MediaTrafficDetected}");
                    MediaConnectionState = CallConnectionState.Connected;
                    StatusMessage = "Connected";
                    CancelNegotiationTimeout("PeerConnected");
                    if (CurrentCall is not null && IsSignalingConnected) await PublishParticipantStateAsync();
                    NotifyChanged();
                    return;
                }
                await FailMediaAsync("Unable to establish the media connection.", "WEBRTC NEGOTIATION TIMED OUT");
            }
        }
        finally { _gate.Release(); }
    }

    private void CancelNegotiationTimeout(string reason)
    {
        if (_negotiationCancellation is not null)
        {
            VoiceDiagnostic("NegotiationTimeoutCancelled", negotiationGeneration: _negotiationGeneration,
                details: $"reason={reason} timeoutGeneration={_negotiationTimeoutPeerGeneration} timeoutToken={_negotiationTimeoutId}");
            var callId = CurrentCall?.Id ?? IncomingCall?.CallId;
            if (callId is { } id)
                _ = ForwardVoiceDiagnosticAsync(new VoiceDiagnosticReport(id, "NegotiationTimeoutCancelled",
                    _peerGeneration, _negotiationGeneration, Reason: CanonicalCleanupReason(reason)));
        }
        _negotiationCancellation?.Cancel();
        _negotiationCancellation?.Dispose();
        _negotiationCancellation = null;
        _negotiationTimeoutId = null;
        _negotiationTimeoutPeerGeneration = 0;
    }

    private async Task LogWebRtcSummaryAsync(string eventName)
    {
        if (CurrentCall is not { } call) return;
        WebRtcDiagnosticSnapshot? snapshot = null;
        try { snapshot = await media.GetDiagnosticSnapshotAsync(); }
        catch (Exception exception) { logger.LogDebug(exception, "Call {CallId}: could not read the WebRTC diagnostic snapshot.", call.Id); }
        logger.LogError(
            "Call {CallId} account {AccountId} role {Role} PeerGeneration {PeerGeneration}: {Event}. " +
            "SignalingState={SignalingState}, IceGatheringState={IceGatheringState}, IceConnectionState={IceConnectionState}, ConnectionState={ConnectionState}, " +
            "NegotiationGeneration={NegotiationGeneration}, LocalDescription={LocalDescription}, RemoteDescription={RemoteDescription}, " +
            "CreateOfferCount={CreateOfferCount}, CreateAnswerCount={CreateAnswerCount}, OfferReceivedCount={OfferReceivedCount}, " +
            "AnswerReceivedCount={AnswerReceivedCount}, IceReceivedCount={IceReceivedCount}, NegotiationNeededCount={NegotiationNeededCount}, " +
            "LocalGenerated={LocalGenerated}, LocalSent={LocalSent}, LocalTypes={LocalTypes}, RemoteReceived={RemoteReceived}, " +
            "RemoteAdded={RemoteAdded}, RemoteAddFailures={RemoteAddFailures}, QueuedRemote={QueuedRemote}, " +
            "TimeoutGeneration={TimeoutGeneration}, CurrentGeneration={CurrentGeneration}, SelectedPair={SelectedLocalType}/{SelectedRemoteType}/{SelectedProtocol}, " +
            "StatsLocalCandidates={StatsLocalCandidates}, StatsRemoteCandidates={StatsRemoteCandidates}, CandidatePairs={CandidatePairs}, PairSummary={PairSummary}.",
            call.Id, _accountId, _mediaRole, _peerGeneration, eventName,
            snapshot?.SignalingState ?? "unavailable", snapshot?.IceGatheringState ?? "unavailable",
            snapshot?.IceConnectionState ?? "unavailable", snapshot?.ConnectionState ?? "unavailable",
            _negotiationGeneration, snapshot?.LocalDescriptionType ?? "none", snapshot?.RemoteDescriptionType ?? "none",
            _createOfferCount, _createAnswerCount, _offerReceivedCount, _answerReceivedCount, _iceReceivedCount,
            snapshot?.NegotiationNeededCount ?? 0,
            _localCandidatesGenerated, _localCandidatesSent, CandidateTypeSummary(_localCandidateTypes),
            _remoteCandidatesReceived, _remoteCandidatesAdded, _remoteCandidateAddFailures,
            snapshot?.QueuedRemoteCandidateCount ?? _pendingIce.Count,
            _negotiationTimeoutPeerGeneration, _peerGeneration,
            snapshot?.SelectedLocalCandidateType ?? "none", snapshot?.SelectedRemoteCandidateType ?? "none",
            snapshot?.SelectedCandidateProtocol ?? "none", snapshot?.StatsLocalCandidateCount ?? 0,
            snapshot?.StatsRemoteCandidateCount ?? 0, snapshot?.StatsCandidatePairCount ?? 0,
            snapshot?.CandidatePairSummary ?? "none");
        await ForwardVoiceDiagnosticAsync(new VoiceDiagnosticReport(call.Id, "VoiceFailureSnapshot",
            _peerGeneration, _negotiationGeneration,
            SignalingState: snapshot?.SignalingState, IceGatheringState: snapshot?.IceGatheringState,
            IceConnectionState: snapshot?.IceConnectionState, ConnectionState: snapshot?.ConnectionState,
            LocalDescriptionType: snapshot?.LocalDescriptionType, RemoteDescriptionType: snapshot?.RemoteDescriptionType,
            Reason: CanonicalVoiceReason(eventName), OffersCreated: _createOfferCount, OffersReceived: _offerReceivedCount,
            AnswersCreated: _createAnswerCount, AnswersReceived: _answerReceivedCount,
            LocalIceGenerated: _localCandidatesGenerated, LocalIceSent: _localCandidatesSent,
            RemoteIceReceived: _remoteCandidatesReceived, RemoteIceQueued: _remoteCandidatesQueued,
            RemoteIceAdded: _remoteCandidatesAdded, RemoteIceAddFailures: _remoteCandidateAddFailures,
            RemoteTrackReceived: snapshot?.RemoteTrackReceived, RemoteAudioPlaySucceeded: snapshot?.RemoteAudioPlaySucceeded,
            MediaTrafficDetected: snapshot?.MediaTrafficDetected, LocalCandidateStats: snapshot?.StatsLocalCandidateCount,
            RemoteCandidateStats: snapshot?.StatsRemoteCandidateCount, CandidatePairStats: snapshot?.StatsCandidatePairCount,
            SucceededCandidatePairs: snapshot?.StatsSucceededCandidatePairCount,
            NominatedPairExists: snapshot?.StatsNominatedPairExists, SelectedPairExists: snapshot?.StatsSelectedPairExists,
            LocalCandidateType: snapshot?.SelectedLocalCandidateType, RemoteCandidateType: snapshot?.SelectedRemoteCandidateType,
            Protocol: snapshot?.SelectedCandidateProtocol, PacketsSent: snapshot?.PacketsSent,
            PacketsReceived: snapshot?.PacketsReceived, PacketsLost: snapshot?.PacketsLost,
            BytesSent: snapshot?.BytesSent, BytesReceived: snapshot?.BytesReceived));
    }

    private void ResetAttemptDiagnostics(bool preserveRemoteCandidates)
    {
        _localCandidatesGenerated = 0;
        _localCandidatesSent = 0;
        _remoteCandidatesReceived = preserveRemoteCandidates ? _pendingIce.Count : 0;
        _remoteCandidatesQueued = preserveRemoteCandidates ? _pendingIce.Count : 0;
        _remoteCandidatesAdded = 0;
        _remoteCandidateAddFailures = 0;
        _localCandidateTypes.Clear();
    }

    private void ResetCallDiagnostics()
    {
        _offerReceivedCount = 0;
        _answerReceivedCount = 0;
        _iceReceivedCount = 0;
        _createOfferCount = 0;
        _createAnswerCount = 0;
        _acceptInvokedCount = 0;
        _callAcceptedReceivedCount = 0;
        _negotiationGeneration = 0;
        _lastPublishedParticipantState = null;
        _receivedSignalIds.Clear();
    }

    // TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
    private void VoiceDiagnostic(string eventName, Guid? callId = null, Guid? signalId = null,
        int? negotiationGeneration = null, int? registrationCount = null, string? details = null)
    {
        if (!media.DiagnosticsEnabled) return;
        logger.LogDebug(
            "VOICE DIAGNOSTIC {Event}: CallClientServiceInstance={ServiceInstanceId} CallId={CallId} AccountId={AccountId} " +
            "Role={Role} PeerGeneration={PeerGeneration} NegotiationGeneration={NegotiationGeneration} " +
            "SignalId={SignalId} RegistrationCount={RegistrationCount} Details={Details}",
            eventName, _instanceId, callId ?? CurrentCall?.Id ?? IncomingCall?.CallId, _accountId, _mediaRole,
            _peerGeneration, negotiationGeneration ?? _negotiationGeneration, signalId, registrationCount, details);
        var diagnosticCallId = callId ?? CurrentCall?.Id ?? IncomingCall?.CallId;
        if (diagnosticCallId is { } id)
            _ = ForwardVoiceDiagnosticAsync(new VoiceDiagnosticReport(id, CanonicalVoiceEvent(eventName),
                _peerGeneration, negotiationGeneration ?? _negotiationGeneration, signalId));
    }

    private static string CanonicalVoiceEvent(string value)
    {
        if (value.StartsWith("RECEIVED Offer", StringComparison.Ordinal)) return "OfferReceivedByClient";
        if (value.StartsWith("RECEIVED Answer", StringComparison.Ordinal)) return "AnswerReceivedByClient";
        if (value.StartsWith("SIGNALR RECEIVED REMOTE ICE", StringComparison.Ordinal)) return "IceReceivedByClient";
        if (value.StartsWith("REMOTE ICE QUEUED", StringComparison.Ordinal)) return "IceQueued";
        if (value.StartsWith("ADDING", StringComparison.Ordinal)) return "IceAddStarted";
        if (value.StartsWith("addIceCandidate SUCCESS", StringComparison.Ordinal)) return "IceAddSucceeded";
        if (value.StartsWith("addIceCandidate FAILED", StringComparison.Ordinal)) return "IceAddFailed";
        if (value.StartsWith("CLIENT SENDING ICE", StringComparison.Ordinal)) return "IceSending";
        if (value.StartsWith("SEND Offer", StringComparison.Ordinal)) return "OfferSending";
        if (value.StartsWith("SEND Answer", StringComparison.Ordinal)) return "AnswerSending";
        if (value.Contains("stale", StringComparison.OrdinalIgnoreCase)) return "StaleSignalIgnored";
        if (value.Contains("duplicate", StringComparison.OrdinalIgnoreCase)) return "DuplicateOrStaleAnswerIgnored";
        var characters = value.Where(char.IsLetterOrDigit).ToArray();
        return characters.Length == 0 ? "ClientDiagnostic" : new string(characters)[..Math.Min(64, characters.Length)];
    }

    private static string CanonicalVoiceReason(string value) => value switch
    {
        "WEBRTC NEGOTIATION TIMED OUT" => "NegotiationTimeout",
        "WEBRTC FAILED" => "TerminalPeerFailure",
        _ => "SignalError"
    };

    private static string CanonicalCleanupReason(string value) => value switch
    {
        "current peer connected" or "PeerConnected" => "PeerConnected",
        "IceConnected" => "IceConnected",
        "call finished" => "CallEnded",
        "retry replacement" or "callee retry replacement" => "Retry",
        "peer replacement during initialization" => "PeerReplaced",
        _ => "PeerReplaced"
    };

    private static int CreateDiagnosticInstance(ILogger logger, bool diagnosticsEnabled)
    {
        var instanceId = Interlocked.Increment(ref _nextInstanceId);
        // TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
        if (diagnosticsEnabled)
            logger.LogDebug(
                "VOICE DIAGNOSTIC CallClientService instance {ServiceInstanceId} created: CallId={CallId} AccountId={AccountId} " +
                "Role={Role} PeerGeneration={PeerGeneration} NegotiationGeneration={NegotiationGeneration}",
                instanceId, null, null, "unknown", 0, 0);
        return instanceId;
    }

    private static void IncrementCandidateType(Dictionary<string, int> counts, string candidate)
    {
        var parts = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var protocol = parts.Length > 2 ? parts[2].ToLowerInvariant() : "unknown";
        var typeIndex = Array.FindIndex(parts, value => string.Equals(value, "typ", StringComparison.OrdinalIgnoreCase));
        var type = typeIndex >= 0 && typeIndex + 1 < parts.Length ? parts[typeIndex + 1].ToLowerInvariant() : "unknown";
        var key = $"{type}/{protocol}";
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }

    private static string CandidateTypeSummary(Dictionary<string, int> counts) => counts.Count == 0
        ? "none"
        : string.Join(", ", counts.OrderBy(value => value.Key).Select(value => $"{value.Value} {value.Key}"));

    private void SetParticipantSpeaking(Guid accountId, bool isSpeaking)
    {
        if (CurrentCall is null) return;
        CurrentCall = CurrentCall with { Participants = CurrentCall.Participants.Select(participant =>
            participant.AccountId == accountId ? participant with { IsSpeaking = isSpeaking } : participant).ToList() };
        NotifyChanged();
    }

    private void ApplyPublishedStream(PublishedVoiceStreamDto stream)
    {
        _publishedStreams.RemoveAll(value => value.StreamId == stream.StreamId ||
            value.OwnerAccountId == stream.OwnerAccountId && value.Kind == stream.Kind);
        _publishedStreams.Add(stream);
        NotifyChanged();
    }

    private void ApplyEndedStream(Guid streamId)
    {
        _publishedStreams.RemoveAll(value => value.StreamId == streamId);
        if (WatchedStream?.StreamId == streamId) WatchedStream = null;
        NotifyChanged();
    }

    private async Task RestoreCurrentCallAsync()
    {
        if (!IsSignalingConnected) return;
        var restored = await _connection!.InvokeAsync<CallSessionDto?>(VoiceCallHubContract.GetCurrent);
        if (restored is null && (CurrentCall is not null || IncomingCall is not null)) await FinishAsync();
        else if (restored is not null && CurrentCall?.Id == restored.Id) { CurrentCall = restored; NotifyChanged(); }
        else if (restored is not null && restored.State == CallState.Ringing && restored.CallerAccountId != _accountId)
        {
            var caller = restored.Participants.Single(value => value.AccountId == restored.CallerAccountId);
            IncomingCall = new(restored.Id, restored.DirectConversationId!.Value, caller.AccountId,
                caller.DisplayName, restored.CreatedAt, restored.ExpiresAt);
            NotifyChanged();
        }
        else if (restored is not null)
        {
            // A fresh page cannot safely reconstruct the prior RTCPeerConnection. Brief SignalR
            // reconnects keep CurrentCall and take the branch above; a full refresh ends cleanly.
            await TryInvokeAsync(VoiceCallHubContract.HangUp, restored.Id, CancellationToken.None);
            StatusMessage = "Call ended after the page reconnected";
            await FinishAsync(clearMessage: false);
        }
    }

    private async Task FinishAsync(bool clearMessage = true)
    {
        CancelNegotiationTimeout("call finished");
        _heartbeatCancellation?.Cancel();
        _heartbeatCancellation?.Dispose();
        _heartbeatCancellation = null;
        await media.CleanupAsync("call finished");
        _mediaReady = false; _remoteDescriptionReady = false; _pendingOffer = null; _pendingSignalingCallId = null; _pendingIce.Clear();
        _negotiationId = null; _negotiationStarted = false; _processedAnswerNegotiations.Clear();
        _lastPublishedParticipantState = null;
        _publishedStreams.Clear();
        WatchedStream = null;
        MediaConnectionState = CallConnectionState.Closed;
        CurrentCall = null; IncomingCall = null; IsMuted = false; IsDeafened = false;
        if (clearMessage) { StatusMessage = null; ErrorMessage = null; }
        NotifyChanged();
    }

    private async Task TryInvokeAsync(string method, Guid callId, CancellationToken cancellationToken)
    {
        if (!IsSignalingConnected) return;
        try { await _connection!.InvokeAsync(method, callId, cancellationToken); }
        catch (Exception exception) { logger.LogWarning(exception, "Could not send {CallAction} for call {CallId}.", method, callId); }
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                if (CurrentCall is not null && IsSignalingConnected)
                    await _connection!.InvokeAsync(VoiceCallHubContract.Heartbeat, CurrentCall.Id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogWarning(exception, "Voice call signaling heartbeat failed."); }
    }

    private async Task RunHandlerAsync(Func<Task> action, bool preserveCallOnFailure = false)
    {
        await _signalingGate.WaitAsync();
        await _gate.WaitAsync();
        try { await action(); }
        catch (Exception exception)
        {
            logger.LogError(exception, "Voice call event handling failed.");
            if (CurrentCall?.State == CallState.Active && !preserveCallOnFailure)
                await FailMediaAsync(MediaErrorMessage(exception));
            else if (CurrentCall?.State == CallState.Active)
            {
                ErrorMessage = $"Screen media negotiation failed: {MediaErrorMessage(exception)}";
                NotifyChanged();
            }
            else { ErrorMessage = MediaErrorMessage(exception); NotifyChanged(); }
        }
        finally
        {
            _gate.Release();
            _signalingGate.Release();
        }
    }

    private static string MediaErrorMessage(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("NotAllowedError", StringComparison.OrdinalIgnoreCase)) return "Microphone permission was denied.";
        if (message.Contains("NotFoundError", StringComparison.OrdinalIgnoreCase)) return "No microphone is available.";
        if (message.Contains("NotReadableError", StringComparison.OrdinalIgnoreCase)) return "The microphone is already in use or unavailable.";
        return message;
    }

    private void NotifyChanged() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        VoiceDiagnostic("CallClientService disposed");
        await FinishAsync();
        if (_connection is not null)
        {
            DisposeHandlerRegistrations();
        }
        await media.DisposeAsync();
        _signalingGate.Dispose();
        _gate.Dispose();
    }
}
