// Read-only browser audit. Evaluate in the rendered page at 360, 768 and 1440 px.
// Lists focusable/clickable elements under 44 x 44 px (48 inside .app-bottom-dock) and elements wider than the viewport.
// Text links inside running prose are reported separately as "proseLinks" and do not fail the audit.
() => {
    const viewportWidth = document.documentElement.clientWidth;
    const visible = el => {
        const rect = el.getBoundingClientRect(), style = getComputedStyle(el);
        return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none' && style.opacity !== '0' && !el.closest('[hidden], [inert], [aria-hidden="true"]');
    };
    const describe = el => {
        const rect = el.getBoundingClientRect();
        const name = el.getAttribute('aria-label') || el.textContent.trim().replace(/\s+/g, ' ').slice(0, 30);
        return `${el.tagName.toLowerCase()}${el.className && typeof el.className === 'string' ? '.' + el.className.trim().split(/\s+/).slice(0, 2).join('.') : ''} "${name}" ${Math.round(rect.width)}x${Math.round(rect.height)}`;
    };
    const selector = 'a[href], button, input:not([type="hidden"]), select, textarea, summary, [role="button"], [role="option"], [role="menuitem"], [role="tab"], [role="link"], [tabindex]:not([tabindex="-1"])';
    const undersized = [], proseLinks = [];
    for (const el of document.querySelectorAll(selector)) {
        if (!visible(el) || el.closest('.sr-only, .visually-hidden')) continue;
        const rect = el.getBoundingClientRect();
        const min = el.closest('.app-bottom-dock') ? 48 : 44;
        if (rect.width >= min - 0.5 && rect.height >= min - 0.5) continue;
        const prose = el.tagName === 'A' && getComputedStyle(el).display === 'inline' && el.closest('p, li, dd, td');
        (prose ? proseLinks : undersized).push(describe(el));
    }
    const wide = [];
    for (const el of document.querySelectorAll('body *')) {
        const rect = el.getBoundingClientRect();
        if (rect.width > viewportWidth + 1 && visible(el) && !el.closest('[data-allow-overflow], .swimlane-scroll, [style*="overflow"]')) {
            let scroller = false;
            for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
                const ox = getComputedStyle(p).overflowX;
                if (ox === 'auto' || ox === 'scroll' || ox === 'hidden' || ox === 'clip') { scroller = true; break; }
            }
            if (!scroller) wide.push(describe(el));
        }
    }
    return {
        passed: undersized.length === 0 && wide.length === 0,
        viewportWidth,
        undersizedCount: undersized.length,
        widerThanViewportCount: wide.length,
        undersized: [...new Set(undersized)].slice(0, 60),
        widerThanViewport: [...new Set(wide)].slice(0, 30),
        proseLinks: proseLinks.length
    };
}
