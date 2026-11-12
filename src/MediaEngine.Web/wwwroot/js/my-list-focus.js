// My List: moves keyboard focus to the first element that matches a selector (the Select bar when Select opens, the button that opened it
// when Select closes). Returns true once something took focus.
export function focusFirst(selector) {
    if (typeof selector !== 'string' || selector.length === 0) return false;
    const target = document.querySelector(selector);
    if (!target) return false;
    target.focus({ preventScroll: true });
    return document.activeElement === target;
}
