// Presentation only: this module never loads, seeks, pauses or owns a media source.
const states = new WeakMap();
export function cueLine(railTop, videoRect) { return Math.max(0, Math.min(95, (railTop - videoRect.top - 12) / Math.max(1, videoRect.height) * 100)); }

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
    const originals = new Map();
    const positionCues = () => {
        const rail = host.querySelector('[data-playback-seek-rail]');
        for (const track of Array.from(video.textTracks || [])) {
            if (track.mode !== 'showing') continue;
            for (const cue of Array.from(track.activeCues || [])) {
                if (!originals.has(cue)) originals.set(cue, { line:cue.line, snapToLines:cue.snapToLines, lineAlign:cue.lineAlign });
                if (!state.hidden && rail) {
                    // Native text tracks use the video viewport, including object-fit letterboxing.
                    const frame = video.getBoundingClientRect(), railTop = rail.getBoundingClientRect().top;
                    if (originals.get(cue).lineAlign !== undefined) {
                        cue.snapToLines = false; cue.lineAlign = 'end'; cue.line = cueLine(railTop, frame);
                    } else {
                        // Chromium versions without native lineAlign ignore an expando assignment.
                        // Its native cue line box retains a 5%-of-frame font even with smaller ::cue text.
                        const font = typeof getComputedStyle === 'function' ? parseFloat(getComputedStyle(video).fontSize) : 18;
                        const lineHeight = Math.max((font || 18) * 1.3, frame.height * .05 * 1.3);
                        cue.snapToLines = false;
                        const columns = Math.max(1, frame.width * (cue.size || 100) / 100 / ((font || 18) * .6));
                        const rows = String(cue.text || '').replace(/<[^>]*>/g, '').split('\n')
                            .reduce((count, line) => count + Math.max(1, Math.ceil(line.length / columns)), 0);
                        cue.line = cueLine(railTop - rows * lineHeight, frame);
                    }
                }
                else Object.assign(cue, originals.get(cue));
            }
        }
        if (state.hidden) { for (const [cue, original] of originals) Object.assign(cue, original); originals.clear(); }
    };
    const tracked = new Set();
    const bindTracks = () => { for (const track of Array.from(video.textTracks || [])) { if (!tracked.has(track)) { tracked.add(track); listen(track, 'cuechange', positionCues); } } positionCues(); };
    if (video.textTracks?.addEventListener) { listen(video.textTracks, 'addtrack', bindTracks); listen(video.textTracks, 'change', bindTracks); }
    state.restoreCues = () => { for (const [cue, original] of originals) Object.assign(cue, original); originals.clear(); };
    const hidden = value => {
        state.hidden = value;
        host.classList.toggle('playback-chrome-hidden', value);
        for (const element of controls()) element.inert = value || state.controls.get(element);
        positionCues();
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
    listen(host, 'pointerdown', event => {
        if (event.pointerType === 'touch' && !event.target.closest?.('button,input,select,[role="slider"]')) {
            if (state.hidden) reveal(); else if (!held()) { clearTimeout(state.timer); hidden(true); }
        } else reveal();
    }, { passive: true });
    listen(host, 'pointerenter', reveal, { passive: true });
    listen(host, 'pointerleave', schedule, { passive: true });
    listen(host, 'focusin', reveal);
    listen(host, 'focusout', () => queueMicrotask(schedule));
    listen(document, 'keydown', onKey);
    for (const name of ['play', 'playing', 'pause', 'ended', 'loadeddata', 'loadedmetadata', 'resize', 'canplay', 'error', 'emptied']) listen(video, name, reveal);
    listen(video, 'waiting', () => { state.holds.add('native-loading'); reveal(); });
    listen(video, 'stalled', () => { state.holds.add('native-loading'); reveal(); });
    listen(video, 'playing', () => { state.holds.delete('native-loading'); reveal(); });
    listen(video, 'canplay', () => { state.holds.delete('native-loading'); reveal(); });
    state.lastMediaTime = video.currentTime;
    listen(video, 'timeupdate', () => {
        // Managed tracks can finish loading after chrome and track inventory bind.
        // Refresh while visible even when that browser emits no cuechange for the load.
        bindTracks();
        const advancing = video.currentTime > state.lastMediaTime;
        state.lastMediaTime = video.currentTime;
        if (advancing && !video.paused && state.holds.delete('native-loading')) reveal();
    });
    const measure = () => {
        const bottom = host.querySelector('[data-playback-chrome-bottom]');
        const header = host.querySelector('[data-playback-chrome="header"]');
        if (state.observedHeader !== header) {
            if (state.observedHeader) state.resizeObserver?.unobserve(state.observedHeader);
            if (header) state.resizeObserver?.observe(header);
            state.observedHeader = header;
        }
        if (state.observedBottom !== bottom) {
            if (state.observedBottom) state.resizeObserver?.unobserve(state.observedBottom);
            if (bottom) state.resizeObserver?.observe(bottom);
            state.observedBottom = bottom;
        }
        host.style.setProperty('--playback-chrome-height', `${bottom?.getBoundingClientRect().height || 0}px`);
        host.style.setProperty('--playback-chrome-header-height', `${header?.getBoundingClientRect().height || 0}px`);
        bindTracks();
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
    clearTimeout(state.timer); state.restoreCues(); state.listeners.forEach(remove => remove());
    state.resizeObserver.disconnect(); state.mutationObserver.disconnect();
    for (const [element, originalInert] of state.controls) element.inert = originalInert;
    host.classList.remove('playback-chrome-hidden'); host.style.removeProperty('--playback-chrome-height');
    states.delete(host);
}
