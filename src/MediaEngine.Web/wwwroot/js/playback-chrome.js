// Presentation only: this module never loads, seeks, pauses or owns a media source.
const states = new WeakMap();

export function attach(host, video) {
    if (!host || !video || states.has(host)) return;
    const state = { host, video, timer: null, holds: new Set(), controls: new Map(), hidden: false, listeners: [], disposed: false };
    const listen = (target, name, handler, options) => {
        target.addEventListener(name, handler, options);
        state.listeners.push(() => target.removeEventListener(name, handler, options));
    };
    const controls = () => {
        for (const [element, original] of state.controls) {
            if (!host.contains(element)) { element.inert = original; state.controls.delete(element); }
        }
        for (const element of host.querySelectorAll('[data-playback-chrome]')) {
            if (!state.controls.has(element)) state.controls.set(element, element.inert);
        }
        return state.controls.keys();
    };
    const hidden = value => {
        state.hidden = value;
        host.classList.toggle('playback-chrome-hidden', value);
        for (const element of controls()) element.inert = value || state.controls.get(element);
    };
    const held = () => state.holds.size > 0 || video.paused || video.ended || video.readyState < 2
        || !!video.error || host.querySelector('[data-chrome-hold]:hover') !== null
        || host.querySelector('[data-playback-chrome]:focus-within') !== null;
    const schedule = () => {
        clearTimeout(state.timer);
        if (held()) { hidden(false); return; }
        state.timer = setTimeout(() => { if (!held()) hidden(true); }, 3000);
    };
    const reveal = () => { hidden(false); schedule(); };
    const onKey = event => {
        if (event.target.closest?.('input,textarea,select,[contenteditable="true"],[role="combobox"],[role="listbox"]')) return;
        if (!host.contains(event.target) && document.fullscreenElement !== host
            && event.target !== document.body && event.target !== document.documentElement) return;
        reveal();
        if (event.key === 'Escape' && !event.defaultPrevented && document.fullscreenElement === host) {
            // The popover's capture listener consumes Escape before this bubble listener.
            document.exitFullscreen?.();
        }
    };
    listen(host, 'pointermove', reveal, { passive: true });
    listen(host, 'pointerdown', reveal, { passive: true });
    listen(host, 'pointerenter', reveal, { passive: true });
    listen(host, 'pointerleave', schedule, { passive: true });
    listen(host, 'focusin', reveal);
    listen(host, 'focusout', () => queueMicrotask(schedule));
    listen(document, 'keydown', onKey);
    for (const name of ['play', 'playing', 'pause', 'ended', 'loadeddata', 'canplay', 'error', 'emptied']) listen(video, name, reveal);
    listen(video, 'waiting', () => { state.holds.add('native-loading'); reveal(); });
    listen(video, 'stalled', () => { state.holds.add('native-loading'); reveal(); });
    listen(video, 'playing', () => { state.holds.delete('native-loading'); reveal(); });
    listen(video, 'canplay', () => { state.holds.delete('native-loading'); reveal(); });
    const measure = () => {
        const bottom = host.querySelector('[data-playback-chrome-bottom]');
        if (state.observedBottom !== bottom) {
            if (state.observedBottom) state.resizeObserver?.unobserve(state.observedBottom);
            if (bottom) state.resizeObserver?.observe(bottom);
            state.observedBottom = bottom;
        }
        host.style.setProperty('--playback-chrome-height', `${bottom?.getBoundingClientRect().height || 0}px`);
    };
    state.resizeObserver = new ResizeObserver(measure);
    state.resizeObserver.observe(host);
    state.mutationObserver = new MutationObserver(() => { hidden(state.hidden); measure(); });
    state.mutationObserver.observe(host, { childList: true, subtree: true });
    state.reveal = reveal; state.schedule = schedule;
    states.set(host, state); measure(); reveal();
}

export function setHold(host, reason, active) {
    const state = states.get(host); if (!state) return;
    if (state.holds.has(reason) === active) return;
    if (active) state.holds.add(reason); else state.holds.delete(reason);
    state.reveal();
}

export function update(host, options = {}) {
    for (const [reason, active] of Object.entries(options)) setHold(host, reason, !!active);
}

export function reveal(host) { states.get(host)?.reveal(); }

export function detach(host) {
    const state = states.get(host); if (!state) return;
    clearTimeout(state.timer); state.listeners.forEach(remove => remove());
    state.resizeObserver.disconnect(); state.mutationObserver.disconnect();
    for (const [element, originalInert] of state.controls) element.inert = originalInert;
    host.classList.remove('playback-chrome-hidden'); host.style.removeProperty('--playback-chrome-height');
    states.delete(host);
}
