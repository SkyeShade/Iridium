// In the Iridium desktop app, a screen share started with "Share sound" gets its audio from the app's native
// per-app capture (only the shared program, or everything except Iridium for a whole screen) instead of the
// browser's system loopback. This turns that stream into a MediaStreamTrack the share can publish.

const workletUrl = new URL("pcmStreamWorklet.js", import.meta.url).href;

// Returns an audio track for the capture the desktop app set up for the last getDisplayMedia call, or null
// outside the desktop app or when no sound was requested.
export async function takeDesktopAppAudioTrack() {
    const capture = await globalThis.iridiumDesktop?.takeAppAudio?.();
    if (!capture) return null;
    let context;
    try {
        context = new AudioContext({ sampleRate: capture.sampleRate, latencyHint: "interactive" });
        await context.audioWorklet.addModule(workletUrl);
        const node = new AudioWorkletNode(context, "iridium-pcm-stream", {
            numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [capture.channels]
        });
        const destination = context.createMediaStreamDestination();
        node.connect(destination);
        if (context.state !== "running") await context.resume().catch(() => {});
        capture.onData(chunk => node.port.postMessage(new Float32Array(chunk)));
        const track = destination.stream.getAudioTracks()[0];
        const stopTrack = track.stop.bind(track);
        let stopped = false;
        // Stopping the share stops this track; that must also end the native capture.
        track.stop = () => {
            if (stopped) return;
            stopped = true;
            stopTrack();
            capture.stop();
            node.disconnect();
            void context.close().catch(() => {});
        };
        capture.onEnded(() => track.stop());
        return track;
    } catch (error) {
        capture.stop();
        await context?.close().catch(() => {});
        throw error;
    }
}
