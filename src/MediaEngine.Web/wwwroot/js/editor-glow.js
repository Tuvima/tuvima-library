// The page proves the save (universal editor v3, D7): the parts a save changed glow for a few seconds. The picture and title of the page's
// own item, and any list row that carries one of the saved items' ids, get the class that plays the glow; it is taken off again so a
// later save can glow the same part. A reduced-motion setting turns the animation into a short, still outline (see app.css).
const glowClass = 'tl-just-changed';
const glowMilliseconds = 3600;

export function glow(itemIds) {
    const targets = new Set();
    for (const id of Array.isArray(itemIds) ? itemIds : []) {
        for (const row of document.querySelectorAll(`[data-work-id="${CSS.escape(String(id))}"]`)) targets.add(row);
    }
    for (const part of document.querySelectorAll('.tl-detail-hero img, .tl-detail-hero h1, .tl-detail-person-portrait img')) targets.add(part);
    // Two frames later, so the page has drawn the refreshed values before the glow starts.
    requestAnimationFrame(() => requestAnimationFrame(() => {
        for (const element of targets) {
            element.classList.remove(glowClass);
            void element.offsetWidth;
            element.classList.add(glowClass);
            setTimeout(() => element.classList.remove(glowClass), glowMilliseconds);
        }
    }));
}
