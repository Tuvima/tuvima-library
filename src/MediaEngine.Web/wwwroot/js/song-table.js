// Press and hold on a phone song row: opens the row's "..." bottom sheet by pressing its menu button, so the sheet and its items stay
// the shared menu component's. Returns false when the row or its button is not on the page.

export function openRowMenu(rowKey) {
    const row = document.querySelector('[data-song-row-key="' + CSS.escape(rowKey) + '"]');
    const trigger = row ? row.querySelector('button[aria-haspopup="menu"]') : null;
    if (!trigger) {
        return false;
    }

    trigger.click();
    return true;
}

// The phone Songs list's A to Z strip: scrolls the page so the list item at `index` sits near the top. The list is virtualized with a
// fixed estimated row height, so the offset is index * itemSize from the list's top; the rows around it then draw themselves.
// The jump is instant (no smooth scroll), which also respects reduced motion.
export function scrollToIndex(list, index, itemSize, topOffset) {
    if (!list) {
        return false;
    }

    let scroller = list.parentElement;
    while (scroller && scroller !== document.body && scroller !== document.documentElement) {
        const overflowY = getComputedStyle(scroller).overflowY;
        if ((overflowY === 'auto' || overflowY === 'scroll') && scroller.scrollHeight > scroller.clientHeight) {
            break;
        }

        scroller = scroller.parentElement;
    }

    const listTop = list.getBoundingClientRect().top;
    if (!scroller || scroller === document.body || scroller === document.documentElement) {
        window.scrollBy({ top: listTop + index * itemSize - topOffset, behavior: 'instant' });
    } else {
        const scrollerTop = scroller.getBoundingClientRect().top;
        scroller.scrollBy({ top: listTop - scrollerTop + index * itemSize - topOffset, behavior: 'instant' });
    }

    return true;
}
