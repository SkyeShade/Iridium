import assert from "node:assert/strict";
import test from "node:test";
import {
    composerActionLongPressMilliseconds,
    longPressMovementExceeded,
    messageActionLongPressMilliseconds,
    mobileLongPressMoveTolerance,
    mobileMessageSheetBackdropOpacity,
    mobileMessageSheetDismissDistance,
    mobileMessageSheetDragOffset,
    mobileMessageSheetSnapMilliseconds,
    mobileMessageSheetVelocityThreshold,
    shouldDismissMobileMessageActionSheet
} from "../../Iridium.Web/wwwroot/js/chat.js";

test("mobile long-press thresholds are responsive and intentionally distinct", () => {
    assert.equal(composerActionLongPressMilliseconds, 500);
    assert.equal(messageActionLongPressMilliseconds, 550);
});

test("bottom-sheet dismissal distinguishes short drags, distance, and velocity", () => {
    assert.equal(shouldDismissMobileMessageActionSheet(60, 500, .2), false);
    assert.equal(mobileMessageSheetDismissDistance(500), 140);
    assert.equal(mobileMessageSheetDismissDistance(250), 90);
    assert.equal(shouldDismissMobileMessageActionSheet(140, 500, .2), true);
    assert.equal(shouldDismissMobileMessageActionSheet(50, 500, .9), true);
    assert.equal(mobileMessageSheetVelocityThreshold, .85);
    assert.equal(mobileMessageSheetSnapMilliseconds, 190);
});

test("bottom-sheet drag is one-to-one, upward-clamped, and fades the backdrop", () => {
    assert.equal(mobileMessageSheetDragOffset(100, 120), 20);
    assert.equal(mobileMessageSheetDragOffset(100, 80), 0);
    assert.equal(mobileMessageSheetBackdropOpacity(0, 100), .62);
    assert.ok(Math.abs(mobileMessageSheetBackdropOpacity(50, 100) - .434) < .0001);
    assert.ok(Math.abs(mobileMessageSheetBackdropOpacity(100, 100) - .248) < .0001);
});

test("movement tolerance permits small jitter and cancels a scroll gesture", () => {
    assert.equal(mobileLongPressMoveTolerance, 10);
    assert.equal(longPressMovementExceeded(0, 0, 6, 8), false);
    assert.equal(longPressMovementExceeded(0, 0, 7, 8), true);
});
