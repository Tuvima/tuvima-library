const states = new WeakMap();
let nextId = 0;

export function attach(root) {
    if (!root || states.has(root)) return;
    let timer, tooltip, target, description;
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
        target = root.querySelector('button, [role="combobox"], [tabindex="0"]');
        if (!target || target.disabled || root.dataset.playbackTooltipSuppressed === 'true'
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
        const preferred = box.top - tip.height - 8;
        tooltip.style.top = `${Math.max(top + 8, Math.min(preferred < top + 8 ? box.bottom + 8 : preferred, top + height - tip.height - 8))}px`;
    };
    const enter = event => { if (event.pointerType !== 'touch') timer = setTimeout(show, 400); };
    const focus = event => { if (event.target.matches(':focus-visible')) show(); };
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
    root.addEventListener('pointerdown', hide);
    window.addEventListener('resize', hide);
    window.addEventListener('scroll', hide, true);
    states.set(root, { dispose() {
        hide(); observer.disconnect();
        root.removeEventListener('pointerenter', enter); root.removeEventListener('pointerleave', hide);
        root.removeEventListener('focusin', focus); root.removeEventListener('focusout', hide);
        root.removeEventListener('pointerdown', hide);
        window.removeEventListener('resize', hide); window.removeEventListener('scroll', hide, true);
    }});
}

export function detach(root) { states.get(root)?.dispose(); states.delete(root); }
