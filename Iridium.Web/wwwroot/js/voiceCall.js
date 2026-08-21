const sessions = new Map();

function requireSession(id) {
    const session = sessions.get(id);
    if (!session) throw new Error("The voice media session is no longer active.");
    return session;
}

function normalizeIceServers(servers) {
    return (servers ?? []).map(server => ({
        urls: server.urls,
        ...(server.username ? { username: server.username } : {}),
        ...(server.credential ? { credential: server.credential } : {})
    }));
}

function diagnostic(session, event, details = {}) {
    if (!session.diagnostics) return;
    console.debug("[Iridium Voice]", {
        callId: session.callId,
        localAccountId: session.localAccountId,
        role: session.role,
        peerGeneration: session.peerGeneration,
        event,
        connectionState: session.peer?.connectionState,
        iceConnectionState: session.peer?.iceConnectionState,
        iceGatheringState: session.peer?.iceGatheringState,
        signalingState: session.peer?.signalingState,
        ...details
    });
}

function stateTransition(session, stateName, nextState) {
    const previousState = session.states[stateName];
    session.states[stateName] = nextState;
    diagnostic(session, `${stateName}: ${previousState} -> ${nextState}`);
}

function incrementType(counts, type, protocol) {
    const key = `${type ?? "unknown"}/${protocol ?? "unknown"}`;
    counts[key] = (counts[key] ?? 0) + 1;
}

function summarizeTypes(counts) {
    const entries = Object.entries(counts);
    return entries.length === 0 ? "none" : entries.map(([key, count]) => `${count} ${key}`).join(", ");
}

function descriptionMetadata(description) {
    const type = description?.type ?? null;
    const sdp = description?.sdp ?? null;
    return {
        receivedKeys: description && typeof description === "object" ? Object.keys(description).sort().join(",") : "none",
        type,
        sdpLength: typeof sdp === "string" ? sdp.length : 0,
        hasAudioMediaSection: typeof sdp === "string" && /(^|\r?\n)m=audio\s/im.test(sdp),
        hasUnexpectedPascalCase: description?.Type !== undefined || description?.Sdp !== undefined
    };
}

function browserDescription(session, description, expectedType) {
    const metadata = descriptionMetadata(description);
    diagnostic(session, `${expectedType} description received by JS`, metadata);
    if (metadata.type !== expectedType || metadata.sdpLength === 0)
        throw new TypeError(`Invalid ${expectedType} RTCSessionDescriptionInit shape.`);
    return { type: metadata.type, sdp: description.sdp };
}

function browserCandidate(session, candidate) {
    const keys = candidate && typeof candidate === "object" ? Object.keys(candidate).sort() : [];
    const init = {
        candidate: candidate?.candidate,
        sdpMid: candidate?.sdpMid ?? null,
        sdpMLineIndex: candidate?.sdpMLineIndex ?? null,
        usernameFragment: candidate?.usernameFragment ?? null
    };
    diagnostic(session, "ICE candidate shape received by JS", {
        receivedKeys: keys.join(","),
        hasCandidate: typeof init.candidate === "string" && init.candidate.length > 0,
        hasSdpMid: init.sdpMid !== null,
        hasSdpMLineIndex: init.sdpMLineIndex !== null,
        hasUsernameFragment: init.usernameFragment !== null
    });
    if (typeof init.candidate !== "string" || init.candidate.length === 0)
        throw new TypeError("Invalid RTCIceCandidateInit shape: candidate is missing.");
    return new RTCIceCandidate(init);
}

async function selectedPairSummary(session) {
    try {
        const stats = await session.peer.getStats();
        let pair = null;
        for (const report of stats.values()) {
            if (report.type === "transport" && report.selectedCandidatePairId)
                pair = stats.get(report.selectedCandidatePairId);
            if (!pair && report.type === "candidate-pair" && report.state === "succeeded" && report.nominated)
                pair = report;
        }
        if (!pair) return null;
        const local = stats.get(pair.localCandidateId);
        const remote = stats.get(pair.remoteCandidateId);
        const summary = {
            selectedLocalCandidateType: local?.candidateType ?? null,
            selectedRemoteCandidateType: remote?.candidateType ?? null,
            selectedCandidateProtocol: local?.protocol ?? remote?.protocol ?? null
        };
        session.selectedPair = summary;
        diagnostic(session, "selected ICE candidate pair", summary);
        return summary;
    } catch (error) {
        diagnostic(session, "selected ICE candidate pair unavailable", { name: error?.name, message: error?.message });
        return null;
    }
}

function diagnosticSummary(session, event) {
    diagnostic(session, event, {
        localCandidatesGenerated: session.localCandidateCount,
        localCandidateTypes: summarizeTypes(session.localCandidateTypes),
        remoteCandidatesReceived: session.remoteCandidateCount,
        remoteCandidateTypes: summarizeTypes(session.remoteCandidateTypes),
        remoteCandidatesAdded: session.remoteCandidateAddedCount,
        remoteCandidateAddFailures: session.remoteCandidateAddFailureCount,
        queuedRemoteCandidates: session.pendingRemoteCandidates.length
    });
}

async function notify(session, method, argument) {
    try {
        await session.callback.invokeMethodAsync(method, argument);
    } catch (error) {
        if (session.diagnostics) console.error("[Iridium Voice] .NET callback failed", { method, error });
    }
}

function reportOperationError(session, operation, error) {
    const message = `${error?.name ?? "WebRtcError"}: ${operation}: ${error?.message ?? "WebRTC operation failed."}`;
    if (session?.diagnostics) console.error("[Iridium Voice]", { event: "operation failed", operation, name: error?.name, message: error?.message });
    if (session) notify(session, "OnMediaError", message);
}

async function flushRemoteCandidates(session) {
    if (!session.peer.remoteDescription) return;
    const queued = session.pendingRemoteCandidates.splice(0);
    if (queued.length > 0) diagnostic(session, "flushing queued remote ICE candidates", { queuedCount: queued.length });
    for (const candidate of queued) {
        try {
            await session.peer.addIceCandidate(candidate);
            session.remoteCandidateAddedCount++;
            diagnostic(session, "addIceCandidate succeeded", { source: "queue" });
        } catch (error) {
            session.remoteCandidateAddFailureCount++;
            diagnostic(session, "addIceCandidate failed", { source: "queue", name: error?.name, message: error?.message });
            throw error;
        }
    }
}

function setSpeaking(session, value) {
    if (session.speaking === value) return;
    session.speaking = value;
    notify(session, "OnSpeakingChanged", value);
}

async function startVoiceActivityDetection(session) {
    const AudioContextType = window.AudioContext ?? window.webkitAudioContext;
    if (!AudioContextType) {
        diagnostic(session, "voice activity unavailable", { reason: "AudioContext unsupported" });
        return;
    }
    const context = new AudioContextType();
    const source = context.createMediaStreamSource(session.localStream);
    const analyser = context.createAnalyser();
    analyser.fftSize = 512;
    analyser.smoothingTimeConstant = 0.72;
    source.connect(analyser);
    if (context.state === "suspended") await context.resume().catch(() => {});
    const samples = new Float32Array(analyser.fftSize);
    session.audioContext = context;
    session.audioSource = source;
    session.analyser = analyser;
    session.noiseFloor = 0.008;
    session.aboveThresholdFrames = 0;
    session.lastVoiceAt = 0;

    const sample = timestamp => {
        if (!sessions.has(session.id)) return;
        analyser.getFloatTimeDomainData(samples);
        let sum = 0;
        for (const value of samples) sum += value * value;
        const rms = Math.sqrt(sum / samples.length);
        if (!session.speaking && rms < 0.035) session.noiseFloor = session.noiseFloor * 0.97 + rms * 0.03;
        const startThreshold = Math.max(0.032, session.noiseFloor * 3.2);
        const stopThreshold = Math.max(0.019, session.noiseFloor * 2.0);
        if (rms >= startThreshold) {
            session.aboveThresholdFrames++;
            session.lastVoiceAt = timestamp;
            if (session.aboveThresholdFrames >= 2) setSpeaking(session, true);
        } else {
            session.aboveThresholdFrames = 0;
            if (session.speaking && rms < stopThreshold && timestamp - session.lastVoiceAt >= 420) setSpeaking(session, false);
        }
        session.vadFrame = requestAnimationFrame(sample);
    };
    session.vadFrame = requestAnimationFrame(sample);
    diagnostic(session, "voice activity detector started");
}

export async function initialize(callback, iceServers, diagnostics = false, callId = null, localAccountId = null,
    role = "unknown", peerGeneration = 0, negotiationId = null) {
    if (!navigator.mediaDevices?.getUserMedia) throw new Error("NotSupportedError: This browser does not support microphone capture.");
    let localStream;
    try {
        if (diagnostics) console.debug("[Iridium Voice]", { callId, localAccountId, role, peerGeneration, event: "getUserMedia started" });
        localStream = await navigator.mediaDevices.getUserMedia({ audio: true });
        if (diagnostics) console.debug("[Iridium Voice]", { callId, localAccountId, role, peerGeneration,
            event: "getUserMedia succeeded", audioTrackCount: localStream.getAudioTracks().length });
    } catch (error) {
        if (diagnostics) console.error("[Iridium Voice]", { callId, localAccountId, role, peerGeneration,
            event: "getUserMedia failed", name: error?.name, message: error?.message });
        throw new Error(`${error?.name ?? "MediaError"}: ${error?.message ?? "Microphone access failed."}`);
    }

    const id = crypto.randomUUID();
    const remoteAudio = document.createElement("audio");
    let session;
    try {
        remoteAudio.autoplay = true;
        remoteAudio.playsInline = true;
        remoteAudio.hidden = true;
        remoteAudio.dataset.iridiumVoiceCall = id;
        document.body.appendChild(remoteAudio);

        const peer = new RTCPeerConnection({ iceServers: normalizeIceServers(iceServers) });
        session = {
            id, callback, peer, localStream, remoteStream: new MediaStream(), remoteAudio, diagnostics,
            callId, localAccountId, role, peerGeneration, negotiationId, appliedAnswerNegotiationId: null,
            answersReceived: 0, answersApplied: 0, answersIgnored: 0,
            lastAnswerSignalingStateBefore: null, lastAnswerSignalingStateAfter: null,
            localCandidateCount: 0, remoteCandidateCount: 0,
            remoteCandidateAddedCount: 0, remoteCandidateAddFailureCount: 0,
            localCandidateTypes: {}, remoteCandidateTypes: {}, selectedPair: null,
            pendingRemoteCandidates: [], speaking: false, vadFrame: null, audioContext: null,
            audioSource: null, analyser: null,
            states: {
                signalingState: peer.signalingState,
                iceGatheringState: peer.iceGatheringState,
                iceConnectionState: peer.iceConnectionState,
                connectionState: peer.connectionState
            }
        };
        sessions.set(id, session);
        diagnostic(session, "PeerConnection created", {
            configuredIceServerCount: iceServers?.length ?? 0,
            iceTransportPolicy: peer.getConfiguration().iceTransportPolicy ?? "all"
        });
        for (const track of localStream.getAudioTracks()) {
            peer.addTrack(track, localStream);
            diagnostic(session, "microphone track added", { kind: track.kind, enabled: track.enabled, readyState: track.readyState });
        }

        peer.onicecandidate = event => {
            if (!event.candidate) {
                diagnostic(session, "ICE candidate gathering complete", { localCandidateCount: session.localCandidateCount });
                return;
            }
            session.localCandidateCount++;
            incrementType(session.localCandidateTypes, event.candidate.type, event.candidate.protocol);
            diagnostic(session, "ICE candidate generated", {
                candidateType: event.candidate.type ?? "unknown",
                protocol: event.candidate.protocol ?? "unknown",
                localCandidateCount: session.localCandidateCount
            });
            const candidate = event.candidate.toJSON();
            notify(session, "OnIceCandidate", {
                candidate: candidate.candidate,
                sdpMid: candidate.sdpMid ?? null,
                sdpMLineIndex: candidate.sdpMLineIndex ?? null,
                usernameFragment: candidate.usernameFragment ?? null
            });
        };
        peer.onconnectionstatechange = () => {
            stateTransition(session, "connectionState", peer.connectionState);
            if (peer.connectionState === "connected") {
                diagnostic(session, "transport connected; notifying .NET to cancel negotiation timeout");
                selectedPairSummary(session);
            } else if (peer.connectionState === "failed") diagnosticSummary(session, "WEBRTC FAILED");
            notify(session, "OnConnectionStateChanged", peer.connectionState);
        };
        peer.oniceconnectionstatechange = () => stateTransition(session, "iceConnectionState", peer.iceConnectionState);
        peer.onicegatheringstatechange = () => stateTransition(session, "iceGatheringState", peer.iceGatheringState);
        peer.onsignalingstatechange = () => stateTransition(session, "signalingState", peer.signalingState);
        peer.onicecandidateerror = event => diagnostic(session, "ICE candidate error (non-terminal)", {
            errorCode: event.errorCode, errorText: event.errorText, urlScheme: event.url?.split(":", 1)[0] ?? null
        });
        peer.ontrack = event => {
            diagnostic(session, "ontrack fired; remote audio track received", {
                kind: event.track.kind, enabled: event.track.enabled, readyState: event.track.readyState,
                streamCount: event.streams?.length ?? 0
            });
            const stream = event.streams?.[0];
            if (stream) session.remoteStream = stream;
            else session.remoteStream.addTrack(event.track);
            remoteAudio.srcObject = session.remoteStream;
            diagnostic(session, "remote stream attached to audio element", { remoteAudioTrackCount: session.remoteStream.getAudioTracks().length });
            remoteAudio.play()
                .then(() => diagnostic(session, "remote audio play succeeded"))
                .catch(error => diagnostic(session, "remote audio play failed (transport unaffected)", { name: error?.name, message: error?.message }));
        };
        diagnostic(session, "For additional Chromium diagnostics, inspect chrome://webrtc-internals while the call is active.");
        await startVoiceActivityDetection(session);
        return id;
    } catch (error) {
        sessions.delete(id);
        for (const track of localStream.getTracks()) track.stop();
        remoteAudio.remove();
        if (session?.audioContext) await session.audioContext.close().catch(() => {});
        throw error;
    }
}

export async function createOffer(id, negotiationId) {
    const session = requireSession(id);
    try {
        if (session.negotiationId && session.negotiationId !== negotiationId)
            throw new DOMException("The peer already belongs to a different WebRTC negotiation.", "InvalidStateError");
        session.negotiationId = negotiationId;
        diagnostic(session, "createOffer started");
        const offer = await session.peer.createOffer();
        diagnostic(session, "createOffer completed", descriptionMetadata(offer));
        await session.peer.setLocalDescription(offer);
        diagnostic(session, "setLocalDescription(offer) completed", descriptionMetadata(session.peer.localDescription));
        return { type: session.peer.localDescription.type, sdp: session.peer.localDescription.sdp };
    } catch (error) {
        reportOperationError(session, "create/set local offer", error);
        throw error;
    }
}

export async function acceptOffer(id, negotiationId, offer) {
    const session = requireSession(id);
    try {
        if (session.negotiationId && session.negotiationId !== negotiationId)
            throw new DOMException("The offer belongs to a different WebRTC negotiation.", "InvalidStateError");
        session.negotiationId = negotiationId;
        await session.peer.setRemoteDescription(browserDescription(session, offer, "offer"));
        diagnostic(session, "setRemoteDescription(offer) completed");
        await flushRemoteCandidates(session);
        const answer = await session.peer.createAnswer();
        diagnostic(session, "createAnswer completed", descriptionMetadata(answer));
        await session.peer.setLocalDescription(answer);
        diagnostic(session, "setLocalDescription(answer) completed", descriptionMetadata(session.peer.localDescription));
        return { type: session.peer.localDescription.type, sdp: session.peer.localDescription.sdp };
    } catch (error) {
        reportOperationError(session, "accept offer/create answer", error);
        throw error;
    }
}

export async function applyAnswer(id, negotiationId, answer) {
    const session = requireSession(id);
    try {
        const stateBefore = session.peer.signalingState;
        session.answersReceived++;
        session.lastAnswerSignalingStateBefore = stateBefore;
        const alreadyProcessed = session.appliedAnswerNegotiationId === negotiationId;
        diagnostic(session, "answer application requested", { negotiationId, stateBefore, alreadyProcessed });
        if (session.negotiationId !== negotiationId) {
            session.answersIgnored++;
            return { applied: false, signalingState: stateBefore, ignoreReason: "stale-negotiation" };
        }
        if (alreadyProcessed) {
            session.answersIgnored++;
            return { applied: false, signalingState: stateBefore, ignoreReason: "duplicate-answer" };
        }
        if (stateBefore !== "have-local-offer") {
            if (stateBefore === "stable") {
                session.answersIgnored++;
                return { applied: false, signalingState: stateBefore, ignoreReason: "answer-already-applied" };
            }
            throw new DOMException(`Cannot apply a remote answer while signalingState is ${stateBefore}.`, "InvalidStateError");
        }
        await session.peer.setRemoteDescription(browserDescription(session, answer, "answer"));
        session.appliedAnswerNegotiationId = negotiationId;
        session.answersApplied++;
        session.lastAnswerSignalingStateAfter = session.peer.signalingState;
        diagnostic(session, "setRemoteDescription(answer) completed", { negotiationId, stateAfter: session.peer.signalingState });
        await flushRemoteCandidates(session);
        return { applied: true, signalingState: session.peer.signalingState, ignoreReason: null };
    } catch (error) {
        reportOperationError(session, "set remote answer", error);
        throw error;
    }
}

export async function addIceCandidate(id, candidate) {
    const session = requireSession(id);
    try {
        const browserValue = browserCandidate(session, candidate);
        session.remoteCandidateCount++;
        incrementType(session.remoteCandidateTypes, browserValue.type, browserValue.protocol);
        diagnostic(session, "remote ICE candidate received", {
            candidateType: browserValue.type ?? "unknown", protocol: browserValue.protocol ?? "unknown",
            remoteCandidateCount: session.remoteCandidateCount
        });
        if (!session.peer.remoteDescription) {
            session.pendingRemoteCandidates.push(browserValue);
            diagnostic(session, "remote ICE candidate queued before remote description", { queuedCount: session.pendingRemoteCandidates.length });
            return;
        }
        await session.peer.addIceCandidate(browserValue);
        session.remoteCandidateAddedCount++;
        diagnostic(session, "addIceCandidate succeeded", { source: "direct" });
    } catch (error) {
        session.remoteCandidateAddFailureCount++;
        diagnostic(session, "addIceCandidate failed", { source: "direct", name: error?.name, message: error?.message });
        reportOperationError(session, "add ICE candidate", error);
        throw error;
    }
}

function snapshot(session) {
    return {
        callId: session.callId,
        localAccountId: session.localAccountId,
        signalingState: session.peer.signalingState,
        iceGatheringState: session.peer.iceGatheringState,
        iceConnectionState: session.peer.iceConnectionState,
        connectionState: session.peer.connectionState,
        localCandidateCount: session.localCandidateCount,
        remoteCandidateCount: session.remoteCandidateCount,
        remoteCandidateAddedCount: session.remoteCandidateAddedCount,
        remoteCandidateAddFailureCount: session.remoteCandidateAddFailureCount,
        queuedRemoteCandidateCount: session.pendingRemoteCandidates.length,
        localCandidateTypes: summarizeTypes(session.localCandidateTypes),
        remoteCandidateTypes: summarizeTypes(session.remoteCandidateTypes),
        selectedLocalCandidateType: session.selectedPair?.selectedLocalCandidateType ?? null,
        selectedRemoteCandidateType: session.selectedPair?.selectedRemoteCandidateType ?? null,
        selectedCandidateProtocol: session.selectedPair?.selectedCandidateProtocol ?? null,
        answersReceived: session.answersReceived,
        answersApplied: session.answersApplied,
        answersIgnored: session.answersIgnored,
        lastAnswerSignalingStateBefore: session.lastAnswerSignalingStateBefore,
        lastAnswerSignalingStateAfter: session.lastAnswerSignalingStateAfter,
        peerGeneration: session.peerGeneration,
        role: session.role
    };
}

export function getDiagnosticSnapshot(id) {
    return snapshot(requireSession(id));
}

export function getActiveDiagnosticSnapshots() {
    return Array.from(sessions.values()).filter(session => session.diagnostics).map(snapshot);
}

export function setMuted(id, muted) {
    const session = requireSession(id);
    for (const track of session.localStream.getAudioTracks()) track.enabled = !muted;
    if (muted) setSpeaking(session, false);
}

export function setDeafened(id, deafened) {
    requireSession(id).remoteAudio.muted = deafened;
}

export async function cleanup(id) {
    const session = sessions.get(id);
    if (!session) return;
    sessions.delete(id);
    if (session.vadFrame !== null) cancelAnimationFrame(session.vadFrame);
    setSpeaking(session, false);
    session.peer.onicecandidate = null;
    session.peer.onicecandidateerror = null;
    session.peer.onconnectionstatechange = null;
    session.peer.oniceconnectionstatechange = null;
    session.peer.onicegatheringstatechange = null;
    session.peer.onsignalingstatechange = null;
    session.peer.ontrack = null;
    session.peer.close();
    for (const track of session.localStream.getTracks()) track.stop();
    for (const track of session.remoteStream.getTracks()) track.stop();
    session.audioSource?.disconnect();
    session.analyser?.disconnect();
    if (session.audioContext) await session.audioContext.close().catch(() => {});
    session.remoteAudio.pause();
    session.remoteAudio.srcObject = null;
    session.remoteAudio.remove();
    diagnostic(session, "PeerConnection and media cleaned up");
}
