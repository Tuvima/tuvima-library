const states = new WeakMap();

const focusable = root => [...root.querySelectorAll(
    'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])')]
    .filter(element => !element.hidden && element.getAttribute('aria-hidden') !== 'true'
        && element.tabIndex >= 0
        && !element.closest('[inert], [aria-hidden="true"], fieldset:disabled')
        && getComputedStyle(element).visibility !== 'hidden' && element.getClientRects().length > 0);

export function attachModal(root) {
    if (!root || states.has(root)) return;
    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const onKeyDown = event => {
        if (event.key === 'Escape') {
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

export function restoreFocus(root) {
    const state = states.get(root);
    if (!state) return;
    root.removeEventListener('keydown', state.onKeyDown);
    states.delete(root);
    if (state.previous?.isConnected) state.previous.focus();
}
