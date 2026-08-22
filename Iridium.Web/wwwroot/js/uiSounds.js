let context;
let soundDesign;
let incomingTimer;
let incomingRequested = false;
let unlocked = false;

async function ensureContext() {
    const AudioContextType = window.AudioContext ?? window.webkitAudioContext;
    if (!AudioContextType) return null;
    context ??= new AudioContextType();
    if (context.state === "suspended") await context.resume();
    unlocked = context.state === "running";
    return context;
}

async function playSequence(notes, categoryVolume) {
    const audio = await ensureContext().catch(() => null);
    if (!audio || !notes?.length) return false;
    let at = audio.currentTime + 0.015;
    for (const [frequency, duration] of notes) {
        const oscillator = audio.createOscillator();
        const gain = audio.createGain();
        oscillator.type = "sine";
        oscillator.frequency.value = frequency;
        gain.gain.setValueAtTime(0.0001, at);
        const volume = Math.min(1, Math.max(0, soundDesign.masterVolume * categoryVolume));
        gain.gain.exponentialRampToValueAtTime(volume, at + 0.018);
        gain.gain.exponentialRampToValueAtTime(0.0001, at + duration);
        oscillator.connect(gain).connect(audio.destination);
        oscillator.start(at);
        oscillator.stop(at + duration + 0.02);
        at += duration + 0.035;
    }
    return true;
}

async function ringOnce() {
    if (!incomingRequested) return;
    await playSequence(soundDesign.incoming, soundDesign.incomingCallVolume).catch(() => false);
}

function beginRingTimer() {
    if (incomingTimer !== undefined || !incomingRequested || !unlocked) return;
    ringOnce();
    incomingTimer = window.setInterval(ringOnce, 2100);
}

function unlock() {
    ensureContext().then(() => beginRingTimer()).catch(() => {});
}

export async function initialize() {
    soundDesign ??= await fetch("./sounds/iridium-voice.json").then(response => response.json());
    window.addEventListener("pointerdown", unlock, { passive: true });
    window.addEventListener("keydown", unlock, { passive: true });
}

export async function playIncomingCallLoop() {
    incomingRequested = true;
    await ensureContext().catch(() => null);
    beginRingTimer();
}

export function stopIncomingCallLoop() {
    incomingRequested = false;
    if (incomingTimer !== undefined) window.clearInterval(incomingTimer);
    incomingTimer = undefined;
}

export async function playVoiceJoin() {
    await playSequence(soundDesign?.join, soundDesign?.voiceJoinVolume ?? 0.22).catch(() => false);
}

export async function playVoiceLeave() {
    await playSequence(soundDesign?.leave, soundDesign?.voiceLeaveVolume ?? 0.2).catch(() => false);
}

export function dispose() {
    stopIncomingCallLoop();
    window.removeEventListener("pointerdown", unlock);
    window.removeEventListener("keydown", unlock);
    context?.close().catch(() => {});
    context = undefined;
    unlocked = false;
}
