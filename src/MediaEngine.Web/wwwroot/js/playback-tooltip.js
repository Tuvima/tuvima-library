// Two ways in. AppTooltip renders .playback-tooltip-owner[data-playback-tooltip] and does no script work of its own: ONE controller,
// installed when this module loads (it is imported once per page by app-overflow-tooltip.js), listens on the document and serves every
// owner, however many the page has and however Blazor redraws them. attach/detach stay for the parts that wire a single element
// themselves (AppSelect, cut-off text).
const states = new WeakMap();
let nextId = 0;
let lastHiddenAt = 0;
const HOVER_DELAY_MS = 150, WARM_WINDOW_MS = 300, TOUCH_PRESS_MS = 500, TOUCH_LINGER_MS = 1500, MOUSE_REST_MS = 4000;
// The one visible tooltip (Look v3 D8): { root, target, tooltip, hide }. It goes when its control leaves the page or is replaced,
// on any press anywhere, when a menu or dialog opens, and after 4 s of rest on a mouse (bug 27: nothing used to hide it).
let active = null;
let pageObserver = null;
const OPENED_SURFACE = '[role="menu"], [role="dialog"], [role="listbox"], dialog[open]';

function hideActive() { active?.hide(); }

function pageChanged(records) {
    if (!active) return;
    if (active.root.isConnected === false || active.target?.isConnected === false) { hideActive(); return; }
    for (const record of records ?? []) {
        if (record.type === 'attributes') {
            const element = record.target;
            if (element === active.tooltip) continue;
            if (active.delegated && record.attributeName?.startsWith('data-playback-tooltip')) { refreshActive(element); if (!active) return; continue; }
            if (record.attributeName === 'aria-expanded' && element.getAttribute?.('aria-expanded') === 'true') { hideActive(); return; }
            if (record.attributeName === 'open' && element.hasAttribute?.('open')) { hideActive(); return; }
        } else if (record.type === 'childList') {
            for (const node of record.addedNodes ?? []) {
                if (node === active.tooltip || typeof node.matches !== 'function') continue;
                if (node.matches(OPENED_SURFACE) || node.querySelector?.(OPENED_SURFACE)) { hideActive(); return; }
            }
        }
    }
}

function watchPage() {
    if (!pageObserver && typeof MutationObserver === 'function') pageObserver = new MutationObserver(pageChanged);
    const root = document.documentElement || document.body;
    if (pageObserver && root) pageObserver.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ['aria-expanded', 'open', 'data-playback-tooltip', 'data-playback-tooltip-suppressed'] });
}

if (typeof document !== 'undefined' && typeof document.addEventListener === 'function') {
    // A press anywhere hides the tooltip; a touch press on the control itself is left to its own long-press handling.
    document.addEventListener('pointerdown', event => {
        if (!active || (event.pointerType === 'touch' && active.root.contains?.(event.target))) return;
        hideActive();
    }, true);
    // A popover or dialog opening (menus, pop-outs, windows) hides the tooltip; the tooltip's own popover is ignored.
    document.addEventListener('toggle', event => {
        if (active && event.newState === 'open' && event.target !== active.tooltip) hideActive();
    }, true);
}

// A tooltip lives in the browser's top layer (popover="manual" + showPopover()), inside the open modal dialog when there is one, so a
// dialog or its backdrop can never cover it (F9). Without a dialog it goes in the fullscreen element, else the body.
function tooltipHost() {
    const dialogs = [...document.querySelectorAll('dialog[open]')].filter(dialog => dialog.matches(':modal'));
    return dialogs.at(-1) || document.fullscreenElement || document.body;
}

// Builds the tooltip element for a control, puts it in the top layer and works out where it sits (above the control, flipped below
// when there is no room, and kept inside the visual viewport).
function openTooltip(root, target) {
    const tooltip = document.createElement('span');
    tooltip.id = `playback-tooltip-${++nextId}`;
    tooltip.className = 'playback-tooltip'; tooltip.role = 'tooltip';
    tooltip.textContent = root.dataset.playbackTooltip;
    tooltip.setAttribute('popover', 'manual');
    // The browser's own popover box (centred, inset 0, scrolling) is replaced by the plain fixed box the position below is worked out for.
    tooltip.style.inset = 'auto'; tooltip.style.margin = '0'; tooltip.style.overflow = 'visible';
    tooltipHost().append(tooltip);
    if (typeof tooltip.showPopover === 'function') tooltip.showPopover();
    const box = target.getBoundingClientRect(), tip = tooltip.getBoundingClientRect();
    const viewport = window.visualViewport;
    const left = viewport?.offsetLeft || 0, top = viewport?.offsetTop || 0;
    const width = viewport?.width || innerWidth, height = viewport?.height || innerHeight;
    tooltip.style.left = `${Math.max(left + 8, Math.min(box.left + box.width / 2 - tip.width / 2, left + width - tip.width - 8))}px`;
    const preferred = root.dataset.playbackTooltipPlacement?.startsWith('bottom') ? box.bottom + 8 : box.top - tip.height - 8;
    tooltip.style.top = `${Math.max(top + 8, Math.min(preferred < top + 8 ? box.bottom + 8 : preferred, top + height - tip.height - 8))}px`;
    return tooltip;
}

export function attach(root, options) {
    if (!root || states.has(root)) return;
    if (!pageObserver && typeof MutationObserver === 'function') pageObserver = new MutationObserver(pageChanged);
    let timer, restTimer, tooltip, target, description;
    const candidate = root.querySelector('button, a[href], input, [role="combobox"], [tabindex="0"]') || root.closest('button, a[href], [tabindex="0"]');
    const originalTab = root.getAttribute('tabindex');
    const wrapperTarget = !options?.hoverOnly && (!candidate || candidate.disabled);
    if (wrapperTarget) { root.setAttribute('tabindex', '0'); root.dataset.tooltipWrapperTarget = 'true'; }
    const hide = () => {
        clearTimeout(timer); clearTimeout(restTimer);
        if (active?.hide === hide) { active = null; pageObserver?.disconnect(); }
        if (target && tooltip) {
            if (description === null) target.removeAttribute('aria-describedby');
            else target.setAttribute('aria-describedby', description);
        }
        if (tooltip) lastHiddenAt = Date.now();
        tooltip?.remove(); tooltip = null; target = null;
    };
    const show = (byMouse = false) => {
        if (active && active.hide !== hide) active.hide();
        hide();
        target = wrapperTarget ? root : (candidate || root);
        if (!target || root.dataset.playbackTooltipSuppressed === 'true'
            || target.getAttribute('aria-expanded') === 'true' || !root.dataset.playbackTooltip) return;
        tooltip = openTooltip(root, target);
        description = target.getAttribute('aria-describedby');
        target.setAttribute('aria-describedby', [description, tooltip.id].filter(Boolean).join(' '));
        active = { root, target, tooltip, hide };
        watchPage();
        if (byMouse) restTimer = setTimeout(hide, MOUSE_REST_MS);
    };
    const enter = event => {
        if (event.pointerType === 'touch') return;
        clearTimeout(timer);
        timer = setTimeout(() => show(true), Date.now() - lastHiddenAt < WARM_WINDOW_MS ? 0 : HOVER_DELAY_MS);
    };
    const focus = event => { if (event.target.matches(':focus-visible')) show(); };
    const key = event => { if (event.key === 'Escape') hide(); };
    const down = event => { hide(); if (event.pointerType === 'touch') timer = setTimeout(show, TOUCH_PRESS_MS); };
    // After a long press the text stays readable for a moment instead of vanishing under the thumb.
    const up = event => { if (event.pointerType === 'touch' && tooltip) { clearTimeout(timer); timer = setTimeout(hide, TOUCH_LINGER_MS); } else hide(); };
    const observer = new MutationObserver(() => {
        if (!tooltip) return;
        if (root.dataset.playbackTooltipSuppressed === 'true' || target?.getAttribute('aria-expanded') === 'true') hide();
        else tooltip.textContent = root.dataset.playbackTooltip || '';
    });
    observer.observe(root, { subtree: true, attributes: true, attributeFilter: ['data-playback-tooltip', 'data-playback-tooltip-suppressed', 'aria-expanded'] });
    root.addEventListener('pointerenter', enter);
    root.addEventListener('pointerleave', hide);
    root.addEventListener('focusin', focus);
    root.addEventListener('focusout', hide);
    root.addEventListener('pointerdown', down);
    root.addEventListener('pointerup', up);
    root.addEventListener('pointercancel', hide);
    root.addEventListener('keydown', key);
    window.addEventListener('resize', hide);
    window.addEventListener('scroll', hide, true);
    states.set(root, { dispose() {
        hide(); observer.disconnect();
        root.removeEventListener('pointerenter', enter); root.removeEventListener('pointerleave', hide);
        root.removeEventListener('focusin', focus); root.removeEventListener('focusout', hide);
        root.removeEventListener('pointerdown', down); root.removeEventListener('pointerup', up); root.removeEventListener('pointercancel', hide); root.removeEventListener('keydown', key);
        if (wrapperTarget) { delete root.dataset.tooltipWrapperTarget; if (originalTab === null) root.removeAttribute('tabindex'); else root.setAttribute('tabindex', originalTab); }
        window.removeEventListener('resize', hide); window.removeEventListener('scroll', hide, true);
    }});
}

export function detach(root) { states.get(root)?.dispose(); states.delete(root); }

// ---- The one shared controller for every AppTooltip -------------------------------------------------------------------------------
// AppTooltip renders <span class="playback-tooltip-owner" data-playback-tooltip="text" data-playback-tooltip-placement data-playback-tooltip-suppressed>.
// Hover, focus, press, Escape and the rest are handled here on the document, so there is no per-tooltip script, no per-tooltip listener
// and nothing to clean up when Blazor redraws or removes an owner. Only one tooltip shows at a time, so one state serves them all.
const OWNER = '.playback-tooltip-owner';
const CANDIDATE = 'button, a[href], input, [role="combobox"], [tabindex="0"]';
const CANDIDATE_ABOVE = 'button, a[href], [tabindex="0"]';
// The owner the controller is working for (waiting to show, or showing), its tooltip, the control it describes and that control's
// aria-describedby from before.
let owner = null, ownerTimer, ownerRest, ownerTip = null, ownerTarget = null, ownerDescription = null;

function ownerOf(node) {
    return node && typeof node.closest === 'function' ? node.closest(OWNER) : null;
}

function closeOwnerTip() {
    clearTimeout(ownerTimer); clearTimeout(ownerRest);
    ownerTimer = ownerRest = undefined;
    if (active?.hide === closeOwnerTip) { active = null; pageObserver?.disconnect(); }
    if (ownerTarget && ownerTip) {
        if (ownerDescription === null) ownerTarget.removeAttribute('aria-describedby');
        else ownerTarget.setAttribute('aria-describedby', ownerDescription);
    }
    if (ownerTip) lastHiddenAt = Date.now();
    ownerTip?.remove();
    ownerTip = ownerTarget = owner = null;
}

function closeFor(element) { if (element && owner === element) closeOwnerTip(); }

function showFor(element, byMouse = false) {
    if (element.isConnected === false) return;
    if (active && active.hide !== closeOwnerTip) active.hide();
    closeOwnerTip();
    const wrapperTarget = element.dataset.tooltipWrapperTarget === 'true';
    const target = wrapperTarget ? element : (element.querySelector(CANDIDATE) || element.closest(CANDIDATE_ABOVE) || element);
    if (element.dataset.playbackTooltipSuppressed === 'true'
        || target.getAttribute('aria-expanded') === 'true' || !element.dataset.playbackTooltip) return;
    owner = element; ownerTarget = target;
    ownerTip = openTooltip(element, target);
    ownerDescription = target.getAttribute('aria-describedby');
    target.setAttribute('aria-describedby', [ownerDescription, ownerTip.id].filter(Boolean).join(' '));
    active = { root: element, target, tooltip: ownerTip, hide: closeOwnerTip, delegated: true };
    watchPage();
    if (byMouse) ownerRest = setTimeout(closeOwnerTip, MOUSE_REST_MS);
}

// A change to the shown owner's text or suppression (a menu opened from its control) updates or closes the open tooltip.
// Only the owner whose tooltip is showing counts: `owner` can already point at a nested owner that is still waiting to show.
function refreshActive(changed) {
    if (!active?.delegated || !ownerTip) return;
    const shown = active.root;
    if (!shown || !(changed === shown || shown.contains?.(changed))) return;
    if (shown.dataset.playbackTooltipSuppressed === 'true' || ownerTarget?.getAttribute('aria-expanded') === 'true') closeOwnerTip();
    else ownerTip.textContent = shown.dataset.playbackTooltip || '';
}

// An owner still waiting to show (a hover or press timer) is dropped once Blazor removes it, so nothing keeps the removed element.
// When a nested owner was waiting while its parent's tooltip shows, the parent's tooltip stays and the parent is the owner again.
function dropDetachedPendingOwner() {
    if (!owner || owner.isConnected !== false) return;
    if (ownerTip && active?.hide === closeOwnerTip && active.root !== owner) {
        clearTimeout(ownerTimer); ownerTimer = undefined;
        owner = active.root;
    } else closeOwnerTip();
}

// An owner whose content cannot take focus itself (an info icon, a status symbol, a disabled button) becomes focusable so a keyboard
// user can reach the text. Decided when the owner first appears on the page, as the per-element attach always did.
function prepareOwner(element) {
    if (states.has(element) || element.dataset.tooltipWrapperTarget === 'true') return;
    const candidate = element.querySelector(CANDIDATE) || element.closest(CANDIDATE_ABOVE);
    if (candidate && !candidate.disabled) return;
    element.setAttribute('tabindex', '0'); element.dataset.tooltipWrapperTarget = 'true';
}

function prepareWithin(node) {
    if (!node || node.nodeType !== 1) return;
    if (typeof node.matches === 'function' && node.matches(OWNER)) prepareOwner(node);
    if (typeof node.querySelectorAll === 'function') for (const found of node.querySelectorAll(OWNER)) prepareOwner(found);
}

if (typeof document !== 'undefined' && typeof document.addEventListener === 'function') {
    const listen = (type, handler) => document.addEventListener(type, handler, true);
    // pointerenter and pointerleave do not bubble, but a capturing listener on the document still sees them for every element, so the
    // owner itself is the target; this keeps the exact enter and leave timing of a listener on the owner (also over a disabled control).
    listen('pointerenter', event => {
        const element = event.target;
        if (event.pointerType === 'touch' || typeof element?.matches !== 'function' || !element.matches(OWNER) || states.has(element)) return;
        clearTimeout(ownerTimer);
        owner = element;
        ownerTimer = setTimeout(() => showFor(element, true), Date.now() - lastHiddenAt < WARM_WINDOW_MS ? 0 : HOVER_DELAY_MS);
    });
    listen('pointerleave', event => { if (event.target === owner) closeOwnerTip(); });
    listen('focusin', event => {
        const element = ownerOf(event.target);
        if (element && !states.has(element) && event.target.matches(':focus-visible')) showFor(element);
    });
    listen('focusout', event => closeFor(ownerOf(event.target)));
    listen('keydown', event => { if (event.key === 'Escape' && owner && owner.contains?.(event.target)) closeOwnerTip(); });
    listen('pointerdown', event => {
        const element = ownerOf(event.target);
        if (!element || states.has(element)) return;
        closeOwnerTip();
        if (event.pointerType === 'touch') { owner = element; ownerTimer = setTimeout(() => showFor(element), TOUCH_PRESS_MS); }
    });
    // After a long press the text stays readable for a moment instead of vanishing under the thumb.
    listen('pointerup', event => {
        const element = ownerOf(event.target);
        if (!element || element !== owner) return;
        if (event.pointerType === 'touch' && ownerTip) { clearTimeout(ownerTimer); ownerTimer = setTimeout(closeOwnerTip, TOUCH_LINGER_MS); }
        else closeOwnerTip();
    });
    listen('pointercancel', event => closeFor(ownerOf(event.target)));
    // Moving or resizing the page puts the tooltip in the wrong place, so it goes (a pending one is dropped too).
    if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
        window.addEventListener('resize', () => { if (owner) closeOwnerTip(); });
        window.addEventListener('scroll', () => { if (owner) closeOwnerTip(); }, true);
    }
    // Owners that are already on the page, and every one Blazor adds later, are prepared by one observer for the whole page.
    if (typeof document.querySelectorAll === 'function') for (const found of document.querySelectorAll(OWNER)) prepareOwner(found);
    const root = document.documentElement;
    if (typeof MutationObserver === 'function' && root && typeof root.nodeType === 'number') {
        new MutationObserver(records => {
            // Blazor inserts a parent and then its children, each as its own record: a node already inside a scanned subtree is not scanned again.
            const scanned = new Set();
            for (const record of records) for (const added of record.addedNodes ?? []) {
                if (added?.nodeType !== 1) continue;
                let covered = false;
                for (const scannedRoot of scanned) if (scannedRoot.contains?.(added)) { covered = true; break; }
                if (covered) continue;
                scanned.add(added);
                prepareWithin(added);
            }
            dropDetachedPendingOwner();
        }).observe(root, { childList: true, subtree: true });
    }
}
