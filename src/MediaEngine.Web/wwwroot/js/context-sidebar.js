const resizeAttachments = new WeakMap();

export function createContextSidebarModalLifecycle(loadModal, getActiveElement) {
    const modalAttachments = new WeakMap();
    const load = loadModal || (() => import('./playback-tool-sheet.js'));
    const activeElement = getActiveElement || (() => document.activeElement);

    async function attach(shell, aside, attachmentKey) {
        if (!shell || !aside || attachmentKey == null) return;
        const current = modalAttachments.get(shell);
        if (current?.attachmentKey === attachmentKey && current.aside === aside) return;
        let previous = activeElement();
        if (current?.previous && current.aside?.contains?.(previous)) previous = current.previous;
        if (current) restore(shell, current.attachmentKey, false);

        const state = { attachmentKey, aside, previous, module: null };
        modalAttachments.set(shell, state);
        const module = await load();
        if (modalAttachments.get(shell) !== state || !shell.isConnected || !aside.isConnected) return;
        state.module = module;
        module.attachModal(aside, state.previous);
    }

    function restore(shell, attachmentKey, restorePrevious = true) {
        if (!shell) return false;
        const state = modalAttachments.get(shell);
        if (!state || state.attachmentKey !== attachmentKey) return false;
        modalAttachments.delete(shell);
        if (state.module) state.module.restoreFocus(state.aside, restorePrevious);
        return true;
    }

    return {
        attach,
        restore,
        attachContextSidebarModal: (shell, aside, attachmentKey) => attach(shell, aside, attachmentKey),
        restoreContextSidebarModal: (shell, attachmentKey, restorePreviousFocus = true) =>
            restore(shell, attachmentKey, restorePreviousFocus),
    };
}

const contextSidebarModalLifecycle = createContextSidebarModalLifecycle();

export function attachContextSidebarResize(shell, handle, dotNet, minWidth, maxWidth) {
    if (!shell || !handle) return;
    detachContextSidebarResize(handle);
    const state = { pointerId: null };
    resizeAttachments.set(handle, state);
    handle.onpointerdown = event => {
        if (event.button !== 0) return;
        event.preventDefault();
        state.pointerId = event.pointerId;
        handle.setPointerCapture(event.pointerId);
        const right = shell.getBoundingClientRect().right;
        const update = clientX => {
            const width = Math.max(minWidth, Math.min(maxWidth, Math.round(right - clientX)));
            shell.style.setProperty('--context-sidebar-width', `${width}px`);
            handle.setAttribute('aria-valuenow', String(width));
            return width;
        };
        update(event.clientX);
        handle.onpointermove = move => update(move.clientX);
        handle.onpointerup = end => {
            const width = update(end.clientX);
            clearActivePointer(handle, state);
            dotNet.invokeMethodAsync('CommitWidthAsync', width);
        };
        handle.onpointercancel = () => clearActivePointer(handle, state);
    };
}

function clearActivePointer(handle, state) {
    handle.onpointermove = null;
    handle.onpointerup = null;
    handle.onpointercancel = null;
    if (state.pointerId !== null && handle.hasPointerCapture?.(state.pointerId)) {
        handle.releasePointerCapture(state.pointerId);
    }
    state.pointerId = null;
}

export function detachContextSidebarResize(handle) {
    if (!handle) return;
    const state = resizeAttachments.get(handle);
    if (state) clearActivePointer(handle, state);
    handle.onpointerdown = null;
    resizeAttachments.delete(handle);
}

export function attachContextSidebarModal(shell, aside, attachmentKey) {
    return contextSidebarModalLifecycle.attachContextSidebarModal(shell, aside, attachmentKey);
}

export function restoreContextSidebarModal(shell, attachmentKey, restorePreviousFocus = true) {
    return contextSidebarModalLifecycle.restoreContextSidebarModal(shell, attachmentKey, restorePreviousFocus);
}
