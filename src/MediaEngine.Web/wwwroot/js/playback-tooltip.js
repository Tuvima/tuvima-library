const states = new WeakMap();
let nextId = 0;
let lastHiddenAt = 0;
const HOVER_DELAY_MS = 150, WARM_WINDOW_MS = 300, TOUCH_PRESS_MS = 500, TOUCH_LINGER_MS = 1500;

// A tooltip lives in the browser's top layer (popover="manual" + showPopover()), inside the open modal dialog when there is one, so a
// dialog or its backdrop can never cover it (F9). Without a dialog it goes in the fullscreen element, else the body.
function tooltipHost() {
    const dialogs = [...document.querySelectorAll('dialog[open]')].filter(dialog => dialog.matches(':modal'));
    return dialogs.at(-1) || document.fullscreenElement || document.body;
}

export function attach(root, options) {
    if (!root || states.has(root)) return;
    let timer, tooltip, target, description;
    const candidate = root.querySelector('button, a[href], input, [role="combobox"], [tabindex="0"]') || root.closest('button, a[href], [tabindex="0"]');
    const originalTab = root.getAttribute('tabindex');
    const wrapperTarget = !options?.hoverOnly && (!candidate || candidate.disabled);
    if (wrapperTarget) { root.setAttribute('tabindex', '0'); root.dataset.tooltipWrapperTarget = 'true'; }
    const hide = () => {
        clearTimeout(timer);
        if (target && tooltip) {
            if (description === null) target.removeAttribute('aria-describedby');
            else target.setAttribute('aria-describedby', description);
        }
        if (tooltip) lastHiddenAt = Date.now();
        tooltip?.remove(); tooltip = null; target = null;
    };
    const show = () => {
        hide();
        target = wrapperTarget ? root : (candidate || root);
        if (!target || root.dataset.playbackTooltipSuppressed === 'true'
            || target.getAttribute('aria-expanded') === 'true' || !root.dataset.playbackTooltip) return;
        tooltip = document.createElement('span');
        tooltip.id = `playback-tooltip-${++nextId}`;
        tooltip.className = 'playback-tooltip'; tooltip.role = 'tooltip';
        tooltip.textContent = root.dataset.playbackTooltip;
        tooltip.setAttribute('popover', 'manual');
        // The browser's own popover box (centred, inset 0, scrolling) is replaced by the plain fixed box the position below is worked out for.
        tooltip.style.inset = 'auto'; tooltip.style.margin = '0'; tooltip.style.overflow = 'visible';
        tooltipHost().append(tooltip);
        if (typeof tooltip.showPopover === 'function') tooltip.showPopover();
        description = target.getAttribute('aria-describedby');
        target.setAttribute('aria-describedby', [description, tooltip.id].filter(Boolean).join(' '));
        const box = target.getBoundingClientRect(), tip = tooltip.getBoundingClientRect();
        const viewport = window.visualViewport;
        const left = viewport?.offsetLeft || 0, top = viewport?.offsetTop || 0;
        const width = viewport?.width || innerWidth, height = viewport?.height || innerHeight;
        tooltip.style.left = `${Math.max(left + 8, Math.min(box.left + box.width / 2 - tip.width / 2, left + width - tip.width - 8))}px`;
        const preferred = root.dataset.playbackTooltipPlacement?.startsWith('bottom') ? box.bottom + 8 : box.top - tip.height - 8;
        tooltip.style.top = `${Math.max(top + 8, Math.min(preferred < top + 8 ? box.bottom + 8 : preferred, top + height - tip.height - 8))}px`;
    };
    const enter = event => { if (event.pointerType === 'touch') return; timer = setTimeout(show, Date.now() - lastHiddenAt < WARM_WINDOW_MS ? 0 : HOVER_DELAY_MS); };
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
