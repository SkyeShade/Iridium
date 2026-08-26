import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const source = readFileSync(new URL("../../Iridium.Web/wwwroot/js/chat.js", import.meta.url), "utf8");
const start = source.indexOf("export function messageViewportBottomDistance");
const end = source.indexOf("export function wireChannelSorter", start);
assert.ok(start >= 0 && end > start, "Unable to locate message viewport anchor helpers.");

const helpers = source.slice(start, end).replaceAll("export ", "");
const context = vm.createContext({ Math });
vm.runInContext(`${helpers}\nthis.bottomDistance = messageViewportBottomDistance; this.anchoredTop = messageViewportAnchoredScrollTop;`, context);

test("message viewport preserves a bottom-relative position when its height shrinks", () => {
    const distance = context.bottomDistance(3000, 2100, 500);
    assert.equal(distance, 400);
    assert.equal(context.anchoredTop(3000, 260, distance), 2340);
});

test("latest remains pinned through keyboard open and close", () => {
    assert.equal(context.anchoredTop(3000, 260, 0), 2740);
    assert.equal(context.anchoredTop(3000, 500, 0), 2500);
});

test("anchor restoration clamps safely for short histories", () => {
    assert.equal(context.anchoredTop(200, 500, 800), 0);
    assert.equal(context.bottomDistance(200, 0, 500), 0);
});
