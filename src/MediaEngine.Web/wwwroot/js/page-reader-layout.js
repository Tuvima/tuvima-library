// The rules the page reader follows to decide what is on screen (PDF and comic reader spec D5, D6, D8, D18).
// Pure functions with no page access, so they are tested on their own (tests/MediaEngine.Web.Tests/JavaScript).

export const TWO_PAGE_MIN_WIDTH = 1180;

/** Auto shows two pages when the window is at least 1180 wide and wider than it is tall; otherwise one (D5). */
export function effectiveLayout(layout, width, height) {
    if (layout !== "auto") {
        return layout;
    }

    return width >= TWO_PAGE_MIN_WIDTH && width > height ? "two" : "one";
}

/** True when the pages read right to left: chosen by hand, or "automatic" and the comic says it is manga (D7). */
export function isRightToLeft(direction, comicSaysRtl) {
    return direction === "rtl" || (direction === "auto" && !!comicSaysRtl);
}

function isWide(page) {
    return page.width > 0 && page.width > page.height;
}

/**
 * The views (what is on screen at once) in reading order, each a list of 1-based page numbers.
 * Two-page layout: the cover is alone and a printed double-page spread (a page wider than tall) is always alone (D5).
 */
export function buildViews(mode, pages) {
    const total = pages.length;
    const views = [];
    if (mode !== "two") {
        for (let n = 1; n <= total; n += 1) {
            views.push([n]);
        }

        return views;
    }

    if (total === 0) {
        return views;
    }

    views.push([1]);
    let n = 2;
    while (n <= total) {
        const first = pages[n - 1];
        const second = n + 1 <= total ? pages[n] : null;
        if (second && !isWide(first) && !isWide(second)) {
            views.push([n, n + 1]);
            n += 2;
        } else {
            views.push([n]);
            n += 1;
        }
    }

    return views;
}

export function viewIndexOf(views, pageNumber) {
    const index = views.findIndex(view => view.includes(pageNumber));
    return index < 0 ? 0 : index;
}

/** The smallest size the Engine offers that is at least `needed` pixels wide; 0 (the original) when none is big enough (D18). */
export function chooseWidth(widths, needed) {
    if (!widths || widths.length === 0) {
        return 0;
    }

    const sorted = [...widths].sort((a, b) => a - b);
    return sorted.find(width => width >= needed) ?? 0;
}

/**
 * What a tap at `fraction` (0 to 1 across the page) does: "previous", "next" or "chrome" (show or hide the bars).
 * The left third turns back and the right third forward; right to left swaps them (D8).
 */
export function tapAction(fraction, rtl, zoomed) {
    if (zoomed || (fraction >= 1 / 3 && fraction <= 2 / 3)) {
        return "chrome";
    }

    const left = fraction < 1 / 3;
    return left ? (rtl ? "next" : "previous") : (rtl ? "previous" : "next");
}

/** What a key does: "next", "previous", "first", "last", "zoom-in", "zoom-out", "zoom-reset", "close", or null (D8). */
export function keyAction(key, shift, rtl) {
    switch (key) {
        case "ArrowRight": return rtl ? "previous" : "next";
        case "ArrowLeft": return rtl ? "next" : "previous";
        case "ArrowDown":
        case "PageDown": return "next";
        case "ArrowUp":
        case "PageUp": return "previous";
        case " ": return shift ? "previous" : "next";
        case "Home": return "first";
        case "End": return "last";
        case "+":
        case "=": return "zoom-in";
        case "-":
        case "_": return "zoom-out";
        case "0": return "zoom-reset";
        case "Escape": return "close";
        default: return null;
    }
}
