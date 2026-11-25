// The window only ever shows the configured node. Everything else (links in messages, embeds) opens in the
// user's browser, and only the node gets device permissions.

const NODE_PERMISSIONS = new Set([
    'media', 'display-capture', 'notifications', 'fullscreen', 'clipboard-read', 'clipboard-sanitized-write',
    'speaker-selection'
]);

function isNodeUrl(url, nodeUrl) {
    try { return new URL(url).origin === new URL(nodeUrl).origin; }
    catch { return false; }
}

function isExternalWebUrl(url) {
    try {
        const { protocol } = new URL(url);
        return protocol === 'https:' || protocol === 'http:' || protocol === 'mailto:';
    } catch { return false; }
}

function allowsPermission(permission, requestingUrl, nodeUrl) {
    return NODE_PERMISSIONS.has(permission) && isNodeUrl(requestingUrl, nodeUrl);
}

module.exports = { isNodeUrl, isExternalWebUrl, allowsPermission };
