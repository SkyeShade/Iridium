using Iridium.Protocol;
using Iridium.Server.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Iridium.Server.Calls;

public sealed class CallService(IOptions<MediaOptions> options, TimeProvider timeProvider, ILogger<CallService> logger)
    : ICallService
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CallSession> _calls = [];

    public CallSessionDto CreateDirect(Guid conversationId, Guid callerId, string callerDisplayName,
        Guid calleeId, string calleeDisplayName, string callerConnectionId)
    {
        if (callerId == calleeId) throw new HubException("You cannot call yourself.");
        lock (_gate)
        {
            if (_calls.Values.Any(value => value.DirectConversationId == conversationId && IsLive(value.State)))
                throw new HubException("This direct conversation already has an active call.");
            if (_calls.Values.Any(value => IsLive(value.State) &&
                (value.Participants.ContainsKey(callerId) || value.Participants.ContainsKey(calleeId))))
                throw new HubException("One of the participants is already in another call.");
            var now = timeProvider.GetUtcNow();
            var session = new CallSession
            {
                Id = Guid.NewGuid(), Kind = CallKind.DirectVoice, DirectConversationId = conversationId,
                CallerAccountId = callerId, State = CallState.Ringing, CreatedAt = now,
                ExpiresAt = now.AddSeconds(Math.Clamp(options.Value.RingTimeoutSeconds, 5, 300)),
                Participants = new Dictionary<Guid, CallParticipant>
                {
                    [callerId] = new() { AccountId = callerId, DisplayName = callerDisplayName, JoinedAt = now,
                        LastSignalingAt = now, SignalingConnectionId = callerConnectionId },
                    [calleeId] = new() { AccountId = calleeId, DisplayName = calleeDisplayName, LastSignalingAt = now }
                }
            };
            _calls.Add(session.Id, session);
            logger.LogInformation("Voice call {CallId} created and ringing for direct conversation {ConversationId}.",
                session.Id, conversationId);
            return ToDto(session);
        }
    }

    public CallSessionDto Accept(Guid callId, Guid accountId, string calleeConnectionId)
    {
        lock (_gate)
        {
            var call = Require(callId, accountId, CallState.Ringing);
            if (accountId == call.CallerAccountId) throw new HubException("The caller cannot accept their own call.");
            call.State = CallState.Active;
            call.AcceptedAt = timeProvider.GetUtcNow();
            call.Participants[accountId].JoinedAt = call.AcceptedAt;
            call.Participants[accountId].SignalingConnectionId = calleeConnectionId;
            foreach (var participant in call.Participants.Values) participant.LastSignalingAt = timeProvider.GetUtcNow();
            logger.LogInformation("Voice call {CallId} accepted.", callId);
            return ToDto(call);
        }
    }

    public CallSessionDto Reject(Guid callId, Guid accountId) => EndRinging(callId, accountId, CallState.Rejected, false);
    public CallSessionDto Cancel(Guid callId, Guid accountId) => EndRinging(callId, accountId, CallState.Cancelled, true);

    public CallSessionDto HangUp(Guid callId, Guid accountId)
    {
        lock (_gate)
        {
            var call = Require(callId, accountId, CallState.Ringing, CallState.Active);
            call.State = call.State == CallState.Ringing ? CallState.Cancelled : CallState.Ended;
            CloseParticipants(call);
            logger.LogInformation("Voice call {CallId} ended by participant {AccountId}.", callId, accountId);
            return ToDto(call);
        }
    }

    public CallParticipantStateEvent SetParticipantState(Guid callId, Guid accountId, bool muted, bool deafened,
        CallConnectionState connectionState)
    {
        if (!Enum.IsDefined(connectionState)) throw new HubException("That connection state is not supported.");
        lock (_gate)
        {
            var call = Require(callId, accountId, CallState.Ringing, CallState.Active);
            var participant = call.Participants[accountId];
            participant.IsMuted = muted;
            participant.IsDeafened = deafened;
            participant.ConnectionState = connectionState;
            participant.LastSignalingAt = timeProvider.GetUtcNow();
            return new(callId, accountId, muted, deafened, connectionState);
        }
    }

    public CallParticipantSpeakingEvent SetParticipantSpeaking(Guid callId, Guid accountId, bool isSpeaking)
    {
        lock (_gate)
        {
            var call = Require(callId, accountId, CallState.Ringing, CallState.Active);
            var participant = call.Participants[accountId];
            participant.IsSpeaking = isSpeaking;
            participant.LastSignalingAt = timeProvider.GetUtcNow();
            return new(callId, accountId, isSpeaking);
        }
    }

    public CallSessionDto RequireParticipant(Guid callId, Guid accountId, params CallState[] allowedStates)
    {
        lock (_gate) return ToDto(Require(callId, accountId, allowedStates));
    }

    public IReadOnlyList<Guid> OtherParticipants(Guid callId, Guid accountId, params CallState[] allowedStates)
    {
        lock (_gate)
        {
            var call = Require(callId, accountId, allowedStates);
            return call.Participants.Keys.Where(value => value != accountId).ToList();
        }
    }

    public CallSignalingRoute RequireSignalingRoute(Guid callId, Guid accountId, string senderConnectionId,
        params CallState[] allowedStates)
    {
        lock (_gate)
        {
            var call = Require(callId, accountId, allowedStates);
            var sender = call.Participants[accountId];
            if (!string.Equals(sender.SignalingConnectionId, senderConnectionId, StringComparison.Ordinal))
                throw new HubException("This connection is not the selected media endpoint for the call.");
            var target = call.Participants.Values.Single(value => value.AccountId != accountId);
            if (string.IsNullOrWhiteSpace(target.SignalingConnectionId))
                throw new HubException("The remote media endpoint has not accepted the call.");
            sender.LastSignalingAt = timeProvider.GetUtcNow();
            return new(target.AccountId, target.SignalingConnectionId);
        }
    }

    public void RequireSelectedConnection(Guid callId, Guid accountId, string connectionId,
        params CallState[] allowedStates)
    {
        lock (_gate)
        {
            var participant = Require(callId, accountId, allowedStates).Participants[accountId];
            if (!string.Equals(participant.SignalingConnectionId, connectionId, StringComparison.Ordinal))
                throw new HubException("This connection is not the selected media endpoint for the call.");
        }
    }

    public void TouchSignaling(Guid callId, Guid accountId, string senderConnectionId)
    {
        lock (_gate)
        {
            var call = Require(callId, accountId, CallState.Active);
            var participant = call.Participants[accountId];
            if (!string.Equals(participant.SignalingConnectionId, senderConnectionId, StringComparison.Ordinal))
                throw new HubException("This connection is not the selected media endpoint for the call.");
            participant.LastSignalingAt = timeProvider.GetUtcNow();
        }
    }

    public CallConnectionLoss? DisconnectSignaling(string connectionId)
    {
        lock (_gate)
        {
            var call = _calls.Values.FirstOrDefault(value => IsLive(value.State) && value.Participants.Values.Any(
                participant => string.Equals(participant.SignalingConnectionId, connectionId, StringComparison.Ordinal)));
            if (call is null) return null;
            call.State = call.State == CallState.Ringing ? CallState.Cancelled : CallState.Ended;
            CloseParticipants(call);
            var remaining = call.Participants.Values.Single(value =>
                !string.Equals(value.SignalingConnectionId, connectionId, StringComparison.Ordinal));
            return new(ToDto(call), remaining.AccountId, remaining.SignalingConnectionId);
        }
    }

    public IReadOnlyList<CallSessionDto> ExpireRingingCalls()
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            var expired = _calls.Values.Where(value => value.State == CallState.Ringing && value.ExpiresAt <= now).ToList();
            foreach (var call in expired) { call.State = CallState.Cancelled; CloseParticipants(call); }
            return expired.Select(ToDto).ToList();
        }
    }

    public IReadOnlyList<CallSessionDto> ExpireAbandonedActiveCalls()
    {
        lock (_gate)
        {
            var cutoff = timeProvider.GetUtcNow().AddSeconds(-Math.Clamp(options.Value.SignalingLossTimeoutSeconds, 20, 300));
            var expired = _calls.Values.Where(value => value.State == CallState.Active &&
                value.Participants.Values.Any(participant => participant.LastSignalingAt <= cutoff)).ToList();
            foreach (var call in expired) { call.State = CallState.Ended; CloseParticipants(call); }
            return expired.Select(ToDto).ToList();
        }
    }

    public CallSessionDto? CurrentFor(Guid accountId, string connectionId)
    {
        lock (_gate)
        {
            var call = _calls.Values.FirstOrDefault(value => IsLive(value.State) && value.Participants.TryGetValue(accountId, out var participant) &&
                (string.Equals(participant.SignalingConnectionId, connectionId, StringComparison.Ordinal) ||
                 value.State == CallState.Ringing && accountId != value.CallerAccountId && participant.SignalingConnectionId is null));
            return call is null ? null : ToDto(call);
        }
    }

    private CallSessionDto EndRinging(Guid callId, Guid accountId, CallState state, bool callerRequired)
    {
        lock (_gate)
        {
            var call = Require(callId, accountId, CallState.Ringing);
            if ((accountId == call.CallerAccountId) != callerRequired)
                throw new HubException(callerRequired ? "Only the caller can cancel this call." : "Only the callee can reject this call.");
            call.State = state;
            CloseParticipants(call);
            logger.LogInformation("Voice call {CallId} changed to {CallState}.", callId, state);
            return ToDto(call);
        }
    }

    private CallSession Require(Guid callId, Guid accountId, params CallState[] states)
    {
        if (!_calls.TryGetValue(callId, out var call) || !call.Participants.ContainsKey(accountId))
            throw new HubException("Call not found for this account.");
        if (!states.Contains(call.State)) throw new HubException("The call is no longer in a valid state for that action.");
        return call;
    }

    private static bool IsLive(CallState state) => state is CallState.Ringing or CallState.Active;
    private static void CloseParticipants(CallSession call)
    {
        foreach (var participant in call.Participants.Values) participant.ConnectionState = CallConnectionState.Closed;
    }

    private static CallSessionDto ToDto(CallSession call) => new(call.Id, call.Kind, call.DirectConversationId,
        call.CallerAccountId, call.State, call.CreatedAt, call.ExpiresAt,
        call.Participants.Values.Select(value => new CallParticipantDto(value.AccountId, value.DisplayName,
            value.IsMuted, value.IsDeafened, value.IsSpeaking, value.JoinedAt, value.ConnectionState)).ToList(),
        call.AcceptedAt);
}
