// Dragging photos from the View timeline onto a Gallery in the sidebar (and onto its New Gallery heading). The tiles are plain draggable elements whose
// start and end .NET hears through Blazor events; what the browser itself needs while the pointer is down is the same as for songs (the drag carries an empty text, which is enough for Firefox), so this reuses the song
// table's drag (song-table.js wireDragOut): the drag carries some data (Firefox will not start a drag without it), every place that takes the photos
// (an element with data-song-drop) shows where to let go, and the one under the pointer is highlighted and accepts the drop.
import { wireDragOut } from './song-table.js';

const wirings = new WeakMap();

// `root` is the timeline element. Returns nothing; unwire(root) removes the drag. Wiring the same element again replaces the earlier wiring.
export function wire(root) {
    unwire(root);
    if (!root) {
        return;
    }

    wirings.set(root, wireDragOut(root, null, {
        rowSelector: '.view-tile[draggable="true"]',
        alwaysOut: true,
    }));
}

export function unwire(root) {
    const remove = root ? wirings.get(root) : null;
    if (remove) {
        remove();
        wirings.delete(root);
    }
}
