const fs = require('node:fs');
const path = require('node:path');

const DEFAULT_NODE_URL = 'https://iridiumonline.net';

// Accepts "iridiumonline.net", "https://iridiumonline.net/some/path" or a localhost dev URL and returns
// the origin the app should load, or null when the input can't be a node address.
function normalizeNodeUrl(input) {
    const trimmed = String(input ?? '').trim();
    if (!trimmed) return null;
    const withScheme = /^[a-z][a-z0-9+.-]*:\/\//i.test(trimmed) ? trimmed : `https://${trimmed}`;
    let url;
    try { url = new URL(withScheme); }
    catch { return null; }
    if (url.protocol !== 'https:' && url.protocol !== 'http:') return null;
    // Plain http only for local development; a real node always serves TLS.
    if (url.protocol === 'http:' && !isLoopbackHost(url.hostname)) return null;
    if (!url.hostname) return null;
    return url.origin;
}

function isLoopbackHost(hostname) {
    return hostname === 'localhost' || hostname === '127.0.0.1' || hostname === '[::1]';
}

function createSettingsStore(directory) {
    const file = path.join(directory, 'settings.json');
    let current = { nodeUrl: DEFAULT_NODE_URL, bounds: null };
    try {
        const saved = JSON.parse(fs.readFileSync(file, 'utf8'));
        current = {
            nodeUrl: normalizeNodeUrl(saved.nodeUrl) ?? DEFAULT_NODE_URL,
            bounds: isBounds(saved.bounds) ? saved.bounds : null
        };
    } catch { /* first run or unreadable file: keep defaults */ }

    return {
        get: () => ({ ...current }),
        update(changes) {
            current = { ...current, ...changes };
            fs.mkdirSync(directory, { recursive: true });
            fs.writeFileSync(file, JSON.stringify(current, null, 2));
            return { ...current };
        }
    };
}

function isBounds(value) {
    return value && ['x', 'y', 'width', 'height'].every(key => Number.isFinite(value[key]));
}

module.exports = { DEFAULT_NODE_URL, normalizeNodeUrl, createSettingsStore };
