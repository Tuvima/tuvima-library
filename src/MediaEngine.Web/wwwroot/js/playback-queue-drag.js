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
