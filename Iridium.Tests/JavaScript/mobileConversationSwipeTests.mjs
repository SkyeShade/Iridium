import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

class EventTargetFake {
    listeners = new Map();
    addEventListener(name, handler) {
        const handlers = this.listeners.get(name) ?? new Set();
        handlers.add(handler);
        this.listeners.set(name, handlers);
    }
    removeEventListener(name, handler) { this.listeners.get(name)?.delete(handler); }
    dispatch(name, event = {}) {
        for (const handler of this.listeners.get(name) ?? []) handler(event);
    }
    count(name) { return this.listeners.get(name)?.size ?? 0; }
}

class StyleFake {
    values = new Map();
    setProperty(name, value) { this.values.set(name, value); }
    removeProperty(name) { this.values.delete(name); }
    get(name) { return this.values.get(name); }
}

const query = new EventTargetFake();
query.matches = true;
globalThis.matchMedia = () => query;
const documentFake = new EventTargetFake();
documentFake.visibilityState = 'visible';
documentFake.activeElement = null;
globalThis.document = documentFake;
const viewport = new EventTargetFake();
viewport.height = 500;
viewport.width = 390;
viewport.offsetTop = 0;
const windowFake = new EventTargetFake();
windowFake.visualViewport = viewport;
windowFake.innerHeight = 500;
windowFake.innerWidth = 390;
windowFake.setTimeout = setTimeout;
windowFake.clearTimeout = clearTimeout;
globalThis.window = windowFake;

let nextFrame = 1;
const frames = new Map();
globalThis.requestAnimationFrame = callback => {
    const id = nextFrame++;
    frames.set(id, callback);
    return id;
};
globalThis.cancelAnimationFrame = id => frames.delete(id);
const flushFrames = () => {
    const pending = [...frames.values()];
    frames.clear();
    for (const callback of pending) callback();
};

const source = await readFile(new URL('../../Iridium.UI/wwwroot/js/mobileConversationSwipe.js', import.meta.url), 'utf8');
const module = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

globalThis.getComputedStyle = element => element.computedStyle;
const diagnosticElement = (rect, computedStyle, selectors = []) => ({
    getBoundingClientRect: () => rect,
    computedStyle,
    dataset: { mobileContentKind: 'direct' },
    querySelector: selector => selectors.includes(selector) ? {} : null
});
const navigationPanel = diagnosticElement(
    { x: -390, y: 0, width: 390, height: 780 },
    { display: 'grid', visibility: 'hidden', transform: 'matrix(1, 0, 0, 1, -390, 0)', zIndex: '20' });
const conversationPanel = diagnosticElement(
    { x: 0, y: 0, width: 390, height: 780 },
    { display: 'flex', visibility: 'visible', transform: 'matrix(1, 0, 0, 1, 0, 0)', zIndex: '30' },
    ['.mobile-conversation-header', '.main-content-slot', '.direct-message-view', '.dm-message-region',
        '.dm-message-history', '.message-list', '.composer-wrap']);
const panelSnapshot = module.inspectMobilePanels(
    { className: 'app-shell mobile-conversation' }, navigationPanel, conversationPanel);
assert.equal(panelSnapshot.navigationX, -390);
assert.equal(panelSnapshot.navigationWidth, 390);
assert.equal(panelSnapshot.conversationX, 0);
assert.equal(panelSnapshot.conversationWidth, 390);
assert.equal(panelSnapshot.contentKind, 'direct');
assert.equal(panelSnapshot.hasHeader, true);
assert.equal(panelSnapshot.hasDirectMessageView, true);
assert.equal(panelSnapshot.hasChannelView, false);
assert.equal(panelSnapshot.hasDmMessageRegion, true);
assert.equal(panelSnapshot.hasDmMessageHistory, true);
assert.equal(panelSnapshot.hasMessageList, true);
assert.equal(panelSnapshot.hasComposer, true);
assert.deepEqual(panelSnapshot.missingNodes, []);

const blankConversation = diagnosticElement(
    { x: 0, y: 0, width: 390, height: 780 },
    { display: 'flex', visibility: 'visible', transform: 'matrix(1, 0, 0, 1, 0, 0)', zIndex: '30' });
const blankSnapshot = module.inspectMobilePanels(
    { className: 'app-shell mobile-conversation' }, navigationPanel, blankConversation);
assert.deepEqual(blankSnapshot.missingNodes,
    ['mobile-conversation-header', 'main-content-slot', 'direct-message-view', 'dm-message-region',
        'dm-message-history', 'message-list', 'composer-wrap']);

assert.equal(module.shouldSuppressMobileSafeBottom(true, false, true, true), false);
assert.equal(module.shouldSuppressMobileSafeBottom(true, true, true, true), true);
assert.equal(module.shouldSuppressMobileSafeBottom(false, true, true, true), false);
assert.equal(module.shouldSuppressMobileSafeBottom(true, true, false, true), false);
assert.equal(module.shouldSuppressMobileSafeBottom(true, true, true, false), false);

const shell = { style: new StyleFake() };
const dotnet = { invokeMethodAsync: async () => {} };
module.wireMobileViewport(shell, dotnet);
assert.equal(shell.style.get('--iridium-mobile-safe-bottom'), 'env(safe-area-inset-bottom, 0px)');

const editor = { matches: selector => selector === '.composer-rich-editor' };
documentFake.activeElement = editor;
documentFake.dispatch('focusin', { target: editor });
viewport.height = 300;
viewport.dispatch('resize');
flushFrames();
assert.equal(shell.style.get('--iridium-mobile-safe-bottom'), '0px');

documentFake.activeElement = null;
documentFake.dispatch('focusout', { target: editor });
flushFrames();
assert.equal(shell.style.get('--iridium-mobile-safe-bottom'), 'env(safe-area-inset-bottom, 0px)');

query.matches = false;
query.dispatch('change');
flushFrames();
assert.equal(shell.style.get('--iridium-mobile-safe-bottom'), undefined);

const countsBeforeUnwire = {
    query: query.count('change'),
    focusin: documentFake.count('focusin'),
    focusout: documentFake.count('focusout'),
    viewportResize: viewport.count('resize')
};
assert.deepEqual(countsBeforeUnwire, { query: 1, focusin: 1, focusout: 1, viewportResize: 1 });

module.wireMobileViewport(shell, dotnet);
assert.equal(query.count('change'), 1, 'repeated wire must not accumulate listeners');
module.unwireMobileViewport(shell);
assert.equal(query.count('change'), 0);
assert.equal(documentFake.count('focusin'), 0);
assert.equal(documentFake.count('focusout'), 0);
assert.equal(viewport.count('resize'), 0);
module.unwireMobileViewport(shell);

const resumeCalls = [];
const resumeDotnet = { invokeMethodAsync: async (...args) => { resumeCalls.push(args); } };
module.wireRealtimeResume(shell, resumeDotnet);
module.wireRealtimeResume(shell, resumeDotnet);
assert.equal(windowFake.count('online'), 1, 'repeated resume wiring must not accumulate listeners');
windowFake.dispatch('online');
windowFake.dispatch('pageshow');
windowFake.dispatch('focus');
await new Promise(resolve => setTimeout(resolve, 180));
assert.equal(resumeCalls.length, 1, 'related resume events should coalesce');
assert.deepEqual(resumeCalls[0], ['RealtimeResumeAsync', 'online+pageshow+focus']);
module.unwireRealtimeResume(shell);
assert.equal(windowFake.count('online'), 0);
assert.equal(windowFake.count('pageshow'), 0);
assert.equal(windowFake.count('focus'), 0);
assert.equal(documentFake.count('visibilitychange'), 0);
module.unwireRealtimeResume(shell);

console.log('mobileConversationSwipe tests passed');
