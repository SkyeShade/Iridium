using Iridium.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Iridium.Client.Core;

public sealed class CallClientService(NodeSession session, ICallMediaService media, ILogger<CallClientService> logger)
    : IAsyncDisposable
{
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
    private bool _negotiationStarted;
    private readonly HashSet<Guid> _processedAnswerNegotiations = [];
    private int _localCandidatesGenerated;
    private int _localCandidatesSent;
    private int _remoteCandidatesReceived;
    private int _remoteCandidatesAdded;
    private int _remoteCandidateAddFailures;
    private string _mediaRole = "unknown";
    private string? _appliedOfferSdp;
    private bool _mediaFailureInProgress;
    private int _negotiationTimeoutPeerGeneration;
    private Guid? _negotiationTimeoutId;
    private bool _disposed;

    public CallSessionDto? CurrentCall { get; private set; }
    public IncomingCallEvent? IncomingCall { get; private set; }
    public string? StatusMessage { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool IsMuted { get; private set; }
    public bool IsDeafened { get; private set; }
    public CallConnectionState MediaConnectionState { get; private set; } = CallConnectionState.New;
    public bool CanRetry => CurrentCall?.State == CallState.Active && MediaConnectionState == CallConnectionState.Failed;
    public bool IsSignalingConnected => _connection?.State == HubConnectionState.Connected;
    public Guid? AccountId => _accountId;
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
        finally { _gate.Release(); }
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
                await ResetMediaAsync(cancellationToken);
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
                await _connection.DisposeAsync();
            }
            _connection = null; _node = null; _accountId = null;
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var client = session.AuthorizedClient;
        var accountId = session.Account?.Id ?? throw new InvalidOperationException("Log in before using voice calls.");
        if (_connection is not null && _node == client.NodeAddress && _accountId == accountId)
        {
            if (_connection.State == HubConnectionState.Connected) return;
            if (_connection.State == HubConnectionState.Disconnected) { await _connection.StartAsync(cancellationToken); return; }
            throw new InvalidOperationException("Voice-call signaling is reconnecting. Please wait a moment.");
        }

        if (_connection is not null)
        {
            DisposeHandlerRegistrations();
            await _connection.DisposeAsync();
        }
        _node = client.NodeAddress; _accountId = accountId;
        var connection = new HubConnectionBuilder().WithUrl(new Uri(client.NodeAddress, "hubs/chat"), options =>
            options.AccessTokenProvider = () => Task.FromResult(client.AccessToken)).WithAutomaticReconnect().Build();
        _connection = connection;
        RegisterHandlers(connection);
        await connection.StartAsync(cancellationToken);
        await RestoreCurrentCallAsync();
    }

    private void RegisterHandlers(HubConnection connection)
    {
        DisposeHandlerRegistrations();
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
        _handlerRegistrations.Add(connection.On<WebRtcDescriptionEvent>(VoiceCallHubContract.Offer, value => RunHandlerAsync(() => ReceiveOfferAsync(value))));
        _handlerRegistrations.Add(connection.On<WebRtcDescriptionEvent>(VoiceCallHubContract.Answer, value => RunHandlerAsync(() => ReceiveAnswerAsync(value))));
        _handlerRegistrations.Add(connection.On<WebRtcIceCandidateEvent>(VoiceCallHubContract.IceCandidate, value => RunHandlerAsync(() => ReceiveIceAsync(value))));
        logger.LogDebug("Registered one WebRtcAnswer handler on the active call signaling connection; {HandlerCount} total call handlers are active.",
            _handlerRegistrations.Count);
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
        foreach (var registration in _handlerRegistrations) registration.Dispose();
        _handlerRegistrations.Clear();
    }

    private Task ReceiveIncoming(IncomingCallEvent incoming)
    {
        if (CurrentCall is not null || IncomingCall is not null) return Task.CompletedTask;
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
        CurrentCall = CurrentCall with { State = CallState.Active };
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
        if (IncomingCall?.CallId != value.CallId && CurrentCall?.Id != value.CallId)
        {
            // The server has already authorized and targeted this signal. Keep it if SignalR
            // callback scheduling delivered it just ahead of IncomingCall.
            if (_pendingSignalingCallId is not null && _pendingSignalingCallId != value.CallId) _pendingIce.Clear();
            _pendingSignalingCallId = value.CallId;
        }
        logger.LogDebug("Call {CallId} negotiation {NegotiationId} account {AccountId}: offer received from account {SenderAccountId}; active negotiation is {ActiveNegotiationId}.",
            value.CallId, value.NegotiationId, _accountId, value.SenderAccountId, _negotiationId);
        if (_negotiationId is { } activeNegotiationId && activeNegotiationId != value.NegotiationId)
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
        _pendingOffer = value;
        if (CurrentCall?.State == CallState.Active)
        {
            if (!_mediaReady || _remoteDescriptionReady)
            {
                await ResetMediaAsync();
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
            logger.LogDebug("Call {CallId} negotiation {NegotiationId}: duplicate answer ignored; the current peer remains untouched.",
                value.CallId, value.NegotiationId);
            return;
        }
        if (disposition == RemoteAnswerDisposition.AlreadyApplied)
        {
            _processedAnswerNegotiations.Add(value.NegotiationId);
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
        var result = await media.ApplyAnswerAsync(value.NegotiationId, value.Description);
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
        if (CurrentCall?.Id != value.CallId && IncomingCall?.CallId != value.CallId)
        {
            if (_pendingSignalingCallId is not null && _pendingSignalingCallId != value.CallId) return;
            _pendingSignalingCallId = value.CallId;
        }
        logger.LogDebug("Call {CallId} negotiation {NegotiationId} account {AccountId}: remote ICE candidate received from account {SenderAccountId}.",
            value.CallId, value.NegotiationId, _accountId, value.SenderAccountId);
        _remoteCandidatesReceived++;
        if (_negotiationId is { } currentNegotiationId && currentNegotiationId != value.NegotiationId)
        {
            logger.LogDebug("Call {CallId}: stale ICE candidate for negotiation {NegotiationId} ignored; active negotiation is {ActiveNegotiationId}.",
                value.CallId, value.NegotiationId, currentNegotiationId);
            return;
        }
        if (!_mediaReady || CurrentCall is null || !_remoteDescriptionReady || _negotiationId is null)
        {
            _pendingIce.Add(value);
            logger.LogDebug("Call {CallId}: ICE candidate queued until remote description exists ({QueuedCount} queued).",
                value.CallId, _pendingIce.Count);
        }
        else
        {
            try
            {
                await media.AddIceCandidateAsync(value.Candidate);
                _remoteCandidatesAdded++;
                logger.LogDebug("Call {CallId}: ICE candidate successfully added.", value.CallId);
            }
            catch
            {
                _remoteCandidateAddFailures++;
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
        media.SpeakingChanged -= LocalSpeakingChangedAsync;
        media.Error -= MediaErrorAsync;
        media.IceCandidateGenerated += SendIceAsync;
        media.ConnectionStateChanged += MediaConnectionChangedAsync;
        media.SpeakingChanged += LocalSpeakingChangedAsync;
        media.Error += MediaErrorAsync;
        var accountId = _accountId ?? throw new InvalidOperationException("The active call account is unavailable.");
        var callerAccountId = CurrentCall?.CallerAccountId ?? IncomingCall?.CallerAccountId;
        _mediaRole = callerAccountId == accountId ? "caller" : "callee";
        _peerGeneration++;
        ResetAttemptDiagnostics(preserveRemoteCandidates: true);
        _mediaFailureInProgress = false;
        await media.InitializeAsync(configuration,
            new CallMediaSessionContext(callId, accountId, _mediaRole, _peerGeneration, _negotiationId), cancellationToken);
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

    private async Task SendIceAsync(WebRtcIceCandidate candidate)
    {
        var callId = CurrentCall?.Id ?? IncomingCall?.CallId;
        var negotiationId = _negotiationId;
        _localCandidatesGenerated++;
        IncrementCandidateType(_localCandidateTypes, candidate.Candidate);
        if (callId is not null && negotiationId is not null && IsSignalingConnected)
        {
            logger.LogDebug("Call {CallId} account {AccountId}: ICE candidate generated; forwarding without candidate contents.",
                callId, _accountId);
            await _connection!.InvokeAsync(VoiceCallHubContract.SendIceCandidate, callId.Value, negotiationId.Value, candidate);
            _localCandidatesSent++;
            logger.LogDebug("Call {CallId}: ICE candidate sent.", callId);
        }
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
            CancelNegotiationTimeout();
        }
        if (CurrentCall is not null && IsSignalingConnected) await PublishParticipantStateAsync();
        NotifyChanged();
        if (state == CallConnectionState.Failed) await FailMediaAsync("The WebRTC connection failed.", "WEBRTC FAILED");
    }

    private async Task MediaErrorAsync(string message)
    {
        logger.LogError("Call {CallId}: browser WebRTC error: {MediaError}", CurrentCall?.Id, message);
        if (CurrentCall?.State == CallState.Active) await FailMediaAsync(message);
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

    private async Task PublishParticipantStateAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentCall is null || !IsSignalingConnected) return;
        await _connection!.InvokeAsync(VoiceCallHubContract.SetParticipantState, CurrentCall.Id,
            IsMuted, IsDeafened, MediaConnectionState, cancellationToken);
    }

    private async Task FlushIceAsync(CancellationToken cancellationToken = default)
    {
        if (!_mediaReady || !_remoteDescriptionReady || CurrentCall is null) return;
        foreach (var signal in _pendingIce.Where(value => value.NegotiationId == _negotiationId).ToList())
        {
            try
            {
                await media.AddIceCandidateAsync(signal.Candidate, cancellationToken);
                _remoteCandidatesAdded++;
                logger.LogDebug("Call {CallId}: queued ICE candidate successfully flushed and added.", CurrentCall.Id);
            }
            catch
            {
                _remoteCandidateAddFailures++;
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
        var answer = await media.AcceptOfferAsync(offer.NegotiationId, offer.Description, cancellationToken);
        _remoteDescriptionReady = true;
        _appliedOfferSdp = offer.Description.Sdp;
        _pendingOffer = null;
        logger.LogDebug("Call {CallId} negotiation {NegotiationId}: setRemoteDescription(offer), createAnswer, and setLocalDescription(answer) completed.",
            callId, offer.NegotiationId);
        await _connection!.InvokeAsync(VoiceCallHubContract.SendAnswer, callId, offer.NegotiationId, answer, cancellationToken);
        logger.LogDebug("Call {CallId} negotiation {NegotiationId}: answer sent exactly once.", callId, offer.NegotiationId);
        await FlushIceAsync(cancellationToken);
    }

    private async Task StartOffererNegotiationAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentCall is not { } call || call.CallerAccountId != _accountId || _negotiationStarted) return;
        if (!_mediaReady) await StartMediaAsync(cancellationToken);
        _negotiationId = Guid.NewGuid();
        _negotiationStarted = true;
        _remoteDescriptionReady = false;
        MediaConnectionState = CallConnectionState.Connecting;
        StatusMessage = "Connecting media…";
        StartNegotiationTimeout(_negotiationId.Value);
        var offer = await media.CreateOfferAsync(_negotiationId.Value, cancellationToken);
        logger.LogDebug("Call {CallId} negotiation {NegotiationId}: offer created and local description set exactly once.",
            call.Id, _negotiationId);
        await _connection!.InvokeAsync(VoiceCallHubContract.SendOffer, call.Id, _negotiationId.Value, offer, cancellationToken);
        logger.LogDebug("Call {CallId} negotiation {NegotiationId}: offer sent exactly once.", call.Id, _negotiationId);
    }

    private async Task RestartOffererAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentCall is not { } call || call.CallerAccountId != _accountId) return;
        await ResetMediaAsync(cancellationToken);
        _negotiationId = null;
        _negotiationStarted = false;
        await StartMediaAsync(cancellationToken);
        await StartOffererNegotiationAsync(cancellationToken);
        NotifyChanged();
    }

    private async Task ResetMediaAsync(CancellationToken cancellationToken = default)
    {
        CancelNegotiationTimeout();
        await media.CleanupAsync(cancellationToken);
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
        await ResetMediaAsync();
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
        CancelNegotiationTimeout();
        _negotiationTimeoutPeerGeneration = _peerGeneration;
        _negotiationTimeoutId = negotiationId;
        var cancellation = _negotiationCancellation = new CancellationTokenSource();
        _ = WaitForNegotiationAsync(cancellation, negotiationId, _peerGeneration);
    }

    private async Task WaitForNegotiationAsync(CancellationTokenSource cancellation, Guid negotiationId, int peerGeneration)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(18), cancellation.Token);
            if (ReferenceEquals(_negotiationCancellation, cancellation) &&
                _negotiationTimeoutId == negotiationId && _negotiationId == negotiationId &&
                _negotiationTimeoutPeerGeneration == peerGeneration && _peerGeneration == peerGeneration &&
                CurrentCall?.State == CallState.Active && MediaConnectionState == CallConnectionState.Connecting)
            {
                await FailMediaAsync("WebRTC negotiation timed out after 18 seconds.", "WEBRTC NEGOTIATION TIMED OUT");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private void CancelNegotiationTimeout()
    {
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
            "LocalGenerated={LocalGenerated}, LocalSent={LocalSent}, LocalTypes={LocalTypes}, RemoteReceived={RemoteReceived}, " +
            "RemoteAdded={RemoteAdded}, RemoteAddFailures={RemoteAddFailures}, QueuedRemote={QueuedRemote}, " +
            "SelectedPair={SelectedLocalType}/{SelectedRemoteType}/{SelectedProtocol}.",
            call.Id, _accountId, _mediaRole, _peerGeneration, eventName,
            snapshot?.SignalingState ?? "unavailable", snapshot?.IceGatheringState ?? "unavailable",
            snapshot?.IceConnectionState ?? "unavailable", snapshot?.ConnectionState ?? "unavailable",
            _localCandidatesGenerated, _localCandidatesSent, CandidateTypeSummary(_localCandidateTypes),
            _remoteCandidatesReceived, _remoteCandidatesAdded, _remoteCandidateAddFailures,
            snapshot?.QueuedRemoteCandidateCount ?? _pendingIce.Count,
            snapshot?.SelectedLocalCandidateType ?? "none", snapshot?.SelectedRemoteCandidateType ?? "none",
            snapshot?.SelectedCandidateProtocol ?? "none");
    }

    private void ResetAttemptDiagnostics(bool preserveRemoteCandidates)
    {
        _localCandidatesGenerated = 0;
        _localCandidatesSent = 0;
        _remoteCandidatesReceived = preserveRemoteCandidates ? _pendingIce.Count : 0;
        _remoteCandidatesAdded = 0;
        _remoteCandidateAddFailures = 0;
        _localCandidateTypes.Clear();
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
        CancelNegotiationTimeout();
        _heartbeatCancellation?.Cancel();
        _heartbeatCancellation?.Dispose();
        _heartbeatCancellation = null;
        await media.CleanupAsync();
        _mediaReady = false; _remoteDescriptionReady = false; _pendingOffer = null; _pendingSignalingCallId = null; _pendingIce.Clear();
        _negotiationId = null; _negotiationStarted = false; _processedAnswerNegotiations.Clear();
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
                if (CurrentCall is not null && IsSignalingConnected) await PublishParticipantStateAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogWarning(exception, "Voice call signaling heartbeat failed."); }
    }

    private async Task RunHandlerAsync(Func<Task> action)
    {
        await _signalingGate.WaitAsync();
        await _gate.WaitAsync();
        try { await action(); }
        catch (Exception exception)
        {
            logger.LogError(exception, "Voice call event handling failed.");
            if (CurrentCall?.State == CallState.Active) await FailMediaAsync(MediaErrorMessage(exception));
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
        await FinishAsync();
        if (_connection is not null)
        {
            DisposeHandlerRegistrations();
            await _connection.DisposeAsync();
        }
        await media.DisposeAsync();
        _signalingGate.Dispose();
        _gate.Dispose();
    }
}
