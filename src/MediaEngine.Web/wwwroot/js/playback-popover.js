// Playback panels (unified player v2 D11). One owner decides what is open (PlaybackTransientToolCoordinator on the
// Dashboard side); this module draws the open panel in the browser top layer, places it, and reports the ways a
// person closes it: the trigger, outside click and Escape. There is no hover-open anywhere.
const states = new WeakMap();
const opened = []; // States whose panel is open, parents before the nested panels they own.
const focusable = root => [...root.querySelectorAll('button:not(:disabled),a[href],input:not(:disabled),[tabindex="0"]')]
    .filter(element => element.getClientRects().length && !element.closest('[inert],[aria-hidden="true"]'));

const EDGE = 12;          // A panel stays at least this far inside the viewport.
const GAP = 8;                   // Space between a panel and the bar or trigger it opens from.
const MAX_HEIGHT_RATIO = 0.8;    // A panel is at most this share of the player or viewport height.
const OPEN_ATTRIBUTE = 'data-playback-panel-open';

/**
 * Pure placement math, exported so it can be tested without a browser.
 * trigger: {left,right,top,bottom}; panel: {width,height}; viewport: {x,y,width,height};
 * area: {top,bottom} the player's own box (video) or the viewport; anchorTop: the top of the bar the panel opens above;
 * alignRight: the x the panel's right edge lines up with (the tools zone's right edge, or the trigger's).
 */
export function computePlacement({ trigger, panel, viewport, area, anchorTop, alignRight, edge = EDGE, gap = GAP }) {
    const areaTop = Math.max(viewport.y, area?.top ?? viewport.y) + edge;
    const areaBottom = Math.min(viewport.y + viewport.height, area?.bottom ?? viewport.y + viewport.height) - edge;
    const areaHeight = Math.max(0, areaBottom - areaTop);
    const width = Math.min(panel.width, Math.max(0, viewport.width - edge * 2));
    const spaceAbove = Math.max(0, (anchorTop ?? trigger.top) - gap - areaTop);
    const spaceBelow = Math.max(0, areaBottom - (trigger.bottom + gap));
    const cap = Math.max(0, Math.floor(Math.min(areaHeight * MAX_HEIGHT_RATIO, viewport.height * MAX_HEIGHT_RATIO)));
    const wanted = Math.min(panel.height, cap);
    // Open above the bar; flip below the trigger only when above is too short and below has more room.
    const below = wanted > spaceAbove && spaceBelow > spaceAbove;
    const room = below ? spaceBelow : spaceAbove;
    const maxHeight = Math.max(0, Math.floor(Math.min(cap, room)));
    const height = Math.min(panel.height, maxHeight);
    const wantedLeft = (alignRight ?? trigger.right) - width;
    const left = Math.max(viewport.x + edge, Math.min(wantedLeft, viewport.x + viewport.width - edge - width));
    const top = below ? trigger.bottom + gap : Math.max(areaTop, (anchorTop ?? trigger.top) - gap - height);
    return { left, top, width, maxHeight, placement: below ? 'below' : 'above' };
}

const containerFor = () => document.fullscreenElement || document.body;

function showInTopLayer(panel) {
    try {
        if (panel.isConnected && typeof panel.showPopover === 'function' && !panel.matches?.(':popover-open')) panel.showPopover();
    } catch (error) {
        // A browser without the Popover API still shows the fixed-position panel above the page.
        if (!(error instanceof DOMException)) throw error;
    }
}

function leaveTopLayer(panel) {
    try {
        if (typeof panel.hidePopover === 'function' && panel.matches?.(':popover-open')) panel.hidePopover();
    } catch (error) {
        if (!(error instanceof DOMException)) throw error;
    }
}

function publishOpen() {
    const root = document.documentElement;
    if (!root?.setAttribute) return;
    if (opened.length) root.setAttribute(OPEN_ATTRIBUTE, 'true'); else root.removeAttribute?.(OPEN_ATTRIBUTE);
}

// True when `child` was opened from inside `parent` (directly, or through other nested panels).
function isChildOf(child, parent) {
    let owner = child.panel?.dataset?.playbackOwnedMenu;
    for (let depth = 0; owner && depth < 8; depth++) {
        if (owner === parent.panel?.id) return true;
        owner = opened.find(candidate => candidate.panel?.id === owner)?.panel?.dataset?.playbackOwnedMenu;
    }
    return false;
}

export function attach(root, externalTrigger, owner) {
    if (!root) return;
    const trigger = externalTrigger || root.querySelector('button');
    if (!trigger) return;
    if (states.get(root)?.trigger === trigger) return;
    if (states.has(root)) detach(root);
    const state = { root, trigger, owner, panel: null, marker: null, open: false, focusOnOpen: false,
        restoreOnClose: false, resizeObserver: null, mutationObserver: null, listeners: [] };
    const listen = (target, name, callback, options) => {
        target.addEventListener(name, callback, options);
        state.listeners.push(() => target.removeEventListener(name, callback, options));
    };
    const open = () => { owner.invokeMethodAsync('OpenAsync', true); };
    const close = restore => { state.open = false; state.restoreOnClose = restore; releasePanel(state); owner.invokeMethodAsync('CloseFromBrowserAsync', restore); };
    state.close = close;
    listen(trigger, 'click', event => {
        event.preventDefault();
        state.focusOnOpen = event.detail === 0;
        if (state.open) close(true); else open();
    });
    listen(document, 'pointerdown', event => {
        if (!state.open || trigger.contains(event.target) || state.panel?.contains(event.target)
            || event.target.closest?.('[data-playback-owned-menu]')?.dataset.playbackOwnedMenu === state.panel?.id) return;
        close(false); // The newly activated target retains its intended focus.
    }, true);
    listen(document, 'keydown', event => {
        if (!state.open || event.key !== 'Escape') return;
        if (state.panel?.querySelector('.tl-popover-open[data-playback-owned-menu]')) return; // A nested selector handles its own Escape first.
        if (state.panel?.id && document.querySelector(`[data-playback-popover-panel][data-playback-owned-menu="${state.panel.id}"]`)) return;
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
    states.set(root, state);
}

function releasePanel(state) {
    state.resizeObserver?.disconnect(); state.mutationObserver?.disconnect();
    if (state.panel) {
        state.panel.removeEventListener('pointerdown', state.onPanelPointerDown);
        state.panel.removeAttribute?.('data-playback-panel-placed');
        leaveTopLayer(state.panel);
        if (state.marker?.isConnected) state.marker.replaceWith(state.panel);
        else state.panel.remove(); // The owner already left; never retain an orphan portal.
    }
    state.panel = null; state.marker = null;
    const index = opened.indexOf(state);
    if (index >= 0) opened.splice(index, 1);
    publishOpen();
}

const sizeOf = (element, fallback) => {
    const box = element.getBoundingClientRect?.() ?? {};
    return { width: box.width || fallback.width, height: box.height || fallback.height };
};

function position(state) {
    if (!state.open || !state.panel?.isConnected || !state.trigger.isConnected) return;
    const panel = state.panel;
    const container = containerFor();
    if (panel.parentNode !== container) { container.append(panel); }
    showInTopLayer(panel);
    const visual = window.visualViewport;
    const viewport = { x: visual?.offsetLeft || 0, y: visual?.offsetTop || 0, width: visual?.width || innerWidth, height: visual?.height || innerHeight };
    if (panel.classList.contains('playback-panel--sheet')) {
        // A bottom sheet fills the visual viewport; the sheet inside it sits on the bottom edge.
        Object.assign(panel.style, { left: `${viewport.x}px`, top: `${viewport.y}px`, width: `${viewport.width}px`, height: `${viewport.height}px`, maxHeight: `${viewport.height}px` });
        panel.setAttribute('data-playback-panel-placed', 'true');
        return;
    }
    const trigger = state.trigger.getBoundingClientRect();
    const audioDock = state.trigger.closest('.listen-player');
    const videoHost = panel.dataset?.playbackPopoverSurface === 'video' ? state.trigger.closest('.video-playback-host,.media-viewer__stage') : null;
    const hostBox = videoHost?.getBoundingClientRect();
    const chromeTop = (videoHost ?? state.trigger.closest('[data-playback-bar]'))?.querySelector?.('[data-playback-chrome-bottom],[data-playback-bar]')?.getBoundingClientRect().top;
    const anchorTop = audioDock ? Math.min(trigger.top, audioDock.getBoundingClientRect().top) : chromeTop ?? trigger.top;
    const zone = state.trigger.closest('[data-playback-tools-zone]');
    const isMenu = panel.classList.contains('playback-panel--menu');
    const alignRight = !isMenu && zone ? zone.getBoundingClientRect().right : trigger.right;
    // Measure at the CSS width, uncapped in height, so the cap reflects the real content.
    panel.style.maxHeight = '';
    const natural = sizeOf(panel, { width: 400, height: 0 });
    const placed = computePlacement({ trigger, panel: natural, viewport, area: hostBox ? { top: hostBox.top, bottom: hostBox.bottom } : null, anchorTop, alignRight });
    Object.assign(panel.style, { left: `${placed.left}px`, top: `${placed.top}px`, maxWidth: `${Math.max(0, viewport.width - EDGE * 2)}px`, maxHeight: `${placed.maxHeight}px` });
    panel.dataset.playbackPanelPlacement = placed.placement;
    panel.setAttribute('data-playback-panel-placed', 'true');
}

export function update(root, panel, open, restoreFocus = false) {
    const state = states.get(root); if (!state) return;
    state.open = open;
    state.trigger.setAttribute('aria-expanded', open ? 'true' : 'false');
    if (!open) {
        state.restoreOnClose ||= restoreFocus;
        releasePanel(state);
        if (state.restoreOnClose && state.trigger.isConnected) state.trigger.focus();
        state.restoreOnClose = false;
        return;
    }
    if (!panel?.isConnected || !state.trigger.isConnected) { state.open = false; releasePanel(state); return; }
    if (panel !== state.panel) {
        releasePanel(state);
        state.panel = panel;
        // Exactly one top-level panel at a time: opening another closes the others; a panel opened from inside a panel is its child.
        for (const other of [...opened]) {
            if (other !== state && !isChildOf(state, other)) other.close(false);
        }
        opened.push(state);
        publishOpen();
        state.marker = document.createComment('playback-panel-position');
        panel.before(state.marker);
        state.onPanelPointerDown = event => {
            if (event.target.closest('[data-playback-popover-backdrop]')) state.close(true);
        };
        panel.addEventListener('pointerdown', state.onPanelPointerDown);
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
    if (state.focusOnOpen && !panel.classList.contains('playback-panel--sheet')) {
        (focusable(panel)[0] || panel).focus(); state.focusOnOpen = false;
    }
}

export function detach(root) {
    const state = states.get(root); if (!state) return;
    releasePanel(state); state.listeners.forEach(remove => remove()); states.delete(root);
}
