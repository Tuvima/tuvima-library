const states = new Map();
const active = [];
let removalObserver;
function focusable(root) { return [...root.querySelectorAll('a[href],button,input,select,textarea,[tabindex]')].filter(el => !el.disabled && el.tabIndex >= 0 && !el.closest('[hidden],[inert]') && el.getClientRects().length); }
export function update(root, enabled, defaultFocus) {
    if (!(root instanceof HTMLElement)) return;
    if (states.has(root)) { if (!enabled) detach(root); return; }
    if (!enabled) return;
    const opener = document.activeElement;
    const key = event => {
        if (event.key !== 'Tab' || active.at(-1) !== root || !root.contains(event.target)) return;
        const list = focusable(root), first = list[0], last = list.at(-1);
        if (!first) { event.preventDefault(); root.focus({preventScroll:true}); }
        else if (event.shiftKey && (document.activeElement === first || document.activeElement === root)) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    };
    const focus = event => {
        if (active.at(-1) !== root || root.contains(event.target)) return;
        const modal = event.target.closest?.('dialog[open]');
        if (modal && !root.contains(modal)) return;
        if (event.target.closest?.('.tl-popover-open')) return;
        (focusable(root)[0] || root).focus({preventScroll:true});
    };
    states.set(root, {opener,key,focus}); active.push(root);
    removalObserver ??= new MutationObserver(() => {
        for (const owner of states.keys()) if (!owner.isConnected) detach(owner);
    });
    removalObserver.observe(document.documentElement, { childList: true, subtree: true });
    document.addEventListener('keydown',key); document.addEventListener('focusin',focus);
    if (defaultFocus && !root.contains(document.activeElement)) (focusable(root)[0] || root).focus({preventScroll:true});
}
export function detach(root) {
    const state=states.get(root); if (!state) return;
    const wasActive = active.at(-1) === root;
    document.removeEventListener('keydown',state.key); document.removeEventListener('focusin',state.focus);
    const i=active.indexOf(root); if(i>=0) active.splice(i,1);
    states.delete(root);
    if (wasActive && state.opener?.isConnected) state.opener.focus({preventScroll:true});
    if (!states.size) { removalObserver?.disconnect(); removalObserver = undefined; }
}
