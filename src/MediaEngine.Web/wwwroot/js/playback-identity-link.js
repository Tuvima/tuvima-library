const states = new WeakMap();
export function attach(link, receiver) {
    if (!link || states.has(link)) return;
    const click = event => {
        if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        event.preventDefault(); event.stopPropagation();
        receiver.invokeMethodAsync('ActivateAsync').catch(() => {});
    };
    link.addEventListener('click', click);
    states.set(link, click);
}
export function detach(link) { const click = states.get(link); if (click) link.removeEventListener('click', click); states.delete(link); }
