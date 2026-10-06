// Local pointer presentation and actual-playing clock; the controller remains the transport owner.
const states = new WeakMap();
const clock = () => performance.now();
export function attach(host, video, owner) {
    if (!host || !video || states.has(host)) return;
    const state = { host, video, owner, visible: false, stalled: false, binding: null, last: clock(), listeners: [] };
    const listen = (target, name, fn, options) => { target.addEventListener(name, fn, options); state.listeners.push(() => target.removeEventListener(name, fn, options)); };
    const report = (method, ...args) => { try { owner.invokeMethodAsync(method, ...args)?.catch(() => {}); } catch (_) {} };
    const source = () => video.dataset.playbackSource || video.currentSrc || video.src;
    const fullscreen = () => report('HandleFullscreenChanged', document.fullscreenElement === host || document.webkitFullscreenElement === host || !!video.webkitDisplayingFullscreen);
    listen(document, 'fullscreenchange', fullscreen);
    listen(document, 'webkitfullscreenchange', fullscreen);
    listen(video, 'webkitbeginfullscreen', () => report('HandleFullscreenChanged', true));
    listen(video, 'webkitendfullscreen', () => report('HandleFullscreenChanged', false));
    const pictureInPicture = active => {
        const captured = state.binding;
        if (!captured || video.dataset.playbackAssetId !== captured.asset
            || Number(video.dataset.playbackRequestVersion) !== captured.request || source() !== captured.source) return;
        report('HandlePictureInPictureChanged', captured.profile, captured.work, captured.asset, captured.request, active);
    };
    listen(video, 'enterpictureinpicture', () => pictureInPicture(true));
    listen(video, 'leavepictureinpicture', () => pictureInPicture(false));
    listen(video, 'waiting', () => { state.stalled = true; });
    listen(video, 'stalled', () => { state.stalled = true; });
    listen(video, 'playing', () => { state.stalled = false; state.last = clock(); });
    listen(video, 'canplay', () => { state.stalled = false; state.last = clock(); });
    listen(document, 'visibilitychange', () => { state.last = clock(); });
    listen(video, 'ended', () => {
        const captured = state.binding;
        if (!captured || !video.ended || video.dataset.playbackAssetId !== captured.asset
            || Number(video.dataset.playbackRequestVersion) !== captured.request
            || source() !== captured.source || (video._tuvimaHls || null) !== captured.hls) return;
        // Capture immutable loaded-source identity before crossing the asynchronous .NET boundary.
        report('HandleNativeVideoEnded', captured.profile, captured.work, captured.asset, captured.request);
    });
    state.timer = setInterval(() => {
        const now = clock(), elapsed = (now - state.last) / 1000; state.last = now;
        const captured = state.binding;
        if (captured && state.visible && !video.paused && !video.ended && video.readyState >= 2 && !state.stalled && !video.error && !document.hidden)
            report('HandleEndCardTick', captured.profile, captured.work, captured.asset, captured.request, Math.min(1.5, elapsed));
    }, 1000);
    state.source = source;
    states.set(host, state);
}
export function bindSource(host, profile, work, asset, request) {
    const state = states.get(host); if (!state) return;
    if (state.binding?.asset !== asset || state.binding?.request !== Number(request) || state.binding?.profile !== profile) state.last = clock();
    state.binding = { profile, work, asset, request: Number(request), source: state.source(), hls: state.video._tuvimaHls || null };
    state.video._tuvimaVideoBinding = state.binding;
    window.listenPlayback?.synchronizeCaptionSelection?.(state.video);
}
export function update(host, visible) {
    const state = states.get(host); if (!state) return;
    if (state.visible !== visible) state.last = clock();
    state.visible = visible;
}
export function detach(host) {
    const state = states.get(host); if (!state) return;
    clearInterval(state.timer); state.listeners.forEach(remove => remove()); delete state.video._tuvimaVideoBinding; states.delete(host);
}
