let createRemoteVoicePlayback, updateRemoteVoicePlayback, destroyRemoteVoicePlayback;
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

function normalizeIceTransportPolicy(policy) { return policy === "relay" ? "relay" : "all"; }

function candidateType(candidate) {
    if (candidate?.type) return candidate.type;
    return candidate?.candidate?.match(/\btyp\s+(host|srflx|prflx|relay)\b/i)?.[1]?.toLowerCase() ?? null;
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
        signalingState: peerState?.peer.signalingState ?? null,
        iceGatheringState: peerState?.peer.iceGatheringState ?? null,
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
        framesEncoded: values.framesEncoded ?? null, framesDecoded: values.framesDecoded ?? null,
        framesDropped: values.framesDropped ?? null, frameWidth: values.frameWidth ?? null,
        frameHeight: values.frameHeight ?? null,
        hostCandidateAvailable: (peerState?.localCandidateTypes.host ?? 0) > 0,
        serverReflexiveCandidateAvailable: (peerState?.localCandidateTypes.srflx ?? 0) > 0,
        peerReflexiveCandidateAvailable: (peerState?.localCandidateTypes.prflx ?? 0) > 0,
        relayCandidateAvailable: (peerState?.localCandidateTypes.relay ?? 0) > 0,
        selectedLocalCandidateType: values.selectedLocalCandidateType ?? null,
        selectedRemoteCandidateType: values.selectedRemoteCandidateType ?? null,
        selectedCandidateProtocol: values.selectedCandidateProtocol ?? null,
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
    for (const stream of state.visualStreams.values()) session.visualStreams.delete(stream.id);
    void diagnostic(session, `PeerClosed: ${reason}`, state);
}

function createPeer(session, remoteParticipantId, negotiationId) {
    const existing = session.peers.get(remoteParticipantId);
    if (existing) {
        if (negotiationId) existing.negotiationId = negotiationId;
        return existing;
    }
    const peer = new RTCPeerConnection({
        iceServers: normalizeIceServers(session.iceConfiguration.iceServers),
        iceTransportPolicy: normalizeIceTransportPolicy(session.iceConfiguration.iceTransportPolicy)
    });
    const state = {
        remoteParticipantId, negotiationId, peer, remoteStream: new MediaStream(),
        pendingIce: [], localIceGenerated: 0, remoteIceReceived: 0,
        remoteAudioPlaySucceeded: false, connectionTimer: null, playback: null, visualStreams:new Map(),
        negotiationChain:Promise.resolve(), localCandidateTypes:{}, selectedPairReported:false,
        remoteAccountId: session.participantAccounts.get(remoteParticipantId) ?? null
    };
    session.peers.set(remoteParticipantId, state);

    peer.onicecandidate = event => {
        if (!event.candidate) {
            void diagnostic(session, "IceGatheringComplete", state);
            return;
        }
        const type = candidateType(event.candidate);
        if (type) state.localCandidateTypes[type] = (state.localCandidateTypes[type] ?? 0) + 1;
        if (!state.negotiationId) return;
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
        if (peer.connectionState === "connected" && !state.selectedPairReported) {
            state.selectedPairReported = true;
            void collectPeerStats(session, state, "SelectedCandidatePair");
        } else if (peer.connectionState === "failed") {
            void diagnostic(session, "MediaConnectionFailed", state);
            session.callback.invokeMethodAsync("OnMediaError", "Unable to establish the media connection.").catch(() => {});
        }
    };
    peer.oniceconnectionstatechange = () => void diagnostic(session, `IceConnectionState:${peer.iceConnectionState}`, state);
    peer.onicegatheringstatechange = () => void diagnostic(session, `IceGatheringState:${peer.iceGatheringState}`, state);
    peer.onsignalingstatechange = () => void diagnostic(session, `SignalingState:${peer.signalingState}`, state);
    peer.ontrack = async event => {
        const stream = event.streams?.[0];
        const visualStream = stream?.getVideoTracks().length > 0 || event.track.kind === "video";
        if (visualStream) {
            const value = stream ?? new MediaStream([event.track]);
            state.visualStreams.set(value.id, value);
            session.visualStreams.set(value.id, value);
            void diagnostic(session, event.track.kind === "video" ? "RemoteVideoTrackReceived" :
                "RemoteScreenAudioTrackReceived", state, { readyState:event.track.readyState, muted:event.track.muted });
            event.track.onended = () => {
                if (value.getTracks().every(track => track.readyState === "ended")) {
                    state.visualStreams.delete(value.id);
                    session.visualStreams.delete(value.id);
                }
                for (const [elementId, viewer] of session.viewers)
                    if (viewer.mediaStreamId === value.id) detachViewer(session, elementId);
            };
            return;
        }
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
    if (session.screenShare)
        for (const track of session.screenShare.stream.getTracks()) {
            const sender = peer.addTrack(track, session.screenShare.stream);
            let senders = session.screenShare.senders.get(remoteParticipantId);
            if (!senders) session.screenShare.senders.set(remoteParticipantId, senders = []);
            senders.push(sender);
        }
    void diagnostic(session, "PeerCreated", state);
    state.connectionTimer = setTimeout(() => {
        if (peer.connectionState !== "connected") {
            void diagnostic(session, "MediaConnectionTimedOut", state);
            session.callback.invokeMethodAsync("OnMediaError", "Unable to establish the media connection.").catch(() => {});
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
    state.negotiationId = negotiationId;
    state.negotiationChain = state.negotiationChain.then(async () => {
        if (state.peer.signalingState !== "stable") return;
        const offer = await state.peer.createOffer();
        await diagnostic(session, "OfferCreated", state);
        await state.peer.setLocalDescription(offer);
        await session.callback.invokeMethodAsync("OnOfferCreated", remoteParticipantId, negotiationId,
            { type: state.peer.localDescription.type, sdp: state.peer.localDescription.sdp });
    }).catch(error => reportError(session, "Renegotiation failed", error));
    await state.negotiationChain;
}

function reportError(session, prefix, error) {
    console.error("[Iridium Community Voice]", { event:prefix, name:error?.name ?? "MediaError" });
    void diagnostic(session, "SignalingFailure", null,
        { name:error?.name ?? "MediaError", message:error?.message ?? String(error) });
    session.callback.invokeMethodAsync("OnMediaError", "Community voice signaling failed.").catch(() => {});
}

async function collectPeerStats(session, state, event = "StatsSnapshot") {
        let packetsSent = 0, packetsReceived = 0, bytesSent = 0, bytesReceived = 0;
        let framesEncoded = 0, framesDecoded = 0, framesDropped = 0, frameWidth = null, frameHeight = null;
        const reports = await state.peer.getStats();
        const byId = new Map();
        let selectedPair = null;
        reports.forEach(report => {
            byId.set(report.id, report);
            if (report.type === "outbound-rtp") {
                packetsSent += report.packetsSent ?? 0;
                bytesSent += report.bytesSent ?? 0;
                if (report.kind === "video") { framesEncoded += report.framesEncoded ?? 0;
                    frameWidth = report.frameWidth ?? frameWidth; frameHeight = report.frameHeight ?? frameHeight; }
            } else if (report.type === "inbound-rtp") {
                packetsReceived += report.packetsReceived ?? 0;
                bytesReceived += report.bytesReceived ?? 0;
                if (report.kind === "video") { framesDecoded += report.framesDecoded ?? 0;
                    framesDropped += report.framesDropped ?? 0; frameWidth = report.frameWidth ?? frameWidth;
                frameHeight = report.frameHeight ?? frameHeight; }
            }
            if (report.type === "transport" && report.selectedCandidatePairId)
                selectedPair = report.selectedCandidatePairId;
            if (report.type === "candidate-pair" && report.selected === true) selectedPair = report.id;
        });
        const pair = selectedPair ? byId.get(selectedPair) : [...byId.values()].find(value =>
            value.type === "candidate-pair" && value.state === "succeeded" && value.nominated);
        const local = pair ? byId.get(pair.localCandidateId) : null;
        const remote = pair ? byId.get(pair.remoteCandidateId) : null;
        await diagnostic(session, event, state,
            { packetsSent, packetsReceived, bytesSent, bytesReceived, framesEncoded, framesDecoded,
                framesDropped, frameWidth, frameHeight, selectedLocalCandidateType:local?.candidateType ?? null,
                selectedRemoteCandidateType:remote?.candidateType ?? null,
                selectedCandidateProtocol:local?.protocol ?? remote?.protocol ?? null });
        return { remoteParticipantId: state.remoteParticipantId, packetsSent, packetsReceived,
            bytesSent, bytesReceived, remoteTrackCount: state.remoteStream.getAudioTracks().length,
            remoteAudioPlaySucceeded: state.remoteAudioPlaySucceeded };
}

async function collectStats(session) {
    const snapshots = [];
    for (const state of session.peers.values()) {
        snapshots.push(await collectPeerStats(session, state));
    }
    return snapshots;
}

export async function getStatsSnapshot(id) {
    return collectStats(requireSession(id));
}

export async function connect(mediaBuildId, callback, mediaSession, room, localAccountId, preferences = [], iceConfiguration = null) {
    const build = await import(`./mediaBuild.js?build=${encodeURIComponent(mediaBuildId)}`);
    await build.requireMatchingMediaBuild(mediaBuildId);
    ({ createRemoteVoicePlayback, updateRemoteVoicePlayback, destroyRemoteVoicePlayback } =
        await build.loadVoicePlayback(mediaBuildId));
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
        id, callback, mediaSession, iceConfiguration: iceConfiguration ?? { iceServers:[], iceTransportPolicy:"all" },
        room, localAccountId, localParticipantId: mediaSession.participantId,
        localStream, peers: new Map(), muted: localParticipant?.muted === true,
        deafened: localParticipant?.deafened === true, speaking: false,
        vadFrame: null, context: null, source: null, analyser: null,
        diagnostics: mediaSession.diagnosticsEnabled === true,
        participantAccounts:new Map((room.participants ?? []).map(value => [value.participantId, value.accountId])),
        preferences:new Map((preferences ?? []).map(value => [value.remoteAccountId, value])),
        visualStreams:new Map(), viewers:new Map(), screenShare:null
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
        if (state.peer.signalingState === "have-local-offer") {
            if (session.localParticipantId.localeCompare(event.sourceParticipantId) < 0) {
                await diagnostic(session, "GlareOfferIgnored", state);
                return;
            }
            await state.peer.setLocalDescription({ type:"rollback" });
        }
        state.negotiationId = event.negotiationId;
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

function detachViewer(session, elementId) {
    const viewer = session.viewers.get(elementId);
    const element = document.getElementById(elementId);
    if (element) {
        element.pause();
        element.srcObject = null;
    }
    session.viewers.delete(elementId);
    if (viewer) void diagnostic(session, "StreamViewerDetached", null, { mediaStreamId:viewer.mediaStreamId });
}

async function waitForVisualStream(session, mediaStreamId) {
    if (session.screenShare?.stream?.id === mediaStreamId) return session.screenShare.stream;
    for (let attempt = 0; attempt < 100; attempt++) {
        const stream = session.visualStreams.get(mediaStreamId);
        if (stream) return stream;
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new DOMException("The remote screen track did not arrive.", "TimeoutError");
}

export async function captureStreamThumbnail(id, mediaStreamId) {
    const session = requireSession(id);
    return captureThumbnail(await waitForVisualStream(session, mediaStreamId));
}

async function captureThumbnail(stream) {
    const video = document.createElement("video");
    video.muted = true; video.playsInline = true; video.srcObject = stream;
    try {
        await video.play();
        if (!video.videoWidth) await new Promise((resolve, reject) => {
            const timer = setTimeout(() => reject(new Error("Video frame was not ready.")), 3000);
            video.onloadeddata = () => { clearTimeout(timer); resolve(); };
            video.onerror = () => { clearTimeout(timer); reject(new Error("Video preview failed.")); };
        });
        const width = Math.min(280, video.videoWidth || 280);
        const height = Math.max(1, Math.round(width * (video.videoHeight || 158) / (video.videoWidth || 280)));
        const canvas = document.createElement("canvas");
        canvas.width = width; canvas.height = height;
        canvas.getContext("2d", { alpha:false }).drawImage(video, 0, 0, width, height);
        return canvas.toDataURL("image/webp", .72);
    } finally { video.pause(); video.srcObject = null; }
}

export async function startScreenShare(id) {
    const session = requireSession(id);
    if (!navigator.mediaDevices?.getDisplayMedia)
        throw new DOMException("This browser does not support screen capture.", "NotSupportedError");
    if (session.screenShare) await stopScreenShare(id, "Replaced");
    await diagnostic(session, "DisplayCaptureStarted");
    let stream;
    try {
        stream = await navigator.mediaDevices.getDisplayMedia({
            video:{ frameRate:{ ideal:30, max:30 } }, audio:true
        });
    } catch (error) {
        await diagnostic(session, "DisplayCaptureFailed", null, { name:error?.name, message:error?.message });
        throw error;
    }
    const videoTrack = stream.getVideoTracks()[0];
    if (!videoTrack) {
        for (const track of stream.getTracks()) track.stop();
        throw new DOMException("Screen capture did not provide a video track.", "NotFoundError");
    }
    const streamId = crypto.randomUUID();
    const share = { streamId, mediaStreamId:stream.id, stream, senders:new Map(), stopping:false };
    session.screenShare = share;
    for (const [participantId, state] of session.peers) {
        const senders = [];
        for (const track of stream.getTracks()) {
            senders.push(state.peer.addTrack(track, stream));
            await diagnostic(session, track.kind === "video" ? "VideoTrackAdded" : "ScreenAudioTrackAdded", state,
                { readyState:track.readyState });
        }
        share.senders.set(participantId, senders);
        await startOffer(session, participantId);
    }
    videoTrack.addEventListener("ended", () => {
        if (!session.screenShare || session.screenShare.streamId !== streamId || session.screenShare.stopping) return;
        session.callback.invokeMethodAsync("OnScreenShareEnded", "BrowserStopSharing").catch(() => {});
    }, { once:true });
    await diagnostic(session, "DisplayCaptureSucceeded", null,
        { readyState:videoTrack.readyState, audioTrackCount:stream.getAudioTracks().length });
    return { streamId, kind:0, hasAudio:stream.getAudioTracks().length > 0, mediaStreamId:stream.id };
}

export async function stopScreenShare(id, reason = "UserStoppedInIridium") {
    const session = requireSession(id);
    const share = session.screenShare;
    if (!share) return;
    session.screenShare = null;
    share.stopping = true;
    for (const [participantId, senders] of share.senders) {
        const state = session.peers.get(participantId);
        if (!state) continue;
        for (const sender of senders) {
            try { state.peer.removeTrack(sender); } catch { }
        }
        await startOffer(session, participantId);
    }
    for (const track of share.stream.getTracks()) track.stop();
    await diagnostic(session, "ScreenShareEnded", null, { reason });
}

export async function attachStreamViewer(id, options) {
    const session = requireSession(id);
    const stream = await waitForVisualStream(session, options.mediaStreamId);
    const element = document.getElementById(options.elementId);
    if (!element) throw new DOMException("The stream viewer is unavailable.", "NotFoundError");
    detachViewer(session, options.elementId);
    element.srcObject = stream;
    element.muted = !!options.audioMuted || stream.getAudioTracks().length === 0;
    element.playsInline = true;
    session.viewers.set(options.elementId, { mediaStreamId:options.mediaStreamId });
    await diagnostic(session, "VideoPlaybackStarted");
    await element.play();
    await diagnostic(session, "VideoPlaybackSucceeded");
}

export function detachStreamViewer(id, elementId) { detachViewer(requireSession(id), elementId); }

export function setStreamAudioMuted(id, options) {
    requireSession(id);
    const element = document.getElementById(options.elementId);
    if (element) element.muted = !!options.muted;
}

export async function requestStreamFullscreen(id, elementId) {
    requireSession(id);
    const element = document.getElementById(elementId);
    if (!element?.requestFullscreen) throw new DOMException("Fullscreen is unavailable.", "NotSupportedError");
    await element.requestFullscreen();
}

export async function disconnect(id, reason = "unspecified") {
    const session = sessions.get(id);
    if (!session) return;
    sessions.delete(id);
    for (const elementId of [...session.viewers.keys()]) detachViewer(session, elementId);
    if (session.screenShare) {
        session.screenShare.stopping = true;
        for (const track of session.screenShare.stream.getTracks()) track.stop();
        session.screenShare = null;
    }
    if (session.vadFrame !== null) cancelAnimationFrame(session.vadFrame);
    publishSpeaking(session, false);
    for (const participantId of [...session.peers.keys()]) closePeer(session, participantId, reason);
    for (const track of session.localStream.getTracks()) track.stop();
    session.source?.disconnect();
    session.analyser?.disconnect();
    if (session.context) await session.context.close().catch(() => {});
    await diagnostic(session, "Disconnected");
}
