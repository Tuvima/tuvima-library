// Arrow keys move focus between the buttons of a toolbar (WAI-ARIA toolbar pattern); Home and End jump to the ends.
export function moveFocus(toolbar, key) {
    if (!toolbar) return false;
    const buttons = Array.from(toolbar.querySelectorAll('button:not([disabled])'));
    if (buttons.length === 0) return false;
    const index = buttons.indexOf(document.activeElement);
    let next;
    if (key === 'ArrowRight') next = index < 0 ? 0 : (index + 1) % buttons.length;
    else if (key === 'ArrowLeft') next = index <= 0 ? buttons.length - 1 : index - 1;
    else if (key === 'Home') next = 0;
    else if (key === 'End') next = buttons.length - 1;
    else return false;
    buttons[next].focus();
    return true;
}
