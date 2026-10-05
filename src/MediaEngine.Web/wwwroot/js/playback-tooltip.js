const states = new WeakMap();
let nextId = 0;

export function attach(root) {
    if (!root || states.has(root)) return;
    let timer, tooltip, target, description;
    const candidate = root.querySelector('button, a[href], input, [role="combobox"], [tabindex="0"]') || root.closest('button, a[href], [tabindex="0"]');
    const originalTab = root.getAttribute('tabindex');
    const wrapperTarget = !candidate || candidate.disabled;
    if (wrapperTarget) { root.setAttribute('tabindex', '0'); root.dataset.tooltipWrapperTarget = 'true'; }
    const hide = () => {
        clearTimeout(timer);
        if (target && tooltip) {
            if (description === null) target.removeAttribute('aria-describedby');
            else target.setAttribute('aria-describedby', description);
        }
        tooltip?.remove(); tooltip = null; target = null;
    };
    const show = () => {
        hide();
        target = wrapperTarget ? root : candidate;
        if (!target || root.dataset.playbackTooltipSuppressed === 'true'
            || target.getAttribute('aria-expanded') === 'true' || !root.dataset.playbackTooltip) return;
        tooltip = document.createElement('span');
        tooltip.id = `playback-tooltip-${++nextId}`;
        tooltip.className = 'playback-tooltip'; tooltip.role = 'tooltip';
        tooltip.textContent = root.dataset.playbackTooltip;
        (document.fullscreenElement || document.body).append(tooltip);
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
    const enter = event => { if (event.pointerType !== 'touch') timer = setTimeout(show, 400); };
    const focus = event => { if (event.target.matches(':focus-visible')) show(); };
    const key = event => { if (event.key === 'Escape') hide(); };
    const down = event => { hide(); if (event.pointerType === 'touch') timer = setTimeout(show, 600); };
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
    root.addEventListener('pointerup', hide);
    root.addEventListener('pointercancel', hide);
    root.addEventListener('keydown', key);
    window.addEventListener('resize', hide);
    window.addEventListener('scroll', hide, true);
    states.set(root, { dispose() {
        hide(); observer.disconnect();
        root.removeEventListener('pointerenter', enter); root.removeEventListener('pointerleave', hide);
        root.removeEventListener('focusin', focus); root.removeEventListener('focusout', hide);
        root.removeEventListener('pointerdown', down); root.removeEventListener('pointerup', hide); root.removeEventListener('pointercancel', hide); root.removeEventListener('keydown', key);
        if (wrapperTarget) { delete root.dataset.tooltipWrapperTarget; if (originalTab === null) root.removeAttribute('tabindex'); else root.setAttribute('tabindex', originalTab); }
        window.removeEventListener('resize', hide); window.removeEventListener('scroll', hide, true);
    }});
}

export function detach(root) { states.get(root)?.dispose(); states.delete(root); }
