// One browser media session is shared by the Listen audio element and the video player. Each owns its element and
// receiver; the video owner takes over the lock screen while it has metadata and hands it back when it clears.
const actions = ['play', 'pause', 'previoustrack', 'nexttrack', 'seekbackward', 'seekforward', 'seekto'];
const owners = { audio: null, video: null };
let installed = null;
const controller = () => (owners.video?.active ? owners.video : owners.audio);
const position = (session, value) => { try { session.setPositionState?.(value); } catch { /* Position reporting is an optional browser capability. */ } };

// Gives the session to `next`: its action handlers, its metadata and its playback state. Only the actions the metadata lists get a handler
// (unified player v2 D27: music has previous/next track and no skip buttons; video offers next only when an episode follows), and a skip never
// carries the browser's default 10 s offset: the host uses the profile's own skip seconds.
function install(next) {
    const previous = installed;
    installed = next ?? null;
    const session = (next ?? previous)?.session;
    if (!session) return;
    const allowed = next?.active && Array.isArray(next.value?.actions) ? next.value.actions : null;
    for (const action of actions) {
        try {
            const wanted = next && (allowed === null || allowed.includes(action));
            session.setActionHandler(action, wanted ? details => next.receiver.invokeMethodAsync('MediaSessionActionAsync', action,
                action === 'seekto' ? (details.seekTime ?? null) : null).catch(() => {}) : null);
        } catch { /* Each action is independently optional. */ }
    }
    session.metadata = next?.active && next.value && typeof MediaMetadata !== 'undefined' ? new MediaMetadata(mediaMetadata(next.value)) : null;
    if (next) next.update();
    else { session.playbackState = 'none'; position(session); }
}

// MediaMetadata takes title, artist, album and artwork; the other fields of the shared builder (actions, skip lengths) stay with the page.
const mediaMetadata = value => ({ title: value.title ?? '', artist: value.artist ?? '', album: value.album ?? '', artwork: value.artwork ?? [] });

function create(kind, element, receiver) {
    const session = navigator.mediaSession;
    const owner = { kind, element, receiver, session, active: false, value: null };
    owner.update = () => {
        if (installed !== owner) return;
        if (!owner.active) { session.playbackState = 'none'; position(session); return; }
        session.playbackState = element.paused ? 'paused' : 'playing';
        if (!session.setPositionState) return;
        if (Number.isFinite(element.duration) && element.duration > 0 && Number.isFinite(element.currentTime)) {
            position(session, { duration: element.duration, position: Math.max(0, Math.min(element.duration, element.currentTime)), playbackRate: Number.isFinite(element.playbackRate) && element.playbackRate > 0 ? element.playbackRate : 1 });
        } else position(session);
    };
    const events = ['play', 'pause', 'loadedmetadata', 'durationchange', 'seeked', 'ratechange'];
    for (const event of events) element.addEventListener(event, owner.update);
    let lastPosition = 0;
    const time = () => { if (performance.now() - lastPosition > 1000) { lastPosition = performance.now(); owner.update(); } };
    element.addEventListener('timeupdate', time);
    owner.dispose = () => {
        for (const event of events) element.removeEventListener(event, owner.update);
        element.removeEventListener('timeupdate', time);
        if (owners[kind] === owner) owners[kind] = null;
        if (installed === owner) install(controller());
    };
    owners[kind] = owner;
    // The first owner installs its handlers straight away, as before; a second owner only takes over once it has metadata.
    if (!installed) install(controller());
    return owner;
}

function attachOwner(kind, element, receiver) {
    const current = owners[kind];
    if (!element || !navigator.mediaSession) return;
    // A component that attaches on every render hands over a fresh receiver proxy each time; keep the owner and use the newest.
    if (current?.element === element) { current.receiver = receiver; return; }
    current?.dispose();
    create(kind, element, receiver);
}

function setMetadata(kind, value) {
    const owner = owners[kind];
    if (!owner) return;
    owner.active = Boolean(value);
    owner.value = value ?? null;
    const next = controller();
    if (next === owner || installed !== next) install(next);
}

function detachOwner(kind, element) {
    if (owners[kind]?.element !== element) return;
    owners[kind].dispose();
}

export function attach(element, receiver) { attachOwner('audio', element, receiver); }
export function metadata(value) { setMetadata('audio', value); }
export function detach(element) { detachOwner('audio', element); }
export function attachVideo(element, receiver) { attachOwner('video', element, receiver); }
export function videoMetadata(value) { setMetadata('video', value); }
export function detachVideo(element) { detachOwner('video', element); }
