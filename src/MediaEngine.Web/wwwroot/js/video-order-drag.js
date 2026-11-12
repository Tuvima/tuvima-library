// Drag and drop for the videos of a series in the editor's Match tool (universal editor v3.1, D7). A grip handle on each row starts the
// drag; a line shows where the video will land; dropping tells the page which gap was chosen and the page stages the new order exactly as
// the arrow buttons do. A video can also be dropped on a section row of the tree on the left. No library: plain drag events.
// A finger cannot start an HTML5 drag, so the same handle also drags by pointer events (universal editor v6, D21): pressing the handle with a
// finger or pen follows the pointer, marks the gap it is over the same way, and releasing there reports the same drop. The handle carries
// touch-action: none so the page does not scroll under the finger, and the list scrolls itself when the finger nears its edge.
// Rows carry data-vo-list and data-vo-row; the tree's section rows carry data-vo-drop-parent.
const MIME = 'application/x-tuvima-video';
let active = null;

function clearMarks() {
    document.querySelectorAll('.is-drop-before, .is-drop-after, .is-drop-parent').forEach(element =>
        element.classList.remove('is-drop-before', 'is-drop-after', 'is-drop-parent'));
}

function insertionPoint(row, event) {
    const index = Number(row.dataset.voRow);
    const box = row.getBoundingClientRect();
    return event.clientY < box.top + box.height / 2 ? index : index + 1;
}

const EDGE = 48;
const SCROLL_STEP = 14;

// The nearest ancestor that scrolls, so a long list can be dragged past its visible rows.
function scrollParent(element) {
    const view = element?.ownerDocument?.defaultView;
    for (let current = element?.parentElement; current && view; current = current.parentElement) {
        const overflow = view.getComputedStyle(current).overflowY;
        if ((overflow === 'auto' || overflow === 'scroll') && current.scrollHeight > current.clientHeight) return current;
    }

    return null;
}

export function attach(root, dotnet) {
    if (!root || root.__voAttached) return;
    root.__voAttached = true;

    // A drag by finger or pen: the handle is captured, so every move and the release come back to the list.
    let touch = null;

    const targetUnder = event => {
        const hit = root.ownerDocument.elementFromPoint?.(event.clientX, event.clientY);
        return { row: hit?.closest?.('[data-vo-row]') ?? null, parent: hit?.closest?.('[data-vo-drop-parent]') ?? null };
    };

    const endTouch = () => {
        if (!touch) return;
        const { handle, pointerId } = touch;
        touch = null;
        try { handle.releasePointerCapture?.(pointerId); } catch { /* the capture was already gone */ }
        clearMarks();
        root.querySelectorAll('.is-dragging').forEach(element => element.classList.remove('is-dragging'));
    };

    const onPointerDown = event => {
        if (event.pointerType === 'mouse' || (event.button ?? 0) > 0 || touch) return;
        const handle = event.target.closest?.('[data-vo-handle]');
        const row = handle?.closest('[data-vo-row]');
        if (!handle || !row || handle.getAttribute?.('draggable') === 'false') return;
        touch = { handle, pointerId: event.pointerId, from: { list: Number(row.dataset.voList), index: Number(row.dataset.voRow) } };
        try { handle.setPointerCapture?.(event.pointerId); } catch { /* the pointer ended before it could be captured */ }
        row.classList.add('is-dragging');
    };

    const onPointerMove = event => {
        if (!touch || event.pointerId !== touch.pointerId) return;
        event.preventDefault?.();
        const { row, parent } = targetUnder(event);
        clearMarks();
        if (row) {
            row.classList.add(insertionPoint(row, event) === Number(row.dataset.voRow) ? 'is-drop-before' : 'is-drop-after');
        } else if (parent) {
            parent.classList.add('is-drop-parent');
        }

        const scroller = scrollParent(touch.handle);
        if (scroller) {
            const box = scroller.getBoundingClientRect();
            if (event.clientY < box.top + EDGE) scroller.scrollTop -= SCROLL_STEP;
            else if (event.clientY > box.bottom - EDGE) scroller.scrollTop += SCROLL_STEP;
        }
    };

    const onPointerUp = event => {
        if (!touch || event.pointerId !== touch.pointerId) return;
        const from = touch.from;
        const { row, parent } = targetUnder(event);
        const point = row ? insertionPoint(row, event) : 0;
        const toList = row ? Number(row.dataset.voList) : -1;
        const parentId = parent ? parent.dataset.voDropParent : null;
        endTouch();
        if (row) dotnet.invokeMethodAsync('OnDropAsync', from.list, from.index, toList, point);
        else if (parent) dotnet.invokeMethodAsync('OnDropOnParentAsync', from.list, from.index, parentId);
    };

    const onStart = event => {
        // A finger on the handle is the pointer drag above; the browser's own drag must not start beside it.
        if (touch) { event.preventDefault?.(); return; }
        const handle = event.target.closest?.('[data-vo-handle]');
        const row = handle?.closest('[data-vo-row]');
        if (!handle || !row || !event.dataTransfer) return;
        const from = { list: Number(row.dataset.voList), index: Number(row.dataset.voRow) };
        active = from;
        // Firefox will not start a drag without a payload.
        event.dataTransfer.setData(MIME, `${from.list}:${from.index}`);
        event.dataTransfer.setData('text/plain', row.dataset.voTitle || '');
        event.dataTransfer.effectAllowed = 'move';
        row.classList.add('is-dragging');
        document.addEventListener('dragover', onOver);
        document.addEventListener('drop', onDrop);
        document.addEventListener('dragend', onEnd);
    };

    const onOver = event => {
        if (!active) return;
        const row = event.target.closest?.('[data-vo-row]');
        const parent = event.target.closest?.('[data-vo-drop-parent]');
        if (!row && !parent) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
        clearMarks();
        if (row) {
            row.classList.add(insertionPoint(row, event) === Number(row.dataset.voRow) ? 'is-drop-before' : 'is-drop-after');
        } else {
            parent.classList.add('is-drop-parent');
        }
    };

    const onDrop = event => {
        if (!active) return;
        const from = active;
        const row = event.target.closest?.('[data-vo-row]');
        const parent = event.target.closest?.('[data-vo-drop-parent]');
        if (!row && !parent) return;
        event.preventDefault();
        const point = row ? insertionPoint(row, event) : 0;
        const toList = row ? Number(row.dataset.voList) : -1;
        const parentId = parent ? parent.dataset.voDropParent : null;
        onEnd();
        if (row) dotnet.invokeMethodAsync('OnDropAsync', from.list, from.index, toList, point);
        else dotnet.invokeMethodAsync('OnDropOnParentAsync', from.list, from.index, parentId);
    };

    const onEnd = () => {
        active = null;
        clearMarks();
        document.querySelectorAll('.is-dragging').forEach(element => element.classList.remove('is-dragging'));
        document.removeEventListener('dragover', onOver);
        document.removeEventListener('drop', onDrop);
        document.removeEventListener('dragend', onEnd);
    };

    root.addEventListener('dragstart', onStart);
    root.addEventListener('pointerdown', onPointerDown);
    root.addEventListener('pointermove', onPointerMove);
    root.addEventListener('pointerup', onPointerUp);
    root.addEventListener('pointercancel', endTouch);
    root.__voDetach = () => {
        root.removeEventListener('dragstart', onStart);
        root.removeEventListener('pointerdown', onPointerDown);
        root.removeEventListener('pointermove', onPointerMove);
        root.removeEventListener('pointerup', onPointerUp);
        root.removeEventListener('pointercancel', endTouch);
        endTouch();
        onEnd();
        root.__voAttached = false;
    };
}

export function detach(root) {
    root?.__voDetach?.();
}
