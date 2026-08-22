using System.Text.RegularExpressions;
using Iridium.Protocol;

namespace Iridium.Server.Hubs;

// TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
public sealed partial class VoiceTraceLogger(IHostEnvironment environment, ILogger<VoiceTraceLogger> logger)
{
    public bool Enabled => environment.IsDevelopment();

    public void Log(CallSessionDto call, Guid accountId, string connectionId, VoiceDiagnosticReport report)
    {
        if (!Enabled) return;
        var role = call.CallerAccountId == accountId ? "caller" : "callee";
        var eventName = SafeIdentifier(report.Event, "InvalidDiagnosticEvent");
        var errorName = SafeIdentifier(report.ErrorName, null);
        var message = SafeMessage(report.SafeMessage);
        var reason = SafeIdentifier(report.Reason, null);
        logger.LogDebug(
            "VOICE TRACE Call={CallId} Account={AccountId} Role={Role} Connection={ConnectionId} Peer={Peer} " +
            "Negotiation={Negotiation} Event={Event} Signal={SignalId} Sequence={Sequence} Old={OldState} New={NewState} " +
            "Signaling={SignalingState} IceGathering={IceGatheringState} IceConnection={IceConnectionState} " +
            "ConnectionState={ConnectionState} LocalDescription={LocalDescriptionType} RemoteDescription={RemoteDescriptionType} " +
            "CandidateType={CandidateType} Protocol={Protocol} SdpMid={SdpMid} SdpMLineIndex={SdpMLineIndex} " +
            "TrackKind={TrackKind} TrackEnabled={TrackEnabled} TrackReadyState={TrackReadyState} TrackMuted={TrackMuted} " +
            "IceServers={IceServerCount} TransportPolicy={IceTransportPolicy} Count={Count} QueueLength={QueueLength} " +
            "AudioTracks={AudioTrackCount} Senders={SenderCount} SdpLength={SdpLength} " +
            "CandidateLines={CandidateLineCount} CandidatePresent={CandidatePresent} HasAudio={HasAudioMediaSection} " +
            "Error={ErrorName} Message={SafeMessage} Reason={Reason}",
            call.Id, accountId, role, ShortConnectionId(connectionId), report.PeerGeneration,
            report.NegotiationGeneration, eventName, report.SignalId, report.Sequence,
            SafeState(report.OldState), SafeState(report.NewState), SafeState(report.SignalingState),
            SafeState(report.IceGatheringState), SafeState(report.IceConnectionState), SafeState(report.ConnectionState),
            SafeState(report.LocalDescriptionType), SafeState(report.RemoteDescriptionType),
            SafeState(report.CandidateType), SafeState(report.Protocol), SafeSdpMid(report.SdpMid), report.SdpMLineIndex,
            SafeState(report.TrackKind), report.TrackEnabled, SafeState(report.TrackReadyState), report.TrackMuted,
            report.IceServerCount, SafeState(report.IceTransportPolicy), report.Count, report.QueueLength,
            report.AudioTrackCount, report.SenderCount, report.SdpLength, report.CandidateLineCount,
            report.CandidatePresent, report.HasAudioMediaSection,
            errorName, message, reason);

        if (eventName is "VoiceFailureSnapshot" or "StatsSnapshot" or "MediaTrafficDetected")
            logger.LogDebug(
                "VOICE TRACE Call={CallId} Account={AccountId} Role={Role} Peer={Peer} Negotiation={Negotiation} " +
                "Event={Event} OffersCreated={OffersCreated} OffersReceived={OffersReceived} AnswersCreated={AnswersCreated} " +
                "AnswersReceived={AnswersReceived} LocalIceGenerated={LocalIceGenerated} LocalIceSent={LocalIceSent} " +
                "RemoteIceReceived={RemoteIceReceived} RemoteIceQueued={RemoteIceQueued} RemoteIceAdded={RemoteIceAdded} " +
                "RemoteIceAddFailures={RemoteIceAddFailures} RemoteTrackReceived={RemoteTrackReceived} " +
                "RemoteAudioPlaySucceeded={RemoteAudioPlaySucceeded} MediaTrafficDetected={MediaTrafficDetected} " +
                "LocalCandidateStats={LocalCandidateStats} RemoteCandidateStats={RemoteCandidateStats} " +
                "CandidatePairStats={CandidatePairStats} SucceededCandidatePairs={SucceededCandidatePairs} " +
                "NominatedPairExists={NominatedPairExists} SelectedPairExists={SelectedPairExists} PairState={PairState} " +
                "Pair={LocalCandidateType}->{RemoteCandidateType}/{Protocol} PacketsSent={PacketsSent} " +
                "PacketsReceived={PacketsReceived} PacketsLost={PacketsLost} BytesSent={BytesSent} BytesReceived={BytesReceived}",
                call.Id, accountId, role, report.PeerGeneration, report.NegotiationGeneration, eventName,
                report.OffersCreated, report.OffersReceived, report.AnswersCreated, report.AnswersReceived,
                report.LocalIceGenerated, report.LocalIceSent, report.RemoteIceReceived, report.RemoteIceQueued,
                report.RemoteIceAdded, report.RemoteIceAddFailures, report.RemoteTrackReceived,
                report.RemoteAudioPlaySucceeded, report.MediaTrafficDetected, report.LocalCandidateStats,
                report.RemoteCandidateStats, report.CandidatePairStats, report.SucceededCandidatePairs,
                report.NominatedPairExists, report.SelectedPairExists, SafeState(report.PairState),
                SafeState(report.LocalCandidateType), SafeState(report.RemoteCandidateType), SafeState(report.Protocol),
                report.PacketsSent, report.PacketsReceived, report.PacketsLost, report.BytesSent, report.BytesReceived);
    }

    private static string ShortConnectionId(string value) => value.Length <= 8 ? value : value[..8];
    private static string? SafeIdentifier(string? value, string? fallback) =>
        string.IsNullOrWhiteSpace(value) || !IdentifierRegex().IsMatch(value) ? fallback : value;
    private static string? SafeState(string? value) => SafeIdentifier(value, null);
    private static string? SafeSdpMid(string? value) => value is not null && SdpMidRegex().IsMatch(value) ? value : null;

    private static string? SafeMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (SensitivePayloadRegex().IsMatch(singleLine)) return "[redacted potentially sensitive WebRTC payload]";
        return singleLine.Length <= 160 ? singleLine : singleLine[..160];
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SdpMidRegex();
    [GeneratedRegex("candidate:|(^|\\s)v=0(\\s|$)|m=(audio|video)|a=(ice-ufrag|fingerprint|candidate):|(?:\\d{1,3}\\.){3}\\d{1,3}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitivePayloadRegex();
}
