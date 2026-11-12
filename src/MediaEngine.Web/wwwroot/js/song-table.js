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
// Dragging a header edge, or pressing Left or Right on it, is handled here in the browser, so the table follows the pointer without a
// round trip per mouse move. The component draws the grid as inline CSS variables on the wrapper around the table: --song-table-columns
// (one grid track per header cell, a filler track before the actions track once the Song column has a fixed width, the actions track
// last), --song-table-min-width (the table's smallest width) and --song-table-filler (block or none: whether the empty filler cells show).
// While a handle is dragged this only rewrites those variables, so the header and every row follow together. When the drag ends, .NET is
// called once with the column and the final width. It saves the choice and returns the exact variables it now draws, which are written
// back so the page matches what .NET holds even when its own redraw finds nothing to change.

const COLUMNS_VARIABLE = '--song-table-columns';
const MIN_WIDTH_VARIABLE = '--song-table-min-width';
const FILLER_VARIABLE = '--song-table-filler';
const FILLER_TRACK = 'minmax(0, 1fr)';
const COLUMN_GAP = 8;
const KEYBOARD_STEP = 8;
const wirings = new WeakMap();

// A width kept between the column's smallest and largest allowed size.
export function clampWidth(width, min, max) {
    return Math.min(Math.max(width, min), max);
}

// The width after the pointer moved by `delta` pixels from where the drag began, rounded to whole pixels and kept within the limits.
export function widthFromDelta(startWidth, delta, min, max) {
    return clampWidth(Math.round(startWidth + delta), min, max);
}

// The upper limit for one drag: the column's own limit, or the width it already has when it starts wider than that (the Song column on a
// very wide screen), so the first move never shrinks it by itself.
export function limitForDrag(max, startWidth) {
    return Math.max(max, startWidth);
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

// True when the template already has the filler track right before the last (actions) track.
export function hasFillerTrack(template) {
    const tracks = splitTemplate(template);
    return tracks.length >= 2 && tracks[tracks.length - 2] === FILLER_TRACK;
}

// The template with the filler track put right before the last (actions) track, so spare width falls there and the actions track stays
// at the right edge. A template that has it already is returned as it is.
export function withFillerTrack(template) {
    const tracks = splitTemplate(template);
    if (tracks.length < 1 || hasFillerTrack(template)) {
        return tracks.join(' ');
    }

    tracks.splice(tracks.length - 1, 0, FILLER_TRACK);
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

    const variables = (holder) => ({
        columns: holder.style.getPropertyValue(COLUMNS_VARIABLE),
        minWidth: holder.style.getPropertyValue(MIN_WIDTH_VARIABLE),
        filler: holder.style.getPropertyValue(FILLER_VARIABLE),
    });

    const restore = (holder, saved) => {
        holder.style.setProperty(COLUMNS_VARIABLE, saved.columns);
        holder.style.setProperty(MIN_WIDTH_VARIABLE, saved.minWidth);
        holder.style.setProperty(FILLER_VARIABLE, saved.filler);
    };

    // Calls .NET and writes back the variables it answers with. A failed call must not throw out of an event handler (nothing could catch
    // it), so it is reported on the console and the table goes back to how it was before the change.
    const callDotnet = (holder, saved, method, ...args) => {
        Promise.resolve(dotnetRef.invokeMethodAsync(method, ...args))
            .then((css) => {
                if (typeof css === 'string' && css) {
                    holder.style.cssText = css;
                }
            })
            .catch((error) => {
                console.warn('Song table: could not apply the column change.', error);
                restore(holder, saved);
            });
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

        if (commit && finished.width !== finished.startWidth) {
            callDotnet(finished.holder, finished.saved, 'ColumnResized', finished.column, finished.width);
        } else {
            // Cancelled, or let go where it started: nothing is sent, so the table must not keep the fixed width written while dragging.
            restore(finished.holder, finished.saved);
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

        // No text selection and no native drag may start from here.
        event.preventDefault();

        const saved = variables(holder);
        const index = Array.prototype.indexOf.call(head.children, cell);
        const startWidth = Math.round(cell.getBoundingClientRect().width);
        drag = {
            column,
            handle,
            holder,
            index,
            saved,
            pointerId: event.pointerId,
            startX: event.clientX,
            startWidth,
            width: startWidth,
            min: Number(handle.dataset.min) || 0,
            max: limitForDrag(Number(handle.dataset.max) || Number.MAX_SAFE_INTEGER, startWidth),
            minTable: parseFloat(saved.minWidth),
            trackMinimum: trackMinimum(saved.columns, index),
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
        let columns = withTrackWidth(drag.saved.columns, drag.index, width);
        let extra = 0;
        if (drag.column === 'song' && !hasFillerTrack(columns)) {
            // Song now has a fixed width: the spare width goes to a filler before the actions column, which stays at the right edge.
            columns = withFillerTrack(columns);
            extra = COLUMN_GAP;
            drag.holder.style.setProperty(FILLER_VARIABLE, 'block');
        }

        drag.holder.style.setProperty(COLUMNS_VARIABLE, columns);
        if (Number.isFinite(drag.minTable)) {
            drag.holder.style.setProperty(MIN_WIDTH_VARIABLE, `${drag.minTable + width - drag.trackMinimum + extra}px`);
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
        const holder = root.parentElement;
        if (!handle || !root.contains(handle) || !handle.dataset.column || !holder) {
            return;
        }

        event.preventDefault();
        callDotnet(holder, variables(holder), 'ColumnResetWidth', handle.dataset.column);
    };

    // Left and Right on a focused handle change that column by one step from the width it has on screen (the Song column is usually wider
    // than its default), kept within the column's limits, and tell .NET once.
    const onKeyDown = (event) => {
        const handle = event.target?.closest?.('.song-table__resizer');
        const delta = event.key === 'ArrowLeft' ? -KEYBOARD_STEP : event.key === 'ArrowRight' ? KEYBOARD_STEP : 0;
        const cell = handle?.closest('.song-table__head-cell');
        const holder = root.parentElement;
        if (!handle || !delta || !cell || !holder || !root.contains(handle) || !handle.dataset.column) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        const measured = Math.round(cell.getBoundingClientRect().width);
        const min = Number(handle.dataset.min) || 0;
        const max = limitForDrag(Number(handle.dataset.max) || Number.MAX_SAFE_INTEGER, measured);
        const width = widthFromDelta(measured, delta, min, max);
        if (width !== measured) {
            callDotnet(holder, variables(holder), 'ColumnResized', handle.dataset.column, width);
        }
    };

    root.addEventListener('pointerdown', onPointerDown);
    root.addEventListener('pointermove', onPointerMove);
    root.addEventListener('pointerup', onPointerUp);
    root.addEventListener('pointercancel', onPointerCancel);
    root.addEventListener('dblclick', onDoubleClick);
    root.addEventListener('keydown', onKeyDown);

    const dispose = () => {
        root.removeEventListener('pointerdown', onPointerDown);
        root.removeEventListener('pointermove', onPointerMove);
        root.removeEventListener('pointerup', onPointerUp);
        root.removeEventListener('pointercancel', onPointerCancel);
        root.removeEventListener('dblclick', onDoubleClick);
        root.removeEventListener('keydown', onKeyDown);
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
