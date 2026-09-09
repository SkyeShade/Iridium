import test from "node:test";
import assert from "node:assert/strict";

const tracks = [];
const contexts = [];
let nextFrame = 1;
let captureOverride = null;
globalThis.requestAnimationFrame = () => nextFrame++;
globalThis.cancelAnimationFrame = () => {};

class FakeAudioContext {
    constructor() { this.state = "running"; this.closed = false; contexts.push(this); }
    createMediaStreamSource() { return { connect() {}, disconnect() {} }; }
    createAnalyser() {
        return { fftSize: 0, smoothingTimeConstant: 0, connect() {}, disconnect() {},
            getFloatTimeDomainData(values) { values.fill(0.04); } };
    }
    async close() { this.closed = true; }
}
globalThis.window = { AudioContext: FakeAudioContext };
Object.defineProperty(globalThis, "navigator", { configurable: true, value: { mediaDevices: {
    async getUserMedia(constraints) {
        if (captureOverride) return captureOverride(constraints);
        const track = { constraints, stopped: false, stop() { this.stopped = true; } };
        tracks.push(track);
        return { getTracks: () => [track] };
    },
    async enumerateDevices() {
        return [{ kind: "audioinput", deviceId: "default", label: "Default microphone" },
            { kind: "audioinput", deviceId: "second", label: "Second microphone" }];
    }
} } });

const meter = await import("../../Iridium.Web/wwwroot/js/microphoneInputMeter.js");
const callback = { invokeMethodAsync: async () => {} };

test("meter rebind acquires replacement before releasing old source and cleanup stops resources", async () => {
    const started = await meter.startMicrophoneInputMeter("meter-rebind", callback, null);
    assert.equal(started.status, "ready");
    assert.equal(started.devices.length, 2);
    const oldTrack = tracks.at(-1);

    const rebound = await meter.rebindMicrophoneInputMeter(started.meterId, "second");
    assert.equal(rebound.status, "ready");
    assert.equal(oldTrack.stopped, true);
    const newTrack = tracks.at(-1);
    assert.deepEqual(newTrack.constraints.audio.deviceId, { exact: "second" });
    assert.equal(newTrack.stopped, false);

    meter.stopMicrophoneInputMeter(started.meterId);
    assert.equal(newTrack.stopped, true);
    assert.equal(contexts.every(context => context.closed), true);
});

test("stopping a pending meter prevents late acquisition from being installed", async () => {
    let completeCapture;
    const track = { stopped: false, stop() { this.stopped = true; } };
    captureOverride = () => new Promise(resolve => completeCapture = () => resolve({ getTracks: () => [track] }));
    const pending = meter.startMicrophoneInputMeter("meter-pending", callback, null);
    await Promise.resolve();
    meter.stopMicrophoneInputMeter("meter-pending");
    completeCapture();
    const result = await pending;
    captureOverride = null;

    assert.equal(result.status, "cancelled");
    assert.equal(result.meterId, null);
    assert.equal(track.stopped, true);
});
