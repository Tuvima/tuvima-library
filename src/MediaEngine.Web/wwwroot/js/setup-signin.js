// setup-signin.js — Tuvima Library
// Redeems the one-time code the Dashboard's server holds for the first administrator's sign-in, so first-run setup
// can carry on signed in without leaving its page. The code travels in a custom header (a cross-site page cannot send
// one) and works once; the response only sets the sign-in cookie. Returns true when the browser is now signed in.
export async function redeem(code) {
    try {
        const response = await fetch('/auth/setup-handoff', {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'X-Tuvima-Setup-Handoff': code },
        });
        return response.ok;
    } catch {
        return false;
    }
}
