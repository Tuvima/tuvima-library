// Reordering a list by finger (spec listen-playlists WP5, issue 1201). A finger cannot start the browser's own drag and drop, and a whole row
// cannot be the handle (the page must still scroll under a finger that lands on a row), so each reorderable row carries a grip. Pressing the grip
// with a finger or pen follows the pointer, marks the row it is over (a line before or after it by which half the finger is in), keeps the
// list scrolling while the finger is held near its edge, and releasing reports the drop. A mouse is left to the browser's own drag, which
// each list already has. The grip carries touch-action: none (and only the grip does) so the page does not scroll from it.
// Used by the song table (song-table.js), the queue (playback-queue-drag.js) and the playlist lists (playlist-reorder.js).

export const DRAGGING_CLASS = 'is-touch-dragging';
export const BEFORE_CLASS = 'is-touch-drop-before';
export const AFTER_CLASS = 'is-touch-drop-after';
export const EDGE = 48;
export const SCROLL_STEP = 14;
export const SCROLL_INTERVAL = 30;

// Which side of a row a finger at `clientY` points at: before it in its upper half, after it in its lower half.
export function placementFor(clientY, box) {
    return clientY < box.top + box.height / 2 ? 'before' : 'after';
}

// -1 near the top edge of the visible area, 1 near the bottom edge, 0 elsewhere (and 0 when the area is not known).
export function edgeDirection(y, top, bottom, edge = EDGE) {
    if (!Number.isFinite(top) || !Number.isFinite(bottom)) return 0;
    if (y < top + edge) return -1;
    if (y > bottom - edge) return 1;
    return 0;
}

// The nearest ancestor that scrolls, or null when the page itself does.
export function scrollParent(element) {
    const view = element?.ownerDocument?.defaultView;
    for (let current = element?.parentElement; current && view; current = current.parentElement) {
        const overflow = view.getComputedStyle(current).overflowY;
        if ((overflow === 'auto' || overflow === 'scroll') && current.scrollHeight > current.clientHeight) return current;
    }

    return null;
}

// Wires one list. `root` holds the rows. options:
//   handle    selector of the grip inside a row
//   row       selector of a row (the grip's closest row, and what is under the finger)
//   keyOf     (row) => string | null, what names a row (default: its data-touch-reorder-key)
//   onStart   (key) => void, called when a finger takes a grip
//   onDrop    (fromKey, toKey, placement) => void, called once on release over another row; placement is 'before' or 'after'
//   onCancel  () => void, called when the drag ends without a drop (cancelled, or released away from every row)
// Returns a function that removes everything it added.
export function wire(root, options) {
    if (!root) return () => { };
    const { handle: handleSelector, row: rowSelector, onStart, onDrop, onCancel } = options;
    const keyOf = options.keyOf ?? (row => row?.dataset?.touchReorderKey ?? null);
    const doc = root.ownerDocument;
    let touch = null;

    const rowsOf = () => Array.from(root.querySelectorAll?.(rowSelector) ?? []);
    const rowFor = key => rowsOf().find(row => keyOf(row) === key) ?? null;

    const clearMarks = () => {
        if (!touch) return;
        touch.marked?.classList.remove(BEFORE_CLASS, AFTER_CLASS);
        touch.marked = null;
    };

    const targetAt = (x, y) => {
        const hit = doc?.elementFromPoint?.(x, y);
        const row = hit?.closest?.(rowSelector) ?? null;
        if (!row || !(root.contains?.(row) ?? true)) return null;
        const key = keyOf(row);
        return key === null || key === undefined ? null : { row, key, placement: placementFor(y, row.getBoundingClientRect()) };
    };

    // The line shows where the song would land; it is the same for a move and for the list scrolling under a finger that is held still.
    const mark = () => {
        clearMarks();
        const target = targetAt(touch.x, touch.y);
        if (!target || target.key === touch.key) return;
        target.row.classList.add(target.placement === 'before' ? BEFORE_CLASS : AFTER_CLASS);
        touch.marked = target.row;
    };

    const area = () => {
        if (touch.scroller) {
            const box = touch.scroller.getBoundingClientRect();
            return { top: box.top, bottom: box.bottom };
        }

        const height = doc?.defaultView?.innerHeight;
        return { top: 0, bottom: typeof height === 'number' ? height : Number.NaN };
    };

    const direction = () => {
        const { top, bottom } = area();
        return edgeDirection(touch.y, top, bottom);
    };

    const stopScroll = () => {
        if (touch?.timer) {
            clearInterval(touch.timer);
            touch.timer = null;
        }
    };

    // A finger held still at the edge keeps the list scrolling: a timer, not the pointer's moves, drives it.
    const scrollTick = () => {
        const step = touch ? direction() : 0;
        if (!touch || step === 0) {
            stopScroll();
            return;
        }

        if (touch.scroller) touch.scroller.scrollTop += step * SCROLL_STEP;
        else doc?.defaultView?.scrollBy?.(0, step * SCROLL_STEP);
        mark();
    };

    const updateScroll = () => {
        if (direction() === 0) stopScroll();
        else if (!touch.timer) touch.timer = setInterval(scrollTick, SCROLL_INTERVAL);
    };

    const detachDocument = () => {
        doc?.removeEventListener?.('pointerup', onPointerUp, true);
        doc?.removeEventListener?.('pointercancel', onPointerCancel, true);
    };

    const end = () => {
        if (!touch) return;
        stopScroll();
        clearMarks();
        const { handle, pointerId, key } = touch;
        touch = null;
        detachDocument();
        try { handle.releasePointerCapture?.(pointerId); } catch { /* the capture was already gone */ }
        // The row may have been redrawn or recycled meanwhile, so it is found again by its key.
        rowFor(key)?.classList.remove(DRAGGING_CLASS);
    };

    const onPointerDown = event => {
        if (touch || event.pointerType === 'mouse' || (event.button ?? 0) > 0) return;
        const handle = event.target?.closest?.(handleSelector);
        const row = handle?.closest?.(rowSelector);
        if (!handle || !row || !(root.contains?.(handle) ?? true)) return;
        const key = keyOf(row);
        if (key === null || key === undefined) return;
        touch = {
            handle,
            key,
            pointerId: event.pointerId,
            scroller: scrollParent(handle),
            x: event.clientX ?? 0,
            y: event.clientY ?? 0,
            marked: null,
            timer: null,
        };
        try { handle.setPointerCapture?.(event.pointerId); } catch { /* the pointer ended before it could be captured */ }
        row.classList.add(DRAGGING_CLASS);
        // The grip can leave the page while the list scrolls (a long list draws only the rows near the screen), which ends the capture; the
        // end of the touch is then heard on the document instead.
        doc?.addEventListener?.('pointerup', onPointerUp, true);
        doc?.addEventListener?.('pointercancel', onPointerCancel, true);
        onStart?.(key);
    };

    const onPointerMove = event => {
        if (!touch || event.pointerId !== touch.pointerId) return;
        event.preventDefault?.();
        touch.x = event.clientX;
        touch.y = event.clientY;
        mark();
        updateScroll();
    };

    function onPointerUp(event) {
        if (!touch || event.pointerId !== touch.pointerId) return;
        const from = touch.key;
        const target = targetAt(event.clientX ?? touch.x, event.clientY ?? touch.y);
        end();
        if (target && target.key !== from) onDrop?.(from, target.key, target.placement);
        else onCancel?.();
    }

    // Only the finger that started the drag can cancel it: a second finger's cancel (a palm, a gesture) leaves it going.
    function onPointerCancel(event) {
        if (!touch || event.pointerId !== touch.pointerId) return;
        end();
        onCancel?.();
    }

    // A long press on a grip must not open the row's own menu or the browser's menu.
    const onContextMenu = event => {
        if (event.target?.closest?.(handleSelector)) {
            event.preventDefault?.();
            event.stopPropagation?.();
        }
    };

    root.addEventListener('pointerdown', onPointerDown);
    root.addEventListener('pointermove', onPointerMove);
    root.addEventListener('pointerup', onPointerUp);
    root.addEventListener('pointercancel', onPointerCancel);
    root.addEventListener('contextmenu', onContextMenu, true);
    return () => {
        root.removeEventListener('pointerdown', onPointerDown);
        root.removeEventListener('pointermove', onPointerMove);
        root.removeEventListener('pointerup', onPointerUp);
        root.removeEventListener('pointercancel', onPointerCancel);
        root.removeEventListener('contextmenu', onContextMenu, true);
        end();
    };
}

// The same wiring for a .NET component: `config` names the grip and the row and the two .NET methods to call. The start is told first
// (so the component can keep the queue as it was when the finger landed) and the drop after it; calls to .NET are in order.
export function wireToDotnet(root, dotnetRef, config) {
    if (!root || !dotnetRef) return () => { };
    const call = (method, ...args) => {
        if (!method) return;
        Promise.resolve(dotnetRef.invokeMethodAsync(method, ...args)).catch(error => {
            console.warn('touch-reorder: could not tell the page about the move', error);
        });
    };

    return wire(root, {
        handle: config.handle,
        row: config.row,
        keyOf: row => row?.getAttribute?.(config.keyAttribute) ?? null,
        onStart: key => call(config.startMethod, key),
        onDrop: (from, to, placement) => call(config.dropMethod, from, to, placement === 'after'),
        onCancel: () => call(config.cancelMethod),
    });
}
