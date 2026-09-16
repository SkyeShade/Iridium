import test from "node:test";
import assert from "node:assert/strict";
import { attachStreamViewerForSession, captureScreenTracks, detachStreamViewerForSession,
    fitScreenShareResolution, publicationStreamIdentity, publishScreenTracks, reconcileScreenWatch,
    refreshViewers, replacePublishedScreenTracks, screenShareBitrate, screenShareCaptureOptions,
    setStreamSubscriptionForSession, wireScreenEnded, wireRoom, mediaStatsInterval
} from "../../Iridium.Web/wwwroot/js/liveKitMedia.js";
import { clampPlaybackVolume, effectiveGain, createRemoteVoicePlayback } from "../../Iridium.Web/wwwroot/js/voicePlayback.js";

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

test("screen video and audio publish separately with one LiveKit stream identity", async () => {
    globalThis.LivekitClient = { Room:function(){}, VideoPreset:class {},
        Track:{ Source:{ ScreenShare:"screen_share", ScreenShareAudio:"screen_share_audio" } } };
    const calls = [];
    const session = { diagnostics:false, room:{ localParticipant:{ publishTrack:async (track, options) => {
        calls.push({ track, options });
        return { kind:track.kind, source:track.source, trackName:`accepted-${track.kind}`,
            trackInfo:{ stream:options.stream } };
    } } } };
    const tracks = [fakeTrack("video", []), fakeTrack("audio", [])];

    const publications = await publishScreenTracks(session, tracks, "shared-screen-stream");

    assert.equal(publications.length, 2);
    assert.deepEqual(calls.map(value => value.options.source), ["screen_share", "screen_share_audio"]);
    assert.deepEqual(calls.map(value => value.options.stream), ["shared-screen-stream", "shared-screen-stream"]);
    assert.deepEqual(publications.map(publicationStreamIdentity), ["shared-screen-stream", "shared-screen-stream"]);
});

test("LiveKit stream metadata wins and trackName is the compatibility fallback", () => {
    assert.equal(publicationStreamIdentity({ trackName:"screen-video", trackInfo:{ stream:"iridium-screen-1" } }),
        "iridium-screen-1");
    assert.equal(publicationStreamIdentity({ trackName:"screen-audio", track:{ mediaStream:{ id:"iridium-screen-1" } } }),
        "screen-audio");
});

test("receiver-created media stream IDs never replace authoritative or compatible publication identity", () => {
    assert.equal(publicationStreamIdentity({ trackName:"shared-screen",
        track:{ mediaStream:{ id:"receiver-only-audio-stream" } } }), "shared-screen");
});

function installPlaybackDom() {
    const elements = new Map(), audioElements = [];
    globalThis.MediaStream = class {
        constructor(tracks = []) { this.tracks = tracks; }
        getAudioTracks() { return this.tracks.filter(track => track.kind === "audio"); }
        getVideoTracks() { return this.tracks.filter(track => track.kind === "video"); }
    };
    const makeElement = kind => ({ kind, dataset:{}, muted:false, srcObject:null,
        play:async () => {}, pause:() => {}, remove:() => {}, removeAttribute:() => {},
        addEventListener:() => {}, removeEventListener:() => {} });
    globalThis.document = {
        getElementById:id => elements.get(id) ?? null,
        createElement:kind => { const element = makeElement(kind); if (kind === "audio") audioElements.push(element); return element; },
        body:{ appendChild:() => {} }
    };
    elements.set("viewer", makeElement("video"));
    return { elements, audioElements };
}

function screenPublication(kind, stream, subscribed = false) {
    const mediaTrack = { kind, id:`${kind}-track`, readyState:"live", getSettings:() => ({}) };
    const publication = {
        kind, source:kind === "video" ? "screen_share" : "screen_share_audio",
        trackName:`compatible-${kind}`, trackInfo:{ stream }, trackSid:`sid-${kind}`,
        isSubscribed:subscribed, track:subscribed ? { mediaStreamTrack:mediaTrack } : null,
        subscriptionRequests:[], setSubscribed(value) { this.subscriptionRequests.push(value); this.isSubscribed = value; }
    };
    publication.subscribedTrack = { mediaStreamTrack:mediaTrack };
    return publication;
}

function screenSession(publications, participantIdentity = "publisher") {
    const participant = { identity:participantIdentity,
        trackPublications:new Map(publications.map((publication, index) => [index, publication])) };
    return { kind:"community", diagnostics:false, callback:{ invokeMethodAsync:async () => {} },
        room:{ localParticipant:{ identity:"viewer", trackPublications:new Map() },
            remoteParticipants:new Map([[participantIdentity, participant]]) },
        viewers:new Map(), watched:new Map(), audioContext:null, deafened:false };
}

for (const caller of ["A", "B"]) {
    for (const owner of ["A", "B"]) {
        test(`CALLER=${caller} owner=${owner}: legacy DM metadata discovers remote publications without self-watch`, async () => {
            installCapture();
            installPlaybackDom();
            const publications = [screenPublication("video", `stream-${owner}`), screenPublication("audio", `stream-${owner}`)];
            const session = screenSession(publications, owner);
            session.kind = "call";
            session.room.localParticipant.identity = owner === "A" ? "B" : "A";
            await setStreamSubscriptionForSession(session, "id", `stream-${owner}`, null, true);
            assert.deepEqual(publications.map(p => p.subscriptionRequests), [[true], [true]]);
        });
    }
}

test("reconciliation preserves the video MediaStream when only screen audio changes", async () => {
    installCapture();
    const dom = installPlaybackDom();
    const video = screenPublication("video", "stable", true);
    const session = screenSession([video]);
    await setStreamSubscriptionForSession(session, "id", "stable", "publisher", true);
    attachStreamViewerForSession(session, "stable", "viewer", false);
    await refreshViewers(session, "stable");
    const before = dom.elements.get("viewer").srcObject;
    session.room.remoteParticipants.get("publisher").trackPublications.set("audio", screenPublication("audio", "stable", true));
    await refreshViewers(session, "stable");
    assert.equal(dom.elements.get("viewer").srcObject, before);
});

test("Stop Watching detaches immediately even while SDK retains unsubscribing tracks", async () => {
    installCapture();
    const dom = installPlaybackDom();
    const session = screenSession([screenPublication("video", "stop", true), screenPublication("audio", "stop", true)]);
    await setStreamSubscriptionForSession(session, "id", "stop", "publisher", true);
    attachStreamViewerForSession(session, "stop", "viewer", false);
    await refreshViewers(session, "stop");
    await new Promise(resolve => setTimeout(resolve, 0));
    const playback = session.viewers.get("viewer").audioPlayback;
    await setStreamSubscriptionForSession(session, "id", "stop", "publisher", false);
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(dom.elements.get("viewer").srcObject, null);
    assert.equal(playback.disposed, true);
});

test("pending first video frame does not block screen audio or stop-watch cleanup", async () => {
    installCapture();
    const dom = installPlaybackDom();
    dom.elements.get("viewer").play = () => new Promise(() => {});
    const session = screenSession([screenPublication("video", "pending", true), screenPublication("audio", "pending", true)]);
    await setStreamSubscriptionForSession(session, "id", "pending", "publisher", true);
    attachStreamViewerForSession(session, "pending", "viewer", false);
    await refreshViewers(session, "pending");
    await new Promise(resolve => setTimeout(resolve, 0));
    const audio = session.viewers.get("viewer").audioPlayback;
    assert.ok(audio);
    await setStreamSubscriptionForSession(session, "id", "pending", "publisher", false);
    assert.equal(audio.disposed, true);
    assert.equal(dom.elements.get("viewer").srcObject, null);
});

test("cancelled audio attachment disposes a media element even when autoplay never settles", async () => {
    installPlaybackDom();
    const original = document.createElement;
    let element;
    document.createElement = kind => {
        element = original(kind);
        element.play = () => new Promise(() => {});
        return element;
    };
    const controller = new AbortController();
    const pending = createRemoteVoicePlayback(new MediaStream([{ kind:"audio" }]), null, { signal:controller.signal });
    controller.abort();
    const playback = await pending;
    assert.equal(playback.disposed, true);
    assert.equal(element.srcObject, null);
});

test("Watch enumerates existing remote video and audio and requests both subscriptions despite DTO lag", async () => {
    installPlaybackDom();
    const video = screenPublication("video", "existing-stream"), audio = screenPublication("audio", "existing-stream");
    const session = screenSession([video, audio]);

    await setStreamSubscriptionForSession(session, "iridium-id", "existing-stream", "publisher", true);

    assert.deepEqual(video.subscriptionRequests, [true]);
    assert.deepEqual(audio.subscriptionRequests, [true]);
    assert.equal(session.watched.get("existing-stream").audioAvailable, true);
});

test("delayed TrackSubscribed reconciliation attaches screen audio", async () => {
    const dom = installPlaybackDom();
    const video = screenPublication("video", "delayed", true), audio = screenPublication("audio", "delayed");
    const session = screenSession([video, audio]);
    await setStreamSubscriptionForSession(session, "stream-id", "delayed", "publisher", true);
    attachStreamViewerForSession(session, "delayed", "viewer", false, 100);
    await refreshViewers(session, "delayed");
    assert.equal(session.viewers.get("viewer").audioPlayback, null);

    audio.track = audio.subscribedTrack;
    await refreshViewers(session, "delayed");

    assert.equal(session.viewers.get("viewer").audioTrackId, "audio-track");
    assert.equal(dom.audioElements.length, 1);
});

test("already-subscribed audio attaches immediately and duplicate reconciliation does not double attach", async () => {
    const dom = installPlaybackDom();
    const video = screenPublication("video", "ready", true), audio = screenPublication("audio", "ready", true);
    const session = screenSession([video, audio]);
    await setStreamSubscriptionForSession(session, "stream-id", "ready", "publisher", true);
    attachStreamViewerForSession(session, "ready", "viewer", false, 100);
    await refreshViewers(session, "ready");
    await refreshViewers(session, "ready");
    await new Promise(resolve => setTimeout(resolve, 0));

    assert.equal(session.viewers.get("viewer").audioTrackId, "audio-track");
    assert.equal(dom.audioElements.length, 1);
});

test("StopWatch then Watch creates a new playback and never reuses the disposed graph", async () => {
    const dom = installPlaybackDom();
    const publications = [screenPublication("video", "reenter", true), screenPublication("audio", "reenter", true)];
    const session = screenSession(publications);
    await setStreamSubscriptionForSession(session, "stream-id", "reenter", "publisher", true);
    attachStreamViewerForSession(session, "reenter", "viewer", false, 100);
    await refreshViewers(session, "reenter");
    await new Promise(resolve => setTimeout(resolve, 0));
    const first = session.viewers.get("viewer").audioPlayback;

    detachStreamViewerForSession(session, "viewer");
    await setStreamSubscriptionForSession(session, "stream-id", "reenter", "publisher", false);
    await setStreamSubscriptionForSession(session, "stream-id", "reenter", "publisher", true);
    attachStreamViewerForSession(session, "reenter", "viewer", false, 100);
    await refreshViewers(session, "reenter");
    await new Promise(resolve => setTimeout(resolve, 0));
    const second = session.viewers.get("viewer").audioPlayback;

    assert.equal(first.disposed, true);
    assert.notEqual(second, first);
    assert.equal(second.disposed, false);
    assert.equal(dom.audioElements.length, 2);
});

test("remote subscription is scoped to the authoritative participant and is independent of self-watch", () => {
    installPlaybackDom();
    const remoteAudio = screenPublication("audio", "same-name"), localAudio = screenPublication("audio", "same-name", true);
    const session = screenSession([remoteAudio], "remote-publisher");
    session.room.localParticipant.trackPublications.set("local", localAudio);
    const watch = { iridiumStreamId:"id", mediaStreamId:"same-name",
        participantIdentity:"remote-publisher", audioAvailable:null };
    session.watched.set("same-name", watch);

    const discovered = reconcileScreenWatch(session, watch);

    assert.deepEqual(discovered, [remoteAudio]);
    assert.deepEqual(remoteAudio.subscriptionRequests, [true]);
    assert.deepEqual(localAudio.subscriptionRequests, []);
});

test("a ScreenShareAudio publication appearing after Watch is discovered and subscribed", () => {
    installPlaybackDom();
    const video = screenPublication("video", "late-audio");
    const session = screenSession([video]);
    const watch = { iridiumStreamId:"id", mediaStreamId:"late-audio",
        participantIdentity:"publisher", audioAvailable:null };
    session.watched.set("late-audio", watch);
    reconcileScreenWatch(session, watch);
    const audio = screenPublication("audio", "late-audio");
    session.room.remoteParticipants.get("publisher").trackPublications.set("audio", audio);

    reconcileScreenWatch(session, watch);

    assert.deepEqual(audio.subscriptionRequests, [true]);
    assert.equal(watch.audioAvailable, true);
});

test("an independently ended screen-audio track is unpublished and clears advertised availability", async () => {
    globalThis.LivekitClient = { Room:function(){} };
    let ended;
    const callbacks = [], unpublished = [];
    const audio = { kind:"audio", source:"screen_share_audio", mediaStreamTrack:{
        addEventListener:(name, handler) => { if (name === "ended") ended = handler; }
    } };
    const video = { kind:"video", source:"screen_share", mediaStreamTrack:{ addEventListener:() => {} } };
    const session = { id:"session", kind:"community", screenGeneration:3, screenMediaStreamId:"stream",
        screenStreamId:"published", screenAudioAvailable:true, screenPublicationMutation:false,
        screenTracks:[video, audio], viewers:new Map(), callback:{
            invokeMethodAsync:async (...args) => callbacks.push(args)
        }, room:{ localParticipant:{ unpublishTrack:async track => unpublished.push(track) } } };

    wireScreenEnded(session, [video, audio], 3);
    await ended();

    assert.deepEqual(session.screenTracks, [video]);
    assert.equal(session.screenAudioAvailable, false);
    assert.deepEqual(unpublished, [audio]);
    assert.deepEqual(callbacks, [["OnScreenShareAudioAvailabilityChanged", false]]);
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

    const result = await replacePublishedScreenTracks(session, replacement);

    assert.equal(result.previous[0], previous[0]);
    assert.equal(result.publications.length, 2);
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

for (const caller of ["A", "B"]) {
    test(`CALLER=${caller}: room events reconcile both owners, late audio and replacement participants`, async () => {
        installCapture(); installPlaybackDom();
        const names = ["TrackPublished", "TrackSubscribed", "TrackUnsubscribed", "TrackUnpublished", "TrackMuted", "TrackUnmuted",
            "LocalTrackPublished", "LocalTrackUnpublished", "ParticipantConnected", "ParticipantDisconnected", "Reconnecting", "Reconnected", "Disconnected", "ConnectionStateChanged"];
        LivekitClient.RoomEvent = Object.fromEntries(names.map(name => [name, name]));
        LivekitClient.VideoQuality = { HIGH:2 };
        const handlers = new Map();
        const video = screenPublication("video", "remote");
        video.setVideoQuality = quality => { video.quality = quality; };
        const session = screenSession([video], caller === "A" ? "B" : "A");
        session.playbacks = new Map();
        session.room.on = (event, handler) => handlers.set(event, handler);
        wireRoom(session);
        const remote = [...session.room.remoteParticipants.values()][0];
        await setStreamSubscriptionForSession(session, "id", "remote", remote.identity, true);
        assert.equal(video.quality, 2);
        const audio = screenPublication("audio", "remote");
        remote.trackPublications.set("audio", audio);
        handlers.get("TrackPublished")(audio, remote);
        assert.equal(audio.subscriptionRequests.at(-1), true);
        const newVideo = screenPublication("video", "remote"), newAudio = screenPublication("audio", "remote");
        const replacement = { identity:remote.identity, trackPublications:new Map([["v",newVideo],["a",newAudio]]) };
        session.room.remoteParticipants.set(remote.identity, replacement);
        handlers.get("Reconnected")();
        assert.equal(newVideo.subscriptionRequests.at(-1), true);
        assert.equal(newAudio.subscriptionRequests.at(-1), true);
        replacement.trackPublications.delete("a");
        handlers.get("TrackUnpublished")(newAudio, replacement);
        assert.equal(session.watched.get("remote").audioAvailable, false);
        await setStreamSubscriptionForSession(session, "id", "remote", remote.identity, false);
        assert.equal(newVideo.subscriptionRequests.at(-1), false);
    });
}

test("diagnostic intervals use elapsed RTP time and reject counters reset by reconnect", () => {
    const previous = { timestamp:1000, bytes:100, framesEncoded:10, framesDecoded:5, totalEncodeTime:1, totalDecodeTime:.5 };
    const current = { timestamp:3000, bytes:2100, framesEncoded:130, framesDecoded:125, totalEncodeTime:1.6, totalDecodeTime:.8 };
    const sample = mediaStatsInterval(previous, current);
    assert.equal(sample.bitrateBps, 8000);
    assert.equal(sample.encodedFps, 60);
    assert.equal(sample.decodedFps, 60);
    assert.ok(Math.abs(sample.encodeMsPerFrame - 5) < .001);
    assert.equal(mediaStatsInterval(current, previous), null);
    assert.equal(mediaStatsInterval(previous, { ...current, bytes:0 }), null);
});
