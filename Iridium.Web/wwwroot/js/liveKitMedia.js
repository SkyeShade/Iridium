import { createRemoteVoicePlayback, updateRemoteVoicePlayback, destroyRemoteVoicePlayback } from "./voicePlayback.js";

const sessions = new Map();

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
}

function configurePublication(session, publication) {
    const source = publication.source;
    const { Track } = sdk();
    const microphone = source === Track.Source.Microphone;
    const screen = source === Track.Source.ScreenShare || source === Track.Source.ScreenShareAudio;
    if (publication.setSubscribed) publication.setSubscribed(microphone || (screen && session.watched.has(publicationName(publication))));
}

function wireRoom(session) {
    const { RoomEvent, Track } = sdk();
    const room = session.room;
    room.on(RoomEvent.TrackPublished, publication => configurePublication(session, publication));
    room.on(RoomEvent.TrackSubscribed, (track, publication, participant) => {
        if (publication.source === Track.Source.Microphone) makeAudioPlayback(session, participant, track);
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

function refreshViewers(session, mediaStreamId) {
    for (const viewer of session.viewers.values()) {
        if (viewer.mediaStreamId !== mediaStreamId) continue;
        const element = document.getElementById(viewer.elementId);
        if (!element) continue;
        element.srcObject = new MediaStream(matchingTracks(session, mediaStreamId));
        element.muted = viewer.audioMuted;
        element.playsInline = true;
        element.play?.().catch(() => {});
    }
}

async function connectCore(callbackRef, nodeSession, preferences, kind, peerGeneration = 0) {
    if (!nodeSession?.accessToken || !nodeSession?.publicUrl) throw new Error("The Node did not provide LiveKit room access.");
    const { Room, RoomEvent } = sdk();
    const id = crypto.randomUUID();
    const session = {
        id, kind, peerGeneration, callback: callbackRef, diagnostics: nodeSession.diagnosticsEnabled === true,
        room: new Room({ autoSubscribe: false, adaptiveStream: true, dynacast: true }),
        preferences: new Map((preferences ?? []).map(p => [accountKey(p.remoteAccountId), p])),
        playbacks: new Map(), viewers: new Map(), watched: new Set(), deafened: false,
        screenTracks: [], screenStreamId: null,
        audioContext: (globalThis.AudioContext || globalThis.webkitAudioContext)
            ? new (globalThis.AudioContext || globalThis.webkitAudioContext)() : null
    };
    sessions.set(id, session);
    wireRoom(session);
    try {
        await session.room.connect(nodeSession.publicUrl, nodeSession.accessToken, { autoSubscribe: false });
        await session.room.localParticipant.setMicrophoneEnabled(true);
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
}

export function setParticipantPreference(id, preference) {
    const session = sessions.get(id); if (!session) return;
    const key = accountKey(preference.remoteAccountId);
    session.preferences.set(key, preference);
    updateRemoteVoicePlayback(session.playbacks.get(key), { ...preference, deafened: session.deafened });
}

export async function startScreenShare(id) {
    const session = sessions.get(id); if (!session) throw new Error("LiveKit media is not connected.");
    await stopScreenShare(id, "Replaced");
    const { createLocalScreenTracks } = sdk();
    const streamId = crypto.randomUUID(), mediaStreamId = `iridium-screen-${streamId.replaceAll("-", "")}`;
    const tracks = await createLocalScreenTracks({ audio: true });
    session.screenTracks = tracks; session.screenStreamId = streamId;
    for (const track of tracks) {
        track.mediaStreamTrack.addEventListener("ended", () => stopScreenShare(id, "BrowserEnded"), { once: true });
        await session.room.localParticipant.publishTrack(track, { name: mediaStreamId, source: track.source });
    }
    return { streamId, kind: 0, hasAudio: tracks.some(t => t.kind === "audio"), mediaStreamId };
}

export async function stopScreenShare(id, reason) {
    const session = sessions.get(id); if (!session || session.screenTracks.length === 0) return;
    const tracks = session.screenTracks.splice(0); session.screenStreamId = null;
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
            if (publicationName(publication) === mediaStreamId) publication.setSubscribed(subscribed);
    refreshViewers(session, mediaStreamId);
}

export function attachStreamViewer(id, mediaStreamId, elementId, audioMuted) {
    const session = sessions.get(id); if (!session) return;
    session.viewers.set(elementId, { mediaStreamId, elementId, audioMuted });
    setStreamSubscription(id, mediaStreamId, true); refreshViewers(session, mediaStreamId);
}

export function detachStreamViewer(id, elementId) {
    const session = sessions.get(id); if (!session) return;
    const viewer = session.viewers.get(elementId), element = document.getElementById(elementId);
    if (element) { element.pause?.(); element.srcObject = null; }
    session.viewers.delete(elementId);
    if (viewer && !Array.from(session.viewers.values()).some(v => v.mediaStreamId === viewer.mediaStreamId))
        setStreamSubscription(id, viewer.mediaStreamId, session.watched.has(viewer.mediaStreamId));
}

export function setStreamAudioMuted(id, elementId, muted) {
    const session = sessions.get(id), viewer = session?.viewers.get(elementId);
    if (viewer) viewer.audioMuted = muted;
    const element = document.getElementById(elementId); if (element) element.muted = muted;
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
    for (const elementId of session.viewers.keys()) { const element = document.getElementById(elementId); if (element) element.srcObject = null; }
    await session.room.disconnect();
    await session.audioContext?.close?.();
}
