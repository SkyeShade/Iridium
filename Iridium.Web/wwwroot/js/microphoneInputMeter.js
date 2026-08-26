import { normalizedInputLevel } from "./inputSensitivity.js";

const meters = new Map();

function microphoneConstraints(deviceId) {
    return {
        audio: {
            ...(deviceId ? { deviceId: { exact: deviceId } } : {}),
            channelCount: 1,
            echoCancellation: true,
            noiseSuppression: true,
            autoGainControl: true
        }
    };
}

async function inputDevices() {
    if (!navigator.mediaDevices?.enumerateDevices) return [];
    const devices = await navigator.mediaDevices.enumerateDevices();
    let index = 0;
    return devices.filter(device => device.kind === "audioinput").map(device => ({
        deviceId: device.deviceId,
        label: device.label || `Microphone ${++index}`
    }));
}

function unavailableStatus(error) {
    if (error?.name === "NotAllowedError" || error?.name === "SecurityError")
        return { status: "permission-denied", message: "Microphone permission is required for the live input preview." };
    if (error?.name === "NotFoundError" || error?.name === "OverconstrainedError")
        return { status: "no-device", message: "No microphone is available for the live input preview." };
    return { status: "unavailable", message: "Live microphone input preview is unavailable." };
}

function releaseGraph(meter) {
    if (meter.frame) cancelAnimationFrame(meter.frame);
    meter.frame = 0;
    try { meter.source?.disconnect(); } catch { }
    try { meter.analyser?.disconnect(); } catch { }
    for (const track of meter.stream?.getTracks?.() ?? []) track.stop();
    meter.stream = null;
    meter.source = null;
    meter.analyser = null;
    void meter.context?.close?.();
    meter.context = null;
}

async function createGraph(deviceId) {
    const stream = await navigator.mediaDevices.getUserMedia(microphoneConstraints(deviceId));
    let context;
    try {
        const AudioContextType = window.AudioContext ?? window.webkitAudioContext;
        if (!AudioContextType)
            throw new DOMException("Web Audio is unavailable.", "NotSupportedError");
        context = new AudioContextType();
        const source = context.createMediaStreamSource(stream);
        const analyser = context.createAnalyser();
        analyser.fftSize = 512;
        analyser.smoothingTimeConstant = 0.72;
        source.connect(analyser);
        if (context.state === "suspended") await context.resume().catch(() => {});
        return { stream, context, source, analyser, deviceId: deviceId || null };
    } catch (error) {
        stream.getTracks().forEach(track => track.stop());
        try { await context?.close?.(); } catch { }
        throw error;
    }
}

function installGraph(meter, graph) {
    releaseGraph(meter);
    Object.assign(meter, graph);
    const analyser = meter.analyser;
    const samples = new Float32Array(analyser.fftSize);
    let smoothed = 0, lastReport = 0;
    const sample = timestamp => {
        if (!meters.has(meter.id) || meter.analyser !== analyser) return;
        analyser.getFloatTimeDomainData(samples);
        let sum = 0;
        for (const value of samples) sum += value * value;
        const level = normalizedInputLevel(Math.sqrt(sum / samples.length));
        smoothed = level > smoothed ? smoothed * 0.45 + level * 0.55 : smoothed * 0.82 + level * 0.18;
        if (timestamp - lastReport >= 66) {
            lastReport = timestamp;
            meter.callback.invokeMethodAsync("OnMicrophoneLevel", smoothed).catch(() => {});
        }
        meter.frame = requestAnimationFrame(sample);
    };
    meter.frame = requestAnimationFrame(sample);
}

async function captureInto(meter, deviceId) {
    installGraph(meter, await createGraph(deviceId));
}

export async function startMicrophoneInputMeter(callback, deviceId) {
    if (!navigator.mediaDevices?.getUserMedia)
        return { meterId: null, status: "unavailable", message: "This browser does not support microphone input preview.", devices: [] };
    const meter = { id: crypto.randomUUID(), callback, frame: 0, stream: null, context: null, source: null, analyser: null };
    try {
        await captureInto(meter, deviceId);
        meters.set(meter.id, meter);
        return { meterId: meter.id, status: "ready", message: null, devices: await inputDevices() };
    } catch (error) {
        releaseGraph(meter);
        return { meterId: null, ...unavailableStatus(error), devices: await inputDevices().catch(() => []) };
    }
}

export async function rebindMicrophoneInputMeter(meterId, deviceId) {
    const meter = meters.get(meterId);
    if (!meter) return { status: "unavailable", message: "Live microphone input preview is unavailable." };
    // Acquire the replacement first; a failed device switch leaves the current meter alive.
    try {
        installGraph(meter, await createGraph(deviceId));
        return { status: "ready", message: null };
    } catch (error) {
        return unavailableStatus(error);
    }
}

export function stopMicrophoneInputMeter(meterId) {
    const meter = meters.get(meterId);
    if (!meter) return;
    meters.delete(meterId);
    releaseGraph(meter);
}

export async function listMicrophoneInputDevices() {
    return inputDevices();
}
