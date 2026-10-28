// Drag a floating panel by its header (the Artwork picker, universal editor spec D8). The panel starts fixed to a corner of the
// window; the first drag switches it to explicit left/top so it follows the pointer, and it is kept inside the window.
const states = new WeakMap();

// Clamp a panel position so at least the header stays inside the window.
export function clampPosition(left, top, width, height, viewportWidth, viewportHeight) {
    const maxLeft = Math.max(0, viewportWidth - width);
    const maxTop = Math.max(0, viewportHeight - Math.min(height, 48));
    return {
        left: Math.min(Math.max(0, left), maxLeft),
        top: Math.min(Math.max(0, top), maxTop),
    };
}

function isInteractive(target) {
    return !!target?.closest?.('button, a, input, select, textarea, [role="button"]');
}

export function attach(root, handle) {
    if (!root || !handle || states.has(root)) return;
    let drag = null;

    const onPointerDown = event => {
        if (event.button !== 0 || isInteractive(event.target)) return;
        const rect = root.getBoundingClientRect();
        drag = { dx: event.clientX - rect.left, dy: event.clientY - rect.top, id: event.pointerId };
        root.style.left = `${rect.left}px`;
        root.style.top = `${rect.top}px`;
        root.style.right = 'auto';
        root.style.bottom = 'auto';
        handle.setPointerCapture?.(event.pointerId);
        event.preventDefault();
    };

    const onPointerMove = event => {
        if (!drag || event.pointerId !== drag.id) return;
        const rect = root.getBoundingClientRect();
        const next = clampPosition(
            event.clientX - drag.dx,
            event.clientY - drag.dy,
            rect.width,
            rect.height,
            root.ownerDocument.documentElement.clientWidth,
            root.ownerDocument.documentElement.clientHeight);
        root.style.left = `${next.left}px`;
        root.style.top = `${next.top}px`;
    };

    const onPointerUp = event => {
        if (!drag || event.pointerId !== drag.id) return;
        handle.releasePointerCapture?.(event.pointerId);
        drag = null;
    };

    handle.addEventListener('pointerdown', onPointerDown);
    handle.addEventListener('pointermove', onPointerMove);
    handle.addEventListener('pointerup', onPointerUp);
    handle.addEventListener('pointercancel', onPointerUp);
    states.set(root, { handle, onPointerDown, onPointerMove, onPointerUp });
}

export function detach(root) {
    const state = states.get(root);
    if (!state) return;
    state.handle.removeEventListener('pointerdown', state.onPointerDown);
    state.handle.removeEventListener('pointermove', state.onPointerMove);
    state.handle.removeEventListener('pointerup', state.onPointerUp);
    state.handle.removeEventListener('pointercancel', state.onPointerUp);
    states.delete(root);
}
