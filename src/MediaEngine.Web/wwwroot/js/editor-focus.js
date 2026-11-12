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

// The editor on a phone (universal editor v6, section 4a): keeps the window's height above the on-screen keyboard so the footer stays in sight, and scrolls the
// field being typed in into view once the keyboard is up. Whether the phone arrangement is drawn is the device's form factor, not decided here; the property
// this sets (--tl-editor-keyboard) is read only by the phone window's height, so on any other window it does nothing.
let keyboard = null;

export function watchKeyboard() {
    stopKeyboard();
    const viewport = window.visualViewport;
    const root = document.documentElement;

    // The part of the window the keyboard covers (the visual viewport shrinks, the layout viewport does not); the window's height takes it off. A pinch zoom
    // shrinks the visual viewport too but is not a keyboard, so while the page is zoomed the last answer stands. With the keyboard up the home bar is
    // behind it, so the bottom safe area is not added to the footer then.
    const fit = () => {
        if (!viewport || Math.abs(viewport.scale - 1) > 0.01) return;
        const covered = Math.max(0, Math.round(window.innerHeight - viewport.height - viewport.offsetTop));
        if (covered > 0) {
            root.style.setProperty('--tl-editor-keyboard', covered + 'px');
            root.style.setProperty('--tl-editor-safe-bottom', '0px');
        } else {
            root.style.removeProperty('--tl-editor-keyboard');
            root.style.removeProperty('--tl-editor-safe-bottom');
        }
    };

    const reveal = (event) => {
        const field = event.target;
        if (!(field instanceof HTMLElement) || !field.matches('input, textarea, select, [contenteditable="true"]') || !field.closest('.tl-editor-window--phone')) return;
        // Once the keyboard has finished opening, so the field is scrolled to the room that is left.
        window.setTimeout(() => field.scrollIntoView({ block: 'center', behavior: 'smooth' }), 300);
    };

    viewport?.addEventListener('resize', fit);
    viewport?.addEventListener('scroll', fit);
    document.addEventListener('focusin', reveal);
    keyboard = { viewport, fit, reveal, root };
    fit();
}

export function stopKeyboard() {
    if (!keyboard) return;
    keyboard.viewport?.removeEventListener('resize', keyboard.fit);
    keyboard.viewport?.removeEventListener('scroll', keyboard.fit);
    document.removeEventListener('focusin', keyboard.reveal);
    keyboard.root.style.removeProperty('--tl-editor-keyboard');
    keyboard.root.style.removeProperty('--tl-editor-safe-bottom');
    keyboard = null;
}

// Match on a phone (universal editor v6, section 4a): after Apply match closes the sheet, the "staged" note is at the top of a tab that may be a long list, so it is scrolled
// into view and given focus (it is a polite live region, so a screen reader reads it). Returns true once the note exists and has focus.
export function revealStagedNote() {
    const note = document.querySelector('.tl-editor-window [data-staged-note]');
    if (!(note instanceof HTMLElement)) return false;
    const still = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;
    note.scrollIntoView({ block: 'start', behavior: still ? 'auto' : 'smooth' });
    note.focus({ preventScroll: true });
    return document.activeElement === note;
}
