const states = new WeakMap();
export function formatTime(value) {
    const s = Math.floor(Math.max(0, Number.isFinite(value) ? value : 0));
    return s >= 3600 ? `${Math.floor(s / 3600)}:${String(Math.floor(s / 60) % 60).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}` : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}
export function pointerSeconds(x, rect, duration) { return rect.width > 0 ? Math.max(0, Math.min(1, (x - rect.left) / rect.width)) * Math.max(0, duration) : 0; }
export function attach(root) {
    if (!root || states.has(root)) return;
    const track = root.querySelector('.playback-seek-rail__track'), input = root.querySelector('input[type="range"]'), bubble = root.querySelector('[data-seek-time]');
    if (!input || !track || !bubble) return;
    let dragging = false, keyboard = false;
    const listeners = [], listen = (target, name, fn) => { target.addEventListener(name, fn); listeners.push(() => target.removeEventListener(name, fn)); };
    const hide = () => bubble.classList.remove('is-visible');
    const paint = (seconds, fraction) => {
        bubble.textContent = formatTime(seconds);
        let chapters = []; try { chapters = JSON.parse(root.dataset.chapters || '[]'); } catch (_) { }
        const chapter = chapters.filter(c => c.time <= seconds).sort((a,b) => b.time - a.time)[0];
        if (chapter?.title) bubble.textContent += ` · ${chapter.title}`;
        const width = track.getBoundingClientRect().width;
        bubble.style.left = `${Math.max(24, Math.min(Math.max(24, width - 24), width * fraction))}px`;
        bubble.classList.add('is-visible');
    };
    const thumb = () => { const duration = Number(root.dataset.duration), seconds = Number(input.value); paint(seconds, duration > 0 ? seconds / duration : 0); };
    listen(track, 'pointermove', e => {
        if (dragging || keyboard) { thumb(); return; }
        if (e.pointerType === 'touch' || window.matchMedia?.('(hover: none)').matches) return;
        const duration = Number(root.dataset.duration); if (!(duration > 0)) return;
        const seconds = pointerSeconds(e.clientX, input.getBoundingClientRect(), duration); paint(seconds, seconds / duration);
    });
    listen(input, 'pointerdown', () => { dragging = true; keyboard = false; thumb(); });
    listen(input, 'input', thumb);
    listen(window, 'pointerup', () => { if (!dragging) return; dragging = false; if (!keyboard) hide(); });
    listen(window, 'pointercancel', () => { dragging = false; hide(); });
    listen(track, 'pointerleave', () => { if (!dragging && !keyboard) hide(); });
    listen(input, 'keydown', () => { keyboard = true; thumb(); });
    listen(input, 'focus', () => { if (input.matches(':focus-visible')) { keyboard = true; thumb(); } });
    listen(input, 'blur', () => { keyboard = false; if (!dragging) hide(); });
    states.set(root, listeners);
}
export function detach(root) { states.get(root)?.forEach(remove => remove()); states.delete(root); }
