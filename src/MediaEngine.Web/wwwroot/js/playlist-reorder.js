// Reordering a list of playlists by finger (spec listen-playlists WP5, issue 1201): the Listen lane's sidebar, on a touch tablet. A playlist
// row carries data-playlist-reorder-row (its playlist id) and holds a grip with data-playlist-reorder-handle. The drag itself is
// touch-reorder.js; a drop tells the page which playlist moved and which one it was dropped before or after. A mouse keeps the browser's drag.
import { wireToDotnet } from './touch-reorder.js';

export const PLAYLIST_REORDER = Object.freeze({
    handle: '[data-playlist-reorder-handle]',
    row: '[data-playlist-reorder-row]',
    keyAttribute: 'data-playlist-reorder-row',
    dropMethod: 'TouchReorderPlaylistAsync',
});

const wirings = new WeakMap();

// `dropMethod` names the .NET method that hears a drop (the Listen page and the section shell's reorder mode each have their own).
export function attach(root, dotnetRef, dropMethod) {
    detach(root);
    if (root) wirings.set(root, wireToDotnet(root, dotnetRef, { ...PLAYLIST_REORDER, dropMethod: dropMethod || PLAYLIST_REORDER.dropMethod }));
}

export function detach(root) {
    if (root && wirings.has(root)) {
        wirings.get(root)();
        wirings.delete(root);
    }
}
