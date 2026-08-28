export function scrollToCommunity(sectionId) {
    document.getElementById(sectionId)?.scrollIntoView({ behavior: "smooth", block: "start" });
}

const gridObservers = new WeakMap();
const dismissHandlers = new WeakMap();
const anchoredHandlers = new WeakMap();

export function observeGridWidth(element, dotnet) {
    disposeGridWidth(element);
    const update = width => {
        const columns = Math.max(4, Math.min(12, Math.floor((width - 20) / 39)));
        dotnet.invokeMethodAsync("SetEmojiColumns", columns);
    };
    const observer = new ResizeObserver(entries => update(entries[0].contentRect.width));
    gridObservers.set(element, observer);
    observer.observe(element);
    update(element.clientWidth);
}

export function disposeGridWidth(element) {
    gridObservers.get(element)?.disconnect();
    gridObservers.delete(element);
}

export function wireDismiss(element, anchor, dotnet) {
    disposeDismiss(element);
    const pointerdown = event => {
        if (!element.contains(event.target) && !anchor?.contains(event.target))
            dotnet.invokeMethodAsync("DismissAsync");
    };
    const keydown = event => {
        if (event.key === "Escape") dotnet.invokeMethodAsync("DismissAsync");
    };
    document.addEventListener("pointerdown", pointerdown, true);
    document.addEventListener("keydown", keydown);
    dismissHandlers.set(element, { pointerdown, keydown });
}

export function wireAnchoredPopup(element, anchor, dotnet) {
    disposeDismiss(element);
    disposeAnchoredPopup(element);
    element.classList.remove("positioned");
    const margin = 10;
    const gap = 8;
    const popupRect = element.getBoundingClientRect();
    const anchorRect = anchor?.getBoundingClientRect?.();
    const { x, y } = calculateAnchoredPosition(anchorRect, popupRect, window.innerWidth, window.innerHeight,
        margin, gap);
    if (!Number.isFinite(x) || !Number.isFinite(y)) {
        dotnet.invokeMethodAsync("DismissAsync");
        return;
    }
    element.style.transform = `translate3d(${Math.round(x)}px,${Math.round(y)}px,0)`;
    element.classList.add("positioned");

    const close = () => dotnet.invokeMethodAsync("DismissAsync");
    const closeOnScroll = event => { if (!element.contains(event.target)) close(); };
    const pointerdown = event => {
        if (!element.contains(event.target) && !anchor?.contains?.(event.target)) close();
    };
    const keydown = event => { if (event.key === "Escape") close(); };
    document.addEventListener("pointerdown", pointerdown, true);
    document.addEventListener("keydown", keydown, true);
    document.addEventListener("scroll", closeOnScroll, true);
    window.addEventListener("resize", close, true);
    anchoredHandlers.set(element, { pointerdown, keydown, close, closeOnScroll });
}

export function calculateAnchoredPosition(anchorRect, popupRect, viewportWidth, viewportHeight,
    margin = 10, gap = 8) {
    const hasAnchor = anchorRect && anchorRect.width > 0 && anchorRect.height > 0;
    let x;
    let y;
    if (hasAnchor) {
        x = anchorRect.right - popupRect.width;
        const above = anchorRect.top - popupRect.height - gap;
        const below = anchorRect.bottom + gap;
        if (above >= margin) y = above;
        else if (below + popupRect.height <= viewportHeight - margin) y = below;
        else y = anchorRect.top >= viewportHeight - anchorRect.bottom ? above : below;
    } else {
        x = (viewportWidth - popupRect.width) / 2;
        y = viewportHeight - popupRect.height - margin;
    }
    return {
        x: Math.max(margin, Math.min(viewportWidth - popupRect.width - margin, x)),
        y: Math.max(margin, Math.min(viewportHeight - popupRect.height - margin, y))
    };
}

export function disposeAnchoredPopup(element) {
    const handlers = anchoredHandlers.get(element);
    if (!handlers) return;
    document.removeEventListener("pointerdown", handlers.pointerdown, true);
    document.removeEventListener("keydown", handlers.keydown, true);
    document.removeEventListener("scroll", handlers.closeOnScroll, true);
    window.removeEventListener("resize", handlers.close, true);
    anchoredHandlers.delete(element);
}

export function disposeDismiss(element) {
    disposeAnchoredPopup(element);
    const handlers = dismissHandlers.get(element);
    if (!handlers) return;
    document.removeEventListener("pointerdown", handlers.pointerdown, true);
    document.removeEventListener("keydown", handlers.keydown);
    dismissHandlers.delete(element);
}
