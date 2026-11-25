import test from "node:test";
import assert from "node:assert/strict";

const captures = [];
Object.defineProperty(globalThis, "navigator", { configurable: true, value: { mediaDevices: {
    async getUserMedia(constraints) {
        const track = { constraints, enabled: true, stopped: false, stop() { this.stopped = true; } };
        captures.push(track);
        return { getTracks: () => [track], getAudioTracks: () => [track] };
    }
} } });

const noise = await import("../../Iridium.Web/wwwroot/js/noiseSuppression.js");
const { inputSensitivityConfiguration } = await import("../../Iridium.Web/wwwroot/js/inputSensitivity.js");

test("mode defaults to enhanced and accepts the serialized C# enum names", () => {
    assert.equal(noise.noiseSuppressionMode(undefined), "enhanced");
    assert.equal(noise.noiseSuppressionMode("Standard"), "standard");
    assert.equal(noise.noiseSuppressionMode("Off"), "off");
    assert.equal(noise.noiseSuppressionMode("krisp"), "enhanced");
    assert.equal(inputSensitivityConfiguration({}).noiseSuppression, "enhanced");
    assert.equal(inputSensitivityConfiguration({ noiseSuppression: "Off" }).noiseSuppression, "off");
});

test("browser suppression is off only while RNNoise runs or when suppression is off", () => {
    assert.equal(noise.microphoneCaptureConstraints(null, "enhanced", true).noiseSuppression, false);
    assert.equal(noise.microphoneCaptureConstraints(null, "enhanced", false).noiseSuppression, true);
    assert.equal(noise.microphoneCaptureConstraints(null, "standard").noiseSuppression, true);
    assert.equal(noise.microphoneCaptureConstraints(null, "off").noiseSuppression, false);
    const constraints = noise.microphoneCaptureConstraints("mic-2", "off");
    assert.deepEqual(constraints.deviceId, { exact: "mic-2" });
    assert.equal(constraints.echoCancellation, true);
    assert.equal(constraints.autoGainControl, true);
    assert.equal(constraints.channelCount, 1);
});

test("enhanced capture falls back to the browser suppressor without AudioWorklet", async () => {
    assert.equal(noise.enhancedNoiseSuppressionSupported(), false);
    const capture = await noise.captureVoiceMicrophone("mic-1", "Enhanced");
    assert.equal(capture.noiseSuppression, "standard");
    assert.equal(capture.mode, "enhanced");
    const track = captures.at(-1);
    assert.equal(track.constraints.audio.noiseSuppression, true);
    capture.release();
    assert.equal(track.stopped, true);
});

test("replacing the microphone swaps matching senders and skips unchanged settings", async () => {
    const current = await noise.captureVoiceMicrophone(null, "standard");
    const oldTrack = current.stream.getAudioTracks()[0];
    const replaced = [];
    const sender = { track: oldTrack, async replaceTrack(track) { replaced.push(track); this.track = track; } };
    const other = { track: { kind: "video" }, async replaceTrack() { throw new Error("wrong sender"); } };
    const peer = { getSenders: () => [sender, other] };

    assert.equal(await noise.replaceVoiceMicrophone(current, null, "Standard", [peer], true), null);

    const next = await noise.replaceVoiceMicrophone(current, null, "off", [peer], false);
    const newTrack = next.stream.getAudioTracks()[0];
    assert.deepEqual(replaced, [newTrack]);
    assert.equal(newTrack.enabled, false);
    assert.equal(newTrack.constraints.audio.noiseSuppression, false);
    assert.equal(next.noiseSuppression, "off");
});

test("LiveKit processor leaves the raw track with browser suppression when RNNoise cannot start", async () => {
    const applied = [];
    const track = {
        getConstraints: () => ({ noiseSuppression: false, echoCancellation: true }),
        async applyConstraints(constraints) { applied.push(constraints); }
    };
    const events = [];
    const processor = noise.createLiveKitNoiseProcessor(name => events.push(name));
    await processor.init({ track });
    assert.equal(processor.processedTrack, undefined);
    assert.equal(processor.active, false);
    assert.deepEqual(applied, [{ noiseSuppression: true, echoCancellation: true }]);
    assert.deepEqual(events, ["NoiseSuppressionUnavailable"]);
    await processor.restart({ track });
    await processor.destroy();
});
