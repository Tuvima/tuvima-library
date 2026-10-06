const states = new Map();
const opened = [];
let removalObserver;
const edge = 8;
function discoverAnchor(marker) {
    const parent = marker.parentElement;
    return parent?.querySelector('.tl-select-trigger,.tl-input-slot input,.tl-input-slot,.app-overflow-menu__trigger,[role="combobox"],button:not([hidden]),input') || parent;
}
function factors(origin) {
    const text = String(origin).toLowerCase();
    return [text.includes('right') ? 1 : text.includes('left') ? 0 : .5, text.includes('bottom') ? 1 : text.includes('top') ? 0 : .5];
}
function position(state) {
    if (!state.open || !state.anchor?.isConnected) return;
    const surface = state.surface;
    if (!surface.matches(':popover-open')) surface.showPopover();
    // Playback owns positioning in its explicit parent panel and fullscreen controls.
    if (surface.hasAttribute('data-playback-owned-menu')) return;
    const box = state.anchor.getBoundingClientRect();
    surface.style.maxWidth = `${Math.max(0, state.matchWidth ? Math.min(box.width, innerWidth - edge * 2) : innerWidth - edge * 2)}px`;
    if (state.matchWidth) surface.style.width = `${Math.min(box.width, innerWidth - edge * 2)}px`;
    const af = factors(state.anchorOrigin), tf = factors(state.transformOrigin);
    surface.style.maxHeight = `${Math.max(44, innerHeight - edge * 2)}px`;
    const width = surface.getBoundingClientRect().width;
    const height = surface.getBoundingClientRect().height;
    let left = box.left + box.width * af[0] - width * tf[0];
    let top = box.top + box.height * af[1] - height * tf[1];
    if (top + height > innerHeight - edge && box.top > innerHeight - box.bottom) top = box.top - height;
    if (top < edge && box.bottom + height <= innerHeight - edge) top = box.bottom;
    left = Math.max(edge, Math.min(left, innerWidth - width - edge));
    top = Math.max(edge, Math.min(top, innerHeight - height - edge));
    Object.assign(surface.style, { position: 'fixed', left: `${left}px`, top: `${top}px`, transform: 'none' });
}
function close(state, restoreFocus) {
    if (!state.open || state.dismissing) return;
    state.dismissing = true;
    state.dotnet.invokeMethodAsync('Dismiss').finally(() => { state.dismissing = false; });
    if (restoreFocus && state.anchor instanceof HTMLElement && state.anchor.isConnected) state.anchor.focus({ preventScroll: true });
}
export function update(marker, surface, anchor, dotnet, open, anchorOrigin, transformOrigin, matchWidth, focusOnOpen, hostId) {
    let state = states.get(surface);
    if (!state) {
        state = { marker, surface, anchor: anchor || discoverAnchor(marker), dotnet, open: false };
        state.layout = () => position(state);
        state.pointer = event => {
            if (opened.at(-1) !== state || surface.contains(event.target) || state.anchor?.contains(event.target)) return;
            // Descendant dialogs belong to this popup's journey and must not dismiss its ancestor accidentally.
            if (event.target.closest?.('dialog[open]') && !state.anchor.closest('dialog[open]')?.contains(event.target)) return;
            close(state, false);
        };
        state.key = event => {
            if (event.key !== 'Escape' || opened.at(-1) !== state) return;
            const targetDialog = event.target?.closest?.('dialog[open]');
            if (targetDialog && targetDialog !== state.anchor?.closest?.('dialog[open]') && !surface.contains(event.target)) return;
            event.preventDefault(); event.stopImmediatePropagation(); close(state, true);
        };
        states.set(surface, state);
        removalObserver ??= new MutationObserver(() => {
            for (const popup of states.values()) if (!popup.surface.isConnected) detach(popup.surface);
        });
        removalObserver.observe(document.documentElement, { childList: true, subtree: true });
    }
    Object.assign(state, { dotnet, anchor: anchor || state.anchor, anchorOrigin, transformOrigin, matchWidth, hostId });
    if (open && !state.open) {
        state.open = true;
        // Keep Razor-owned DOM in its original position. The browser's top layer handles
        // both modal and fullscreen stacking without a portal or a z-index workaround.
        surface.showPopover();
        opened.push(state);
        document.addEventListener('pointerdown', state.pointer, true);
        document.addEventListener('keydown', state.key, true);
        document.addEventListener('scroll', state.layout, true);
        window.addEventListener('resize', state.layout);
        document.addEventListener('fullscreenchange', state.layout);
        state.observer = new ResizeObserver(state.layout);
        if (state.anchor) state.observer.observe(state.anchor);
        state.observer.observe(surface);
        position(state);
        if (focusOnOpen) surface.querySelector('[role="menuitem"]:not(:disabled),[role="option"]:not([aria-disabled="true"]),button:not(:disabled)')?.focus({ preventScroll: true });
    } else if (!open && state.open) deactivate(state);
    else if (open) position(state);
}
function deactivate(state) {
    state.open = false;
    const index = opened.indexOf(state);
    if (index >= 0) opened.splice(index, 1);
    document.removeEventListener('pointerdown', state.pointer, true);
    document.removeEventListener('keydown', state.key, true);
    document.removeEventListener('scroll', state.layout, true);
    window.removeEventListener('resize', state.layout);
    document.removeEventListener('fullscreenchange', state.layout);
    state.observer?.disconnect();
    if (state.surface.matches(':popover-open')) state.surface.hidePopover();
}
export function detach(surface) {
    const state = states.get(surface);
    if (!state) return;
    deactivate(state); states.delete(surface);
    if (!states.size) { removalObserver?.disconnect(); removalObserver = undefined; }
}
