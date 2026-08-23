let cleanup = null;

export function open(anchor, clientX, clientY, popup, dotnet) {
    close();
    const margin = 10;
    const gap = 8;
    const popupRect = popup.getBoundingClientRect();
    const anchorRect = anchor?.getBoundingClientRect?.() ?? {
        left: clientX ?? margin,
        right: clientX ?? margin,
        top: clientY ?? margin,
        bottom: clientY ?? margin,
        height: 0
    };
    let x = anchorRect.right + gap;
    if (x + popupRect.width + margin > window.innerWidth) x = anchorRect.left - popupRect.width - gap;
    x = Math.max(margin, Math.min(window.innerWidth - popupRect.width - margin, x));
    let y = anchorRect.top + (anchorRect.height - popupRect.height) / 2;
    y = Math.max(margin, Math.min(window.innerHeight - popupRect.height - margin, y));
    popup.style.transform = `translate3d(${Math.round(x)}px,${Math.round(y)}px,0)`;

    const outside = event => {
        if (!popup.contains(event.target) && !anchor?.contains?.(event.target)) dotnet.invokeMethodAsync("CloseFromBrowser");
    };
    const keydown = event => {
        if (event.key === "Escape") dotnet.invokeMethodAsync("CloseFromBrowser");
    };
    const moved = () => dotnet.invokeMethodAsync("CloseFromBrowser");
    document.addEventListener("pointerdown", outside, true);
    document.addEventListener("keydown", keydown, true);
    document.addEventListener("scroll", moved, true);
    window.addEventListener("resize", moved, true);
    cleanup = () => {
        document.removeEventListener("pointerdown", outside, true);
        document.removeEventListener("keydown", keydown, true);
        document.removeEventListener("scroll", moved, true);
        window.removeEventListener("resize", moved, true);
    };
}

export function close() {
    cleanup?.();
    cleanup = null;
}
