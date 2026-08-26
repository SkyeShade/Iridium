import test from "node:test";
import assert from "node:assert/strict";
import {
    createVoiceActivityGate, evaluateVoiceActivity, inputSensitivityConfiguration,
    normalizedInputLevel, updateVoiceActivityConfiguration
} from "../../Iridium.Web/wwwroot/js/inputSensitivity.js";

test("manual sensitivity controls voice activity threshold immediately", () => {
    const gate = createVoiceActivityGate({ autoInputSensitivity: false, manualInputSensitivityThreshold: 0.7 });
    assert.equal(evaluateVoiceActivity(gate, 0.6, 0), false);
    assert.equal(evaluateVoiceActivity(gate, 0.72, 10), false);
    assert.equal(evaluateVoiceActivity(gate, 0.72, 20), true);

    updateVoiceActivityConfiguration(gate,
        { autoInputSensitivity: false, manualInputSensitivityThreshold: 0.4 });
    gate.speaking = false;
    assert.equal(evaluateVoiceActivity(gate, 0.5, 30), false);
    assert.equal(evaluateVoiceActivity(gate, 0.5, 40), true);
});

test("automatic sensitivity uses the adaptive source rather than the manual threshold", () => {
    const gate = createVoiceActivityGate({ autoInputSensitivity: true, manualInputSensitivityThreshold: 0.95 });
    for (let index = 0; index < 20; index++) evaluateVoiceActivity(gate, 0.12, index * 20);
    assert.equal(evaluateVoiceActivity(gate, 0.5, 500), false);
    assert.equal(evaluateVoiceActivity(gate, 0.5, 520), true);
    assert.equal(gate.configuration.automatic, true);
});

test("push to talk bypasses sensitivity without bypassing mute", () => {
    const gate = createVoiceActivityGate({ autoInputSensitivity: false, manualInputSensitivityThreshold: 1 });
    assert.equal(evaluateVoiceActivity(gate, 0, 0, { pushToTalkActive: true }), true);
    assert.equal(evaluateVoiceActivity(gate, 1, 10, { pushToTalkActive: true, muted: true }), false);
});

test("normalization and persisted configuration clamp safely", () => {
    assert.equal(normalizedInputLevel(0), 0);
    assert.equal(inputSensitivityConfiguration({ manualInputSensitivityThreshold: 8 }).manualThreshold, 1);
    assert.equal(inputSensitivityConfiguration({ manualInputSensitivityThreshold: -2 }).manualThreshold, 0);
    assert.equal(inputSensitivityConfiguration(null).automatic, true);
});
