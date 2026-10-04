const states = new WeakMap();

const isFocusable = element => element instanceof HTMLElement && element !== document.body
    && element.isConnected && !element.hidden && element.getAttribute('aria-hidden') !== 'true'
    && element.tabIndex >= 0 && !element.closest('[inert], [aria-hidden="true"], fieldset:disabled')
    && getComputedStyle(element).visibility !== 'hidden' && element.getClientRects().length > 0;

const focusable = root => [...root.querySelectorAll(
    'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])')]
    .filter(isFocusable);

export function attachModal(root, previousActiveElement = document.activeElement) {
    if (!root || states.has(root)) return;
    const previous = isFocusable(previousActiveElement) ? previousActiveElement : null;
    const onKeyDown = event => {
        if (event.key === 'Escape') {
            if (root.querySelector('.mud-popover-open[data-playback-owned-menu]')) return;
            const close = root.querySelector('.playback-tool-sheet__close, [data-playback-sheet-close]');
            if (close) { event.preventDefault(); event.stopPropagation(); close.click(); }
            return;
        }
        if (event.key !== 'Tab') return;
        const items = focusable(root);
        if (!items.length) { event.preventDefault(); root.focus(); return; }
        const first = items[0], last = items[items.length - 1];
        if (event.shiftKey && (document.activeElement === first || !root.contains(document.activeElement))) {
            event.preventDefault(); last.focus();
        } else if (!event.shiftKey && (document.activeElement === last || !root.contains(document.activeElement))) {
            event.preventDefault(); first.focus();
        }
    };
    root.addEventListener('keydown', onKeyDown);
    states.set(root, { previous, onKeyDown });
    (focusable(root)[0] || root).focus();
}

export function restoreFocus(root, restorePrevious = true) {
    const state = states.get(root);
    if (!state) return;
    root.removeEventListener('keydown', state.onKeyDown);
    states.delete(root);
    const shouldRestore = restorePrevious && isFocusable(state.previous);
    if (shouldRestore) state.previous.focus();
}
