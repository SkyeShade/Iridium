export const defaultManualInputSensitivityThreshold = 0.5;

export function clampInputLevel(value) {
    return Math.max(0, Math.min(1, Number(value) || 0));
}

// Both the settings meter and voice-session VAD use this -60 dBFS .. 0 dBFS scale.
// It gives quiet microphone input enough visual resolution without changing captured audio.
export function normalizedInputLevel(rms) {
    const decibels = 20 * Math.log10(Math.max(0.000001, Number(rms) || 0));
    return clampInputLevel((decibels + 60) / 60);
}

export function inputSensitivityConfiguration(value) {
    return {
        automatic: value?.autoInputSensitivity !== false,
        manualThreshold: clampInputLevel(
            value?.manualInputSensitivityThreshold ?? defaultManualInputSensitivityThreshold),
        inputDeviceId: value?.inputDeviceId || null
    };
}

export function createVoiceActivityGate(configuration) {
    return {
        configuration: inputSensitivityConfiguration(configuration),
        noiseFloor: 0.2,
        speaking: false,
        aboveThresholdFrames: 0,
        lastVoiceAt: 0
    };
}

export function updateVoiceActivityConfiguration(gate, configuration) {
    gate.configuration = inputSensitivityConfiguration(configuration);
    gate.aboveThresholdFrames = 0;
}

export function voiceActivityThresholds(gate, level) {
    const configuration = gate.configuration;
    if (!configuration.automatic) {
        const start = configuration.manualThreshold;
        return { start, stop: Math.max(0, start - 0.08) };
    }
    if (!gate.speaking && level < Math.max(0.58, gate.noiseFloor + 0.2))
        gate.noiseFloor = gate.noiseFloor * 0.97 + level * 0.03;
    const start = Math.max(0.34, Math.min(0.76, gate.noiseFloor + 0.16));
    return { start, stop: Math.max(0.24, start - 0.1) };
}

// Push-to-talk is intentionally authoritative when a caller supplies it: sensitivity must
// never prevent a deliberately-open PTT path. Current Iridium sessions use voice activation.
export function evaluateVoiceActivity(gate, level, timestamp, options = {}) {
    level = clampInputLevel(level);
    const muted = options.muted === true;
    if (options.pushToTalkActive === true) {
        gate.aboveThresholdFrames = 0;
        gate.speaking = !muted;
        if (gate.speaking) gate.lastVoiceAt = timestamp;
        return gate.speaking;
    }
    const thresholds = voiceActivityThresholds(gate, level);
    if (!muted && level >= thresholds.start) {
        gate.aboveThresholdFrames++;
        gate.lastVoiceAt = timestamp;
        if (gate.aboveThresholdFrames >= 2) gate.speaking = true;
    } else {
        gate.aboveThresholdFrames = 0;
        if (gate.speaking && (muted || level < thresholds.stop) && timestamp - gate.lastVoiceAt >= 420)
            gate.speaking = false;
    }
    return gate.speaking;
}
