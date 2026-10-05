let owner;
const actions = ['play', 'pause', 'previoustrack', 'nexttrack', 'seekbackward', 'seekforward', 'seekto'];
export function attach(element, receiver) {
    if (!element || !navigator.mediaSession || owner?.element === element && owner?.receiver === receiver) return;
    if (owner) { owner.dispose(); owner = null; }
    const session = navigator.mediaSession;
    const position = value => { try { session.setPositionState?.(value); } catch { /* Position reporting is an optional browser capability. */ } };
    const update = () => {
        if (!owner?.active) { session.playbackState = 'none'; position(); return; }
        session.playbackState = element.paused ? 'paused' : 'playing';
        if (!session.setPositionState) return;
        if (Number.isFinite(element.duration) && element.duration > 0 && Number.isFinite(element.currentTime)) {
            position({ duration: element.duration, position: Math.max(0, Math.min(element.duration, element.currentTime)), playbackRate: Number.isFinite(element.playbackRate) && element.playbackRate > 0 ? element.playbackRate : 1 });
        } else position();
    };
    const events = ['play', 'pause', 'loadedmetadata', 'durationchange', 'seeked', 'ratechange'];
    for (const event of events) element.addEventListener(event, update);
    let lastPosition = 0;
    const time = () => { if (performance.now() - lastPosition > 1000) { lastPosition = performance.now(); update(); } };
    element.addEventListener('timeupdate', time);
    for (const action of actions) {
        try { session.setActionHandler(action, details => receiver.invokeMethodAsync('MediaSessionActionAsync', action,
            details.seekTime ?? details.seekOffset ?? null).catch(() => {})); } catch { /* Each action is independently optional. */ }
    }
    owner = { element, receiver, session, update, active:false, dispose() {
        for (const event of events) element.removeEventListener(event, update);
        element.removeEventListener('timeupdate', time);
        for (const action of actions) { try { session.setActionHandler(action, null); } catch {} }
        session.metadata = null; session.playbackState = 'none'; position();
    }};
}
export function metadata(value) {
    if (!owner) return;
    owner.active = Boolean(value);
    owner.session.metadata = value && typeof MediaMetadata !== 'undefined' ? new MediaMetadata(value) : null;
    owner.update();
}
export function detach(element) {
    if (owner?.element !== element) return;
    owner.dispose(); owner = null;
}
