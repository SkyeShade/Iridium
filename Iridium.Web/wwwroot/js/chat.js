const composerHandlers = new WeakMap();
const composerActionButtonHandlers = new WeakMap();
const messageLongPressHandlers = new WeakMap();
const mobileMessageActionSheets = new WeakMap();
const horizontalWheelElements = new WeakSet();
const markdownSourceEditorHandlers = new WeakMap();
const channelSorters = new WeakMap();
const messageViewports = new WeakMap();
const searchAutocompleteHandlers = new WeakMap();
const mobileViewportLayoutEvent = "iridium-mobile-viewport-layout";

export function messageViewportBottomDistance(scrollHeight, scrollTop, clientHeight) {
    return Math.max(0, scrollHeight - scrollTop - clientHeight);
}

export function messageViewportAnchoredScrollTop(scrollHeight, clientHeight, bottomDistance) {
    const maximum = Math.max(0, scrollHeight - clientHeight);
    return Math.max(0, Math.min(maximum, scrollHeight - clientHeight - Math.max(0, bottomDistance)));
}

export function wireChannelSorter(root, dotNetReference, initialProjection) {
    if (!root || channelSorters.has(root)) return;
    let candidate = null;
    let active = false;
    let target = null;
    let committing = false;
    let projection = [];
    const gapIndicator = document.createElement("div");
    gapIndicator.className = "pointer-gap-indicator";
    gapIndicator.hidden = true;
    const currentTreeRoot = () => root.querySelector("[data-category-tree-root]");
    const ensureGapIndicator = () => {
        const tree = currentTreeRoot();
        if (tree && gapIndicator.parentElement !== tree) tree.appendChild(gapIndicator);
        return tree;
    };
    ensureGapIndicator();
    const setProjection = rows => {
        ensureGapIndicator();
        projection = Array.from(rows || []).map(row => ({
            itemId: String(row.itemId),
            itemType: typeof row.itemType === "number" ? (row.itemType === 0 ? "channel" : "category") : String(row.itemType).toLowerCase(),
            parentCategoryId: row.parentCategoryId ? String(row.parentCategoryId) : "",
            depth: Number(row.depth), positionWithinParent: Number(row.positionWithinParent),
            flatVisibleIndex: Number(row.flatVisibleIndex), subtreeEndIndex: Number(row.subtreeEndIndex),
            subtreeHeight: Number(row.subtreeHeight || 1)
        }));
    };
    setProjection(initialProjection);
    const targetClasses = ["pointer-drop-inside", "pointer-drop-invalid", "pointer-drop-parent"];
    const clearTarget = () => {
        root.querySelectorAll(targetClasses.map(value => `.${value}`).join(",")).forEach(element =>
            targetClasses.forEach(value => element.classList.remove(value)));
        gapIndicator.hidden = true;
        root.removeAttribute("data-drop-intent");
        target = null;
    };
    const rowFor = element => projection.find(row => row.itemId === element?.dataset.sidebarId &&
        row.itemType === element?.dataset.sidebarKind);
    const elementFor = row => Array.from(root.querySelectorAll("[data-sidebar-item]"))
        .find(element => element.dataset.sidebarId === row?.itemId && element.dataset.sidebarKind === row?.itemType);
    const siblingsFor = (parentCategoryId, excluded) => projection.filter(row =>
        row.parentCategoryId === (parentCategoryId || "") &&
        !(row.itemId === excluded?.id && row.itemType === excluded?.kind));
    const pointerDown = event => {
        if (committing || event.button !== 0 || !(event.target instanceof Element)) return;
        const button = event.target.closest("button");
        if ((button && !button.classList.contains("category-toggle")) || event.target.closest(".row-menu, .category-menu")) return;
        const channel = event.target.closest("[data-iridium-channel-drag]");
        const categoryHandle = event.target.closest("[data-iridium-category-drag-handle]");
        if (channel && root.contains(channel)) {
            const row = rowFor(channel);
            candidate = {
                kind: "channel", element: channel, id: channel.dataset.iridiumChannelDrag,
                pointerId: event.pointerId, startY: event.clientY, startX: event.clientX, subtreeHeight: 1, row
            };
            return;
        }
        const category = categoryHandle?.closest?.("[data-iridium-category-drag]");
        if (category && root.contains(category)) {
            const node = category.closest("[data-category-node]");
            const row = rowFor(node);
            candidate = {
                kind: "category", element: node,
                id: category.dataset.iridiumCategoryDrag, pointerId: event.pointerId, startY: event.clientY,
                startX: event.clientX, subtreeHeight: row?.subtreeHeight || 1, row
            };
        }
    };
    const validDepth = destinationDepth => candidate.kind !== "category" ||
        destinationDepth + candidate.subtreeHeight - 1 <= 5;
    const markParent = parentCategoryId => {
        if (!parentCategoryId) return;
        const parentRow = projection.find(row => row.itemType === "category" && row.itemId === parentCategoryId);
        elementFor(parentRow)?.classList.add("pointer-drop-parent");
    };
    const gapY = gapIndex => {
        const treeRoot = ensureGapIndicator();
        if (!treeRoot) return 0;
        if (projection.length === 0) return 0;
        if (gapIndex < projection.length) {
            const rowElement = elementFor(projection[Math.max(0, gapIndex)]);
            if (rowElement) return rowElement.getBoundingClientRect().top - treeRoot.getBoundingClientRect().top;
        }
        const lastElement = elementFor(projection[projection.length - 1]);
        return lastElement ? lastElement.getBoundingClientRect().bottom - treeRoot.getBoundingClientRect().top : 0;
    };
    const visualSubtreeBottom = row => {
        if (!row) return 0;
        const endRow = projection[row.subtreeEndIndex] || row;
        const endElement = elementFor(endRow);
        if (!endElement) return 0;
        if (endRow.itemType === "category") {
            const heading = endElement.querySelector(":scope > .category-block > .category-heading");
            if (heading) return heading.getBoundingClientRect().bottom;
        }
        return endElement.getBoundingClientRect().bottom;
    };
    const relativeTreeY = clientY => {
        const tree = ensureGapIndicator();
        return tree ? clientY - tree.getBoundingClientRect().top : undefined;
    };
    const showGap = destination => {
        gapIndicator.hidden = false;
        gapIndicator.style.top = `${destination.indicatorY ?? gapY(destination.indicatorGapIndex)}px`;
        gapIndicator.style.setProperty("--indicator-depth", String(destination.targetDepth));
        markParent(destination.parentCategoryId);
        root.dataset.dropIntent = destination.visualIntent || destination.intent;
    };
    const showInvalid = element => { element?.classList.add("pointer-drop-invalid"); root.dataset.dropIntent = "invalid"; };
    const categoryEndTarget = zone => {
        const categoryId = zone?.dataset.categoryEndDrop;
        const row = projection.find(value => value.itemType === "category" && value.itemId === categoryId);
        if (!row) return null;
        const directChildren = siblingsFor(row.itemId, candidate)
            .slice().sort((left, right) => left.flatVisibleIndex - right.flatVisibleIndex);
        const heading = elementFor(row)?.querySelector(":scope > .category-block > .category-heading");
        const visualBottom = directChildren.length > 0
            ? visualSubtreeBottom(directChildren[directChildren.length - 1])
            : heading?.getBoundingClientRect().bottom;
        const destination = {
            parentCategoryId: row.itemId, insertIndex: siblingsFor(row.itemId, candidate).length,
            intent: "end", visualIntent: "inside-end", targetItemId: "", targetItemType: "",
            targetDepth: row.depth + 1, indicatorGapIndex: row.subtreeEndIndex + 1,
            indicatorY: visualBottom ? relativeTreeY(visualBottom) : undefined
        };
        if (!validDepth(destination.targetDepth + 1)) { showInvalid(zone); return null; }
        showGap(destination);
        return destination;
    };
    const categoryGapTarget = (item, row, headingRect, clientY) => {
        const directChildren = siblingsFor(row.itemId, candidate)
            .slice().sort((left, right) => left.flatVisibleIndex - right.flatVisibleIndex);
        const gaps = [{
            y: headingRect.bottom,
            parentCategoryId: row.itemId, insertIndex: 0, intent: "insideAtStart", visualIntent: "inside",
            targetItemId: row.itemId, targetItemType: row.itemType,
            targetDepth: row.depth + 1, indicatorGapIndex: row.flatVisibleIndex + 1,
            indicatorY: relativeTreeY(headingRect.bottom)
        }];
        for (let index = 0; index < directChildren.length; index++) {
            const child = directChildren[index];
            const next = directChildren[index + 1];
            const childBottom = visualSubtreeBottom(child);
            if (!childBottom) continue;
            gaps.push({
                y: childBottom,
                parentCategoryId: row.itemId, insertIndex: index + 1,
                intent: next ? "before" : "end",
                targetItemId: next?.itemId || "", targetItemType: next?.itemType || "",
                targetDepth: row.depth + 1,
                indicatorGapIndex: next?.flatVisibleIndex ?? row.subtreeEndIndex + 1,
                indicatorY: relativeTreeY(childBottom)
            });
        }
        const destination = gaps.reduce((nearest, gap) =>
            Math.abs(gap.y - clientY) < Math.abs(nearest.y - clientY) ? gap : nearest);
        if (!validDepth(destination.targetDepth + 1)) { showInvalid(item); return null; }
        showGap(destination);
        return destination;
    };
    const itemTarget = (item, clientY) => {
        const row = rowFor(item);
        if (!row || item === candidate.element || candidate.element.contains(item)) { showInvalid(item); return null; }
        let intent;
        if (row.itemType === "category") {
            const heading = item.querySelector(":scope > .category-block > .category-heading");
            const headingRect = heading?.getBoundingClientRect();
            if (!headingRect) return null;
            if (clientY > headingRect.bottom)
                return categoryGapTarget(item, row, headingRect, clientY);
            const ratio = (clientY - headingRect.top) / Math.max(1, headingRect.height);
            intent = ratio < .24 ? "before" : "inside";
        } else {
            const rect = item.getBoundingClientRect();
            intent = clientY < rect.top + rect.height / 2 ? "before" : "after";
        }
        if (intent === "inside") {
            const destination = {
                parentCategoryId: row.itemId, insertIndex: 0, intent: "insideAtStart", visualIntent: "inside",
                targetItemId: row.itemId, targetItemType: row.itemType,
                targetDepth: row.depth + 1, indicatorGapIndex: row.flatVisibleIndex + 1
            };
            if (!validDepth(destination.targetDepth + 1)) { showInvalid(item); return null; }
            showGap(destination);
            return destination;
        }
        const siblings = siblingsFor(row.parentCategoryId, candidate);
        const siblingIndex = siblings.findIndex(value => value.itemId === row.itemId && value.itemType === row.itemType);
        if (siblingIndex < 0) return null;
        const destination = {
            parentCategoryId: row.parentCategoryId,
            insertIndex: siblingIndex + (intent === "after" ? 1 : 0), intent,
            targetItemId: row.itemId, targetItemType: row.itemType,
            targetDepth: row.depth,
            indicatorGapIndex: intent === "after" ? row.subtreeEndIndex + 1 : row.flatVisibleIndex,
            indicatorY: intent === "after" ? relativeTreeY(visualSubtreeBottom(row)) : undefined
        };
        if (!validDepth(destination.targetDepth + 1)) { showInvalid(item); return null; }
        showGap(destination);
        return destination;
    };
    const rootEndTarget = () => {
        const siblings = siblingsFor("", candidate);
        const destination = { parentCategoryId: "", insertIndex: siblings.length, intent: "end",
            targetDepth: 0, indicatorGapIndex: projection.length, targetItemId: "", targetItemType: "" };
        if (!validDepth(1)) { showInvalid(currentTreeRoot()); return null; }
        showGap(destination);
        return destination;
    };
    const pointerMove = event => {
        if (!candidate || event.pointerId !== candidate.pointerId) return;
        if (!active) {
            if (Math.hypot(event.clientY - candidate.startY, event.clientX - candidate.startX) < 6) return;
            active = true;
            try { candidate.element.setPointerCapture?.(event.pointerId); } catch { }
            candidate.element.classList.add("pointer-dragging");
            root.classList.add("pointer-sorting");
        }
        event.preventDefault();
        clearTarget();
        const hit = document.elementFromPoint(event.clientX, event.clientY);
        const endZone = hit?.closest?.("[data-category-end-drop]");
        if (endZone && root.contains(endZone)) {
            target = categoryEndTarget(endZone);
            return;
        }
        const item = hit?.closest?.("[data-sidebar-item]");
        target = item && root.contains(item) ? itemTarget(item, event.clientY) : rootEndTarget();
    };
    const finish = async event => {
        if (!candidate || event.pointerId !== candidate.pointerId) return;
        const dragged = candidate;
        const destination = target;
        const shouldCommit = active && destination;
        candidate = null;
        dragged.element.classList.remove("pointer-dragging");
        root.classList.remove("pointer-sorting");
        clearTarget();
        if (!active) return;
        active = false;
        event.preventDefault();
        const suppressClick = click => { click.preventDefault(); click.stopPropagation(); };
        root.addEventListener("click", suppressClick, { capture: true, once: true });
        if (!shouldCommit) return;
        committing = true;
        try {
            if (dragged.kind === "category") {
                await dotNetReference.invokeMethodAsync("CommitCategoryDropAsync", dragged.id,
                    destination.parentCategoryId, destination.targetItemId, destination.targetItemType, destination.intent);
            } else {
                await dotNetReference.invokeMethodAsync("CommitChannelDropAsync", dragged.id,
                    destination.parentCategoryId, destination.targetItemId, destination.targetItemType, destination.intent);
            }
        } finally {
            committing = false;
        }
    };
    const cancel = event => {
        if (!candidate || event.pointerId !== candidate.pointerId) return;
        candidate.element.classList.remove("pointer-dragging");
        candidate = null;
        active = false;
        root.classList.remove("pointer-sorting");
        clearTarget();
    };

    root.addEventListener("pointerdown", pointerDown);
    window.addEventListener("pointermove", pointerMove, { passive: false });
    window.addEventListener("pointerup", finish);
    window.addEventListener("pointercancel", cancel);
    channelSorters.set(root, { pointerDown, pointerMove, finish, cancel, setProjection, gapIndicator });
}

export function updateChannelSorterProjection(root, projection) {
    channelSorters.get(root)?.setProjection(projection);
}

export function unwireChannelSorter(root) {
    const handlers = root ? channelSorters.get(root) : null;
    if (!handlers) return;
    root.removeEventListener("pointerdown", handlers.pointerDown);
    window.removeEventListener("pointermove", handlers.pointerMove);
    window.removeEventListener("pointerup", handlers.finish);
    window.removeEventListener("pointercancel", handlers.cancel);
    handlers.gapIndicator?.remove();
    channelSorters.delete(root);
}

function composerViewportHeight(textarea) {
    const channelView = textarea.closest(".channel-view, .direct-message-view");
    return channelView?.clientHeight || window.innerHeight;
}

function resizeTextarea(textarea, viewportRatio, maximumPixels = Number.POSITIVE_INFINITY) {
    if (!textarea) return;
    const styles = getComputedStyle(textarea);
    const minimumHeight = Number.parseFloat(styles.minHeight) || 0;
    const responsiveMaximum = Math.floor(composerViewportHeight(textarea) * viewportRatio);
    const maximumHeight = Math.max(minimumHeight, Math.min(responsiveMaximum, maximumPixels));

    textarea.style.height = "auto";
    const contentHeight = textarea.scrollHeight;
    textarea.style.height = `${Math.min(maximumHeight, Math.max(minimumHeight, contentHeight))}px`;
    textarea.style.maxHeight = `${maximumHeight}px`;
    textarea.style.overflowY = contentHeight > maximumHeight + 1 ? "auto" : "hidden";
}

export function resizeComposer(textarea) {
    const configuredRatio = Number.parseFloat(getComputedStyle(textarea).getPropertyValue('--composer-viewport-ratio'));
    resizeTextarea(textarea, Number.isFinite(configuredRatio) ? configuredRatio : 0.5);
    syncComposerPreview(textarea);
}

export function syncComposerPreview(textarea) {
    const preview = textarea?.closest(".composer-editor, .markdown-source-editor")
        ?.querySelector(".composer-highlight, .markdown-source-highlight");
    if (!preview) return;
    preview.scrollTop = textarea.scrollTop;
    preview.scrollLeft = textarea.scrollLeft;
}

export function focusComposer(textarea) {
    if (!textarea) return;
    textarea.focus({ preventScroll: true });
    setComposerCaret(textarea, composerSnapshot(textarea).content.length);
    window.dispatchEvent(new Event("iridium-composer-focus"));
}

export function composerCaret(textarea) {
    if (!textarea) return 0;
    return composerSelectionOffset(textarea);
}

export function focusComposerAt(textarea, position) {
    if (!textarea) return;
    textarea.focus({ preventScroll: true });
    setComposerCaret(textarea, position);
    window.dispatchEvent(new Event("iridium-composer-focus"));
}

export function updateComposerEmojiRanges(textarea, ranges) {
    // Compatibility no-op. Custom emoji are now real contenteditable=false nodes, not text ranges.
}

const composerObjectCharacter = "\uFFFC";
const composerPlaceholderCharacters = /[\u200B\uFEFF]/gu;
const clipboardImageExtensions = new Map([
    ["image/png", ".png"], ["image/jpeg", ".jpg"], ["image/webp", ".webp"],
    ["image/gif", ".gif"], ["image/avif", ".avif"], ["image/svg+xml", ".svg"]
]);

export function clipboardFileExtension(contentType) {
    const normalized = String(contentType || "").trim().toLowerCase();
    if (clipboardImageExtensions.has(normalized)) return clipboardImageExtensions.get(normalized);
    const subtype = normalized.startsWith("image/") ? normalized.slice(6).split("+")[0] : "";
    return /^[a-z0-9]+$/.test(subtype) ? `.${subtype}` : ".bin";
}

export function clipboardFileName(file, now = new Date()) {
    if (String(file?.name || "").trim()) return file.name;
    const pad = value => String(value).padStart(2, "0");
    const stamp = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}-` +
        `${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`;
    return `${String(file?.type || "").toLowerCase().startsWith("image/") ? "pasted-image" : "pasted-file"}-${stamp}${clipboardFileExtension(file?.type)}`;
}

export function composerClipboardFiles(clipboardData, now = new Date()) {
    if (!clipboardData) return [];
    const fileItems = Array.from(clipboardData.items || []).filter(item => item.kind === "file");
    let sawDirectory = false;
    const itemFiles = fileItems.map(item => {
        if (item.webkitGetAsEntry?.()?.isDirectory) { sawDirectory = true; return null; }
        return item.getAsFile?.();
    }).filter(Boolean);
    const exposed = itemFiles.length ? itemFiles : sawDirectory ? [] : Array.from(clipboardData.files || []);
    const seen = new Set();
    return exposed.filter(file => {
        if (seen.has(file)) return false;
        seen.add(file); return true;
    }).map(file => {
        const name = clipboardFileName(file, now);
        if (name === file.name) return file;
        return new File([file], name, { type: file.type || "application/octet-stream", lastModified: file.lastModified || now.getTime() });
    });
}

function stageComposerFiles(composerRoot, files) {
    const input = composerRoot?.querySelector('input[type="file"]');
    if (!input || !files?.length || typeof DataTransfer !== "function") return null;
    try {
        const transfer = new DataTransfer();
        for (const file of files) transfer.items.add(file);
        input.files = transfer.files;
        return { input, dispatch: () => input.dispatchEvent(new Event("change", { bubbles: true })) };
    } catch { return null; }
}

function queueComposerFiles(composerRoot, files) {
    const staged = stageComposerFiles(composerRoot, files);
    if (!staged) return false;
    staged.dispatch();
    return true;
}

function isComposerEmoji(node) { return node?.nodeType === Node.ELEMENT_NODE && node.classList?.contains("composer-inline-emoji"); }
function composerNodeLength(node) {
    if (!node) return 0;
    if (node.nodeType === Node.TEXT_NODE) return node.data.length;
    if (isComposerEmoji(node)) return 1;
    if (node.nodeName === "BR") return 1;
    return Array.from(node.childNodes).reduce((sum, child) => sum + composerNodeLength(child), 0);
}
function appendComposerSnapshot(node, result) {
    if (node.nodeType === Node.TEXT_NODE) { result.content += node.data; return; }
    if (isComposerEmoji(node)) {
        result.tokens.push({ start: result.content.length, emojiId: node.dataset.emojiId || null,
            name: node.dataset.name, communityId: node.dataset.communityId || null,
            standardArtworkKey: node.dataset.standardArtworkKey || null, glyph: node.dataset.glyph || null });
        result.content += composerObjectCharacter;
        return;
    }
    if (node.nodeName === "BR") { result.content += "\n"; return; }
    for (const child of node.childNodes) appendComposerSnapshot(child, result);
}
function composerSnapshot(editor) {
    const result = { content: "", caret: 0, tokens: [] };
    if (!editor) return result;
    for (const child of editor.childNodes) appendComposerSnapshot(child, result);
    // Chromium represents an emptied contenteditable with a lone <br> (and some engines may
    // retain a zero-width caret sentinel). Those DOM placeholders are not authored Markdown.
    // Iridium-authored newlines are text nodes, so a placeholder-only DOM can safely collapse
    // to the canonical empty source without changing multiline source/caret mapping.
    const placeholderOnly = result.tokens.length === 0 && result.content.length > 0 &&
        Array.from(editor.childNodes).every(composerPlaceholderNode);
    if (placeholderOnly) {
        result.content = "";
        result.caret = 0;
        return result;
    }
    result.caret = composerSelectionOffset(editor);
    return result;
}
function composerPlaceholderNode(node) {
    if (node.nodeType === Node.TEXT_NODE)
        return node.data.replace(composerPlaceholderCharacters, "").length === 0;
    if (isComposerEmoji(node)) return false;
    if (node.nodeName === "BR") return true;
    return Array.from(node.childNodes).every(composerPlaceholderNode);
}
function composerOffsetWithin(root, target, targetOffset) {
    let total = 0, found = false;
    const visit = node => {
        if (found) return;
        if (node === target) {
            if (node.nodeType === Node.TEXT_NODE) total += Math.min(targetOffset, node.data.length);
            else for (let i = 0; i < Math.min(targetOffset, node.childNodes.length); i++) total += composerNodeLength(node.childNodes[i]);
            found = true; return;
        }
        if (node.nodeType === Node.TEXT_NODE || isComposerEmoji(node) || node.nodeName === "BR") { total += composerNodeLength(node); return; }
        for (const child of node.childNodes) visit(child);
    };
    visit(root);
    return total;
}
function composerSelectionOffset(editor) {
    const selection = window.getSelection();
    if (!selection?.rangeCount || !editor.contains(selection.focusNode)) return composerNodeLength(editor);
    return composerOffsetWithin(editor, selection.focusNode, selection.focusOffset);
}
function composerPoint(editor, requested) {
    let remaining = Math.max(0, Math.min(Number(requested) || 0, composerNodeLength(editor)));
    const find = node => {
        if (node.nodeType === Node.TEXT_NODE) {
            if (remaining <= node.data.length) return { node, offset: remaining };
            remaining -= node.data.length; return null;
        }
        if (isComposerEmoji(node) || node.nodeName === "BR") {
            const parent = node.parentNode, index = Array.prototype.indexOf.call(parent.childNodes, node);
            if (remaining === 0) return { node: parent, offset: index };
            if (remaining === 1) return { node: parent, offset: index + 1 };
            remaining--; return null;
        }
        for (const child of node.childNodes) { const point = find(child); if (point) return point; }
        return null;
    };
    return find(editor) || { node: editor, offset: editor.childNodes.length };
}
function setComposerCaret(editor, position) {
    const point = composerPoint(editor, position), selection = window.getSelection(), range = document.createRange();
    range.setStart(point.node, point.offset); range.collapse(true); selection.removeAllRanges(); selection.addRange(range);
}
function createComposerEmoji(token) {
    const span = document.createElement("span");
    span.className = "composer-inline-emoji inline-emoji-token"; span.contentEditable = "false";
    if (token.emojiId) span.dataset.emojiId = token.emojiId;
    if (token.communityId) span.dataset.communityId = token.communityId;
    if (token.standardArtworkKey) span.dataset.standardArtworkKey = token.standardArtworkKey;
    if (token.glyph) span.dataset.glyph = token.glyph;
    span.dataset.name = token.name;
    span.setAttribute("role", "img"); span.setAttribute("aria-label", `:${token.name}:`);
    const image = document.createElement("img"); image.src = token.mediaUrl; image.alt = `:${token.name}:`; image.draggable = false;
    if (token.standardArtworkKey) image.addEventListener("error", () => {
        image.remove(); span.classList.add("composer-standard-fallback"); span.textContent = token.glyph || "";
    }, { once:true });
    span.appendChild(image); return span;
}
function replaceComposerRange(editor, start, end, replacement) {
    const first = composerPoint(editor, start), last = composerPoint(editor, end), range = document.createRange();
    const replacementLength = composerNodeLength(replacement);
    range.setStart(first.node, first.offset); range.setEnd(last.node, last.offset); range.deleteContents();
    range.insertNode(replacement); setComposerCaret(editor, start + replacementLength);
    editor.normalize(); resizeComposer(editor); return composerSnapshot(editor);
}
export function insertComposerEmoji(editor, token, start = null, end = null, addSpace = true) {
    const caret = composerSelectionOffset(editor), from = start === null ? caret : start, to = end === null ? caret : end;
    const fragment = document.createDocumentFragment(); fragment.appendChild(createComposerEmoji(token));
    if (addSpace) fragment.appendChild(document.createTextNode(" "));
    return replaceComposerRange(editor, from, to, fragment);
}
export function replaceComposerText(editor, start, end, text) {
    return replaceComposerRange(editor, start, end, document.createTextNode(text));
}
export function clearComposer(editor) { if (editor) editor.replaceChildren(); resizeComposer(editor); }
export function setComposerText(editor, text) {
    if (!editor) return { content: "", caret: 0, tokens: [] };
    editor.replaceChildren(document.createTextNode(text || ""));
    resizeComposer(editor);
    setComposerCaret(editor, composerNodeLength(editor));
    return composerSnapshot(editor);
}

export function setComposerDocument(editor, text, tokens = []) {
    let snapshot = setComposerText(editor, text);
    for (const token of Array.from(tokens || []).sort((left, right) => right.start - left.start)) {
        snapshot = replaceComposerRange(editor, token.start, token.start + 1, createComposerEmoji(token));
    }
    return snapshot;
}

export function wireSearchAutocomplete(input, dotNetReference) {
    if (!input || searchAutocompleteHandlers.has(input)) return;
    const keydown = async event => {
        const suggestions = input.closest(".message-search")?.querySelector(".search-suggestions");
        if (event.key === "Escape") {
            event.preventDefault();
            await dotNetReference.invokeMethodAsync("HandleSearchKeyAsync", event.key);
            return;
        }
        if (!suggestions || !["ArrowDown", "ArrowUp", "Enter", "Tab"].includes(event.key)) return;
        event.preventDefault();
        await dotNetReference.invokeMethodAsync("HandleSearchKeyAsync", event.key);
    };
    const shortcut = async event => {
        if (!(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== "f" || !input.isConnected) return;
        event.preventDefault();
        input.focus({ preventScroll: true });
        await dotNetReference.invokeMethodAsync("OpenSearchFromShortcutAsync");
    };
    const outside = async event => {
        if (!input.closest(".message-search")?.contains(event.target))
            await dotNetReference.invokeMethodAsync("CloseSearchFromOutsideAsync");
    };
    input.addEventListener("keydown", keydown);
    window.addEventListener("keydown", shortcut);
    document.addEventListener("pointerdown", outside);
    searchAutocompleteHandlers.set(input, { keydown, shortcut, outside });
}

export function unwireSearchAutocomplete(input) {
    const handlers = input ? searchAutocompleteHandlers.get(input) : null;
    if (!handlers) return;
    input.removeEventListener("keydown", handlers.keydown);
    window.removeEventListener("keydown", handlers.shortcut);
    document.removeEventListener("pointerdown", handlers.outside);
    searchAutocompleteHandlers.delete(input);
}

const friendAutocompleteHandlers = new WeakMap();
export function wireFriendAutocomplete(input, root, dotNetReference) {
    if (!input || friendAutocompleteHandlers.has(input)) return;
    const keydown = async event => {
        const suggestions = root?.querySelector(".friend-suggestions");
        if (!["ArrowDown", "ArrowUp", "Enter", "Escape"].includes(event.key) ||
            (!suggestions && event.key !== "Escape")) return;
        event.preventDefault();
        await dotNetReference.invokeMethodAsync("HandleFriendSearchKeyAsync", event.key);
    };
    const outside = async event => {
        if (!root?.contains(event.target)) await dotNetReference.invokeMethodAsync("CloseFriendSearchAsync");
    };
    input.addEventListener("keydown", keydown);
    document.addEventListener("pointerdown", outside);
    friendAutocompleteHandlers.set(input, { keydown, outside });
}

export function unwireFriendAutocomplete(input) {
    const handlers = input ? friendAutocompleteHandlers.get(input) : null;
    if (!handlers) return;
    input.removeEventListener("keydown", handlers.keydown);
    document.removeEventListener("pointerdown", handlers.outside);
    friendAutocompleteHandlers.delete(input);
}

export function profileCardPosition(clientX, clientY, context) {
    const target = document.elementFromPoint(clientX, clientY);
    const rootFontSize = Number.parseFloat(getComputedStyle(document.documentElement).fontSize) || 16;
    const cardWidth = 18.5 * rootFontSize;
    if (context === "member") {
        const sidebar = target?.closest?.(".context-sidebar") || document.querySelector(".context-sidebar");
        const row = target?.closest?.(".member-entry");
        const sidebarRect = sidebar?.getBoundingClientRect();
        const rowRect = row?.getBoundingClientRect();
        return { x: (sidebarRect?.left ?? clientX) - cardWidth, y: rowRect?.top ?? clientY };
    }

    const messageRow = target?.closest?.(".message-row");
    const avatar = messageRow?.querySelector?.(".message-avatar");
    const rowRect = messageRow?.getBoundingClientRect();
    const avatarRect = avatar?.getBoundingClientRect();
    return { x: (avatarRect?.right ?? clientX) - 4, y: rowRect?.top ?? clientY };
}

export function wireMarkdownSourceEditor(editor, dotNetReference, options = {}) {
    if (!editor || markdownSourceEditorHandlers.has(editor)) return;
    let handlingShortcut = false;
    let lastAccepted = composerSnapshot(editor);
    const notify = async snapshot => {
        lastAccepted = snapshot;
        await dotNetReference.invokeMethodAsync("MarkdownSourceChangedAsync", snapshot);
    };
    const keydown = async event => {
        if (event.isComposing || event.keyCode === 229 || handlingShortcut) return;
        const selection = window.getSelection();
        if (selection?.isCollapsed && editor.contains(selection.focusNode)) {
            const caret = composerSelectionOffset(editor), snapshot = composerSnapshot(editor);
            const before = snapshot.tokens.find(token => token.start + 1 === caret);
            const after = snapshot.tokens.find(token => token.start === caret);
            if ((event.key === "Backspace" && before) || (event.key === "Delete" && after)) {
                event.preventDefault();
                const token = event.key === "Backspace" ? before : after;
                await notify(replaceComposerRange(editor, token.start, token.start + 1, document.createTextNode("")));
                return;
            }
            if (event.key === "ArrowLeft" && before) { event.preventDefault(); setComposerCaret(editor, before.start); return; }
            if (event.key === "ArrowRight" && after) { event.preventDefault(); setComposerCaret(editor, after.start + 1); return; }
        }
        const cancel = options.canCancel && event.key === "Escape";
        const submit = options.submitOnEnter && event.key === "Enter" && !event.shiftKey;
        if (cancel || submit) {
            event.preventDefault();
            handlingShortcut = true;
            try {
                await dotNetReference.invokeMethodAsync(cancel ? "CancelMarkdownSourceAsync" : "SubmitMarkdownSourceAsync");
            } finally { handlingShortcut = false; }
            return;
        }
        if (event.key === "Enter" && event.shiftKey) {
            event.preventDefault();
            const caret = composerSelectionOffset(editor);
            const snapshot = replaceComposerRange(editor, caret, caret, document.createTextNode("\n"));
            await notify(snapshot);
        }
    };
    const input = async () => {
        let snapshot = composerSnapshot(editor);
        if (Number.isFinite(options.maxLength) && snapshot.content.length > options.maxLength) {
            snapshot = setComposerDocument(editor, lastAccepted.content, lastAccepted.tokens);
            focusComposerAt(editor, Math.min(lastAccepted.caret, lastAccepted.content.length));
        } else await notify(snapshot);
        resizeComposer(editor);
    };
    const paste = async event => {
        event.preventDefault();
        const text = event.clipboardData?.getData("text/plain") || "";
        const selection = window.getSelection();
        let start = composerSelectionOffset(editor), end = start;
        if (selection?.rangeCount && !selection.isCollapsed) {
            const range = selection.getRangeAt(0);
            start = composerOffsetWithin(editor, range.startContainer, range.startOffset);
            end = composerOffsetWithin(editor, range.endContainer, range.endOffset);
        }
        const allowed = Number.isFinite(options.maxLength)
            ? text.slice(0, Math.max(0, options.maxLength - (lastAccepted.content.length - (end - start))))
            : text;
        await notify(replaceComposerRange(editor, start, end, document.createTextNode(allowed)));
    };
    const scroll = () => syncComposerPreview(editor);
    const preview = editor.closest(".markdown-source-editor")?.querySelector(".markdown-source-highlight");
    const highlightObserver = preview && typeof MutationObserver !== "undefined"
        ? new MutationObserver(() => syncComposerPreview(editor)) : null;
    markdownSourceEditorHandlers.set(editor, { keydown, input, paste, scroll, highlightObserver });
    editor.addEventListener("keydown", keydown);
    editor.addEventListener("input", input);
    editor.addEventListener("paste", paste);
    editor.addEventListener("scroll", scroll, { passive: true });
    highlightObserver?.observe(preview, { childList: true, subtree: true, characterData: true });
    resizeComposer(editor);
    if (options.autofocus) focusComposerAt(editor, lastAccepted.content.length);
}

export function unwireMarkdownSourceEditor(editor) {
    const handlers = editor ? markdownSourceEditorHandlers.get(editor) : null;
    if (!handlers) return;
    editor.removeEventListener("keydown", handlers.keydown);
    editor.removeEventListener("input", handlers.input);
    editor.removeEventListener("paste", handlers.paste);
    editor.removeEventListener("scroll", handlers.scroll);
    handlers.highlightObserver?.disconnect();
    markdownSourceEditorHandlers.delete(editor);
}

export function wireComposer(textarea, dotNetReference, composerRoot) {
    if (!textarea || composerHandlers.has(textarea)) return;
    let submitting = false;
    const state = {};
    const keydown = async event => {
        const selection = window.getSelection();
        if (selection?.isCollapsed && textarea.contains(selection.focusNode)) {
            const caret = composerSelectionOffset(textarea), snapshot = composerSnapshot(textarea);
        const before = snapshot.tokens.find(token => token.start + 1 === caret);
            const after = snapshot.tokens.find(token => token.start === caret);
            if ((event.key === "Backspace" && before) || (event.key === "Delete" && after)) {
                event.preventDefault();
                const token = event.key === "Backspace" ? before : after;
                replaceComposerRange(textarea, token.start, token.start + 1, document.createTextNode(""));
                await dotNetReference.invokeMethodAsync("ComposerDocumentChangedAsync", composerSnapshot(textarea));
                return;
            }
            if (event.key === "ArrowLeft" && before) { event.preventDefault(); setComposerCaret(textarea, before.start); return; }
            if (event.key === "ArrowRight" && after) { event.preventDefault(); setComposerCaret(textarea, after.start + 1); return; }
        }
        const mentionMenu = textarea.closest(".composer-shell")?.querySelector(".mention-suggestions");
        if (mentionMenu && ["ArrowDown", "ArrowUp", "Enter", "Tab", "Escape"].includes(event.key)) {
            event.preventDefault();
            await dotNetReference.invokeMethodAsync("HandleMentionKeyAsync", event.key);
            return;
        }
        if (event.key === "ArrowUp" && !event.isComposing) {
            const snapshot = composerSnapshot(textarea);
            if (snapshot.content.length === 0 && (!selection || selection.isCollapsed)) {
                event.preventDefault();
                await dotNetReference.invokeMethodAsync("EditLastMessageFromKeyboardAsync");
                return;
            }
        }
        if (event.key === "Enter" && event.shiftKey && !event.isComposing) {
            event.preventDefault();
            const caret = composerSelectionOffset(textarea);
            replaceComposerRange(textarea, caret, caret, document.createTextNode("\n"));
            await dotNetReference.invokeMethodAsync("ComposerDocumentChangedAsync", composerSnapshot(textarea));
            return;
        }
        if (event.key !== "Enter" || event.shiftKey || event.isComposing || event.keyCode === 229) return;
        event.preventDefault();
        if (submitting) return;
        submitting = true;
        try {
            await dotNetReference.invokeMethodAsync("SubmitFromKeyboardAsync");
            textarea.focus({ preventScroll: true });
        } catch (error) {
            console.error("Iridium message submission failed in the client.", error);
        } finally {
            submitting = false;
            requestAnimationFrame(() => resizeComposer(textarea));
        }
    };
    const input = async () => {
        resizeComposer(textarea);
        await dotNetReference.invokeMethodAsync("ComposerDocumentChangedAsync", composerSnapshot(textarea));
    };
    const copy = event => {
        const selection = window.getSelection();
        if (!selection?.rangeCount || selection.isCollapsed || !textarea.contains(selection.anchorNode)) return;
        const start = composerOffsetWithin(textarea, selection.getRangeAt(0).startContainer, selection.getRangeAt(0).startOffset);
        const end = composerOffsetWithin(textarea, selection.getRangeAt(0).endContainer, selection.getRangeAt(0).endOffset);
        const snapshot = composerSnapshot(textarea);
        let plain = snapshot.content.slice(start, end);
        for (const token of snapshot.tokens.filter(value => value.start >= start && value.start < end).sort((a,b) => b.start-a.start)) {
            const offset = token.start - start;
            const replacement = token.standardArtworkKey ? token.glyph : `:${token.name}:`;
            plain = plain.slice(0, offset) + replacement + plain.slice(offset + 1);
        }
        event.preventDefault(); event.clipboardData.setData("text/plain", plain);
    };
    const paste = async event => {
        const files = composerClipboardFiles(event.clipboardData);
        const stagedFiles = stageComposerFiles(composerRoot, files);
        if (stagedFiles) {
            event.preventDefault();
            const caret = composerSelectionOffset(textarea);
            await dotNetReference.invokeMethodAsync("PrepareAttachmentPasteAsync", caret);
            stagedFiles.dispatch();
            textarea.focus({ preventScroll: true });
            return;
        }
        event.preventDefault();
        const text = event.clipboardData?.getData("text/plain") || "";
        const selection = window.getSelection();
        let start = composerSelectionOffset(textarea), end = start;
        if (selection?.rangeCount && !selection.isCollapsed) {
            const range = selection.getRangeAt(0);
            start = composerOffsetWithin(textarea, range.startContainer, range.startOffset);
            end = composerOffsetWithin(textarea, range.endContainer, range.endOffset);
        }
        replaceComposerRange(textarea, start, end, document.createTextNode(text));
        await dotNetReference.invokeMethodAsync("ComposerDocumentChangedAsync", composerSnapshot(textarea));
    };
    const scroll = () => syncComposerPreview(textarea);
    const preview = textarea.closest(".composer-editor")?.querySelector(".composer-highlight, .markdown-source-highlight");
    const highlightObserver = preview && typeof MutationObserver !== "undefined"
        ? new MutationObserver(() => syncComposerPreview(textarea))
        : null;
    const observedViewport = textarea.closest(".channel-view, .direct-message-view");
    const observer = observedViewport && typeof ResizeObserver !== "undefined"
        ? new ResizeObserver(() => resizeComposer(textarea))
        : null;
    const dropRegion = composerRoot?.closest(".channel-view, .direct-message-view") || composerRoot;
    let dragDepth = 0;
    const setDragHighlight = active => {
        composerRoot?.classList.toggle("drag-over", active);
        dropRegion?.classList.toggle("file-drag-over", active);
    };
    const hasFiles = event => Array.from(event.dataTransfer?.types || []).includes("Files");
    const dragenter = event => {
        if (!hasFiles(event)) return;
        event.preventDefault();
        dragDepth++;
        setDragHighlight(true);
    };
    const dragover = event => {
        if (!hasFiles(event)) return;
        event.preventDefault();
        if (event.dataTransfer) event.dataTransfer.dropEffect = "copy";
    };
    const dragleave = event => {
        if (!hasFiles(event)) return;
        dragDepth = Math.max(0, dragDepth - 1);
        if (dragDepth === 0) setDragHighlight(false);
    };
    const drop = event => {
        if (!hasFiles(event)) return;
        event.preventDefault();
        dragDepth = 0;
        setDragHighlight(false);
        queueComposerFiles(composerRoot, Array.from(event.dataTransfer?.files || []));
    };

    Object.assign(state, { keydown, input, copy, paste, scroll, observer, highlightObserver, composerRoot, dropRegion, dragenter, dragover, dragleave, drop });
    composerHandlers.set(textarea, state);
    textarea.addEventListener("keydown", keydown);
    textarea.addEventListener("input", input);
    textarea.addEventListener("copy", copy);
    textarea.addEventListener("paste", paste);
    textarea.addEventListener("scroll", scroll, { passive: true });
    observer?.observe(observedViewport);
    highlightObserver?.observe(preview, { childList: true, subtree: true, characterData: true });
    dropRegion?.addEventListener("dragenter", dragenter);
    dropRegion?.addEventListener("dragover", dragover);
    dropRegion?.addEventListener("dragleave", dragleave);
    dropRegion?.addEventListener("drop", drop);
    resizeComposer(textarea);
}

export function unwireComposer(textarea) {
    const handlers = textarea ? composerHandlers.get(textarea) : null;
    if (!handlers) return;
    textarea.removeEventListener("keydown", handlers.keydown);
    textarea.removeEventListener("input", handlers.input);
    textarea.removeEventListener("copy", handlers.copy);
    textarea.removeEventListener("paste", handlers.paste);
    textarea.removeEventListener("scroll", handlers.scroll);
    handlers.observer?.disconnect();
    handlers.highlightObserver?.disconnect();
    handlers.dropRegion?.removeEventListener("dragenter", handlers.dragenter);
    handlers.dropRegion?.removeEventListener("dragover", handlers.dragover);
    handlers.dropRegion?.removeEventListener("dragleave", handlers.dragleave);
    handlers.dropRegion?.removeEventListener("drop", handlers.drop);
    handlers.composerRoot?.classList.remove("drag-over");
    handlers.dropRegion?.classList.remove("file-drag-over");
    composerHandlers.delete(textarea);
}

export function openComposerFilePicker(composerRoot) {
    composerRoot?.querySelector('input[type="file"]')?.click();
}

export const composerActionLongPressMilliseconds = 500;
export const messageActionLongPressMilliseconds = 550;
export const mobileLongPressMoveTolerance = 10;

export function longPressMovementExceeded(startX, startY, currentX, currentY,
    tolerance = mobileLongPressMoveTolerance) {
    return Math.hypot(currentX - startX, currentY - startY) > tolerance;
}

function wireTouchLongPress(root, { threshold, targetSelector, activeClass, invoke }) {
    let timer = 0;
    let candidate = null;
    let suppressTarget = null;
    let suppressPoint = null;
    let suppressUntil = 0;

    const targetFor = event => targetSelector ? event.target?.closest?.(targetSelector) : root;
    const clearCandidate = () => {
        if (timer) window.clearTimeout(timer);
        timer = 0;
        candidate?.target?.classList.remove(activeClass);
        candidate = null;
    };
    const down = event => {
        if ((event.pointerType !== "touch" && event.pointerType !== "pen") || event.button !== 0 ||
            !window.matchMedia("(max-width: 860px)").matches) return;
        const target = targetFor(event);
        if (!target) return;
        clearCandidate();
        candidate = { pointerId: event.pointerId, startX: event.clientX, startY: event.clientY, target };
        target.classList.add(activeClass);
        timer = window.setTimeout(async () => {
            if (!candidate) return;
            timer = 0;
            const activated = candidate;
            activated.target.classList.remove(activeClass);
            suppressTarget = activated.target;
            suppressPoint = { x: activated.startX, y: activated.startY };
            suppressUntil = Date.now() + 1000;
            candidate = null;
            try { await invoke(activated.target, activated.startX, activated.startY); }
            catch (error) { console.error("Could not complete the long-press action.", error); }
        }, threshold);
    };
    const move = event => {
        if (!candidate || event.pointerId !== candidate.pointerId) return;
        if (longPressMovementExceeded(candidate.startX, candidate.startY, event.clientX, event.clientY))
            clearCandidate();
    };
    const up = event => {
        if (candidate && event.pointerId === candidate.pointerId) clearCandidate();
    };
    const pointercancel = () => clearCandidate();
    const click = event => {
        if (!suppressTarget || !suppressPoint || Date.now() > suppressUntil ||
            !suppressTarget.contains(event.target) ||
            longPressMovementExceeded(suppressPoint.x, suppressPoint.y, event.clientX, event.clientY, 24)) return;
        suppressTarget = null;
        suppressPoint = null;
        suppressUntil = 0;
        event.preventDefault();
        event.stopImmediatePropagation();
    };
    const contextmenu = event => {
        const target = targetFor(event);
        const recognized = candidate?.target === target ||
            (suppressTarget === target && Date.now() <= suppressUntil);
        if (!recognized) return;
        event.preventDefault();
        event.stopImmediatePropagation();
    };
    root.addEventListener("pointerdown", down);
    window.addEventListener("pointermove", move, { passive: true });
    window.addEventListener("pointerup", up);
    window.addEventListener("pointercancel", pointercancel);
    root.addEventListener("click", click, true);
    root.addEventListener("contextmenu", contextmenu);
    return {
        cancelPointer(pointerId) {
            if (candidate?.pointerId === pointerId) clearCandidate();
        },
        dispose() {
            clearCandidate();
            root.removeEventListener("pointerdown", down);
            window.removeEventListener("pointermove", move);
            window.removeEventListener("pointerup", up);
            window.removeEventListener("pointercancel", pointercancel);
            root.removeEventListener("click", click, true);
            root.removeEventListener("contextmenu", contextmenu);
        }
    };
}

export function wireComposerActionButton(button, dotNetReference, enableModeSwitch = true) {
    if (!button || !enableModeSwitch || composerActionButtonHandlers.has(button)) return;
    composerActionButtonHandlers.set(button, wireTouchLongPress(button, {
        threshold: composerActionLongPressMilliseconds,
        activeClass: "long-pressing",
        invoke: () => dotNetReference.invokeMethodAsync("OpenComposerActionModeMenuFromLongPressAsync")
    }));
}

export function unwireComposerActionButton(button) {
    const handlers = button ? composerActionButtonHandlers.get(button) : null;
    if (!handlers) return;
    handlers.dispose();
    composerActionButtonHandlers.delete(button);
}

export function wireMessageLongPress(container, dotNetReference) {
    if (!container || messageLongPressHandlers.has(container)) return;
    let menuOpened = false;
    const gesture = wireTouchLongPress(container, {
        threshold: messageActionLongPressMilliseconds,
        targetSelector: "article.message-row[data-message-id]",
        activeClass: "message-long-pressing",
        invoke: async target => {
            await dotNetReference.invokeMethodAsync(
                "OpenMessageActionsFromLongPressAsync", target.dataset.messageId);
            window.dispatchEvent(new CustomEvent("iridium-mobile-message-actions-open"));
            menuOpened = true;
        }
    });
    const keydown = event => {
        if (!menuOpened || event.key !== "Escape") return;
        menuOpened = false;
        dotNetReference.invokeMethodAsync("CloseMessageActionsFromLongPressAsync");
    };
    const navigationSwipeClaimed = event => gesture.cancelPointer(event.detail?.pointerId);
    window.addEventListener("keydown", keydown);
    window.addEventListener("iridium-mobile-navigation-swipe-claimed", navigationSwipeClaimed);
    messageLongPressHandlers.set(container, {
        dispose() {
            gesture.dispose();
            window.removeEventListener("keydown", keydown);
            window.removeEventListener("iridium-mobile-navigation-swipe-claimed", navigationSwipeClaimed);
        }
    });
}

export function unwireMessageLongPress(container) {
    const handlers = container ? messageLongPressHandlers.get(container) : null;
    if (!handlers) return;
    handlers.dispose();
    messageLongPressHandlers.delete(container);
}

export function animateMobileMessageActionSheetClose(backdrop, sheet) {
    if (!backdrop || !sheet) return Promise.resolve();
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) return Promise.resolve();
    return new Promise(resolve => {
        let finished = false;
        const finish = () => {
            if (finished) return;
            finished = true;
            sheet.removeEventListener("transitionend", finish);
            resolve();
        };
        sheet.style.transition = "transform 180ms cubic-bezier(.4,0,1,1)";
        backdrop.style.transition = "background 180ms ease";
        requestAnimationFrame(() => {
            sheet.style.transform = "translate3d(0,100%,0)";
            backdrop.style.background = "transparent";
        });
        sheet.addEventListener("transitionend", finish, { once: true });
        window.setTimeout(finish, 230);
    });
}

export const mobileMessageSheetVelocityThreshold = .85;
export const mobileMessageSheetSnapMilliseconds = 190;

export function mobileMessageSheetDragOffset(startY, currentY) {
    return Math.max(0, currentY - startY);
}

export function mobileMessageSheetDismissDistance(sheetHeight) {
    return Math.max(90, Math.min(140, sheetHeight * .3));
}

export function mobileMessageSheetBackdropOpacity(distance, dismissDistance, baseOpacity = .62) {
    const progress = Math.max(0, Math.min(1, distance / Math.max(1, dismissDistance)));
    return baseOpacity * (1 - progress * .6);
}

export function shouldDismissMobileMessageActionSheet(distance, sheetHeight, velocity) {
    return distance >= mobileMessageSheetDismissDistance(sheetHeight) ||
        (distance >= 36 && velocity >= mobileMessageSheetVelocityThreshold);
}

export function wireMobileMessageActionSheet(backdrop, sheet, dragHandle, dotNetReference) {
    if (!backdrop || !sheet || !dragHandle || mobileMessageActionSheets.has(backdrop)) return;
    const scrollContainer = backdrop.closest(".message-list");
    let drag = null;
    let dismissing = false;
    let visualFrame = 0;
    let suppressClick = null;
    scrollContainer?.classList.add("message-action-sheet-open");

    const releasePointer = pointerId => {
        try {
            if (dragHandle.hasPointerCapture?.(pointerId)) dragHandle.releasePointerCapture(pointerId);
        } catch { }
    };
    const writeDragVisual = () => {
        visualFrame = 0;
        if (!drag) return;
        drag.renderedY = drag.desiredY;
        sheet.style.transform = `translate3d(0,${drag.renderedY}px,0)`;
        const opacity = mobileMessageSheetBackdropOpacity(drag.renderedY, drag.dismissDistance);
        backdrop.style.background = `rgba(3,5,9,${opacity})`;
    };
    const scheduleDragVisual = () => {
        if (!visualFrame) visualFrame = requestAnimationFrame(writeDragVisual);
    };
    const flushDragVisual = () => {
        if (visualFrame) cancelAnimationFrame(visualFrame);
        visualFrame = 0;
        writeDragVisual();
    };
    const releaseTransition = () => {
        const duration = window.matchMedia("(prefers-reduced-motion: reduce)").matches
            ? 1 : mobileMessageSheetSnapMilliseconds;
        sheet.style.transition = `transform ${duration}ms cubic-bezier(.2,.75,.25,1)`;
        backdrop.style.transition = `background ${duration}ms ease`;
    };
    const snapBack = () => {
        releaseTransition();
        visualFrame = requestAnimationFrame(() => {
            visualFrame = 0;
            sheet.style.transform = "translate3d(0,0,0)";
            backdrop.style.background = "rgba(3,5,9,.62)";
        });
    };

    const dismiss = async () => {
        if (dismissing) return;
        dismissing = true;
        await animateMobileMessageActionSheetClose(backdrop, sheet);
        await dotNetReference.invokeMethodAsync("DismissFromGestureAsync");
    };
    const down = event => {
        if (event.button !== 0) return;
        const sheetHeight = sheet.getBoundingClientRect().height;
        drag = { pointerId: event.pointerId, startY: event.clientY, lastY: event.clientY,
            lastAt: performance.now(), velocity: 0, desiredY: 0, renderedY: 0, maxY: 0,
            sheetHeight, dismissDistance: mobileMessageSheetDismissDistance(sheetHeight) };
        try { dragHandle.setPointerCapture?.(event.pointerId); } catch { }
        sheet.style.animation = "none";
        backdrop.style.animation = "none";
        sheet.style.transition = "none";
        backdrop.style.transition = "none";
    };
    const move = event => {
        if (!drag || event.pointerId !== drag.pointerId) return;
        const now = performance.now();
        const elapsed = Math.max(1, now - drag.lastAt);
        drag.velocity = (event.clientY - drag.lastY) / elapsed;
        drag.lastY = event.clientY;
        drag.lastAt = now;
        drag.desiredY = mobileMessageSheetDragOffset(drag.startY, event.clientY);
        drag.maxY = Math.max(drag.maxY, drag.desiredY);
        scheduleDragVisual();
        event.preventDefault();
    };
    const end = event => {
        if (!drag || event.pointerId !== drag.pointerId) return;
        const now = performance.now();
        drag.velocity = (event.clientY - drag.lastY) / Math.max(1, now - drag.lastAt);
        drag.desiredY = mobileMessageSheetDragOffset(drag.startY, event.clientY);
        drag.maxY = Math.max(drag.maxY, drag.desiredY);
        flushDragVisual();
        const completed = drag;
        releasePointer(completed.pointerId);
        if (completed.maxY > mobileLongPressMoveTolerance)
            suppressClick = { x: event.clientX, y: event.clientY, until: Date.now() + 600 };
        const shouldDismiss = shouldDismissMobileMessageActionSheet(
            completed.desiredY, completed.sheetHeight, completed.velocity);
        drag = null;
        if (shouldDismiss) { void dismiss(); return; }
        snapBack();
    };
    const cancel = event => {
        if (!drag || (event.pointerId !== undefined && event.pointerId !== drag.pointerId)) return;
        flushDragVisual();
        releasePointer(drag.pointerId);
        drag = null;
        snapBack();
    };
    const click = event => {
        if (!suppressClick || Date.now() > suppressClick.until ||
            longPressMovementExceeded(suppressClick.x, suppressClick.y, event.clientX, event.clientY, 24)) return;
        suppressClick = null;
        event.preventDefault();
        event.stopImmediatePropagation();
    };
    const keydown = event => {
        if (event.key === "Escape") { event.preventDefault(); void dismiss(); return; }
        if (event.key !== "Tab") return;
        const focusable = Array.from(sheet.querySelectorAll("button:not(:disabled),[tabindex]:not([tabindex='-1'])"));
        if (!focusable.length) { event.preventDefault(); sheet.focus(); return; }
        const first = focusable[0], last = focusable[focusable.length - 1];
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    };
    dragHandle.addEventListener("pointerdown", down);
    window.addEventListener("pointermove", move, { passive: false });
    window.addEventListener("pointerup", end);
    window.addEventListener("pointercancel", cancel);
    window.addEventListener("keydown", keydown);
    sheet.addEventListener("click", click, true);
    sheet.querySelector("button")?.focus({ preventScroll: true });
    mobileMessageActionSheets.set(backdrop, {
        scrollContainer, dragHandle, sheet, down, move, end, cancel, keydown, click,
        cancelVisualFrame: () => { if (visualFrame) cancelAnimationFrame(visualFrame); }
    });
}

export function unwireMobileMessageActionSheet(backdrop) {
    const wired = backdrop ? mobileMessageActionSheets.get(backdrop) : null;
    if (!wired) return;
    wired.dragHandle.removeEventListener("pointerdown", wired.down);
    window.removeEventListener("pointermove", wired.move);
    window.removeEventListener("pointerup", wired.end);
    window.removeEventListener("pointercancel", wired.cancel);
    window.removeEventListener("keydown", wired.keydown);
    wired.sheet.removeEventListener("click", wired.click, true);
    wired.cancelVisualFrame();
    wired.scrollContainer?.classList.remove("message-action-sheet-open");
    mobileMessageActionSheets.delete(backdrop);
}

export function focusComposerActionPopup(root, selector) {
    root?.querySelector(selector)?.focus({ preventScroll: true });
}

export function moveComposerActionPopupFocus(root, selector, delta) {
    const entries = Array.from(root?.querySelectorAll(selector) || []);
    if (!entries.length) return;
    const current = entries.indexOf(document.activeElement);
    entries[(current + delta + entries.length) % entries.length].focus({ preventScroll: true });
}

export function wireHorizontalWheel(element) {
    if (!element || horizontalWheelElements.has(element)) return;
    horizontalWheelElements.add(element);
    element.addEventListener("wheel", event => {
        if (element.scrollWidth <= element.clientWidth || Math.abs(event.deltaX) >= Math.abs(event.deltaY)) return;
        event.preventDefault();
        element.scrollLeft += event.deltaY;
    }, { passive: false });
}

export async function analyzeComposerFiles(composerRoot) {
    const files = Array.from(composerRoot?.querySelector('input[type="file"]')?.files || []);
    return Promise.all(files.map(async file => {
        const result = { name: file.name, size: file.size, lastModified: file.lastModified, width: null, height: null, averageColor: null };
        if (file.type?.toLowerCase() === "video/mp4") {
            if (typeof URL === "undefined" || typeof URL.createObjectURL !== "function") return result;
            const objectUrl = URL.createObjectURL(file);
            const video = document.createElement("video");
            video.preload = "metadata";
            try {
                await new Promise(resolve => {
                    let settled = false;
                    let timeout;
                    const finish = () => { if (!settled) { settled = true; clearTimeout(timeout); resolve(); } };
                    video.onloadedmetadata = () => {
                        result.width = video.videoWidth || null;
                        result.height = video.videoHeight || null;
                        finish();
                    };
                    video.onerror = finish;
                    timeout = setTimeout(finish, 3000);
                    video.src = objectUrl;
                });
            } finally {
                video.removeAttribute("src");
                video.load();
                URL.revokeObjectURL(objectUrl);
            }
            return result;
        }
        if (!file.type?.startsWith("image/") || typeof createImageBitmap !== "function") return result;
        let bitmap;
        try {
            bitmap = await createImageBitmap(file);
            result.width = bitmap.width;
            result.height = bitmap.height;
            const sampleSize = 24;
            const canvas = document.createElement("canvas");
            canvas.width = sampleSize; canvas.height = sampleSize;
            const context = canvas.getContext("2d", { willReadFrequently: true });
            context.drawImage(bitmap, 0, 0, sampleSize, sampleSize);
            const pixels = context.getImageData(0, 0, sampleSize, sampleSize).data;
            let red = 0, green = 0, blue = 0, weight = 0;
            for (let index = 0; index < pixels.length; index += 4) {
                const alpha = pixels[index + 3] / 255;
                if (alpha <= 0) continue;
                red += pixels[index] * alpha; green += pixels[index + 1] * alpha; blue += pixels[index + 2] * alpha; weight += alpha;
            }
            if (weight > 0) {
                const hex = value => Math.round(value / weight).toString(16).padStart(2, "0");
                result.averageColor = `#${hex(red)}${hex(green)}${hex(blue)}`.toUpperCase();
            }
        } catch { }
        finally { bitmap?.close?.(); }
        return result;
    }));
}

export function scrollToEnd(container, force) {
    if (!container) return;
    const closeToBottom = container.scrollHeight - container.scrollTop - container.clientHeight < 180;
    if (force || closeToBottom) container.scrollTop = container.scrollHeight;
}

export function wireMessageViewport(container, dotNetReference) {
    if (!container || messageViewports.has(container)) return;
    const state = {
        isPinnedToLatest: true,
        shouldShowJumpToLatest: false,
        programmaticLatest: false,
        topRequested: false,
        prependHeight: 0,
        prependTop: 0,
        bottomDistance: messageViewportBottomDistance(container.scrollHeight, container.scrollTop, container.clientHeight),
        clientHeight: container.clientHeight,
        interactionRevision: 0,
        anchorFrame: 0,
        pendingMobileAnchor: null
    };
    const update = () => {
        const suppressTopRequest = state.programmaticLatest;
        const distance = messageViewportBottomDistance(container.scrollHeight, container.scrollTop, container.clientHeight);
        state.bottomDistance = distance;
        const isPinnedToLatest = distance <= 2;
        const jumpThreshold = Math.max(240, container.clientHeight * 0.35);
        const shouldShowJumpToLatest = distance >= jumpThreshold;
        if (isPinnedToLatest !== state.isPinnedToLatest || shouldShowJumpToLatest !== state.shouldShowJumpToLatest) {
            state.isPinnedToLatest = isPinnedToLatest;
            state.shouldShowJumpToLatest = shouldShowJumpToLatest;
            dotNetReference.invokeMethodAsync("ViewportStateChangedAsync", isPinnedToLatest, shouldShowJumpToLatest);
        }
        if (state.programmaticLatest && isPinnedToLatest) state.programmaticLatest = false;
        if (container.scrollTop < 180 && !suppressTopRequest && !state.topRequested) {
            state.topRequested = true;
            dotNetReference.invokeMethodAsync("LoadOlderFromScrollAsync").finally(() => state.topRequested = false);
        }
    };
    const cancelPendingAnchor = () => {
        state.pendingMobileAnchor = null;
        if (state.anchorFrame) cancelAnimationFrame(state.anchorFrame);
        state.anchorFrame = 0;
    };
    const cancelProgrammaticLatest = () => {
        state.programmaticLatest = false;
        state.interactionRevision++;
        cancelPendingAnchor();
    };
    const mobileQuery = window.matchMedia("(max-width: 860px)");
    const queueMobileAnchorRestore = () => {
        if (!mobileQuery.matches) return;
        state.pendingMobileAnchor ??= {
            bottomDistance: state.bottomDistance,
            interactionRevision: state.interactionRevision
        };
        if (state.anchorFrame) cancelAnimationFrame(state.anchorFrame);
        state.anchorFrame = requestAnimationFrame(() => {
            state.anchorFrame = 0;
            const anchor = state.pendingMobileAnchor;
            state.pendingMobileAnchor = null;
            if (!anchor || !mobileQuery.matches || anchor.interactionRevision !== state.interactionRevision) return;
            const previous = container.style.scrollBehavior;
            container.style.scrollBehavior = "auto";
            container.scrollTop = messageViewportAnchoredScrollTop(
                container.scrollHeight, container.clientHeight, anchor.bottomDistance);
            container.style.scrollBehavior = previous;
            state.clientHeight = container.clientHeight;
            update();
        });
    };
    const mobileViewportLayout = () => queueMobileAnchorRestore();
    const resizeObserver = typeof ResizeObserver !== "undefined"
        ? new ResizeObserver(() => {
            const clientHeight = container.clientHeight;
            if (clientHeight === state.clientHeight) return;
            state.clientHeight = clientHeight;
            if (mobileQuery.matches) queueMobileAnchorRestore();
            else {
                cancelPendingAnchor();
                state.bottomDistance = messageViewportBottomDistance(
                    container.scrollHeight, container.scrollTop, clientHeight);
            }
        })
        : null;
    const scrollEnd = () => {
        state.programmaticLatest = true;
        update();
        state.programmaticLatest = false;
    };
    container.addEventListener("scroll", update, { passive: true });
    container.addEventListener("wheel", cancelProgrammaticLatest, { passive: true });
    container.addEventListener("pointerdown", cancelProgrammaticLatest, { passive: true });
    container.addEventListener("touchstart", cancelProgrammaticLatest, { passive: true });
    container.addEventListener("scrollend", scrollEnd, { passive: true });
    window.addEventListener(mobileViewportLayoutEvent, mobileViewportLayout);
    resizeObserver?.observe(container);
    messageViewports.set(container, {
        state, update, cancelProgrammaticLatest, scrollEnd, mobileViewportLayout, resizeObserver, cancelPendingAnchor
    });
}

export function unwireMessageViewport(container) {
    const wired = container ? messageViewports.get(container) : null;
    if (!wired) return;
    container.removeEventListener("scroll", wired.update);
    container.removeEventListener("wheel", wired.cancelProgrammaticLatest);
    container.removeEventListener("pointerdown", wired.cancelProgrammaticLatest);
    container.removeEventListener("touchstart", wired.cancelProgrammaticLatest);
    container.removeEventListener("scrollend", wired.scrollEnd);
    window.removeEventListener(mobileViewportLayoutEvent, wired.mobileViewportLayout);
    wired.resizeObserver?.disconnect();
    wired.cancelPendingAnchor();
    messageViewports.delete(container);
}

export function positionInitialLatest(container) {
    if (!container) return;
    const previous = container.style.scrollBehavior;
    container.style.scrollBehavior = "auto";
    container.scrollTop = container.scrollHeight;
    container.style.scrollBehavior = previous;
    const wired = messageViewports.get(container);
    if (wired) {
        wired.state.programmaticLatest = true;
        wired.update();
    }
}

export function capturePrependPosition(container) {
    const wired = messageViewports.get(container);
    if (!wired) return;
    wired.state.prependHeight = container.scrollHeight;
    wired.state.prependTop = container.scrollTop;
}

export function restorePrependPosition(container) {
    const wired = messageViewports.get(container);
    if (!wired) return;
    const previous = container.style.scrollBehavior;
    container.style.scrollBehavior = "auto";
    container.scrollTop = wired.state.prependTop + (container.scrollHeight - wired.state.prependHeight);
    container.style.scrollBehavior = previous;
    wired.update();
}

export function followRealtimeAppend(container) {
    const wired = messageViewports.get(container);
    if (!container || !wired) return;
    if (wired.state.isPinnedToLatest) {
        wired.state.programmaticLatest = true;
        const previous = container.style.scrollBehavior;
        container.style.scrollBehavior = "auto";
        container.scrollTop = container.scrollHeight;
        container.style.scrollBehavior = previous;
    }
    wired.update();
}

export function scrollMessageBottom(container, behavior) {
    if (!container) return;
    const wired = messageViewports.get(container);
    if (behavior === "smooth") {
        if (wired) wired.state.programmaticLatest = true;
        container.scrollTo({ top: container.scrollHeight, behavior: "smooth" });
        requestAnimationFrame(() => wired?.update());
        return;
    }
    const previous = container.style.scrollBehavior;
    if (wired) wired.state.programmaticLatest = true;
    container.style.scrollBehavior = "auto";
    container.scrollTop = container.scrollHeight;
    container.style.scrollBehavior = previous;
    if (wired) {
        wired.update();
    }
}

export function focusMessage(container, messageId) {
    if (!container) return;
    const row = Array.from(container.querySelectorAll("[data-message-id]"))
        .find(element => element.dataset.messageId === messageId);
    if (!row) return;
    row.scrollIntoView({ behavior: "smooth", block: "center" });
    row.classList.remove("message-focus");
    void row.offsetWidth;
    row.classList.add("message-focus");
}

export function focusMessageImmediate(container, messageId) {
    if (!container) return;
    const row = container.querySelector(`[data-message-id="${CSS.escape(messageId)}"]`);
    if (!row) return;
    const previous = container.style.scrollBehavior;
    container.style.scrollBehavior = "auto";
    row.scrollIntoView({ behavior: "auto", block: "center" });
    container.style.scrollBehavior = previous;
    row.classList.add("reply-focus");
    window.setTimeout(() => row.classList.remove("reply-focus"), 1200);
}

const roleSorters = new WeakMap();
export function wireRoleSorter(root, dotNetReference) {
    if (!root || roleSorters.has(root)) return;
    let candidate = null;
    let dragging = null;
    let dropIndex = -1;
    const clear = () => root.querySelectorAll(".role-drop-before,.role-dragging").forEach(value => value.classList.remove("role-drop-before", "role-dragging"));
    const down = event => {
        if (event.button !== 0 || event.target.closest("button.role-row-action")) return;
        const row = event.target.closest("[data-iridium-role-drag]");
        if (!row || !root.contains(row)) return;
        candidate = { row, id: row.dataset.iridiumRoleDrag, pointerId: event.pointerId, startY: event.clientY };
    };
    const move = event => {
        if (!candidate || candidate.pointerId !== event.pointerId) return;
        if (!dragging && Math.abs(event.clientY - candidate.startY) < 5) return;
        if (!dragging) {
            dragging = candidate;
            try { dragging.row.setPointerCapture?.(event.pointerId); } catch { }
            dragging.row.classList.add("role-dragging");
        }
        event.preventDefault(); clear(); dragging.row.classList.add("role-dragging");
        const rows = [...root.querySelectorAll("[data-iridium-role-drag]")].filter(value => value !== dragging.row);
        dropIndex = rows.findIndex(value => event.clientY < value.getBoundingClientRect().top + value.getBoundingClientRect().height / 2);
        if (dropIndex < 0) dropIndex = rows.length;
        if (dropIndex < rows.length) rows[dropIndex].classList.add("role-drop-before");
    };
    const up = event => {
        if (!candidate || candidate.pointerId !== event.pointerId) return;
        const current = dragging; clear(); candidate = null; dragging = null;
        if (current && dropIndex >= 0) dotNetReference.invokeMethodAsync("CommitRoleDropAsync", current.id, dropIndex);
        dropIndex = -1;
    };
    const cancel = () => { clear(); candidate = null; dragging = null; dropIndex = -1; };
    root.addEventListener("pointerdown", down); window.addEventListener("pointermove", move, { passive: false });
    window.addEventListener("pointerup", up); window.addEventListener("pointercancel", cancel);
    roleSorters.set(root, { down, move, up, cancel });
}
export function unwireRoleSorter(root) {
    const handlers = root && roleSorters.get(root); if (!handlers) return;
    root.removeEventListener("pointerdown", handlers.down); window.removeEventListener("pointermove", handlers.move);
    window.removeEventListener("pointerup", handlers.up); window.removeEventListener("pointercancel", handlers.cancel);
    roleSorters.delete(root);
}

export function downloadBase64(fileName, contentType, base64) {
    const bytes = Uint8Array.from(atob(base64), value => value.charCodeAt(0));
    const url = URL.createObjectURL(new Blob([bytes], { type: contentType || "application/octet-stream" }));
    const link = document.createElement("a");
    link.href = url; link.download = fileName || "attachment"; link.rel = "noopener";
    document.body.appendChild(link); link.click(); link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
