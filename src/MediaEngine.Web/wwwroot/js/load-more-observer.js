// Asks .NET for the next page when a sentinel element after a list scrolls near the screen.
// Generic on purpose: the page owns the sentinel and the paging; this only reports "the end is close".
// Observing again on the same sentinel replaces the old observer, and a new observer reports the sentinel's current
// state straight away, so a sentinel that is still on screen after a page was added asks again.
const observers = new WeakMap();

const DEFAULT_ROOT_MARGIN = '800px 0px';
const DEFAULT_METHOD = 'LoadMoreFromObserverAsync';

// The nearest ancestor that scrolls on its own, or null (the viewport) when the page itself scrolls.
export function resolveScrollRoot(sentinel) {
    for (let element = sentinel?.parentElement; element; element = element.parentElement) {
        const overflowY = getComputedStyle(element).overflowY;
        if (overflowY === 'auto' || overflowY === 'scroll') return element;
    }
    return null;
}

export function observe(sentinel, dotnetRef, options) {
    disconnect(sentinel);
    if (!sentinel || !dotnetRef || typeof IntersectionObserver === 'undefined') return;

    const method = options?.method || DEFAULT_METHOD;
    const rootMargin = options?.rootMargin || DEFAULT_ROOT_MARGIN;
    const observer = new IntersectionObserver(entries => {
        if (entries.some(entry => entry.isIntersecting)) dotnetRef.invokeMethodAsync(method);
    }, { root: resolveScrollRoot(sentinel), rootMargin });
    observer.observe(sentinel);
    observers.set(sentinel, observer);
}

export function disconnect(sentinel) {
    if (!sentinel) return;
    const observer = observers.get(sentinel);
    if (!observer) return;
    observer.disconnect();
    observers.delete(sentinel);
}
