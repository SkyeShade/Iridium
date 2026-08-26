import { createRemoteVoicePlayback, updateRemoteVoicePlayback, destroyRemoteVoicePlayback,
    resumeRemoteVoicePlayback } from "./voicePlayback.js";

const sessions = new Map();

// getDisplayMedia treats these as an upper capture target, so smaller sources retain their
// native dimensions while 1440p/4K displays are no longer downscaled to LiveKit's 1080p30 default.
const screenCaptureTarget = Object.freeze({ width: 3840, height: 2160, frameRate: 60 });
const screenBitrateAnchors = Object.freeze([
    { pixels: 640 * 360, fps30: 1_000_000, fps60: 1_500_000 },
    { pixels: 1280 * 720, fps30: 3_500_000, fps60: 6_000_000 },
    { pixels: 1920 * 1080, fps30: 6_000_000, fps60: 10_000_000 },
    { pixels: 2560 * 1440, fps30: 10_000_000, fps60: 16_000_000 },
    { pixels: 3840 * 2160, fps30: 20_000_000, fps60: 30_000_000 }
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

function allPublications(participant) {
    return Array.from(participant?.trackPublications?.values?.() ?? []);
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
    return Math.max(1_000_000, Math.min(30_000_000, Math.round(target / 250_000) * 250_000));
}

function screenSharePublishOptions(track, mediaStreamId) {
    const { VideoPreset } = sdk();
    const settings = track.mediaStreamTrack.getSettings();
    const width = settings.width || 1920, height = settings.height || 1080;
    const frameRate = Math.max(1, Math.min(60, settings.frameRate || 30));
    const maxBitrate = screenShareBitrate(width, height, frameRate);
    const lowWidth = Math.max(1, Math.floor(width / 2)), lowHeight = Math.max(1, Math.floor(height / 2));
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

export function microphoneProfile(configuredBitrate) {
    const bitrate = Math.max(64_000, Math.min(128_000, Number(configuredBitrate) || 96_000));
    return {
        bitrate,
        capture: {
            channelCount: 1,
            echoCancellation: true,
            noiseSuppression: true,
            autoGainControl: true
        },
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
        qualityLimitationReasons: [...new Set(rows.map(row => row.qualityLimitationReason).filter(Boolean))],
        packetsLost: lossRows.reduce((sum, row) => sum + Number(row.packetsLost || 0), 0),
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
        console.debug(`LiveKit screen share ${direction}`, {
            ...details, codec: second?.codec || "unknown", activeLayers: second?.layers ?? 0,
            bitrateBps: bitrate, framesPerSecond: second?.framesPerSecond ?? 0,
            frameWidth: second?.frameWidth ?? 0, frameHeight: second?.frameHeight ?? 0,
            qualityLimitationReasons: second?.qualityLimitationReasons ?? [],
            packetsLost: second?.packetsLost ?? 0, rttMs: second?.rttMs ?? null,
            senderEncodings: direction === "outbound" ? senderEncodingSummary(track) : undefined
        });
    } catch (error) {
        console.debug("LiveKit screen share stats unavailable", { direction, error: error?.name });
    }
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

async function diagnoseAudioTransport(session, track, direction) {
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
        console.debug(`LiveKit microphone ${direction}`, {
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
    if (subscribed && publication.source === Track.Source.ScreenShare && publication.setVideoQuality) {
        publication.setVideoQuality(VideoQuality.HIGH);
        if (session.diagnostics) console.debug("LiveKit screen share subscription", { selectedQuality: "HIGH" });
    }
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

function configurePublication(session, publication) {
    const source = publication.source;
    const { Track } = sdk();
    const microphone = source === Track.Source.Microphone;
    const screen = source === Track.Source.ScreenShare || source === Track.Source.ScreenShareAudio;
    if (publication.setSubscribed) setScreenSubscription(session, publication, microphone || (screen && session.watched.has(publicationName(publication))));
}

function wireRoom(session) {
    const { RoomEvent, Track } = sdk();
    const room = session.room;
    room.on(RoomEvent.TrackPublished, publication => configurePublication(session, publication));
    room.on(RoomEvent.TrackSubscribed, (track, publication, participant) => {
        if (publication.source === Track.Source.Microphone) makeAudioPlayback(session, participant, track);
        if (publication.source === Track.Source.ScreenShare)
            diagnoseVideoBitrate(session, track, "inbound", { selectedQuality: "HIGH" });
        refreshViewers(session, publicationName(publication));
    });
    room.on(RoomEvent.TrackUnsubscribed, (track, publication, participant) => {
        if (publication.source === Track.Source.Microphone) {
            const key = accountKey(participant.identity), playback = session.playbacks.get(key);
            if (playback) destroyRemoteVoicePlayback(playback);
            session.playbacks.delete(key);
        }
        refreshViewers(session, publicationName(publication));
    });
    room.on(RoomEvent.ParticipantConnected, participant => allPublications(participant).forEach(p => configurePublication(session, p)));
    room.on(RoomEvent.ParticipantDisconnected, participant => {
        const key = accountKey(participant.identity), playback = session.playbacks.get(key);
        if (playback) destroyRemoteVoicePlayback(playback);
        session.playbacks.delete(key);
    });
    room.on(RoomEvent.Reconnecting, () => reportState(session, "connecting"));
    room.on(RoomEvent.Reconnected, () => reportState(session, "connected"));
    room.on(RoomEvent.Disconnected, () => reportState(session, "disconnected"));
    room.on(RoomEvent.ConnectionStateChanged, state => {
        const mapped = state === "connected" ? "connected" : state === "disconnected" ? "disconnected" : "connecting";
        reportState(session, mapped);
    });
}

function startLocalSpeaking(session) {
    const { Track, createAudioAnalyser } = sdk();
    const publication = allPublications(session.room.localParticipant).find(p => p.source === Track.Source.Microphone);
    if (!publication?.track) return;
    try {
        const analyser = createAudioAnalyser(publication.track, { fftSize: 512, smoothingTimeConstant: .65 });
        session.speakingAnalyser = analyser;
        let speaking = false, quietFrames = 0;
        const tick = () => {
            if (!sessions.has(session.id)) return;
            const active = analyser.calculateVolume() > .035;
            if (active) quietFrames = 0; else quietFrames++;
            const next = active || (speaking && quietFrames < 8);
            if (next !== speaking) {
                speaking = next;
                callback(session, "OnSpeakingChanged", ...(session.kind === "call" ? [session.peerGeneration, speaking] : [speaking]));
            }
            session.speakingFrame = requestAnimationFrame(tick);
        };
        tick();
    } catch (error) {
        if (session.diagnostics) console.debug("LiveKit local speaking analysis unavailable", error?.name);
    }
}

function matchingTracks(session, mediaStreamId) {
    const publications = [
        ...allPublications(session.room.localParticipant),
        ...Array.from(session.room.remoteParticipants.values()).flatMap(allPublications)
    ].filter(p => publicationName(p) === mediaStreamId && p.track?.mediaStreamTrack);
    return publications.map(p => p.track.mediaStreamTrack);
}

async function refreshViewers(session, mediaStreamId) {
    for (const viewer of session.viewers.values()) {
        if (viewer.mediaStreamId !== mediaStreamId) continue;
        const refreshGeneration = ++viewer.refreshGeneration;
        const element = document.getElementById(viewer.elementId);
        if (!element) continue;
        const tracks = matchingTracks(session, mediaStreamId);
        const videoTracks = tracks.filter(track => track.kind === "video");
        const audioTrack = tracks.find(track => track.kind === "audio");
        element.srcObject = new MediaStream(videoTracks);
        // Shared audio has exactly one playback path: a separate GainNode graph. The video is
        // always muted so attaching the combined LiveKit stream cannot duplicate audio.
        element.muted = true;
        element.playsInline = true;
        try { await element.play?.(); }
        catch (error) {
            if (session.diagnostics) console.debug("LiveKit screen video play blocked", { name: error?.name });
        }
        if (viewer.audioTrackId === audioTrack?.id) continue;
        if (viewer.audioPlayback) destroyRemoteVoicePlayback(viewer.audioPlayback);
        viewer.audioPlayback = null;
        viewer.audioTrackId = audioTrack?.id ?? null;
        if (!audioTrack) continue;
        const playback = await createRemoteVoicePlayback(new MediaStream([audioTrack]), session.audioContext, {
            volumePercent: viewer.volumePercent, minimumVolumePercent: 0,
            locallyMuted: viewer.audioMuted, deafened: session.deafened,
            diagnostic: (event, details) => session.diagnostics && console.debug("LiveKit screen audio", event, {
                mediaStreamId, volumePercent: viewer.volumePercent, muted: viewer.audioMuted,
                deafened: session.deafened, ...details
            })
        });
        if (viewer.refreshGeneration !== refreshGeneration) {
            destroyRemoteVoicePlayback(playback);
            continue;
        }
        viewer.audioPlayback = playback;
        element.dataset.audioBlocked = playback.playBlocked ? "true" : "false";
    }
}

async function connectCore(callbackRef, nodeSession, preferences, kind, peerGeneration = 0) {
    if (!nodeSession?.accessToken || !nodeSession?.publicUrl) throw new Error("The Node did not provide LiveKit room access.");
    const { Room, RoomEvent } = sdk();
    const id = crypto.randomUUID();
    const microphone = microphoneProfile(nodeSession.voiceBitrate);
    const voiceBitrate = microphone.bitrate;
    const session = {
        id, kind, peerGeneration, callback: callbackRef, diagnostics: nodeSession.diagnosticsEnabled === true,
        voiceBitrate,
        // Screen viewers are deliberately subscribed by Watch/Stop Watching rather than SDK-managed elements.
        room: new Room({ autoSubscribe: false, adaptiveStream: false, dynacast: true }),
        preferences: new Map((preferences ?? []).map(p => [accountKey(p.remoteAccountId), p])),
        playbacks: new Map(), viewers: new Map(), watched: new Set(), deafened: false,
        screenTracks: [], screenStreamId: null, screenMediaStreamId: null, screenGeneration: 0,
        audioContext: (globalThis.AudioContext || globalThis.webkitAudioContext)
            ? new (globalThis.AudioContext || globalThis.webkitAudioContext)() : null
    };
    sessions.set(id, session);
    wireRoom(session);
    try {
        await session.room.connect(nodeSession.publicUrl, nodeSession.accessToken, { autoSubscribe: false });
        const microphonePublication = await session.room.localParticipant.setMicrophoneEnabled(
            true, microphone.capture, microphone.publish);
        if (session.diagnostics && microphonePublication?.track) {
            const settings = microphonePublication.track.mediaStreamTrack.getSettings();
            console.debug("LiveKit microphone published", {
                codec: "opus", targetBitrateBps: voiceBitrate,
                channelCount: settings.channelCount || 1, sampleRate: settings.sampleRate || null,
                dtx: true, red: true, inBandFec: "browser-negotiated",
                echoCancellation: settings.echoCancellation ?? true,
                noiseSuppression: settings.noiseSuppression ?? true,
                autoGainControl: settings.autoGainControl ?? true,
                senderEncodings: senderEncodingSummary(microphonePublication.track)
            });
            diagnoseAudioTransport(session, microphonePublication.track, "outbound");
        }
        startLocalSpeaking(session);
        for (const participant of session.room.remoteParticipants.values())
            allPublications(participant).forEach(p => configurePublication(session, p));
        await reportState(session, "connected");
        if (session.diagnostics) console.debug("LiveKit connected", { provider: nodeSession.provider, roomKind: nodeSession.roomKind });
        return id;
    } catch (error) {
        sessions.delete(id);
        await session.room.disconnect();
        throw error;
    }
}

export function connectCall(callbackRef, configuration, context, preferences) {
    return connectCore(callbackRef, configuration.nodeSession, preferences, "call", context.peerGeneration);
}

export function connectCommunity(callbackRef, mediaSession, preferences) {
    return connectCore(callbackRef, mediaSession.nodeSession, preferences, "community");
}

export async function setMuted(id, muted) {
    const session = sessions.get(id); if (!session) return;
    await session.room.localParticipant.setMicrophoneEnabled(!muted);
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
        width: { ideal: screenCaptureTarget.width }, height: { ideal: screenCaptureTarget.height },
        frameRate: { ideal: screenCaptureTarget.frameRate, max: screenCaptureTarget.frameRate }
    };
    return captureOptions;
}

async function captureScreenTracks() {
    const { LocalVideoTrack, LocalAudioTrack, Track } = sdk();
    const supported = navigator.mediaDevices?.getSupportedConstraints?.() ?? {};
    const media = await navigator.mediaDevices.getDisplayMedia(
        screenShareCaptureOptions(supported, sdk().getBrowser?.()?.name === "Safari"));
    const video = media.getVideoTracks()[0];
    if (!video) {
        media.getTracks().forEach(track => track.stop());
        throw new DOMException("Screen capture did not provide a video track.", "NotFoundError");
    }
    const videoTrack = new LocalVideoTrack(video, undefined, false);
    videoTrack.source = Track.Source.ScreenShare;
    const tracks = [videoTrack], audio = media.getAudioTracks()[0];
    if (audio) {
        const audioTrack = new LocalAudioTrack(audio, undefined, false);
        audioTrack.source = Track.Source.ScreenShareAudio;
        tracks.push(audioTrack);
    }
    return tracks;
}

async function publishScreenTracks(session, tracks, mediaStreamId) {
    for (const track of tracks) {
        if (track.kind === "video") {
            track.mediaStreamTrack.contentHint = "detail";
            const profile = screenSharePublishOptions(track, mediaStreamId);
            await session.room.localParticipant.publishTrack(track, profile.options);
            if (session.diagnostics) console.debug("LiveKit screen share published", {
                displayVideoTrackPresent: true, displayAudioTrackPresent: tracks.some(value => value.kind === "audio"),
                displaySurface: track.mediaStreamTrack.getSettings().displaySurface ?? null,
                audioTrackPublished: tracks.some(value => value.kind === "audio"),
                ...profile.settings, contentHint: track.mediaStreamTrack.contentHint,
                codec: profile.options.videoCodec, targetMaxBitrateBps: profile.maxBitrate,
                configuredSimulcastLayers: profile.options.screenShareSimulcastLayers.length + 1,
                degradationPreference: profile.options.degradationPreference,
                senderEncodings: senderEncodingSummary(track)
            });
            diagnoseVideoBitrate(session, track, "outbound", {
                capturedWidth: profile.settings.width, capturedHeight: profile.settings.height,
                capturedFrameRate: profile.settings.frameRate, targetMaxBitrateBps: profile.maxBitrate,
                configuredSimulcastLayers: profile.options.screenShareSimulcastLayers.length + 1
            });
        } else {
            await session.room.localParticipant.publishTrack(track, {
                name: mediaStreamId, stream: mediaStreamId, source: track.source
            });
        }
    }
}

function wireScreenEnded(session, tracks, generation) {
    const video = tracks.find(track => track.kind === "video");
    video?.mediaStreamTrack.addEventListener("ended", () => {
        if (session.screenGeneration === generation)
            stopScreenShare(session.id, "BrowserEnded");
    }, { once: true });
}

export async function startScreenShare(id) {
    const session = sessions.get(id); if (!session) throw new Error("LiveKit media is not connected.");
    if (!navigator.mediaDevices?.getDisplayMedia)
        throw new DOMException("Display capture is unavailable on this browser or device.", "NotSupportedError");
    if (session.screenTracks.length) return switchScreenShare(id);
    const streamId = crypto.randomUUID(), mediaStreamId = `iridium-screen-${streamId.replaceAll("-", "")}`;
    const tracks = await captureScreenTracks();
    try { await publishScreenTracks(session, tracks, mediaStreamId); }
    catch (error) {
        for (const track of tracks) {
            try { await session.room.localParticipant.unpublishTrack(track, true); } catch { }
            track.stop();
        }
        throw error;
    }
    session.screenTracks = tracks; session.screenStreamId = streamId; session.screenMediaStreamId = mediaStreamId;
    wireScreenEnded(session, tracks, ++session.screenGeneration);
    return { streamId, kind: 0, hasAudio: tracks.some(t => t.kind === "audio"), mediaStreamId };
}

export async function switchScreenShare(id) {
    const session = sessions.get(id); if (!session?.screenTracks.length)
        throw new Error("There is no active screen share to switch.");
    if (!navigator.mediaDevices?.getDisplayMedia)
        throw new DOMException("Display capture is unavailable on this browser or device.", "NotSupportedError");
    // Capture first. Picker cancellation or capture failure therefore cannot disturb the old share.
    const replacement = await captureScreenTracks();
    const previous = await replacePublishedScreenTracks(session, replacement);
    session.screenTracks = replacement;
    wireScreenEnded(session, replacement, ++session.screenGeneration);
    for (const track of previous) track.stop();
    refreshViewers(session, session.screenMediaStreamId);
    return {
        streamId: session.screenStreamId, kind: 0,
        hasAudio: replacement.some(track => track.kind === "audio"),
        mediaStreamId: session.screenMediaStreamId
    };
}

export async function replacePublishedScreenTracks(session, replacement) {
    const previous = session.screenTracks.slice();
    try {
        for (const track of previous)
            await session.room.localParticipant.unpublishTrack(track, false);
        await publishScreenTracks(session, replacement, session.screenMediaStreamId);
    } catch (error) {
        for (const track of replacement) {
            try { await session.room.localParticipant.unpublishTrack(track, true); } catch { }
            track.stop();
        }
        // Best-effort rollback keeps the existing browser capture and Iridium stream alive.
        try { await publishScreenTracks(session, previous, session.screenMediaStreamId); } catch { }
        throw error;
    }
    return previous;
}

export async function stopScreenShare(id, reason) {
    const session = sessions.get(id); if (!session || session.screenTracks.length === 0) return;
    const tracks = session.screenTracks.splice(0); session.screenStreamId = null;
    session.screenMediaStreamId = null; session.screenGeneration++;
    for (const track of tracks) {
        try { await session.room.localParticipant.unpublishTrack(track); } catch { }
        track.stop();
    }
    if (reason === "BrowserEnded") await callback(session, "OnScreenShareEnded", ...(session.kind === "call" ? [session.peerGeneration, reason] : [reason]));
}

export function setStreamSubscription(id, mediaStreamId, subscribed) {
    const session = sessions.get(id); if (!session) return;
    if (subscribed) session.watched.add(mediaStreamId); else session.watched.delete(mediaStreamId);
    for (const participant of session.room.remoteParticipants.values())
        for (const publication of allPublications(participant))
            if (publicationName(publication) === mediaStreamId) setScreenSubscription(session, publication, subscribed);
    refreshViewers(session, mediaStreamId);
}

export function attachStreamViewer(id, mediaStreamId, elementId, audioMuted, volumePercent = 100) {
    const session = sessions.get(id); if (!session) return;
    session.viewers.set(elementId, { mediaStreamId, elementId, audioMuted,
        volumePercent: Math.min(300, Math.max(0, Number(volumePercent) || 0)),
        audioTrackId: null, audioPlayback: null, refreshGeneration: 0 });
    setStreamSubscription(id, mediaStreamId, true); refreshViewers(session, mediaStreamId);
}

export function detachStreamViewer(id, elementId) {
    const session = sessions.get(id); if (!session) return;
    const viewer = session.viewers.get(elementId), element = document.getElementById(elementId);
    if (element) { element.pause?.(); element.srcObject = null; }
    if (viewer?.audioPlayback) destroyRemoteVoicePlayback(viewer.audioPlayback);
    session.viewers.delete(elementId);
    if (viewer && !Array.from(session.viewers.values()).some(v => v.mediaStreamId === viewer.mediaStreamId))
        setStreamSubscription(id, viewer.mediaStreamId, session.watched.has(viewer.mediaStreamId));
}

export async function setStreamAudioMuted(id, elementId, muted) {
    const session = sessions.get(id), viewer = session?.viewers.get(elementId);
    if (viewer) viewer.audioMuted = muted;
    updateRemoteVoicePlayback(viewer?.audioPlayback, { locallyMuted: muted, deafened: session?.deafened });
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
    if (session.speakingFrame) cancelAnimationFrame(session.speakingFrame);
    session.speakingAnalyser?.cleanup?.();
    for (const playback of session.playbacks.values()) destroyRemoteVoicePlayback(playback);
    for (const viewer of session.viewers.values()) {
        destroyRemoteVoicePlayback(viewer.audioPlayback);
        const element = document.getElementById(viewer.elementId); if (element) element.srcObject = null;
    }
    await session.room.disconnect();
    await session.audioContext?.close?.();
}
