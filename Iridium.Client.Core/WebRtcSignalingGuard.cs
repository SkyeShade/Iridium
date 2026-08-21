namespace Iridium.Client.Core;

internal enum RemoteAnswerDisposition
{
    Apply,
    StaleNegotiation,
    Duplicate,
    AlreadyApplied,
    InvalidState
}

internal static class WebRtcSignalingGuard
{
    public static RemoteAnswerDisposition ClassifyAnswer(
        Guid? activeNegotiationId,
        Guid answerNegotiationId,
        bool alreadyProcessed,
        string? signalingState)
    {
        if (activeNegotiationId != answerNegotiationId) return RemoteAnswerDisposition.StaleNegotiation;
        if (alreadyProcessed) return RemoteAnswerDisposition.Duplicate;
        if (signalingState is null or "unavailable") return RemoteAnswerDisposition.Apply;
        if (string.Equals(signalingState, "have-local-offer", StringComparison.Ordinal)) return RemoteAnswerDisposition.Apply;
        if (string.Equals(signalingState, "stable", StringComparison.Ordinal)) return RemoteAnswerDisposition.AlreadyApplied;
        return RemoteAnswerDisposition.InvalidState;
    }
}
