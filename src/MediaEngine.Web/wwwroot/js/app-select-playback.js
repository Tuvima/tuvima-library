import { attach as attachTooltip, detach as detachTooltip } from './playback-tooltip.js';
const attachedRoots = new WeakMap();
const playbackPopoverPrefix = "app-select__playback-menu-";

function setIfChanged(element, name, value) {
    if (element.getAttribute(name) !== value) element.setAttribute(name, value);
}

function visible(element) {
    if (!(element instanceof HTMLElement) || !element.isConnected
        || element.closest("[hidden], [inert], [aria-hidden='true']")) return false;
    const style = getComputedStyle(element);
    return style.display !== "none" && style.visibility !== "hidden" && element.getClientRects().length > 0;
}

function popoverToken(popoverClass) {
    return String(popoverClass ?? "").split(/\s+/).find(name => name.startsWith(playbackPopoverPrefix)) ?? null;
}

function locatePopover(token) {
    if (!token) return null;
    return [...document.querySelectorAll(".tl-popover")]
        .find(element => element.classList.contains(token)
            && element.classList.contains("tl-popover-open")
            && visible(element)
            && visible(element.querySelector(".tl-list"))) ?? null;
}

function remember(state, element, name) {
    let attributes = state.originalAttributes.get(element);
    if (!attributes) {
        attributes = new Map();
        state.originalAttributes.set(element, attributes);
        state.elements.push(element);
    }
    if (!attributes.has(name)) attributes.set(name, { original: element.getAttribute(name), owned: null });
    return attributes.get(name);
}

function setOwned(state, element, name, value) {
    const ownership = remember(state, element, name);
    setIfChanged(element, name, value);
    ownership.owned = value;
}

function restorePortal(state) {
    state.nativePanel?.removeAttribute('data-playback-owned-menu');
    state.nativePanel = null;
    state.portalPanel?.removeAttribute('data-playback-owned-menu');
    if (state.portalMarker?.parentNode && state.portalPanel) state.portalMarker.replaceWith(state.portalPanel);
    if (state.portalPanel && state.portalStyle !== null) state.portalPanel.setAttribute('style', state.portalStyle);
    else state.portalPanel?.removeAttribute('style');
    state.portalMarker = null; state.portalPanel = null; state.portalStyle = null;
}

function positionInFullscreen(root, state, trigger, popover) {
    const fullscreen = document.fullscreenElement;
    const parentPanel = root.dataset.playbackParentPanel ? document.getElementById(root.dataset.playbackParentPanel) : null;
    const container = parentPanel || (fullscreen?.contains(root) ? fullscreen : null);
    if (!popover || !trigger || !container) {
        if (state.portalPanel) restorePortal(state);
        return;
    }
    // Native popovers already occupy the browser top layer. Keep their DOM in
    // the Razor owner; only positioning belongs to this playback adapter.
    const nativeSurface = popover.hasAttribute('popover');
    if (nativeSurface) {
        if (state.portalPanel || state.nativePanel) restorePortal(state);
        state.nativePanel = popover;
    } else if (state.portalPanel !== popover) {
        restorePortal(state);
        state.portalPanel = popover;
        state.portalStyle = popover.getAttribute('style');
        state.portalMarker = document.createComment('playback-select-position');
        popover.before(state.portalMarker);
    }
    if (!nativeSurface && popover.parentNode !== container) container.append(popover);
    if (parentPanel) popover.setAttribute('data-playback-owned-menu', parentPanel.id);
    const box = trigger.getBoundingClientRect();
    const width = Math.min(popover.getBoundingClientRect().width || 240, innerWidth - 16);
    const maxHeight = Math.max(44, Math.min(innerHeight * .7, 640, box.top - 16));
    const left = Math.max(8, Math.min(box.right - width, innerWidth - width - 8));
    const top = Math.max(8, box.top - Math.min(popover.scrollHeight, maxHeight) - 8);
    for (const [property, value] of Object.entries({ position: 'fixed', left: `${left}px`, top: `${top}px`, transform: 'none', maxHeight: `${maxHeight}px`, overflow: 'auto' })) {
        if (popover.style[property] !== value) popover.style[property] = value;
    }
}

function decorate(root, state) {
    if (attachedRoots.get(root) !== state) return;
    const trigger = [...root.querySelectorAll(".tl-select-trigger")].find(visible);
    const popover = locatePopover(state.token);
    positionInFullscreen(root, state, trigger, popover);

    if (trigger) {
        setOwned(state, trigger, "role", "combobox");
        setOwned(state, trigger, "aria-haspopup", "listbox");
        setOwned(state, trigger, "aria-label", state.label || "Playback speed");
        setOwned(state, trigger, "aria-expanded", popover ? "true" : "false");
        if (state.listId) setOwned(state, trigger, "aria-controls", state.listId);
    }

    if (!popover) return;
    const list = popover.querySelector(".tl-list");
    if (!list) return;
    if (!state.listId) state.listId = `playback-menu-options-${state.token}`;
    setOwned(state, list, "id", state.listId);
    setOwned(state, list, "role", "listbox");

    for (const option of list.querySelectorAll(".tl-list-item")) {
        // the select remains the source of selection truth. Never infer selection
        // from an ARIA value that this adapter may have written previously.
        const selected = option.classList.contains("tl-selected-item");
        setOwned(state, option, "role", "option");
        setOwned(state, option, "aria-selected", selected ? "true" : "false");
    }
}

function cleanup(root, state) {
    detachTooltip(root);
    state.observer?.disconnect();
    state.removeListeners?.();
    restorePortal(state);
    for (const element of state.elements) {
        for (const [name, ownership] of state.originalAttributes.get(element) ?? []) {
            // Restore only an attribute value that is still ours. If the select
            // or another owner changed it since, leave that newer value intact.
            if (ownership.owned === null || element.getAttribute(name) !== ownership.owned) continue;
            if (ownership.original === null) element.removeAttribute(name);
            else setIfChanged(element, name, ownership.original);
        }
    }
    if (attachedRoots.get(root) === state) attachedRoots.delete(root);
}

export function attach(root, popoverClass, ariaLabel, dotNetRef) {
    if (!(root instanceof HTMLElement)) return;

    const token = popoverToken(popoverClass);
    const previous = attachedRoots.get(root);
    if (previous?.token === token && previous.label === ariaLabel) { previous.dotNetRef = dotNetRef; return; }
    if (previous) cleanup(root, previous);
    const state = {
        observer: null,
        dotNetRef,
        token,
        label: ariaLabel,
        listId: token ? `playback-menu-options-${token}` : null,
        originalAttributes: new Map(),
        elements: []
    };
    attachedRoots.set(root, state);
    attachTooltip(root);
    state.observer = new MutationObserver(() => decorate(root, state));
    state.observer.observe(document.documentElement, {
        attributes: true,
        childList: true,
        subtree: true,
        attributeFilter: ["class", "style", "aria-selected"]
    });
    const reposition = () => decorate(root, state);
    window.addEventListener('resize', reposition);
    window.addEventListener('scroll', reposition, true);
    document.addEventListener('fullscreenchange', reposition);
    const nestedEscape = event => {
        if (event.key !== 'Escape' || !root.dataset.playbackParentPanel || !state.dotNetRef || !locatePopover(state.token)) return;
        const panel = document.getElementById(root.dataset.playbackParentPanel);
        if (!panel?.contains(event.target)) return;
        event.preventDefault(); event.stopImmediatePropagation();
        Promise.resolve(state.dotNetRef.invokeMethodAsync('ClosePlaybackMenuAsync')).catch(() => {});
    };
    document.addEventListener('keydown', nestedEscape, true);
    state.removeListeners = () => {
        window.removeEventListener('resize', reposition);
        window.removeEventListener('scroll', reposition, true);
        document.removeEventListener('fullscreenchange', reposition);
        document.removeEventListener('keydown', nestedEscape, true);
    };
    decorate(root, state);
}

export function detach(root, popoverClass) {
    const state = attachedRoots.get(root);
    if (!state || state.token !== popoverToken(popoverClass)) return;
    cleanup(root, state);
}
