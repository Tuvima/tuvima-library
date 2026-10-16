// A row of links or buttons with one tab stop: the arrow keys, Home and End move focus between the items.
// The server renders which item holds tabindex 0 (the current one); this only moves focus between items.
const states = new WeakMap();

export function attach(container, itemSelector) {
    if (states.has(container)) return;
    const items = () => [...container.querySelectorAll(itemSelector)];
    const rtl = () => getComputedStyle(container).direction === 'rtl';
    const onKeyDown = event => {
        if (event.altKey || event.ctrlKey || event.metaKey) return;
        const list = items();
        const index = list.indexOf(document.activeElement);
        if (index < 0) return;
        const forward = rtl() ? 'ArrowLeft' : 'ArrowRight';
        const backward = rtl() ? 'ArrowRight' : 'ArrowLeft';
        let next = -1;
        if (event.key === forward) next = Math.min(list.length - 1, index + 1);
        else if (event.key === backward) next = Math.max(0, index - 1);
        else if (event.key === 'Home') next = 0;
        else if (event.key === 'End') next = list.length - 1;
        if (next < 0) return;
        event.preventDefault();
        if (next !== index) list[next].focus();
    };
    container.addEventListener('keydown', onKeyDown);
    states.set(container, { onKeyDown });
}

export function detach(container) {
    const state = states.get(container);
    if (!state) return;
    container.removeEventListener('keydown', state.onKeyDown);
    states.delete(container);
}
