using Iridium.Protocol;

namespace Iridium.Server.Calls;

public interface ICallService
{
    CallSessionDto CreateDirect(Guid conversationId, Guid callerId, string callerDisplayName,
        Guid calleeId, string calleeDisplayName, string callerConnectionId);
    CallSessionDto Accept(Guid callId, Guid accountId, string calleeConnectionId);
    CallSessionDto Reject(Guid callId, Guid accountId);
    CallSessionDto Cancel(Guid callId, Guid accountId);
    CallSessionDto HangUp(Guid callId, Guid accountId);
    CallParticipantStateEvent SetParticipantState(Guid callId, Guid accountId, bool muted, bool deafened,
        CallConnectionState connectionState);
    CallParticipantSpeakingEvent SetParticipantSpeaking(Guid callId, Guid accountId, bool isSpeaking);
    CallSessionDto RequireParticipant(Guid callId, Guid accountId, params CallState[] allowedStates);
    IReadOnlyList<Guid> OtherParticipants(Guid callId, Guid accountId, params CallState[] allowedStates);
    CallSignalingRoute RequireSignalingRoute(Guid callId, Guid accountId, string senderConnectionId,
        params CallState[] allowedStates);
    void RequireSelectedConnection(Guid callId, Guid accountId, string connectionId, params CallState[] allowedStates);
    void TouchSignaling(Guid callId, Guid accountId, string senderConnectionId);
    CallConnectionLoss? DisconnectSignaling(string connectionId);
    IReadOnlyList<CallSessionDto> ExpireRingingCalls();
    IReadOnlyList<CallSessionDto> ExpireAbandonedActiveCalls();
    CallSessionDto? CurrentFor(Guid accountId, string connectionId);
}

public sealed record CallSignalingRoute(Guid TargetAccountId, string TargetConnectionId);
public sealed record CallConnectionLoss(CallSessionDto Call, Guid RemainingAccountId, string? RemainingConnectionId);
