// Touch presentation for the video player: double-tap seek, the rotate hint and fullscreen on rotation.
// It never loads, seeks or pauses media itself; every action goes back to the video host through the owner reference,
// which uses the same seek and play paths as the visible controls.
export const DOUBLE_TAP_MS = 300;
export const DOUBLE_TAP_PX = 24;
export const TAP_SLOP_PX = 12;
export const TAP_MAX_MS = 500;
export const SEEK_SECONDS = 10;
export const SEEK_RUN_MS = 700;
export const HINT_VISIBLE_MS = 8000;
export const HINT_STORAGE_KEY = 'tuvima.video.rotate-hint-seen';

// Left third seeks back, right third seeks forward, the middle third toggles play.
export function regionFor(x, left, width) {
    if (!(width > 0)) return 'center';
    const ratio = (x - left) / width;
    return ratio < 1 / 3 ? 'left' : ratio > 2 / 3 ? 'right' : 'center';
}

export function formatSeek(total) {
    return `${total < 0 ? '−' : '+'}${Math.abs(total)} s`;
}

export function describeSeek(total) {
    return `Skipped ${total < 0 ? 'back' : 'forward'} ${Math.abs(total)} seconds`;
}

// Decides what each tap means. A second tap within 300 ms and 24 px is a double tap; further taps on the same side
// within 700 ms keep seeking, like the native players, so the count builds up.
export function createTapTracker() {
    let last = null;
    let run = null;
    return {
        reset() { last = null; run = null; },
        tap(time, x, y, rect) {
            const side = regionFor(x, rect.left, rect.width);
            if (run && time <= run.until && side === run.side && side !== 'center') {
                run.total += run.delta;
                run.until = time + SEEK_RUN_MS;
                last = null;
                return { kind: 'seek', side, delta: run.delta, total: run.total };
            }
            run = null;
            if (last && time - last.time <= DOUBLE_TAP_MS && Math.hypot(x - last.x, y - last.y) <= DOUBLE_TAP_PX) {
                last = null;
                if (side === 'center') return { kind: 'toggle' };
                const delta = side === 'left' ? -SEEK_SECONDS : SEEK_SECONDS;
                run = { side, delta, total: delta, until: time + SEEK_RUN_MS };
                return { kind: 'seek', side, delta, total: delta };
            }
            last = { time, x, y };
            return { kind: 'single' };
        },
    };
}

export function isPhoneDevice(win = window) {
    const touch = win.matchMedia?.('(hover: none), (pointer: coarse)').matches ?? false;
    const shortSide = Math.min(win.screen?.width || win.innerWidth, win.screen?.height || win.innerHeight);
    return touch && shortSide < 600;
}

const isPortrait = () => window.matchMedia?.('(orientation: portrait)').matches ?? window.innerHeight >= window.innerWidth;
const states = new WeakMap();
let hintShownThisSession = false;

function readHintSeen() {
    try { return window.localStorage.getItem(HINT_STORAGE_KEY) === '1'; }
    catch { /* Storage can be blocked; the in-memory flag still keeps the hint to once per session. */ return false; }
}

function writeHintSeen() {
    try { window.localStorage.setItem(HINT_STORAGE_KEY, '1'); }
    catch { /* Storage can be blocked; the in-memory flag still keeps the hint to once per session. */ }
}

export function attach(host, video, owner) {
    if (!host || !video || states.has(host)) return;
    const state = { host, video, owner, tracker: createTapTracker(), listeners: [], down: null, touchUntil: 0,
        feedbackTimer: null, hintTimer: null, autoFullscreen: false, pendingFullscreen: false };
    const listen = (target, name, handler, options) => {
        target.addEventListener(name, handler, options);
        state.listeners.push(() => target.removeEventListener(name, handler, options));
    };
    const report = (method, ...args) => { try { owner.invokeMethodAsync(method, ...args)?.catch(() => {}); } catch { /* The host may already be disposed. */ } };
    const feedback = () => host.querySelector('[data-video-touch-feedback]');
    const announcer = () => host.querySelector('[data-video-touch-announce]');
    const hint = () => host.querySelector('[data-video-touch-hint]');

    const showSeekFeedback = (side, total) => {
        const target = feedback(); if (!target) return;
        const ripple = document.createElement('span');
        ripple.className = `video-touch-ripple video-touch-ripple--${side}`;
        const label = document.createElement('span');
        label.className = 'video-touch-ripple__label';
        label.textContent = formatSeek(total);
        ripple.append(label);
        target.replaceChildren(ripple);
        clearTimeout(state.feedbackTimer);
        state.feedbackTimer = setTimeout(() => target.replaceChildren(), SEEK_RUN_MS);
        const live = announcer(); if (live) live.textContent = describeSeek(total);
    };

    const hideHint = () => {
        clearTimeout(state.hintTimer);
        const element = hint(); if (element) element.hidden = true;
    };
    const maybeShowHint = () => {
        const element = hint();
        if (!element || hintShownThisSession || !isPhoneDevice() || !isPortrait() || readHintSeen()) return;
        hintShownThisSession = true;
        writeHintSeen();
        element.hidden = false;
        state.hintTimer = setTimeout(hideHint, HINT_VISIBLE_MS);
    };

    const fullscreenElement = () => document.fullscreenElement || document.webkitFullscreenElement || null;
    const enterFullscreen = async () => {
        try {
            if (typeof host.requestFullscreen === 'function') await host.requestFullscreen();
            else if (typeof host.webkitRequestFullscreen === 'function') host.webkitRequestFullscreen();
            else { console.debug('Fullscreen on rotation is not supported by this browser.'); return false; }
            state.autoFullscreen = true;
            return true;
        } catch (error) {
            console.debug('Fullscreen on rotation was refused by the browser.', error);
            return false;
        }
    };
    const onOrientation = async () => {
        if (!host.classList.contains('is-expanded') || !isPhoneDevice()) return;
        if (!isPortrait()) {
            hideHint();
            if (fullscreenElement()) return;
            // Most browsers only allow fullscreen right after a tap; if this attempt is refused, the next tap completes it.
            const entered = await enterFullscreen();
            state.pendingFullscreen = !entered && !fullscreenElement();
        } else if (state.autoFullscreen && fullscreenElement() === host) {
            state.autoFullscreen = false; state.pendingFullscreen = false;
            try { await document.exitFullscreen?.(); }
            catch (error) { console.debug('Leaving fullscreen after rotating back was refused by the browser.', error); }
        } else { state.autoFullscreen = false; state.pendingFullscreen = false; }
    };
    listen(window, 'orientationchange', onOrientation);
    if (screen.orientation?.addEventListener) listen(screen.orientation, 'change', onOrientation);
    listen(document, 'fullscreenchange', () => {
        if (fullscreenElement()) state.pendingFullscreen = false;
        else state.autoFullscreen = false;
    });

    listen(video, 'play', maybeShowHint);
    listen(host, 'click', event => {
        if (event.target.closest?.('[data-video-touch-hint-dismiss]')) hideHint();
    });

    // Taps on the bare video surface: the shade and chrome only receive pointers while visible.
    listen(host, 'pointerdown', event => {
        if (event.pointerType !== 'touch') state.touchUntil = 0;
        if (event.pointerType !== 'touch' || !event.isPrimary) return;
        state.down = event.target === video ? { time: performance.now(), x: event.clientX, y: event.clientY } : null;
    }, { passive: true });
    listen(host, 'pointercancel', () => { state.down = null; }, { passive: true });
    listen(host, 'pointerup', event => {
        const down = state.down; state.down = null;
        if (event.pointerType !== 'touch' || !event.isPrimary || !down || event.target !== video) return;
        const now = performance.now();
        state.touchUntil = now + 800;
        if (state.pendingFullscreen && !isPortrait() && !fullscreenElement()) { state.pendingFullscreen = false; enterFullscreen(); }
        if (now - down.time > TAP_MAX_MS || Math.hypot(event.clientX - down.x, event.clientY - down.y) > TAP_SLOP_PX) { state.tracker.reset(); return; }
        const action = state.tracker.tap(now, event.clientX, event.clientY, video.getBoundingClientRect());
        if (action.kind === 'seek') {
            showSeekFeedback(action.side, action.total);
            // The run's total is applied to where the run started, so quick taps never depend on a round trip.
            if (Math.abs(action.total) === SEEK_SECONDS) state.seekBase = video.currentTime;
            const target = Math.max(0, Math.min(Number.isFinite(video.duration) ? video.duration : Infinity, (state.seekBase ?? video.currentTime) + action.total));
            report('HandleTouchSeek', video.dataset.playbackAssetId ?? '', Number(video.dataset.playbackRequestVersion) || 0, target);
        } else if (action.kind === 'toggle') report('HandleTouchTogglePlay');
    }, { passive: true });
    // The page-level click that follows a touch tap would toggle play on every single tap, so touch taps are
    // handled here instead. Mouse and keyboard clicks reach the video host untouched.
    listen(host, 'click', event => {
        if (event.target === video && performance.now() <= state.touchUntil) event.stopPropagation();
    }, true);

    states.set(host, state);
}

export function detach(host) {
    const state = states.get(host); if (!state) return;
    clearTimeout(state.feedbackTimer); clearTimeout(state.hintTimer);
    state.listeners.forEach(remove => remove());
    states.delete(host);
}
