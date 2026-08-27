const swipeBindings = new WeakMap();
const viewportBindings = new WeakMap();
const realtimeResumeBindings = new WeakMap();
const modifierBindings = new WeakMap();
export const mobileConversationSwipeSlop = 10;
const dominance = 1.2;
export const mobileConversationSwipeCompletionRatio = .33;
export const mobileConversationSwipeMinimumDistance = 110;
export const mobileConversationSwipeVelocityThreshold = .85;
export const mobileConversationSwipeSnapMilliseconds = 210;
const ignoredSelector = 'input,textarea,select,button,a,img,video,audio,canvas,iframe,pre,[draggable="true"],[contenteditable="true"],[role="button"],[role="slider"],[role="dialog"],[data-no-mobile-swipe],[data-swipe-nav-ignore],.message-actions,.context-menu,.emoji-picker,.youtube-embed,.video-attachment,.voice-stream-viewer,.mobile-message-action-backdrop';
const mobileViewportLayoutEvent = 'iridium-mobile-viewport-layout';
export const MobileConversationSwipePhase = Object.freeze({
    idle: 'Idle',
    candidate: 'Candidate',
    draggingHorizontal: 'DraggingHorizontal',
    completing: 'Completing',
    snappingBack: 'SnappingBack'
});
let nextSwipeGestureId = 1;

const mobileQuery = () => matchMedia('(max-width: 860px)');
const reducedMotion = () => matchMedia('(prefers-reduced-motion: reduce)').matches;

export function shouldSuppressMobileSafeBottom(isMobile, composerFocused, hasVisualViewport, viewportConstrained) {
    return isMobile && composerFocused && hasVisualViewport && viewportConstrained;
}

export function mobileConversationSwipeDistance(width) {
    return Math.min(width, Math.max(mobileConversationSwipeMinimumDistance,
        width * mobileConversationSwipeCompletionRatio));
}

export function mobileConversationSwipeOffset(startX, currentX, width) {
    return Math.min(width, Math.max(0, currentX - startX));
}

export function qualifiesMobileBackSwipe(dx, dy, width, horizontal = true, abandoned = false, velocity = 0) {
    return !abandoned && horizontal && width > 0 &&
        (dx >= mobileConversationSwipeDistance(width) ||
            (dx >= 36 && velocity >= mobileConversationSwipeVelocityThreshold));
}

export function classifyMobileSwipeDirection(dx, dy) {
    const ax = Math.abs(dx);
    const ay = Math.abs(dy);
    if (Math.hypot(dx, dy) < mobileConversationSwipeSlop) return 'undecided';
    if (ay > ax * dominance) return 'vertical';
    if (dx > 0 && ax > ay * dominance) return 'horizontal';
    if (dx < 0 && ax > ay * dominance) return 'rejected';
    return 'undecided';
}

export function shouldCancelMobileConversationSwipe(phase, reason) {
    if (phase === MobileConversationSwipePhase.idle) return false;
    if (reason === 'scroll-cancel' || reason === 'pointerleave' ||
        reason === 'bottom-sheet-cancel-event' || reason === 'resize')
        return phase === MobileConversationSwipePhase.candidate;
    return true;
}

function targetDescription(target) {
    if (!(target instanceof Element)) return String(target?.nodeName ?? 'unknown');
    const id = target.id ? `#${target.id}` : '';
    const classes = [...target.classList].slice(0, 3).map(value => `.${value}`).join('');
    return `${target.localName}${id}${classes}`;
}

function touchActionPath(target, boundary) {
    const result = [];
    for (let current = target instanceof Element ? target : target?.parentElement;
         current; current = current.parentElement) {
        result.push({ target: targetDescription(current), touchAction: getComputedStyle(current).touchAction });
        if (current === boundary || result.length === 8) break;
    }
    return result;
}

function clearSwipeStyles(element, shell) {
    element.style.removeProperty('transition');
    element.style.removeProperty('transform');
    shell?.classList.remove('mobile-swipe-revealing', 'mobile-swipe-dragging');
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
        element.style.transition = `transform ${mobileConversationSwipeSnapMilliseconds}ms cubic-bezier(.2,.75,.25,1)`;
        frame = requestAnimationFrame(() => {
            frame = 0;
            if (revision !== state.presentationRevision) { finish(); return; }
            element.style.transform = `translate3d(${destination}px,0,0)`;
        });
    });
}

export function wireMobileConversationSwipe(element, dotnet, diagnosticsEnabled = false) {
    if (!element || swipeBindings.has(element)) return;
    const shell = element.closest('.app-shell');
    const state = { gestureId: 0, phase: MobileConversationSwipePhase.idle, pointerId: null,
        startX: 0, startY: 0, direction: 'undecided', dragging: false,
        presentationRevision: 0, cancelAnimation: null, width: 0, desiredX: 0, renderedX: 0,
        visualFrame: 0, lastX: 0, lastAt: 0, velocity: 0 };
    const diagnostic = (event, details = {}) => {
        if (!diagnosticsEnabled) return;
        console.debug(`[Iridium mobile swipe #${state.gestureId || '-'}] ${event}`, {
            phase: state.phase,
            pointerId: state.pointerId,
            connected: element.isConnected,
            ...details
        });
    };

    const cancelVisualFrame = () => {
        if (state.visualFrame) cancelAnimationFrame(state.visualFrame);
        state.visualFrame = 0;
    };
    const writeDragVisual = () => {
        state.visualFrame = 0;
        if (state.phase !== MobileConversationSwipePhase.draggingHorizontal) return;
        state.renderedX = state.desiredX;
        element.style.transform = `translate3d(${state.renderedX}px,0,0)`;
        const progress = Math.min(1, state.renderedX / Math.max(1, mobileConversationSwipeDistance(state.width)));
        element.style.setProperty('--mobile-swipe-progress', String(progress));
    };
    const scheduleDragVisual = () => {
        if (!state.visualFrame) state.visualFrame = requestAnimationFrame(writeDragVisual);
    };
    const flushDragVisual = () => {
        cancelVisualFrame();
        writeDragVisual();
    };
    const releasePointer = (pointerId, reason) => {
        try {
            if (element.hasPointerCapture?.(pointerId)) {
                element.releasePointerCapture(pointerId);
                diagnostic('POINTER CAPTURE RELEASE', { reason, pointerId });
            }
        }
        catch (error) { diagnostic('POINTER CAPTURE RELEASE FAILED', { reason, error: String(error) }); }
    };

    const resetState = () => {
        state.phase = MobileConversationSwipePhase.idle;
        state.pointerId = null;
        state.direction = 'undecided';
        state.dragging = false;
        state.desiredX = 0;
        state.renderedX = 0;
        state.velocity = 0;
    };
    const resetPresentation = reason => {
        diagnostic('TERMINATION', { reason });
        state.presentationRevision++;
        state.cancelAnimation?.();
        cancelVisualFrame();
        if (state.pointerId !== null) releasePointer(state.pointerId, reason);
        resetState();
        element.style.removeProperty('--mobile-swipe-progress');
        clearSwipeStyles(element, shell);
    };
    const snapBack = async reason => {
        diagnostic('TERMINATION', { reason });
        const hadOffset = state.dragging || Boolean(element.style.transform);
        if (state.dragging) flushDragVisual();
        const pointerId = state.pointerId;
        state.phase = MobileConversationSwipePhase.snappingBack;
        if (pointerId !== null) releasePointer(pointerId, reason);
        state.pointerId = null;
        state.direction = 'undecided';
        state.dragging = false;
        state.desiredX = 0;
        state.velocity = 0;
        shell?.classList.remove('mobile-swipe-dragging');
        if (!hadOffset) {
            state.phase = MobileConversationSwipePhase.idle;
            shell?.classList.remove('mobile-swipe-revealing');
            return;
        }
        await animateSwipe(element, 0, state);
        if (state.pointerId !== null) return;
        element.style.removeProperty('--mobile-swipe-progress');
        clearSwipeStyles(element, shell);
        state.phase = MobileConversationSwipePhase.idle;
    };
    const down = event => {
        if (state.pointerId !== null) { void snapBack('new-pointer'); return; }
        if (!mobileQuery().matches || !shell?.classList.contains('mobile-conversation') || !event.isPrimary ||
            (event.pointerType !== 'touch' && event.pointerType !== 'pen') || event.button !== 0 ||
            event.target?.closest?.(ignoredSelector) || hasHorizontalScrollTarget(event.target, element)) return;
        state.gestureId = nextSwipeGestureId++;
        state.phase = MobileConversationSwipePhase.candidate;
        state.pointerId = event.pointerId;
        state.startX = event.clientX;
        state.startY = event.clientY;
        state.lastX = event.clientX;
        state.lastAt = performance.now();
        state.width = element.getBoundingClientRect().width;
        state.direction = 'undecided';
        diagnostic('GESTURE START', {
            pointerType: event.pointerType,
            startX: state.startX,
            startY: state.startY,
            target: targetDescription(event.target),
            mobilePanelState: shell?.className ?? '',
            touchAction: touchActionPath(event.target, element)
        });
    };
    const move = event => {
        if (state.pointerId !== event.pointerId || state.phase === MobileConversationSwipePhase.completing ||
            state.phase === MobileConversationSwipePhase.snappingBack) return;
        if (!element.isConnected) { resetPresentation('DOM-disconnected'); return; }
        const dx = event.clientX - state.startX;
        const dy = event.clientY - state.startY;

        if (state.direction === 'undecided') {
            state.direction = classifyMobileSwipeDirection(dx, dy);
            if (state.direction !== 'undecided') diagnostic('DIRECTION LOCK', {
                deltaX: dx,
                deltaY: dy,
                horizontalClaimed: state.direction === 'horizontal'
            });
            if (state.direction === 'vertical' || state.direction === 'rejected') {
                resetPresentation(state.direction === 'vertical' ? 'vertical-intent' : 'horizontal-rejected');
                return;
            }
        }
        if (state.direction === 'horizontal' && !state.dragging) {
            state.phase = MobileConversationSwipePhase.draggingHorizontal;
            state.dragging = true;
            shell.classList.add('mobile-swipe-revealing', 'mobile-swipe-dragging');
            element.style.transition = 'none';
            try {
                diagnostic('POINTER CAPTURE', { action: 'attempt', pointerId: event.pointerId });
                element.setPointerCapture(event.pointerId);
                diagnostic('POINTER CAPTURE', {
                    action: 'set', pointerId: event.pointerId,
                    hasPointerCapture: element.hasPointerCapture?.(event.pointerId) === true
                });
            }
            catch (error) { diagnostic('POINTER CAPTURE', { action: 'failed', error: String(error) }); }
            window.dispatchEvent(new CustomEvent('iridium-mobile-navigation-swipe-claimed', {
                detail: { pointerId: event.pointerId, gestureId: state.gestureId }
            }));
        }
        if (state.direction !== 'horizontal') return;
        if (event.cancelable) event.preventDefault();
        const now = performance.now();
        state.velocity = (event.clientX - state.lastX) / Math.max(1, now - state.lastAt);
        state.lastX = event.clientX;
        state.lastAt = now;
        state.desiredX = mobileConversationSwipeOffset(state.startX, event.clientX, state.width);
        scheduleDragVisual();
    };
    const up = async event => {
        if (state.pointerId !== event.pointerId) return;
        const dx = event.clientX - state.startX;
        const dy = event.clientY - state.startY;
        const now = performance.now();
        const releaseElapsed = Math.max(1, now - state.lastAt);
        const releaseDelta = event.clientX - state.lastX;
        if (Math.abs(releaseDelta) > .5) state.velocity = releaseDelta / releaseElapsed;
        else if (releaseElapsed > 80) state.velocity = 0;
        if (state.dragging) {
            state.desiredX = mobileConversationSwipeOffset(state.startX, event.clientX, state.width);
            flushDragVisual();
        }
        const width = state.width;
        const hasSelection = Boolean(window.getSelection?.()?.toString());
        const complete = !hasSelection && qualifiesMobileBackSwipe(
            dx, dy, width, state.direction === 'horizontal', state.direction === 'vertical', state.velocity);
        const hadPointerCapture = element.hasPointerCapture?.(event.pointerId);
        const pointerId = state.pointerId;
        state.phase = complete ? MobileConversationSwipePhase.completing : MobileConversationSwipePhase.snappingBack;
        state.pointerId = null;
        state.direction = 'undecided';
        state.dragging = false;
        state.desiredX = 0;
        diagnostic('TERMINATION', { reason: 'pointerup', complete, dx, dy, velocity: state.velocity });
        shell?.classList.remove('mobile-swipe-dragging');
        if (hadPointerCapture) {
            releasePointer(pointerId, 'pointerup');
        }
        if (!complete) {
            const presentationRevision = state.presentationRevision;
            await animateSwipe(element, 0, state);
            if (presentationRevision !== state.presentationRevision) return;
            element.style.removeProperty('--mobile-swipe-progress');
            clearSwipeStyles(element, shell);
            state.phase = MobileConversationSwipePhase.idle;
            return;
        }
        const presentationRevision = state.presentationRevision;
        await animateSwipe(element, width, state);
        if (presentationRevision !== state.presentationRevision) return;
        try { await dotnet.invokeMethodAsync('MobileConversationSwipeBackAsync'); }
        catch (error) {
            diagnostic('EXCEPTION', { error: String(error) });
            resetPresentation('exception');
        }
    };
    const pointerCancel = event => {
        if (state.pointerId === event.pointerId) void snapBack('pointercancel');
    };
    const gotPointerCapture = event => {
        if (state.pointerId === event.pointerId) diagnostic('POINTER CAPTURE', {
            action: 'gotpointercapture',
            hasPointerCapture: element.hasPointerCapture?.(event.pointerId) === true
        });
    };
    const lostPointerCapture = event => {
        diagnostic('POINTER CAPTURE', { action: 'lostpointercapture', eventPointerId: event.pointerId });
        if (state.pointerId === event.pointerId &&
            shouldCancelMobileConversationSwipe(state.phase, 'lostpointercapture'))
            void snapBack('lostpointercapture');
    };
    const resize = () => {
        if (state.pointerId === null && !element.style.transform) return;
        if (!shouldCancelMobileConversationSwipe(state.phase, 'resize')) {
            diagnostic('RESIZE IGNORED', { reason: 'active-horizontal-capture' });
            return;
        }
        resetPresentation('resize');
    };
    const orientationChange = () => {
        if (state.pointerId !== null || element.style.transform) resetPresentation('orientation-change');
    };
    const visibilityChange = () => {
        if (document.visibilityState !== 'visible' && (state.pointerId !== null || element.style.transform))
            resetPresentation('visibility-change');
    };
    const messageActionsOpened = () => {
        if (state.pointerId === null) return;
        if (!shouldCancelMobileConversationSwipe(state.phase, 'bottom-sheet-cancel-event')) {
            diagnostic('BOTTOM SHEET EVENT IGNORED', { reason: 'horizontal-swipe-already-claimed' });
            return;
        }
        void snapBack('bottom-sheet-cancel-event');
    };
    element.addEventListener('pointerdown', down);
    element.addEventListener('pointermove', move, { passive: false });
    element.addEventListener('pointerup', up);
    element.addEventListener('pointercancel', pointerCancel);
    element.addEventListener('gotpointercapture', gotPointerCapture);
    element.addEventListener('lostpointercapture', lostPointerCapture);
    window.addEventListener('resize', resize, { passive: true });
    window.addEventListener('orientationchange', orientationChange, { passive: true });
    window.addEventListener('iridium-mobile-message-actions-open', messageActionsOpened);
    document.addEventListener('visibilitychange', visibilityChange);
    swipeBindings.set(element, { down, move, up, pointerCancel, gotPointerCapture, lostPointerCapture,
        resize, orientationChange, visibilityChange, messageActionsOpened,
        resetPresentation: reason => resetPresentation(reason ?? 'manual-reset'), state });
}

function hasHorizontalScrollTarget(target, root) {
    for (let current = target instanceof Element ? target : target?.parentElement;
         current && current !== root; current = current.parentElement) {
        if (current.hasAttribute?.('data-horizontal-scroll')) return true;
        const style = getComputedStyle(current);
        if ((style.overflowX === 'auto' || style.overflowX === 'scroll') &&
            current.scrollWidth > current.clientWidth + 1) return true;
    }
    return false;
}

export function resetMobileConversationSwipe(element) {
    swipeBindings.get(element)?.resetPresentation('navigation-state-change');
}

export function inspectMobileConversationSwipeState(element) {
    const state = swipeBindings.get(element)?.state;
    return state ? {
        gestureId: state.gestureId,
        phase: state.phase,
        pointerId: state.pointerId,
        direction: state.direction,
        dragX: state.renderedX
    } : null;
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
    element.removeEventListener('pointercancel', binding.pointerCancel);
    element.removeEventListener('gotpointercapture', binding.gotPointerCapture);
    element.removeEventListener('lostpointercapture', binding.lostPointerCapture);
    window.removeEventListener('resize', binding.resize);
    window.removeEventListener('orientationchange', binding.orientationChange);
    window.removeEventListener('iridium-mobile-message-actions-open', binding.messageActionsOpened);
    document.removeEventListener('visibilitychange', binding.visibilityChange);
    binding.resetPresentation('component-dispose');
    swipeBindings.delete(element);
}

export function wireMobileViewport(shell, dotnet) {
    const query = mobileQuery();
    if (!shell || viewportBindings.has(shell)) return;
    let frame = 0;
    let composerFocused = document.activeElement?.matches?.('.composer-rich-editor') === true;
    let unfocusedViewportHeight = window.visualViewport?.height ?? window.innerHeight;
    let unfocusedViewportWidth = window.visualViewport?.width ?? window.innerWidth;
    let appliedViewportHeight = null;
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
            appliedViewportHeight = null;
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
        if (appliedViewportHeight !== height) {
            appliedViewportHeight = height;
            window.dispatchEvent(new CustomEvent(mobileViewportLayoutEvent, { detail: { height } }));
        }
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
