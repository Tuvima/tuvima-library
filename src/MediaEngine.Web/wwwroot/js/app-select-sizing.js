import { attach as attachTooltip, detach as detachTooltip } from './playback-tooltip.js';
const states = new WeakMap();
export function attach(root, label) {
    const previous = states.get(root);
    if (previous) { previous.label = label; previous.update(); return; }
    const state = { label, observer: null, update: null };
    const update = () => {
        const natural = root.querySelector('.app-select__width-label');
        const field = root.querySelector('.tl-input-control');
        if (!natural || !field) return;
        const clipped = natural.scrollWidth > field.getBoundingClientRect().width + 1;
        root.dataset.playbackTooltip = clipped ? state.label || '' : '';
        root.dataset.selectTruncated = String(clipped);
    };
    state.update = update;
    state.observer = new ResizeObserver(update);
    state.observer.observe(root); states.set(root, state); update(); attachTooltip(root);
}
export function detach(root) { states.get(root)?.observer.disconnect(); states.delete(root); detachTooltip(root); }
