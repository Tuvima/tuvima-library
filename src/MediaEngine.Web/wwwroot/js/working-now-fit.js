// Activity page, Working now (spec library-activity D34): tells the row how many whole tiles fit across it, so it never scrolls and never cuts one off.
// The tile width and the gap are the row's own CSS (--lact-working-tile-w and its column gap); this only measures and reports.
const observers = new WeakMap();

// Whole tiles that fit in a row `width` wide, with `gap` between tiles; always at least one.
export function fitCount(width, tileWidth, gap) {
    if (!(tileWidth > 0) || !(width > 0)) return 1;
    return Math.max(1, Math.floor((width + gap) / (tileWidth + gap)));
}

function measure(row) {
    const style = getComputedStyle(row);
    const tileWidth = parseFloat(style.getPropertyValue('--lact-working-tile-w'));
    const gap = parseFloat(style.columnGap) || 0;
    return fitCount(row.clientWidth, tileWidth, gap);
}

export function attach(row, dotNetRef) {
    detach(row);
    let last = -1;
    const report = () => {
        const fit = measure(row);
        if (fit === last) return;
        last = fit;
        dotNetRef.invokeMethodAsync('OnFit', fit);
    };
    const observer = new ResizeObserver(report);
    observer.observe(row);
    observers.set(row, observer);
    report();
}

export function detach(row) {
    const observer = observers.get(row);
    if (!observer) return;
    observer.disconnect();
    observers.delete(row);
}
