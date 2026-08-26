import test from "node:test";
import assert from "node:assert/strict";
import { replacePublishedScreenTracks, screenShareCaptureOptions } from "../../Iridium.Web/wwwroot/js/liveKitMedia.js";
import { clampPlaybackVolume, effectiveGain } from "../../Iridium.Web/wwwroot/js/voicePlayback.js";

test("display capture requests browser-provided audio without Safari resolution constraints", () => {
    const chromium = screenShareCaptureOptions({ suppressLocalAudioPlayback: true }, false);
    assert.deepEqual(chromium.audio, { suppressLocalAudioPlayback: false });
    assert.equal(chromium.systemAudio, "include");
    assert.deepEqual(chromium.video, {
        width:{ ideal:3840 }, height:{ ideal:2160 }, frameRate:{ ideal:60, max:60 }
    });
    assert.equal(chromium.windowAudio, "window");

    const safari = screenShareCaptureOptions({}, true);
    assert.equal(safari.audio, true);
    assert.equal(safari.video, true);
});

test("screen volume supports zero through 300 percent and deafen overrides gain", () => {
    assert.equal(clampPlaybackVolume(-1, 0), 0);
    assert.equal(clampPlaybackVolume(35, 0), 35);
    assert.equal(clampPlaybackVolume(500, 0), 300);
    assert.equal(effectiveGain({ deafened:false, locallyMuted:false, volumePercent:200 }), 2);
    assert.equal(effectiveGain({ deafened:true, locallyMuted:false, volumePercent:200 }), 0);
    assert.equal(effectiveGain({ deafened:false, locallyMuted:true, volumePercent:200 }), 0);
});

function fakeTrack(kind, stopped) {
    return {
        kind, source:kind === "video" ? "screen_share" : "screen_share_audio",
        mediaStreamTrack:{ contentHint:"", getSettings:() => ({ width:1280, height:720, frameRate:30 }) },
        stop:() => stopped.push(kind)
    };
}

test("successful source replacement publishes under the existing media identity", async () => {
    globalThis.LivekitClient = { Room:function(){}, VideoPreset:class {}, VideoQuality:{ HIGH:2 } };
    const stopped = [], calls = [], previous = [fakeTrack("video", stopped)];
    const replacement = [fakeTrack("video", stopped), fakeTrack("audio", stopped)];
    const session = { screenTracks:previous, screenMediaStreamId:"stable-media", diagnostics:false,
        room:{ localParticipant:{
            unpublishTrack:async track => calls.push(["unpublish", track]),
            publishTrack:async (track, options) => calls.push(["publish", track, options])
        } } };

    const returned = await replacePublishedScreenTracks(session, replacement);

    assert.equal(returned[0], previous[0]);
    assert.equal(calls[1][2].name, "stable-media");
    assert.equal(calls[2][2].source, "screen_share_audio");
    assert.deepEqual(stopped, []);
});

test("failed source replacement stops only replacement capture and republishes old track", async () => {
    globalThis.LivekitClient = { Room:function(){}, VideoPreset:class {}, VideoQuality:{ HIGH:2 } };
    const oldStopped = [], newStopped = [], previous = [fakeTrack("video", oldStopped)], replacement = [fakeTrack("video", newStopped)];
    let publishCount = 0;
    const session = { screenTracks:previous, screenMediaStreamId:"stable-media", diagnostics:false,
        room:{ localParticipant:{ unpublishTrack:async () => {}, publishTrack:async () => {
            if (++publishCount === 1) throw new Error("publish failed");
        } } } };

    await assert.rejects(() => replacePublishedScreenTracks(session, replacement), /publish failed/);

    assert.deepEqual(oldStopped, []);
    assert.deepEqual(newStopped, ["video"]);
    assert.equal(publishCount, 2);
});
