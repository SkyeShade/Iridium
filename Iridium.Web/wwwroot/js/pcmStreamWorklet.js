// Plays interleaved stereo float32 chunks posted to its port (the desktop app's per-app audio capture) as a
// continuous output, with a small jitter buffer. Silence fills any gap, and the buffer is trimmed when it
// grows, so shared sound never drifts behind the video.

const START_FRAMES = 2880;   // 60 ms buffered before playback starts
const MAX_FRAMES = 14400;    // 300 ms: beyond this, drop old audio
const TRIM_TO_FRAMES = 4800; // back to 100 ms

class IridiumPcmStream extends AudioWorkletProcessor {
    constructor() {
        super();
        this.capacity = 48000 * 2;
        this.buffer = new Float32Array(this.capacity * 2);
        this.read = 0;
        this.frames = 0;
        this.playing = false;
        this.port.onmessage = event => this.push(event.data);
    }

    push(samples) {
        const frames = samples.length >> 1;
        for (let i = 0; i < frames; i++) {
            const at = ((this.read + this.frames) % this.capacity) * 2;
            this.buffer[at] = samples[i * 2];
            this.buffer[at + 1] = samples[i * 2 + 1];
            if (this.frames < this.capacity) this.frames++;
            else this.read = (this.read + 1) % this.capacity;
        }
        if (this.frames > MAX_FRAMES) {
            this.read = (this.read + this.frames - TRIM_TO_FRAMES) % this.capacity;
            this.frames = TRIM_TO_FRAMES;
        }
    }

    process(_inputs, outputs) {
        const [left, right] = outputs[0];
        if (!this.playing && this.frames >= START_FRAMES) this.playing = true;
        for (let i = 0; i < left.length; i++) {
            if (!this.playing || this.frames === 0) {
                left[i] = 0;
                if (right) right[i] = 0;
                continue;
            }
            const at = this.read * 2;
            left[i] = this.buffer[at];
            if (right) right[i] = this.buffer[at + 1];
            this.read = (this.read + 1) % this.capacity;
            this.frames--;
        }
        // Rebuffer after an underrun instead of crackling sample by sample.
        if (this.frames === 0) this.playing = false;
        return true;
    }
}

registerProcessor("iridium-pcm-stream", IridiumPcmStream);
