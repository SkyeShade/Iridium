export function effectiveGain(state) {
    return state.deafened || state.locallyMuted ? 0 : state.volumePercent / 100;
}

export function clampPlaybackVolume(volumePercent, minimumVolumePercent = 10) {
    return Math.min(300, Math.max(minimumVolumePercent, Number(volumePercent) || 0));
}

export async function createRemoteVoicePlayback(stream, audioContext, options = {}) {
    const minimumVolumePercent = options.minimumVolumePercent ?? 10;
    const state = {
        stream, audioContext, minimumVolumePercent,
        volumePercent: clampPlaybackVolume(options.volumePercent ?? 100, minimumVolumePercent),
        locallyMuted: options.locallyMuted === true, deafened: options.deafened === true,
        source: null, gain: null, limiter: null, element: null, mode: "none", disposed: false,
        diagnostic: options.diagnostic ?? (() => {}), playBlocked: false
    };
    const element = document.createElement("audio");
    element.autoplay = true; element.playsInline = true; element.hidden = true;
    element.srcObject = stream;
    element.volume = Math.min(1, state.volumePercent / 100);
    element.muted = state.deafened || state.locallyMuted;
    document.body.appendChild(element);
    state.element = element;
    state.diagnostic("RemoteAudioElementCreated", { elementMuted:element.muted, elementVolume:element.volume });
    state.diagnostic("RemoteStreamAttached", { remoteAudioTrackCount:stream.getAudioTracks().length });
    state.diagnostic("AudioPlayRequested", { elementMuted:element.muted, elementVolume:element.volume });
    try {
        await element.play();
        state.mode = "element";
        state.diagnostic("AudioPlaySucceeded", { elementMuted:element.muted, elementVolume:element.volume });
    } catch (error) {
        state.playBlocked = true;
        state.diagnostic("AudioPlayFailed", { name:error?.name, message:error?.message,
            elementMuted:element.muted, elementVolume:element.volume });
    }

    if (audioContext) {
        try {
            if (audioContext.state === "suspended") await audioContext.resume();
            state.diagnostic("AudioContextState", { audioContextState:audioContext.state });
            if (audioContext.state === "running") {
                state.source = audioContext.createMediaStreamSource(stream);
                state.gain = audioContext.createGain();
                state.limiter = audioContext.createDynamicsCompressor();
                state.limiter.threshold.value = -1;
                state.limiter.knee.value = 2;
                state.limiter.ratio.value = 8;
                state.limiter.attack.value = 0.002;
                state.limiter.release.value = 0.08;
                state.gain.gain.value = effectiveGain(state);
                element.pause(); element.muted = true;
                state.source.connect(state.gain).connect(state.limiter).connect(audioContext.destination);
                state.mode = "webaudio";
                state.playBlocked = false;
                state.diagnostic("GainValue", { gainValue:state.gain.gain.value, audioContextState:audioContext.state });
            }
        } catch (error) {
            state.diagnostic("AudioGraphFailed", { name:error?.name, message:error?.message,
                audioContextState:audioContext?.state });
        }
    }
    return state;
}

export async function resumeRemoteVoicePlayback(state) {
    if (!state || state.disposed) return false;
    try {
        if (state.audioContext?.state === "suspended") await state.audioContext.resume();
        if (state.mode === "none" && state.element) {
            state.element.muted = state.deafened || state.locallyMuted;
            await state.element.play();
            state.mode = "element";
        }
        state.playBlocked = false;
        return true;
    } catch (error) {
        state.playBlocked = true;
        state.diagnostic("AudioPlayFailed", { name:error?.name, message:error?.message });
        return false;
    }
}

export function updateRemoteVoicePlayback(state, options) {
    if (!state || state.disposed) return;
    if (options.volumePercent !== undefined)
        state.volumePercent = clampPlaybackVolume(options.volumePercent, state.minimumVolumePercent);
    if (options.locallyMuted !== undefined) state.locallyMuted = options.locallyMuted;
    if (options.deafened !== undefined) state.deafened = options.deafened;
    const gain = effectiveGain(state);
    if (state.gain) state.gain.gain.value = gain;
    if (state.element) {
        state.element.volume = Math.min(1, state.volumePercent / 100);
        state.element.muted = state.mode === "webaudio" || state.deafened || state.locallyMuted;
    }
    state.diagnostic("GainValue", { gainValue:gain, elementMuted:state.element?.muted,
        elementVolume:state.element?.volume, audioContextState:state.audioContext?.state });
}

export function destroyRemoteVoicePlayback(state) {
    if (!state || state.disposed) return;
    state.disposed = true;
    state.source?.disconnect(); state.gain?.disconnect(); state.limiter?.disconnect();
    if (state.element) { state.element.pause(); state.element.srcObject = null; state.element.remove(); }
}
