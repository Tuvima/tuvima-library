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
    return [...document.querySelectorAll(".mud-popover")]
        .find(element => element.classList.contains(token)
            && element.classList.contains("mud-popover-open")
            && visible(element)
            && visible(element.querySelector(".mud-list"))) ?? null;
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

function decorate(root, state) {
    if (attachedRoots.get(root) !== state) return;
    const trigger = [...root.querySelectorAll(".mud-select-input[tabindex]")].find(visible);
    const popover = locatePopover(state.token);

    if (trigger) {
        setOwned(state, trigger, "role", "combobox");
        setOwned(state, trigger, "aria-haspopup", "listbox");
        setOwned(state, trigger, "aria-label", state.label || "Playback speed");
        setOwned(state, trigger, "aria-expanded", popover ? "true" : "false");
        if (state.listId) setOwned(state, trigger, "aria-controls", state.listId);
    }

    if (!popover) return;
    const list = popover.querySelector(".mud-list");
    if (!list) return;
    if (!state.listId) state.listId = `playback-menu-options-${state.token}`;
    setOwned(state, list, "id", state.listId);
    setOwned(state, list, "role", "listbox");

    for (const option of list.querySelectorAll(".mud-list-item")) {
        // MudBlazor remains the source of selection truth. Never infer selection
        // from an ARIA value that this adapter may have written previously.
        const selected = option.classList.contains("mud-selected-item");
        setOwned(state, option, "role", "option");
        setOwned(state, option, "aria-selected", selected ? "true" : "false");
    }
}

function cleanup(root, state) {
    state.observer?.disconnect();
    for (const element of state.elements) {
        for (const [name, ownership] of state.originalAttributes.get(element) ?? []) {
            // Restore only an attribute value that is still ours. If MudBlazor
            // or another owner changed it since, leave that newer value intact.
            if (ownership.owned === null || element.getAttribute(name) !== ownership.owned) continue;
            if (ownership.original === null) element.removeAttribute(name);
            else setIfChanged(element, name, ownership.original);
        }
    }
    if (attachedRoots.get(root) === state) attachedRoots.delete(root);
}

export function attach(root, popoverClass, ariaLabel) {
    if (!(root instanceof HTMLElement)) return;

    const previous = attachedRoots.get(root);
    if (previous) cleanup(root, previous);

    const token = popoverToken(popoverClass);
    const state = {
        observer: null,
        token,
        label: ariaLabel,
        listId: token ? `playback-menu-options-${token}` : null,
        originalAttributes: new Map(),
        elements: []
    };
    attachedRoots.set(root, state);
    state.observer = new MutationObserver(() => decorate(root, state));
    state.observer.observe(document.documentElement, {
        attributes: true,
        childList: true,
        subtree: true,
        attributeFilter: ["class", "style", "aria-selected"]
    });
    decorate(root, state);
}

export function detach(root, popoverClass) {
    const state = attachedRoots.get(root);
    if (!state || state.token !== popoverToken(popoverClass)) return;
    cleanup(root, state);
}
