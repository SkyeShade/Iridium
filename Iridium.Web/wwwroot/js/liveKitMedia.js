import { createRemoteVoicePlayback, updateRemoteVoicePlayback, destroyRemoteVoicePlayback,
    resumeRemoteVoicePlayback } from "./voicePlayback.js";
import { createVoiceActivityGate, evaluateVoiceActivity, inputSensitivityConfiguration,
    normalizedInputLevel, updateVoiceActivityConfiguration } from "./inputSensitivity.js";
import { createLiveKitNoiseProcessor, enhancedNoiseSuppressionSupported,
    microphoneCaptureConstraints } from "./noiseSuppression.js";
import { takeDesktopAppAudioTrack } from "./desktopAppAudio.js";

const sessions = new Map();

// Capture keeps the selected source's native shape and only scales when it exceeds this box.
const screenCaptureTarget = Object.freeze({ width: 2560, height: 1440, frameRate: 60 });
const screenCaptureSources = new WeakMap();
const screenBitrateAnchors = Object.freeze([
    { pixels: 640 * 360, fps30: 1_000_000, fps60: 1_500_000 },
    { pixels: 1280 * 720, fps30: 3_500_000, fps60: 6_000_000 },
    { pixels: 1920 * 1080, fps30: 6_000_000, fps60: 10_000_000 },
    { pixels: 2560 * 1440, fps30: 10_000_000, fps60: 16_000_000 }
]);

function sdk() {
    if (!globalThis.LivekitClient?.Room) throw new Error("The LiveKit browser SDK is unavailable.");
    return globalThis.LivekitClient;
}

function accountKey(identity) {
    return (identity || "").replaceAll("-", "").toLowerCase();
}

function preferenceFor(session, identity) {
    return session.preferences.get(accountKey(identity)) ?? { volumePercent: 100, locallyMuted: false };
}

function publicationName(publication) {
    return publication?.trackName || publication?.name || publication?.track?.name || "";
}

// LiveKit publishes screen video and screen audio as separate publications. The `stream`
// identity is their shared association; track names are presentation metadata and are not
// guaranteed to remain identical after the server accepts both publications.
export function publicationStreamIdentity(publication) {
    // `RemoteTrack.mediaStream.id` is created by the receiving SDK and is not the publisher's
    // authoritative stream association. It can also differ between the video and audio tracks.
    return publication?.trackInfo?.stream || publicationName(publication);
}

function allPublications(participant) {
    return Array.from(participant?.trackPublications?.values?.() ?? []);
}

function safeTrackDiagnostic(track) {
    if (!track) return null;
    const settings = track.getSettings?.() ?? {};
    return {
        kind: track.kind, label: track.label || null, enabled: track.enabled,
        muted: track.muted, readyState: track.readyState,
        settings: {
            width: settings.width ?? null, height: settings.height ?? null,
            frameRate: settings.frameRate ?? null, displaySurface: settings.displaySurface ?? null,
            sampleRate: settings.sampleRate ?? null, channelCount: settings.channelCount ?? null,
            suppressLocalAudioPlayback: settings.suppressLocalAudioPlayback ?? null
        }
    };
}

function safePublicationDiagnostic(publication) {
    return {
        kind: publication?.kind ?? null, source: publication?.source ?? null,
        trackName: publicationName(publication) || null,
        streamIdentity: publicationStreamIdentity(publication) || null, trackSid: publication?.trackSid ?? null,
        isSubscribed: publication?.isSubscribed ?? null, hasTrack: !!publication?.track
    };
}

function interpolate(value, lowerValue, upperValue, lowerResult, upperResult) {
    if (upperValue === lowerValue) return upperResult;
    const amount = Math.max(0, Math.min(1, (value - lowerValue) / (upperValue - lowerValue)));
    return lowerResult + (upperResult - lowerResult) * amount;
}

// Exported to keep the automatic profile independently testable without involving capture/UI state.
export function screenShareBitrate(width, height, frameRate) {
    const pixels = Math.max(1, Number(width) || 1920) * Math.max(1, Number(height) || 1080);
    const fps = Math.max(1, Math.min(60, Number(frameRate) || 30));
    let lower = screenBitrateAnchors[0], upper = screenBitrateAnchors[screenBitrateAnchors.length - 1];
    for (let index = 1; index < screenBitrateAnchors.length; index++) {
        if (pixels <= screenBitrateAnchors[index].pixels) {
            lower = screenBitrateAnchors[index - 1]; upper = screenBitrateAnchors[index]; break;
        }
    }
    const at30 = interpolate(pixels, lower.pixels, upper.pixels, lower.fps30, upper.fps30);
    const at60 = interpolate(pixels, lower.pixels, upper.pixels, lower.fps60, upper.fps60);
    const target = fps <= 30 ? at30 * Math.max(.5, fps / 30) : interpolate(fps, 30, 60, at30, at60);
    return Math.max(1_000_000, Math.min(16_000_000, Math.round(target / 250_000) * 250_000));
}

export function fitScreenShareResolution(sourceWidth, sourceHeight, maxWidth = 2560, maxHeight = 1440) {
    const width = Math.max(2, Number(sourceWidth) || maxWidth);
    const height = Math.max(2, Number(sourceHeight) || maxHeight);
    const scale = Math.min(1, maxWidth / width, maxHeight / height);
    const even = (value, maximum) => Math.max(2, Math.min(maximum, Math.round(value * scale / 2) * 2));
    return { width: even(width, maxWidth), height: even(height, maxHeight), scale };
}

function screenSharePublishOptions(track, mediaStreamId) {
    const { VideoPreset } = sdk();
    const settings = track.mediaStreamTrack.getSettings();
    const output = fitScreenShareResolution(settings.width || 1920, settings.height || 1080);
    const width = output.width, height = output.height;
    const frameRate = Math.max(1, Math.min(60, settings.frameRate || 30));
    const maxBitrate = screenShareBitrate(width, height, frameRate);
    const lowWidth = Math.max(2, Math.round(width / 4) * 2);
    const lowHeight = Math.max(2, Math.round(height / 4) * 2);
    return {
        settings: { width, height, frameRate }, maxBitrate,
        options: {
            name: mediaStreamId, stream: mediaStreamId, source: track.source,
            videoCodec: "vp8", simulcast: true,
            screenShareEncoding: { maxBitrate, maxFramerate: frameRate, priority: "high" },
            screenShareSimulcastLayers: [
                new VideoPreset(lowWidth, lowHeight, Math.max(500_000, Math.round(maxBitrate / 4)), Math.min(30, frameRate), "medium")
            ],
            degradationPreference: "maintain-resolution"
        }
    };
}

// A noise processor turns the browser suppressor off; it falls back to it if RNNoise cannot start.
export function microphoneProfile(configuredBitrate, inputDeviceId = null, noiseSuppression = "standard",
        noiseProcessor = null) {
    const bitrate = Math.max(64_000, Math.min(128_000, Number(configuredBitrate) || 96_000));
    return {
        bitrate,
        capture: microphoneCaptureConstraints(inputDeviceId, noiseSuppression, noiseProcessor !== null),
        publish: {
            audioPreset: { maxBitrate: bitrate, priority: "high" },
            dtx: true,
            red: true,
            forceStereo: false
        }
    };
}

function statsSummary(report, direction) {
    if (!report?.forEach) return null;
    const codecs = new Map(), rows = [], remoteInbound = [], pairs = [];
    report.forEach(stat => {
        if (stat.type === "codec") codecs.set(stat.id, stat.mimeType);
        const wanted = direction === "outbound" ? stat.type === "outbound-rtp" : stat.type === "inbound-rtp";
        if (wanted && !stat.isRemote && (stat.kind === "video" || stat.mediaType === "video")) rows.push(stat);
        if (direction === "outbound" && stat.type === "remote-inbound-rtp" &&
            (stat.kind === "video" || stat.mediaType === "video")) remoteInbound.push(stat);
        if (stat.type === "candidate-pair" && (stat.selected || stat.nominated) && stat.state === "succeeded") pairs.push(stat);
    });
    const lossRows = direction === "outbound" && remoteInbound.length ? remoteInbound : rows;
    const rttSeconds = remoteInbound.map(row => Number(row.roundTripTime)).find(Number.isFinite) ??
        pairs.map(row => Number(row.currentRoundTripTime)).find(Number.isFinite);
    return {
        timestamp: Math.max(0, ...rows.map(row => Number(row.timestamp) || 0)),
        bytes: rows.reduce((sum, row) => sum + Number((direction === "outbound" ? row.bytesSent : row.bytesReceived) || 0), 0),
        codec: rows.map(row => codecs.get(row.codecId)).find(Boolean)?.replace(/^video\//i, "")?.toLowerCase() || "unknown",
        layers: rows.filter(row => row.active !== false).length,
        framesPerSecond: Math.max(0, ...rows.map(row => Number(row.framesPerSecond) || 0)),
        frameWidth: Math.max(0, ...rows.map(row => Number(row.frameWidth) || 0)),
        frameHeight: Math.max(0, ...rows.map(row => Number(row.frameHeight) || 0)),
        framesEncoded: rows.reduce((sum, row) => sum + Number(row.framesEncoded || 0), 0),
        framesSent: rows.reduce((sum, row) => sum + Number(row.framesSent || 0), 0),
        framesDecoded: rows.reduce((sum, row) => sum + Number(row.framesDecoded || 0), 0),
        framesReceived: rows.reduce((sum, row) => sum + Number(row.framesReceived || 0), 0),
        keyFramesDecoded: rows.reduce((sum, row) => sum + Number(row.keyFramesDecoded || 0), 0),
        totalDecodeTime: rows.reduce((sum, row) => sum + Number(row.totalDecodeTime || 0), 0),
        packetsSent: rows.reduce((sum, row) => sum + Number(row.packetsSent || 0), 0),
        packetsReceived: rows.reduce((sum, row) => sum + Number(row.packetsReceived || 0), 0),
        nackCount: rows.reduce((sum, row) => sum + Number(row.nackCount || 0), 0),
        targetBitrate: rows.reduce((sum, row) => sum + Number(row.targetBitrate || 0), 0),
        availableOutgoingBitrate: pairs.map(row => row.availableOutgoingBitrate).find(Number.isFinite) ?? null,
        framesDropped: rows.reduce((sum, row) => sum + Number(row.framesDropped || 0), 0),
        freezeCount: rows.reduce((sum, row) => sum + Number(row.freezeCount || 0), 0),
        totalFreezesDuration: rows.reduce((sum, row) => sum + Number(row.totalFreezesDuration || 0), 0),
        totalEncodeTime: rows.reduce((sum, row) => sum + Number(row.totalEncodeTime || 0), 0),
        retransmittedPacketsSent: rows.reduce((sum, row) => sum + Number(row.retransmittedPacketsSent || 0), 0),
        retransmittedBytesSent: rows.reduce((sum, row) => sum + Number(row.retransmittedBytesSent || 0), 0),
        qualityLimitationReasons: [...new Set(rows.map(row => row.qualityLimitationReason).filter(Boolean))],
        qualityLimitationDurations: rows.reduce((combined, row) => {
            for (const [reason, duration] of Object.entries(row.qualityLimitationDurations ?? {}))
                combined[reason] = (combined[reason] ?? 0) + Number(duration || 0);
            return combined;
        }, {}),
        packetsLost: lossRows.reduce((sum, row) => sum + Number(row.packetsLost || 0), 0),
        jitterMs: Math.round(Math.max(0, ...lossRows.map(row => Number(row.jitter) || 0)) * 1000 * 10) / 10,
        decoderImplementations: [...new Set(rows.map(row => row.decoderImplementation).filter(Boolean))],
        rttMs: Number.isFinite(rttSeconds) ? Math.round(rttSeconds * 1000 * 10) / 10 : null
    };
}

function senderEncodingSummary(track) {
    try {
        return Array.from(track?.sender?.getParameters?.().encodings ?? []).map(encoding => ({
            rid: encoding.rid || null,
            active: encoding.active !== false,
            maxBitrate: encoding.maxBitrate ?? null,
            maxFramerate: encoding.maxFramerate ?? null,
            scaleResolutionDownBy: encoding.scaleResolutionDownBy ?? 1
        }));
    } catch {
        return [];
    }
}

async function diagnoseVideoBitrate(session, track, direction, details) {
    if (!session.diagnostics || typeof track.getRTCStatsReport !== "function") return;
    try {
        const first = statsSummary(await track.getRTCStatsReport(), direction);
        await new Promise(resolve => globalThis.setTimeout(resolve, 1000));
        if (!sessions.has(session.id)) return;
        const second = statsSummary(await track.getRTCStatsReport(), direction);
        const elapsed = second && first ? second.timestamp - first.timestamp : 0;
        const bitrate = elapsed > 0 ? Math.max(0, Math.round((second.bytes - first.bytes) * 8000 / elapsed)) : null;
        const encodedFrames = Math.max(0, (second?.framesEncoded ?? 0) - (first?.framesEncoded ?? 0));
        const encodeSeconds = Math.max(0, (second?.totalEncodeTime ?? 0) - (first?.totalEncodeTime ?? 0));
        console.debug(`LiveKit screen share ${direction}`, {
            ...details, codec: second?.codec || "unknown", activeLayers: second?.layers ?? 0,
            bitrateBps: bitrate, framesPerSecond: second?.framesPerSecond ?? 0,
            frameWidth: second?.frameWidth ?? 0, frameHeight: second?.frameHeight ?? 0,
            qualityLimitationReasons: second?.qualityLimitationReasons ?? [],
            qualityLimitationDurations: second?.qualityLimitationDurations ?? {},
            framesEncoded: second?.framesEncoded ?? 0, framesSent: second?.framesSent ?? 0,
            framesDecoded: second?.framesDecoded ?? 0, framesDropped: second?.framesDropped ?? 0,
            freezeCount: second?.freezeCount ?? 0, totalFreezesDuration: second?.totalFreezesDuration ?? 0,
            encodeTimePerFrameMs: encodedFrames > 0 ? Math.round(encodeSeconds * 100000 / encodedFrames) / 100 : null,
            retransmittedPacketsSent: second?.retransmittedPacketsSent ?? 0,
            retransmittedBytesSent: second?.retransmittedBytesSent ?? 0,
            packetsLost: second?.packetsLost ?? 0, jitterMs: second?.jitterMs ?? null,
            rttMs: second?.rttMs ?? null, decoderImplementations: second?.decoderImplementations ?? [],
            documentVisibility: document.visibilityState,
            senderEncodings: direction === "outbound" ? senderEncodingSummary(track) : undefined
        });
    } catch (error) {
        console.debug("LiveKit screen share stats unavailable", { direction, error: error?.name });
    }
}

function scheduleVideoDiagnostics(session, track, direction, details) {
    if (!session.diagnostics) return;
    diagnoseVideoBitrate(session, track, direction, { ...details, sample: "initial" });
    globalThis.setTimeout(() => {
        if (!sessions.has(session.id) || track?.mediaStreamTrack?.readyState === "ended") return;
        diagnoseVideoBitrate(session, track, direction, { ...details, sample: "10s" });
    }, 9000);
}

// Explicit allowlist: never return nodeSession, credentials, SDP, candidate addresses or SDK objects.
export async function collectMediaDiagnostics() {
    const snapshots = [];
    for (const session of sessions.values()) {
        if (!session.diagnostics) continue;
        const publications = [];
        for (const participant of [session.room.localParticipant, ...session.room.remoteParticipants.values()]) {
            for (const publication of allPublications(participant)) {
                if (publication.source !== sdk().Track.Source.ScreenShare &&
                    publication.source !== sdk().Track.Source.ScreenShareAudio) continue;
                const direction = participant === session.room.localParticipant ? "outbound" : "inbound";
                const track = publication.track;
                let stats = null;
                try {
                    const report = await track?.getRTCStatsReport?.();
                    stats = publication.kind === "video" ? statsSummary(report, direction) : audioStatsSummary(report, direction);
                } catch { }
                publications.push({ participantIdentity: participant.identity, direction,
                    ...safePublicationDiagnostic(publication), selectedQuality: selectedVideoQuality(publication),
                    capture: direction === "outbound" ? safeTrackDiagnostic(track?.mediaStreamTrack) : null,
                    senderEncodings: direction === "outbound" ? senderEncodingSummary(track) : null, stats,
                    interval: mediaStatsInterval(session.diagnosticStats?.get(publication.trackSid), stats) });
                session.diagnosticStats ??= new Map();
                if (stats) session.diagnosticStats.set(publication.trackSid, stats);
            }
        }
        snapshots.push({ timestamp: performance.now(), roomName: session.room.name,
            localParticipantIdentity: session.room.localParticipant.identity,
            participantIdentities: [...session.room.remoteParticipants.values()].map(p => p.identity),
            state: session.room.state, publications,
            watches: [...session.watched.values()].map(w => ({ ...w })),
            longTasks: session.longTasks?.splice(0) ?? [],
            viewers: [...session.viewers.values()].map(v => ({
                mediaStreamId: v.mediaStreamId, elementId: v.elementId,
                readyState: v.videoElement?.readyState, width: v.videoElement?.videoWidth,
                height: v.videoElement?.videoHeight, renderedFrames: v.renderedFrames ?? null,
                lastFrameAt: v.lastFrameAt ?? null, maxFrameGapMs: v.maxFrameGapMs ?? null,
                audioAttached: !!v.audioPlayback, audioBlocked: v.audioPlayback?.playBlocked ?? null,
                audioMuted: v.audioMuted, audioMode: v.audioPlayback?.mode ?? null
            })) });
    }
    return snapshots;
}

export function mediaStatsInterval(previous, current) {
    if (!previous || !current || current.timestamp <= previous.timestamp || current.bytes < previous.bytes) return null;
    const seconds = (current.timestamp - previous.timestamp) / 1000;
    const delta = field => Number.isFinite(current[field]) && Number.isFinite(previous[field]) && current[field] >= previous[field]
        ? current[field] - previous[field] : null;
    return { seconds, bitrateBps: delta("bytes") * 8 / seconds,
        encodedFps: delta("framesEncoded") == null ? null : delta("framesEncoded") / seconds,
        decodedFps: delta("framesDecoded") == null ? null : delta("framesDecoded") / seconds,
        framesDropped: delta("framesDropped"), packetsLost: delta("packetsLost"),
        freezes: delta("freezeCount"), freezeSeconds: delta("totalFreezesDuration"),
        encodeMsPerFrame: delta("framesEncoded") > 0 ? delta("totalEncodeTime") * 1000 / delta("framesEncoded") : null,
        decodeMsPerFrame: delta("framesDecoded") > 0 ? delta("totalDecodeTime") * 1000 / delta("framesDecoded") : null };
}

export async function reconnectDevelopmentMedia() {
    const active = [...sessions.values()].filter(session => session.diagnostics);
    if (active.length !== 1) throw new Error("Reconnect audit requires exactly one Development media session.");
    // SDK-supported local signaling interruption; does not alter the SFU or Iridium SignalR connection.
    await active[0].room.simulateScenario("signal-reconnect");
}

function audioStatsSummary(report, direction) {
    if (!report?.forEach) return null;
    const codecs = new Map(), rtp = [], remoteInbound = [], pairs = [];
    report.forEach(stat => {
        if (stat.type === "codec") codecs.set(stat.id, stat);
        const wanted = direction === "outbound" ? stat.type === "outbound-rtp" : stat.type === "inbound-rtp";
        if (wanted && !stat.isRemote && (stat.kind === "audio" || stat.mediaType === "audio")) rtp.push(stat);
        if (direction === "outbound" && stat.type === "remote-inbound-rtp" &&
            (stat.kind === "audio" || stat.mediaType === "audio")) remoteInbound.push(stat);
        if (stat.type === "candidate-pair" && (stat.selected || stat.nominated) && stat.state === "succeeded") pairs.push(stat);
    });
    const lossRows = direction === "outbound" && remoteInbound.length ? remoteInbound : rtp;
    const bytesField = direction === "outbound" ? "bytesSent" : "bytesReceived";
    const packetsField = direction === "outbound" ? "packetsSent" : "packetsReceived";
    const rttSeconds = remoteInbound.map(row => Number(row.roundTripTime)).find(Number.isFinite) ??
        pairs.map(row => Number(row.currentRoundTripTime)).find(Number.isFinite);
    const selectedCodec = rtp.map(row => codecs.get(row.codecId)).find(Boolean);
    const opusCodec = Array.from(codecs.values()).find(codec => /^audio\/opus$/i.test(codec.mimeType || ""));
    return {
        timestamp: Math.max(0, ...rtp.map(row => Number(row.timestamp) || 0)),
        bytes: rtp.reduce((sum, row) => sum + Number(row[bytesField] || 0), 0),
        packets: rtp.reduce((sum, row) => sum + Number(row[packetsField] || 0), 0),
        packetsLost: lossRows.reduce((sum, row) => sum + Number(row.packetsLost || 0), 0),
        jitterMs: Math.round(Math.max(0, ...lossRows.map(row => Number(row.jitter) || 0)) * 1000 * 10) / 10,
        rttMs: Number.isFinite(rttSeconds) ? Math.round(rttSeconds * 1000 * 10) / 10 : null,
        codec: selectedCodec?.mimeType?.replace(/^audio\//i, "")?.toLowerCase() || "unknown",
        opusInBandFec: opusCodec?.sdpFmtpLine
            ? /(?:^|;)\s*useinbandfec=1(?:;|$)/i.test(opusCodec.sdpFmtpLine) : null
    };
}

async function diagnoseAudioTransport(session, track, direction, label = "microphone") {
    if (!session.diagnostics || typeof track?.getRTCStatsReport !== "function") return;
    try {
        const first = audioStatsSummary(await track.getRTCStatsReport(), direction);
        await new Promise(resolve => globalThis.setTimeout(resolve, 1000));
        if (!sessions.has(session.id)) return;
        const second = audioStatsSummary(await track.getRTCStatsReport(), direction);
        const elapsed = second && first ? second.timestamp - first.timestamp : 0;
        const bytes = second && first ? Math.max(0, second.bytes - first.bytes) : 0;
        const packets = second && first ? Math.max(0, second.packets - first.packets) : 0;
        const packetsLost = second && first ? Math.max(0, second.packetsLost - first.packetsLost) : 0;
        console.debug(`LiveKit ${label} ${direction}`, {
            codec: second?.codec || "unknown", targetBitrateBps: session.voiceBitrate,
            actualBitrateBps: elapsed > 0 ? Math.round(bytes * 8000 / elapsed) : null,
            packetsLost, packetLossPercent: packets + packetsLost > 0
                ? Math.round(packetsLost * 10_000 / (packets + packetsLost)) / 100 : 0,
            jitterMs: second?.jitterMs ?? null, rttMs: second?.rttMs ?? null,
            opusInBandFec: second?.opusInBandFec ?? null,
            senderEncodings: direction === "outbound" ? senderEncodingSummary(track) : undefined
        });
    } catch (error) {
        console.debug("LiveKit microphone stats unavailable", { direction, error: error?.name });
    }
}

function setScreenSubscription(session, publication, subscribed) {
    const { Track, VideoQuality } = sdk();
    publication.setSubscribed?.(subscribed);
    if (session.diagnostics && (publication.source === Track.Source.ScreenShare ||
        publication.source === Track.Source.ScreenShareAudio))
        console.debug("LiveKit screen publication subscription requested", {
            subscribed, watchedMediaStream: session.watched.has(publicationStreamIdentity(publication)),
            ...safePublicationDiagnostic(publication)
        });
    if (subscribed && publication.source === Track.Source.ScreenShare && publication.setVideoQuality) {
        publication.setVideoQuality(VideoQuality.HIGH);
        if (session.diagnostics) console.debug("LiveKit screen share subscription", { selectedQuality: "HIGH" });
    }
}

function selectedVideoQuality(publication) {
    const { VideoQuality } = sdk();
    if (publication?.videoQuality === VideoQuality.HIGH) return "HIGH";
    if (publication?.videoQuality === VideoQuality.MEDIUM) return "MEDIUM";
    if (publication?.videoQuality === VideoQuality.LOW) return "LOW";
    return publication?.videoQuality ?? "unknown";
}

async function callback(session, method, ...args) {
    try { await session.callback.invokeMethodAsync(method, ...args); }
    catch (error) { if (session.diagnostics) console.debug("LiveKit callback unavailable", method, error?.name); }
}

async function reportState(session, state) {
    if (session.kind === "call") await callback(session, "OnConnectionStateChanged", session.peerGeneration, state);
}

function makeAudioPlayback(session, participant, track) {
    const identity = accountKey(participant.identity);
    const old = session.playbacks.get(identity);
    if (old) destroyRemoteVoicePlayback(old);
    const stream = new MediaStream([track.mediaStreamTrack]);
    createRemoteVoicePlayback(stream, session.audioContext, {
        ...preferenceFor(session, identity), deafened: session.deafened,
        diagnostic: (event, details) => session.diagnostics && console.debug("LiveKit audio", event, details)
    }).then(playback => session.playbacks.set(identity, playback));
    diagnoseAudioTransport(session, track, "inbound");
}

function participantMatchesWatch(session, participant, watch) {
    if (!participant || !watch) return false;
    if (!watch.participantIdentity)
        return participant === participantForWatch(session, watch);
    return accountKey(participant?.identity) === accountKey(watch.participantIdentity);
}

function participantForWatch(session, watch) {
    if (!watch?.participantIdentity) {
        // Compatibility with older DM metadata: only an unambiguous canonical stream
        // association can identify the owner. Never default to the local participant.
        const owners = [session.room.localParticipant, ...session.room.remoteParticipants.values()]
            .filter(participant => allPublications(participant).some(p => publicationMatchesWatch(p, watch)));
        return owners.length === 1 ? owners[0] : null;
    }
    if (participantMatchesWatch(session, session.room.localParticipant, watch))
        return session.room.localParticipant;
    return Array.from(session.room.remoteParticipants.values())
        .find(participant => participantMatchesWatch(session, participant, watch)) ?? null;
}

function publicationMatchesWatch(publication, watch) {
    const { Track } = sdk();
    return (publication?.source === Track.Source.ScreenShare ||
        publication?.source === Track.Source.ScreenShareAudio) &&
        publicationStreamIdentity(publication) === watch.mediaStreamId;
}

function reportWatchedAudioAvailability(session, watch, publications) {
    const { Track } = sdk();
    const available = publications.some(publication => publicationMatchesWatch(publication, watch) &&
        publication.source === Track.Source.ScreenShareAudio);
    if (watch.audioAvailable === available) return;
    watch.audioAvailable = available;
    callback(session, "OnWatchedStreamAudioAvailabilityChanged",
        ...(session.kind === "call"
            ? [session.peerGeneration, watch.mediaStreamId, available]
            : [watch.mediaStreamId, available]));
}

export function reconcileScreenWatch(session, watch) {
    const participant = participantForWatch(session, watch);
    const publications = allPublications(participant);
    if (session.diagnostics) {
        console.debug("[ScreenWatch] begin", {
            participantIdentity: watch.participantIdentity || participant?.identity || null,
            stream: watch.mediaStreamId, iridiumStreamId: watch.iridiumStreamId
        });
        for (const publication of publications) console.debug("[ScreenWatch] publication", {
            participantIdentity: participant?.identity || null,
            source: publication?.source ?? null,
            publicationSid: publication?.trackSid ?? null,
            trackName: publicationName(publication) || null,
            trackInfoStream: publication?.trackInfo?.stream || null,
            stream: publicationStreamIdentity(publication) || null,
            isSubscribed: publication?.isSubscribed ?? null,
            trackObjectPresent: !!publication?.track
        });
    }
    // A missing participant is still connection/publication discovery pending, not proof that the
    // stream has no audio. Once the participant exists, publication metadata is LiveKit truth.
    if (participant) reportWatchedAudioAvailability(session, watch, publications);
    if (!participant || participant === session.room.localParticipant) {
        refreshViewers(session, watch.mediaStreamId);
        return publications;
    }
    for (const publication of publications) {
        if (!publicationMatchesWatch(publication, watch)) continue;
        setScreenSubscription(session, publication, true);
        if (session.diagnostics && publication.source === sdk().Track.Source.ScreenShareAudio)
            console.debug("[ScreenWatch] requested audio subscription", safePublicationDiagnostic(publication));
    }
    // This covers publications whose RemoteTrack already existed before Watch. Publications that
    // are still metadata-only are attached by TrackSubscribed and reconciled again there.
    refreshViewers(session, watch.mediaStreamId);
    return publications;
}

function configurePublication(session, publication, participant = null) {
    const source = publication.source;
    const { Track } = sdk();
    const microphone = source === Track.Source.Microphone;
    const screen = source === Track.Source.ScreenShare || source === Track.Source.ScreenShareAudio;
    const watch = screen ? Array.from(session.watched.values()).find(value =>
        publicationMatchesWatch(publication, value) && participantMatchesWatch(session, participant, value)) : null;
    const shouldSubscribe = microphone || !!watch;
    if (session.diagnostics && screen) console.debug("LiveKit screen publication discovered", {
        shouldSubscribe, watchedMediaStream: !!watch,
        ...safePublicationDiagnostic(publication)
    });
    if (publication.setSubscribed) setScreenSubscription(session, publication, shouldSubscribe);
}

export function wireRoom(session) {
    const { RoomEvent, Track } = sdk();
    const room = session.room;
    room.on(RoomEvent.TrackPublished, (publication, participant) => {
        configurePublication(session, publication, participant);
        for (const watch of session.watched.values())
            if (participantMatchesWatch(session, participant, watch)) reconcileScreenWatch(session, watch);
    });
    room.on(RoomEvent.TrackSubscribed, (track, publication, participant) => {
        if (publication.source === Track.Source.Microphone) makeAudioPlayback(session, participant, track);
        if (publication.source === Track.Source.ScreenShare)
            scheduleVideoDiagnostics(session, track, "inbound", {
                requestedQuality: "HIGH", selectedQuality: selectedVideoQuality(publication)
            });
        if (publication.source === Track.Source.ScreenShareAudio) {
            diagnoseAudioTransport(session, track, "inbound", "screen share audio");
            if (session.diagnostics) console.debug("[ScreenWatch] audio TrackSubscribed", {
                participant: participant?.identity || null, stream: publicationStreamIdentity(publication),
                ...safePublicationDiagnostic(publication)
            });
        }
        if (session.diagnostics && (publication.source === Track.Source.ScreenShare ||
            publication.source === Track.Source.ScreenShareAudio))
            console.debug("LiveKit screen track subscribed", {
                ...safePublicationDiagnostic(publication), track: safeTrackDiagnostic(track.mediaStreamTrack)
            });
        for (const watch of session.watched.values())
            if (participantMatchesWatch(session, participant, watch) && publicationMatchesWatch(publication, watch))
                reconcileScreenWatch(session, watch);
    });
    room.on(RoomEvent.TrackUnsubscribed, (track, publication, participant) => {
        if (publication.source === Track.Source.Microphone) {
            const key = accountKey(participant.identity), playback = session.playbacks.get(key);
            if (playback) destroyRemoteVoicePlayback(playback);
            session.playbacks.delete(key);
        }
        for (const watch of session.watched.values())
            if (participantMatchesWatch(session, participant, watch) && publicationMatchesWatch(publication, watch))
                reconcileScreenWatch(session, watch);
    });
    room.on(RoomEvent.TrackUnpublished, (publication, participant) => {
        if (session.diagnostics && (publication.source === Track.Source.ScreenShare ||
            publication.source === Track.Source.ScreenShareAudio))
            console.debug("LiveKit screen publication removed", safePublicationDiagnostic(publication));
        for (const watch of session.watched.values())
            if (participantMatchesWatch(session, participant, watch)) reconcileScreenWatch(session, watch);
    });
    room.on(RoomEvent.TrackMuted, publication => {
        if (publication.source === Track.Source.ScreenShare || publication.source === Track.Source.ScreenShareAudio)
            refreshViewers(session, publicationStreamIdentity(publication));
    });
    room.on(RoomEvent.TrackUnmuted, publication => {
        if (publication.source === Track.Source.ScreenShare || publication.source === Track.Source.ScreenShareAudio)
            refreshViewers(session, publicationStreamIdentity(publication));
    });
    room.on(RoomEvent.LocalTrackUnpublished, publication => {
        if (publication.source !== Track.Source.ScreenShareAudio || session.screenPublicationMutation ||
            publicationStreamIdentity(publication) !== session.screenMediaStreamId) return;
        updateLocalScreenAudioAvailability(session, false);
    });
    room.on(RoomEvent.LocalTrackPublished, publication => {
        if (publication.source === Track.Source.ScreenShareAudio &&
            publicationStreamIdentity(publication) === session.screenMediaStreamId)
            updateLocalScreenAudioAvailability(session, true);
    });
    room.on(RoomEvent.ParticipantConnected, participant => {
        allPublications(participant).forEach(publication => configurePublication(session, publication, participant));
        for (const watch of session.watched.values())
            if (participantMatchesWatch(session, participant, watch)) reconcileScreenWatch(session, watch);
    });
    room.on(RoomEvent.ParticipantDisconnected, participant => {
        const key = accountKey(participant.identity), playback = session.playbacks.get(key);
        if (playback) destroyRemoteVoicePlayback(playback);
        session.playbacks.delete(key);
    });
    room.on(RoomEvent.Reconnecting, () => reportState(session, "disconnected"));
    room.on(RoomEvent.Reconnected, () => {
        for (const participant of room.remoteParticipants.values())
            for (const publication of allPublications(participant)) configurePublication(session, publication, participant);
        if (session.screenMediaStreamId)
            updateLocalScreenAudioAvailability(session, allPublications(room.localParticipant).some(publication =>
                publication.source === Track.Source.ScreenShareAudio &&
                publicationStreamIdentity(publication) === session.screenMediaStreamId));
        for (const watch of session.watched.values()) reconcileScreenWatch(session, watch);
        reportState(session, "connected");
    });
    room.on(RoomEvent.Disconnected, () => reportState(session, "disconnected"));
    room.on(RoomEvent.ConnectionStateChanged, state => {
        const mapped = state === "connected" ? "connected"
            : state === "disconnected" || state === "reconnecting" ? "disconnected"
            : "connecting";
        reportState(session, mapped);
    });
}

function startLocalSpeaking(session) {
    const { Track, createAudioAnalyser } = sdk();
    const publication = allPublications(session.room.localParticipant).find(p => p.source === Track.Source.Microphone);
    if (!publication?.track) return;
    try {
        // mediaStreamTrack is the processed track while noise suppression runs, so noise isn't speech.
        const analyser = createAudioAnalyser(publication.track, { fftSize: 512, smoothingTimeConstant: .65 });
        session.speakingAnalyser = analyser;
        const gate = createVoiceActivityGate(session.inputSensitivity);
        session.voiceActivityGate = gate;
        const tick = () => {
            if (!sessions.has(session.id)) return;
            const next = evaluateVoiceActivity(gate, normalizedInputLevel(analyser.calculateVolume()), performance.now(), {
                muted: session.muted === true
            });
            if (next !== session.speaking) {
                session.speaking = next;
                callback(session, "OnSpeakingChanged", ...(session.kind === "call" ? [session.peerGeneration, next] : [next]));
            }
            session.speakingFrame = requestAnimationFrame(tick);
        };
        tick();
    } catch (error) {
        if (session.diagnostics) console.debug("LiveKit local speaking analysis unavailable", error?.name);
    }
}

function restartLocalSpeaking(session) {
    if (session.speakingFrame) cancelAnimationFrame(session.speakingFrame);
    session.speakingFrame = 0;
    session.speakingAnalyser?.cleanup?.();
    session.speakingAnalyser = null;
    startLocalSpeaking(session);
}

function noiseProcessorFor(session, mode) {
    if (mode !== "enhanced" || !enhancedNoiseSuppressionSupported()) return null;
    return createLiveKitNoiseProcessor((event, details) => session.diagnostics &&
        console.debug("LiveKit noise suppression", event, details));
}

function microphoneFor(session, mode) {
    session.noiseSuppression = mode;
    session.noiseProcessor = noiseProcessorFor(session, mode);
    session.microphone = microphoneProfile(session.voiceBitrate, session.activeInputDeviceId, mode,
        session.noiseProcessor);
    return session.microphone;
}

function localMicrophoneTrack(session) {
    const { Track } = sdk();
    return allPublications(session.room.localParticipant).find(p => p.source === Track.Source.Microphone)?.track;
}

// Processors can't go in the capture options: LiveKit only hands a track an AudioContext when the
// room mixes remote audio itself, which Iridium doesn't, so the processor is attached once published.
async function attachNoiseProcessor(session, track = localMicrophoneTrack(session)) {
    const processor = session.noiseProcessor;
    if (!track || !processor || track.getProcessor?.() === processor) return;
    track.setAudioContext(session.audioContext);
    await track.setProcessor(processor);
}

// Switching modes mid-call re-acquires the microphone, since browsers can't toggle their own
// suppressor on a live track reliably.
async function applyNoiseSuppression(session) {
    const mode = session.inputSensitivity.noiseSuppression;
    if (mode === session.noiseSuppression) return;
    const previousProcessor = session.noiseProcessor;
    const microphone = microphoneFor(session, mode);
    const track = localMicrophoneTrack(session);
    // Without a track yet, the next setMicrophoneEnabled picks up session.microphone.
    if (!track) return;
    if (track.getProcessor?.()) await track.stopProcessor();
    else await previousProcessor?.destroy();
    await track.restartTrack(microphone.capture);
    await attachNoiseProcessor(session, track);
    restartLocalSpeaking(session);
    if (session.diagnostics) console.debug("LiveKit noise suppression changed", {
        mode, enhanced: session.noiseProcessor?.active === true
    });
}

function matchingPublications(session, mediaStreamId) {
    const watch = session.watched.get(mediaStreamId);
    const participant = watch ? participantForWatch(session, watch) : null;
    const publications = participant ? allPublications(participant) : watch ? [] : [
        ...allPublications(session.room.localParticipant),
        ...Array.from(session.room.remoteParticipants.values()).flatMap(allPublications)
    ];
    return publications.filter(publication => publicationMatchesWatch(publication, { mediaStreamId }) &&
        publication.track?.mediaStreamTrack && publication.track.mediaStreamTrack.readyState !== "ended");
}

function matchingTracks(session, mediaStreamId) {
    return matchingPublications(session, mediaStreamId).map(p => p.track.mediaStreamTrack);
}

function configureLiveVideoElement(session, viewer, element) {
    if (viewer.videoElement === element) return;
    if (viewer.videoElement) {
        if (viewer.frameCallback != null) viewer.videoElement.cancelVideoFrameCallback?.(viewer.frameCallback);
        viewer.videoElement.removeEventListener("click", viewer.preventVideoToggle);
        viewer.videoElement.removeEventListener("pause", viewer.resumeLiveVideo);
    }
    viewer.videoElement = element;
    if (session.diagnostics && element.requestVideoFrameCallback) {
        const frame = now => {
            if (session.viewers.get(viewer.elementId) !== viewer || viewer.videoElement !== element) return;
            viewer.renderedFrames = (viewer.renderedFrames ?? 0) + 1;
            if (viewer.lastFrameAt != null) viewer.maxFrameGapMs = Math.max(viewer.maxFrameGapMs ?? 0, now - viewer.lastFrameAt);
            viewer.lastFrameAt = now;
            viewer.frameCallback = element.requestVideoFrameCallback(frame);
        };
        viewer.frameCallback = element.requestVideoFrameCallback(frame);
    }
    element.controls = false;
    element.disablePictureInPicture = true;
    element.removeAttribute("controls");
    viewer.preventVideoToggle = event => event.preventDefault();
    viewer.resumeLiveVideo = async () => {
        if (viewer.resumePending || !session.viewers.has(viewer.elementId) ||
            !element.srcObject?.getVideoTracks?.().some(track => track.readyState === "live")) return;
        viewer.resumePending = true;
        try { await element.play(); }
        catch (error) {
            if (session.diagnostics) console.debug("LiveKit live stream resume blocked", { name: error?.name });
        }
        finally { viewer.resumePending = false; }
    };
    element.addEventListener("click", viewer.preventVideoToggle);
    element.addEventListener("pause", viewer.resumeLiveVideo);
}

export async function refreshViewers(session, mediaStreamId) {
    for (const viewer of session.viewers.values()) {
        if (viewer.mediaStreamId !== mediaStreamId) continue;
        viewer.refreshGeneration++;
        const refreshGeneration = viewer.refreshGeneration;
        const element = document.getElementById(viewer.elementId);
        if (!element) continue;
        configureLiveVideoElement(session, viewer, element);
        const publications = session.watched.has(mediaStreamId) ? matchingPublications(session, mediaStreamId) : [];
        const tracks = publications.map(publication => publication.track.mediaStreamTrack);
        const videoTracks = tracks.filter(track => track.kind === "video");
        const audioTrack = tracks.find(track => track.kind === "audio");
        const attached = element.srcObject?.getVideoTracks?.() ?? [];
        if (attached.length !== videoTracks.length || attached.some((track, index) => track !== videoTracks[index]))
            element.srcObject = videoTracks.length ? new MediaStream(videoTracks) : null;
        // Shared audio has exactly one playback path: a separate GainNode graph. The video is
        // always muted so attaching the combined LiveKit stream cannot duplicate audio.
        element.muted = true;
        element.playsInline = true;
        // Video play can remain pending until the first decoded frame. Audio attachment and
        // Stop Watching cleanup must not wait for that promise (or for an empty video's play).
        try {
            if (videoTracks.length) element.play?.()?.catch(error => {
                if (session.diagnostics) console.debug("LiveKit screen video play blocked", { name: error?.name });
            });
            else element.pause?.();
        }
        catch (error) {
            if (session.diagnostics) console.debug("LiveKit screen video play blocked", { name: error?.name });
        }
        if (viewer.refreshGeneration !== refreshGeneration) continue;
        // Detach, source replacement, or a newer refresh may have won while play awaited.
        if (session.viewers.get(viewer.elementId) !== viewer ||
            !session.watched.has(mediaStreamId)) {
            viewer.audioAbort?.abort();
            viewer.pendingAudioAttachment = null;
            viewer.pendingAudioTrackId = null;
            destroyRemoteVoicePlayback(viewer.audioPlayback);
            viewer.audioPlayback = null;
            viewer.audioTrackId = null;
            continue;
        }
        if (viewer.audioTrackId === audioTrack?.id && viewer.audioPlayback && !viewer.audioPlayback.disposed) continue;
        if (audioTrack && viewer.pendingAudioTrackId === audioTrack.id) continue;
        viewer.pendingAudioAttachment = null;
        viewer.pendingAudioTrackId = null;
        viewer.audioAbort?.abort();
        if (viewer.audioPlayback) destroyRemoteVoicePlayback(viewer.audioPlayback);
        viewer.audioPlayback = null;
        viewer.audioTrackId = null;
        if (!audioTrack) {
            if (session.diagnostics) console.debug("LiveKit screen viewer media attached", {
                mediaStreamId, screenVideoSubscribed: videoTracks.length > 0,
                screenAudioSubscribed: false, screenAudioAttached: false, playSucceeded: null
            });
            continue;
        }
        const audioAttachment = {};
        viewer.pendingAudioAttachment = audioAttachment;
        viewer.pendingAudioTrackId = audioTrack.id;
        viewer.audioAbort = new AbortController();
        const playback = await createRemoteVoicePlayback(new MediaStream([audioTrack]), session.audioContext, {
            signal: viewer.audioAbort.signal,
            volumePercent: viewer.volumePercent, minimumVolumePercent: 0,
            locallyMuted: viewer.audioMuted, deafened: session.deafened,
            diagnostic: (event, details) => session.diagnostics && console.debug("LiveKit screen audio", event, {
                mediaStreamId, volumePercent: viewer.volumePercent, muted: viewer.audioMuted,
                deafened: session.deafened, ...details
            })
        });
        if (session.viewers.get(viewer.elementId) !== viewer ||
            viewer.pendingAudioAttachment !== audioAttachment) {
            destroyRemoteVoicePlayback(playback);
            continue;
        }
        viewer.pendingAudioAttachment = null;
        viewer.pendingAudioTrackId = null;
        viewer.audioPlayback = playback;
        viewer.audioTrackId = audioTrack.id;
        // Preferences can change while browser autoplay/AudioContext activation awaits.
        updateRemoteVoicePlayback(playback, { volumePercent: viewer.volumePercent,
            locallyMuted: viewer.audioMuted, deafened: session.deafened });
        element.dataset.audioBlocked = playback.playBlocked ? "true" : "false";
        if (session.diagnostics) console.debug("LiveKit screen viewer media attached", {
            mediaStreamId, screenVideoSubscribed: videoTracks.length > 0,
            screenAudioSubscribed: publications.some(publication =>
                publication.source === sdk().Track.Source.ScreenShareAudio && publication.isSubscribed !== false),
            screenAudioAttached: true, playSucceeded: !playback.playBlocked, playbackMode: playback.mode,
            selfPreview: allPublications(session.room.localParticipant).some(publication =>
                publicationStreamIdentity(publication) === mediaStreamId),
            audioMuted: viewer.audioMuted, volumePercent: viewer.volumePercent
        });
        if (session.diagnostics) console.debug("[ScreenWatch] audio playback attached", {
            mediaStreamId, trackId: audioTrack.id, playbackMode: playback.mode,
            blocked: playback.playBlocked
        });
    }
}

async function connectCore(callbackRef, nodeSession, preferences, kind, peerGeneration = 0,
        initialMuted = false, initialDeafened = false, localVoicePreference = null) {
    if (!nodeSession?.accessToken || !nodeSession?.publicUrl) throw new Error("The Node did not provide LiveKit room access.");
    const { Room, RoomEvent } = sdk();
    const id = crypto.randomUUID();
    const inputSensitivity = inputSensitivityConfiguration(localVoicePreference);
    const voiceBitrate = microphoneProfile(nodeSession.voiceBitrate).bitrate;
    const session = {
        id, kind, peerGeneration, callback: callbackRef, diagnostics: nodeSession.diagnosticsEnabled === true,
        voiceBitrate,
        // Screen viewers are deliberately subscribed by Watch/Stop Watching rather than SDK-managed elements.
        room: new Room({ autoSubscribe: false, adaptiveStream: false, dynacast: true }),
        preferences: new Map((preferences ?? []).map(p => [accountKey(p.remoteAccountId), p])),
        playbacks: new Map(), viewers: new Map(), watched: new Map(), deafened: initialDeafened === true,
        muted: initialMuted === true, speaking: false, inputSensitivity, voiceActivityGate: null,
        activeInputDeviceId: inputSensitivity.inputDeviceId,
        noiseSuppression: null, noiseProcessor: null, microphone: null, microphoneChange: Promise.resolve(),
        screenTracks: [], screenStreamId: null, screenMediaStreamId: null, screenGeneration: 0,
        screenAudioAvailable: false, screenPublicationMutation: false,
        audioContext: (globalThis.AudioContext || globalThis.webkitAudioContext)
            ? new (globalThis.AudioContext || globalThis.webkitAudioContext)() : null
    };
    const microphone = microphoneFor(session, inputSensitivity.noiseSuppression);
    sessions.set(id, session);
    if (session.diagnostics && globalThis.PerformanceObserver?.supportedEntryTypes?.includes("longtask")) {
        session.longTasks = [];
        session.longTaskObserver = new PerformanceObserver(list => {
            for (const entry of list.getEntries()) session.longTasks.push({ startTime: entry.startTime, duration: entry.duration });
            session.longTasks = session.longTasks.slice(-100);
        });
        session.longTaskObserver.observe({ type: "longtask" });
    }
    wireRoom(session);
    try {
        await session.room.connect(nodeSession.publicUrl, nodeSession.accessToken, { autoSubscribe: false });
        const microphonePublication = await session.room.localParticipant.setMicrophoneEnabled(
            initialMuted !== true, microphone.capture, microphone.publish);
        await attachNoiseProcessor(session, microphonePublication?.track);
        if (session.diagnostics && microphonePublication?.track) {
            const settings = microphonePublication.track.mediaStreamTrack.getSettings();
            console.debug("LiveKit microphone published", {
                codec: "opus", targetBitrateBps: voiceBitrate,
                channelCount: settings.channelCount || 1, sampleRate: settings.sampleRate || null,
                dtx: true, red: true, inBandFec: "browser-negotiated",
                echoCancellation: settings.echoCancellation ?? true,
                noiseSuppression: settings.noiseSuppression ?? true,
                enhancedNoiseSuppression: session.noiseProcessor?.active === true,
                autoGainControl: settings.autoGainControl ?? true,
                senderEncodings: senderEncodingSummary(microphonePublication.track)
            });
            diagnoseAudioTransport(session, microphonePublication.track, "outbound");
        }
        startLocalSpeaking(session);
        for (const participant of session.room.remoteParticipants.values())
            allPublications(participant).forEach(p => configurePublication(session, p, participant));
        await reportState(session, "connected");
        if (session.diagnostics) console.debug("LiveKit connected", { provider: nodeSession.provider,
            roomKind: nodeSession.roomKind, roomName: session.room.name,
            localParticipantIdentity: session.room.localParticipant.identity,
            remoteParticipantIdentities: [...session.room.remoteParticipants.values()].map(p => p.identity) });
        return id;
    } catch (error) {
        sessions.delete(id);
        session.longTaskObserver?.disconnect();
        await session.audioContext?.close?.();
        await session.room.disconnect();
        throw error;
    }
}

export function connectCall(callbackRef, configuration, context, preferences, localVoicePreference = null) {
    return connectCore(callbackRef, configuration.nodeSession, preferences, "call", context.peerGeneration,
        context.muted, context.deafened, localVoicePreference);
}

export function connectCommunity(callbackRef, mediaSession, preferences, muted, deafened, localVoicePreference = null) {
    return connectCore(callbackRef, mediaSession.nodeSession, preferences, "community", 0, muted, deafened,
        localVoicePreference);
}

export async function setMuted(id, muted) {
    const session = sessions.get(id); if (!session) return;
    session.muted = muted === true;
    if (muted && session.voiceActivityGate) {
        const wasSpeaking = session.speaking;
        session.voiceActivityGate.speaking = false;
        session.voiceActivityGate.aboveThresholdFrames = 0;
        session.speaking = false;
        if (wasSpeaking)
            await callback(session, "OnSpeakingChanged", ...(session.kind === "call" ? [session.peerGeneration, false] : [false]));
    }
    // A track first created on unmute still needs the session's capture profile and noise processor.
    await session.room.localParticipant.setMicrophoneEnabled(!muted, session.microphone.capture,
        session.microphone.publish);
    if (!muted) await attachNoiseProcessor(session);
    // Joining muted publishes no microphone, so the speaking detector starts with the first unmute.
    if (!muted && !session.speakingAnalyser) startLocalSpeaking(session);
}

export async function setInputSensitivity(id, preference) {
    const session = sessions.get(id); if (!session) return;
    session.inputSensitivity = inputSensitivityConfiguration(preference);
    if (session.voiceActivityGate)
        updateVoiceActivityConfiguration(session.voiceActivityGate, session.inputSensitivity);
    const deviceId = session.inputSensitivity.inputDeviceId;
    if (deviceId !== session.activeInputDeviceId && session.room.switchActiveDevice) {
        await session.room.switchActiveDevice("audioinput", deviceId || "default");
        session.activeInputDeviceId = deviceId;
        session.microphone = microphoneProfile(session.voiceBitrate, deviceId, session.noiseSuppression,
            session.noiseProcessor);
    }
    // Preference changes arrive in bursts (the sensitivity slider); mode switches run one at a time.
    session.microphoneChange = session.microphoneChange.then(() => sessions.has(id) && applyNoiseSuppression(session))
        .catch(error => { if (session.diagnostics) console.debug("LiveKit noise suppression change failed", error?.name); });
    await session.microphoneChange;
}

export function setDeafened(id, deafened) {
    const session = sessions.get(id); if (!session) return;
    session.deafened = deafened;
    for (const [identity, playback] of session.playbacks)
        updateRemoteVoicePlayback(playback, { ...preferenceFor(session, identity), deafened });
    for (const viewer of session.viewers.values())
        updateRemoteVoicePlayback(viewer.audioPlayback, { deafened });
}

export function setParticipantPreference(id, preference) {
    const session = sessions.get(id); if (!session) return;
    const key = accountKey(preference.remoteAccountId);
    session.preferences.set(key, preference);
    updateRemoteVoicePlayback(session.playbacks.get(key), { ...preference, deafened: session.deafened });
}

export function screenShareCaptureOptions(supported = {}, safari = false) {
    const captureOptions = { audio: true, video: true };
    // suppressLocalAudioPlayback is a constrainable property on supporting engines. Unknown
    // display-picker options are deliberately not guessed, preserving Safari/mobile behavior.
    if (supported.suppressLocalAudioPlayback)
        captureOptions.audio = { suppressLocalAudioPlayback: false };
    // systemAudio is a standardized optional display-picker hint; engines that do not implement
    // the dictionary member ignore it. It requests browser-provided audio without fabricating it.
    captureOptions.systemAudio = "include";
    captureOptions.windowAudio = "window";
    captureOptions.surfaceSwitching = "include";
    if (!safari) captureOptions.video = {
        width: { ideal: screenCaptureTarget.width, max: screenCaptureTarget.width },
        height: { ideal: screenCaptureTarget.height, max: screenCaptureTarget.height },
        frameRate: { ideal: screenCaptureTarget.frameRate, max: screenCaptureTarget.frameRate }
    };
    return captureOptions;
}

async function constrainScreenCapture(video) {
    const source = video.getSettings?.() ?? {};
    const fitted = fitScreenShareResolution(source.width, source.height);
    if ((source.width > fitted.width || source.height > fitted.height) && video.applyConstraints) {
        await video.applyConstraints({
            width: { ideal: fitted.width, max: fitted.width },
            height: { ideal: fitted.height, max: fitted.height },
            aspectRatio: { ideal: source.width / source.height },
            frameRate: { ideal: Math.min(screenCaptureTarget.frameRate, source.frameRate || screenCaptureTarget.frameRate),
                max: screenCaptureTarget.frameRate }
        });
    }
    const output = video.getSettings?.() ?? source;
    if ((output.width || 0) > screenCaptureTarget.width || (output.height || 0) > screenCaptureTarget.height)
        throw new DOMException("Screen capture could not be constrained to 2560x1440.", "OverconstrainedError");
    screenCaptureSources.set(video, { sourceWidth: source.width ?? null, sourceHeight: source.height ?? null,
        sourceFrameRate: source.frameRate ?? null });
    return { source, output };
}

export async function captureScreenTracks(session) {
    const { LocalVideoTrack, LocalAudioTrack, Track } = sdk();
    const supported = navigator.mediaDevices?.getSupportedConstraints?.() ?? {};
    const media = await navigator.mediaDevices.getDisplayMedia(
        screenShareCaptureOptions(supported, sdk().getBrowser?.()?.name === "Safari"));
    const videoTracks = media.getVideoTracks(), audioTracks = media.getAudioTracks();
    const video = videoTracks[0];
    if (!video) {
        media.getTracks().forEach(track => track.stop());
        throw new DOMException("Screen capture did not provide a video track.", "NotFoundError");
    }
    let captureGeometry;
    try { captureGeometry = await constrainScreenCapture(video); }
    catch (error) {
        media.getTracks().forEach(track => track.stop());
        throw error;
    }
    // The desktop app captures the shared program's own sound natively instead of browser loopback.
    if (!audioTracks.length) {
        try {
            const appAudio = await takeDesktopAppAudioTrack();
            if (appAudio) audioTracks.push(appAudio);
        } catch (error) {
            if (session.diagnostics) console.debug("Desktop app audio unavailable", { name: error?.name, message: error?.message });
        }
    }
    const videoTrack = new LocalVideoTrack(video, undefined, false);
    videoTrack.source = Track.Source.ScreenShare;
    const tracks = [videoTrack], audio = audioTracks[0];
    if (session.diagnostics) console.debug("LiveKit display capture result", {
            videoTracks: videoTracks.length, audioTracks: audioTracks.length,
            video: safeTrackDiagnostic(video), audio: safeTrackDiagnostic(audio),
            sourceWidth: captureGeometry.source.width ?? null, sourceHeight: captureGeometry.source.height ?? null,
            actualCaptureFPS: captureGeometry.output.frameRate ?? null,
            outputWidth: captureGeometry.output.width ?? null, outputHeight: captureGeometry.output.height ?? null,
            systemAudioHintRequested: true,
            suppressLocalAudioPlaybackSupported: supported.suppressLocalAudioPlayback === true
        });
    if (audio) {
        const audioTrack = new LocalAudioTrack(audio, undefined, false);
        audioTrack.source = Track.Source.ScreenShareAudio;
        tracks.push(audioTrack);
    }
    return tracks;
}

async function publishCapturedScreenTrack(session, track, options, mediaStreamId) {
    try { return await session.room.localParticipant.publishTrack(track, options); }
    catch (error) {
        if (session.diagnostics) console.debug("LiveKit screen track publication failed", {
            mediaStreamId, kind: track.kind, source: track.source,
            errorName: error?.name, errorMessage: error?.message
        });
        throw error;
    }
}

export async function publishScreenTracks(session, tracks, mediaStreamId) {
    const published = [];
    for (const track of tracks) {
        if (track.kind === "video") {
            track.mediaStreamTrack.contentHint = "detail";
            const profile = screenSharePublishOptions(track, mediaStreamId);
            const publication = await publishCapturedScreenTrack(session, track, profile.options, mediaStreamId);
            published.push(publication);
            if (session.diagnostics) console.debug("LiveKit screen share published", {
                videoPublished: true, audioCaptured: tracks.some(value => value.kind === "audio"),
                displaySurface: track.mediaStreamTrack.getSettings().displaySurface ?? null,
                publication: safePublicationDiagnostic(publication),
                ...profile.settings, ...screenCaptureSources.get(track.mediaStreamTrack),
                outputWidth: profile.settings.width, outputHeight: profile.settings.height,
                contentHint: track.mediaStreamTrack.contentHint,
                codec: profile.options.videoCodec, targetMaxBitrateBps: profile.maxBitrate,
                maxBitrateBps: 16_000_000,
                configuredSimulcastLayers: profile.options.screenShareSimulcastLayers.length + 1,
                degradationPreference: profile.options.degradationPreference,
                senderEncodings: senderEncodingSummary(track)
            });
            scheduleVideoDiagnostics(session, track, "outbound", {
                capturedWidth: profile.settings.width, capturedHeight: profile.settings.height,
                capturedFrameRate: profile.settings.frameRate, targetMaxBitrateBps: profile.maxBitrate,
                configuredSimulcastLayers: profile.options.screenShareSimulcastLayers.length + 1
            });
        } else {
            const publication = await publishCapturedScreenTrack(session, track, {
                name: mediaStreamId, stream: mediaStreamId, source: track.source
            }, mediaStreamId);
            published.push(publication);
            if (session.diagnostics) {
                const settings = track.mediaStreamTrack.getSettings();
                console.debug("LiveKit screen share audio published", {
                    audioPublished: true, mediaStreamId,
                    publication: safePublicationDiagnostic(publication),
                    track: safeTrackDiagnostic(track.mediaStreamTrack),
                    sampleRate: settings.sampleRate ?? null, channelCount: settings.channelCount ?? null
                });
                diagnoseAudioTransport(session, track, "outbound", "screen share audio");
            }
        }
    }
    if (session.diagnostics) console.debug("LiveKit screen share publication complete", {
        mediaStreamId, publicationCount: published.length,
        videoPublished: published.some(publication => publication?.kind === "video"),
        audioPublished: published.some(publication => publication?.kind === "audio")
    });
    return published;
}

function publishedScreenAudio(publications) {
    const { Track } = sdk();
    return publications.some(publication => publication?.source === Track.Source.ScreenShareAudio ||
        publication?.kind === "audio");
}

function updateLocalScreenAudioAvailability(session, available) {
    if (session.screenAudioAvailable === available) return;
    session.screenAudioAvailable = available;
    if (session.screenStreamId)
        callback(session, "OnScreenShareAudioAvailabilityChanged",
            ...(session.kind === "call" ? [session.peerGeneration, available] : [available]));
}

export function wireScreenEnded(session, tracks, generation) {
    const video = tracks.find(track => track.kind === "video");
    video?.mediaStreamTrack.addEventListener("ended", () => {
        if (session.screenGeneration === generation)
            stopScreenShare(session.id, "BrowserEnded");
    }, { once: true });
    const audio = tracks.find(track => track.kind === "audio");
    audio?.mediaStreamTrack.addEventListener("ended", async () => {
        if (session.screenGeneration !== generation || session.screenMediaStreamId === null) return;
        session.screenTracks = session.screenTracks.filter(track => track !== audio);
        session.screenPublicationMutation = true;
        try { await session.room.localParticipant.unpublishTrack(audio, true); }
        catch { }
        finally { session.screenPublicationMutation = false; }
        updateLocalScreenAudioAvailability(session, false);
        refreshViewers(session, session.screenMediaStreamId);
    }, { once: true });
}

export async function startScreenShare(id) {
    const session = sessions.get(id); if (!session) throw new Error("LiveKit media is not connected.");
    if (!navigator.mediaDevices?.getDisplayMedia)
        throw new DOMException("Display capture is unavailable on this browser or device.", "NotSupportedError");
    if (session.screenTracks.length) return switchScreenShare(id);
    const streamId = crypto.randomUUID(), mediaStreamId = `iridium-screen-${streamId.replaceAll("-", "")}`;
    const tracks = await captureScreenTracks(session);
    let publications;
    try { publications = await publishScreenTracks(session, tracks, mediaStreamId); }
    catch (error) {
        for (const track of tracks) {
            try { await session.room.localParticipant.unpublishTrack(track, true); } catch { }
            track.stop();
        }
        throw error;
    }
    session.screenTracks = tracks; session.screenStreamId = streamId; session.screenMediaStreamId = mediaStreamId;
    session.screenAudioAvailable = publishedScreenAudio(publications);
    wireScreenEnded(session, tracks, ++session.screenGeneration);
    if (session.diagnostics) console.debug("LiveKit screen share state", {
        screenVideoTrack: tracks.some(track => track.kind === "video"),
        screenAudioTrack: tracks.some(track => track.kind === "audio"),
        screenVideoPublished: publications.some(publication => publication?.kind === "video"),
        screenAudioPublished: session.screenAudioAvailable
    });
    return { streamId, kind: 0, hasAudio: session.screenAudioAvailable, mediaStreamId };
}

export async function switchScreenShare(id) {
    const session = sessions.get(id); if (!session?.screenTracks.length)
        throw new Error("There is no active screen share to switch.");
    if (!navigator.mediaDevices?.getDisplayMedia)
        throw new DOMException("Display capture is unavailable on this browser or device.", "NotSupportedError");
    // Capture first. Picker cancellation or capture failure therefore cannot disturb the old share.
    const replacement = await captureScreenTracks(session);
    const { previous, publications } = await replacePublishedScreenTracks(session, replacement);
    session.screenTracks = replacement;
    session.screenAudioAvailable = publishedScreenAudio(publications);
    wireScreenEnded(session, replacement, ++session.screenGeneration);
    for (const track of previous) track.stop();
    refreshViewers(session, session.screenMediaStreamId);
    return {
        streamId: session.screenStreamId, kind: 0,
        hasAudio: session.screenAudioAvailable,
        mediaStreamId: session.screenMediaStreamId
    };
}

export async function replacePublishedScreenTracks(session, replacement) {
    const previous = session.screenTracks.slice();
    let publications;
    session.screenPublicationMutation = true;
    try {
        for (const track of previous)
            await session.room.localParticipant.unpublishTrack(track, false);
        publications = await publishScreenTracks(session, replacement, session.screenMediaStreamId);
    } catch (error) {
        for (const track of replacement) {
            try { await session.room.localParticipant.unpublishTrack(track, true); } catch { }
            track.stop();
        }
        // Best-effort rollback keeps the existing browser capture and Iridium stream alive.
        try { await publishScreenTracks(session, previous, session.screenMediaStreamId); } catch { }
        throw error;
    }
    finally { session.screenPublicationMutation = false; }
    return { previous, publications };
}

export async function stopScreenShare(id, reason) {
    const session = sessions.get(id); if (!session || session.screenTracks.length === 0) return;
    const tracks = session.screenTracks.splice(0); session.screenStreamId = null;
    session.screenMediaStreamId = null; session.screenGeneration++; session.screenAudioAvailable = false;
    session.screenPublicationMutation = true;
    try {
        for (const track of tracks) {
            try { await session.room.localParticipant.unpublishTrack(track); } catch { }
            track.stop();
        }
    }
    finally { session.screenPublicationMutation = false; }
    if (reason === "BrowserEnded") await callback(session, "OnScreenShareEnded", ...(session.kind === "call" ? [session.peerGeneration, reason] : [reason]));
}

export async function setStreamSubscriptionForSession(session, iridiumStreamId, mediaStreamId,
        participantIdentity, subscribed) {
    const previous = session.watched.get(mediaStreamId);
    const watch = previous ?? { iridiumStreamId, mediaStreamId, participantIdentity, audioAvailable:null };
    Object.assign(watch, { iridiumStreamId, mediaStreamId, participantIdentity });
    if (subscribed) {
        session.watched.set(mediaStreamId, watch);
        try { if (session.audioContext?.state === "suspended") await session.audioContext.resume(); }
        catch (error) {
            if (session.diagnostics) console.debug("[ScreenWatch] audio activation blocked", { name:error?.name });
        }
        reconcileScreenWatch(session, watch);
        return;
    }
    session.watched.delete(mediaStreamId);
    const participant = participantForWatch(session, watch);
    if (participant && participant !== session.room.localParticipant)
        for (const publication of allPublications(participant))
            if (publicationMatchesWatch(publication, watch)) setScreenSubscription(session, publication, false);
    refreshViewers(session, mediaStreamId);
}

export async function setStreamSubscription(id, iridiumStreamId, mediaStreamId, participantIdentity, subscribed) {
    const session = sessions.get(id); if (!session) return;
    await setStreamSubscriptionForSession(session, iridiumStreamId, mediaStreamId, participantIdentity, subscribed);
}

export function attachStreamViewerForSession(session, mediaStreamId, elementId, audioMuted, volumePercent = 100) {
    const previous = session.viewers.get(elementId);
    if (previous) detachStreamViewerForSession(session, elementId);
    session.viewers.set(elementId, { mediaStreamId, elementId, audioMuted,
        volumePercent: Math.min(300, Math.max(0, Number(volumePercent) || 0)),
        audioTrackId: null, audioPlayback: null, pendingAudioTrackId: null,
        pendingAudioAttachment: null, refreshGeneration: 0,
        videoElement: null, preventVideoToggle: null, resumeLiveVideo: null, resumePending: false });
    const watch = session.watched.get(mediaStreamId);
    if (watch) reconcileScreenWatch(session, watch); else refreshViewers(session, mediaStreamId);
}

export function attachStreamViewer(id, mediaStreamId, elementId, audioMuted, volumePercent = 100) {
    const session = sessions.get(id); if (!session) return;
    attachStreamViewerForSession(session, mediaStreamId, elementId, audioMuted, volumePercent);
}

export function detachStreamViewerForSession(session, elementId) {
    const viewer = session.viewers.get(elementId), element = document.getElementById(elementId);
    session.viewers.delete(elementId);
    viewer?.audioAbort?.abort();
    if (viewer) viewer.refreshGeneration++;
    if (viewer?.videoElement) {
        if (viewer.frameCallback != null) viewer.videoElement.cancelVideoFrameCallback?.(viewer.frameCallback);
        viewer.videoElement.removeEventListener("click", viewer.preventVideoToggle);
        viewer.videoElement.removeEventListener("pause", viewer.resumeLiveVideo);
    }
    if (element) { element.pause?.(); element.srcObject = null; }
    if (viewer?.audioPlayback) {
        destroyRemoteVoicePlayback(viewer.audioPlayback);
        if (session.diagnostics) console.debug("[ScreenWatch] audio playback disposed", {
            mediaStreamId:viewer.mediaStreamId
        });
    }
    if (viewer && !Array.from(session.viewers.values()).some(v => v.mediaStreamId === viewer.mediaStreamId))
        refreshViewers(session, viewer.mediaStreamId);
}

export function detachStreamViewer(id, elementId) {
    const session = sessions.get(id); if (!session) return;
    detachStreamViewerForSession(session, elementId);
}

export async function setStreamAudioMuted(id, elementId, muted) {
    const session = sessions.get(id), viewer = session?.viewers.get(elementId);
    if (viewer) viewer.audioMuted = muted;
    updateRemoteVoicePlayback(viewer?.audioPlayback, { locallyMuted: muted, deafened: session?.deafened });
    if (session?.diagnostics) console.debug("LiveKit screen audio mute changed", {
        muted, audioAttached: !!viewer?.audioPlayback, playbackMode: viewer?.audioPlayback?.mode ?? "none"
    });
    if (!muted && viewer?.audioPlayback) {
        await resumeRemoteVoicePlayback(viewer.audioPlayback);
        const element = document.getElementById(elementId);
        if (element) element.dataset.audioBlocked = viewer.audioPlayback.playBlocked ? "true" : "false";
    }
}

export async function setStreamAudioVolume(id, elementId, volumePercent) {
    const session = sessions.get(id), viewer = session?.viewers.get(elementId);
    if (!viewer) return;
    viewer.volumePercent = Math.min(300, Math.max(0, Number(volumePercent) || 0));
    updateRemoteVoicePlayback(viewer.audioPlayback, { volumePercent: viewer.volumePercent,
        locallyMuted: viewer.audioMuted, deafened: session.deafened });
    if (session.diagnostics) console.debug("LiveKit screen audio volume changed", {
        volumePercent: viewer.volumePercent, audioAttached: !!viewer.audioPlayback,
        effectiveGain: viewer.audioPlayback?.gain?.gain?.value ??
            (viewer.audioMuted || session.deafened ? 0 : viewer.volumePercent / 100),
        playbackMode: viewer.audioPlayback?.mode ?? "none"
    });
    await resumeRemoteVoicePlayback(viewer.audioPlayback);
    const element = document.getElementById(elementId);
    if (element) element.dataset.audioBlocked = viewer.audioPlayback?.playBlocked ? "true" : "false";
}

export function requestStreamFullscreen(id, elementId) { return document.getElementById(elementId)?.requestFullscreen?.(); }

export function captureStreamThumbnail(id, mediaStreamId) {
    const session = sessions.get(id), track = session && matchingTracks(session, mediaStreamId).find(t => t.kind === "video");
    if (!track) return null;
    const video = document.createElement("video"), canvas = document.createElement("canvas");
    video.srcObject = new MediaStream([track]); video.muted = true;
    return video.play().then(() => { canvas.width = 320; canvas.height = Math.max(1, Math.round(320 * video.videoHeight / video.videoWidth)); canvas.getContext("2d").drawImage(video, 0, 0, canvas.width, canvas.height); video.srcObject = null; return canvas.toDataURL("image/jpeg", .72); });
}

export async function disconnect(id, reason) {
    const session = sessions.get(id); if (!session) return;
    await stopScreenShare(id, reason);
    sessions.delete(id);
    session.longTaskObserver?.disconnect();
    if (session.speakingFrame) cancelAnimationFrame(session.speakingFrame);
    session.speakingAnalyser?.cleanup?.();
    for (const playback of session.playbacks.values()) destroyRemoteVoicePlayback(playback);
    for (const elementId of Array.from(session.viewers.keys())) detachStreamViewerForSession(session, elementId);
    await session.room.disconnect();
    await session.noiseProcessor?.destroy();
    await session.audioContext?.close?.();
}
