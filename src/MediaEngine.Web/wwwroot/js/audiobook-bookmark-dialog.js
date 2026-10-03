const attachedRoots = new WeakMap();
const positionedDialogs = new WeakMap();
export const BOOKMARK_SWIPE_METRICS = Object.freeze({
    intentThreshold: 12,
    horizontalIntentRatio: 1.5,
    revealWidth: 48,
    actionTarget: 56,
});
export const isHorizontalBookmarkSwipe = (dx, dy) =>
    Math.abs(dx) >= BOOKMARK_SWIPE_METRICS.intentThreshold
    && Math.abs(dx) > Math.abs(dy) * BOOKMARK_SWIPE_METRICS.horizontalIntentRatio;

const excludedFromSwipe = target => Boolean(target?.closest?.(
    'input, textarea, select, button, [contenteditable], [data-bookmark-swipe-exclude]'));

export function attachBookmarkSwipe(root) {
    if (!root || attachedRoots.has(root)) return;
    root.style?.setProperty("--bookmark-swipe-reveal-width", `${BOOKMARK_SWIPE_METRICS.revealWidth}px`);
    root.style?.setProperty("--bookmark-swipe-action-target", `${BOOKMARK_SWIPE_METRICS.actionTarget}px`);

    let gesture = null;
    const activeTouchPointers = new Set();

    const onPointerDown = event => {
        if (event.pointerType !== "touch" || event.button !== 0) return;
        activeTouchPointers.add(event.pointerId);
        if (activeTouchPointers.size !== 1) {
            gesture = null;
            return;
        }
        if (excludedFromSwipe(event.target)) return;
        const row = event.target.closest?.("[data-bookmark-swipe-row]");
        if (!row || !root.contains(row)) return;
        gesture = {
            pointerId: event.pointerId,
            row,
            x: event.clientX,
            y: event.clientY,
            horizontal: false,
        };
    };

    const onPointerMove = event => {
        if (!gesture || event.pointerId !== gesture.pointerId) return;
        const dx = event.clientX - gesture.x;
        const dy = event.clientY - gesture.y;
        if (!isHorizontalBookmarkSwipe(dx, dy)) return;
        gesture.horizontal = true;
    };

    const onPointerUp = event => {
        activeTouchPointers.delete(event.pointerId);
        if (!gesture || event.pointerId !== gesture.pointerId) return;
        const { row, x, y, horizontal } = gesture;
        const dx = event.clientX - x;
        const dy = event.clientY - y;
        if (horizontal && isHorizontalBookmarkSwipe(dx, dy)) {
            root.querySelectorAll("[data-bookmark-swipe-row]").forEach(candidate => {
                candidate.dataset.swipeRevealed = candidate === row && dx < 0 ? "true" : "false";
            });
        }
        gesture = null;
    };

    const onPointerCancel = event => {
        activeTouchPointers.delete(event.pointerId);
        if (!gesture || event.pointerId !== gesture.pointerId) return;
        if (gesture.horizontal) gesture.row.dataset.swipeRevealed = "false";
        gesture = null;
    };

    const onClick = event => {
        if (!event.target.closest?.("[data-bookmark-swipe-action]")) return;
        event.target.closest("[data-bookmark-swipe-row]")?.setAttribute("data-swipe-revealed", "false");
    };

    root.addEventListener("pointerdown", onPointerDown, { passive: true });
    root.addEventListener("pointermove", onPointerMove, { passive: true });
    root.addEventListener("pointerup", onPointerUp, { passive: true });
    root.addEventListener("pointercancel", onPointerCancel, { passive: true });
    root.addEventListener("click", onClick);

    attachedRoots.set(root, () => {
        root.removeEventListener("pointerdown", onPointerDown);
        root.removeEventListener("pointermove", onPointerMove);
        root.removeEventListener("pointerup", onPointerUp);
        root.removeEventListener("pointercancel", onPointerCancel);
        root.removeEventListener("click", onClick);
        activeTouchPointers.clear();
        gesture = null;
    });
}

export function hideBookmarkSwipe(root) {
    root?.querySelectorAll?.("[data-bookmark-swipe-row]").forEach(row => {
        row.setAttribute("data-swipe-revealed", "false");
    });
}

export function detachBookmarkSwipe(root) {
    const detach = root && attachedRoots.get(root);
    if (!detach) return;
    detach();
    attachedRoots.delete(root);
}

export function positionBookmarkDialog(layer, trigger, isMobile) {
    const sheet = layer?.querySelector?.(".audiobook-bookmark-dialog-sheet");
    if (!layer || !sheet) return;
    let positionState = positionedDialogs.get(layer);
    if (!positionState) {
        positionState = { trigger, isMobile, sheet, reposition: null };
        positionState.reposition = () => positionBookmarkDialogCore(layer, positionState);
        window.addEventListener("resize", positionState.reposition, { passive: true });
        window.addEventListener("scroll", positionState.reposition, { passive: true, capture: true });
        positionedDialogs.set(layer, positionState);
    }
    positionState.trigger = trigger;
    positionState.isMobile = isMobile;
    positionState.sheet = sheet;
    positionBookmarkDialogCore(layer, positionState);
}

function positionBookmarkDialogCore(layer, { trigger, isMobile, sheet }) {
    const narrow = isMobile || window.matchMedia?.("(max-width: 720px)").matches;
    layer.classList.toggle("is-anchored", !narrow && trigger instanceof HTMLElement && trigger.isConnected);
    if (narrow || !(trigger instanceof HTMLElement) || !trigger.isConnected) return;

    const rect = trigger.getBoundingClientRect();
    const margin = 12;
    const gap = 8;
    const width = Math.min(360, window.innerWidth - margin * 2);
    const left = Math.max(margin, Math.min(rect.left, window.innerWidth - width - margin));
    const viewportRoom = Math.max(0, window.innerHeight - margin * 2);
    const below = Math.min(viewportRoom, Math.max(0, window.innerHeight - rect.bottom - gap - margin));
    const above = Math.min(viewportRoom, Math.max(0, rect.top - gap - margin));
    const placeBelow = below >= above;
    const availableHeight = placeBelow ? below : above;
    const height = Math.min(480, availableHeight);
    const requestedTop = placeBelow ? rect.bottom + gap : rect.top - gap - height;
    const top = Math.max(margin, Math.min(requestedTop, window.innerHeight - margin - height));
    layer.style.setProperty("--bookmark-anchor-left", `${left}px`);
    layer.style.setProperty("--bookmark-anchor-top", `${top}px`);
    layer.style.setProperty("--bookmark-anchor-width", `${width}px`);
    layer.style.setProperty("--bookmark-anchor-height", `${height}px`);
}

export function detachBookmarkDialogPosition(layer) {
    const state = layer && positionedDialogs.get(layer);
    if (!state) return;
    window.removeEventListener("resize", state.reposition);
    window.removeEventListener("scroll", state.reposition, true);
    positionedDialogs.delete(layer);
}
