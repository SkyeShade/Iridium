using Iridium.Protocol;
using Iridium.Server.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace Iridium.Server.Calls;

public sealed class DirectWebRtcMediaService(ICallService calls, IOptions<MediaOptions> options) : IMediaService
{
    private sealed class NegotiationState(Guid negotiationId, string offerSdp)
    {
        public Guid NegotiationId { get; } = negotiationId;
        public string OfferSdp { get; } = offerSdp;
        public bool AnswerForwarded { get; set; }
        public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
    }

    private readonly ConcurrentDictionary<Guid, NegotiationState> _negotiations = [];

    public CallMediaConfigurationDto GetConfiguration() => new(options.Value.Mode,
        options.Value.IceServers.Where(value => value.Urls.Count > 0)
            .Select(value => new IceServerDto(value.Urls, value.Username, value.Credential)).ToList());

    public MediaSignalRoute AuthorizeOffer(Guid callId, Guid senderAccountId, Guid negotiationId, WebRtcSessionDescription description)
    {
        ValidateNegotiationId(negotiationId);
        ValidateDescription(description, "offer");
        var call = calls.RequireParticipant(callId, senderAccountId, CallState.Ringing, CallState.Active);
        if (call.CallerAccountId != senderAccountId) throw new HubException("Only the caller can send the WebRTC offer.");
        var target = DirectPeer(call, senderAccountId, CallState.Ringing, CallState.Active);
        PruneExpiredNegotiations();
        while (true)
        {
            if (!_negotiations.TryGetValue(callId, out var existing))
            {
                if (_negotiations.TryAdd(callId, new NegotiationState(negotiationId, description.Sdp)))
                    return new MediaSignalRoute(target, true);
                continue;
            }
            lock (existing)
            {
                if (existing.NegotiationId == negotiationId)
                {
                    existing.LastActivity = DateTimeOffset.UtcNow;
                    if (!string.Equals(existing.OfferSdp, description.Sdp, StringComparison.Ordinal))
                        throw new HubException("A WebRTC negotiation identifier cannot be reused for a different offer.");
                    return new MediaSignalRoute(target, false, "duplicate-offer");
                }
                if (_negotiations.TryUpdate(callId, new NegotiationState(negotiationId, description.Sdp), existing))
                    return new MediaSignalRoute(target, true);
            }
        }
    }

    public MediaSignalRoute AuthorizeAnswer(Guid callId, Guid senderAccountId, Guid negotiationId, WebRtcSessionDescription description)
    {
        ValidateNegotiationId(negotiationId);
        ValidateDescription(description, "answer");
        var call = calls.RequireParticipant(callId, senderAccountId, CallState.Active);
        if (call.CallerAccountId == senderAccountId) throw new HubException("Only the callee can send the WebRTC answer.");
        var target = DirectPeer(call, senderAccountId, CallState.Active);
        if (!_negotiations.TryGetValue(callId, out var negotiation) || negotiation.NegotiationId != negotiationId)
            return new MediaSignalRoute(target, false, "stale-negotiation");
        lock (negotiation)
        {
            negotiation.LastActivity = DateTimeOffset.UtcNow;
            if (negotiation.AnswerForwarded) return new MediaSignalRoute(target, false, "duplicate-answer");
            negotiation.AnswerForwarded = true;
            return new MediaSignalRoute(target, true);
        }
    }

    public MediaSignalRoute AuthorizeIceCandidate(Guid callId, Guid senderAccountId, Guid negotiationId, WebRtcIceCandidate candidate)
    {
        ValidateNegotiationId(negotiationId);
        var call = calls.RequireParticipant(callId, senderAccountId, CallState.Ringing, CallState.Active);
        if (string.IsNullOrWhiteSpace(candidate.Candidate) || candidate.Candidate.Length > 16_384)
            throw new HubException("The ICE candidate is invalid.");
        var target = DirectPeer(call, senderAccountId, CallState.Ringing, CallState.Active);
        // ICE can be raised by setLocalDescription before the caller's SendOffer invocation
        // reaches the hub. Forward it with its negotiation identity; the receiving client
        // buffers current-generation ICE and discards stale-generation ICE.
        if (_negotiations.TryGetValue(callId, out var negotiation) && negotiation.NegotiationId == negotiationId)
            negotiation.LastActivity = DateTimeOffset.UtcNow;
        return new MediaSignalRoute(target, true);
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

    private Guid DirectPeer(CallSessionDto call, Guid senderAccountId, params CallState[] states)
    {
        if (call.Kind != CallKind.DirectVoice)
            throw new HubException("Direct WebRTC peer signaling is only available for direct calls.");
        var targets = calls.OtherParticipants(call.Id, senderAccountId, states);
        if (targets.Count != 1) throw new HubException("The direct call does not have exactly one remote participant.");
        return targets[0];
    }

    private void PruneExpiredNegotiations()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-2);
        foreach (var entry in _negotiations)
            if (entry.Value.LastActivity < cutoff) _negotiations.TryRemove(entry.Key, out _);
    }
}
