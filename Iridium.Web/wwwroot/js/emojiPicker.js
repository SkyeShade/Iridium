export function scrollToCommunity(sectionId) {
    document.getElementById(sectionId)?.scrollIntoView({ behavior: "smooth", block: "start" });
}

const gridObservers = new WeakMap();
const dismissHandlers = new WeakMap();

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

export function disposeDismiss(element) {
    const handlers = dismissHandlers.get(element);
    if (!handlers) return;
    document.removeEventListener("pointerdown", handlers.pointerdown, true);
    document.removeEventListener("keydown", handlers.keydown);
    dismissHandlers.delete(element);
}
