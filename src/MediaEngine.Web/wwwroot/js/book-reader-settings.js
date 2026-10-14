// ============================================================================
// Book reader settings merge — Tuvima Library
//
// Pure helpers (no foliate-js, no DOM beyond a Storage object passed in) so the
// order in which the reader's appearance is decided can be unit tested in Node.
//
// The order, per key:
//   1. the profile's reading settings (handed over by the page as the base),
//   2. whatever this device saved under 'tuvima-reader-settings' on top of it.
// A key the device never saved uses the profile value. Everything is validated
// and clamped on the way in, so an unreadable or out-of-range stored value is
// dropped (or pulled into range) instead of reaching the page.
// ============================================================================

export const STORAGE_KEY = 'tuvima-reader-settings';

export const THEME_NAMES = ['light', 'sepia', 'dark', 'system'];
export const FONT_NAMES = ['book', 'serif', 'sans'];
export const PAGE_WIDTH_NAMES = ['narrow', 'standard', 'wide'];
export const FLOW_NAMES = ['paginated', 'scrolled'];
export const FONT_SIZE_RANGE = { min: 12, max: 32 };
export const LINE_HEIGHT_RANGE = { min: 1.2, max: 2.4 };

// The key each setting has on the device (PascalCase, like the current reader). Only these are ever written or cleared.
const DEVICE_KEYS = {
    theme: 'Theme',
    fontSize: 'FontSize',
    lineHeight: 'LineHeight',
    font: 'ReaderFont',
    pageWidth: 'PageWidth',
    flow: 'Flow',
};

/**
 * The valid part of `raw` ({ theme, fontSize, lineHeight, font, pageWidth, flow }); anything missing,
 * of the wrong kind or not one of the known values is left out. Numbers are rounded/clamped into range.
 */
export function sanitizeSettings(raw) {
    const out = {};
    if (!raw || typeof raw !== 'object') return out;

    if (typeof raw.theme === 'string' && THEME_NAMES.includes(raw.theme)) out.theme = raw.theme;
    if (typeof raw.font === 'string' && FONT_NAMES.includes(raw.font)) out.font = raw.font;
    if (typeof raw.pageWidth === 'string' && PAGE_WIDTH_NAMES.includes(raw.pageWidth)) out.pageWidth = raw.pageWidth;
    if (typeof raw.flow === 'string' && FLOW_NAMES.includes(raw.flow)) out.flow = raw.flow;

    const size = Number(raw.fontSize);
    if (raw.fontSize !== null && raw.fontSize !== '' && Number.isFinite(size) && size > 0) {
        out.fontSize = clamp(Math.round(size), FONT_SIZE_RANGE.min, FONT_SIZE_RANGE.max);
    }
    const spacing = Number(raw.lineHeight);
    if (raw.lineHeight !== null && raw.lineHeight !== '' && Number.isFinite(spacing) && spacing > 0) {
        out.lineHeight = Math.round(clamp(spacing, LINE_HEIGHT_RANGE.min, LINE_HEIGHT_RANGE.max) * 100) / 100;
    }
    return out;
}

/** The profile values as the base, with every valid device value on top (device wins per key). */
export function mergeSettings(profile, device) {
    return { ...sanitizeSettings(profile), ...sanitizeSettings(device) };
}

/** What this device saved, as a sanitized partial. Unreadable storage gives nothing (the profile values then apply). */
export function readDeviceSettings(storage) {
    try {
        const stored = JSON.parse(storage?.getItem(STORAGE_KEY) ?? 'null');
        if (!stored || typeof stored !== 'object') return {};
        const pick = name => stored[name] ?? stored[name[0].toLowerCase() + name.slice(1)];
        return sanitizeSettings({
            theme: pick(DEVICE_KEYS.theme),
            fontSize: pick(DEVICE_KEYS.fontSize),
            lineHeight: pick(DEVICE_KEYS.lineHeight),
            font: pick(DEVICE_KEYS.font),
            pageWidth: pick(DEVICE_KEYS.pageWidth),
            flow: pick(DEVICE_KEYS.flow),
        });
    } catch (e) {
        console.debug('Reader settings could not be read; using the profile values.', e);
        return {};
    }
}

/**
 * Remembers the keys present in `options` on this device and leaves every other key alone: a setting the person never
 * changed here stays unwritten, so it keeps following the profile. Other entries under the key survive too.
 */
export function writeDeviceSettings(storage, options) {
    try {
        let stored = null;
        try {
            stored = JSON.parse(storage?.getItem(STORAGE_KEY) ?? 'null');
        } catch {
            // An entry that is not JSON holds nothing worth keeping; it is replaced by what is saved now.
        }
        const merged = stored && typeof stored === 'object' ? stored : {};
        const valid = sanitizeSettings(options);
        for (const [name, key] of Object.entries(DEVICE_KEYS)) {
            if (name in valid) merged[key] = valid[name];
        }
        storage?.setItem(STORAGE_KEY, JSON.stringify(merged));
    } catch (e) {
        console.debug('Reader settings could not be saved on this device.', e);
    }
}

/** Forgets this device's reader choices so the profile values apply again. Other keys under the same entry are kept. */
export function clearDeviceSettings(storage) {
    try {
        let stored = null;
        try {
            stored = JSON.parse(storage?.getItem(STORAGE_KEY) ?? 'null');
        } catch {
            // An entry that is not JSON holds nothing worth keeping; it is removed below so the profile values apply.
        }
        if (!stored || typeof stored !== 'object') {
            storage?.removeItem(STORAGE_KEY);
            return;
        }
        for (const key of Object.values(DEVICE_KEYS)) {
            delete stored[key];
            delete stored[key[0].toLowerCase() + key.slice(1)];
        }
        if (Object.keys(stored).length === 0) storage?.removeItem(STORAGE_KEY);
        else storage?.setItem(STORAGE_KEY, JSON.stringify(stored));
    } catch (e) {
        console.debug('Reader settings could not be cleared on this device.', e);
    }
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}
