// Every native title="..." hint is replaced by the app's own tooltip (same look, speed and top layer as AppTooltip).
// A page keeps writing title="..." (or Title=) on its control; the first hover or keyboard focus on it moves the text
// into the shared tooltip, so the browser's slow, unstyled hint never appears. Elements whose title has a real
// meaning to the browser are left alone: images and frames, abbreviations, times and SVG.
import { attach } from './playback-tooltip.js';

const SKIP = 'img, iframe, abbr, time, svg, svg *, [data-native-title]';
const INTERACTIVE = 'button, a[href], input, select, textarea, summary, [role="button"], [role="menuitem"], [tabindex]';

function take(event) {
    const element = event.target instanceof Element ? event.target.closest('[title]') : null;
    if (!element || element.matches(SKIP)) return;
    const text = element.getAttribute('title');
    element.removeAttribute('title');
    if (!text || !text.trim()) return;
    // A redraw puts the title back on a control that is already wired: refresh the text only. Wiring it again and replaying
    // the hover is what used to show a second bubble, or one on the wrong element (Look v3 D8).
    if (element.dataset.tooltipFromTitle === 'true') { element.dataset.playbackTooltip = text.trim(); return; }
    element.dataset.playbackTooltip = text.trim();
    if (!element.hasAttribute('aria-label') && !element.textContent.trim() && !element.getAttribute('aria-labelledby')) {
        element.setAttribute('aria-label', text.trim());
    }
    element.dataset.tooltipFromTitle = 'true';
    attach(element, { hoverOnly: !element.matches(INTERACTIVE) });
    // The element was just entered or focused, so show the tooltip for it right now.
    element.dispatchEvent(new PointerEvent(event.type === 'focusin' ? 'focusin' : 'pointerenter', { pointerType: 'mouse' }));
}

document.addEventListener('pointerover', take, true);
document.addEventListener('focusin', take, true);
