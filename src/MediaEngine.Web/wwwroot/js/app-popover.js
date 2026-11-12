const states = new Map();
const opened = [];
let removalObserver;
const edge = 8;
function discoverAnchor(marker) {
    const parent = marker?.parentElement;
    return parent?.querySelector('.tl-select-trigger,.tl-input-slot input,.tl-input-slot,.app-overflow-menu__trigger,[role="combobox"],button:not([hidden]),input') || parent || null;
}
function factors(origin) {
    const text = String(origin).toLowerCase();
    return [text.includes('right') ? 1 : text.includes('left') ? 0 : .5, text.includes('bottom') ? 1 : text.includes('top') ? 0 : .5];
}
// Dropdown lists (Look v3.1 D3): a list is at least as wide as the control it opens from, grows to fit its longest option, and stops at
// 480 px or the window less 16 px each side. An anchor wider than 480 px gets a list exactly its own width (still inside the window).
export const listCap = 480;
export const listGap = 16;
// A Sheet's dim area is the surface's own backdrop: a press there has the surface as its target and lands above the Sheet's top edge.
export function isSheetBackdropPress(surface, event) {
    return event.target === surface && surface.classList.contains('app-popout--sheet') && event.clientY < surface.getBoundingClientRect().top;
}
export function listWidth(anchorWidth, contentWidth, viewportWidth, cap = listCap, gap = listGap) {
    const room = Math.max(0, viewportWidth - gap * 2);
    if (anchorWidth > cap) return Math.min(anchorWidth, room);
    return Math.min(Math.max(anchorWidth, Math.min(contentWidth, cap)), room);
}
// A surface that must stay clear of a fixed bar at the bottom of the screen (the Reorder bar, spec editor v6 D23) names the bar's height in
// --tl-popover-bottom-reserve; without it the whole screen is available, as before.
export function bottomReserve(surface) {
    if (typeof getComputedStyle !== 'function') return 0;
    const style = getComputedStyle(surface);
    const value = typeof style?.getPropertyValue === 'function' ? parseFloat(style.getPropertyValue('--tl-popover-bottom-reserve')) : 0;
    return Number.isFinite(value) && value > 0 ? value : 0;
}
// The tallest a surface may be: the screen less its edge gaps and any reserve; with a reserve it also stops above that reserve when it opens under
// its anchor (so its list shrinks and scrolls) unless the anchor is so low that almost nothing is left below it.
export function maxSurfaceHeight(viewport, edgeGap, reserve, anchorBottom) {
    const whole = viewport - edgeGap * 2 - reserve;
    const below = viewport - edgeGap - reserve - anchorBottom;
    return Math.max(44, reserve > 0 && below >= 120 ? Math.min(whole, below) : whole);
}
// The stylesheet states some of these with !important, so the list's own size is written the same way.
function pin(style, name, value) {
    if (typeof style.setProperty === 'function') style.setProperty(name, value, 'important'); else style[name.replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())] = value;
}
function sizeList(surface, anchorWidth) {
    pin(surface.style, 'min-width', '0'); pin(surface.style, 'max-width', 'none'); pin(surface.style, 'width', 'max-content');
    const width = `${listWidth(anchorWidth, surface.getBoundingClientRect().width, innerWidth)}px`;
    pin(surface.style, 'min-width', width); pin(surface.style, 'max-width', width); pin(surface.style, 'width', width);
}
const sheetStyle = { width: '100%', 'min-width': '0', 'max-width': 'none', 'border-radius': 'var(--tl-sheet-radius,16px) var(--tl-sheet-radius,16px) 0 0', 'padding-bottom': 'env(safe-area-inset-bottom,0px)' };
function position(state) {
    if (!state.open || !state.anchor?.isConnected) return;
    const surface = state.surface;
    if (!surface.matches(':popover-open')) surface.showPopover();
    // Playback owns positioning in its explicit parent panel and fullscreen controls.
    if (surface.hasAttribute('data-playback-owned-menu')) return;
    const listMode = surface.dataset?.listWidth === 'true';
    const listSheet = surface.dataset?.listSheet === 'true';
    // A sized Pop-out (Menu or Panel) is a Sheet on phones: the stylesheet pins it to the bottom, so no anchoring.
    // A dropdown list that asks for it (data-list-sheet) is the same Sheet, with 52 px rows.
    if (surface.dataset?.popoutSize || listSheet) {
        const sheet = typeof matchMedia === 'function' && matchMedia('(max-width: 600px)').matches;
        surface.classList.toggle('app-popout--sheet', sheet);
        if (sheet) {
            for (const name of ['position', 'left', 'top', 'width', 'min-width', 'max-width', 'max-height', 'transform']) surface.style.removeProperty(name);
            // The shared menu rules fix a menu's width and corners with !important, which a Sheet must outrank.
            if (listSheet) for (const [name, value] of Object.entries(sheetStyle)) pin(surface.style, name, value);
            return;
        }
        if (listSheet) for (const name of ['border-radius', 'padding-bottom']) surface.style.removeProperty?.(name);
    }
    const box = state.anchor.getBoundingClientRect();
    const reserve = bottomReserve(surface);
    const floor = innerHeight - edge - reserve;
    if (listMode) {
        // The height limit goes first so a list that scrolls is measured with its scrollbar.
        surface.style.maxHeight = `${maxSurfaceHeight(innerHeight, edge, reserve, box.bottom)}px`;
        sizeList(surface, box.width);
    } else {
        surface.style.maxWidth = `${Math.max(0, state.matchWidth ? Math.min(box.width, innerWidth - edge * 2) : innerWidth - edge * 2)}px`;
        if (state.matchWidth) surface.style.width = `${Math.min(box.width, innerWidth - edge * 2)}px`;
    }
    const af = factors(state.anchorOrigin), tf = factors(state.transformOrigin);
    surface.style.maxHeight = `${maxSurfaceHeight(innerHeight, edge, reserve, box.bottom)}px`;
    const width = surface.getBoundingClientRect().width;
    const height = surface.getBoundingClientRect().height;
    let left = box.left + box.width * af[0] - width * tf[0];
    let top = box.top + box.height * af[1] - height * tf[1];
    // Flip sideways before clamping: a box that runs off the right edge mirrors around the anchor (a left-aligned
    // menu becomes right-aligned to its button), and one that runs off the left edge mirrors the other way. The
    // mirrored spot is used only when it fits, so a popover that already fits never moves.
    if (left + width > innerWidth - edge || left < edge) {
        const mirrored = box.left + box.width * (1 - af[0]) - width * (1 - tf[0]);
        if (mirrored >= edge && mirrored + width <= innerWidth - edge) left = mirrored;
    }
    if (top + height > floor && box.top > innerHeight - box.bottom) top = box.top - height;
    if (top < edge && box.bottom + height <= floor) top = box.bottom;
    left = Math.max(edge, Math.min(left, innerWidth - width - edge));
    top = Math.max(edge, Math.min(top, floor - height));
    Object.assign(surface.style, { position: 'fixed', left: `${left}px`, top: `${top}px`, transform: 'none' });
}
function close(state, restoreFocus) {
    if (!state.open || state.dismissing) return;
    state.dismissing = true;
    state.dotnet.invokeMethodAsync('Dismiss').finally(() => { state.dismissing = false; });
    if (restoreFocus && state.anchor instanceof HTMLElement && state.anchor.isConnected) state.anchor.focus({ preventScroll: true });
}
export function update(marker, surface, anchor, dotnet, open, anchorOrigin, transformOrigin, matchWidth, focusOnOpen, hostId) {
    // Blazor resolves an element reference to null once its element has left the page. A render that was replaced
    // while this module was still loading arrives here with nothing to position, so there is nothing to do.
    if (!surface?.isConnected) {
        if (surface) detach(surface);
        return;
    }
    let state = states.get(surface);
    if (!state) {
        state = { marker, surface, anchor: anchor || discoverAnchor(marker), dotnet, open: false };
        state.layout = () => position(state);
        state.pointer = event => {
            // A Sheet's dim area is the surface's own backdrop, so a press there reports the surface as its target: above the Sheet's top edge it is an outside press.
            if (opened.at(-1) === state && isSheetBackdropPress(surface, event)) { close(state, false); return; }
            if (opened.at(-1) !== state || surface.contains(event.target) || state.anchor?.contains(event.target)) return;
            // Descendant dialogs belong to this popup's journey and must not dismiss its ancestor accidentally.
            // The one exception is a live (non-modal) window such as the details overlay: it is not a dialog this popup opened, so a click
            // into it, from a menu on the player bar beside it, is an ordinary outside click.
            const targetDialog = event.target.closest?.('dialog[open]');
            if (targetDialog && !targetDialog.hasAttribute('data-overlay-window') && !state.anchor?.closest('dialog[open]')?.contains(event.target)) return;
            close(state, false);
        };
        state.key = event => {
            if (event.key !== 'Escape' || opened.at(-1) !== state) return;
            const targetDialog = event.target?.closest?.('dialog[open]');
            // A live window is not a dialog this popup opened: with a menu open, Escape closes the menu first wherever focus is.
            if (targetDialog && !targetDialog.hasAttribute('data-overlay-window') && targetDialog !== state.anchor?.closest?.('dialog[open]') && !surface.contains(event.target)) return;
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
