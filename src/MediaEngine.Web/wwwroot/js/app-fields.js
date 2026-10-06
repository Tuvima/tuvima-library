const attached = new WeakMap();
export function attachKeyboard(root) {
    if (!root || attached.has(root)) return;
    const onKey = event => {
        const trigger = event.target.closest('.tl-select-trigger');
        const autocomplete = event.target.matches('input[aria-autocomplete="list"]');
        if (!trigger && !autocomplete) return;
        const expanded = event.target.getAttribute('aria-expanded') === 'true';
        if (['ArrowDown', 'ArrowUp'].includes(event.key)
            || (['Home', 'End'].includes(event.key) && (trigger || expanded))
            || (trigger && ['Enter', ' '].includes(event.key))
            || (autocomplete && event.key === 'Enter' && expanded)) {
            event.preventDefault();
        }
        if (event.key === 'Escape' && event.target.getAttribute('aria-expanded') === 'true') {
            event.preventDefault();
        }
    };
    root.addEventListener('keydown', onKey);
    attached.set(root, onKey);
}
export function detachKeyboard(root) {
    const handler = attached.get(root);
    if (handler) root.removeEventListener('keydown', handler);
    attached.delete(root);
}
export function scrollActive(root) {
    const id = root.querySelector('[aria-activedescendant]')?.getAttribute('aria-activedescendant');
    if (!id) return;
    const option = document.getElementById(id);
    const list = option?.closest('.tl-select-options');
    if (!option || !list) return;
    const box = option.getBoundingClientRect();
    const bounds = list.getBoundingClientRect();
    if (box.top < bounds.top) list.scrollTop -= bounds.top - box.top;
    else if (box.bottom > bounds.bottom) list.scrollTop += box.bottom - bounds.bottom;
}
export function openFilePicker(input) { if (input && !input.disabled) input.click(); }
