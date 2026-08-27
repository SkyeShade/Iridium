import test from "node:test";
import assert from "node:assert/strict";
import { captureScreenTracks, fitScreenShareResolution, replacePublishedScreenTracks, screenShareBitrate,
    screenShareCaptureOptions } from "../../Iridium.Web/wwwroot/js/liveKitMedia.js";
import { clampPlaybackVolume, effectiveGain } from "../../Iridium.Web/wwwroot/js/voicePlayback.js";

test("display capture requests browser-provided audio without Safari resolution constraints", () => {
    const chromium = screenShareCaptureOptions({ suppressLocalAudioPlayback: true }, false);
    assert.deepEqual(chromium.audio, { suppressLocalAudioPlayback: false });
    assert.equal(chromium.systemAudio, "include");
    assert.deepEqual(chromium.video, {
        width:{ ideal:2560, max:2560 }, height:{ ideal:1440, max:1440 }, frameRate:{ ideal:60, max:60 }
    });
    assert.equal(chromium.windowAudio, "window");

    const safari = screenShareCaptureOptions({}, true);
    assert.equal(safari.audio, true);
    assert.equal(safari.video, true);
});

test("screen resolution fits proportionally within 1440p using even dimensions", () => {
    assert.deepEqual(fitScreenShareResolution(1280, 720), { width:1280, height:720, scale:1 });
    assert.deepEqual(fitScreenShareResolution(1920, 1080), { width:1920, height:1080, scale:1 });
    assert.deepEqual(fitScreenShareResolution(2560, 1440), { width:2560, height:1440, scale:1 });
    assert.deepEqual(fitScreenShareResolution(3840, 2160), { width:2560, height:1440, scale:2 / 3 });
    assert.deepEqual(fitScreenShareResolution(3440, 1440), { width:2560, height:1072, scale:2560 / 3440 });
    assert.deepEqual(fitScreenShareResolution(1080, 1920), { width:810, height:1440, scale:.75 });
});

test("screen bitrate retains established 1080p and 1440p targets without a 4K tier", () => {
    assert.equal(screenShareBitrate(1920, 1080, 60), 10_000_000);
    assert.equal(screenShareBitrate(2560, 1440, 60), 16_000_000);
    assert.equal(screenShareBitrate(3840, 2160, 60), 16_000_000);
});

test("screen volume supports zero through 300 percent and deafen overrides gain", () => {
    assert.equal(clampPlaybackVolume(-1, 0), 0);
    assert.equal(clampPlaybackVolume(35, 0), 35);
    assert.equal(clampPlaybackVolume(500, 0), 300);
    assert.equal(effectiveGain({ deafened:false, locallyMuted:false, volumePercent:200 }), 2);
    assert.equal(effectiveGain({ deafened:true, locallyMuted:false, volumePercent:200 }), 0);
    assert.equal(effectiveGain({ deafened:false, locallyMuted:true, volumePercent:200 }), 0);
});

function installCapture(audioTrack = null, initialVideoSettings = null) {
    let videoSettings = initialVideoSettings ?? { width:1920, height:1080, frameRate:60, displaySurface:"browser" };
    const appliedConstraints = [];
    const videoTrack = {
        kind:"video", label:"screen", enabled:true, muted:false, readyState:"live",
        getSettings:() => videoSettings,
        applyConstraints:async constraints => {
            appliedConstraints.push(constraints);
            videoSettings = { ...videoSettings, width:constraints.width.max, height:constraints.height.max };
        }
    };
    Object.defineProperty(globalThis, "navigator", { configurable:true, value:{ mediaDevices:{
        getSupportedConstraints:() => ({ suppressLocalAudioPlayback:true }),
        getDisplayMedia:async () => ({
            getVideoTracks:() => [videoTrack], getAudioTracks:() => audioTrack ? [audioTrack] : [],
            getTracks:() => audioTrack ? [videoTrack, audioTrack] : [videoTrack]
        })
    } } });
    class LocalTrack {
        constructor(mediaStreamTrack) { this.mediaStreamTrack = mediaStreamTrack; this.kind = mediaStreamTrack.kind; }
    }
    globalThis.LivekitClient = {
        Room:function(){}, LocalVideoTrack:LocalTrack, LocalAudioTrack:LocalTrack,
        Track:{ Source:{ ScreenShare:"screen_share", ScreenShareAudio:"screen_share_audio" } },
        getBrowser:() => ({ name:"Chrome" })
    };
    return { videoTrack, appliedConstraints };
}

test("capture keeps browser-provided screen audio and marks it as ScreenShareAudio", async () => {
    installCapture({ kind:"audio", label:"tab audio", enabled:true, muted:false, readyState:"live",
        getSettings:() => ({ sampleRate:48000, channelCount:2 }) });

    const tracks = await captureScreenTracks({ diagnostics:false });

    assert.equal(tracks.length, 2);
    assert.equal(tracks[0].source, "screen_share");
    assert.equal(tracks[1].kind, "audio");
    assert.equal(tracks[1].source, "screen_share_audio");
});

test("capture remains video-only when the browser returns no display audio", async () => {
    installCapture();

    const tracks = await captureScreenTracks({ diagnostics:false });

    assert.equal(tracks.length, 1);
    assert.equal(tracks[0].source, "screen_share");
});

test("capture applies a proportional 1440p publication cap to a 4K source", async () => {
    const capture = installCapture(null,
        { width:3840, height:2160, frameRate:60, displaySurface:"monitor" });

    const tracks = await captureScreenTracks({ diagnostics:false });

    assert.equal(tracks[0].mediaStreamTrack.getSettings().width, 2560);
    assert.equal(tracks[0].mediaStreamTrack.getSettings().height, 1440);
    assert.equal(capture.appliedConstraints.length, 1);
    assert.equal(capture.appliedConstraints[0].aspectRatio.ideal, 16 / 9);
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
