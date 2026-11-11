// The song table's remembered columns and widths, kept per profile and place in this browser only (nothing about them is sent
// anywhere). Storage can be blocked (private windows, cleared site data): the calls then throw, the Dashboard logs it and the
// table uses the place's default columns for that visit.

export function load(key) {
    return localStorage.getItem(key);
}

export function save(key, text) {
    localStorage.setItem(key, text);
    return true;
}

export function remove(key) {
    localStorage.removeItem(key);
    return true;
}
