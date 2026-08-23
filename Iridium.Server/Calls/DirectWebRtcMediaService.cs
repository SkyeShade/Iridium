using Iridium.Protocol;
using Iridium.Server.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace Iridium.Server.Calls;

public sealed class DirectWebRtcMediaService(ICallService calls, IOptions<MediaOptions> options) : IMediaService
{
    private sealed class NegotiationState(Guid negotiationId, Guid offererAccountId,
        WebRtcNegotiationKind kind, string offerSdp)
    {
        public Guid NegotiationId { get; } = negotiationId;
        public string OfferSdp { get; } = offerSdp;
        public Guid OffererAccountId { get; } = offererAccountId;
        public WebRtcNegotiationKind Kind { get; } = kind;
        public bool AnswerForwarded { get; set; }
        public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
    }

    // Multiple negotiation identifiers may briefly coexist during offer glare. Keeping them
    // separately lets the polite peer roll back and answer the winning offer without a later
    // colliding offer invalidating that answer at the server.
    private readonly ConcurrentDictionary<(Guid CallId, Guid NegotiationId), NegotiationState> _negotiations = [];

    public CallMediaConfigurationDto GetConfiguration() => new(options.Value.Mode, []);

    public MediaSignalRoute AuthorizeOffer(Guid callId, Guid senderAccountId, string senderConnectionId,
        Guid negotiationId, WebRtcNegotiationKind negotiationKind, WebRtcSessionDescription description)
    {
        ValidateNegotiationId(negotiationId);
        ValidateDescription(description, "offer");
        var call = calls.RequireParticipant(callId, senderAccountId, CallState.Active);
        if (negotiationKind == WebRtcNegotiationKind.Initial && call.CallerAccountId != senderAccountId)
            throw new HubException("Only the caller can send the initial WebRTC offer.");
        var target = calls.RequireSignalingRoute(callId, senderAccountId, senderConnectionId, CallState.Active);
        PruneExpiredNegotiations();
        var key = (callId, negotiationId);
        var proposed = new NegotiationState(negotiationId, senderAccountId, negotiationKind, description.Sdp);
        if (_negotiations.TryAdd(key, proposed))
            return new MediaSignalRoute(target.TargetAccountId, target.TargetConnectionId, true,
                NegotiationKind: negotiationKind);
        var existing = _negotiations[key];
        lock (existing)
        {
            existing.LastActivity = DateTimeOffset.UtcNow;
            if (!string.Equals(existing.OfferSdp, description.Sdp, StringComparison.Ordinal))
                throw new HubException("A WebRTC negotiation identifier cannot be reused for a different offer.");
            if (existing.OffererAccountId != senderAccountId || existing.Kind != negotiationKind)
                throw new HubException("A WebRTC negotiation identifier cannot be reused by another participant or negotiation kind.");
            return new MediaSignalRoute(target.TargetAccountId, target.TargetConnectionId, false,
                "duplicate-offer", negotiationKind);
        }
    }

    public MediaSignalRoute AuthorizeAnswer(Guid callId, Guid senderAccountId, string senderConnectionId,
        Guid negotiationId, WebRtcSessionDescription description)
    {
        ValidateNegotiationId(negotiationId);
        ValidateDescription(description, "answer");
        calls.RequireParticipant(callId, senderAccountId, CallState.Active);
        var target = calls.RequireSignalingRoute(callId, senderAccountId, senderConnectionId, CallState.Active);
        if (!_negotiations.TryGetValue((callId, negotiationId), out var negotiation))
            return new MediaSignalRoute(target.TargetAccountId, target.TargetConnectionId, false, "stale-negotiation");
        lock (negotiation)
        {
            if (negotiation.OffererAccountId == senderAccountId)
                throw new HubException("The WebRTC offerer cannot answer its own negotiation.");
            negotiation.LastActivity = DateTimeOffset.UtcNow;
            if (negotiation.AnswerForwarded) return new MediaSignalRoute(target.TargetAccountId, target.TargetConnectionId,
                false, "duplicate-answer", negotiation.Kind);
            negotiation.AnswerForwarded = true;
            return new MediaSignalRoute(target.TargetAccountId, target.TargetConnectionId, true,
                NegotiationKind: negotiation.Kind);
        }
    }

    public MediaSignalRoute AuthorizeIceCandidate(Guid callId, Guid senderAccountId, string senderConnectionId,
        Guid negotiationId, WebRtcIceCandidate candidate)
    {
        ValidateNegotiationId(negotiationId);
        var call = calls.RequireParticipant(callId, senderAccountId, CallState.Ringing, CallState.Active);
        if (string.IsNullOrWhiteSpace(candidate.Candidate) || candidate.Candidate.Length > 16_384)
            throw new HubException("The ICE candidate is invalid.");
        var target = calls.RequireSignalingRoute(callId, senderAccountId, senderConnectionId, CallState.Active);
        // ICE can be raised by setLocalDescription before the caller's SendOffer invocation
        // reaches the hub. Forward it with its negotiation identity; the receiving client
        // buffers current-generation ICE and discards stale-generation ICE.
        if (_negotiations.TryGetValue((callId, negotiationId), out var negotiation))
            negotiation.LastActivity = DateTimeOffset.UtcNow;
        return new MediaSignalRoute(target.TargetAccountId, target.TargetConnectionId, true);
    }

    private static void ValidateDescription(WebRtcSessionDescription description, string expectedType)
    {
        if (!string.Equals(description.Type, expectedType, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(description.Sdp) || description.Sdp.Length > 1_000_000)
            throw new HubException($"The WebRTC {expectedType} is invalid.");
    }

    private static void ValidateNegotiationId(Guid negotiationId)
    {
        if (negotiationId == Guid.Empty) throw new HubException("The WebRTC negotiation identifier is invalid.");
    }

    private void PruneExpiredNegotiations()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-2);
        foreach (var entry in _negotiations)
            if (entry.Value.LastActivity < cutoff) _negotiations.TryRemove(entry.Key, out _);
    }
}
