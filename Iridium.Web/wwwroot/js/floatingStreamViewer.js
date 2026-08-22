const storageKey = "iridium.voice.floatingPosition";
export const floatingPositions = ["top-left","top-middle","top-right","bottom-left","bottom-middle","bottom-right"];

export function nearestFloatingPosition(x, y, anchors) {
    return anchors.reduce((best, item) => Math.hypot(item.x-x,item.y-y) < Math.hypot(best.x-x,best.y-y) ? item : best).name;
}

export function initialize(elementId) {
    const element = document.getElementById(elementId);
    if (!element) throw new Error("Floating stream viewer was not found.");
    const handle = element.querySelector(".floating-drag-handle");
    let position = localStorage.getItem(storageKey);
    if (!floatingPositions.includes(position)) position = "top-right";
    let dragging = false, pointerId = null, dx = 0, dy = 0;

    const bounds = () => {
        const app = document.querySelector(".app-shell") ?? document.documentElement;
        const rect = app.getBoundingClientRect();
        const content = document.querySelector(".main-content")?.getBoundingClientRect() ?? rect;
        const margin = 12, width = element.offsetWidth, height = element.offsetHeight;
        const left = content.left + margin, right = content.right - width - margin;
        const top = rect.top + margin, bottom = rect.bottom - height - margin;
        const middle = left + Math.max(0, right-left)/2;
        return [
            {name:"top-left",x:left,y:top},{name:"top-middle",x:middle,y:top},{name:"top-right",x:right,y:top},
            {name:"bottom-left",x:left,y:bottom},{name:"bottom-middle",x:middle,y:bottom},{name:"bottom-right",x:right,y:bottom}
        ];
    };
    const apply = (animate = true) => {
        const target = bounds().find(value => value.name === position) ?? bounds()[2];
        element.style.transition = animate ? "left 150ms ease, top 150ms ease" : "none";
        element.style.left = `${Math.max(0,target.x)}px`; element.style.top = `${Math.max(0,target.y)}px`;
    };
    const down = event => {
        if (event.button !== 0 || event.target.closest("button")) return;
        dragging = true; pointerId = event.pointerId;
        const rect = element.getBoundingClientRect(); dx = event.clientX-rect.left; dy = event.clientY-rect.top;
        handle.setPointerCapture(pointerId); element.style.transition = "none"; event.preventDefault();
    };
    const move = event => {
        if (!dragging || event.pointerId !== pointerId) return;
        const app = (document.querySelector(".app-shell") ?? document.documentElement).getBoundingClientRect();
        const content = document.querySelector(".main-content")?.getBoundingClientRect() ?? app;
        const x = Math.min(content.right-element.offsetWidth,Math.max(content.left,event.clientX-dx));
        const y = Math.min(app.bottom-element.offsetHeight,Math.max(app.top,event.clientY-dy));
        element.style.left=`${x}px`; element.style.top=`${y}px`;
    };
    const up = event => {
        if (!dragging || event.pointerId !== pointerId) return;
        dragging=false; const rect=element.getBoundingClientRect();
        position=nearestFloatingPosition(rect.left,rect.top,bounds()); localStorage.setItem(storageKey,position); apply();
    };
    handle.addEventListener("pointerdown",down); handle.addEventListener("pointermove",move);
    handle.addEventListener("pointerup",up); handle.addEventListener("pointercancel",up);
    window.addEventListener("resize",apply); requestAnimationFrame(() => apply(false));
    return { dispose() { handle.removeEventListener("pointerdown",down); handle.removeEventListener("pointermove",move); handle.removeEventListener("pointerup",up); handle.removeEventListener("pointercancel",up); window.removeEventListener("resize",apply); } };
}
