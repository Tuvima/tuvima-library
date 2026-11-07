// Keeps the highlighted row of the shared browser frame (AppBrowserFrame) in view while the arrow keys move through a long list.
// The list is a grid whose focus stays on the table itself (aria-activedescendant), so the browser does not scroll to the row on its own.
export function scrollRowIntoView(rowId) {
    if (typeof rowId !== 'string' || rowId.length === 0) return;
    document.getElementById(rowId)?.scrollIntoView({ block: 'nearest' });
}
