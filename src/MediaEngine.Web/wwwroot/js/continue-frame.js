import { loadIndex, tileFor } from './playback-seek-rail.js';
/**
 * Continue tile frame (Home bundle H, issue 891): puts the scrub-preview frame at the saved position over the tile's fallback
 * picture. Reads the video's preview index once, and only when the tile scrolls into view. No index, no access or no previews
 * leaves the fallback picture exactly as it was.
 */
const observed = new WeakMap();
export function attach(element) {
    if (!element || observed.has(element)) return;
    const watcher = new IntersectionObserver(entries => {
        if (!entries.some(entry => entry.isIntersecting)) return;
        watcher.disconnect();
        show(element);
    }, { rootMargin: '200px' });
    observed.set(element, watcher);
    watcher.observe(element);
}
export function detach(element) {
    observed.get(element)?.disconnect();
    observed.delete(element);
}
async function show(element) {
    const asset = element.dataset.asset, seconds = Number(element.dataset.position);
    if (!asset) return;
    const index = await loadIndex(`/engine-scrub/${asset}/index.json`);
    const tile = tileFor(index, seconds);
    if (!tile) return;
    element.style.backgroundImage = `url(/engine-scrub/${asset}/${encodeURIComponent(tile.sheet)})`;
    element.style.backgroundSize = `${index.columns * 100}% ${index.rows * 100}%`;
    const x = index.columns > 1 ? tile.column / (index.columns - 1) * 100 : 0;
    const y = index.rows > 1 ? tile.row / (index.rows - 1) * 100 : 0;
    element.style.backgroundPosition = `${x}% ${y}%`;
    element.classList.add('is-ready');
}
