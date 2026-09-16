// Loaded only by the Development component with the explicit mediaAudit=1 query.
let restoreCapture;
let timer;
export async function install(reference, buildId) {
    const media = await import(`./liveKitMedia.js?build=${encodeURIComponent(buildId)}`);
    const samples = [];
    let sampling = false;
    timer = setInterval(async () => {
        if (sampling) return;
        sampling = true;
        try {
            samples.push({ call: await reference.invokeMethodAsync("Snapshot"), media: await media.collectMediaDiagnostics() });
            if (samples.length > 180) samples.shift();
        } catch (error) {
            // Disposing the page can race the in-flight sample; never log exception payloads.
            if (globalThis.iridiumMediaAudit) console.debug("Media audit sample unavailable", { name: error?.name });
        } finally { sampling = false; }
    }, 2000);
    globalThis.iridiumMediaAudit = {
        snapshot: () => reference.invokeMethodAsync("Snapshot"),
        execute: (operation, value = null) => reference.invokeMethodAsync("Execute", operation, value),
        diagnostics: () => media.collectMediaDiagnostics(),
        reconnect: () => media.reconnectDevelopmentMedia(),
        drain: () => samples.splice(0),
        synthetic({ width = 1920, height = 1080, fps = 60, audio = true } = {}) {
            restoreCapture?.();
            const original = navigator.mediaDevices.getDisplayMedia;
            const cleanups = new Set();
            navigator.mediaDevices.getDisplayMedia = async () => {
                const canvas = document.createElement("canvas");
                canvas.width = width; canvas.height = height;
                const ctx = canvas.getContext("2d");
                let frame = 0;
                const draw = () => {
                    ctx.fillStyle = "#102030"; ctx.fillRect(0, 0, width, height);
                    ctx.fillStyle = "#ffffff"; ctx.font = "48px monospace";
                    ctx.fillText(`${++frame} ${Date.now()}`, 50, 80);
                    ctx.fillStyle = "#00ff88"; ctx.fillRect((frame * 13) % width, height / 2, 120, 120);
                };
                draw();
                const interval = setInterval(draw, 1000 / fps);
                const stream = canvas.captureStream(fps);
                let context;
                if (audio) {
                    context = new AudioContext();
                    await context.resume();
                    const oscillator = context.createOscillator(), gain = context.createGain();
                    const destination = context.createMediaStreamDestination();
                    gain.gain.value = 0.03;
                    oscillator.connect(gain).connect(destination); oscillator.start();
                    stream.addTrack(destination.stream.getAudioTracks()[0]);
                }
                const cleanup = () => { clearInterval(interval); context?.close(); cleanups.delete(cleanup); };
                const video = stream.getVideoTracks()[0], stop = video.stop.bind(video);
                video.stop = () => { stop(); cleanup(); };
                video.addEventListener("ended", cleanup, { once: true });
                cleanups.add(cleanup);
                return stream;
            };
            restoreCapture = () => { navigator.mediaDevices.getDisplayMedia = original; for (const cleanup of cleanups) cleanup(); };
        }
    };
}

export function uninstall() {
    clearInterval(timer); restoreCapture?.(); restoreCapture = null;
    delete globalThis.iridiumMediaAudit;
}

export function viewerElementId() {
    return document.querySelector("#media-audit-viewer, video[id^='voice-stream-']")?.id ?? "media-audit-viewer";
}
