let createRemoteVoicePlayback, updateRemoteVoicePlayback, destroyRemoteVoicePlayback;
const sessions = new Map();

// TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.

const diagnosticEventNames = {
    "PeerConnection created": "PeerCreated",
    "microphone track added": "LocalTrackAdded",
    "createOffer started": "CreateOfferStarted",
    "createOffer completed": "CreateOfferSucceeded",
    "createAnswer started": "CreateAnswerStarted",
    "createAnswer completed": "CreateAnswerSucceeded",
    "answer application requested": "SetRemoteAnswerStarted",
    "stale answer ignored": "StaleSignalIgnored",
    "duplicate answer ignored": "DuplicateOrStaleAnswerIgnored",
    "stale/duplicate answer ignored before setRemoteDescription": "DuplicateOrStaleAnswerIgnored",
    "setRemoteDescription(answer) completed": "SetRemoteAnswerSucceeded",
    "ICE candidate gathering complete": "IceGatheringFinished",
    "ontrack fired; remote audio track received": "RemoteTrackReceived",
    "remote stream attached to audio element": "RemoteAudioAttached",
    "remote audio play succeeded": "RemoteAudioPlaySucceeded",
    "remote audio play failed (transport unaffected)": "RemoteAudioPlayFailed",
    "RTCPeerConnection.close()": "PeerClosing",
    "PeerConnection and media cleaned up": "PeerClosed",
    "local media track stop()": "LocalTracksStopping",
    "ICE candidate error (non-terminal)": "IceCandidateError",
    "negotiationneeded observed; accepted caller path remains sole offer authority": "NegotiationNeeded",
    "transport connected; notifying .NET to cancel negotiation timeout": "PeerTransportConnected",
    "WebRTC connected": "WebRtcConnected",
    "WebRTC connection failed": "WebRtcConnectionFailed"
};

function canonicalEvent(value) {
    if (diagnosticEventNames[value]) return diagnosticEventNames[value];
    if (value.startsWith("JS GENERATED ICE")) return "IceGenerated";
    if (value.startsWith("JS INVOKING DOTNET ICE CALLBACK")) return "IceInteropSending";
    if (value.startsWith("DOTNET ICE CALLBACK COMPLETED")) return "IceInteropSucceeded";
    if (value.startsWith("JS RECEIVED REMOTE ICE")) return "IceReceivedByBrowserInterop";
    if (value.startsWith("JS QUEUED REMOTE ICE")) return "IceQueuedInBrowser";
    if (value.startsWith("JS ADDING REMOTE ICE")) return "BrowserIceAddStarted";
    if (value.startsWith("JS addIceCandidate SUCCESS")) return "BrowserIceAddSucceeded";
    if (value.startsWith("JS addIceCandidate FAILED")) return "BrowserIceAddFailed";
    if (value.startsWith("flushing queued remote ICE")) return "IceQueueFlushStarted";
    if (value.startsWith("BEFORE setLocalDescription(offer)")) return "SetLocalOfferStarted";
    if (value.startsWith("AFTER setLocalDescription(offer)")) return "SetLocalOfferSucceeded";
    if (value.startsWith("FAILED setLocalDescription(offer)")) return "SetLocalOfferFailed";
    if (value.startsWith("BEFORE setRemoteDescription(offer)")) return "SetRemoteOfferStarted";
    if (value.startsWith("AFTER setRemoteDescription(offer)")) return "SetRemoteOfferSucceeded";
    if (value.startsWith("FAILED setRemoteDescription(offer)")) return "SetRemoteOfferFailed";
    if (value.startsWith("BEFORE setLocalDescription(answer)")) return "SetLocalAnswerStarted";
    if (value.startsWith("AFTER setLocalDescription(answer)")) return "SetLocalAnswerSucceeded";
    if (value.startsWith("FAILED setLocalDescription(answer)")) return "SetLocalAnswerFailed";
    if (value.startsWith("BEFORE setRemoteDescription(answer)")) return "SetRemoteAnswerStarted";
    if (value.startsWith("AFTER setRemoteDescription(answer)")) return "SetRemoteAnswerSucceeded";
    if (value.startsWith("FAILED setRemoteDescription(answer)")) return "SetRemoteAnswerFailed";
    if (value.includes("getStats")) return "StatsSnapshot";
    return value.replace(/[^A-Za-z0-9]+(.)/g, (_, next) => next.toUpperCase()).replace(/[^A-Za-z0-9]/g, "").slice(0, 64) || "BrowserDiagnostic";
}

function canonicalReason(value) {
    if (!value) return null;
    const mapped = {
        "call finished": "CallEnded", "negotiation timeout": "NegotiationTimeout",
        "retry replacement": "RetryReplacement", "callee retry replacement": "RetryReplacement",
        "peer replacement during initialization": "PeerReplaced", "account switch": "AccountSwitch",
        "signaling failure": "SignalError", "initialization failure": "SignalError",
        "terminal peer failure": "TerminalPeerFailure"
    }[value];
    return mapped ?? value.replace(/[^A-Za-z0-9]+(.)/g, (_, next) => next.toUpperCase())
        .replace(/[^A-Za-z0-9]/g, "").slice(0, 64);
}

function voiceDiagnosticReport(session, event, details) {
    const peer = session.peer;
    const report = {
        callId: session.callId, event: canonicalEvent(event), peerGeneration: session.peerGeneration,
        negotiationGeneration: session.negotiationGeneration,
        signalingState: peer?.signalingState ?? null, iceGatheringState: peer?.iceGatheringState ?? null,
        iceConnectionState: peer?.iceConnectionState ?? null, connectionState: peer?.connectionState ?? null,
        localDescriptionType: peer?.localDescription?.type ?? null,
        remoteDescriptionType: peer?.remoteDescription?.type ?? null
    };
    const mappings = {
        signalId: "signalId", candidateSequence: "sequence", sequence: "sequence", oldState: "oldState", newState: "newState",
        candidateType: "candidateType", protocol: "protocol", sdpMid: "sdpMid", sdpMLineIndex: "sdpMLineIndex",
        kind: "trackKind", enabled: "trackEnabled", trackEnabled: "trackEnabled",
        readyState: "trackReadyState", trackReadyState: "trackReadyState", muted: "trackMuted",
        configuredIceServerCount: "iceServerCount", iceTransportPolicy: "iceTransportPolicy", count: "count",
        queuedCount: "queueLength", audioTrackCount: "audioTrackCount", remoteAudioTrackCount: "audioTrackCount",
        senderCount: "senderCount", sdpLength: "sdpLength", candidateLineCount: "candidateLineCount",
        candidatePresent: "candidatePresent", hasAudioMediaSection: "hasAudioMediaSection",
        name: "errorName", message: "safeMessage", reason: "reason", createOfferCount: "offersCreated",
        createAnswerCount: "answersCreated", answersReceived: "answersReceived", localCandidatesGenerated: "localIceGenerated",
        localCandidateCount: "localIceGenerated", localIceSent: "localIceSent", remoteCandidatesReceived: "remoteIceReceived",
        remoteCandidateCount: "remoteIceReceived", remoteIceQueued: "remoteIceQueued", remoteCandidatesAdded: "remoteIceAdded",
        remoteCandidateAddedCount: "remoteIceAdded", remoteCandidateAddFailures: "remoteIceAddFailures",
        localCandidateCountStats: "localCandidateStats", remoteCandidateCountStats: "remoteCandidateStats",
        candidatePairCount: "candidatePairStats", succeededCandidatePairCount: "succeededCandidatePairs",
        nominatedPairExists: "nominatedPairExists", selectedPairExists: "selectedPairExists", pairState: "pairState",
        localCandidateType: "localCandidateType", remoteCandidateType: "remoteCandidateType",
        packetsSent: "packetsSent", packetsReceived: "packetsReceived", packetsLost: "packetsLost",
        bytesSent: "bytesSent", bytesReceived: "bytesReceived", framesEncoded:"framesEncoded",
        framesDecoded:"framesDecoded", framesDropped:"framesDropped", frameWidth:"frameWidth", frameHeight:"frameHeight",
        remoteTrackReceived: "remoteTrackReceived",
        remoteAudioPlaySucceeded: "remoteAudioPlaySucceeded", mediaTrafficDetected: "mediaTrafficDetected",
        hostCandidateAvailable: "hostCandidateAvailable",
        serverReflexiveCandidateAvailable: "serverReflexiveCandidateAvailable",
        peerReflexiveCandidateAvailable: "peerReflexiveCandidateAvailable",
        relayCandidateAvailable: "relayCandidateAvailable", turnConfigured: "turnConfigured",
        turnCredentialsPresent: "turnCredentialsPresent",
        turnConfiguredButNoRelayCandidate: "turnConfiguredButNoRelayCandidate"
    };
    for (const [source, target] of Object.entries(mappings))
        if (details[source] !== undefined) report[target] = target === "reason" ? canonicalReason(details[source]) : details[source];
    return report;
}

function sendVoiceDiagnostic(session, event, details = {}) {
    if (!session.diagnostics) return Promise.resolve();
    return session.callback.invokeMethodAsync("OnVoiceDiagnostic", session.peerGeneration,
        voiceDiagnosticReport(session, event, details)).catch(error => {
        console.error("[Iridium Voice] diagnostic callback failed", { event: canonicalEvent(event), name: error?.name });
    });
}

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

function normalizeIceTransportPolicy(policy) {
    return policy === "relay" ? "relay" : "all";
}

function iceConfigurationMetadata(servers) {
    const entries = servers ?? [];
    const hasScheme = (server, schemes) => (Array.isArray(server?.urls) ? server.urls : [server?.urls])
        .some(url => typeof url === "string" && schemes.some(scheme => url.toLowerCase().startsWith(scheme)));
    const turnServers = entries.filter(server => hasScheme(server, ["turn:", "turns:"]));
    return {
        turnConfigured: turnServers.length > 0,
        turnCredentialsPresent: turnServers.some(server => !!server.username && !!server.credential)
    };
}

function markObservedCandidateType(session, type) {
    const normalized = typeof type === "string" ? type.toLowerCase() : "";
    if (["host", "srflx", "prflx", "relay"].includes(normalized))
        session.observedCandidateTypes[normalized] = true;
}

function candidateAvailability(session) {
    const observed = session.observedCandidateTypes;
    return {
        hostCandidateAvailable: observed.host,
        serverReflexiveCandidateAvailable: observed.srflx,
        peerReflexiveCandidateAvailable: observed.prflx,
        relayCandidateAvailable: observed.relay,
        turnConfigured: session.turnConfigured,
        turnCredentialsPresent: session.turnCredentialsPresent,
        turnConfiguredButNoRelayCandidate: session.turnConfigured && !observed.relay
    };
}

function diagnostic(session, event, details = {}) {
    if (!session.diagnostics) return Promise.resolve();
    console.debug("[Iridium Voice]", {
        callId: session.callId,
        localAccountId: session.localAccountId,
        role: session.role,
        peerGeneration: session.peerGeneration,
        negotiationGeneration: session.negotiationGeneration,
        event,
        connectionState: session.peer?.connectionState,
        iceConnectionState: session.peer?.iceConnectionState,
        iceGatheringState: session.peer?.iceGatheringState,
        signalingState: session.peer?.signalingState,
        localDescription: session.peer?.localDescription?.type ?? null,
        remoteDescription: session.peer?.remoteDescription?.type ?? null,
        ...details
    });
    return sendVoiceDiagnostic(session, event, details);
}

function stateTransition(session, stateName, nextState) {
    const previousState = session.states[stateName];
    if (previousState === nextState) return;
    session.states[stateName] = nextState;
    const event = stateName === "signalingState" ? "SignalingState" :
        stateName === "iceGatheringState" ? "IceGatheringState" :
        stateName === "iceConnectionState" ? "IceConnectionState" : "PeerConnectionState";
    diagnostic(session, event, { oldState: previousState, newState: nextState });
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

function localDescriptionCandidateMetadata(peer) {
    const sdp = peer.localDescription?.sdp ?? "";
    return {
        sdpLength: sdp.length,
        candidateLineCount: sdp.split(/\r?\n/).filter(line => line.startsWith("a=candidate:")).length
    };
}

function safeCandidateMetadata(candidate) {
    let candidateType = candidate?.type ?? null;
    let protocol = candidate?.protocol ?? null;
    try {
        const parts = typeof candidate?.candidate === "string"
            ? candidate.candidate.split(" ", 9) : [];
        protocol ??= parts.length > 2 ? parts[2].toLowerCase() : null;
        const typeIndex = parts.findIndex(value => value.toLowerCase() === "typ");
        candidateType ??= typeIndex >= 0 && typeIndex + 1 < parts.length ? parts[typeIndex + 1].toLowerCase() : null;
    } catch { }
    return { candidateType: candidateType ?? "unknown", protocol: protocol ?? "unknown" };
}

async function waitForIceGatheringComplete(session) {
    if (session.peer.iceGatheringState === "complete") return;
    await new Promise(resolve => {
        const changed = () => {
            if (session.peer.iceGatheringState !== "complete") return;
            session.peer.removeEventListener("icegatheringstatechange", changed);
            resolve();
        };
        session.peer.addEventListener("icegatheringstatechange", changed);
    });
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
    const rtcCandidate = new RTCIceCandidate(init);
    markObservedCandidateType(session, safeCandidateMetadata(rtcCandidate).candidateType);
    return rtcCandidate;
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
        markObservedCandidateType(session, local?.candidateType);
        markObservedCandidateType(session, remote?.candidateType);
        const summary = {
            selectedLocalCandidateType: local?.candidateType ?? null,
            selectedRemoteCandidateType: remote?.candidateType ?? null,
            selectedCandidateProtocol: local?.protocol ?? remote?.protocol ?? null
        };
        session.selectedPair = summary;
        diagnostic(session, "WebRTC connected", { ...summary, ...candidateAvailability(session) });
        return summary;
    } catch (error) {
        diagnostic(session, "selected ICE candidate pair unavailable", { name: error?.name, message: error?.message });
        return null;
    }
}

async function iceStatsSummary(session, event) {
    try {
        const stats = await session.peer.getStats();
        const localCandidates = new Map();
        const remoteCandidates = new Map();
        const pairs = [];
        let selectedCandidatePairId = null;
        let packetsSent = 0, packetsReceived = 0, packetsLost = 0, bytesSent = 0, bytesReceived = 0;
        let framesEncoded = 0, framesDecoded = 0, framesDropped = 0, frameWidth = null, frameHeight = null;
        for (const report of stats.values()) {
            if (report.type === "local-candidate") {
                localCandidates.set(report.id, report);
                markObservedCandidateType(session, report.candidateType);
            } else if (report.type === "remote-candidate") {
                remoteCandidates.set(report.id, report);
                markObservedCandidateType(session, report.candidateType);
            }
            else if (report.type === "candidate-pair") pairs.push(report);
            else if (report.type === "transport" && report.selectedCandidatePairId)
                selectedCandidatePairId = report.selectedCandidatePairId;
            else if (report.type === "outbound-rtp" && !report.isRemote) {
                packetsSent += report.packetsSent ?? 0;
                bytesSent += report.bytesSent ?? 0;
                if (report.kind === "video") {
                    framesEncoded += report.framesEncoded ?? 0;
                    frameWidth = report.frameWidth ?? frameWidth; frameHeight = report.frameHeight ?? frameHeight;
                }
            } else if (report.type === "inbound-rtp" && !report.isRemote) {
                packetsReceived += report.packetsReceived ?? 0;
                packetsLost += report.packetsLost ?? 0;
                bytesReceived += report.bytesReceived ?? 0;
                if (report.kind === "video") {
                    framesDecoded += report.framesDecoded ?? 0; framesDropped += report.framesDropped ?? 0;
                    frameWidth = report.frameWidth ?? frameWidth; frameHeight = report.frameHeight ?? frameHeight;
                }
            }
        }
        const pairSummaries = pairs.map(pair => {
            const local = localCandidates.get(pair.localCandidateId);
            const remote = remoteCandidates.get(pair.remoteCandidateId);
            markObservedCandidateType(session, local?.candidateType);
            markObservedCandidateType(session, remote?.candidateType);
            return {
                state: pair.state ?? "unknown",
                nominated: Boolean(pair.nominated),
                selected: Boolean(pair.selected),
                localCandidateType: local?.candidateType ?? "unknown",
                remoteCandidateType: remote?.candidateType ?? "unknown",
                protocol: local?.protocol ?? remote?.protocol ?? "unknown"
            };
        });
        const nominatedPair = pairSummaries.find(pair => pair.nominated);
        const explicitlySelectedIndex = pairs.findIndex(pair => pair.selected || pair.id === selectedCandidatePairId);
        const selectedPair = explicitlySelectedIndex >= 0 ? pairSummaries[explicitlySelectedIndex] : nominatedPair;
        session.lastIceStats = {
            localCandidateCount: localCandidates.size,
            remoteCandidateCount: remoteCandidates.size,
            candidatePairCount: pairs.length,
            succeededCandidatePairCount: pairs.filter(pair => pair.state === "succeeded").length,
            nominatedPairExists: Boolean(nominatedPair), selectedPairExists: explicitlySelectedIndex >= 0, pairSummaries,
            packetsSent, packetsReceived, packetsLost, bytesSent, bytesReceived,
            framesEncoded, framesDecoded, framesDropped, frameWidth, frameHeight
        };
        diagnostic(session, event, {
            localCandidateCountStats: localCandidates.size, remoteCandidateCountStats: remoteCandidates.size,
            candidatePairCount: pairs.length, succeededCandidatePairCount: session.lastIceStats.succeededCandidatePairCount,
            nominatedPairExists: session.lastIceStats.nominatedPairExists,
            selectedPairExists: session.lastIceStats.selectedPairExists,
            pairState: selectedPair?.state, localCandidateType: selectedPair?.localCandidateType,
            remoteCandidateType: selectedPair?.remoteCandidateType, protocol: selectedPair?.protocol,
            packetsSent, packetsReceived, packetsLost, bytesSent, bytesReceived
        });
        if (!session.mediaTrafficDetected && (packetsSent > 0 || packetsReceived > 0 || bytesSent > 0 || bytesReceived > 0)) {
            session.mediaTrafficDetected = true;
            diagnostic(session, "MediaTrafficDetected", {
                packetsSent, packetsReceived, packetsLost, bytesSent, bytesReceived,
                localCandidateCountStats: localCandidates.size, remoteCandidateCountStats: remoteCandidates.size,
                candidatePairCount: pairs.length, succeededCandidatePairCount: session.lastIceStats.succeededCandidatePairCount,
                nominatedPairExists: session.lastIceStats.nominatedPairExists,
                selectedPairExists: session.lastIceStats.selectedPairExists
            });
        }
        return session.lastIceStats;
    } catch (error) {
        diagnostic(session, `${event} unavailable`, { name: error?.name, message: error?.message });
        return session.lastIceStats;
    }
}

function diagnosticSummary(session, event) {
    diagnostic(session, event, {
        createOfferCount: session.createOfferCount,
        createAnswerCount: session.createAnswerCount,
        negotiationNeededCount: session.negotiationNeededCount,
        answersReceived: session.answersReceived,
        localCandidatesGenerated: session.localCandidateCount,
        localCandidateTypes: summarizeTypes(session.localCandidateTypes),
        remoteCandidatesReceived: session.remoteCandidateCount,
        remoteCandidateTypes: summarizeTypes(session.remoteCandidateTypes),
        remoteCandidatesAdded: session.remoteCandidateAddedCount,
        remoteCandidateAddFailures: session.remoteCandidateAddFailureCount,
        queuedRemoteCandidates: session.pendingRemoteCandidates.length,
        ...candidateAvailability(session)
    });
}

async function notify(session, method, argument) {
    try {
        await session.callback.invokeMethodAsync(method, session.peerGeneration, argument);
    } catch (error) {
        if (session.diagnostics) console.error("[Iridium Voice] .NET callback failed", { method, error });
    }
}

function reportOperationError(session, operation, error) {
    const message = `${error?.name ?? "WebRtcError"}: ${operation}: ${error?.message ?? "WebRTC operation failed."}`;
    if (session?.diagnostics) console.error("[Iridium Voice]", {
        callId: session.callId, localAccountId: session.localAccountId, role: session.role,
        peerGeneration: session.peerGeneration, negotiationGeneration: session.negotiationGeneration,
        event: "operation failed", operation, name: error?.name, message: error?.message,
        signalingState: session.peer.signalingState,
        localDescription: session.peer.localDescription?.type ?? null,
        remoteDescription: session.peer.remoteDescription?.type ?? null
    });
    if (session) diagnostic(session, "WebRtcOperationFailed", {
        name: error?.name, message: error?.message, reason: operation
    });
    if (session) notify(session, "OnMediaError", message);
}

async function setDescription(session, operation, description, signalId) {
    const descriptionType = description?.type ?? "unknown";
    const details = { operation, descriptionType, signalId };
    diagnostic(session, `BEFORE ${operation}(${descriptionType})`, details);
    try {
        await session.peer[operation](description);
        diagnostic(session, `AFTER ${operation}(${descriptionType})`, {
            ...details, resultingSignalingState: session.peer.signalingState
        });
        if (operation === "setLocalDescription") {
            diagnostic(session, "LocalDescriptionImmediatelyAfterSet", {
                signalId, ...localDescriptionCandidateMetadata(session.peer)
            });
            if (session.nonTrickleDiagnostic) {
                await waitForIceGatheringComplete(session);
                diagnostic(session, "LocalDescriptionAfterGathering", {
                    signalId, ...localDescriptionCandidateMetadata(session.peer)
                });
            }
        }
    } catch (error) {
        diagnostic(session, `FAILED ${operation}(${descriptionType})`, {
            ...details, name: error?.name, message: error?.message,
            failingSignalingState: session.peer.signalingState
        });
        throw error;
    }
}

async function flushRemoteCandidates(session) {
    if (!session.peer.remoteDescription) return;
    const queued = session.pendingRemoteCandidates.splice(0);
    if (queued.length > 0) diagnostic(session, "flushing queued remote ICE candidates", { queuedCount: queued.length });
    for (const queuedCandidate of queued) {
        try {
            diagnostic(session, `JS ADDING REMOTE ICE #${queuedCandidate.sequence}`, {
                signalId: queuedCandidate.signalId, source: "queue"
            });
            await session.peer.addIceCandidate(queuedCandidate.candidate);
            session.remoteCandidateAddedCount++;
            diagnostic(session, `JS addIceCandidate SUCCESS #${queuedCandidate.sequence}`, {
                signalId: queuedCandidate.signalId, source: "queue"
            });
        } catch (error) {
            session.remoteCandidateAddFailureCount++;
            diagnostic(session, `JS addIceCandidate FAILED #${queuedCandidate.sequence}`, {
                signalId: queuedCandidate.signalId, source: "queue", name: error?.name, message: error?.message
            });
            throw error;
        }
    }
    if (queued.length > 0) diagnostic(session, "IceQueueFlushCompleted", { count: queued.length });
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

export async function initialize(mediaBuildId, callback, iceServers, iceTransportPolicy = "all", diagnostics = false, callId = null, localAccountId = null,
    role = "unknown", peerGeneration = 0, negotiationId = null, negotiationGeneration = 0,
    remoteAccountId = null, participantPreference = null, polite = false) {
    const build = await import(`./mediaBuild.js?build=${encodeURIComponent(mediaBuildId)}`);
    await build.requireMatchingMediaBuild(mediaBuildId);
    ({ createRemoteVoicePlayback, updateRemoteVoicePlayback, destroyRemoteVoicePlayback } =
        await build.loadVoicePlayback(mediaBuildId));
    if (!navigator.mediaDevices?.getUserMedia) throw new Error("NotSupportedError: This browser does not support microphone capture.");
    const initializing = { callback, diagnostics, callId, localAccountId, role, peerGeneration,
        negotiationGeneration, peer: null };
    let localStream;
    try {
        diagnostic(initializing, "GetUserMediaStarted");
        localStream = await navigator.mediaDevices.getUserMedia({ audio: true });
        const firstTrack = localStream.getAudioTracks()[0];
        diagnostic(initializing, "GetUserMediaSucceeded", { audioTrackCount: localStream.getAudioTracks().length,
            trackEnabled: firstTrack?.enabled, trackReadyState: firstTrack?.readyState });
    } catch (error) {
        diagnostic(initializing, "GetUserMediaFailed", { name: error?.name, message: error?.message });
        throw new Error(`${error?.name ?? "MediaError"}: ${error?.message ?? "Microphone access failed."}`);
    }

    const id = crypto.randomUUID();
    let session;
    try {
        const peer = new RTCPeerConnection({
            iceServers: normalizeIceServers(iceServers),
            iceTransportPolicy: normalizeIceTransportPolicy(iceTransportPolicy)
        });
        const configurationMetadata = iceConfigurationMetadata(iceServers);
        session = {
            id, callback, peer, localStream, remoteStream: new MediaStream(), diagnostics,
            callId, localAccountId, role, peerGeneration, negotiationId, negotiationGeneration,
            appliedAnswerNegotiationId: null,
            answersReceived: 0, answersApplied: 0, answersIgnored: 0,
            createOfferCount: 0, createAnswerCount: 0, negotiationNeededCount: 0,
            lastAnswerSignalingStateBefore: null, lastAnswerSignalingStateAfter: null,
            localCandidateCount: 0, localCandidateEventCount: 0, remoteCandidateCount: 0,
            remoteCandidateAddedCount: 0, remoteCandidateAddFailureCount: 0,
            localCandidateTypes: {}, remoteCandidateTypes: {}, observedCandidateTypes: {
                host:false, srflx:false, prflx:false, relay:false
            }, selectedPair: null, selectedPairReported:false, ...configurationMetadata,
            lastIceStats: { localCandidateCount: 0, remoteCandidateCount: 0, candidatePairCount: 0,
                succeededCandidatePairCount: 0, nominatedPairExists: false, selectedPairExists: false,
                pairSummaries: [], packetsSent: 0, packetsReceived: 0, packetsLost: 0, bytesSent: 0, bytesReceived: 0 },
            remoteCandidateSequence: 0,
            remoteIceQueuedCount: 0, remoteTrackReceived: false, remoteAudioPlaySucceeded: false,
            mediaTrafficDetected: false,
            nonTrickleDiagnostic: diagnostics && localStorage.getItem("iridium.voice.nonTrickleDiagnostic") === "true",
            pendingRemoteCandidates: [], speaking: false, vadFrame: null, audioContext: null,
            audioSource: null, analyser: null,
            remoteAccountId, participantPreference: participantPreference ?? { volumePercent:100, locallyMuted:false },
            remotePlayback: null, deafened: false, visualStreams:new Map(), viewers:new Map(), screenShare:null,
            polite,
            makingOffer:false, ignoreOffer:false, isSettingRemoteAnswerPending:false,
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
        const iceCandidateHandler = async event => {
            const sequence = ++session.localCandidateEventCount;
            diagnostic(session, "IceCandidateEventFired", {
                candidateSequence: sequence, candidatePresent: event.candidate !== null
            });
            if (!event.candidate) {
                diagnostic(session, "ICE candidate gathering complete", {
                    candidateSequence: sequence, localCandidateCount: session.localCandidateCount
                });
                diagnostic(session, "LocalDescriptionAfterGathering", localDescriptionCandidateMetadata(peer));
                return;
            }
            const signalId = crypto.randomUUID();
            let callbackStarted = false;
            try {
                const serialized = typeof event.candidate.toJSON === "function" ? event.candidate.toJSON() : event.candidate;
                const value = {
                    candidate: serialized.candidate,
                    sdpMid: serialized.sdpMid ?? null,
                    sdpMLineIndex: serialized.sdpMLineIndex ?? null,
                    usernameFragment: serialized.usernameFragment ?? null
                };
                if (typeof value.candidate !== "string" || value.candidate.length === 0)
                    throw new TypeError("RTCIceCandidate did not contain a candidate string.");
                const metadata = safeCandidateMetadata(event.candidate);
                session.localCandidateCount++;
                incrementType(session.localCandidateTypes, metadata.candidateType, metadata.protocol);
                markObservedCandidateType(session, metadata.candidateType);
                diagnostic(session, `JS GENERATED ICE #${sequence}`, {
                    signalId, candidateSequence: sequence, ...metadata,
                    sdpMid: value.sdpMid, sdpMLineIndex: value.sdpMLineIndex,
                    localCandidateCount: session.localCandidateCount
                });
                // TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
                // In this opt-in Development test mode the completed local SDP is the only
                // candidate transport, so a successful call independently proves that the
                // browser gathered usable candidates even when trickle signaling is bypassed.
                if (session.nonTrickleDiagnostic) {
                    diagnostic(session, "IceTrickleSuppressedForNonTrickleDiagnostic", {
                        signalId, candidateSequence: sequence, ...metadata
                    });
                    return;
                }
                diagnostic(session, `JS INVOKING DOTNET ICE CALLBACK #${sequence}`, {
                    signalId, candidateSequence: sequence, ...metadata,
                    sdpMid: value.sdpMid, sdpMLineIndex: value.sdpMLineIndex
                });
                callbackStarted = true;
                await session.callback.invokeMethodAsync("OnIceCandidate", session.peerGeneration,
                    session.negotiationGeneration, sequence, signalId, value);
                diagnostic(session, `DOTNET ICE CALLBACK COMPLETED #${sequence}`, { signalId });
            } catch (error) {
                if (session.diagnostics) console.error("[Iridium Voice] .NET ICE callback failed", {
                    callId: session.callId, peerGeneration: session.peerGeneration,
                    negotiationGeneration: session.negotiationGeneration, sequence, signalId,
                    name: error?.name, message: error?.message
                });
                await sendVoiceDiagnostic(session,
                    callbackStarted ? "IceDotNetCallbackFailed" : "IceCandidatePreparationFailed", {
                    signalId, candidateSequence: sequence, name: error?.name, message: error?.message
                });
            }
        };
        session.iceCandidateHandler = iceCandidateHandler;
        peer.addEventListener("icecandidate", iceCandidateHandler);
        diagnostic(session, "IceHandlerRegistered");
        peer.onnegotiationneeded = () => {
            session.negotiationNeededCount++;
            diagnostic(session, "negotiationneeded observed; serialized coordinator owns offer creation", {
                negotiationNeededCount: session.negotiationNeededCount, polite:session.polite,
                makingOffer:session.makingOffer
            });
        };
        for (const track of localStream.getAudioTracks()) {
            peer.addTrack(track, localStream);
            diagnostic(session, "microphone track added", { kind: track.kind, enabled: track.enabled,
                readyState: track.readyState, senderCount: peer.getSenders().length });
        }

        peer.onconnectionstatechange = async () => {
            stateTransition(session, "connectionState", peer.connectionState);
            if (peer.connectionState === "connected") {
                diagnostic(session, "transport connected; notifying .NET to cancel negotiation timeout");
                if (!session.selectedPairReported) {
                    session.selectedPairReported = true;
                    await selectedPairSummary(session);
                }
            } else if (peer.connectionState === "failed") {
                await iceStatsSummary(session, "ICE getStats at failure");
                diagnosticSummary(session, "WebRTC connection failed");
            }
            notify(session, "OnConnectionStateChanged", peer.connectionState);
        };
        peer.oniceconnectionstatechange = async () => {
            stateTransition(session, "iceConnectionState", peer.iceConnectionState);
            notify(session, "OnIceConnectionStateChanged", peer.iceConnectionState);
            // Selected candidate-pair stats are captured once by connectionstatechange.
        };
        peer.onicegatheringstatechange = () => stateTransition(session, "iceGatheringState", peer.iceGatheringState);
        peer.onsignalingstatechange = () => stateTransition(session, "signalingState", peer.signalingState);
        peer.onicecandidateerror = event => diagnostic(session, "ICE candidate error (non-terminal)", {
            errorCode: event.errorCode, errorText: event.errorText, urlScheme: event.url?.split(":", 1)[0] ?? null
        });
        peer.ontrack = async event => {
            session.remoteTrackReceived = true;
            diagnostic(session, "ontrack fired; remote audio track received", {
                kind: event.track.kind, enabled: event.track.enabled, readyState: event.track.readyState,
                muted: event.track.muted,
                streamCount: event.streams?.length ?? 0
            });
            event.track.onmute = () => diagnostic(session, "RemoteTrackMuted", {
                kind: event.track.kind, readyState: event.track.readyState, muted: event.track.muted });
            event.track.onunmute = () => diagnostic(session, "RemoteTrackUnmuted", {
                kind: event.track.kind, readyState: event.track.readyState, muted: event.track.muted });
            event.track.onended = () => diagnostic(session, "RemoteTrackEnded", {
                kind: event.track.kind, readyState: event.track.readyState, muted: event.track.muted });
            const stream = event.streams?.[0];
            const visualStream = stream?.getVideoTracks().length > 0 || event.track.kind === "video";
            if (visualStream) {
                const value = stream ?? new MediaStream([event.track]);
                session.visualStreams.set(value.id, value);
                diagnostic(session, event.track.kind === "video" ? "RemoteVideoTrackReceived" : "RemoteScreenAudioTrackReceived",
                    { kind:event.track.kind, readyState:event.track.readyState, streamId:value.id });
                event.track.onended = () => {
                    if (value.getTracks().every(track => track.readyState === "ended")) session.visualStreams.delete(value.id);
                    for (const [elementId, viewer] of session.viewers)
                        if (viewer.mediaStreamId === value.id) detachViewer(session, elementId);
                };
                return;
            }
            if (stream) session.remoteStream = stream;
            else session.remoteStream.addTrack(event.track);
            destroyRemoteVoicePlayback(session.remotePlayback);
            session.remotePlayback = await createRemoteVoicePlayback(session.remoteStream, session.audioContext, {
                ...session.participantPreference, deafened:session.deafened,
                diagnostic:(name, values) => diagnostic(session, name, values)
            });
            session.remoteAudioPlaySucceeded = session.remotePlayback.mode !== "none";
        };
        diagnostic(session, "PeerHandlersReady", { count: 7 });
        if (session.nonTrickleDiagnostic) diagnostic(session, "NonTrickleDiagnosticModeEnabled");
        diagnostic(session, "For additional Chromium diagnostics, inspect chrome://webrtc-internals while the call is active.");
        await startVoiceActivityDetection(session);
        return id;
    } catch (error) {
        sessions.delete(id);
        for (const track of localStream.getTracks()) {
            diagnostic(session ?? initializing, "local media track stop()", { reason: "initialization failure", kind: track.kind });
            track.stop();
        }
        if (session?.audioContext) await session.audioContext.close().catch(() => {});
        throw error;
    }
}

export async function createOffer(id, negotiationId, signalId) {
    const session = requireSession(id);
    session.makingOffer = true;
    try {
        if (session.peer.signalingState !== "stable")
            throw new DOMException(`Cannot create an offer while signalingState is ${session.peer.signalingState}.`, "InvalidStateError");
        if (session.negotiationId !== negotiationId) session.negotiationGeneration++;
        session.negotiationId = negotiationId;
        session.appliedAnswerNegotiationId = null;
        session.createOfferCount++;
        diagnostic(session, "RenegotiationMakingOffer", { signalId, createOfferCount: session.createOfferCount,
            makingOffer:true, polite:session.polite, signalingStateBefore:session.peer.signalingState });
        const offer = await session.peer.createOffer();
        diagnostic(session, "createOffer completed", { signalId, ...descriptionMetadata(offer) });
        await setDescription(session, "setLocalDescription", offer, signalId);
        return { type: session.peer.localDescription.type, sdp: session.peer.localDescription.sdp };
    } catch (error) {
        reportOperationError(session, "create/set local offer", error);
        throw error;
    } finally { session.makingOffer = false; }
}

export async function acceptOffer(id, negotiationId, offerSignalId, answerSignalId, offer) {
    const session = requireSession(id);
    try {
        if (session.peer.signalingState === "have-local-offer") {
            diagnostic(session, "RenegotiationGlareRollback", { signalId:offerSignalId, polite:session.polite,
                offerCollision:true, rollbackPerformed:true, signalingStateBefore:session.peer.signalingState });
            await session.peer.setLocalDescription({ type:"rollback" });
        }
        if (session.peer.signalingState !== "stable")
            throw new DOMException(`Cannot accept an offer while signalingState is ${session.peer.signalingState}.`, "InvalidStateError");
        if (session.negotiationId !== negotiationId) session.negotiationGeneration++;
        session.negotiationId = negotiationId;
        session.appliedAnswerNegotiationId = null;
        await setDescription(session, "setRemoteDescription", browserDescription(session, offer, "offer"), offerSignalId);
        await flushRemoteCandidates(session);
        session.createAnswerCount++;
        diagnostic(session, "createAnswer started", { signalId: answerSignalId, createAnswerCount: session.createAnswerCount });
        const answer = await session.peer.createAnswer();
        diagnostic(session, "createAnswer completed", { signalId: answerSignalId, ...descriptionMetadata(answer) });
        await setDescription(session, "setLocalDescription", answer, answerSignalId);
        return { type: session.peer.localDescription.type, sdp: session.peer.localDescription.sdp };
    } catch (error) {
        reportOperationError(session, "accept offer/create answer", error);
        throw error;
    }
}

export async function applyAnswer(id, negotiationId, signalId, answer) {
    const session = requireSession(id);
    session.isSettingRemoteAnswerPending = true;
    try {
        const stateBefore = session.peer.signalingState;
        session.answersReceived++;
        session.lastAnswerSignalingStateBefore = stateBefore;
        const alreadyProcessed = session.appliedAnswerNegotiationId === negotiationId;
        diagnostic(session, "answer application requested", { negotiationId, signalId, stateBefore, alreadyProcessed });
        if (session.negotiationId !== negotiationId) {
            session.answersIgnored++;
            diagnostic(session, "stale answer ignored", { negotiationId, signalId, ignoreReason: "stale-negotiation" });
            return { applied: false, signalingState: stateBefore, ignoreReason: "stale-negotiation" };
        }
        if (alreadyProcessed) {
            session.answersIgnored++;
            diagnostic(session, "duplicate answer ignored", { negotiationId, signalId, ignoreReason: "duplicate-answer" });
            return { applied: false, signalingState: stateBefore, ignoreReason: "duplicate-answer" };
        }
        if (stateBefore !== "have-local-offer") {
            diagnostic(session, "UnexpectedAnswerState", { signalId, answersReceived: session.answersReceived });
            if (stateBefore === "stable") {
                session.answersIgnored++;
                diagnostic(session, "stale/duplicate answer ignored before setRemoteDescription", {
                    negotiationId, signalId, ignoreReason: "answer-already-applied"
                });
                return { applied: false, signalingState: stateBefore, ignoreReason: "answer-already-applied" };
            }
            throw new DOMException(`Cannot apply a remote answer while signalingState is ${stateBefore}.`, "InvalidStateError");
        }
        await setDescription(session, "setRemoteDescription", browserDescription(session, answer, "answer"), signalId);
        session.appliedAnswerNegotiationId = negotiationId;
        session.answersApplied++;
        session.lastAnswerSignalingStateAfter = session.peer.signalingState;
        diagnostic(session, "setRemoteDescription(answer) completed", { negotiationId, signalId, stateAfter: session.peer.signalingState });
        await flushRemoteCandidates(session);
        return { applied: true, signalingState: session.peer.signalingState, ignoreReason: null };
    } catch (error) {
        reportOperationError(session, "set remote answer", error);
        throw error;
    } finally { session.isSettingRemoteAnswerPending = false; }
}

export async function addIceCandidate(id, signalId, candidate) {
    const session = requireSession(id);
    try {
        const browserValue = browserCandidate(session, candidate);
        session.remoteCandidateCount++;
        const sequence = ++session.remoteCandidateSequence;
        incrementType(session.remoteCandidateTypes, browserValue.type, browserValue.protocol);
        diagnostic(session, `JS RECEIVED REMOTE ICE #${sequence}`, {
            signalId,
            candidateType: browserValue.type ?? "unknown", protocol: browserValue.protocol ?? "unknown",
            remoteCandidateCount: session.remoteCandidateCount
        });
        if (!session.peer.remoteDescription) {
            session.pendingRemoteCandidates.push({ signalId, sequence, candidate: browserValue });
            session.remoteIceQueuedCount++;
            diagnostic(session, `JS QUEUED REMOTE ICE #${sequence}`, {
                signalId, queuedCount: session.pendingRemoteCandidates.length
            });
            return;
        }
        diagnostic(session, `JS ADDING REMOTE ICE #${sequence}`, { signalId, source: "direct" });
        await session.peer.addIceCandidate(browserValue);
        session.remoteCandidateAddedCount++;
        diagnostic(session, `JS addIceCandidate SUCCESS #${sequence}`, { signalId, source: "direct" });
    } catch (error) {
        session.remoteCandidateAddFailureCount++;
        diagnostic(session, "JS addIceCandidate FAILED", {
            signalId, source: "direct", name: error?.name, message: error?.message
        });
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
        localDescriptionType: session.peer.localDescription?.type ?? null,
        remoteDescriptionType: session.peer.remoteDescription?.type ?? null,
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
        createOfferCount: session.createOfferCount,
        createAnswerCount: session.createAnswerCount,
        negotiationNeededCount: session.negotiationNeededCount,
        negotiationGeneration: session.negotiationGeneration,
        peerGeneration: session.peerGeneration,
        role: session.role,
        statsLocalCandidateCount: session.lastIceStats.localCandidateCount,
        statsRemoteCandidateCount: session.lastIceStats.remoteCandidateCount,
        statsCandidatePairCount: session.lastIceStats.candidatePairCount,
        candidatePairSummary: session.lastIceStats.pairSummaries.map(pair =>
            `${pair.state};nominated=${pair.nominated};selected=${pair.selected};${pair.localCandidateType}->${pair.remoteCandidateType}/${pair.protocol}`).join(" | ") || "none",
        statsSucceededCandidatePairCount: session.lastIceStats.succeededCandidatePairCount,
        statsNominatedPairExists: session.lastIceStats.nominatedPairExists,
        statsSelectedPairExists: session.lastIceStats.selectedPairExists,
        packetsSent: session.lastIceStats.packetsSent, packetsReceived: session.lastIceStats.packetsReceived,
        packetsLost: session.lastIceStats.packetsLost, bytesSent: session.lastIceStats.bytesSent,
        bytesReceived: session.lastIceStats.bytesReceived, remoteTrackReceived: session.remoteTrackReceived,
        remoteAudioPlaySucceeded: session.remoteAudioPlaySucceeded, mediaTrafficDetected: session.mediaTrafficDetected,
        ...candidateAvailability(session)
    };
}

export async function getDiagnosticSnapshot(id) {
    const session = requireSession(id);
    await iceStatsSummary(session, "StatsSnapshot");
    return snapshot(session);
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
    const session = requireSession(id); session.deafened = deafened;
    updateRemoteVoicePlayback(session.remotePlayback, { deafened });
}

export function setParticipantPreference(id, preference) {
    const session = requireSession(id); session.participantPreference = preference;
    updateRemoteVoicePlayback(session.remotePlayback, preference);
}

function detachViewer(session, elementId) {
    const viewer = session.viewers.get(elementId);
    const element = document.getElementById(elementId);
    if (element) {
        element.pause();
        element.srcObject = null;
    }
    session.viewers.delete(elementId);
    if (viewer) diagnostic(session, "StreamViewerDetached", { streamId:viewer.mediaStreamId });
}

async function waitForVisualStream(session, mediaStreamId) {
    if (session.screenShare?.mediaStreamId === mediaStreamId) return session.screenShare.stream;
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
    diagnostic(session, "DisplayCaptureStarted");
    let stream;
    try {
        stream = await navigator.mediaDevices.getDisplayMedia({
            video:{ frameRate:{ ideal:30, max:30 } }, audio:true
        });
    } catch (error) {
        diagnostic(session, "DisplayCaptureFailed", { name:error?.name, message:error?.message });
        throw error;
    }
    const videoTrack = stream.getVideoTracks()[0];
    if (!videoTrack) {
        for (const track of stream.getTracks()) track.stop();
        throw new DOMException("Screen capture did not provide a video track.", "NotFoundError");
    }
    const streamId = crypto.randomUUID();
    const senders = [];
    for (const track of stream.getTracks()) {
        senders.push(session.peer.addTrack(track, stream));
        diagnostic(session, track.kind === "video" ? "VideoTrackAdded" : "ScreenAudioTrackAdded",
            { kind:track.kind, readyState:track.readyState, streamId:stream.id });
    }
    session.screenShare = { streamId, mediaStreamId:stream.id, stream, senders, stopping:false };
    videoTrack.addEventListener("ended", () => {
        if (!session.screenShare || session.screenShare.streamId !== streamId || session.screenShare.stopping) return;
        session.callback.invokeMethodAsync("OnScreenShareEnded", session.peerGeneration, "BrowserStopSharing")
            .catch(() => {});
    }, { once:true });
    diagnostic(session, "DisplayCaptureSucceeded", { streamId:stream.id,
        audioTrackCount:stream.getAudioTracks().length, trackReadyState:videoTrack.readyState });
    return { streamId, kind:0, hasAudio:stream.getAudioTracks().length > 0, mediaStreamId:stream.id };
}

export async function switchScreenShare(id) {
    const session = requireSession(id), share = session.screenShare;
    if (!share) throw new DOMException("There is no active screen share.", "InvalidStateError");
    const replacement = await navigator.mediaDevices.getDisplayMedia({
        video:{ frameRate:{ ideal:30, max:30 } }, audio:true
    });
    const videoTrack = replacement.getVideoTracks()[0];
    if (!videoTrack) {
        replacement.getTracks().forEach(track => track.stop());
        throw new DOMException("Screen capture did not provide a video track.", "NotFoundError");
    }
    const replacementByKind = new Map(replacement.getTracks().map(track => [track.kind, track]));
    for (const sender of share.senders) {
        const kind = sender.track?.kind, next = replacementByKind.get(kind);
        await sender.replaceTrack(next ?? null);
        replacementByKind.delete(kind);
    }
    for (const track of replacementByKind.values()) share.senders.push(session.peer.addTrack(track, replacement));
    const old = share.stream;
    share.stream = replacement; share.stopping = false;
    for (const [elementId, viewer] of session.viewers)
        if (viewer.mediaStreamId === share.mediaStreamId) {
            const element = document.getElementById(elementId); if (element) element.srcObject = replacement;
        }
    videoTrack.addEventListener("ended", () => {
        if (session.screenShare?.stream !== replacement || session.screenShare.stopping) return;
        session.callback.invokeMethodAsync("OnScreenShareEnded", session.peerGeneration, "BrowserStopSharing").catch(() => {});
    }, { once:true });
    old.getTracks().forEach(track => track.stop());
    return { streamId:share.streamId, kind:0, hasAudio:replacement.getAudioTracks().length > 0,
        mediaStreamId:share.mediaStreamId };
}

export async function stopScreenShare(id, reason = "UserStoppedInIridium") {
    const session = requireSession(id);
    const share = session.screenShare;
    if (!share) return;
    session.screenShare = null;
    share.stopping = true;
    for (const sender of share.senders) {
        try { session.peer.removeTrack(sender); } catch { }
    }
    for (const track of share.stream.getTracks()) track.stop();
    diagnostic(session, "ScreenShareEnded", { reason, streamId:share.mediaStreamId });
}

export async function attachStreamViewer(id, mediaStreamId, elementId, audioMuted, volumePercent = 100) {
    const session = requireSession(id);
    const stream = await waitForVisualStream(session, mediaStreamId);
    const element = document.getElementById(elementId);
    if (!element) throw new DOMException("The stream viewer is unavailable.", "NotFoundError");
    detachViewer(session, elementId);
    element.srcObject = stream;
    element.muted = !!audioMuted || stream.getAudioTracks().length === 0;
    element.volume = Math.min(1, Math.max(0, Number(volumePercent) || 0) / 100);
    element.playsInline = true;
    session.viewers.set(elementId, { mediaStreamId });
    diagnostic(session, "VideoPlaybackStarted", { streamId:mediaStreamId });
    await element.play();
    diagnostic(session, "VideoPlaybackSucceeded", { streamId:mediaStreamId });
}

export function detachStreamViewer(id, elementId) { detachViewer(requireSession(id), elementId); }

export function setStreamAudioMuted(id, elementId, muted) {
    requireSession(id);
    const element = document.getElementById(elementId);
    if (element) element.muted = !!muted;
}

export function setStreamAudioVolume(id, elementId, volumePercent) {
    requireSession(id);
    const element = document.getElementById(elementId);
    if (element) element.volume = Math.min(1, Math.max(0, Number(volumePercent) || 0) / 100);
}

export async function requestStreamFullscreen(id, elementId) {
    requireSession(id);
    const element = document.getElementById(elementId);
    if (!element?.requestFullscreen) throw new DOMException("Fullscreen is unavailable.", "NotSupportedError");
    await element.requestFullscreen();
}

export async function cleanup(id, reason = "unspecified cleanup") {
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
    setSpeaking(session, false);
    if (session.iceCandidateHandler)
        session.peer.removeEventListener("icecandidate", session.iceCandidateHandler);
    session.peer.onicecandidateerror = null;
    session.peer.onconnectionstatechange = null;
    session.peer.oniceconnectionstatechange = null;
    session.peer.onicegatheringstatechange = null;
    session.peer.onsignalingstatechange = null;
    session.peer.onnegotiationneeded = null;
    session.peer.ontrack = null;
    await diagnostic(session, "RTCPeerConnection.close()", { reason });
    session.peer.close();
    for (const track of session.localStream.getTracks()) {
        diagnostic(session, "local media track stop()", { reason, kind: track.kind });
        track.stop();
    }
    for (const track of session.remoteStream.getTracks()) {
        diagnostic(session, "remote media track stop()", { reason, kind: track.kind });
        track.stop();
    }
    session.audioSource?.disconnect();
    session.analyser?.disconnect();
    if (session.audioContext) await session.audioContext.close().catch(() => {});
    destroyRemoteVoicePlayback(session.remotePlayback);
    await diagnostic(session, "PeerConnection and media cleaned up", { reason });
}
