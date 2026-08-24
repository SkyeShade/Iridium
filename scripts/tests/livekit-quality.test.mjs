import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const source = readFileSync(new URL("../../Iridium.Web/wwwroot/js/liveKitMedia.js", import.meta.url), "utf8");
const start = source.indexOf("const sessions = new Map();");
const end = source.indexOf("function statsSummary(");
assert.ok(start >= 0 && end > start, "Unable to locate the deployed LiveKit quality policy.");

const context = {
    globalThis: {
        LivekitClient: {
            Room: class {},
            VideoPreset: class {
                constructor(width, height, maxBitrate, maxFramerate, priority) {
                    this.width = width;
                    this.height = height;
                    this.encoding = { maxBitrate, maxFramerate, priority };
                }
            }
        }
    }
};
vm.createContext(context);
vm.runInContext(`${source.slice(start, end).replaceAll("export function", "function")}
globalThis.calculate = screenShareBitrate;
globalThis.publishOptions = screenSharePublishOptions;
globalThis.microphoneProfile = microphoneProfile;`, context);

test("screen-share bitrate follows the high-quality resolution/FPS policy", () => {
    const bitrate = context.globalThis.calculate;
    assert.equal(bitrate(1920, 1080, 30), 6_000_000);
    assert.equal(bitrate(1920, 1080, 60), 10_000_000);
    assert.equal(bitrate(2560, 1440, 30), 10_000_000);
    assert.equal(bitrate(2560, 1440, 60), 16_000_000);
    assert.equal(bitrate(3840, 2160, 30), 20_000_000);
    assert.equal(bitrate(3840, 2160, 60), 30_000_000);
    assert.equal(bitrate(640, 360, 30), 1_000_000);
});

test("screen-share publish options carry the calculated full-quality encoding", () => {
    const profile = context.globalThis.publishOptions({
        source: "screen_share",
        mediaStreamTrack: { getSettings: () => ({ width: 2560, height: 1440, frameRate: 60 }) }
    }, "screen-stream");

    assert.equal(profile.maxBitrate, 16_000_000);
    assert.equal(profile.options.screenShareEncoding.maxBitrate, 16_000_000);
    assert.equal(profile.options.screenShareEncoding.maxFramerate, 60);
    assert.equal(profile.options.screenShareEncoding.priority, "high");
    assert.equal(profile.options.videoCodec, "vp8");
    assert.equal(profile.options.simulcast, true);
    assert.equal(profile.options.degradationPreference, "maintain-resolution");
    assert.equal(profile.options.screenShareSimulcastLayers.length, 1);
});

test("microphone profile clamps bitrate and passes it to LiveKit publish options", () => {
    const profile = context.globalThis.microphoneProfile;
    for (const bitrate of [64_000, 96_000, 128_000]) {
        const value = profile(bitrate);
        assert.equal(value.bitrate, bitrate);
        assert.equal(value.publish.audioPreset.maxBitrate, bitrate);
        assert.equal(value.capture.channelCount, 1);
        assert.equal(value.publish.forceStereo, false);
        assert.equal(value.publish.dtx, true);
        assert.equal(value.publish.red, true);
    }
    assert.equal(profile(1).bitrate, 64_000);
    assert.equal(profile(999_999).bitrate, 128_000);
});
