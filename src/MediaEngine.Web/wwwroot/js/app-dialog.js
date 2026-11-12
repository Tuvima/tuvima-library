import { engageInert, trackBar, escapeOwnedAbove, escapeAimedAt } from './app-overlay-live.js';

const states = new Map();
const frames = new Map();
const openDialogs = new Set();
const sheetDismissThreshold = 60;
let removalObserver;
let savedOverflow, savedPadding, savedGutter;
function topmost(dialog) {
    return [...openDialogs].filter(item => item.isConnected && item.open).at(-1) === dialog;
}
function fieldOwnsEscape(target) {
    return !!target?.closest?.('[data-app-escape-owner="field"]')
        || target?.tagName === 'SELECT'
        || (target?.tagName === 'INPUT' && ['search', 'date', 'time', 'datetime-local', 'month', 'week', 'color', 'file'].includes(target.type));
}
function requestCancel(state) {
    if (!state.expectedOpen || state.cancelPending) return;
    state.cancelPending = true;
    try {
        Promise.resolve(state.dotnet.invokeMethodAsync('RequestCancel'))
            .catch(error => console.error('Dialog cancellation failed.', error))
            .finally(() => { state.cancelPending = false; });
    } catch (error) { state.cancelPending = false; console.error('Dialog cancellation failed.', error); }
}
// A live window (the details overlay) is not browser-modal: it is shown in place with show(), and engage() makes the
// page behind it inert by hand so the player bar beside it stays usable (app-overlay-live.js).
function engage(state) {
    state.inert ??= engageInert(state.dialog);
    state.bar ??= trackBar(state.dialog);
}
function disengage(state) {
    state.inert?.release(); state.inert = null;
    state.bar?.release(); state.bar = null;
}
function show(state) {
    if (state.live) {
        state.dialog.show();
        engage(state);
    } else state.dialog.showModal();
    // Preserve popover top-layer ordering on initial open and recovery.
    for (const popup of state.dialog.querySelectorAll('[popover]:popover-open')) { popup.hidePopover(); popup.showPopover(); }
}
function lock(dialog) {
    if (!openDialogs.size) {
        const root = document.documentElement;
        savedOverflow = root.style.overflow;
        savedPadding = root.style.paddingRight;
        savedGutter = root.style.scrollbarGutter;
        const gap = innerWidth - root.clientWidth;
        const existing = parseFloat(getComputedStyle(root).paddingRight) || 0;
        root.style.overflow = 'hidden';
        root.style.paddingRight = `${existing + gap}px`;
    }
    openDialogs.add(dialog);
}
function unlock(dialog) {
    openDialogs.delete(dialog);
    if (!openDialogs.size && savedOverflow !== undefined) {
        const root = document.documentElement;
        root.style.overflow = savedOverflow;
        root.style.paddingRight = savedPadding;
        root.style.scrollbarGutter = savedGutter;
        savedOverflow = undefined;
    }
}
// The window's own dimmed backdrop element (a live window has no ::backdrop): the previous sibling marked for it.
function backdropOf(dialog) {
    const candidate = dialog.previousElementSibling;
    return candidate?.hasAttribute?.('data-app-dialog-backdrop') ? candidate : null;
}
// `live` keeps the window non-modal so the persistent player bar stays usable beside it (spec music-player-and-listen-fixes D5).
export function update(dialog, dotnet, open, backdrop, escape, frameId, sheet, live) {
    if (!(dialog instanceof HTMLDialogElement)) return;
    let state = states.get(dialog);
    if (!state) {
        state = { dialog, frameId, dotnet, backdrop, escape, live: live === true, expectedOpen: false, opener: null, pointerOutside: false, cancelPending: false };
        // Native closure is application-owned. Older engines that lack closedby use
        // keydown prevention plus close-event recovery rather than cancel alone.
        dialog.setAttribute('closedby', 'none');
        // Tells the pop-over script this window is not a dialog a menu opened (a click into it closes a menu from the player bar).
        if (state.live) dialog.setAttribute('data-overlay-window', '');
        state.keydown = event => {
            if (event.key !== 'Escape' || event.defaultPrevented || event.isComposing || !topmost(dialog)) return;
            if (dialog.querySelector('.tl-popover-open:not([hidden])')) return;
            const ownsKey = fieldOwnsEscape(event.target);
            // Inline field cancellation still reaches Blazor; only native dialog
            // dismissal is prevented. Native pickers/composition retain their key.
            if (ownsKey && !event.target?.closest?.('[data-app-escape-owner="field"]')) return;
            event.preventDefault();
            if (!ownsKey && state.escape) requestCancel(state);
        };
        state.cancel = event => {
            event.preventDefault();
            // Nested popup Escape belongs to that popup, even if the browser dispatched cancel.
            if (!topmost(dialog) || dialog.querySelector('.tl-popover-open:not([hidden])') || fieldOwnsEscape(document.activeElement)) return;
            if (state.escape) requestCancel(state);
        };
        state.focusin = event => { state.focused = event.target; };
        state.close = () => {
            if (!state.expectedOpen || !dialog.isConnected || dialog.open) return;
            const focused = state.focused;
            show(state); // Keep the existing lock/opener; this is not a new modal.
            if (focused instanceof HTMLElement && focused.isConnected && dialog.contains(focused))
                focused.focus({ preventScroll: true });
            if (state.escape) requestCancel(state);
        };
        state.pointerdown = event => { state.pointerOutside = event.target === dialog && outside(dialog, event); };
        state.click = event => {
            if (state.backdrop && state.pointerOutside && event.target === dialog && outside(dialog, event)) requestCancel(state);
            state.pointerOutside = false;
        };
        // Bottom-sheet swipe-down: drag the grip past the threshold to dismiss (same path as backdrop tap).
        state.gripdown = event => {
            if (!state.sheet || !event.isPrimary) return;
            const grip = event.target?.closest?.('[data-app-dialog-grip]');
            if (!grip) return;
            state.drag = { id: event.pointerId, startY: event.clientY, delta: 0, grip };
            try { grip.setPointerCapture(event.pointerId); } catch { /* capture is best-effort; move/up still bubble from the grip */ }
        };
        state.gripmove = event => {
            const drag = state.drag;
            if (!drag || event.pointerId !== drag.id) return;
            drag.delta = Math.max(0, event.clientY - drag.startY);
            if (!matchMedia('(prefers-reduced-motion: reduce)').matches) dialog.style.setProperty('--sheet-drag', `${drag.delta}px`);
        };
        state.gripend = event => {
            const drag = state.drag;
            if (!drag || event.pointerId !== drag.id) return;
            state.drag = null;
            dialog.style.removeProperty('--sheet-drag');
            if (event.type === 'pointerup' && drag.delta >= sheetDismissThreshold && (state.escape || state.backdrop)) requestCancel(state);
        };
        dialog.addEventListener('pointerdown', state.gripdown);
        dialog.addEventListener('pointermove', state.gripmove);
        dialog.addEventListener('pointerup', state.gripend);
        dialog.addEventListener('pointercancel', state.gripend);
        dialog.addEventListener('cancel', state.cancel);
        if (state.live) {
            // A non-modal window never gets the browser's Escape handling, and focus may be on the page body, so Escape is read on
            // the document: it is the window's only when focus is in it (or nowhere) and no menu, panel or dialog above owns it.
            // Escape inside the player bar or one of its panels is aimed elsewhere and never closes the window.
            state.docKeydown = event => {
                if (event.key !== 'Escape' || !escapeAimedAt(dialog, event.target) || escapeOwnedAbove(dialog)) return;
                state.keydown(event);
            };
            document.addEventListener('keydown', state.docKeydown, true);
            // The backdrop is an element beside the window: a click on it asks to close, and it never scrolls the page under it.
            state.backdropElement = backdropOf(dialog);
            state.backdropClick = () => { if (state.backdrop) requestCancel(state); };
            state.backdropScroll = event => event.preventDefault();
            state.backdropElement?.addEventListener('click', state.backdropClick);
            state.backdropElement?.addEventListener('wheel', state.backdropScroll, { passive: false });
            state.backdropElement?.addEventListener('touchmove', state.backdropScroll, { passive: false });
        } else dialog.addEventListener('keydown', state.keydown, true);
        dialog.addEventListener('close', state.close);
        dialog.addEventListener('focusin', state.focusin);
        dialog.addEventListener('pointerdown', state.pointerdown);
        dialog.addEventListener('click', state.click);
        states.set(dialog, state);
        if (frameId) frames.set(frameId, state);
        // Blazor removes the DOM before asynchronous component disposal can resolve an
        // ElementReference. Keep cleanup in the browser so nested dialogs restore focus
        // even when the .NET disposal call receives a missing element.
        removalObserver ??= new MutationObserver(() => {
            for (const entry of states.values()) if (!entry.dialog.isConnected) cleanup(entry);
        });
        removalObserver.observe(document.documentElement, { childList: true, subtree: true });
    }
    Object.assign(state, { dotnet, backdrop, escape, expectedOpen: open, sheet: sheet === true });
    const heading = dialog.querySelector('h2[id]');
    if (heading) dialog.setAttribute('aria-labelledby', heading.id);
    if (open && !dialog.open) {
        state.opener = document.activeElement;
        lock(dialog);
        try {
            show(state);
        } catch (error) { disengage(state); unlock(dialog); throw error; }
    } else if (!open) {
        if (dialog.open) dialog.close();
        disengage(state);
        unlock(dialog);
        restore(state);
    }
}
function outside(dialog, event) {
    const box = dialog.getBoundingClientRect();
    return event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom;
}
function restore(state) {
    const opener = state.opener;
    state.opener = null;
    if (!(opener instanceof HTMLElement) || !opener.isConnected || opener.closest('[inert]')) return;
    // An unrelated newer modal owns focus until it closes.
    for (const modal of openDialogs) if (modal.isConnected && modal.open && !modal.contains(opener)) return;
    opener.focus({ preventScroll: true });
}
function cleanup(state) {
    const dialog = state.dialog;
    state.expectedOpen = false;
    dialog.removeEventListener('cancel', state.cancel);
    dialog.removeEventListener('keydown', state.keydown, true);
    if (state.docKeydown) document.removeEventListener('keydown', state.docKeydown, true);
    state.backdropElement?.removeEventListener('click', state.backdropClick);
    state.backdropElement?.removeEventListener('wheel', state.backdropScroll);
    state.backdropElement?.removeEventListener('touchmove', state.backdropScroll);
    disengage(state);
    dialog.removeEventListener('close', state.close);
    dialog.removeEventListener('focusin', state.focusin);
    dialog.removeEventListener('pointerdown', state.pointerdown);
    dialog.removeEventListener('click', state.click);
    dialog.removeEventListener('pointerdown', state.gripdown);
    dialog.removeEventListener('pointermove', state.gripmove);
    dialog.removeEventListener('pointerup', state.gripend);
    dialog.removeEventListener('pointercancel', state.gripend);
    if (dialog.open) dialog.close();
    unlock(dialog);
    restore(state);
    states.delete(dialog);
    if (state.frameId) frames.delete(state.frameId);
    if (!states.size) { removalObserver?.disconnect(); removalObserver = undefined; }
}
export function detach(dialogOrFrameId) {
    const state = typeof dialogOrFrameId === 'string' ? frames.get(dialogOrFrameId) : states.get(dialogOrFrameId);
    if (!state) return;
    cleanup(state);
}
