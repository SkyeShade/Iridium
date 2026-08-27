import assert from "node:assert/strict";
import test from "node:test";
import {
    classifyMobileSwipeDirection,
    inspectMobileConversationSwipeState,
    MobileConversationSwipePhase,
    mobileConversationSwipeDistance,
    mobileConversationSwipeOffset,
    mobileConversationSwipeSlop,
    mobileConversationSwipeSnapMilliseconds,
    mobileConversationSwipeVelocityThreshold,
    qualifiesMobileBackSwipe,
    shouldCancelMobileConversationSwipe,
    unwireMobileConversationSwipe,
    wireMobileConversationSwipe
} from "../../Iridium.UI/wwwroot/js/mobileConversationSwipe.js";

test("mobile conversation swipe direction locks only after horizontal intent", () => {
    assert.equal(mobileConversationSwipeSlop, 10);
    assert.equal(classifyMobileSwipeDirection(9, 0), "undecided");
    assert.equal(classifyMobileSwipeDirection(14, 2), "horizontal");
    assert.equal(classifyMobileSwipeDirection(3, 14), "vertical");
    assert.equal(classifyMobileSwipeDirection(-14, 2), "rejected");
});

test("conversation translation is one-to-one and clamps negative movement", () => {
    assert.equal(mobileConversationSwipeOffset(100, 140, 390), 40);
    assert.equal(mobileConversationSwipeOffset(100, 80, 390), 0);
    assert.equal(mobileConversationSwipeOffset(100, 600, 390), 390);
});

test("distance and rightward flick complete while a short slow drag snaps back", () => {
    assert.equal(mobileConversationSwipeDistance(320), 110);
    assert.ok(Math.abs(mobileConversationSwipeDistance(390) - 128.7) < .0001);
    assert.equal(qualifiesMobileBackSwipe(70, 2, 390, true, false, .2), false);
    assert.equal(qualifiesMobileBackSwipe(130, 2, 390, true, false, .2), true);
    assert.equal(qualifiesMobileBackSwipe(45, 2, 390, true, false, .9), true);
    assert.equal(qualifiesMobileBackSwipe(180, 2, 390, false, true, 1.2), false);
    assert.equal(mobileConversationSwipeVelocityThreshold, .85);
    assert.equal(mobileConversationSwipeSnapMilliseconds, 210);
});

test("claimed horizontal drag ignores candidate-only cancellation events", () => {
    assert.equal(shouldCancelMobileConversationSwipe(
        MobileConversationSwipePhase.candidate, "bottom-sheet-cancel-event"), true);
    assert.equal(shouldCancelMobileConversationSwipe(
        MobileConversationSwipePhase.draggingHorizontal, "bottom-sheet-cancel-event"), false);
    assert.equal(shouldCancelMobileConversationSwipe(
        MobileConversationSwipePhase.draggingHorizontal, "resize"), false);
    assert.equal(shouldCancelMobileConversationSwipe(
        MobileConversationSwipePhase.draggingHorizontal, "pointerleave"), false);
    assert.equal(shouldCancelMobileConversationSwipe(
        MobileConversationSwipePhase.draggingHorizontal, "pointercancel"), true);
});

class FakeClassList {
    constructor(...values) { this.values = new Set(values); }
    add(...values) { values.forEach(value => this.values.add(value)); }
    remove(...values) { values.forEach(value => this.values.delete(value)); }
    contains(value) { return this.values.has(value); }
    [Symbol.iterator]() { return this.values[Symbol.iterator](); }
    toString() { return [...this.values].join(" "); }
}

class FakeStyle {
    constructor() { this.values = new Map(); }
    setProperty(name, value) { this.values.set(name, String(value)); }
    removeProperty(name) { this.values.delete(name); }
    get transform() { return this.values.get("transform") ?? ""; }
    set transform(value) { this.values.set("transform", value); }
    get transition() { return this.values.get("transition") ?? ""; }
    set transition(value) { this.values.set("transition", value); }
}

class FakeElement {
    constructor(classes = []) {
        this.classList = new FakeClassList(...classes);
        this.style = new FakeStyle();
        this.listeners = new Map();
        this.captured = new Set();
        this.isConnected = true;
        this.localName = "main";
        this.parentElement = null;
        this.scrollWidth = 390;
        this.clientWidth = 390;
    }
    closest(selector) { return selector === ".app-shell" ? this.shell : null; }
    addEventListener(name, handler) {
        const handlers = this.listeners.get(name) ?? [];
        handlers.push(handler);
        this.listeners.set(name, handlers);
    }
    removeEventListener(name, handler) {
        this.listeners.set(name, (this.listeners.get(name) ?? []).filter(value => value !== handler));
    }
    emit(name, event) { for (const handler of this.listeners.get(name) ?? []) handler(event); }
    setPointerCapture(pointerId) {
        this.captured.add(pointerId);
        this.emit("gotpointercapture", { pointerId });
    }
    releasePointerCapture(pointerId) {
        this.captured.delete(pointerId);
        this.emit("lostpointercapture", { pointerId });
    }
    hasPointerCapture(pointerId) { return this.captured.has(pointerId); }
    getBoundingClientRect() { return { width: 390 }; }
    hasAttribute() { return false; }
}

function installGestureEnvironment() {
    const frames = new Map();
    let nextFrame = 1;
    const windowListeners = new Map();
    const documentListeners = new Map();
    globalThis.Element = FakeElement;
    globalThis.matchMedia = query => ({ matches: query.includes("max-width") || query.includes("reduced-motion") });
    globalThis.getComputedStyle = element => ({
        touchAction: "pan-y",
        overflowX: "visible",
        transform: element.style?.transform || "none"
    });
    globalThis.DOMMatrixReadOnly = class {
        constructor(transform) { this.m41 = Number(transform.match(/translate3d\(([-\d.]+)px/)?.[1] ?? 0); }
    };
    globalThis.requestAnimationFrame = callback => {
        const id = nextFrame++;
        frames.set(id, callback);
        return id;
    };
    globalThis.cancelAnimationFrame = id => frames.delete(id);
    globalThis.CustomEvent = class {
        constructor(type, options) { this.type = type; this.detail = options?.detail; }
    };
    const eventTarget = listeners => ({
        addEventListener(name, handler) {
            const handlers = listeners.get(name) ?? [];
            handlers.push(handler);
            listeners.set(name, handlers);
        },
        removeEventListener(name, handler) {
            listeners.set(name, (listeners.get(name) ?? []).filter(value => value !== handler));
        },
        dispatchEvent(event) {
            for (const handler of listeners.get(event.type) ?? []) handler(event);
            return true;
        }
    });
    globalThis.window = { ...eventTarget(windowListeners), getSelection: () => ({ toString: () => "" }) };
    globalThis.document = { ...eventTarget(documentListeners), visibilityState: "visible" };
    return {
        flushFrames() {
            const pending = [...frames.values()];
            frames.clear();
            pending.forEach(callback => callback(0));
        },
        emitWindow(type) { window.dispatchEvent({ type }); }
    };
}

function pointer(pointerId, x, y) {
    return {
        pointerId, clientX: x, clientY: y, pointerType: "touch", isPrimary: true, button: 0,
        cancelable: true, target: null, preventDefault() { this.defaultPrevented = true; }
    };
}

test("horizontal capture remains active across moves, resize, and unrelated sheet event until pointerup", () => {
    const environment = installGestureEnvironment();
    const shell = new FakeElement(["app-shell", "mobile-conversation"]);
    const panel = new FakeElement(["main-content"]);
    panel.shell = shell;
    const down = pointer(7, 100, 50);
    down.target = panel;
    wireMobileConversationSwipe(panel, { invokeMethodAsync: async () => {} });

    panel.emit("pointerdown", down);
    const firstMove = pointer(7, 120, 52);
    firstMove.target = panel;
    panel.emit("pointermove", firstMove);
    environment.flushFrames();
    assert.equal(inspectMobileConversationSwipeState(panel).phase, MobileConversationSwipePhase.draggingHorizontal);
    assert.equal(panel.style.transform, "translate3d(20px,0,0)");
    assert.equal(panel.hasPointerCapture(7), true);

    environment.emitWindow("resize");
    environment.emitWindow("iridium-mobile-message-actions-open");
    panel.emit("pointerleave", { pointerId: 7 });
    environment.emitWindow("scroll");
    const secondMove = pointer(7, 165, 53);
    secondMove.target = panel;
    panel.emit("pointermove", secondMove);
    environment.flushFrames();
    assert.equal(inspectMobileConversationSwipeState(panel).phase, MobileConversationSwipePhase.draggingHorizontal);
    assert.equal(panel.style.transform, "translate3d(65px,0,0)");
    assert.equal(panel.hasPointerCapture(7), true);

    const release = pointer(7, 165, 53);
    release.target = panel;
    panel.emit("pointerup", release);
    environment.flushFrames();
    unwireMobileConversationSwipe(panel);
});

test("vertical intent abandons before capture and pointercancel safely resets claimed drag", async () => {
    const environment = installGestureEnvironment();
    const shell = new FakeElement(["app-shell", "mobile-conversation"]);
    const verticalPanel = new FakeElement(["main-content"]);
    verticalPanel.shell = shell;
    wireMobileConversationSwipe(verticalPanel, { invokeMethodAsync: async () => {} });
    const down = pointer(8, 100, 50);
    down.target = verticalPanel;
    verticalPanel.emit("pointerdown", down);
    const vertical = pointer(8, 102, 70);
    vertical.target = verticalPanel;
    verticalPanel.emit("pointermove", vertical);
    assert.equal(inspectMobileConversationSwipeState(verticalPanel).phase, MobileConversationSwipePhase.idle);
    assert.equal(verticalPanel.hasPointerCapture(8), false);
    unwireMobileConversationSwipe(verticalPanel);

    const horizontalPanel = new FakeElement(["main-content"]);
    horizontalPanel.shell = shell;
    wireMobileConversationSwipe(horizontalPanel, { invokeMethodAsync: async () => {} });
    const secondDown = pointer(9, 100, 50);
    secondDown.target = horizontalPanel;
    horizontalPanel.emit("pointerdown", secondDown);
    const horizontal = pointer(9, 130, 51);
    horizontal.target = horizontalPanel;
    horizontalPanel.emit("pointermove", horizontal);
    environment.flushFrames();
    horizontalPanel.emit("pointercancel", { pointerId: 9 });
    await Promise.resolve();
    assert.equal(inspectMobileConversationSwipeState(horizontalPanel).phase, MobileConversationSwipePhase.idle);
    assert.equal(horizontalPanel.style.transform, "");
    unwireMobileConversationSwipe(horizontalPanel);
});
