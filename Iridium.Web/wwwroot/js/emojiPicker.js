export function scrollToCommunity(sectionId) {
    document.getElementById(sectionId)?.scrollIntoView({ behavior: "smooth", block: "start" });
}

let gridObserver = null;

export function observeGridWidth(element, dotnet) {
    disposeGridWidth();
    const update = width => {
        const columns = Math.max(4, Math.min(12, Math.floor((width - 20) / 39)));
        dotnet.invokeMethodAsync("SetEmojiColumns", columns);
    };
    gridObserver = new ResizeObserver(entries => update(entries[0].contentRect.width));
    gridObserver.observe(element);
    update(element.clientWidth);
}

export function disposeGridWidth() {
    gridObserver?.disconnect();
    gridObserver = null;
}
