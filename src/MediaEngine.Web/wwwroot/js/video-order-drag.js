// Drag and drop for the videos of a series in the editor's Match tool (universal editor v3.1, D7). A grip handle on each row starts the
// drag; a line shows where the video will land; dropping tells the page which gap was chosen and the page stages the new order exactly as
// the arrow buttons do. A video can also be dropped on a section row of the tree on the left. No library: plain drag events.
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

export function attach(root, dotnet) {
    if (!root || root.__voAttached) return;
    root.__voAttached = true;

    const onStart = event => {
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
    root.__voDetach = () => {
        root.removeEventListener('dragstart', onStart);
        onEnd();
        root.__voAttached = false;
    };
}

export function detach(root) {
    root?.__voDetach?.();
}
