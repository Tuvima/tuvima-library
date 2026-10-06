// Read-only browser audit. Evaluate in the rendered page at each supported viewport.
// Hidden compact-dock rows and the rating choices are excluded from the main action row.
() => {
    const errors = [];
    const rows = [...document.querySelectorAll('.playback-song-actions')].filter(row => row.getBoundingClientRect().width > 0);
    const measurements = rows.map(row => {
        const buttons = [...row.querySelectorAll('button.playback-song-action')];
        if (buttons.length !== 3) errors.push('A song action row must contain Favorite, Rate and More.');
        return buttons.map((button, index) => {
            const rect = button.getBoundingClientRect(), style = getComputedStyle(button);
            const svg = button.querySelector('svg'), icon = svg?.getBoundingClientRect(), glyph = svg?.getAttribute('data-playback-glyph');
            const label = button.getAttribute('aria-label');
            const fail = reason => errors.push(`${label ?? 'Unnamed action'}: ${reason}`);
            if (Math.abs(rect.width - 44) > 1 || Math.abs(rect.height - 44) > 1) fail('target must be a 44px square');
            if (style.borderRadius !== '50%' || style.borderTopWidth !== '1px') fail('target must use the shared circular border');
            if (!icon || Math.abs(icon.width - 22) > 1 || Math.abs(icon.height - 22) > 1) fail('glyph must be 22px');
            if (icon && (Math.abs(icon.x + icon.width / 2 - rect.x - rect.width / 2) > 1 || Math.abs(icon.y + icon.height / 2 - rect.y - rect.height / 2) > 1)) fail('glyph must be centered');
            if (!svg?.classList.contains('playback-utility-glyph') || svg.getAttribute('viewBox') !== '0 0 24 24' || getComputedStyle(svg).strokeWidth !== '1.5px') fail('glyph must use the shared outline family');
            if (!(index === 0 ? glyph === 'Favorite' : index === 1 ? ['Like', 'Dislike'].includes(glyph) : glyph === 'More')) fail('glyph does not match the action');
            if (!label || button.textContent.trim() || button.parentElement.getAttribute('data-playback-tooltip') !== label) fail('icon-only action needs matching tooltip and accessible name');
            return { label, glyph, target: [rect.width, rect.height], icon: icon && [icon.width, icon.height], border: style.borderTopWidth, radius: style.borderRadius };
        });
    });
    if (!rows.length) errors.push('No visible song action row to validate.');
    return { passed: errors.length === 0, errors, measurements };
}
