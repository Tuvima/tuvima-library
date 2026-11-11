const roots = new WeakMap();
export function attach(root) {
    if (!root || roots.has(root)) return;
    const start = event => {
        const handle = event.target.closest?.('[data-playback-queue-drag]');
        if (!handle || !root.contains(handle) || !event.dataTransfer) return;
        // Firefox requires a transfer payload before it will initiate a native drag.
        // The guarded .NET callback retains the displayed occurrence and queue revision.
        event.dataTransfer.setData('text/plain', handle.dataset.playbackQueueDrag);
        event.dataTransfer.effectAllowed = 'move';
    };
    root.addEventListener('dragstart', start); roots.set(root,start);
}
export function detach(root) {
    const start=roots.get(root); if (start) root.removeEventListener('dragstart',start);
    roots.delete(root);
}

// Puts keyboard focus on the drag handle of a queue row (by its queue entry id); with no such row it falls back to the scrolling list, so focus never drops to the page.
export function focusHandle(root, id) {
    if (!root) return false;
    const handle = id ? Array.from(root.querySelectorAll('[data-playback-queue-drag]')).find(item => item.dataset.playbackQueueDrag === id) : null;
    const target = handle || root.querySelector('[data-playback-list-scroll]');
    if (!target) return false;
    target.focus();
    return true;
}
