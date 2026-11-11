// Cut-off text shows its full text in the app tooltip (Look v3 D8c). A cell, name or path whose text is clipped with an ellipsis
// carries data-overflow-tooltip="full text". The first hover over it wires the shared tooltip, but only when the text really is
// clipped, so a name that fits never gets one. There is no other global tooltip hook: a control with an icon only uses the
// Tooltip parameter or AppTooltip, and the browser's own title="..." is not used outside img, iframe, abbr and time.
import { attach, detach } from './playback-tooltip.js';

// Each wired element holds window resize and scroll listeners (through attach), so one that leaves the page must be detached or the
// listeners pile up for the life of the tab.
const wired = new Set();
let leaveObserver = null;

export function sweepDisconnected() {
    for (const element of wired) {
        if (element.isConnected !== false) continue;
        detach(element);
        wired.delete(element);
        delete element.dataset.overflowTooltipWired;
    }
}

function watchLeaving() {
    if (leaveObserver || typeof MutationObserver !== 'function' || typeof document === 'undefined') return;
    const root = document.documentElement || document.body;
    if (!root || typeof root.nodeType !== 'number') return;
    leaveObserver = new MutationObserver(records => { if (records.some(record => record.removedNodes?.length)) sweepDisconnected(); });
    leaveObserver.observe(root, { childList: true, subtree: true });
}

function clipped(element) {
    return element.scrollWidth > element.clientWidth + 1 || element.scrollHeight > element.clientHeight + 1;
}

export function take(event) {
    sweepDisconnected();
    const element = event.target instanceof Element ? event.target.closest('[data-overflow-tooltip]') : null;
    if (!element) return;
    const text = element.getAttribute('data-overflow-tooltip')?.trim();
    // A redraw can change the text (or remove the clipping) on a control that is already wired: keep the shared tooltip in step.
    if (element.dataset.overflowTooltipWired === 'true') { element.dataset.playbackTooltip = clipped(element) && text ? text : ''; return; }
    if (!text || !clipped(element)) return;
    element.dataset.playbackTooltip = text;
    element.dataset.overflowTooltipWired = 'true';
    attach(element, { hoverOnly: true });
    wired.add(element);
    watchLeaving();
    // The element was just entered, so show the tooltip for it right now.
    element.dispatchEvent(new PointerEvent('pointerenter', { pointerType: 'mouse' }));
}

if (typeof document !== 'undefined') document.addEventListener('pointerover', take, true);
