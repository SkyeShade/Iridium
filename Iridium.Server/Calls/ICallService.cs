using Iridium.Protocol;

namespace Iridium.Server.Calls;

public interface ICallService
{
    CallSessionDto CreateDirect(Guid conversationId, Guid callerId, string callerDisplayName,
        Guid calleeId, string calleeDisplayName);
    CallSessionDto Accept(Guid callId, Guid accountId);
    CallSessionDto Reject(Guid callId, Guid accountId);
    CallSessionDto Cancel(Guid callId, Guid accountId);
    CallSessionDto HangUp(Guid callId, Guid accountId);
    CallParticipantStateEvent SetParticipantState(Guid callId, Guid accountId, bool muted, bool deafened,
        CallConnectionState connectionState);
    CallParticipantSpeakingEvent SetParticipantSpeaking(Guid callId, Guid accountId, bool isSpeaking);
    CallSessionDto RequireParticipant(Guid callId, Guid accountId, params CallState[] allowedStates);
    IReadOnlyList<Guid> OtherParticipants(Guid callId, Guid accountId, params CallState[] allowedStates);
    IReadOnlyList<CallSessionDto> ExpireRingingCalls();
    IReadOnlyList<CallSessionDto> ExpireAbandonedActiveCalls();
    CallSessionDto? CurrentFor(Guid accountId);
}
