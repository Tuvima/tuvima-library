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

// ---- Column resizing --------------------------------------------------------------------------------------------------------
// Dragging a header edge is handled here, in the browser, so the table follows the pointer without a round trip per mouse move.
// The component draws the grid as a CSS variable on the wrapper around the table (--song-table-columns, one track per header cell, the
// actions cell last; --song-table-min-width is the table's smallest width). While a handle is dragged this only rewrites that variable
// (and the minimum width for the Song track), so the header and every row follow together. When the drag ends, .NET is called once with
// the column and the final width; it saves the choice and draws the same widths itself.

const COLUMNS_VARIABLE = '--song-table-columns';
const MIN_WIDTH_VARIABLE = '--song-table-min-width';
const FLEXIBLE_TRACK = /^minmax\(\s*[\d.]+px\s*,\s*1fr\s*\)$/;
const wirings = new WeakMap();

// A width kept between the column's smallest and largest allowed size.
export function clampWidth(width, min, max) {
    return Math.min(Math.max(width, min), max);
}

// The width after the pointer moved by `delta` pixels from where the drag began, rounded to whole pixels and kept within the limits.
export function widthFromDelta(startWidth, delta, min, max) {
    return clampWidth(Math.round(startWidth + delta), min, max);
}

// Splits a grid template into its tracks, keeping minmax(a, b) together.
export function splitTemplate(template) {
    const tracks = [];
    let depth = 0;
    let current = '';
    for (const character of String(template ?? '')) {
        if (character === '(') depth++;
        if (character === ')') depth--;
        if (/\s/.test(character) && depth === 0) {
            if (current) tracks.push(current);
            current = '';
        } else {
            current += character;
        }
    }

    if (current) tracks.push(current);
    return tracks;
}

// True when the track is the flexible one (the Song column with no width of its own, minmax(Wpx, 1fr)).
export function isFlexibleTrack(template, index) {
    return FLEXIBLE_TRACK.test(splitTemplate(template)[index] ?? '');
}

// How much a track adds to the table's smallest width: a minmax(a, b) track its minimum a, a fixed track its width. 0 for anything else.
export function trackMinimum(template, index) {
    const track = splitTemplate(template)[index] ?? '';
    const ranged = /^minmax\(\s*([\d.]+)px\s*,/.exec(track);
    if (ranged) {
        return Number(ranged[1]);
    }

    const fixed = /^([\d.]+)px$/.exec(track);
    return fixed ? Number(fixed[1]) : 0;
}

// The template with one track set to a fixed `width`px. A column the person is resizing keeps exactly that width in both directions,
// even when the table is too narrow for every column (the other columns give up width first), so a minmax(a, b) track, the Song column's
// minmax(Wpx, 1fr) included, and an already fixed track all become `width`px. A track in some other shape, or past the end, is left
// as it is. Only resizable columns' tracks are ever passed in.
export function withTrackWidth(template, index, width) {
    const tracks = splitTemplate(template);
    const track = tracks[index];
    if (track !== undefined && (/^minmax\(/.test(track) || /^[\d.]+px$/.test(track))) {
        tracks[index] = `${width}px`;
    }

    return tracks.join(' ');
}

// Wires resizing for one table: `root` is the .song-table element, `dotnetRef` the component. Handles are found by event delegation, so
// showing or hiding columns needs no re-wiring. Each handle names its column and limits in data-column, data-min and data-max.
// Returns a function that removes everything it added; wiring the same table again replaces the earlier wiring.
export function wireResize(root, dotnetRef) {
    unwireResize(root);
    if (!root || !dotnetRef) {
        return () => { };
    }

    let drag = null;

    const restore = (state) => {
        state.holder.style.setProperty(COLUMNS_VARIABLE, state.template);
        if (Number.isFinite(state.minTable)) {
            state.holder.style.setProperty(MIN_WIDTH_VARIABLE, `${state.minTable}px`);
        }
    };

    const finish = (commit) => {
        if (!drag) {
            return;
        }

        const finished = drag;
        drag = null;
        root.classList.remove('is-resizing');
        try {
            finished.handle.releasePointerCapture(finished.pointerId);
        } catch {
            // The browser already ended the capture; there is nothing left to let go of.
        }

        if (!commit) {
            restore(finished);
        } else if (finished.width !== finished.startWidth) {
            Promise.resolve(dotnetRef.invokeMethodAsync('ColumnResized', finished.column, finished.width)).catch(() => { });
        }
    };

    const onPointerDown = (event) => {
        const handle = event.target?.closest?.('.song-table__resizer');
        if (!handle || !root.contains(handle) || (event.pointerType === 'mouse' && event.button !== 0)) {
            return;
        }

        const cell = handle.closest('.song-table__head-cell');
        const head = cell?.parentElement;
        const holder = root.parentElement;
        const column = handle.dataset.column;
        if (!cell || !head || !holder || !column) {
            return;
        }

        // No text selection and no native drag may start from here, and the press must not reach the header's sort.
        event.preventDefault();
        event.stopPropagation();

        const template = holder.style.getPropertyValue(COLUMNS_VARIABLE);
        const index = Array.prototype.indexOf.call(head.children, cell);
        const startWidth = Math.round(cell.getBoundingClientRect().width);
        drag = {
            column,
            handle,
            holder,
            index,
            template,
            pointerId: event.pointerId,
            startX: event.clientX,
            startWidth,
            width: startWidth,
            min: Number(handle.dataset.min) || 0,
            max: Number(handle.dataset.max) || Number.MAX_SAFE_INTEGER,
            minTable: parseFloat(holder.style.getPropertyValue(MIN_WIDTH_VARIABLE)),
            trackMinimum: trackMinimum(template, index),
        };

        handle.setPointerCapture(event.pointerId);
        root.classList.add('is-resizing');
    };

    const onPointerMove = (event) => {
        if (!drag || event.pointerId !== drag.pointerId) {
            return;
        }

        const width = widthFromDelta(drag.startWidth, event.clientX - drag.startX, drag.min, drag.max);
        if (width === drag.width) {
            return;
        }

        drag.width = width;
        drag.holder.style.setProperty(COLUMNS_VARIABLE, withTrackWidth(drag.template, drag.index, width));
        if (Number.isFinite(drag.minTable)) {
            drag.holder.style.setProperty(MIN_WIDTH_VARIABLE, `${drag.minTable + width - drag.trackMinimum}px`);
        }
    };

    const onPointerUp = (event) => {
        if (drag && event.pointerId === drag.pointerId) {
            finish(true);
        }
    };

    // The browser took the pointer away (for example a system gesture): the table goes back to how it was.
    const onPointerCancel = (event) => {
        if (drag && event.pointerId === drag.pointerId) {
            finish(false);
        }
    };

    const onDoubleClick = (event) => {
        const handle = event.target?.closest?.('.song-table__resizer');
        if (!handle || !root.contains(handle) || !handle.dataset.column) {
            return;
        }

        event.preventDefault();
        Promise.resolve(dotnetRef.invokeMethodAsync('ColumnResetWidth', handle.dataset.column)).catch(() => { });
    };

    root.addEventListener('pointerdown', onPointerDown);
    root.addEventListener('pointermove', onPointerMove);
    root.addEventListener('pointerup', onPointerUp);
    root.addEventListener('pointercancel', onPointerCancel);
    root.addEventListener('dblclick', onDoubleClick);

    const dispose = () => {
        root.removeEventListener('pointerdown', onPointerDown);
        root.removeEventListener('pointermove', onPointerMove);
        root.removeEventListener('pointerup', onPointerUp);
        root.removeEventListener('pointercancel', onPointerCancel);
        root.removeEventListener('dblclick', onDoubleClick);
        root.classList.remove('is-resizing');
        drag = null;
        if (wirings.get(root) === dispose) {
            wirings.delete(root);
        }
    };

    wirings.set(root, dispose);
    return dispose;
}

export function unwireResize(root) {
    if (root) {
        wirings.get(root)?.();
    }
}
