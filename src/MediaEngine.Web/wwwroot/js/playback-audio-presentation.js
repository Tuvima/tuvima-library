// Audio composition has its own breakpoint, independent of navigation density.
const observers = new Map();
export function attach(id, owner) {
    if (!id || !owner || observers.has(id)) return;
    const query = window.matchMedia('(max-width:720px)');
    const changed = () => owner.invokeMethodAsync('SetAudioPresentationViewport', window.innerWidth).catch(() => {});
    query.addEventListener('change', changed);
    observers.set(id, { query, changed });
    changed();
}
export function detach(id) {
    const state = observers.get(id);
    if (!state) return;
    state.query.removeEventListener('change', state.changed);
    observers.delete(id);
}
