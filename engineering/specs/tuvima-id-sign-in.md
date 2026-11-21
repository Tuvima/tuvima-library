# Spec: Sign in to Library with Tuvima ID (and the premium tier behind it)

> **Status:** Locked · **Version:** 2 · **Last updated:** 2026-11-20 · **Related docs:** [Access, identity and remote access review](../plans/access-identity-remote-access-review-2026-10-08.md) (section 7) · User-auth plan decisions D8, D19, D20 · [Backup and restore](backup-and-restore.md) (Q4) · public Tuvima ID and Tuvima Social pages on docs.tuvima.com

Locked by Shy 2026-11-20. Questions 190 to 199 answered. Proposed items P1 to P5 are confirmed by the lock.

## What I could and could not find

- **Library side already has the plumbing.** Library has accounts, invitations, passkeys, and one standard "Sign in with…" (OpenID Connect) option. Your decision D8 says a future Tuvima ID is just another provider address. D19 says a first-time external sign-in never creates an account (an invitation links it), and the provider never grants admin. This plan builds on those, it does not replace them.
- **Tuvima ID and Tuvima Social are described only in public docs**, and Library's code does not reference them. The docs say: ID keeps your email and security settings private; each community sees only a persona; the ID operators can read what is stored; "Sign in with Tuvima ID elsewhere" (a separate persona and email address per site) is **planned, not built**; paid member tiers "come later, with payments". ID also plans to deliver email and phone alerts for every Tuvima app.
- **Unknown to me:** where the Tuvima ID and Social code lives (it is not in the three repositories I can see), whether ID will speak standard OpenID Connect, what billing exists, and what it costs to run per user. Section 6 therefore splits work into "ID side" (prerequisites I can only specify, not check) and "Library side".

## 1. Goal

Someone who runs Library at home can optionally link it to their Tuvima ID, then invite family and friends who sign in with theirs (like Plex accounts), with no password to invent per server. Linking is a paid, hosted convenience: **the Library keeps working fully, offline, with its own sign-in, whether or not anyone ever links or the subscription lapses.**

## Product walkthrough (the proposed experience)

1. **Link the server.** Settings > Security shows a "Tuvima ID" card, off by default. An admin presses **Link with Tuvima ID**; their browser opens Tuvima ID's consent screen in plain words: "This will tell Tuvima ID this server's name and a random installation code. It will not share your library, titles, files or photos." They approve, and the card shows "Linked to <their ID>". Unlinking is one button and removes only the link.
2. **Invite a person.** In the existing Add person flow, the admin chooses household, profiles and access as today, then gets an invitation link/code to send. Nothing new is looked up about the person.
3. **Accept.** The invited person opens the link, presses **Sign in with Tuvima ID**, chooses the name this server will show for them (default: their first name), and approves a consent screen that names this server. They are in, with exactly the access the admin chose. The server never grants admin because of an ID sign-in (D19).
4. **Later sign-ins.** The server's sign-in page gets a **Sign in with Tuvima ID** button beside the normal choices (only when linked). On the home network, no-password profiles keep working as now (your standing decision), so nobody at home needs ID at all.
5. **Internet down or subscription paused.** The ID button explains itself ("Tuvima ID can't be reached. Use your Library sign-in."). Devices already signed in stay signed in. Anyone with a local sign-in uses it. Admins must always have one.
6. **Premium paused (Q191).** The card says what stopped and what did not; no data is deleted; linked people are told once and offered to set a local password while they still can.
7. **Phones and TVs.** Same flow on a phone browser. Native apps and TV pairing reuse the device-pairing code flow, with the approval step done with Tuvima ID on a phone; this is a later packet (WP7), not v1.

Stays the same: local accounts, household profiles, PINs, passkeys, invitations, "no-password profiles on the home network", and "server works offline". Not in v1: cross-server watch-progress sync, finding people by name, linking Library to Tuvima Social posts, remote access itself.

### Acceptance criteria
- **A1.** A fresh Library with no ID link behaves exactly as today (no extra screens, no network calls to Tuvima).
- **A2.** An admin can link and unlink from Settings > Security in under a minute; unlinking leaves every local account working.
- **A3.** An invited person can join with Tuvima ID and gets only the access the invitation named.
- **A4.** With the internet unplugged, existing sessions and local sign-ins work; the ID button explains why it is off.
- **A5.** Tuvima ID never learns titles, paths, photos or listening history; the server never learns the person's ID email unless Q192 says so.
- **A6.** A lapsed subscription pauses only linked sign-in, after the grace period in Q191, and deletes nothing.
- **A7.** Works at phone width; every new screen is dark-only and uses existing App* look.

## 2. Scope

**In scope:** link/unlink a server; Tuvima ID as one-click preset of the existing OpenID Connect option; accepting an invitation with ID; sign-in button and offline behaviour; premium entitlement check and lapse behaviour; settings screen; public docs pages; the contract with the ID side.

**Out of scope:** building Tuvima ID itself or billing; remote access (separate thread); notifications/mail/webhooks (separate thread); cross-server progress sync (future, see backup spec Q4); Social posting from Library; finding people by handle; work accounts.

## 3. Decisions made

Answered by Shy 2026-11-20:
- **D1 (190).** The server owner pays. Premium is a tier of Tuvima Social/ID, details still undefined. Any Tuvima ID holder can sign in to a server, but only with an invitation code from that server's admin.
- **D2 (191).** Linked sign-in ends at the end of the subscription period.
- **D3 (192).** The server admin sends the invitation to whomever they choose; the person signs in with Tuvima ID and gets the access named. Library-side work.
- **D4 (193).** Invitation link or code; no directory lookup.
- **D5 (194).** Admins must keep a local password or passkey; others optional.
- **D6 (195).** Up to three servers per premium owner.
- **D7 (197).** Tuvima ID is a separate Tuvima project and will be OIDC compatible. Library writes the contract it needs (see the hand-off spec) and Social/ID support it natively.
- **D8 (198).** Linking Library never links Social; separate opt-in.
- **D9 (199).** Build the plumbing and architecture now. Until ID is ready the feature shows "Coming soon" with the core built but disabled.

- **D10 (196).** After the subscription end date, ID sign-in keeps working 7 days while the server tries to reach Tuvima, then pauses.
- **D11 (199b).** While disabled, "Coming soon" shows to admins only in Settings > Security, nothing on the sign-in page.

Confirmed by the lock:
- **P1.** Linking never turns on remote access or publishes the server address.
- **P2.** Per-server private identifiers, so servers cannot be compared.
- **P3.** Signed entitlement the server verifies itself.
- **P4.** Invited people choose a per-server display name.
- **P5 (192, email).** Linked accounts need no email on the server; ID handles recovery. Local accounts keep the email rule.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal (unlinked) | Settings > Security: one quiet card "Tuvima ID: not linked" with a Learn more link. No banner anywhere else. |
| Linked, admin | Card: linked ID, plan ("Premium, renews <date>"), people signed in with ID, Unlink. |
| Linked, sign-in page | Extra button "Sign in with Tuvima ID" under existing choices. |
| Not premium yet | Link button leads to a plain explainer with the price and a Subscribe button (opens Tuvima ID in the browser). No nagging elsewhere. |
| Empty | "Nobody has joined with Tuvima ID yet. Add a person to send an invitation." |
| Loading | Standard app loading state while the consent screen round-trip completes. |
| Error: ID unreachable | "Tuvima ID can't be reached right now. Your Library sign-in still works." |
| Error: invitation used or expired | Existing invitation messages. |
| Partly ready (grace period) | Admin card: amber line "Premium ended on <date>. ID sign-in pauses on <date>." |
| Phone | Same screens; consent and card stack to one column; Unlink is in the card's "..." menu. |
| TV | Sign-in shows a short pairing code; approve on a phone. |

## 5. Data / model changes

- Library stores: a random installation ID and key (so the ID service can recognise this server), the link (which ID owns it), the cached entitlement (plan, expiry, signature), and per person a link from the private-per-server identifier to their local account (the table for external logins already exists).
- Nothing about media, paths or history ever leaves. No existing data changes; pre-release resets are acceptable.
- ID side (prerequisite, not in this repo): a "My servers" list on the account page, the entitlement, and the per-server identifiers.

## 6. Work packets (Library side; ID side listed as prerequisites)

**ID side (needs an owner; I cannot see this code):**
- **I1.** Tuvima ID as a standard OpenID Connect provider with per-server identifiers and a consent screen.
- **I2.** Premium plan, billing and a signed entitlement the server can fetch. Today docs say payments are "later".
- **I3.** "My servers" page on the account page (link list, unlink, see sign-ins).

**Library side:**
- **WP1 Installation identity and link record.** Standard, Complex-lite. Depends on: none. Done when: a fresh install has an ID and rotatable key; guardrail test proves nothing is sent while unlinked.
- **WP2 Tuvima ID preset.** Mechanical/Standard. Depends on: I1. Done when: the Sign in with… settings offer a one-click "Tuvima ID" with no provider-specific code.
- **WP3 Link/unlink and Settings > Security card.** Standard. Depends on: WP1, I1. Done when: A2 passes at four widths with real-UI mock.
- **WP4 Accept invitation with ID, per-server display name.** Standard. Depends on: WP2. Done when: A3 passes; ID sign-in without an invitation never creates an account.
- **WP5 Entitlement cache and lapse rules.** Complex (security-sensitive: signature check, clock tampering). Depends on: I2, WP1. Done when: A6 passes with test clocks; reviewer agent signs off.
- **WP6 Sign-in page button, offline messages, docs pages.** Standard. Depends on: WP3, WP4. Done when: A4 passes with the network blocked.
- **WP7 TV and native pairing with ID (later).** Depends on: WP6 and the remote-access thread's outcome.

Order: WP1, then WP2 and WP3, then WP4 and WP5, then WP6.

## 7. Local checks (for SHY-DESKTOP, appended to the batch list when built)
- Link, invite, accept and unlink on a small test Library with a test ID account; pull the network cable and repeat sign-ins.
- Check the consent wording reads clearly on a phone.
- Real-media Library: confirm nothing in the Engine log mentions titles or paths in any ID call.

## 8. Risks and open questions

- **Cost and gating risk.** If basic ID sign-in is paid, home users may feel taxed for something Jellyfin gives free. Mitigation: nothing at home depends on it, and the pitch is "invite people without opening your network", not "unlock your own server".
- **Lockout risk.** A person with only an ID sign-in is locked out during an ID outage. Mitigation: Q194.
- **Privacy honesty.** ID will know which servers a person signs into and when, even though it never sees content. The consent and docs pages must say so plainly, as the ID privacy page already does for personas.
- **Email rule conflict.** The user-auth plan says every account has an email; ID is built to keep emails private (Q192).
- **Coordination.** Remote access (relay, Tailscale guide, port forwarding) will probably want a hosted relay; whether that is also premium is decided there, but the same subscription should cover both. Notifications may ride on ID's planned alert delivery; same tier question.
- **Billing does not exist yet** (D9: built disabled until ID is ready).

### Questions still open

None.

## Plain English summary

You would be able to link your home Library to your Tuvima ID and invite people who sign in with theirs, with no new passwords and nothing shared about what is in your library. It is a paid, hosted extra that you (the owner) pay for; guests do not. Your Library never depends on it: at home, offline, or after the subscription ends, everyone still signs in the local way and nothing is deleted. The biggest unknowns are on the Tuvima ID side (where it is built, whether billing exists), so the first thing I need from you is answers to 190 to 199, especially 190 (who pays) and 197 (where ID lives).


## Appendix: hand-off for the Tuvima ID team

> **Status:** Draft · **Version:** 1 · **Last updated:** 2026-11-20 · For the Tuvima ID / Social team. Companion to tuvima-id-sign-in-draft.md. Self-contained.

Library is a local-first home media server. Its owner may link it to their Tuvima ID so invited people sign in with theirs. Library works fully without ID. Library uses only standard OpenID Connect, no ID-specific protocol code, so everything below is something ID must offer.

## 1. What Library will ask for

1. **Standard OIDC provider.** Discovery document, authorization code with PKCE, signed ID tokens, published signing keys (Library caches them and validates offline), refresh not required.
2. **Per-server private identifier.** The `sub` Library receives is different for every server for the same person, and stable over time. ID never gives one server another server's identifier. It is not the Social persona ID.
3. **Minimal claims.** Only `sub` and an optional display-name suggestion. No email unless the person ticks it on the consent screen. Library does not need email (accounts link by invitation, ID handles recovery).
4. **Consent screen naming the server.** Shows the server's name, says plainly that Tuvima ID learns which servers the person signs in to and when, and that no library content is shared.
5. **Server registration (owner link).** Owner signs in and approves linking a server identified by a random installation ID and public key Library generates. ID stores the link. Up to three servers per premium owner. Unlink from either side.
6. **"My servers" page** in the account: servers the person has signed in to (name, last sign-in), and a Sign out / Remove button for each; owner view lists linked servers.
7. **Entitlement.** A signed statement for a linked server: owner ID-link reference, plan, `valid_until` (end of the paid period), signed with a key Library can verify offline. Library fetches it at most daily and on renewal. Renewal extends `valid_until`; cancellation does not shorten it. Exact claim names are for the ID team to propose; Library only needs "plan", "valid_until", "server limit".
8. **Revocation.** Owner unlinks or a person is removed from Library: Library tells ID (best effort); ID can also tell Library a person deleted their ID, via signed notice on next contact.
9. **Device pairing (later).** OAuth device-authorization flow for TVs and native apps: a code shown on the device, approved on a phone with Tuvima ID.

## 2. What Library will NOT send
Titles, file names, paths, photos, listening or watch history, IP addresses of guests, the library's size. ID sees: installation ID, server name the owner chose, sign-in times, person's per-server identifier.

## 3. Social integration points (so Social supports it natively)
- Linking Library never links Social, and never reveals Social personas to a server or servers to Social (separate opt-in later; a future "share what I'm watching" feature would be its own consent).
- Social/ID show premium status and the "My servers" page.
- Paid tier details are undefined; Library only needs the entitlement in item 7.

## 4. Failure behaviour Library assumes
ID unreachable: existing Library sessions and local sign-in continue; the ID button explains it. Subscription ended: ID sign-in stops at `valid_until` plus the grace period Shy chooses (default 7 days). Nothing is deleted.

## Plain English summary
Library speaks plain OpenID Connect. ID must give each server a private identifier per person, show a clear consent screen, let owners link up to three servers, and issue a signed paid-until statement. Library never sends what is in anyone's collection.
