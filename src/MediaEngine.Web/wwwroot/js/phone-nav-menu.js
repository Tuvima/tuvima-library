// The phone navigation menu is a modal sheet. Back closes it: opening adds one history entry that belongs to the menu,
// so Back (the Android button, the iOS edge swipe) removes that entry instead of leaving the page.
// Blazor must never see the removal of the menu's own entry as a navigation, so the pop is swallowed before Blazor's listener
// runs: app.js registers window.tuvimaPopHook ahead of blazor.web.js and this module plugs its handler into it.
let session = null;

function listen(current) {
    if ('tuvimaPopHook' in window) {
        window.tuvimaPopHook = current.onPop;
        current.unlisten = () => { if (window.tuvimaPopHook === current.onPop) window.tuvimaPopHook = null; };
        return;
    }
    // Without the hook (app.js not loaded) a capture listener still closes the menu, but cannot get ahead of Blazor's.
    window.addEventListener('popstate', current.onPop, true);
    current.unlisten = () => window.removeEventListener('popstate', current.onPop, true);
}

function finish(current) {
    clearTimeout(current.timer);
    current.unlisten?.();
    if (session === current) session = null;
    current.settle?.();
}

function notifyClosed(current) {
    // The circuit may already be gone when Back arrives; the menu is closing anyway, so a failed call is fine.
    current.dotnet.invokeMethodAsync('CloseFromBack').catch(() => { });
}

export function attach(dotnet) {
    if (session) finish(session);
    const base = history.state && typeof history.state === 'object' ? history.state : {};
    history.pushState({ ...base, tuvimaPhoneMenu: true }, '', location.href);
    const current = { dotnet, href: location.href, removing: false, settle: null, timer: 0 };
    current.onPop = event => {
        if (session !== current) return;
        const ownEntryGone = location.href === current.href && !history.state?.tuvimaPhoneMenu;
        if (current.removing || ownEntryGone) {
            event.stopImmediatePropagation();
            const closedByBack = !current.removing;
            finish(current);
            if (closedByBack) notifyClosed(current);
            return;
        }
        // The person jumped somewhere else in the history: the app handles that navigation, the menu just closes.
        finish(current);
        notifyClosed(current);
    };
    listen(current);
    session = current;
}

// Resolves once the menu's history entry is gone (or straight away when it is not the current entry any more).
export function detach(removeEntry) {
    const current = session;
    if (!current) return Promise.resolve();
    const stillOnOwnEntry = location.href === current.href && history.state?.tuvimaPhoneMenu === true;
    if (!removeEntry || !stillOnOwnEntry) {
        finish(current);
        return Promise.resolve();
    }
    return new Promise(resolve => {
        current.removing = true;
        current.settle = resolve;
        current.timer = setTimeout(() => finish(current), 600);
        history.back();
    });
}
