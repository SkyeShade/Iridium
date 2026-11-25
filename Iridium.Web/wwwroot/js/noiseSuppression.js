// Enhanced noise suppression runs RNNoise (via @sapphi-red/web-noise-suppressor) as an
// AudioWorklet between the microphone and every voice transport. "standard" is the browser's own
// suppressor and "off" disables both. Any failure falls back to the browser suppressor, so a broken
// filter never silences someone.
const libraryUrl = new URL("../lib/web-noise-suppressor/", import.meta.url);
// RNNoise only processes 48 kHz audio; the worklet context resamples the microphone to it.
const rnnoiseSampleRate = 48_000;
let rnnoiseLibrary = null;

export const noiseSuppressionModes = Object.freeze(["enhanced", "standard", "off"]);

export function noiseSuppressionMode(value) {
    const mode = String(value ?? "").toLowerCase();
    return noiseSuppressionModes.includes(mode) ? mode : "enhanced";
}

export function enhancedNoiseSuppressionSupported() {
    return typeof globalThis.AudioWorkletNode === "function" && typeof globalThis.WebAssembly === "object" &&
        !!(globalThis.AudioContext ?? globalThis.webkitAudioContext);
}

// The browser suppressor stays on as the fallback; next to RNNoise it would process the voice twice.
export function microphoneCaptureConstraints(inputDeviceId = null, mode = "enhanced", enhancedActive = null) {
    mode = noiseSuppressionMode(mode);
    const enhanced = enhancedActive ?? (mode === "enhanced" && enhancedNoiseSuppressionSupported());
    return {
        channelCount: 1,
        ...(inputDeviceId ? { deviceId: { exact: inputDeviceId } } : {}),
        echoCancellation: true,
        noiseSuppression: mode !== "off" && !enhanced,
        autoGainControl: true
    };
}

function loadRnnoiseLibrary() {
    rnnoiseLibrary ??= (async () => {
        const library = await import(new URL("index.js", libraryUrl).href);
        const wasmBinary = await library.loadRnnoise({
            url: new URL("rnnoise.wasm", libraryUrl).href,
            simdUrl: new URL("rnnoise_simd.wasm", libraryUrl).href
        });
        return { RnnoiseWorkletNode: library.RnnoiseWorkletNode, wasmBinary };
    })();
    rnnoiseLibrary.catch(() => { rnnoiseLibrary = null; });
    return rnnoiseLibrary;
}

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

// One RNNoise graph. attach() can be called again with a replacement microphone track (device
// switch) and processedTrack stays the same MediaStreamTrack for the graph's whole life.
export async function createNoiseSuppressor() {
    if (!enhancedNoiseSuppressionSupported())
        throw new DOMException("AudioWorklet is unavailable.", "NotSupportedError");
    const { RnnoiseWorkletNode, wasmBinary } = await loadRnnoiseLibrary();
    const AudioContextType = globalThis.AudioContext ?? globalThis.webkitAudioContext;
    const context = new AudioContextType({ sampleRate: rnnoiseSampleRate, latencyHint: "interactive" });
    try {
        await context.audioWorklet.addModule(new URL("rnnoise/workletProcessor.js", libraryUrl).href);
        const node = new RnnoiseWorkletNode(context, { wasmBinary, maxChannels: 1 });
        const destination = context.createMediaStreamDestination();
        node.connect(destination);
        if (context.state !== "running") await Promise.race([context.resume(), delay(1000)]).catch(() => {});
        // A suspended context outputs silence, which is worse than unfiltered audio.
        if (context.state !== "running")
            throw new DOMException("The noise suppression audio context did not start.", "InvalidStateError");
        // Mobile browsers suspend audio on interruptions such as a phone call; pick back up afterwards.
        context.onstatechange = () => {
            if (context.state === "suspended" || context.state === "interrupted") void context.resume().catch(() => {});
        };
        const processedTrack = destination.stream.getAudioTracks()[0];
        let source = null;
        const detach = () => {
            try { source?.disconnect(); } catch { }
            source = null;
        };
        return {
            processedTrack,
            attach(track) {
                detach();
                source = context.createMediaStreamSource(new MediaStream([track]));
                source.connect(node);
            },
            detach,
            async close() {
                detach();
                context.onstatechange = null;
                try { node.disconnect(); } catch { }
                node.destroy();
                processedTrack.stop();
                await context.close().catch(() => {});
            }
        };
    } catch (error) {
        await context.close().catch(() => {});
        throw error;
    }
}

async function enhancedSuppressorFor(mode, diagnostic) {
    if (noiseSuppressionMode(mode) !== "enhanced" || !enhancedNoiseSuppressionSupported()) return null;
    try {
        return await createNoiseSuppressor();
    } catch (error) {
        diagnostic?.("NoiseSuppressionUnavailable", { name: error?.name, message: error?.message });
        return null;
    }
}

// Captures the microphone for a voice session. stream is what transports should send and analyse;
// release() stops the microphone and the filter. noiseSuppression reports the mode in effect.
export async function captureVoiceMicrophone(inputDeviceId, mode, diagnostic = null) {
    inputDeviceId ||= null;
    mode = noiseSuppressionMode(mode);
    const suppressor = await enhancedSuppressorFor(mode, diagnostic);
    const capture = enhanced => navigator.mediaDevices.getUserMedia({
        audio: microphoneCaptureConstraints(inputDeviceId, mode, enhanced)
    });
    const plain = (stream, effective) => ({
        stream, inputDeviceId, mode, noiseSuppression: effective,
        release() { for (const track of stream.getTracks()) track.stop(); }
    });
    if (!suppressor) return plain(await capture(false), mode === "off" ? "off" : "standard");
    let raw;
    try {
        raw = await capture(true);
    } catch (error) {
        await suppressor.close();
        throw error;
    }
    try {
        suppressor.attach(raw.getAudioTracks()[0]);
    } catch (error) {
        // Some browsers cannot mix a microphone into a context at a different sample rate.
        diagnostic?.("NoiseSuppressionUnavailable", { name: error?.name, message: error?.message });
        for (const track of raw.getTracks()) track.stop();
        await suppressor.close();
        return plain(await capture(false), "standard");
    }
    return {
        stream: new MediaStream([suppressor.processedTrack]), inputDeviceId, mode, noiseSuppression: "enhanced",
        release() {
            for (const track of raw.getTracks()) track.stop();
            void suppressor.close();
        }
    };
}

// Swaps a live session's microphone on its peer connections when the device or noise suppression
// mode changed. Returns the new capture, or null when nothing changed; the caller rewires its own
// analysers and releases the previous capture.
export async function replaceVoiceMicrophone(current, inputDeviceId, mode, peers, enabled, diagnostic = null) {
    if (current.inputDeviceId === (inputDeviceId || null) && current.mode === noiseSuppressionMode(mode)) return null;
    const next = await captureVoiceMicrophone(inputDeviceId, mode, diagnostic);
    const previousTrack = current.stream.getAudioTracks()[0];
    const track = next.stream.getAudioTracks()[0];
    track.enabled = enabled;
    try {
        for (const peer of peers)
            for (const sender of peer.getSenders())
                if (sender.track === previousTrack) await sender.replaceTrack(track);
    } catch (error) {
        next.release();
        throw error;
    }
    return next;
}

// A LiveKit TrackProcessor. LiveKit stops processedTrack when it tears the processor down, so every
// init builds a fresh graph; restart() (device switch, mic re-acquired on unmute) re-attaches it.
export function createLiveKitNoiseProcessor(diagnostic = null) {
    let suppressor = null;
    const fallBack = async (track, error) => {
        diagnostic?.("NoiseSuppressionUnavailable", { name: error?.name, message: error?.message });
        await suppressor?.close();
        suppressor = null;
        // LiveKit sends the raw track when processedTrack is missing; give it the browser suppressor.
        await track?.applyConstraints?.({ ...track.getConstraints?.(), noiseSuppression: true }).catch(() => {});
    };
    return {
        name: "iridium-rnnoise",
        get processedTrack() { return suppressor?.processedTrack; },
        get active() { return suppressor !== null; },
        async init({ track }) {
            await suppressor?.close();
            suppressor = null;
            try {
                suppressor = await createNoiseSuppressor();
                suppressor.attach(track);
            } catch (error) {
                await fallBack(track, error);
            }
        },
        async restart({ track }) {
            if (!suppressor) return;
            try { suppressor.attach(track); } catch (error) { await fallBack(track, error); }
        },
        async destroy() {
            const closing = suppressor;
            suppressor = null;
            await closing?.close();
        }
    };
}
