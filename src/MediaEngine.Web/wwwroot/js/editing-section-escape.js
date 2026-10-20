// Esc for an Editing Mode section slot on a page that is not inside the details overlay (a Universe). Inside the overlay,
// app-dialog.js hears Esc and the overlay offers it to the page; out here nothing does, so the slot listens itself, with the
// same rules as app-dialog.js: an open popup (a select's list, a menu) and a control that uses Esc itself keep their key.
const states = new WeakMap();

function ownedByField(target) {
    return !!target?.closest?.('[data-app-escape-owner="field"]')
        || target?.tagName === 'SELECT'
        || (target?.tagName === 'INPUT' && ['search', 'date', 'time', 'datetime-local', 'month', 'week', 'color', 'file'].includes(target.type));
}

// True when this Esc is the slot's to use: not already handled, not mid-composition, no popup open, and the focused control
// does not use Esc itself.
export function shouldHandleEscape(event, doc) {
    if (event.key !== 'Escape' || event.defaultPrevented || event.isComposing) return false;
    if (doc?.querySelector?.('.tl-popover-open:not([hidden])')) return false;
    return !ownedByField(event.target);
}

export function attach(root, dotnet) {
    if (states.has(root)) return;
    const onKeyDown = event => {
        if (!shouldHandleEscape(event, root.ownerDocument ?? globalThis.document)) return;
        Promise.resolve(dotnet.invokeMethodAsync('HandleEscapeFromBrowser'))
            .catch(error => console.error('Section Escape handling failed.', error));
    };
    root.addEventListener('keydown', onKeyDown, true);
    states.set(root, { onKeyDown });
}

export function detach(root) {
    const state = states.get(root);
    if (!state) return;
    root.removeEventListener('keydown', state.onKeyDown, true);
    states.delete(root);
}
