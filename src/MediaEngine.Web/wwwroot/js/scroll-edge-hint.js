// Horizontal strips (detail section tabs): mark which edge still has content and bring the active item into view.
const states = new WeakMap();

function sync(strip) {
    const overflow = strip.scrollWidth - strip.clientWidth;
    strip.dataset.scrollMoreStart = String(overflow > 1 && strip.scrollLeft > 1);
    strip.dataset.scrollMoreEnd = String(overflow > 1 && strip.scrollLeft < overflow - 1);
}

export function attach(strip) {
    if (states.has(strip)) { sync(strip); return; }
    const onScroll = () => sync(strip);
    const observer = new ResizeObserver(onScroll);
    strip.addEventListener('scroll', onScroll, { passive: true });
    observer.observe(strip);
    states.set(strip, { onScroll, observer });
    sync(strip);
}

export function showActive(strip, selector) {
    const item = strip.querySelector(selector);
    if (!item) { sync(strip); return; }
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const offset = item.getBoundingClientRect().left - strip.getBoundingClientRect().left + strip.scrollLeft;
    const target = offset - (strip.clientWidth - item.offsetWidth) / 2;
    strip.scrollTo({ left: Math.max(0, target), behavior: reduced ? 'auto' : 'smooth' });
    sync(strip);
}

export function detach(strip) {
    const state = states.get(strip);
    if (!state) return;
    strip.removeEventListener('scroll', state.onScroll);
    state.observer.disconnect();
    states.delete(strip);
}
