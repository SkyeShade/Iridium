const swipeBindings = new WeakMap();
const viewportBindings = new WeakMap();
const realtimeResumeBindings = new WeakMap();
const modifierBindings = new WeakMap();
const directionDeadZone = 14;
const dominance = 1.2;
const completionRatio = 0.5;
const ignoredSelector = 'input,textarea,select,button,a,img,video,audio,canvas,iframe,[draggable="true"],[contenteditable="true"],[role="button"],[role="slider"],[data-no-mobile-swipe],.composer-wrap,.message-actions,.context-menu,.emoji-picker,.youtube-embed,.video-attachment,.voice-stream-viewer';

const mobileQuery = () => matchMedia('(max-width: 860px)');
const reducedMotion = () => matchMedia('(prefers-reduced-motion: reduce)').matches;

export function shouldSuppressMobileSafeBottom(isMobile, composerFocused, hasVisualViewport, viewportConstrained) {
    return isMobile && composerFocused && hasVisualViewport && viewportConstrained;
}

export function qualifiesMobileBackSwipe(dx, dy, width, horizontal = true, abandoned = false) {
    return !abandoned && horizontal && width > 0 && dx > width * completionRatio;
}

export function classifyMobileSwipeDirection(dx, dy) {
    const ax = Math.abs(dx);
    const ay = Math.abs(dy);
    if (Math.hypot(dx, dy) < directionDeadZone) return 'undecided';
    if (ay > ax * dominance) return 'vertical';
    if (dx > 0 && ax > ay * dominance) return 'horizontal';
    if (dx < 0 && ax > ay * dominance) return 'rejected';
    return 'undecided';
}

function clearSwipeStyles(element) {
    element.style.removeProperty('transition');
    element.style.removeProperty('transform');
}

function animateSwipe(element, destination, state) {
    state.cancelAnimation?.();
    const revision = state.presentationRevision;
    return new Promise(resolve => {
        let frame = 0;
        let settled = false;
        const finish = () => {
            if (settled) return;
            settled = true;
            if (frame) cancelAnimationFrame(frame);
            element.removeEventListener('transitionend', transitioned);
            if (state.cancelAnimation === finish) state.cancelAnimation = null;
            resolve();
        };
        const transitioned = event => {
            if (event.target === element && event.propertyName === 'transform') finish();
        };
        state.cancelAnimation = finish;
        if (revision !== state.presentationRevision) { finish(); return; }
        const transform = getComputedStyle(element).transform;
        const current = transform === 'none' ? 0 : new DOMMatrixReadOnly(transform).m41;
        if (Math.abs(current - destination) < 0.5) {
            element.style.transform = `translate3d(${destination}px,0,0)`;
            finish();
            return;
        }
        if (reducedMotion()) {
            element.style.transform = `translate3d(${destination}px,0,0)`;
            finish();
            return;
        }
        element.addEventListener('transitionend', transitioned);
        element.style.transition = 'transform 170ms cubic-bezier(.2,.75,.25,1)';
        frame = requestAnimationFrame(() => {
            frame = 0;
            if (revision !== state.presentationRevision) { finish(); return; }
            element.style.transform = `translate3d(${destination}px,0,0)`;
        });
    });
}

export function wireMobileConversationSwipe(element, dotnet) {
    if (!element || swipeBindings.has(element)) return;
    const state = { pointerId: null, startX: 0, startY: 0, direction: 'undecided', dragging: false,
        presentationRevision: 0, cancelAnimation: null };

    const reset = () => {
        state.pointerId = null;
        state.direction = 'undecided';
        state.dragging = false;
    };
    const resetPresentation = () => {
        state.presentationRevision++;
        state.cancelAnimation?.();
        reset();
        clearSwipeStyles(element);
    };
    const snapBack = async () => {
        const hadOffset = state.dragging || Boolean(element.style.transform);
        reset();
        if (!hadOffset) return;
        await animateSwipe(element, 0, state);
        if (state.pointerId !== null) return;
        clearSwipeStyles(element);
    };
    const down = event => {
        if (state.pointerId !== null) { void snapBack(); return; }
        if (!mobileQuery().matches || !event.isPrimary ||
            (event.pointerType !== 'touch' && event.pointerType !== 'pen') || event.button !== 0 ||
            event.target?.closest?.(ignoredSelector)) return;
        state.pointerId = event.pointerId;
        state.startX = event.clientX;
        state.startY = event.clientY;
        state.direction = 'undecided';
    };
    const move = event => {
        if (state.pointerId !== event.pointerId || state.direction === 'vertical' || state.direction === 'rejected') return;
        const dx = event.clientX - state.startX;
        const dy = event.clientY - state.startY;

        if (state.direction === 'undecided') {
            state.direction = classifyMobileSwipeDirection(dx, dy);
        }
        if (state.direction === 'horizontal' && !state.dragging) {
            state.dragging = true;
            element.style.transition = 'none';
            try { element.setPointerCapture(event.pointerId); } catch { }
        }
        if (state.direction !== 'horizontal') return;
        if (event.cancelable) event.preventDefault();
        const width = element.getBoundingClientRect().width;
        const translation = Math.min(width, Math.max(0, dx));
        element.style.transform = `translate3d(${translation}px,0,0)`;
    };
    const up = async event => {
        if (state.pointerId !== event.pointerId) return;
        const dx = event.clientX - state.startX;
        const dy = event.clientY - state.startY;
        const width = element.getBoundingClientRect().width;
        const hasSelection = Boolean(window.getSelection?.()?.toString());
        const complete = !hasSelection && qualifiesMobileBackSwipe(dx, dy, width, state.direction === 'horizontal', state.direction === 'vertical');
        const hadPointerCapture = element.hasPointerCapture?.(event.pointerId);
        reset();
        if (hadPointerCapture) {
            try { element.releasePointerCapture(event.pointerId); } catch { }
        }
        if (!complete) {
            const presentationRevision = state.presentationRevision;
            await animateSwipe(element, 0, state);
            if (presentationRevision !== state.presentationRevision) return;
            clearSwipeStyles(element);
            return;
        }
        const presentationRevision = state.presentationRevision;
        await animateSwipe(element, width, state);
        if (presentationRevision !== state.presentationRevision) return;
        await dotnet.invokeMethodAsync('MobileConversationSwipeBackAsync');
        clearSwipeStyles(element);
    };
    const cancel = event => { if (state.pointerId === event.pointerId) void snapBack(); };
    const resize = () => {
        if (state.pointerId === null && !element.style.transform) return;
        resetPresentation();
    };
    element.addEventListener('pointerdown', down);
    element.addEventListener('pointermove', move, { passive: false });
    element.addEventListener('pointerup', up);
    element.addEventListener('pointercancel', cancel);
    element.addEventListener('lostpointercapture', cancel);
    window.addEventListener('resize', resize, { passive: true });
    window.addEventListener('orientationchange', resize, { passive: true });
    swipeBindings.set(element, { down, move, up, cancel, resize, resetPresentation });
}

export function resetMobileConversationSwipe(element) {
    swipeBindings.get(element)?.resetPresentation();
}

function panelGeometry(element) {
    const style = getComputedStyle(element);
    const rect = element.getBoundingClientRect();
    return {
        x: rect.x,
        y: rect.y,
        width: rect.width,
        height: rect.height,
        transform: style.transform,
        visibility: style.visibility,
        display: style.display,
        zIndex: style.zIndex
    };
}

export function inspectMobilePanels(shell, navigation, conversation) {
    const nav = panelGeometry(navigation);
    const convo = panelGeometry(conversation);
    const contentKind = conversation.dataset.mobileContentKind ?? 'none';
    const nodes = {
        header: Boolean(conversation.querySelector('.mobile-conversation-header')),
        mainContentSlot: Boolean(conversation.querySelector('.main-content-slot')),
        directMessageView: Boolean(conversation.querySelector('.direct-message-view')),
        dmMessageRegion: Boolean(conversation.querySelector('.dm-message-region')),
        dmMessageHistory: Boolean(conversation.querySelector('.dm-message-history')),
        channelView: Boolean(conversation.querySelector('.channel-view')),
        channelMessageRegion: Boolean(conversation.querySelector('.channel-view .message-region')),
        messageList: Boolean(conversation.querySelector('.message-list')),
        composer: Boolean(conversation.querySelector('.composer-wrap'))
    };
    const required = [['main-content', true], ['mobile-conversation-header', nodes.header],
        ['main-content-slot', nodes.mainContentSlot]];
    if (contentKind === 'direct') required.push(
        ['direct-message-view', nodes.directMessageView],
        ['dm-message-region', nodes.dmMessageRegion],
        ['dm-message-history', nodes.dmMessageHistory],
        ['message-list', nodes.messageList],
        ['composer-wrap', nodes.composer]);
    if (contentKind === 'channel') required.push(
        ['channel-view', nodes.channelView],
        ['channel-view/message-region', nodes.channelMessageRegion],
        ['message-list', nodes.messageList],
        ['composer-wrap', nodes.composer]);
    return {
        classes: shell?.className ?? "",
        navigationX: nav.x,
        navigationY: nav.y,
        navigationWidth: nav.width,
        navigationHeight: nav.height,
        navigationDisplay: nav.display,
        navigationVisibility: nav.visibility,
        navigationTransform: nav.transform,
        navigationZIndex: nav.zIndex,
        conversationX: convo.x,
        conversationY: convo.y,
        conversationWidth: convo.width,
        conversationHeight: convo.height,
        conversationDisplay: convo.display,
        conversationVisibility: convo.visibility,
        conversationTransform: convo.transform,
        conversationZIndex: convo.zIndex,
        contentKind,
        hasHeader: nodes.header,
        hasMainContentSlot: nodes.mainContentSlot,
        hasDirectMessageView: nodes.directMessageView,
        hasDmMessageRegion: nodes.dmMessageRegion,
        hasDmMessageHistory: nodes.dmMessageHistory,
        hasChannelView: nodes.channelView,
        hasChannelMessageRegion: nodes.channelMessageRegion,
        hasMessageList: nodes.messageList,
        hasComposer: nodes.composer,
        missingNodes: required.filter(([, present]) => !present).map(([name]) => name)
    };
}

export function unwireMobileConversationSwipe(element) {
    const binding = swipeBindings.get(element);
    if (!binding) return;
    element.removeEventListener('pointerdown', binding.down);
    element.removeEventListener('pointermove', binding.move);
    element.removeEventListener('pointerup', binding.up);
    element.removeEventListener('pointercancel', binding.cancel);
    element.removeEventListener('lostpointercapture', binding.cancel);
    window.removeEventListener('resize', binding.resize);
    window.removeEventListener('orientationchange', binding.resize);
    binding.resetPresentation();
    swipeBindings.delete(element);
}

export function wireMobileViewport(shell, dotnet) {
    const query = mobileQuery();
    if (!shell || viewportBindings.has(shell)) return;
    let frame = 0;
    let composerFocused = document.activeElement?.matches?.('.composer-rich-editor') === true;
    let unfocusedViewportHeight = window.visualViewport?.height ?? window.innerHeight;
    let unfocusedViewportWidth = window.visualViewport?.width ?? window.innerWidth;
    const reportMobileLayout = () => {
        void dotnet.invokeMethodAsync(
            'MobileLayoutChangedAsync',
            query.matches
        );
    };
    const update = () => {
        frame = 0;
        if (!query.matches) {
            shell.style.removeProperty('--iridium-mobile-viewport-height');
            shell.style.removeProperty('--iridium-mobile-viewport-offset');
            shell.style.removeProperty('--iridium-mobile-safe-bottom');
            return;
        }
        const viewport = window.visualViewport;
        const height = viewport?.height ?? window.innerHeight;
        const offset = viewport?.offsetTop ?? 0;
        shell.style.setProperty('--iridium-mobile-viewport-height', `${height}px`);
        shell.style.setProperty('--iridium-mobile-viewport-offset', `${offset}px`);
        const activeComposer = document.activeElement?.matches?.('.composer-rich-editor') === true;
        composerFocused = activeComposer;
        const width = viewport?.width ?? window.innerWidth;
        if (!composerFocused || width !== unfocusedViewportWidth) {
            unfocusedViewportHeight = height;
            unfocusedViewportWidth = width;
        }
        const viewportConstrained = height < unfocusedViewportHeight;
        shell.style.setProperty('--iridium-mobile-safe-bottom',
            shouldSuppressMobileSafeBottom(query.matches, composerFocused, Boolean(viewport), viewportConstrained)
                ? '0px'
                : 'env(safe-area-inset-bottom, 0px)');
    };
    const schedule = () => {
        if (frame) cancelAnimationFrame(frame);
        frame = requestAnimationFrame(update);
    };
    const layoutChanged = () => {
        reportMobileLayout();
        schedule();
    };
    const focusin = event => {
        if (!event.target?.matches?.('.composer-rich-editor')) return;
        if (!composerFocused) {
            unfocusedViewportHeight = window.visualViewport?.height ?? window.innerHeight;
            unfocusedViewportWidth = window.visualViewport?.width ?? window.innerWidth;
        }
        composerFocused = true;
        schedule();
    };
    const focusout = event => {
        if (!event.target?.matches?.('.composer-rich-editor')) return;
        composerFocused = false;
        schedule();
    };
    const composerFocus = () => {
        composerFocused = document.activeElement?.matches?.('.composer-rich-editor') === true;
        schedule();
    };
    window.visualViewport?.addEventListener('resize', schedule, { passive: true });
    window.visualViewport?.addEventListener('scroll', schedule, { passive: true });
    window.addEventListener('resize', schedule, { passive: true });
    window.addEventListener('orientationchange', schedule, { passive: true });
    window.addEventListener('iridium-composer-focus', composerFocus);
    document.addEventListener('focusin', focusin, { passive: true });
    document.addEventListener('focusout', focusout, { passive: true });
    query.addEventListener('change', layoutChanged);
    viewportBindings.set(shell, {
        query,
        schedule,
        layoutChanged,
        focusin,
        focusout,
        composerFocus,
        frame: () => frame
    });
    reportMobileLayout();
    update();
}

export function unwireMobileViewport(shell) {
    const binding = viewportBindings.get(shell);
    if (!binding) return;
    window.visualViewport?.removeEventListener('resize', binding.schedule);
    window.visualViewport?.removeEventListener('scroll', binding.schedule);
    window.removeEventListener('resize', binding.schedule);
    window.removeEventListener('orientationchange', binding.schedule);
    window.removeEventListener('iridium-composer-focus', binding.composerFocus);
    document.removeEventListener('focusin', binding.focusin);
    document.removeEventListener('focusout', binding.focusout);
    binding.query.removeEventListener('change', binding.layoutChanged);
    const frame = binding.frame();
    if (frame) cancelAnimationFrame(frame);
    shell.style.removeProperty('--iridium-mobile-viewport-height');
    shell.style.removeProperty('--iridium-mobile-viewport-offset');
    shell.style.removeProperty('--iridium-mobile-safe-bottom');
    viewportBindings.delete(shell);
}

export function wireRealtimeResume(element, dotnet) {
    if (!element || realtimeResumeBindings.has(element)) return;
    let timer = 0;
    let pendingReason = '';
    const report = reason => {
        pendingReason = pendingReason ? `${pendingReason}+${reason}` : reason;
        if (timer) return;
        timer = window.setTimeout(() => {
            timer = 0;
            const currentReason = pendingReason;
            pendingReason = '';
            void dotnet.invokeMethodAsync('RealtimeResumeAsync', currentReason);
        }, 150);
    };
    const visibility = () => {
        if (document.visibilityState === 'visible') report('visibility');
    };
    const pageshow = () => report('pageshow');
    const online = () => report('online');
    const focus = () => report('focus');
    document.addEventListener('visibilitychange', visibility);
    window.addEventListener('pageshow', pageshow);
    window.addEventListener('online', online);
    window.addEventListener('focus', focus);
    realtimeResumeBindings.set(element, { visibility, pageshow, online, focus, timer: () => timer });
}

export function unwireRealtimeResume(element) {
    const binding = realtimeResumeBindings.get(element);
    if (!binding) return;
    document.removeEventListener('visibilitychange', binding.visibility);
    window.removeEventListener('pageshow', binding.pageshow);
    window.removeEventListener('online', binding.online);
    window.removeEventListener('focus', binding.focus);
    const timer = binding.timer();
    if (timer) window.clearTimeout(timer);
    realtimeResumeBindings.delete(element);
}

export function wireModifierKeys(element, dotnet) {
    if (!element || modifierBindings.has(element)) return;
    let shiftPressed = false;
    const setShift = pressed => {
        if (shiftPressed === pressed) return;
        shiftPressed = pressed;
        void dotnet.invokeMethodAsync('ModifierShiftChangedAsync', pressed);
    };
    const keydown = event => { if (event.key === 'Shift') setShift(true); };
    const keyup = event => { if (event.key === 'Shift') setShift(false); };
    const blur = () => setShift(false);
    const visibility = () => { if (document.visibilityState !== 'visible') setShift(false); };
    window.addEventListener('keydown', keydown);
    window.addEventListener('keyup', keyup);
    window.addEventListener('blur', blur);
    document.addEventListener('visibilitychange', visibility);
    modifierBindings.set(element, { keydown, keyup, blur, visibility, reset: () => setShift(false) });
}

export function unwireModifierKeys(element) {
    const binding = modifierBindings.get(element);
    if (!binding) return;
    window.removeEventListener('keydown', binding.keydown);
    window.removeEventListener('keyup', binding.keyup);
    window.removeEventListener('blur', binding.blur);
    document.removeEventListener('visibilitychange', binding.visibility);
    binding.reset();
    modifierBindings.delete(element);
}
