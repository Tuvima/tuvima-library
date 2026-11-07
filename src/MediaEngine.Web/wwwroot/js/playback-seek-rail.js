const states = new WeakMap();
export function formatTime(value) {
    const s = Math.floor(Math.max(0, Number.isFinite(value) ? value : 0));
    return s >= 3600 ? `${Math.floor(s / 3600)}:${String(Math.floor(s / 60) % 60).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}` : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}
/**
 * Where the preview picture for a moment sits (Unified player v2 D9a). Frame n is the picture at n * intervalSeconds; sheets hold
 * columns * rows frames each, row by row. A moment past the last frame shows the last frame. Mirrors the Engine's ScrubSpriteLayout.
 */
export function tileFor(index, seconds) {
    if (!index || !(index.intervalSeconds > 0) || !(index.frameCount > 0) || !Array.isArray(index.sheets) || index.sheets.length === 0) return null;
    const frame = Math.min(index.frameCount - 1, Math.max(0, Math.floor(Math.max(0, Number.isFinite(seconds) ? seconds : 0) / index.intervalSeconds)));
    const perSheet = index.columns * index.rows, inSheet = frame % perSheet, sheet = index.sheets[Math.floor(frame / perSheet)];
    if (!sheet) return null;
    return { sheet, column: inSheet % index.columns, row: Math.floor(inSheet / index.columns) };
}
const indexReads = new Map();
/** Reads a preview index once per address; null when there is none (no previews, no access, or the read failed). */
export function loadIndex(url, fetchImpl) {
    if (!url) return Promise.resolve(null);
    if (!indexReads.has(url)) {
        const read = (fetchImpl ?? globalThis.fetch)?.(url, { credentials: 'same-origin' })
            .then(response => response.ok ? response.json() : null).catch(() => null);
        indexReads.set(url, Promise.resolve(read ?? null));
    }
    return indexReads.get(url);
}
export function resetIndexes() { indexReads.clear(); }
/**
 * How far an arrow key moves the timeline (Unified player v2 D9): 5 seconds, 30 with Shift. Left and Down go back, Right and Up go forward.
 * Null for any other key, so the browser keeps Home, End and the rest.
 */
export function keySeekDelta(key, shiftKey) {
    const step = shiftKey ? 30 : 5;
    if (key === 'ArrowLeft' || key === 'ArrowDown') return -step;
    if (key === 'ArrowRight' || key === 'ArrowUp') return step;
    return null;
}
/** A seek never goes outside the loaded duration. */
export function clampSeek(seconds, duration) {
    const limit = Number.isFinite(duration) && duration > 0 ? duration : 0;
    return Math.max(0, Math.min(limit, Number.isFinite(seconds) ? seconds : 0));
}
export function pointerSeconds(x, rect, duration) { return rect.width > 0 ? Math.max(0, Math.min(1, (x - rect.left) / rect.width)) * Math.max(0, duration) : 0; }
export function attach(root) {
    if (!root || states.has(root)) return;
    const track = root.querySelector('.playback-seek-rail__track'), input = root.querySelector('input[type="range"]'), bubble = root.querySelector('[data-seek-time]');
    if (!input || !track || !bubble) return;
    let dragging = false, keyboard = false;
    const listeners = [], listen = (target, name, fn) => { target.addEventListener(name, fn); listeners.push(() => target.removeEventListener(name, fn)); };
    let preview = null, previewUrl = null;
    const tileElement = () => root.querySelector('[data-scrub-tile]');
    const hide = () => { bubble.classList.remove('is-visible'); tileElement()?.classList.remove('is-visible'); };
    // The index is read once, when scrubbing first needs it; a copy the page already supplied is used as it is.
    const previewIndex = () => {
        const url = root.dataset.scrubIndexUrl;
        if (!url) return null;
        if (url !== previewUrl) {
            previewUrl = url; preview = null;
            let supplied = null; try { supplied = root.dataset.scrubIndex ? JSON.parse(root.dataset.scrubIndex) : null; } catch (_) { /* A malformed supplied copy is ignored on purpose: the index is then fetched from its URL below. */ }
            if (supplied) preview = supplied; else loadIndex(url).then(index => { if (previewUrl === url) { preview = index; } });
        }
        return preview;
    };
    const paintTile = (seconds, fraction, width) => {
        const tile = tileElement(), index = previewIndex();
        const at = index ? tileFor(index, seconds) : null;
        if (!tile || !at) { tile?.classList.remove('is-visible'); return; }
        const shown = Math.min(index.tileWidth, 160), scale = shown / index.tileWidth, height = Math.round(index.tileHeight * scale);
        const base = new URL(root.dataset.scrubIndexUrl, globalThis.location?.href ?? 'http://localhost/');
        tile.style.width = `${shown}px`; tile.style.height = `${height}px`;
        tile.style.backgroundImage = `url("${new URL(at.sheet, base).href}")`;
        tile.style.backgroundSize = `${index.columns * shown}px ${index.rows * height}px`;
        tile.style.backgroundPosition = `-${at.column * shown}px -${at.row * height}px`;
        tile.style.left = `${Math.max(shown / 2 + 8, Math.min(Math.max(shown / 2 + 8, width - shown / 2 - 8), width * fraction))}px`;
        tile.classList.add('is-visible');
    };
    const paint = (seconds, fraction) => {
        bubble.textContent = formatTime(seconds);
        let chapters = []; try { chapters = JSON.parse(root.dataset.chapters || '[]'); } catch (_) { }
        const chapter = chapters.filter(c => c.time <= seconds).sort((a,b) => b.time - a.time)[0];
        if (chapter?.title) bubble.textContent += ` · ${chapter.title}`;
        const width = track.getBoundingClientRect().width;
        bubble.style.left = `${Math.max(24, Math.min(Math.max(24, width - 24), width * fraction))}px`;
        bubble.classList.add('is-visible');
        paintTile(seconds, fraction, width);
    };
    const thumb = () => { const duration = Number(root.dataset.duration), seconds = Number(input.value); paint(seconds, duration > 0 ? seconds / duration : 0); };
    listen(track, 'pointermove', e => {
        if (dragging || keyboard) { thumb(); return; }
        if (e.pointerType === 'touch' || window.matchMedia?.('(hover: none)').matches) return;
        const duration = Number(root.dataset.duration); if (!(duration > 0)) return;
        const seconds = pointerSeconds(e.clientX, input.getBoundingClientRect(), duration); paint(seconds, seconds / duration);
    });
    // While the handle is dragged the bookmark ticks hide (CSS reads data-dragging on the rail).
    const setDragging = value => { dragging = value; if (value) root.dataset.dragging = 'true'; else delete root.dataset.dragging; };
    listen(input, 'pointerdown', () => { setDragging(true); keyboard = false; thumb(); });
    listen(input, 'input', thumb);
    listen(window, 'pointerup', () => { if (!dragging) return; setDragging(false); if (!keyboard) hide(); });
    listen(window, 'pointercancel', () => { setDragging(false); hide(); });
    listen(track, 'pointerleave', () => { if (!dragging && !keyboard) hide(); });
    // Arrow keys seek 5 seconds, Shift+arrow 30, clamped to the loaded duration. The value is set and the same input and change events a drag
    // raises are sent, so the page seeks exactly as it does for the pointer.
    listen(input, 'keydown', e => {
        keyboard = true;
        const delta = keySeekDelta(e.key, e.shiftKey);
        if (delta !== null && !e.altKey && !e.ctrlKey && !e.metaKey) {
            e.preventDefault();
            const duration = Number(root.dataset.duration);
            if (duration > 0) {
                input.value = String(clampSeek(Number(input.value) + delta, duration));
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
            }
        }
        thumb();
    });
    listen(input, 'focus', () => { if (input.matches(':focus-visible')) { keyboard = true; thumb(); } });
    listen(input, 'blur', () => { keyboard = false; if (!dragging) hide(); });
    states.set(root, listeners);
}
export function detach(root) { states.get(root)?.forEach(remove => remove()); states.delete(root); }
