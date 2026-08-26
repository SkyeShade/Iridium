import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const appearanceSource = readFileSync(
    new URL("../../Iridium.Web/wwwroot/js/appearance.js", import.meta.url), "utf8");
const cssSource = readFileSync(
    new URL("../../Iridium.Web/wwwroot/css/app.css", import.meta.url), "utf8");
const rootBlock = cssSource.match(/:root\s*\{(?<body>[\s\S]*?)\}/)?.groups?.body;
assert.ok(rootBlock, "Unable to locate the canonical :root appearance variables.");
const canonical = Object.fromEntries(Array.from(rootBlock.matchAll(/(?<name>--[\w-]+)\s*:\s*(?<value>[^;]+);/g))
    .map(match => [match.groups.name, match.groups.value.trim()]));

const paletteVariables = [
    "--iridium-accent", "--iridium-bg-base", "--iridium-bg-surface",
    "--accent-soft", "--accent-deep", "--bg-deep", "--bg-sidebar", "--input-bg",
    "--surface-raised", "--surface-hover", "--surface-active", "--border", "--border-subtle",
    "--scrollbar-thumb", "--scrollbar-thumb-hover", "--text-strong", "--text", "--text-muted", "--text-faint"
];
const accentVariables = ["--iridium-accent", "--accent-soft", "--accent-deep"];

function createAppearance(storedJson = null) {
    const inline = new Map();
    const storage = new Map(storedJson === null ? [] : [["iridium.appearance", storedJson]]);
    const root = { style: { setProperty: (name, value) => inline.set(name, value) } };
    const localStorage = {
        getItem: key => storage.get(key) ?? null,
        setItem: (key, value) => storage.set(key, value)
    };
    const context = {
        window: {}, document: { documentElement: root }, localStorage,
        getComputedStyle: () => ({ getPropertyValue: name => inline.get(name) ?? canonical[name] ?? "" })
    };
    vm.createContext(context);
    vm.runInContext(appearanceSource, context);
    const variables = () => Object.fromEntries(paletteVariables.map(name =>
        [name, inline.get(name) ?? canonical[name]]));
    return { api: context.window.iridiumAppearance, variables, storage };
}

function changedVariables(before, after) {
    return Object.keys(after).filter(name => before[name] !== after[name]);
}

const plain = value => JSON.parse(JSON.stringify(value));

test("canonical default to accent-only change preserves every non-accent variable", () => {
    const appearance = createAppearance();
    const before = appearance.variables();
    const saved = appearance.api.save({ ...appearance.api.load(), accentColor: "#43c58a" });
    const after = appearance.variables();

    assert.equal(saved.accentColor, "#43c58a");
    assert.deepEqual(changedVariables(before, after), accentVariables);
    for (const name of paletteVariables.filter(name => !accentVariables.includes(name)))
        assert.equal(after[name], before[name], `${name} changed during an accent-only update`);
});

test("custom base and surface remain byte-identical across an accent change", () => {
    const appearance = createAppearance();
    appearance.api.save({
        accentColor: "#7654d6", baseBackgroundColor: "#10141b", surfaceColor: "#181e28",
        showMessageAvatarPresence: false
    });
    const beforePreferences = appearance.api.load();
    const beforeVariables = appearance.variables();
    const saved = appearance.api.save({ ...beforePreferences, accentColor: "#43c58a" });

    assert.equal(saved.baseBackgroundColor, beforePreferences.baseBackgroundColor);
    assert.equal(saved.surfaceColor, beforePreferences.surfaceColor);
    assert.deepEqual(changedVariables(beforeVariables, appearance.variables()), accentVariables);
});

test("an unrelated appearance toggle changes no color output", () => {
    const appearance = createAppearance();
    const current = appearance.api.save({ ...appearance.api.load(), accentColor: "#43c58a" });
    const before = appearance.variables();
    const saved = appearance.api.save({ ...current, showMessageAvatarPresence: true });

    assert.equal(saved.showMessageAvatarPresence, true);
    assert.deepEqual(appearance.variables(), before);
});

test("reset restores the full canonical default palette", () => {
    const appearance = createAppearance();
    appearance.api.save({
        accentColor: "#43c58a", baseBackgroundColor: "#10141b", surfaceColor: "#181e28",
        showMessageAvatarPresence: true
    });
    const reset = appearance.api.reset();

    assert.deepEqual(plain(reset), {
        accentColor: canonical["--iridium-accent"],
        baseBackgroundColor: canonical["--iridium-bg-base"],
        surfaceColor: canonical["--iridium-bg-surface"],
        showMessageAvatarPresence: false
    });
    for (const name of paletteVariables) assert.equal(appearance.variables()[name], canonical[name]);
});

test("accent persistence round-trip retains all untouched preferences and CSS output", () => {
    const first = createAppearance();
    const saved = first.api.save({ ...first.api.load(), accentColor: "#43c58a" });
    const persisted = first.storage.get("iridium.appearance");
    const reloaded = createAppearance(persisted);

    assert.deepEqual(plain(reloaded.api.load()), plain(saved));
    assert.deepEqual(reloaded.variables(), first.variables());
    assert.equal(JSON.parse(persisted).baseBackgroundColor, canonical["--iridium-bg-base"]);
    assert.equal(JSON.parse(persisted).surfaceColor, canonical["--iridium-bg-surface"]);
});

test("legacy missing fields are filled individually without replacing known colors", () => {
    const legacy = JSON.stringify({ accentColor: "#43c58a", baseBackgroundColor: "#10141b" });
    const appearance = createAppearance(legacy);
    const migrated = appearance.api.load();

    assert.equal(migrated.accentColor, "#43c58a");
    assert.equal(migrated.baseBackgroundColor, "#10141b");
    assert.equal(migrated.surfaceColor, canonical["--iridium-bg-surface"]);
    assert.equal(migrated.showMessageAvatarPresence, false);
    assert.deepEqual(JSON.parse(appearance.storage.get("iridium.appearance")), plain(migrated));
});
