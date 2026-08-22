import { createRemoteVoicePlayback, updateRemoteVoicePlayback, destroyRemoteVoicePlayback } from './voicePlayback.js';
// Community voice media stays behind this module so DevelopmentPeerMesh can be replaced by NodeSfu.
// TODO: Remove temporary Community voice diagnostics once voice channels are stable.
const sessions = new Map();

function requireSession(id) {
    const session = sessions.get(id);
    if (!session) throw new Error("Community voice media session is no longer active.");
    return session;
}

function normalizeIceServers(servers) {
    return (servers ?? []).map(server => ({
        urls: server.urls,
        username: server.username ?? undefined,
        credential: server.credential ?? undefined
    }));
}

function publishSpeaking(session, speaking) {
    speaking = speaking && !session.muted;
    if (session.speaking === speaking) return;
    session.speaking = speaking;
    session.callback.invokeMethodAsync("OnSpeakingChanged", speaking).catch(() => {});
}

async function diagnostic(session, event, peerState = null, values = {}) {
    if (!session.diagnostics) return;
    const snapshot = {
        event,
        remoteParticipantId: peerState?.remoteParticipantId ?? null,
        localStreamPresent: !!session.localStream,
        localAudioTracks: session.localStream?.getAudioTracks().length ?? 0,
        attachedSenderCount: peerState?.peer.getSenders().filter(sender => sender.track?.kind === "audio").length ?? 0,
        connectionState: peerState?.peer.connectionState ?? null,
        iceConnectionState: peerState?.peer.iceConnectionState ?? null,
        localIceGenerated: peerState?.localIceGenerated ?? 0,
        remoteIceReceived: peerState?.remoteIceReceived ?? 0,
        remoteTrackCount: peerState?.remoteStream.getAudioTracks().length ?? 0,
        remoteAudioElements: session.peers ? [...session.peers.values()].filter(value => value.playback?.element).length : 0,
        remoteAudioPlaySucceeded: peerState?.remoteAudioPlaySucceeded ?? false,
        packetsSent: values.packetsSent ?? null,
        packetsReceived: values.packetsReceived ?? null,
        bytesSent: values.bytesSent ?? null,
        bytesReceived: values.bytesReceived ?? null,
        remoteTrackReadyState: values.readyState ?? null,
        remoteTrackMuted: values.muted ?? null,
        elementMuted: values.elementMuted ?? null,
        elementVolume: values.elementVolume ?? null,
        audioContextState: values.audioContextState ?? null,
        gainValue: values.gainValue ?? null,
        errorName: values.name ?? null,
        errorMessage: values.message ?? null
    };
    console.debug("[Iridium Community Voice]", snapshot);
    await session.callback.invokeMethodAsync("OnDiagnostic", snapshot).catch(() => {});
}

async function startVad(session) {
    const AudioContextType = window.AudioContext ?? window.webkitAudioContext;
    if (!AudioContextType) return;
    const context = new AudioContextType();
    const source = context.createMediaStreamSource(session.localStream);
    const analyser = context.createAnalyser();
    analyser.fftSize = 512;
    analyser.smoothingTimeConstant = 0.72;
    source.connect(analyser);
    if (context.state === "suspended") await context.resume().catch(() => {});
    const samples = new Float32Array(analyser.fftSize);
    Object.assign(session, { context, source, analyser, noiseFloor: 0.008, aboveThresholdFrames: 0, lastVoiceAt: 0 });
    const sample = timestamp => {
        if (!sessions.has(session.id)) return;
        analyser.getFloatTimeDomainData(samples);
        let sum = 0;
        for (const value of samples) sum += value * value;
        const rms = Math.sqrt(sum / samples.length);
        if (!session.speaking && rms < 0.035) session.noiseFloor = session.noiseFloor * 0.97 + rms * 0.03;
        const startThreshold = Math.max(0.032, session.noiseFloor * 3.2);
        const stopThreshold = Math.max(0.019, session.noiseFloor * 2.0);
        if (!session.muted && rms >= startThreshold) {
            session.aboveThresholdFrames++;
            session.lastVoiceAt = timestamp;
            if (session.aboveThresholdFrames >= 2) publishSpeaking(session, true);
        } else {
            session.aboveThresholdFrames = 0;
            if (session.speaking && (session.muted || rms < stopThreshold) && timestamp - session.lastVoiceAt >= 420)
                publishSpeaking(session, false);
        }
        session.vadFrame = requestAnimationFrame(sample);
    };
    session.vadFrame = requestAnimationFrame(sample);
}

function candidateDto(candidate) {
    const value = typeof candidate.toJSON === "function" ? candidate.toJSON() : candidate;
    return {
        candidate: value.candidate,
        sdpMid: value.sdpMid ?? null,
        sdpMLineIndex: value.sdpMLineIndex ?? null,
        usernameFragment: value.usernameFragment ?? null
    };
}

function closePeer(session, remoteParticipantId, reason) {
    const state = session.peers.get(remoteParticipantId);
    if (!state) return;
    session.peers.delete(remoteParticipantId);
    if (state.connectionTimer !== null) clearTimeout(state.connectionTimer);
    state.peer.onicecandidate = null;
    state.peer.ontrack = null;
    state.peer.close();
    destroyRemoteVoicePlayback(state.playback);
    void diagnostic(session, `PeerClosed: ${reason}`, state);
}

function createPeer(session, remoteParticipantId, negotiationId) {
    const existing = session.peers.get(remoteParticipantId);
    if (existing) {
        if (negotiationId) existing.negotiationId = negotiationId;
        return existing;
    }
    const peer = new RTCPeerConnection({ iceServers: normalizeIceServers(session.mediaSession.iceServers) });
    const state = {
        remoteParticipantId, negotiationId, peer, remoteStream: new MediaStream(),
        pendingIce: [], localIceGenerated: 0, remoteIceReceived: 0,
        remoteAudioPlaySucceeded: false, connectionTimer: null, playback: null,
        remoteAccountId: session.participantAccounts.get(remoteParticipantId) ?? null
    };
    session.peers.set(remoteParticipantId, state);

    peer.onicecandidate = event => {
        if (!event.candidate || !state.negotiationId) return;
        state.localIceGenerated++;
        void diagnostic(session, "IceGenerated", state);
        session.callback.invokeMethodAsync("OnIceCandidate", remoteParticipantId, state.negotiationId,
            candidateDto(event.candidate)).catch(error => reportError(session, "ICE signaling failed", error));
    };
    peer.onconnectionstatechange = () => {
        if (peer.connectionState === "connected" && state.connectionTimer !== null) {
            clearTimeout(state.connectionTimer);
            state.connectionTimer = null;
        }
        void diagnostic(session, `ConnectionState:${peer.connectionState}`, state);
    };
    peer.oniceconnectionstatechange = () => void diagnostic(session, `IceConnectionState:${peer.iceConnectionState}`, state);
    peer.ontrack = async event => {
        const stream = event.streams?.[0];
        if (stream) state.remoteStream = stream;
        else if (!state.remoteStream.getTracks().includes(event.track)) state.remoteStream.addTrack(event.track);
        void diagnostic(session, "RemoteTrackReceived", state, { readyState:event.track.readyState, muted:event.track.muted });
        event.track.onmute = () => void diagnostic(session, "RemoteTrackMuted", state);
        event.track.onunmute = () => void diagnostic(session, "RemoteTrackUnmuted", state);
        destroyRemoteVoicePlayback(state.playback);
        const preference = session.preferences.get(state.remoteAccountId) ?? { volumePercent:100, locallyMuted:false };
        state.playback = await createRemoteVoicePlayback(state.remoteStream, session.context, {
            ...preference, deafened:session.deafened,
            diagnostic:(name, values) => void diagnostic(session, name, state, values)
        });
        state.remoteAudioPlaySucceeded = state.playback.mode !== "none";
    };
    for (const track of session.localStream.getAudioTracks()) {
        peer.addTrack(track, session.localStream);
        void diagnostic(session, "LocalTrackAdded", state, { trackState: track.readyState });
    }
    void diagnostic(session, "PeerCreated", state);
    state.connectionTimer = setTimeout(() => {
        if (peer.connectionState !== "connected") {
            reportError(session, `Community media connection to ${remoteParticipantId} timed out`,
                new DOMException(`Peer remained ${peer.connectionState}.`, "TimeoutError"));
            closePeer(session, remoteParticipantId, "connection timeout");
        }
    }, 20000);
    return state;
}

async function flushIce(session, state) {
    if (!state.peer.remoteDescription) return;
    const queued = state.pendingIce.splice(0);
    for (const candidate of queued) await state.peer.addIceCandidate(candidate);
    if (queued.length) await diagnostic(session, "QueuedIceAdded", state);
}

async function startOffer(session, remoteParticipantId) {
    const negotiationId = crypto.randomUUID();
    const state = createPeer(session, remoteParticipantId, negotiationId);
    const offer = await state.peer.createOffer();
    await diagnostic(session, "OfferCreated", state);
    await state.peer.setLocalDescription(offer);
    await session.callback.invokeMethodAsync("OnOfferCreated", remoteParticipantId, negotiationId,
        { type: state.peer.localDescription.type, sdp: state.peer.localDescription.sdp });
}

function reportError(session, prefix, error) {
    const message = `${prefix}: ${error?.name ?? "MediaError"}: ${error?.message ?? error}`;
    console.error("[Iridium Community Voice]", message);
    session.callback.invokeMethodAsync("OnMediaError", message).catch(() => {});
}

async function collectStats(session) {
    const snapshots = [];
    for (const state of session.peers.values()) {
        let packetsSent = 0, packetsReceived = 0, bytesSent = 0, bytesReceived = 0;
        const reports = await state.peer.getStats();
        reports.forEach(report => {
            if (report.type === "outbound-rtp" && report.kind === "audio") {
                packetsSent += report.packetsSent ?? 0;
                bytesSent += report.bytesSent ?? 0;
            } else if (report.type === "inbound-rtp" && report.kind === "audio") {
                packetsReceived += report.packetsReceived ?? 0;
                bytesReceived += report.bytesReceived ?? 0;
            }
        });
        await diagnostic(session, "StatsSnapshot", state,
            { packetsSent, packetsReceived, bytesSent, bytesReceived });
        snapshots.push({ remoteParticipantId: state.remoteParticipantId, packetsSent, packetsReceived,
            bytesSent, bytesReceived, remoteTrackCount: state.remoteStream.getAudioTracks().length,
            remoteAudioPlaySucceeded: state.remoteAudioPlaySucceeded });
    }
    return snapshots;
}

export async function getStatsSnapshot(id) {
    return collectStats(requireSession(id));
}

export async function connect(callback, mediaSession, room, localAccountId, preferences = []) {
    if (!navigator.mediaDevices?.getUserMedia)
        throw new Error("This browser does not support microphone capture.");
    let localStream;
    try {
        localStream = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch (error) {
        throw new Error(`${error?.name ?? "MediaError"}: ${error?.message ?? "Microphone access failed."}`);
    }
    const id = crypto.randomUUID();
    const localParticipant = (room.participants ?? []).find(value => value.participantId === mediaSession.participantId);
    const session = {
        id, callback, mediaSession, room, localAccountId, localParticipantId: mediaSession.participantId,
        localStream, peers: new Map(), muted: localParticipant?.muted === true,
        deafened: localParticipant?.deafened === true, speaking: false,
        vadFrame: null, context: null, source: null, analyser: null,
        diagnostics: mediaSession.diagnosticsEnabled === true, statsTimer: null,
        participantAccounts:new Map((room.participants ?? []).map(value => [value.participantId, value.accountId])),
        preferences:new Map((preferences ?? []).map(value => [value.remoteAccountId, value]))
    };
    sessions.set(id, session);
    try {
        for (const track of localStream.getAudioTracks()) track.enabled = !session.muted;
        await startVad(session);
        await diagnostic(session, "JoinedRoom");
        if (mediaSession.provider !== "development-peer-mesh" && mediaSession.provider !== "none") {
            throw new Error(`Unsupported Community voice media provider: ${mediaSession.provider}`);
        }
        return id;
    } catch (error) {
        await disconnect(id, "initialization failed");
        throw error;
    }
}

export async function start(id) {
    const session = requireSession(id);
    if (session.mediaSession.provider !== "development-peer-mesh") return;
    for (const participant of session.room.participants ?? []) {
        if (participant.participantId !== session.localParticipantId)
            await startOffer(session, participant.participantId);
    }
    session.statsTimer = setInterval(() => void collectStats(session), 10000);
}

export function setMuted(id, muted) {
    const session = requireSession(id);
    session.muted = muted;
    for (const track of session.localStream.getAudioTracks()) track.enabled = !muted;
    if (muted) publishSpeaking(session, false);
    void diagnostic(session, muted ? "Muted" : "Unmuted");
}

export function setDeafened(id, deafened) {
    const session = requireSession(id);
    session.deafened = deafened;
    for (const state of session.peers.values()) {
        updateRemoteVoicePlayback(state.playback, { deafened });
    }
    void diagnostic(session, deafened ? "Deafened" : "Undeafened");
}

export function participantJoined(id, participant) {
    const session = requireSession(id);
    if (participant.participantId === session.localParticipantId) return;
    session.participantAccounts.set(participant.participantId, participant.accountId);
    // The joining client is offerer. Existing clients wait, which prevents offer glare.
    void diagnostic(session, "ParticipantJoinedAwaitingOffer", null,
        { remoteParticipantId: participant.participantId });
}

export function setParticipantPreference(id, preference) {
    const session = requireSession(id);
    session.preferences.set(preference.remoteAccountId, preference);
    for (const state of session.peers.values())
        if (state.remoteAccountId === preference.remoteAccountId)
            updateRemoteVoicePlayback(state.playback, preference);
}

export function participantLeft(id, participantId) {
    const session = requireSession(id);
    closePeer(session, participantId, "participant left");
}

export async function handleOffer(id, event) {
    const session = requireSession(id);
    const state = createPeer(session, event.sourceParticipantId, event.negotiationId);
    try {
        await state.peer.setRemoteDescription(event.description);
        await diagnostic(session, "OfferReceived", state);
        await flushIce(session, state);
        const answer = await state.peer.createAnswer();
        await diagnostic(session, "AnswerCreated", state);
        await state.peer.setLocalDescription(answer);
        await session.callback.invokeMethodAsync("OnAnswerCreated", event.sourceParticipantId, event.negotiationId,
            { type: state.peer.localDescription.type, sdp: state.peer.localDescription.sdp });
    } catch (error) {
        reportError(session, "Offer handling failed", error);
    }
}

export async function handleAnswer(id, event) {
    const session = requireSession(id);
    const state = session.peers.get(event.sourceParticipantId);
    if (!state || state.negotiationId !== event.negotiationId) return;
    try {
        await state.peer.setRemoteDescription(event.description);
        await diagnostic(session, "AnswerReceived", state);
        await flushIce(session, state);
    } catch (error) {
        reportError(session, "Answer handling failed", error);
    }
}

export async function handleIceCandidate(id, event) {
    const session = requireSession(id);
    const state = createPeer(session, event.sourceParticipantId, event.negotiationId);
    state.remoteIceReceived++;
    const candidate = new RTCIceCandidate(event.candidate);
    try {
        if (!state.peer.remoteDescription) state.pendingIce.push(candidate);
        else await state.peer.addIceCandidate(candidate);
        await diagnostic(session, "IceReceived", state);
    } catch (error) {
        reportError(session, "Remote ICE candidate failed", error);
    }
}

export async function disconnect(id, reason = "unspecified") {
    const session = sessions.get(id);
    if (!session) return;
    sessions.delete(id);
    if (session.statsTimer !== null) clearInterval(session.statsTimer);
    if (session.vadFrame !== null) cancelAnimationFrame(session.vadFrame);
    publishSpeaking(session, false);
    for (const participantId of [...session.peers.keys()]) closePeer(session, participantId, reason);
    for (const track of session.localStream.getTracks()) track.stop();
    session.source?.disconnect();
    session.analyser?.disconnect();
    if (session.context) await session.context.close().catch(() => {});
    await diagnostic(session, "Disconnected");
}
