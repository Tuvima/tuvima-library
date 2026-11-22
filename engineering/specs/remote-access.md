# Spec: Away-from-home access (guided Remote access)

> **Status:** Built, awaiting Local checks · **Version:** 1 · **Last updated:** 2026-11-20 · **Related docs:** [Mock-ups](../design/mockups/remote-access/mockups.html) · [Access, identity and remote access review (2026-10-08)](../plans/access-identity-remote-access-review-2026-10-08.md) · Settings alignment (draft) · issue 1152

Locked by Shy on 2026-11-20 ("good to lock it and build it"). Built in the cloud, validated locally as one batch. Questions use range 170 to 176. Answered 2026-11-20: 170B, 171A, 172A, 173A, 174A, 175A, 176A (Shy: "agree on all", "agree on both").

## 1. Goal

At home, with no router changes and no Tailscale, Library works with no password (already built, 1152). This spec adds the other half: a guided way for an administrator to reach the Library away from home, choosing the easiest safe route (Tailscale, a tunnel, their own proxy, or a router door) with the trade-offs said in plain words, and with passwords required for anything coming from outside.

## 2. What exists today (found in the code)

- Settings > Network has Home network, Remote access and Advanced tabs. Remote access is a plain list of three radio buttons ("Not set up", "Tailscale", "HTTPS reverse proxy"), a Test button, and an address field. `NetworkSettings.remote.connection_mode` also has `direct-only` but the page never offers it; it is reached only through Advanced > "Port forwarding & router mapping".
- Advanced holds ports, automatic router mapping (UPnP, NAT-PMP, PCP), manual port-forward steps, reverse-proxy trust and diagnostics (including a shared-address check, CGNAT).
- Tailscale is only observed (`TailscaleRemoteConnectivityProvider`); the docker preset in `deploy/tailscale/` runs Serve on the Dashboard's proxy port. Nothing walks a Windows user through installing it.
- A permanent "Remote access is off" bubble sits on Home network, which can read as "something is broken".
- Cloudflare Tunnel, Caddy and nginx appear only in the 2026-10-08 review, not in the product. Built-in HTTPS certificates do not exist.
- The rule that outside traffic is refused for no-password profiles is built (ingress classifier, forwarded requests count as outside).

## 3. Decisions proposed (all open until Shy answers)

- **D1. One guided flow, five steps** on Remote access: Check, Choose, Passwords, Connect, Test. Replaces the radio list. Reason: Ease of use and Reliability; a person never has to know the words.
- **D2. Check first.** Library inspects Tailscale, the router, shared-address providers and the public address, and says what it found in plain sentences. Uses what the Engine already reads; one new read ties them together.
- **D3. Methods:** Tailscale (suggested when it is the best fit), My own address (tunnel, proxy, or router door, with the shared-address warning), and Not now. See Q170 and Q171.
- **D4. Passwords gate.** Outside sign-in always needs a password or passkey. The wizard lists profiles without one; they stay home only. The setting administrator needs a password to continue. See Q173.
- **D5. Live ticks.** Each connection step re-checks itself and ticks when done. Commands have a Copy button and say which computer they run on.
- **D6. Test from outside** reuses the existing remote test, shows a QR for the phone, and a short list of recent outside sign-ins with refusals. One switch turns outside access off at once without touching home. See Q176.
- **D7. The "Remote access is off" bubble is removed** and replaced by the start page's two status cards. Reason: home working is the normal state.
- **D8. Phone parity.** The flow opens on a phone, one step per screen; steps that must run on the server computer are tagged and can be sent to another device.
- **D9. Advanced keeps** ports, router mapping, proxy trust and diagnostics unchanged, except wording from settings alignment D10.
- **D11. Own-domain wizard** (Shy, 2026-11-20). "My own address" is a four-screen wizard: Address (own a domain? pick the front name), Point it here (tunnel, Caddy file or router door, each with ticking steps), Secure it (certificate valid and renewing), Check (resolves, certificate, Library recognises outside visitors). Domain help covers owning or buying one and which registrars work; Library never buys or stores domain credentials.
- **D12. One-click fixes.** A failed check offers one button that applies the safe fix where one exists (for example adding the proxy to the trusted list); otherwise it says the single next step.
- **D13. Invite QR for outside people.** A one-time invitation shown as link and QR, 7 days by default, using the existing invitation system and the same household and library pickers. A guest always chooses a password or passkey.
- **D14. Caddy file.** The Caddy route offers a ready-made file with the person's address filled in. Nothing is installed by Library.
- **D15. Share with friends goes through the native apps** (Shy, 2026-11-20, answers 170B and 172A). A "Share with friends" card is offered in step 2. The invitation QR opens the Library app (phone, tablet, TV) with the server address and one-time invite already filled in, using the existing device pairing; if the app is not installed, the QR's landing page sends them to their app store, then back into the invite. A mobile web browser is a fallback, not the route. Reason: Ease of use; apps already hold the connection and sign-in for the guest.
- **D16. Built-in HTTPS is a separate later spec** (172A). Tailscale gives its own HTTPS certificate and tunnels get theirs from the provider, so only the router-door route is affected.
- **D10. Privacy.** The wizard makes no outside calls beyond the existing connection test. Any step that contacts Tailscale, Cloudflare or a certificate service is named before it runs.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Not set up | Two status cards (home works, away not set up), one Set up button, one "I already have a proxy" link |
| Checking | A short list with ticks; takes a few seconds, with a Cancel |
| Nothing detected | Suggestion is still Tailscale, with an install step and a QR to the phone app |
| Shared-address provider | Router door route shows a warning and is put last |
| Passwords missing | Profiles listed as home only with a Set a password button |
| Step not done | Button "Check again"; no dead ends |
| Test fails | Plain reason and one next step |
| On | Status, QR, recent outside sign-ins, one off switch |
| Phone | One step per screen, 44 px controls, server-computer steps tagged |
| TV | Not shown (TVs do not edit settings) |

## 5. Data and model changes

- `network.json`: `remote.connection_mode` gains `tunnel` (a proxy or tunnel with a public https address) only if Q170 B is chosen; otherwise none. Nothing else changes.
- One new Engine read returns the check results (named findings, not raw facts). A short recent-outside-sign-in list reads existing sign-in history.
- No data-store changes. Pre-release rule: no conversion.

## 6. Work packets (proposed, not final until questions are answered)

1. **WP1 Network check read** (Standard): one Engine read combining existing detections into plain findings and a suggestion. Done when: the result for Tailscale present, absent, and shared-address cases matches the table in section 4.
2. **WP2 Guided flow** (Complex): five steps, stepper, start page, remove bubble. Done when: a fresh install shows the start page and the stepper reaches Test on a spy provider.
3. **WP3 Passwords gate** (Standard, security review): list and block rule. Done when: a profile with no password cannot sign in from outside, and Continue blocks until the setting administrator has a password.
4. **WP4 Tailscale steps** (Standard): live ticks, copy commands, phone QR. Depends on WP2.
5. **WP5 Own-domain wizard** (Complex): four screens, tunnel/Caddy/router routes, checks and one-click fixes, domain help page. Depends on WP2. Done when: each route reaches a green Check on a spy provider and a failed certificate shows its single next step.
6b. **WP5b Invite QR** (Standard, security review): one-time invitation link and QR. Depends on WP5. Done when: a scanned invitation creates a password-protected account limited to the chosen household and libraries.
6. **WP6 Test and monitor** (Standard): QR, recent outside sign-ins, off switch. Depends on WP2.
7. **WP7 Phone layout** (Standard): phone steps, "Send me these steps". Depends on WP2 to WP6.
8. **WP8 Public documentation** (Standard, tuvima-docs, `content/products/library/`; Shy asked for far more detail): a full guide set explaining the whole process and the reasoning behind it. Pages: Away-from-home overview and how to choose (Tailscale, own address, tunnel, proxy, router door, with privacy, cost, effort, TV support); one step-by-step guide per route (Tailscale including turning on HTTPS, Cloudflare Tunnel, Caddy with the downloadable file, nginx, router port forwarding with manual steps and shared-address provider explanation); getting and pointing a domain; HTTPS and certificates explained; passwords and who can sign in from outside and why; inviting friends through the native apps; testing and troubleshooting each failed check in plain words; turning access off; a "why it works this way" section (CLAUDE.md docs rule). Listed in the folder's manifest, plain vocabulary. Done when: the docs site builds, every guide is linked from the wizard's help links, and each wizard check message has a matching troubleshooting entry. Depends on WP2 to WP6.

## 7. Local checks (SHY-DESKTOP)

- Walk Tailscale on the real server and a phone off the home network.
- A router door attempt on the real router and, if possible, a shared-address connection.
- A Cloudflare Tunnel or Caddy route on a spare name if Shy chooses to keep it.
- Check outside sign-in refused for a no-password profile.

## 8. Risks and open questions

- Cloudflare's free plan limits video through its network; terms must be rechecked at build time. Not offered as a default.
- Router doors fail behind a shared address; the check warns first.
- Built-in HTTPS (so the router route needs no proxy) is a separate, security-reviewed spec (Q172).
- Out of scope here by Shy's direction: notifications (mail, Pushover, webhooks, Discord) and Tuvima ID sign-in on Tuvima Social. Each is its own planning thread. This spec leaves one seam: the Passwords step can later add "Sign in with Tuvima ID".

## 9. Plain English summary

At home Library already works without a password. This adds a short guide for using it away from home. It checks your network, suggests the easiest way (usually Tailscale), makes sure everyone coming in from outside has a password, helps you connect, tests it, and gives you one off switch.
