// Moves keyboard focus to one field of the editor pop-over (universal editor v3, D8): the player's lyrics button asks for the Lyrics field.
// Returns true once the field exists and has focus, so the caller can stop asking.
export function focusField(key) {
    if (typeof key !== 'string' || key.length === 0) return false;
    const row = document.querySelector(`.tl-editor-window [data-field-key="${CSS.escape(key)}"]`);
    const input = row?.querySelector('textarea, input, [contenteditable="true"]');
    if (!input) return false;
    input.scrollIntoView({ block: 'center' });
    input.focus({ preventScroll: true });
    return document.activeElement === input;
}

// The editor on a phone (universal editor v6, section 4a): tells the pop-over when the window is no wider than a tablet (so the phone arrangement is drawn
// for a narrow window as well as for a phone), and keeps the window's height above the on-screen keyboard so the footer stays in sight and the field being
// typed in scrolls into view. Returns whether the window is narrow now; the pop-over is told again when that changes.
let layout = null;

export function watchLayout(ref, query) {
    stopLayout();
    const narrow = window.matchMedia(query);
    const viewport = window.visualViewport;
    const root = document.documentElement;

    // The part of the window the keyboard covers (the visual viewport shrinks, the layout viewport does not); the window's height takes it off.
    const fit = () => {
        const covered = narrow.matches && viewport ? Math.max(0, Math.round(window.innerHeight - viewport.height - viewport.offsetTop)) : 0;
        if (covered > 0) root.style.setProperty('--tl-editor-keyboard', covered + 'px');
        else root.style.removeProperty('--tl-editor-keyboard');
    };

    const tell = () => {
        fit();
        ref.invokeMethodAsync('NarrowWindowChanged', narrow.matches).catch(() => { /* the pop-over is gone */ });
    };

    const reveal = (event) => {
        const field = event.target;
        if (!narrow.matches || !(field instanceof HTMLElement) || !field.matches('input, textarea, select, [contenteditable="true"]')) return;
        // Once the keyboard has finished opening, so the field is scrolled to the room that is left.
        window.setTimeout(() => field.scrollIntoView({ block: 'center', behavior: 'smooth' }), 300);
    };

    narrow.addEventListener('change', tell);
    viewport?.addEventListener('resize', fit);
    viewport?.addEventListener('scroll', fit);
    document.addEventListener('focusin', reveal);
    layout = { narrow, viewport, tell, fit, reveal, root };
    fit();
    return narrow.matches;
}

export function stopLayout() {
    if (!layout) return;
    layout.narrow.removeEventListener('change', layout.tell);
    layout.viewport?.removeEventListener('resize', layout.fit);
    layout.viewport?.removeEventListener('scroll', layout.fit);
    document.removeEventListener('focusin', layout.reveal);
    layout.root.style.removeProperty('--tl-editor-keyboard');
    layout = null;
}
