// A window the app keeps modal itself (the details overlay), so the persistent player bar stays usable beside it.
// A browser-modal <dialog> puts the window in the top layer and makes everything outside it inert, the player bar
// included. This module does that part by hand: it sets `inert` on the page behind the window and leaves the rest
// of the shell live, and it tells the window how tall the player bar is so the window can end above it.
//
// What stays live (never inert): anything marked data-overlay-live (the player bar's dock), the pop-over host, any
// <dialog> or [popover] (menus, confirmations, the editor, player panels: all drawn in the top layer), and the
// window's own backdrop. Everything else beside the window, and beside each of its ancestors, is inert.
const barInsetProperty = '--tl-overlay-bar-inset';
const skippedTags = new Set(['SCRIPT', 'STYLE', 'LINK', 'META', 'TEMPLATE', 'NOSCRIPT']);

/** True for an element that must stay interactive while a window is open. */
export function isLive(element) {
    return element.tagName === 'DIALOG'
        || element.hasAttribute('data-overlay-live')
        || element.hasAttribute('data-app-popover-host')
        || element.hasAttribute('data-app-dialog-backdrop')
        || element.hasAttribute('popover');
}

/**
 * Makes everything around `dialog` inert except the live elements, and keeps doing so for elements added while it is
 * open. Returns { release }: removes only the `inert` this call set (an element that was already inert stays so).
 */
export function engageInert(dialog) {
    const marked = new Set();
    const watchers = [];
    const sweep = () => {
        for (let node = dialog; node?.parentElement; node = node.parentElement) {
            for (const sibling of node.parentElement.children) {
                if (sibling === node || marked.has(sibling) || skippedTags.has(sibling.tagName)) continue;
                if (isLive(sibling) || sibling.hasAttribute('inert')) continue;
                sibling.setAttribute('inert', '');
                marked.add(sibling);
            }
        }
    };
    sweep();
    // Elements Blazor adds beside the window later (the page behind finishing its load) are caught here.
    if (typeof MutationObserver === 'function') {
        for (let node = dialog; node?.parentElement; node = node.parentElement) {
            const watcher = new MutationObserver(sweep);
            watcher.observe(node.parentElement, { childList: true });
            watchers.push(watcher);
        }
    }
    return {
        marked,
        release() {
            for (const watcher of watchers) watcher.disconnect();
            watchers.length = 0;
            for (const element of marked) element.removeAttribute('inert');
            marked.clear();
        }
    };
}

/** How far the player bar reaches up from the bottom of the screen: 0 when no bar is showing. */
export function barInset(bar, viewportHeight) {
    if (!bar) return 0;
    const box = bar.getBoundingClientRect();
    return box.height > 0 ? Math.max(0, Math.round(viewportHeight - box.top)) : 0;
}

/**
 * Publishes the bar's reach as --tl-overlay-bar-inset on the window, and keeps it current while the bar appears,
 * resizes or the screen changes. Returns { release }.
 */
export function trackBar(dialog) {
    const bar = () => document.querySelector('[data-overlay-live="player-bar"]');
    const publish = () => dialog.style.setProperty(barInsetProperty, `${barInset(bar(), innerHeight)}px`);
    publish();
    const watcher = typeof ResizeObserver === 'function' ? new ResizeObserver(publish) : null;
    const dock = bar();
    if (dock) watcher?.observe(dock);
    if (document.documentElement) watcher?.observe(document.documentElement);
    window.addEventListener('resize', publish);
    return {
        publish,
        release() {
            watcher?.disconnect();
            window.removeEventListener('resize', publish);
            dialog.style.removeProperty(barInsetProperty);
        }
    };
}

/** True while something drawn above the window owns Escape: a menu or select pop-over, a player panel, another open dialog. */
export function escapeOwnedAbove(dialog) {
    if (document.querySelector('.tl-popover-open:not([hidden])')) return true;
    if (document.documentElement?.hasAttribute('data-playback-panel-open')) return true;
    // The universal search overlay (Ctrl+K) is drawn above the window and closes with its own Escape.
    if (document.querySelector('[data-overlay-live="search"]')) return true;
    for (const other of document.querySelectorAll('dialog[open]')) if (other !== dialog) return true;
    return false;
}

/** True when an Escape from `target` is aimed at the window: focus inside it, or nowhere in particular (the page body). */
export function escapeAimedAt(dialog, target) {
    if (!target || target === document.body || target === document.documentElement) return true;
    return dialog.contains(target);
}
