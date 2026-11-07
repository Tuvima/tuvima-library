// Play on another device (unified player v2 D28): a thin module over the standard Remote Playback API (`element.remote`).
// The browser draws the device picker (AirPlay in Safari, Google Cast in Chrome and Edge). This module loads no Google script and scans no
// network: it only asks the browser to show its own picker and listens to the connecting / connect / disconnect events.
// The decisions (is the API there, what state follows an event, is a rejected prompt a real failure, how the receiver's source is swapped in
// and back out) are small pure functions, exported so tests/MediaEngine.Web.Tests/JavaScript/playback-remote.test.mjs can run them with a fake
// `remote` object. The DOM work around them is kept thin.

const entries = new WeakMap();

// ---- Pure decisions ----

// The API is there when the element has a `remote` object that can prompt. Firefox has no such object; the row is then disabled.
export function supportOf(element) {
    const remote = element?.remote;
    return remote && typeof remote.prompt === 'function' ? 'supported' : 'unsupported';
}

// The device name is not exposed by the standard API in most browsers. A browser that does expose one is used; otherwise null, and the
// Dashboard says "another device".
export function deviceNameOf(remote) {
    for (const key of ['deviceName', 'name', 'label']) {
        const value = remote?.[key];
        if (typeof value === 'string' && value.trim()) return value.trim();
    }
    return null;
}

// The state the row and the bar follow: local (the browser plays), connecting, or remote (a receiver plays).
export function stateAfter(event, remote) {
    switch (event) {
        case 'connecting': return { state: 'connecting', deviceName: deviceNameOf(remote) };
        case 'connect': return { state: 'remote', deviceName: deviceNameOf(remote) };
        case 'disconnect': return { state: 'local', deviceName: null };
        default: return null;
    }
}

// What `remote.state` says at attach time, mapped the same way (a page reloaded into an existing remote session is rare, but cheap to honor).
export function stateOf(remote) {
    switch (remote?.state) {
        case 'connecting': return stateAfter('connecting', remote);
        case 'connected': return stateAfter('connect', remote);
        default: return stateAfter('disconnect', remote);
    }
}

// A prompt that the person dismissed (NotAllowedError, "user cancelled") or that found no device (NotFoundError) is a normal answer, not a
// failure: nothing is logged and nothing is shown. Anything else is reported to the page as a failure.
export function isQuietPromptRejection(error) {
    return error?.name === 'NotAllowedError' || error?.name === 'NotFoundError' || error?.name === 'AbortError';
}

// What to do to the element so the receiver can fetch the media without the browser's sign-in: point it at the Engine's short-lived cast
// link, remember the source the page had so it can be put back, and remember where playback was. Null when there is nothing to swap.
export function planSourceSwap({ castUrl, playbackSource, currentSrc, currentTime, paused }) {
    if (typeof castUrl !== 'string' || !castUrl.trim()) return null;
    if (currentSrc === castUrl) return null;
    const position = Number.isFinite(currentTime) && currentTime > 0 ? currentTime : 0;
    return { castUrl, restoreSource: playbackSource || currentSrc || '', position, resume: paused === false };
}

// What to do when the receiver goes away: put the page's own source back, at the position the receiver reached, playing again if it was.
export function planRestore(swap, { currentTime } = {}) {
    if (!swap) return null;
    const reached = Number.isFinite(currentTime) && currentTime > 0 ? currentTime : swap.position;
    return { source: swap.restoreSource, position: reached, resume: swap.resume };
}

// ---- The element ----

function describe(entry) {
    const { element } = entry;
    return { support: supportOf(element), availability: entry.availability, ...stateOf(element.remote) };
}

function report(entry, event) {
    const next = stateAfter(event, entry.element.remote);
    if (!next) return;
    if (event === 'connect' && entry.swap?.resume) {
        // The receiver starts from the element's position; keep it playing if it was playing before the picker opened.
        entry.element.play?.().catch(error => console.debug('Playback did not resume on the other device.', error));
    }
    if (event === 'disconnect') {
        // Remote playback was switched off to end the session (see stop); it is switched back on so the next prompt works.
        clearTimeout(entry.stopTimer);
        entry.element.disableRemotePlayback = false;
        restoreSource(entry);
    }
    entry.receiver?.invokeMethodAsync('OnRemoteStateChanged', next.state, next.deviceName)
        .catch(error => console.debug('The page is gone; the remote state was not reported.', error));
}

// Availability is optional: Safari throws NotSupportedError for a disabled element and some browsers never answer. "unknown" keeps the row
// usable (the picker itself says what it finds).
async function watchAvailability(entry) {
    const remote = entry.element.remote;
    try {
        entry.watchId = await remote.watchAvailability(available => {
            entry.availability = available ? 'available' : 'none';
        });
    } catch (error) {
        console.debug('Remote playback availability is unknown in this browser.', error);
        entry.availability = 'unknown';
    }
}

export async function attach(element, receiver) {
    detach(element);
    const entry = { element, receiver, availability: 'unknown', watchId: null, swap: null, listeners: [] };
    entries.set(element, entry);
    if (supportOf(element) === 'supported') {
        for (const event of ['connecting', 'connect', 'disconnect']) {
            const listener = () => report(entry, event);
            element.remote.addEventListener(event, listener);
            entry.listeners.push([event, listener]);
        }
        await watchAvailability(entry);
    }
    return describe(entry);
}

export function detach(element) {
    const entry = entries.get(element);
    if (!entry) return;
    for (const [event, listener] of entry.listeners) element.remote?.removeEventListener(event, listener);
    if (entry.watchId !== null) {
        element.remote?.cancelWatchAvailability?.(entry.watchId)?.catch?.(error => console.debug('Cancelling the availability watch was rejected.', error));
    }
    clearTimeout(entry.stopTimer);
    entries.delete(element);
}

// Points the element at the cast link. The page's own source marker (dataset.playbackSource) is left alone on purpose: the page keeps
// calling ensureAudioSource with the same address and finds nothing to redo, so the swap holds until the receiver goes away.
export function swapSource(element, castUrl) {
    const entry = entries.get(element);
    if (!entry) return false;
    const plan = planSourceSwap({
        castUrl,
        playbackSource: element.dataset?.playbackSource,
        currentSrc: element.getAttribute?.('src'),
        currentTime: element.currentTime,
        paused: element.paused,
    });
    if (!plan) return false;
    // Keep the first record while a swap is already in force (a new track swaps again), so the page's own source is never forgotten.
    entry.swap = entry.swap ? { ...entry.swap, castUrl: plan.castUrl, position: 0 } : plan;
    if (element._tuvimaHls) {
        element._tuvimaHls.destroy();
        element._tuvimaHls = null;
    }
    element.setAttribute('src', plan.castUrl);
    element.load?.();
    seekWhenReady(element, entry.swap === plan ? plan.position : 0);
    return true;
}

function seekWhenReady(element, position) {
    if (!(position > 0)) return;
    element.addEventListener('loadedmetadata', () => { element.currentTime = position; }, { once: true });
}

function restoreSource(entry) {
    const { element } = entry;
    const swap = entry.swap;
    entry.swap = null;
    const plan = planRestore(swap, { currentTime: element.currentTime });
    if (!plan) return;
    // Clearing the page's marker lets its own source routine (which also re-attaches the HLS player) run again for the original address.
    delete element.dataset.playbackSource;
    if (plan.source) globalThis.listenPlayback?.ensureAudioSource?.(element, plan.source);
    else element.removeAttribute('src');
    element.addEventListener('loadedmetadata', () => {
        if (plan.position > 0) element.currentTime = plan.position;
        if (plan.resume) element.play?.().catch(error => console.debug('Playback did not resume in the browser.', error));
    }, { once: true });
}

// Opens the browser's picker. The cast link is put on the element first (the picker hands the element's source to the receiver). A person
// who closes the picker, or a browser with no device, ends quietly and the page's own source is put back. Returns the outcome only.
export async function openPicker(element, castUrl) {
    const entry = entries.get(element);
    if (!entry || supportOf(element) !== 'supported') return { outcome: 'unsupported' };
    const swapped = castUrl ? swapSource(element, castUrl) : false;
    try {
        await element.remote.prompt();
        return { outcome: 'prompted', swapped };
    } catch (error) {
        if (swapped) restoreSource(entry);
        if (isQuietPromptRejection(error)) {
            // Closing the picker is the normal way to change one's mind; there is nothing to report.
            return { outcome: 'cancelled', reason: error.name };
        }
        console.debug('The device picker could not open.', error);
        return { outcome: 'failed', reason: error?.name ?? 'Error' };
    }
}

// Moves a new item onto the receiver without asking again (the queue moved on while a receiver plays): only the source changes.
export function followItem(element, castUrl) {
    return swapSource(element, castUrl);
}

// There is no standard "disconnect" call. Switching remote playback off for the element ends the session (the specification says it must),
// and the switch is turned back on at once so the next prompt works. The disconnect event restores the page's source.
export function stop(element) {
    const entry = entries.get(element);
    if (!entry || supportOf(element) !== 'supported') return false;
    element.disableRemotePlayback = true;
    // The disconnect event turns it back on; this is the safety net when the browser never sends one.
    clearTimeout(entry.stopTimer);
    entry.stopTimer = setTimeout(() => { element.disableRemotePlayback = false; }, 3000);
    return true;
}

export function read(element) {
    const entry = entries.get(element);
    return entry ? describe(entry) : { support: supportOf(element), availability: 'unknown', state: 'local', deviceName: null };
}

// ---- By element id ----
// The Blazor components do not hold the media element; they name it. Only one of the audio and video elements is on the page at a time.

const byId = id => (typeof document === 'undefined' ? null : document.getElementById(id));
const missing = { support: 'unsupported', availability: 'unknown', state: 'local', deviceName: null };

export function attachTo(id, receiver) {
    const element = byId(id);
    return element ? attach(element, receiver) : Promise.resolve(missing);
}

export function openPickerOn(id, castUrl) {
    const element = byId(id);
    return element ? openPicker(element, castUrl) : Promise.resolve({ outcome: 'unsupported' });
}

export function followOn(id, castUrl) {
    const element = byId(id);
    return element ? followItem(element, castUrl) : false;
}

export function stopOn(id) {
    const element = byId(id);
    return element ? stop(element) : false;
}

export function detachFrom(id) {
    const element = byId(id);
    if (element) detach(element);
}
