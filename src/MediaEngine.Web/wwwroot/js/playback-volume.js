// The volume slider's reveal (Unified player v2 D10). Hovering the speaker or moving keyboard focus onto it opens a short horizontal slider beside it;
// it stays open while the pointer or focus is on the control (and while the handle is held down), and closes 400 ms after both have left.
// The open state is one attribute on the control (data-open), which the stylesheet reads, so the busy player never re-renders for it.
export const LINGER_MS = 400;

/**
 * The reveal rules, apart from the page: pointer and focus each keep the slider open, leaving both starts the linger, and coming back before
 * it ends cancels the close. The page passes its own timer functions so the timing can be tested without waiting.
 */
export function createReveal({ show, hide, lingerMs = LINGER_MS, setTimer = globalThis.setTimeout, clearTimer = globalThis.clearTimeout }) {
    let pointer = false, focus = false, held = false, timer = null, open = false;
    const cancel = () => { if (timer !== null) { clearTimer(timer); timer = null; } };
    const reveal = () => { cancel(); if (!open) { open = true; show(); } };
    const settle = () => {
        if (pointer || focus || held || !open) return;
        cancel();
        timer = setTimer(() => { timer = null; if (!pointer && !focus && !held && open) { open = false; hide(); } }, lingerMs);
    };
    return {
        pointerEnter() { pointer = true; reveal(); },
        pointerLeave() { pointer = false; settle(); },
        focusIn() { focus = true; reveal(); },
        focusOut() { focus = false; settle(); },
        press() { held = true; reveal(); },
        release() { held = false; settle(); },
        get isOpen() { return open; },
        dispose() { cancel(); },
    };
}

const states = new WeakMap();
export function attach(root) {
    if (!root || states.has(root)) return;
    const reveal = createReveal({
        show: () => { root.dataset.open = 'true'; },
        hide: () => { delete root.dataset.open; },
    });
    const listeners = [], listen = (target, name, fn) => { target.addEventListener(name, fn); listeners.push(() => target.removeEventListener(name, fn)); };
    // A finger never lingers: touch opens the panel instead (the page draws a different control then), so only a mouse or pen reveals the slider.
    listen(root, 'pointerenter', e => { if (e.pointerType !== 'touch') reveal.pointerEnter(); });
    listen(root, 'pointerleave', e => { if (e.pointerType !== 'touch') reveal.pointerLeave(); });
    listen(root, 'focusin', e => { if (e.target?.matches?.(':focus-visible') !== false) reveal.focusIn(); });
    listen(root, 'focusout', e => { if (!root.contains?.(e.relatedTarget)) reveal.focusOut(); });
    // ArrowUp and ArrowDown on the speaker step the volume (the page handles them); the page behind must not scroll with them.
    listen(root, 'keydown', e => { if ((e.key === 'ArrowUp' || e.key === 'ArrowDown') && e.target?.closest?.('[data-volume-keys]')) e.preventDefault(); });
    listen(root, 'pointerdown', e => { if (e.pointerType !== 'touch') reveal.press(); });
    listen(globalThis.window ?? globalThis, 'pointerup', () => reveal.release());
    listen(globalThis.window ?? globalThis, 'pointercancel', () => reveal.release());
    states.set(root, { listeners, reveal });
}
export function detach(root) {
    const state = states.get(root);
    if (!state) return;
    state.listeners.forEach(remove => remove());
    state.reveal.dispose();
    delete root.dataset.open;
    states.delete(root);
}
