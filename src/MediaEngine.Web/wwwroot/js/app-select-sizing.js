import { attach as attachTooltip, detach as detachTooltip } from './playback-tooltip.js';
const states = new WeakMap();
export function attach(root, label) {
    detach(root);
    const update = () => {
        const natural = root.querySelector('.app-select__width-label');
        const field = root.querySelector('.mud-input-control');
        if (!natural || !field) return;
        const clipped = natural.scrollWidth > field.getBoundingClientRect().width + 1;
        root.dataset.playbackTooltip = clipped ? label || '' : '';
        root.dataset.selectTruncated = String(clipped);
    };
    const observer = new ResizeObserver(update);
    observer.observe(root); update(); attachTooltip(root);
    states.set(root, observer);
}
export function detach(root) { states.get(root)?.disconnect(); states.delete(root); detachTooltip(root); }
