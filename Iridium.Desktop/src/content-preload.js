const { contextBridge, ipcRenderer } = require('electron');

const versionArgument = process.argv.find(value => value.startsWith('--iridium-desktop-version='));
const appAudioListeners = new Map();

ipcRenderer.on('app-audio:data', (_event, id, chunk) => {
    // A fresh ArrayBuffer per chunk, so the page can read it as float32 regardless of the IPC buffer's offset.
    appAudioListeners.get(id)?.data?.(chunk.buffer.slice(chunk.byteOffset, chunk.byteOffset + chunk.byteLength));
});
ipcRenderer.on('app-audio:ended', (_event, id) => {
    appAudioListeners.get(id)?.ended?.();
    appAudioListeners.delete(id);
});

// Claims the per-app audio capture the app started for the last screen-share pick, or null when there is none.
async function takeAppAudio() {
    const capture = await ipcRenderer.invoke('app-audio:take');
    if (!capture) return null;
    const listeners = {};
    appAudioListeners.set(capture.id, listeners);
    return {
        sampleRate: capture.sampleRate,
        channels: capture.channels,
        onData: callback => { listeners.data = callback; },
        onEnded: callback => { listeners.ended = callback; },
        stop: () => {
            appAudioListeners.delete(capture.id);
            ipcRenderer.send('app-audio:stop', capture.id);
        }
    };
}

// The node's web client can feature-detect the desktop app through this; keep it small.
contextBridge.exposeInMainWorld('iridiumDesktop', Object.freeze({
    platform: 'electron',
    version: versionArgument?.slice('--iridium-desktop-version='.length) ?? null,
    takeAppAudio
}));
