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
