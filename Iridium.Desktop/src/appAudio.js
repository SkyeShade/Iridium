const { spawn } = require('node:child_process');
const path = require('node:path');

// Per-app sound for screen shares. Chromium's loopback only records the default output device, so with
// GoXLR/Sonar-style setups the shared game is silent and Iridium itself is heard instead. The native helper
// (native/AppAudio) uses Windows process loopback: a window share sends just that program's sound, a screen
// share sends everything except Iridium. The page turns the stream into the share's audio track.

const READY_TIMEOUT_MS = 3000;
const UNCLAIMED_TIMEOUT_MS = 15000;
const FRAME_BYTES = 8; // stereo float32

function windowHandleFromSourceId(sourceId) {
    const match = /^window:(\d+):/.exec(String(sourceId));
    return match ? match[1] : null;
}

function helperArguments(sourceId, ownProcessId) {
    const windowHandle = windowHandleFromSourceId(sourceId);
    return windowHandle ? ['--hwnd', windowHandle] : ['--pid', String(ownProcessId), '--exclude'];
}

function createAppAudio({ app, log }) {
    const executable = app.isPackaged
        ? path.join(process.resourcesPath, 'bin', 'iridium-app-audio.exe')
        : path.join(__dirname, '..', 'native', 'bin', 'iridium-app-audio.exe');
    const captures = new Map();
    let nextId = 1;
    let pending = null;

    function stop(id) {
        const capture = captures.get(id);
        if (!capture) return;
        captures.delete(id);
        if (pending?.id === id) { clearTimeout(pending.timer); pending = null; }
        try { capture.child.stdin.end(); } catch { /* already gone */ }
        setTimeout(() => { if (capture.child.exitCode === null) capture.child.kill(); }, 1000).unref?.();
    }

    function start(args) {
        return new Promise(resolve => {
            let child;
            try { child = spawn(executable, args, { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] }); }
            catch (error) { log(`app audio: could not start helper: ${error.message}`); resolve(null); return; }
            const capture = { id: nextId++, child, contents: null, carry: Buffer.alloc(0), sampleRate: 48000, channels: 2 };
            let settled = false;
            const settle = value => { if (!settled) { settled = true; clearTimeout(timer); resolve(value); } };
            const timer = setTimeout(() => { log('app audio: helper did not become ready'); child.kill(); settle(null); }, READY_TIMEOUT_MS);
            let status = '';
            child.stderr.on('data', data => {
                status += data;
                let newline;
                while ((newline = status.indexOf('\n')) >= 0) {
                    const line = status.slice(0, newline).trim();
                    status = status.slice(newline + 1);
                    if (!line) continue;
                    log(`app audio: ${line}`);
                    try {
                        const message = JSON.parse(line);
                        if (message.event === 'ready') {
                            capture.sampleRate = message.sampleRate;
                            capture.channels = message.channels;
                            captures.set(capture.id, capture);
                            settle(capture);
                        }
                    } catch { /* non-JSON diagnostics */ }
                }
            });
            child.stdout.on('data', data => {
                if (!capture.contents || capture.contents.isDestroyed()) return;
                // Only forward whole frames; the page reads the bytes as float32 pairs.
                const bytes = capture.carry.length ? Buffer.concat([capture.carry, data]) : data;
                const usable = bytes.length - (bytes.length % FRAME_BYTES);
                capture.carry = bytes.subarray(usable);
                if (usable) capture.contents.send('app-audio:data', capture.id, bytes.subarray(0, usable));
            });
            child.on('error', error => { log(`app audio: helper error: ${error.message}`); settle(null); });
            child.on('exit', code => {
                settle(null);
                if (captures.has(capture.id)) {
                    log(`app audio: helper exited (${code})`);
                    if (capture.contents && !capture.contents.isDestroyed()) capture.contents.send('app-audio:ended', capture.id);
                    stop(capture.id);
                }
            });
        });
    }

    return {
        // Starts capturing for the source just picked; the page claims it right after getDisplayMedia resolves.
        // Returns false when per-app capture isn't available, so the caller can fall back to system loopback.
        async prepare(contents, sourceId) {
            if (pending) stop(pending.id);
            const capture = await start(helperArguments(sourceId, process.pid));
            if (!capture) return false;
            pending = { id: capture.id, contents, timer: setTimeout(() => stop(capture.id), UNCLAIMED_TIMEOUT_MS) };
            return true;
        },
        take(contents) {
            if (!pending || pending.contents !== contents) return null;
            clearTimeout(pending.timer);
            const capture = captures.get(pending.id);
            pending = null;
            if (!capture) return null;
            capture.contents = contents;
            return { id: capture.id, sampleRate: capture.sampleRate, channels: capture.channels };
        },
        stop,
        stopFor(contents) {
            if (pending?.contents === contents) stop(pending.id);
            for (const capture of [...captures.values()]) if (capture.contents === contents) stop(capture.id);
        },
        stopAll() { for (const id of [...captures.keys()]) stop(id); }
    };
}

module.exports = { createAppAudio, helperArguments, windowHandleFromSourceId };
