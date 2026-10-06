const previousFocus = new WeakMap();
export function setDrawerOpen(dialog, open) {
    if (!dialog) return;
    if (open && !dialog.open) {
        previousFocus.set(dialog, document.activeElement);
        dialog.showModal();
    } else if (!open && dialog.open) {
        dialog.close();
        restoreFocus(dialog);
    }
}
function restoreFocus(dialog) {
    const target = previousFocus.get(dialog);
    previousFocus.delete(dialog);
    if (target?.isConnected) target.focus({ preventScroll: true });
}
export function disposeDrawer(dialog) {
    if (dialog?.open) dialog.close();
    if (dialog) restoreFocus(dialog);
}
