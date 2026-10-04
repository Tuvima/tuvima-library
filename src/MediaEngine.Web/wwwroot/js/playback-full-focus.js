const boundaries = new WeakMap();
const focusableSelector = 'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])';

function visible(element) {
    return element instanceof HTMLElement && element.isConnected && !element.hidden
        && element.getAttribute('aria-hidden') !== 'true'
        && !element.closest('[inert], [aria-hidden="true"], fieldset:disabled')
        && getComputedStyle(element).visibility !== 'hidden' && element.getClientRects().length > 0;
}

function controls(container) {
    return [...container.querySelectorAll(focusableSelector)].filter(element => visible(element) && element.tabIndex >= 0);
}

function ownedMenus(root) {
    return [...root.querySelectorAll('[role="combobox"][aria-controls]')]
        .map(trigger => document.getElementById(trigger.getAttribute('aria-controls'))?.closest('.mud-popover-open'))
        .filter(menu => visible(menu));
}

function activeModal() {
    return [...document.querySelectorAll('[role="dialog"][aria-modal="true"]')].filter(visible).at(-1);
}

export function attach(root, subject, focusInitially = false) {
    if (!(root instanceof HTMLElement) || !root.isConnected) return;
    const previous = boundaries.get(root);
    if (previous?.subject === subject) return;
    if (previous) detach(root);
    const onKeyDown = event => {
        if (event.key !== 'Tab') return;
        if (!visible(root)) { if (!root.isConnected) detach(root); return; }
        // Sheets and host dialogs already own their modal focus. In particular,
        // bookmarks remain outside this component and must retain their own trap.
        const modal = activeModal();
        if (modal) {
            if (modal.contains(document.activeElement) || ownedMenus(modal).some(menu => menu.contains(document.activeElement))) return;
            event.preventDefault(); event.stopImmediatePropagation();
            const items = controls(modal);
            (items.length ? items[event.shiftKey ? items.length - 1 : 0] : modal).focus();
            return;
        }
        const targets = [...new Set([...controls(root), ...ownedMenus(root).flatMap(controls)])];
        event.preventDefault();
        event.stopImmediatePropagation();
        if (!targets.length) { root.focus(); return; }
        const index = targets.indexOf(document.activeElement);
        const next = index < 0 ? (event.shiftKey ? targets.length - 1 : 0)
            : (index + (event.shiftKey ? -1 : 1) + targets.length) % targets.length;
        targets[next].focus();
    };
    document.addEventListener('keydown', onKeyDown, true);
    boundaries.set(root, { subject, onKeyDown });
    // The main phone host captures Expand before it focuses the player. The
    // popup has no parent presentation host, so it establishes initial focus here.
    if ((focusInitially || previous) && !activeModal() && !root.contains(document.activeElement)
        && !ownedMenus(root).some(menu => menu.contains(document.activeElement))) root.focus();
}

export function detach(root) {
    const state = boundaries.get(root);
    if (!state) return;
    document.removeEventListener('keydown', state.onKeyDown, true);
    boundaries.delete(root);
}
