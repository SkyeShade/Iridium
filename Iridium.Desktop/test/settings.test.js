const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { DEFAULT_NODE_URL, createSettingsStore, normalizeNodeUrl } = require('../src/settings');
const { allowsPermission, isExternalWebUrl, isNodeUrl } = require('../src/navigation');

test('node addresses normalise to an origin', () => {
    assert.equal(normalizeNodeUrl('iridiumonline.net'), 'https://iridiumonline.net');
    assert.equal(normalizeNodeUrl(' https://iridiumonline.net/channels/1?x=2 '), 'https://iridiumonline.net');
    assert.equal(normalizeNodeUrl('chat.example.com:8443'), 'https://chat.example.com:8443');
    assert.equal(normalizeNodeUrl('http://localhost:5185'), 'http://localhost:5185');
});

test('node addresses reject non-web schemes and plain http outside localhost', () => {
    for (const value of ['', '   ', null, 'file:///C:/x', 'javascript:alert(1)', 'http://iridiumonline.net', 'https://'])
        assert.equal(normalizeNodeUrl(value), null, String(value));
});

test('settings persist and fall back to the default node', () => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'iridium-settings-'));
    assert.equal(createSettingsStore(directory).get().nodeUrl, DEFAULT_NODE_URL);
    createSettingsStore(directory).update({ nodeUrl: 'https://chat.example.com' });
    assert.equal(createSettingsStore(directory).get().nodeUrl, 'https://chat.example.com');
    fs.writeFileSync(path.join(directory, 'settings.json'), '{"nodeUrl":"javascript:alert(1)","bounds":{"x":"a"}}');
    assert.deepEqual(createSettingsStore(directory).get(), { nodeUrl: DEFAULT_NODE_URL, bounds: null });
});

test('only the node stays in the window and gets device permissions', () => {
    const node = 'https://iridiumonline.net';
    assert.equal(isNodeUrl('https://iridiumonline.net/api/server-info', node), true);
    assert.equal(isNodeUrl('https://evil.example/iridiumonline.net', node), false);
    assert.equal(isNodeUrl('http://iridiumonline.net/', node), false);
    assert.equal(allowsPermission('media', 'https://iridiumonline.net/', node), true);
    assert.equal(allowsPermission('media', 'https://www.youtube.com/', node), false);
    assert.equal(allowsPermission('geolocation', 'https://iridiumonline.net/', node), false);
    assert.equal(isExternalWebUrl('https://youtube.com/watch'), true);
    assert.equal(isExternalWebUrl('file:///C:/Windows/system32/calc.exe'), false);
});
