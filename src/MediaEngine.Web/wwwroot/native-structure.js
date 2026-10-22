const previousFocus = new WeakMap();
const cancelHandlers = new WeakMap();
export function setDrawerOpen(dialog, open, dotnet) {
    if (!dialog) return;
    watchCancel(dialog, dotnet);
    if (open && !dialog.open) {
        previousFocus.set(dialog, document.activeElement);
        dialog.showModal();
    } else if (!open && dialog.open) {
        dialog.close();
        restoreFocus(dialog);
    }
}
// Escape fires `cancel` on a modal dialog. Keep the browser from closing it on its own
// so the component's Open state stays the one owner, and ask .NET to close it instead.
function watchCancel(dialog, dotnet) {
    if (!dotnet || cancelHandlers.has(dialog)) return;
    const handler = event => {
        event.preventDefault();
        dotnet.invokeMethodAsync('CloseFromEscapeAsync')
            .catch(error => console.debug('Drawer close was not delivered; the circuit is gone.', error));
    };
    cancelHandlers.set(dialog, handler);
    dialog.addEventListener('cancel', handler);
}
function restoreFocus(dialog) {
    const target = previousFocus.get(dialog);
    previousFocus.delete(dialog);
    if (target?.isConnected) target.focus({ preventScroll: true });
}
export function disposeDrawer(dialog) {
    if (dialog?.open) dialog.close();
    if (!dialog) return;
    const handler = cancelHandlers.get(dialog);
    if (handler) dialog.removeEventListener('cancel', handler);
    cancelHandlers.delete(dialog);
    restoreFocus(dialog);
}
