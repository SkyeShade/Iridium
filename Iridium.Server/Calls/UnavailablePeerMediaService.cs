using Iridium.Protocol;

namespace Iridium.Server.Calls;

/// <summary>Prevents production from silently falling back to browser-to-browser media.</summary>
public sealed class UnavailablePeerMediaService : IMediaService
{
    public CallMediaConfigurationDto GetConfiguration() => new(MediaMode.NodeSfu, []);
    public MediaSignalRoute AuthorizeOffer(Guid callId, Guid senderAccountId, string senderConnectionId,
        Guid negotiationId, WebRtcNegotiationKind negotiationKind, WebRtcSessionDescription description) => Throw();
    public MediaSignalRoute AuthorizeAnswer(Guid callId, Guid senderAccountId, string senderConnectionId,
        Guid negotiationId, WebRtcSessionDescription description) => Throw();
    public MediaSignalRoute AuthorizeIceCandidate(Guid callId, Guid senderAccountId, string senderConnectionId,
        Guid negotiationId, WebRtcIceCandidate candidate) => Throw();
    private static MediaSignalRoute Throw() =>
        throw new InvalidOperationException("Direct peer WebRTC signaling is disabled on this Node.");
}
