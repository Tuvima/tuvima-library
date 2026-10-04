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
export function detach(root) { const state = states.get(root); if (!state) return; state.listeners.forEach(([name, handler]) => state.scroller.removeEventListener(name, handler)); states.delete(root); }
