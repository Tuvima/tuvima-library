// Per-profile Vault viewing options, kept only in this browser (nothing about them is sent anywhere). Storage can be blocked
// (private windows, cleared site data); then loading hands back nothing and the screen uses its defaults.
const prefix = 'tuvima.vault.view.v1.';

export function load(profileId) {
    try {
        return localStorage.getItem(prefix + profileId);
    } catch { /* storage is optional */ }
    return null;
}

export function save(profileId, json) {
    try {
        localStorage.setItem(prefix + profileId, json);
        return true;
    } catch { /* storage is optional */ }
    return false;
}
