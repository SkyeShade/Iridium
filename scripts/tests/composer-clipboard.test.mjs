import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const source = readFileSync(new URL("../../Iridium.Web/wwwroot/js/chat.js", import.meta.url), "utf8");
const start = source.indexOf("const clipboardImageExtensions");
const end = source.indexOf("function isComposerEmoji", start);
assert.ok(start >= 0 && end > start, "Unable to locate the composer clipboard helpers.");

class TestFile {
    constructor(parts, name, options = {}) {
        this.parts = parts;
        this.name = name;
        this.type = options.type || "";
        this.lastModified = options.lastModified || 0;
        this.size = parts.reduce((sum, part) => sum + (part.size || part.length || 0), 0);
    }
}

class TestDataTransfer {
    constructor() {
        this.files = [];
        this.items = { add: file => this.files.push(file) };
    }
}

class TestEvent {
    constructor(type, options) { this.type = type; this.options = options; }
}

const context = { File: TestFile, DataTransfer: TestDataTransfer, Event: TestEvent, Date, Map, Set, String };
vm.createContext(context);
vm.runInContext(`${source.slice(start, end).replaceAll("export function", "function")}
globalThis.extension = clipboardFileExtension;
globalThis.fileName = clipboardFileName;
globalThis.files = composerClipboardFiles;
globalThis.stage = stageComposerFiles;`, context);

const instant = new Date(2026, 7, 24, 23, 32, 0);
const clipboardFile = (name, type, size = 12, lastModified = 10) => ({ name, type, size, lastModified });
const item = file => ({ kind: "file", getAsFile: () => file });

test("clipboard screenshot names retain their MIME-derived extension", () => {
    assert.equal(context.extension("image/png"), ".png");
    assert.equal(context.extension("image/jpeg"), ".jpg");
    assert.equal(context.extension("image/webp"), ".webp");
    assert.equal(context.extension("image/gif"), ".gif");
    assert.equal(context.fileName(clipboardFile("", "image/png"), instant),
        "pasted-image-2026-08-24-233200.png");
});

test("normal copied files preserve names, MIME types, and order", () => {
    const first = clipboardFile("notes.txt", "text/plain", 4, 1);
    const second = clipboardFile("photo.webp", "image/webp", 8, 2);
    const files = context.files({ items: [item(first), item(second)] }, instant);
    assert.deepEqual(Array.from(files, file => file.name), ["notes.txt", "photo.webp"]);
    assert.deepEqual(Array.from(files, file => file.type), ["text/plain", "image/webp"]);
});

test("a file representation wins over duplicate HTML and file-list representations", () => {
    const image = clipboardFile("capture.png", "image/png", 20, 3);
    const files = context.files({
        items: [item(image), { kind: "string", type: "text/html" }],
        files: [image]
    }, instant);
    assert.equal(files.length, 1);
    assert.equal(files[0].name, "capture.png");
});

test("duplicate file items collapse and directories are ignored", () => {
    const image = clipboardFile("capture.png", "image/png", 20, 3);
    assert.equal(context.files({ items: [item(image), item(image)] }, instant).length, 1);
    assert.equal(context.files({ items: [{
        kind: "file", webkitGetAsEntry: () => ({ isDirectory: true }), getAsFile: () => image
    }], files: [image] }, instant).length, 0);
});

test("text-only clipboard payloads expose no attachment files", () => {
    assert.equal(context.files({ items: [{ kind: "string", type: "text/plain" }], files: [] }, instant).length, 0);
});

test("clipboard files are staged through the existing file input in order", () => {
    const input = { files: [], events: [], dispatchEvent(event) { this.events.push(event); } };
    const root = { querySelector: selector => selector === 'input[type="file"]' ? input : null };
    const first = clipboardFile("first.png", "image/png", 4, 1);
    const second = clipboardFile("second.gif", "image/gif", 8, 2);

    const staged = context.stage(root, [first, second]);
    assert.ok(staged);
    assert.deepEqual(input.files.map(file => file.name), ["first.png", "second.gif"]);
    assert.equal(input.events.length, 0);
    staged.dispatch();
    assert.equal(input.events.length, 1);
    assert.equal(input.events[0].type, "change");
});
