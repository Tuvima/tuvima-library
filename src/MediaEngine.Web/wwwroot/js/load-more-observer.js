// Asks .NET for the next page when a sentinel element after a list scrolls near the screen.
// Generic on purpose: the page owns the sentinel and the paging; this only reports "the end is close".
// Observing again on the same sentinel replaces the old observer, and a new observer reports the sentinel's current
// state straight away, so a sentinel that is still on screen after a page was added asks again.
const observers = new WeakMap();

const DEFAULT_ROOT_MARGIN = '800px 0px';
const DEFAULT_METHOD = 'LoadMoreFromObserverAsync';

// The nearest ancestor that really scrolls on its own, or null (the viewport) when the page itself scrolls.
// `overflow-x: hidden` makes overflow-y compute to auto on body and layout wrappers that have no fixed height, so the
// style alone proves nothing: such an element must also have content taller than its box. body and html never count.
export function resolveScrollRoot(sentinel) {
    for (let element = sentinel?.parentElement; element; element = element.parentElement) {
        const tag = element.tagName;
        if (tag === 'BODY' || tag === 'HTML') return null;
        const overflowY = getComputedStyle(element).overflowY;
        if ((overflowY === 'auto' || overflowY === 'scroll') && element.scrollHeight > element.clientHeight) return element;
    }
    return null;
}

export function observe(sentinel, dotnetRef, options) {
    disconnect(sentinel);
    if (!sentinel || !dotnetRef || typeof IntersectionObserver === 'undefined') return;

    const method = options?.method || DEFAULT_METHOD;
    const rootMargin = options?.rootMargin || DEFAULT_ROOT_MARGIN;
    const observer = new IntersectionObserver(entries => {
        if (!entries.some(entry => entry.isIntersecting)) return;
        // The call can fail when the page was disposed or the circuit dropped while the observer was still firing;
        // there is nothing to retry then, and the Load more button remains for the person.
        Promise.resolve(dotnetRef.invokeMethodAsync(method)).catch(() => {});
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
