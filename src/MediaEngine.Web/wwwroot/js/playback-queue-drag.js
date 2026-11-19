import { wireToDotnet } from './touch-reorder.js';

// A queue row is reordered by its handle: by mouse with the browser's own drag, by finger (listen-playlists WP5, issue 1201) with pointer events on
// the same handle (touch-reorder.js), and by keyboard with Alt+Arrow or the row menu. The finger's drop reports the row it landed on to .NET, which
// works the place out and sends the same guarded reorder command the mouse drop sends. Rows carry data-queue-index, their place among the
// upcoming songs.
export const QUEUE_TOUCH = Object.freeze({
    handle: '[data-playback-queue-drag]',
    row: '[data-queue-index]',
    keyAttribute: 'data-queue-index',
    startMethod: 'TouchDragStarted',
    dropMethod: 'TouchDropped',
    cancelMethod: 'TouchDragCancelled',
});

const roots = new WeakMap();
export function attach(root, dotnetRef) {
    if (!root || roots.has(root)) return;
    const start = event => {
        const handle = event.target.closest?.('[data-playback-queue-drag]');
        if (!handle || !root.contains(handle) || !event.dataTransfer) return;
        // Firefox requires a transfer payload before it will initiate a native drag.
        // The guarded .NET callback retains the displayed occurrence and queue revision.
        event.dataTransfer.setData('text/plain', handle.dataset.playbackQueueDrag);
        event.dataTransfer.effectAllowed = 'move';
    };
    root.addEventListener('dragstart', start);
    const stopTouch = dotnetRef ? wireToDotnet(root, dotnetRef, QUEUE_TOUCH) : () => { };
    roots.set(root, { start, stopTouch });
}
export function detach(root) {
    const wired = roots.get(root);
    if (wired) {
        root.removeEventListener('dragstart', wired.start);
        wired.stopTouch();
    }
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
