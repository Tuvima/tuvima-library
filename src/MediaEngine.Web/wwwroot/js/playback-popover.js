const states = new WeakMap();
const focusable = root => [...root.querySelectorAll('button:not(:disabled),a[href],input:not(:disabled),[tabindex="0"]')]
    .filter(element => element.getClientRects().length && !element.closest('[inert],[aria-hidden="true"]'));

export function attach(root, externalTrigger, owner) {
    if (!root) return;
    const trigger = externalTrigger || root.querySelector('button');
    if (!trigger) return;
    if (states.get(root)?.trigger === trigger) return;
    if (states.has(root)) detach(root);
    const state = { trigger, owner, panel: null, marker: null, pinned: false, open: false, focusOnOpen: false,
        restoreOnClose: false, hoverTimer: null, closeTimer: null, resizeObserver: null, mutationObserver: null, listeners: [] };
    const listen = (target, name, callback, options) => {
        target.addEventListener(name, callback, options);
        state.listeners.push(() => target.removeEventListener(name, callback, options));
    };
    const cancel = () => { clearTimeout(state.hoverTimer); clearTimeout(state.closeTimer); };
    const open = pinned => { cancel(); owner.invokeMethodAsync('OpenAsync', pinned); };
    const close = restore => { cancel(); state.open = false; state.restoreOnClose = restore; releasePanel(state); owner.invokeMethodAsync('CloseFromBrowserAsync', restore); };
    const enter = event => {
        clearTimeout(state.closeTimer);
        if (event.pointerType === 'touch' || !matchMedia('(hover:hover) and (pointer:fine)').matches || state.open) return;
        state.hoverTimer = setTimeout(() => open(false), 120);
    };
    const leave = event => {
        clearTimeout(state.hoverTimer);
        if (state.pinned || state.panel?.contains(event.relatedTarget) || trigger.contains(event.relatedTarget)) return;
        state.closeTimer = setTimeout(() => close(false), 300);
    };
    listen(trigger, 'pointerenter', enter);
    listen(trigger, 'pointerleave', leave);
    listen(trigger, 'click', event => {
        event.preventDefault();
        state.focusOnOpen = event.detail === 0;
        if (state.open && state.pinned) close(true); else open(true);
    });
    listen(document, 'pointerdown', event => {
        if (!state.open || trigger.contains(event.target) || state.panel?.contains(event.target)
            || event.target.closest?.('[data-playback-owned-menu]')?.dataset.playbackOwnedMenu === state.panel?.id) return;
        close(false); // The newly activated target retains its intended focus.
    }, true);
    listen(document, 'keydown', event => {
        if (!state.open || event.key !== 'Escape') return;
        if (state.panel?.querySelector('.mud-popover-open[data-playback-owned-menu]')) return; // Nested selector handles its own Escape first.
        event.preventDefault(); event.stopImmediatePropagation(); close(true);
    }, true);
    const reposition = () => position(state);
    listen(window, 'resize', reposition);
    listen(window, 'scroll', reposition, true);
    listen(document, 'fullscreenchange', reposition);
    if (window.visualViewport) { listen(visualViewport, 'resize', reposition); listen(visualViewport, 'scroll', reposition); }
    const breakpoint = matchMedia('(max-width:720px)');
    const viewportChanged = () => owner.invokeMethodAsync('SetViewport', breakpoint.matches);
    listen(breakpoint, 'change', viewportChanged);
    viewportChanged();
    state.enter = enter; state.leave = leave; state.close = close; state.cancel = cancel;
    states.set(root, state);
}

function releasePanel(state) {
    state.resizeObserver?.disconnect(); state.mutationObserver?.disconnect();
    if (state.panel) {
        state.panel.removeEventListener('pointerenter', state.enter);
        state.panel.removeEventListener('pointerleave', state.leave);
        state.panel.removeEventListener('pointerdown', state.onPanelPointerDown);
        state.panel.removeEventListener('focusout', state.onFocusOut);
        if (state.marker?.isConnected) state.marker.replaceWith(state.panel);
        else state.panel.remove(); // The owner already left; never retain an orphan portal.
    }
    state.panel = null; state.marker = null;
}

function position(state) {
    if (!state.open || !state.panel?.isConnected || !state.trigger.isConnected) return;
    const container = document.fullscreenElement || document.body;
    if (state.panel.parentNode !== container) container.append(state.panel);
    const viewport = window.visualViewport;
    const x = viewport?.offsetLeft || 0, y = viewport?.offsetTop || 0;
    const width = viewport?.width || innerWidth, height = viewport?.height || innerHeight;
    if (width <= 720) {
        Object.assign(state.panel.style, { left: `${x}px`, top: `${y}px`, width: `${width}px`, height: `${height}px`, maxHeight: `${height}px` });
        return;
    }
    const trigger = state.trigger.getBoundingClientRect();
    state.panel.style.width = `${Math.min(width - 16, state.panel.classList.contains('playback-popover--lyrics') ? 460 : state.panel.classList.contains('playback-popover--video') ? 440 : 400)}px`;
    const audioDock = state.trigger.closest('.listen-player');
    const anchorTop = audioDock ? Math.min(trigger.top, audioDock.getBoundingClientRect().top) : trigger.top;
    const spaceAbove = Math.max(0, anchorTop - y - 16);
    state.panel.style.maxHeight = `${Math.min(height * .7, 640, spaceAbove)}px`;
    state.panel.style.height = '';
    const box = state.panel.getBoundingClientRect();
    state.panel.style.left = `${Math.max(x + 8, Math.min(trigger.right - box.width, x + width - box.width - 8))}px`;
    state.panel.style.top = `${Math.max(y + 8, anchorTop - box.height - 8)}px`;
}

export function update(root, panel, open, pinned, restoreFocus = false) {
    const state = states.get(root); if (!state) return;
    state.open = open; state.pinned = pinned;
    state.trigger.setAttribute('aria-expanded', open ? 'true' : 'false');
    if (!open) {
        state.restoreOnClose ||= restoreFocus;
        state.cancel(); releasePanel(state);
        if (state.restoreOnClose && state.trigger.isConnected) state.trigger.focus();
        state.restoreOnClose = false;
        return;
    }
    if (!panel?.isConnected || !state.trigger.isConnected) { state.open = false; releasePanel(state); return; }
    if (panel !== state.panel) {
        releasePanel(state);
        state.panel = panel;
        state.marker = document.createComment('playback-popover-position');
        panel.before(state.marker);
        state.onPanelPointerDown = event => {
            if (event.target.closest('[data-playback-popover-backdrop]')) state.close(true);
            else if (!state.pinned) { state.pinned = true; state.owner.invokeMethodAsync('OpenAsync', true); }
        };
        state.onFocusOut = event => {
            if (state.panel.classList.contains('playback-popover--sheet')) return;
            if (!state.panel.contains(event.relatedTarget) && !state.trigger.contains(event.relatedTarget)) state.close(false);
        };
        panel.addEventListener('pointerenter', state.enter); panel.addEventListener('pointerleave', state.leave);
        panel.addEventListener('pointerdown', state.onPanelPointerDown); panel.addEventListener('focusout', state.onFocusOut);
        state.resizeObserver = new ResizeObserver(() => position(state));
        state.resizeObserver.observe(panel); state.resizeObserver.observe(state.trigger);
        const centerCurrent = () => {
            const key = panel.querySelector('[data-playback-context-scroll-key]')?.dataset.playbackContextScrollKey || panel.id;
            if (state.centeredKey === key) return;
            state.centeredKey = key;
            requestAnimationFrame(() => {
            const current = panel.querySelector('[aria-current="true"]');
            const scroller = current?.closest('.playback-sheet-list');
            if (scroller && state.panel === panel) scroller.scrollTop = current.offsetTop - scroller.offsetTop - scroller.clientHeight / 2 + current.clientHeight / 2;
            });
        };
        state.centeredKey = null;
        state.mutationObserver = new MutationObserver(() => { position(state); centerCurrent(); });
        state.mutationObserver.observe(panel, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['data-playback-context-scroll-key'] });
        centerCurrent();
    }
    state.trigger.setAttribute('aria-controls', panel.id);
    position(state);
    if (state.focusOnOpen && !panel.classList.contains('playback-popover--sheet')) {
        (focusable(panel)[0] || panel).focus(); state.focusOnOpen = false;
    }
}

export function detach(root) {
    const state = states.get(root); if (!state) return;
    state.cancel(); releasePanel(state); state.listeners.forEach(remove => remove()); states.delete(root);
}
