// Keeps the highlighted row of the shared browser frame (AppBrowserFrame) in view while the arrow keys move through a long list.
// The list is a grid whose focus stays on the table itself (aria-activedescendant), so the browser does not scroll to the row on its own.
export function scrollRowIntoView(rowId) {
    if (typeof rowId !== 'string' || rowId.length === 0) return;
    document.getElementById(rowId)?.scrollIntoView({ block: 'nearest' });
}

// The keys the list's own keyboard model takes. The browser would also scroll the list with them, so they are not left to do both.
const handled = new Set(['ArrowUp', 'ArrowDown', 'Home', 'End', ' ']);
const listeners = new WeakMap();

// Stops the native scroll for those keys, but only when the list itself has focus: a key typed in a field or on a button inside a row is left alone.
export function attachListKeys(list) {
    if (!list || listeners.has(list)) return;
    const onKeyDown = event => {
        if (event.target === list && handled.has(event.key) && !event.altKey && !event.ctrlKey && !event.metaKey) event.preventDefault();
    };
    list.addEventListener('keydown', onKeyDown);
    listeners.set(list, onKeyDown);
}

export function detachListKeys(list) {
    const onKeyDown = list ? listeners.get(list) : null;
    if (!onKeyDown) return;
    list.removeEventListener('keydown', onKeyDown);
    listeners.delete(list);
}
