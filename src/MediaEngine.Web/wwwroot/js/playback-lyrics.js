const states = new WeakMap();
function notify(state) {
    const current = state.scroller.querySelector('[aria-current="true"]');
    const box = current?.getBoundingClientRect(), area = state.scroller.getBoundingClientRect();
    const visible = box && box.bottom > area.top && box.top < area.bottom;
    const needed = !state.following && !!current && !visible;
    if (state.needed !== needed) { state.needed = needed; state.owner.invokeMethodAsync('FollowChanged', needed); }
}
function center(state) {
    const current = state.scroller.querySelector('[aria-current="true"]');
    if (current && state.following) state.scroller.scrollTop = current.offsetTop - state.scroller.offsetTop - state.scroller.clientHeight / 2 + current.clientHeight / 2;
    notify(state);
}
export function attach(root, owner) {
    if (!root || states.has(root)) return;
    const scroller = root.querySelector('.playback-lyrics__lines'); if (!scroller) return;
    const state = { scroller, owner, following:true, needed:false, key:null, index:-1, listeners:[] };
    const listen = (name, handler) => { scroller.addEventListener(name, handler, { passive:true }); state.listeners.push([name, handler]); };
    const manual = () => { state.following = false; notify(state); };
    listen('wheel', manual); listen('touchstart', manual); listen('pointerdown', event => { if (event.target === scroller) manual(); });
    listen('keydown', event => { if (['ArrowUp','ArrowDown','PageUp','PageDown','Home','End',' '].includes(event.key)) manual(); });
    listen('scroll', () => notify(state)); states.set(root, state);
}
export function update(root, index, key) {
    const state = states.get(root); if (!state) return;
    if (state.key !== key) { state.key = key; state.index = -1; state.following = true; }
    if (state.index !== index) { state.index = index; center(state); } else notify(state);
}
export function follow(root) { const state = states.get(root); if (state) { state.following = true; center(state); } }
export function activeIndexAt(times, time) {
    let index = -1, latest = -Infinity;
    times.forEach((stamp, candidate) => { if (stamp !== null && Number.isFinite(stamp) && stamp <= time && stamp >= latest) { index = candidate; latest = stamp; } });
    return index;
}
function paint(state, index) {
    for (const line of state.scroller.querySelectorAll('[data-lyric-index]')) {
        const active = Number(line.dataset.lyricIndex) === index;
        line.classList.toggle('is-active', active);
        if (active) line.setAttribute('aria-current', 'true'); else line.removeAttribute('aria-current');
    }
    if (state.index !== index) { state.index = index; center(state); } else notify(state);
}
function tick(state) {
    if (!state.clock) return;
    const native = state.native;
    const ownedNative = native && native.dataset.currentAssetId === state.clock.asset
        && Number(native.dataset.playbackRequestVersion) === Number(state.clock.request);
    const playing = ownedNative ? !native.paused && !native.ended : state.clock.playing;
    const time = ownedNative ? native.currentTime : state.clock.time + (playing ? (performance.now() - state.received) / 1000 * state.clock.rate : 0);
    paint(state, activeIndexAt(state.times || [], time + state.clock.lead / 1000));
    if (playing) state.frame = requestAnimationFrame(() => tick(state)); else state.frame = 0;
}
export function setTimes(root, times, key) {
    const state = states.get(root); if (!state) return;
    if (state.key !== key) { state.key = key; state.following = true; state.index = -1; }
    state.times = times;
    if (state.clock) { if (state.frame) cancelAnimationFrame(state.frame); tick(state); }
}
export function setClock(root, clock) {
    const state = states.get(root); if (!state) return;
    state.clock = clock; state.received = performance.now();
    if (!state.native) {
        state.native = document.getElementById('listen-audio-engine');
        state.nativeListeners = [];
        for (const name of ['play','pause','seeked','seeking','ratechange','timeupdate']) {
            const handler = () => { if (state.frame) cancelAnimationFrame(state.frame); tick(state); };
            state.native?.addEventListener(name, handler);
            state.nativeListeners.push([name, handler]);
        }
    }
    if (state.frame) cancelAnimationFrame(state.frame);
    tick(state);
}
export function detach(root) { const state = states.get(root); if (!state) return; if (state.frame) cancelAnimationFrame(state.frame); state.nativeListeners?.forEach(([name, handler]) => state.native?.removeEventListener(name, handler)); state.listeners.forEach(([name, handler]) => state.scroller.removeEventListener(name, handler)); states.delete(root); }
