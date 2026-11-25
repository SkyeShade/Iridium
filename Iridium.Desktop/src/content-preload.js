const { contextBridge } = require('electron');

const versionArgument = process.argv.find(value => value.startsWith('--iridium-desktop-version='));

// The node's web client can feature-detect the desktop app through this; keep it small and read-only.
contextBridge.exposeInMainWorld('iridiumDesktop', Object.freeze({
    platform: 'electron',
    version: versionArgument?.slice('--iridium-desktop-version='.length) ?? null
}));
