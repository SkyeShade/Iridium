const composerHandlers = new WeakMap();
const messageEditorHandlers = new WeakMap();
const channelSorters = new WeakMap();
const messageViewports = new WeakMap();
const searchAutocompleteHandlers = new WeakMap();

export function wireChannelSorter(root, dotNetReference) {
    if (!root || channelSorters.has(root)) return;
    let candidate = null;
    let active = false;
    let target = null;

    const clearTarget = () => {
        root.querySelectorAll(".pointer-drop-before").forEach(value => value.classList.remove("pointer-drop-before"));
        root.querySelectorAll(".pointer-drop-after").forEach(value => value.classList.remove("pointer-drop-after"));
        root.querySelectorAll(".pointer-drop-end").forEach(value => value.classList.remove("pointer-drop-end"));
        root.querySelectorAll(".pointer-drop-inside").forEach(value => value.classList.remove("pointer-drop-inside"));
        target = null;
    };
    const pointerDown = event => {
        if (event.button !== 0 || !(event.target instanceof Element)) return;
        if (event.target.closest("button, .row-menu, .category-menu")) return;
        const channel = event.target.closest("[data-iridium-channel-drag]");
        const categoryHandle = event.target.closest("[data-iridium-category-drag-handle]");
        if (channel && root.contains(channel)) {
            candidate = {
                kind: "channel", element: channel, id: channel.dataset.iridiumChannelDrag,
                topItem: channel.dataset.channelCategory ? null : channel.closest("[data-top-level-item]"),
                pointerId: event.pointerId, startY: event.clientY
            };
            return;
        }
        const category = categoryHandle?.closest?.("[data-iridium-category-drag]");
        if (category && root.contains(category)) {
            candidate = {
                kind: "category", element: category.closest("[data-top-level-item]"),
                topItem: category.closest("[data-top-level-item]"),
                id: category.dataset.iridiumCategoryDrag, pointerId: event.pointerId, startY: event.clientY
            };
        }
    };
    const topLevelTarget = clientY => {
        const list = root.querySelector("[data-sidebar-top-level]");
        if (!list) return null;
        const items = Array.from(list.querySelectorAll(":scope > [data-top-level-item]"))
            .filter(item => item !== candidate?.topItem);
        const index = items.findIndex(item => clientY < item.getBoundingClientRect().top + item.getBoundingClientRect().height / 2);
        if (index < 0) {
            list.classList.add("pointer-drop-end");
            return { kind: "top", position: items.length };
        }
        items[index].classList.add("pointer-drop-before");
        return { kind: "top", position: index };
    };
    const categoryTarget = (block, clientY) => {
        const topItem = block.closest("[data-top-level-item]");
        const topItems = Array.from(root.querySelectorAll("[data-sidebar-top-level] > [data-top-level-item]"))
            .filter(item => item !== candidate?.topItem);
        const topIndex = topItems.indexOf(topItem);
        const blockRect = block.getBoundingClientRect();
        const heading = block.querySelector(":scope > .category-heading");
        const headingRect = heading?.getBoundingClientRect();
        const edge = Math.min(8, blockRect.height * .14);
        const before = clientY <= blockRect.top + edge || headingRect && clientY < headingRect.top + headingRect.height * .3;
        const after = clientY >= blockRect.bottom - edge || headingRect && clientY > headingRect.top + headingRect.height * .7;
        if (before || after) {
            topItem.classList.add(before ? "pointer-drop-before" : "pointer-drop-after");
            return { kind: "top", position: Math.max(0, topIndex + (after ? 1 : 0)) };
        }

        const rows = Array.from(block.querySelectorAll("[data-iridium-channel-drag]"))
            .filter(row => row !== candidate?.element && row.getClientRects().length > 0);
        const position = rows.findIndex(row => clientY < row.getBoundingClientRect().top + row.getBoundingClientRect().height / 2);
        block.classList.add("pointer-drop-inside");
        if (position < 0) block.classList.add("pointer-drop-end");
        else rows[position].classList.add("pointer-drop-before");
        return { kind: "category", categoryId: block.dataset.channelGroup, position: position < 0 ? rows.length : position };
    };
    const pointerMove = event => {
        if (!candidate || event.pointerId !== candidate.pointerId) return;
        if (!active) {
            if (Math.abs(event.clientY - candidate.startY) < 6) return;
            active = true;
            candidate.element.setPointerCapture?.(event.pointerId);
            candidate.element.classList.add("pointer-dragging");
            root.classList.add("pointer-sorting");
        }
        event.preventDefault();
        clearTarget();
        const hit = document.elementFromPoint(event.clientX, event.clientY);
        if (candidate.kind === "channel") {
            const category = hit?.closest?.("[data-channel-group]");
            if (category && root.contains(category)) {
                target = categoryTarget(category, event.clientY);
                return;
            }
        }
        target = topLevelTarget(event.clientY);
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
        if (dragged.kind === "category") {
            await dotNetReference.invokeMethodAsync("CommitCategoryDropAsync", dragged.id, destination.position);
        } else {
            await dotNetReference.invokeMethodAsync(
                "CommitChannelDropAsync", dragged.id,
                destination.kind === "category" ? destination.categoryId : "", destination.position);
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
    channelSorters.set(root, { pointerDown, pointerMove, finish, cancel });
}

export function unwireChannelSorter(root) {
    const handlers = root ? channelSorters.get(root) : null;
    if (!handlers) return;
    root.removeEventListener("pointerdown", handlers.pointerDown);
    window.removeEventListener("pointermove", handlers.pointerMove);
    window.removeEventListener("pointerup", handlers.finish);
    window.removeEventListener("pointercancel", handlers.cancel);
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
    resizeTextarea(textarea, 0.5);
}

export function focusComposer(textarea) {
    if (!textarea) return;
    textarea.focus({ preventScroll: true });
    const end = textarea.value.length;
    textarea.setSelectionRange(end, end);
}

export function composerCaret(textarea) {
    if (!textarea) return 0;
    return textarea.selectionStart ?? textarea.value.length;
}

export function focusComposerAt(textarea, position) {
    if (!textarea) return;
    textarea.focus({ preventScroll: true });
    const caret = Math.max(0, Math.min(Number(position) || 0, textarea.value.length));
    textarea.setSelectionRange(caret, caret);
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

function resizeMessageEditor(textarea) {
    resizeTextarea(textarea, 0.35, 256);
}

export function wireComposer(textarea, dotNetReference) {
    if (!textarea || composerHandlers.has(textarea)) return;
    let submitting = false;
    const keydown = async event => {
        const mentionMenu = textarea.closest(".composer-shell")?.querySelector(".mention-suggestions");
        if (mentionMenu && ["ArrowDown", "ArrowUp", "Enter", "Tab", "Escape"].includes(event.key)) {
            event.preventDefault();
            await dotNetReference.invokeMethodAsync("HandleMentionKeyAsync", event.key);
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
    const input = () => resizeComposer(textarea);
    const observedViewport = textarea.closest(".channel-view, .direct-message-view");
    const observer = observedViewport && typeof ResizeObserver !== "undefined"
        ? new ResizeObserver(() => resizeComposer(textarea))
        : null;

    composerHandlers.set(textarea, { keydown, input, observer });
    textarea.addEventListener("keydown", keydown);
    textarea.addEventListener("input", input);
    observer?.observe(observedViewport);
    resizeComposer(textarea);
}

export function unwireComposer(textarea) {
    const handlers = textarea ? composerHandlers.get(textarea) : null;
    if (!handlers) return;
    textarea.removeEventListener("keydown", handlers.keydown);
    textarea.removeEventListener("input", handlers.input);
    handlers.observer?.disconnect();
    composerHandlers.delete(textarea);
}

export function wireMessageEditor(textarea, dotNetReference) {
    if (!textarea || messageEditorHandlers.has(textarea)) return;
    let handlingShortcut = false;
    const keydown = async event => {
        if (event.isComposing || event.keyCode === 229 || handlingShortcut) return;
        const cancel = event.key === "Escape";
        const save = event.key === "Enter" && !event.shiftKey;
        if (!cancel && !save) return;

        event.preventDefault();
        handlingShortcut = true;
        try {
            await dotNetReference.invokeMethodAsync(cancel
                ? "CancelEditFromKeyboardAsync"
                : "SaveEditFromKeyboardAsync");
        } catch (error) {
            console.error("Iridium inline message editing failed in the client.", error);
        } finally {
            handlingShortcut = false;
        }
    };
    const input = () => resizeMessageEditor(textarea);

    messageEditorHandlers.set(textarea, { keydown, input });
    textarea.addEventListener("keydown", keydown);
    textarea.addEventListener("input", input);
    resizeMessageEditor(textarea);
    textarea.focus({ preventScroll: true });
    const end = textarea.value.length;
    textarea.setSelectionRange(end, end);
}

export function unwireMessageEditor(textarea) {
    const handlers = textarea ? messageEditorHandlers.get(textarea) : null;
    if (!handlers) return;
    textarea.removeEventListener("keydown", handlers.keydown);
    textarea.removeEventListener("input", handlers.input);
    messageEditorHandlers.delete(textarea);
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
        prependTop: 0
    };
    const update = () => {
        const suppressTopRequest = state.programmaticLatest;
        const distance = Math.max(0, container.scrollHeight - container.scrollTop - container.clientHeight);
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
    const cancelProgrammaticLatest = () => { state.programmaticLatest = false; };
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
    messageViewports.set(container, { state, update, cancelProgrammaticLatest, scrollEnd });
}

export function unwireMessageViewport(container) {
    const wired = container ? messageViewports.get(container) : null;
    if (!wired) return;
    container.removeEventListener("scroll", wired.update);
    container.removeEventListener("wheel", wired.cancelProgrammaticLatest);
    container.removeEventListener("pointerdown", wired.cancelProgrammaticLatest);
    container.removeEventListener("touchstart", wired.cancelProgrammaticLatest);
    container.removeEventListener("scrollend", wired.scrollEnd);
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
        if (!dragging) { dragging = candidate; dragging.row.setPointerCapture?.(event.pointerId); dragging.row.classList.add("role-dragging"); }
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
