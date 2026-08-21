using Iridium.Protocol;

namespace Iridium.Server.Calls;

public sealed record MediaSignalRoute(Guid TargetAccountId, bool ShouldForward, string? IgnoreReason = null);

public interface IMediaService
{
    CallMediaConfigurationDto GetConfiguration();
    MediaSignalRoute AuthorizeOffer(Guid callId, Guid senderAccountId, Guid negotiationId, WebRtcSessionDescription description);
    MediaSignalRoute AuthorizeAnswer(Guid callId, Guid senderAccountId, Guid negotiationId, WebRtcSessionDescription description);
    MediaSignalRoute AuthorizeIceCandidate(Guid callId, Guid senderAccountId, Guid negotiationId, WebRtcIceCandidate candidate);
}
