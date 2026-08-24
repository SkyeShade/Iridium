const bindings = new WeakMap();
const threshold = 84;
const dominance = 1.35;
const ignoredSelector = 'input,textarea,select,button,a,img,video,audio,canvas,iframe,[draggable="true"],[contenteditable="true"],[role="button"],[role="slider"],[data-no-mobile-swipe],.composer-wrap,.message-actions,.youtube-embed,.video-attachment,.voice-stream-viewer';

export function qualifiesMobileBackSwipe(dx, dy, horizontal = true, abandoned = false) {
    return !abandoned && horizontal && dx >= threshold && dx > 0 &&
        Math.abs(dx) > Math.abs(dy) * dominance;
}

export function wireMobileConversationSwipe(element, dotnet) {
    if (!element || bindings.has(element)) return;
    const state = { pointerId: null, startX: 0, startY: 0, horizontal: false, abandoned: false };

    const reset = () => {
        state.pointerId = null;
        state.horizontal = false;
        state.abandoned = false;
    };
    const down = event => {
        if (!matchMedia('(max-width: 860px)').matches || !event.isPrimary ||
            (event.pointerType !== 'touch' && event.pointerType !== 'pen') || event.button !== 0 ||
            event.target?.closest?.(ignoredSelector)) return;
        state.pointerId = event.pointerId;
        state.startX = event.clientX;
        state.startY = event.clientY;
        state.horizontal = false;
        state.abandoned = false;
    };
    const move = event => {
        if (state.pointerId !== event.pointerId || state.abandoned) return;
        const dx = event.clientX - state.startX;
        const dy = event.clientY - state.startY;
        const ax = Math.abs(dx);
        const ay = Math.abs(dy);
        if (!state.horizontal && ay > 16 && ay > ax) {
            state.abandoned = true;
            return;
        }
        if (!state.horizontal && dx > 18 && ax > ay * dominance) state.horizontal = true;
        if (state.horizontal && event.cancelable) event.preventDefault();
    };
    const up = event => {
        if (state.pointerId !== event.pointerId) return;
        const dx = event.clientX - state.startX;
        const dy = event.clientY - state.startY;
        const hasSelection = Boolean(window.getSelection?.()?.toString());
        const shouldNavigate = !hasSelection && qualifiesMobileBackSwipe(dx, dy, state.horizontal, state.abandoned);
        reset();
        if (shouldNavigate) void dotnet.invokeMethodAsync('MobileConversationSwipeBackAsync');
    };
    const cancel = event => { if (state.pointerId === event.pointerId) reset(); };
    element.addEventListener('pointerdown', down);
    element.addEventListener('pointermove', move, { passive: false });
    element.addEventListener('pointerup', up);
    element.addEventListener('pointercancel', cancel);
    bindings.set(element, { down, move, up, cancel });
}

export function unwireMobileConversationSwipe(element) {
    const binding = bindings.get(element);
    if (!binding) return;
    element.removeEventListener('pointerdown', binding.down);
    element.removeEventListener('pointermove', binding.move);
    element.removeEventListener('pointerup', binding.up);
    element.removeEventListener('pointercancel', binding.cancel);
    bindings.delete(element);
}
