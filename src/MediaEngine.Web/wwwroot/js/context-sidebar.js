export function attachContextSidebarResize(shell, handle, dotNet, minWidth, maxWidth) {
    if (!shell || !handle) return;
    handle.onpointerdown = event => {
        if (event.button !== 0) return;
        event.preventDefault();
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
            handle.onpointermove = null;
            handle.onpointerup = null;
            handle.onpointercancel = null;
            dotNet.invokeMethodAsync('CommitWidthAsync', width);
        };
        handle.onpointercancel = () => {
            handle.onpointermove = null;
            handle.onpointerup = null;
            handle.onpointercancel = null;
        };
    };
}

export function attachContextPanelSplits(root, dotNet) {
    if (!root) return;
    for (const handle of root.querySelectorAll('[data-split-index]')) {
        if (handle.dataset.splitAttached === 'true') continue;
        handle.dataset.splitAttached = 'true';
        handle.onpointerdown = event => {
            if (event.button !== 0) return;
            const first = handle.previousElementSibling;
            const second = handle.nextElementSibling;
            if (!first || !second) return;
            event.preventDefault();
            handle.setPointerCapture(event.pointerId);
            const startY = event.clientY;
            const firstHeight = first.getBoundingClientRect().height;
            const secondHeight = second.getBoundingClientRect().height;
            const pairHeight = firstHeight + secondHeight;
            const firstGrow = Number.parseFloat(first.style.flexGrow) || 1;
            const secondGrow = Number.parseFloat(second.style.flexGrow) || 1;
            const combinedGrow = firstGrow + secondGrow;
            const update = clientY => {
                const firstSize = Math.max(100, Math.min(pairHeight - 100, firstHeight + clientY - startY));
                const ratio = firstSize / pairHeight;
                const nextFirst = ratio * combinedGrow;
                const nextSecond = (1 - ratio) * combinedGrow;
                first.style.flexGrow = String(nextFirst);
                second.style.flexGrow = String(nextSecond);
                return [nextFirst, nextSecond];
            };
            handle.onpointermove = move => update(move.clientY);
            handle.onpointerup = end => {
                const [nextFirst, nextSecond] = update(end.clientY);
                handle.onpointermove = null;
                handle.onpointerup = null;
                handle.onpointercancel = null;
                dotNet.invokeMethodAsync('CommitSplitAsync', Number(handle.dataset.splitIndex), nextFirst, nextSecond);
            };
            handle.onpointercancel = () => {
                first.style.flexGrow = String(firstGrow);
                second.style.flexGrow = String(secondGrow);
                handle.onpointermove = null;
                handle.onpointerup = null;
                handle.onpointercancel = null;
            };
        };
    }
}
