# 0009 — Open from the start; apps always allowed

**Date:** 2026-10-17 (Shy; spec `engineering/specs/open-from-the-start.md`) · **Status:** Accepted

## What
Tuvima is open from the moment it is installed. Any device that can reach the server can sign in, and phone and TV apps can pair straight away; there is no "Who can connect" setting (This computer / Home network / Anywhere), no Anywhere checklist and no "Allow apps to connect" switch (D2, D6). Location checks had already been removed on 2026-10-11, so the setting only blocked apps and showed status text. Tuvima sign-in is always required (no guest access, no localhost administration), with one accepted exception: the no-password "use Tuvima on this computer" account stays as it is, so any device that reaches the server can use it, including a guest on the home Wi-Fi, with its administrator rights, until a password or passkey is added (D5). The server tells apps both its home address and, when the owner set one up in Settings → Network → Remote Access, its outside address (Tailscale or an HTTPS hostname) in the discovery answer; Android uses whichever works, home first (D8). Remote Access stays as an optional secure outside address with **Test remote access**, and unlocks nothing. Details: `docs/architecture/network-and-remote-access.md`, `docs/guides/remote-access.md`.

## Why
Shy, 2026-10-17: closed router ports are the gatekeeper. An accidentally forwarded port is unlikely, and the setting blocked the common case (phones and TVs at home) for no real protection. It was **Maintenance** (one less rule to reason about and test), **Reliability** (apps work on a fresh install, no 404) and honest **Privacy** (no setting that looks like a guard but guards nothing).

## Trade-offs accepted
- **Plain http if a port is forwarded.** The Dashboard serves http only. If someone forwards its port to the internet, sign-ins and pairing codes cross the internet unencrypted. The guides say to use Tailscale or an HTTPS reverse proxy for outside access.
- **The no-password account is open to anyone who reaches the server.** Adding a password or passkey closes it for that account.
- **A leaked cast link works for anyone who reaches the server.** The one-title secret link is the credential; it expires with the title and stops when the phone signs out.
- **Pairing guessing is limited only by the throttle.** The pairing throttle keys on connection address and is now the main brake on code guessing.
- Pre-release: old `who_can_connect` and `native_app_access.enabled` values in a user's `network.json` are ignored, not converted. Apple, Roku and TV web apps do not yet use the outside address; they follow separately.

## Revisit if
Tuvima ships HTTPS itself (then plain http stops being the main risk), users report accidental exposure, or a hosted relay is added (then reach no longer depends on the user's router).
