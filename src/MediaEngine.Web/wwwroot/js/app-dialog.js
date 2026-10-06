const states = new Map();
const frames = new Map();
const openDialogs = new Set();
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
function show(state) {
    state.dialog.showModal();
    // Preserve toast/popover top-layer ordering on initial open and recovery.
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
export function update(dialog, dotnet, open, backdrop, escape, frameId) {
    if (!(dialog instanceof HTMLDialogElement)) return;
    let state = states.get(dialog);
    if (!state) {
        state = { dialog, frameId, dotnet, backdrop, escape, expectedOpen: false, opener: null, pointerOutside: false, cancelPending: false };
        // Native closure is application-owned. Older engines that lack closedby use
        // keydown prevention plus close-event recovery rather than cancel alone.
        dialog.setAttribute('closedby', 'none');
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
        dialog.addEventListener('cancel', state.cancel);
        dialog.addEventListener('keydown', state.keydown, true);
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
    Object.assign(state, { dotnet, backdrop, escape, expectedOpen: open });
    const heading = dialog.querySelector('h2[id]');
    if (heading) dialog.setAttribute('aria-labelledby', heading.id);
    if (open && !dialog.open) {
        state.opener = document.activeElement;
        lock(dialog);
        try {
            show(state);
        } catch (error) { unlock(dialog); throw error; }
    } else if (!open) {
        if (dialog.open) dialog.close();
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
    dialog.removeEventListener('close', state.close);
    dialog.removeEventListener('focusin', state.focusin);
    dialog.removeEventListener('pointerdown', state.pointerdown);
    dialog.removeEventListener('click', state.click);
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
export function updateToastHost(host, visible) {
    if (!(host instanceof HTMLElement)) return;
    if (visible && !host.matches(':popover-open')) host.showPopover();
    else if (!visible && host.matches(':popover-open')) host.hidePopover();
}
