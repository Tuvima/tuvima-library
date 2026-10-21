// The phone companion's own device id: random, kept in this browser so two phones on one profile follow independently.
// Storage can be blocked (private windows, cleared site data); then the id lasts for this page and the companion still works.
const storageKey = 'tuvima.companion.deviceId';
let pageId = null;

function randomId() {
    try {
        if (globalThis.crypto && typeof globalThis.crypto.randomUUID === 'function') {
            return globalThis.crypto.randomUUID();
        }
    } catch { /* fall through to the weaker id */ }
    return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`;
}

export function getDeviceId() {
    try {
        const saved = localStorage.getItem(storageKey);
        if (saved) {
            return saved;
        }
    } catch { /* storage is optional */ }

    pageId ??= `companion-${randomId()}`;
    try { localStorage.setItem(storageKey, pageId); } catch { /* storage is optional */ }
    return pageId;
}
