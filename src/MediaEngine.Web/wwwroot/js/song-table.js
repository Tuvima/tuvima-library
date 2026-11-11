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
