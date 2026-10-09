---
title: "Security Architecture"
description: "Live account, profile, application, and resource authorization in Tuvima Library."
audience: "developer"
category: "architecture"
product_area: "security"
tags:
  - "security"
  - "authentication"
  - "authorization"
status: current
---

# Security Architecture

## In this page

Live account, profile, application, and resource authorization in Tuvima Library.

## Where this lives in the code

- `src/MediaEngine.Identity`
- `src/MediaEngine.Api/Security`
- `src/MediaEngine.Admin`
- `src/MediaEngine.Web`

This describes the Access implementation under final integration. Delivery evidence and remaining acceptance gates are recorded in the [Access execution status](../../engineering/plans/access-architecture-2026-09-08/execution/status.md). The replacement is accepted as one cutover; individual worker checkpoints do not represent deployment.

## Accounts and profiles

An account authenticates a person and owns Read, Watch, Listen, View, actual catalogued-library grants, and administrator eligibility. A profile owns experience, restrictions, history, and Personal Space identity. Each account may hold at most eight profile grants. Sharing a profile deliberately shares that profile's experience and private space; it does not transfer another account's administrator eligibility.

Effective administration requires an enabled account, a valid active account/profile grant, account administrator eligibility, and `AdminEnabled` on that grant. An optional grant-specific PIN protects administrator surfaces independently of sign-in and profile-selection PINs. Unlock expiry and authorization revisions are enforced by the Engine. The Dashboard consumes projected navigation/actions and never trusts a locally stored profile role or a seed-owner fallback. Editor entry uses the shared unlock prompt and revalidates before opening.

Local-only accounts have no invented email address and require an explicitly trusted local entry path. A localhost request alone is never an administrator identity. Public setup, authentication, recovery, and health routes are explicitly designated exceptions; other endpoints require an authenticated authority and the applicable operation/resource checks.

## Applications and native clients

Applications own registered service permissions. Their credentials are hashed, independently revocable, and shown once when created. Rotating a credential does not change Application permissions. `X-Api-Key` resolves the live Application and exact credential rather than a role embedded in a key.

Server integrations and automation act as service principals. User clients additionally bind the current account, active profile grant, device, consent, and native token. Effective delegated access is their intersection. Disabling an account, grant, Application, credential, device, or token must revoke dependent access. The Dashboard service identity only authorizes its narrow transport duties; it cannot substitute for the signed-in human.

Interactive Dashboard clients use `DashboardCircuitHttpClientFactory` to forward the current circuit's session outside the pooled HTTP-handler scope. The existing handler pipeline still resolves the protected service credential at send time. A cleared established session suppresses stale ambient-request fallback; an explicit identity-validation token remains a separate supported request. Client configuration and pooled transport lifetime remain owned by the normal HTTP factory.

Profile loading initializes the existing principal and awaits validated authority before its first request. Empty failures remain retryable, and delayed results cannot publish after session identity or access changes. Repeated validation with equivalent authority does not emit a false change event or cancel active playback tools; genuine session, grant or capability changes retain the revocation path. The live refinement evidence is recorded in [the implementation report](../../engineering/reports/audiobook-refinement-2026-10-02.md).

`TuvimaAuthentication` establishes identity; `IRequestAuthorityResolver` resolves current authority; `IAuthorizationEvaluator` decides registered operations. Endpoint metadata exposes those decisions for mapped-route guardrails. A valid credential alone is insufficient to authorize an operation.

## Resource scope

Catalogue access applies feature and actual library grants to concrete assets before representative selection, counts, grouping, pagination, and serialization. Every artwork, download, reader, HLS, queue, history, bookmark, and progress path verifies its resource independently. Multiple assets of one work do not let an allowed library reveal another library's variant. HLS grants retain exact live authority bindings, and personal writes require the current profile.

View has its own feature and resource policies. Private access resolves the exact active profile. Explicit administrator inspection remains a separate scope. Shared Library access, contributions, review, and Gallery sharing remain separate policies. A Gallery share authorizes proven Gallery members and derivatives, never sibling assets or an entire private source. View assets never enter catalogue provider enrichment. Deleting identity records must not delete original media or transfer a private space to another person.

## Open screens

An open Dashboard screen is a Blazor circuit that never sees a new HTTP request, so it follows access changes by two routes.

- **One-minute check (the backstop).** `SessionRevalidatingAuthenticationStateProvider` asks the Engine once a minute whether the screen's sign-in still stands and re-applies the door rule to the place the screen was opened from (`ExposurePolicy` against the current "who can connect"). The same call refreshes what the person may do, so there is at most one Engine validation per open screen per minute and none per page interaction. A revoked, disabled or wrong-place sign-in turns the screen anonymous and `SessionEndedRedirect` (in every layout a signed-in person can reach) sends it to `/auth/login`. An Engine hiccup (timeout, throttling, restart) never signs anyone out; only a proven invalid sign-in does.
- **Instant for big changes.** `OpenScreenCircuitHandler` registers each signed-in circuit in the singleton `OpenScreenRegistry` (account, profile, session, a hash of the session token, and the place it was opened from) and removes it when the circuit closes. After the Engine confirms the change, the Dashboard calls `CloseWhere` so matching screens go to sign-in at once: lowering "who can connect" (screens from farther away), removing or disabling a person, removing a profile or a profile grant, signing out a device, and signing out other sessions (the kept session stays open).

Not covered: media already streaming (it uses short grants) and Engine-pushed session events.

## Authentication and recovery

The network's **Who can connect** setting (`config/network.json`, Settings > Network) is the one door rule: the Dashboard's exposure policy runs right after the host allow-list and refuses any request from farther away than it allows, and Engine sign-in admits a remote client only under *Anywhere* over HTTPS. Settings > Access contains Users, Applications, and Authentication. Authentication controls local passwords, passkeys, invitation policy, local-only access, session policy, and external providers. Readiness is derived from real configuration; unavailable sign-in methods carry reasons. Verified external identities use provider, canonical issuer, and immutable subject. Email alone never silently links an identity. See [external authentication](../guides/external-authentication.md).

**Sessions remember where they started.** Every session stores the place it was made from (`this_computer`, `home_network` or `remote`) in `auth_sessions.issued_ingress`. The Dashboard tells the Engine where each request comes from in the `X-Tuvima-Client-Ingress` header on session validation (only the Dashboard service credential is believed; a missing header counts as remote). A session made at home or on this computer is refused when it is used from outside, with the reason `sign_in_again_here`; it is not revoked, so it works again at home and the browser keeps its sign-in cookie. A session made from outside keeps working anywhere the door rule allows. Sign-in methods use the same three values to compare where the visitor is against **Who can connect**, so *This computer only* also refuses other devices on the home network.

The administrator-console PIN stays optional: sign-in and profile switching never ask for it, and the unlock is requested only when entering administrator areas while protection is on. A restricted (child) profile never has administrator authority, even if its grant says `AdminEnabled`; the Engine refuses to set it and the Dashboard hides the toggle.

Administrator password recovery uses one-time recovery codes or the elevated host command:

```powershell
dotnet run --project src/MediaEngine.Admin -- auth reset-password --config-dir config
```

The command takes the password through a non-echoing interactive prompt, requires operating-system administration, and refuses to create a missing database. Recovery invalidates existing security credentials/sessions according to the account recovery service. There is no anonymous localhost password-reset bypass.

### Sign-in limits

Three layers keep guessing in check without letting one person lock out everyone else:

- **The Dashboard limits each address.** Every anonymous sign-in request (password, passkey options and finish, recovery, password reset, invitation accept, and the setup calls that start setup or create the administrator) is counted per client address by `SignInAttemptLimiter`: 10 a minute from `this_computer` and `home_network` addresses, 5 a minute from `remote` ones. Past the limit the Dashboard answers 429 with `Retry-After` and "Too many attempts. Try again in a minute." Remote IPv6 visitors share one allowance per /64, and all remote visitors together are capped at 120 a minute so home sign-ins always keep a share. Setup guesses (the setup code and the administrator password) use the visitor's real connection address, passed to the interactive app with the ingress. Setup guesses are counted only when a setup code is actually submitted, not on every page load. Wrong PIN guesses when switching profile are limited to 10 a minute per target profile, kept separately for remote and home callers so a remote guess run (or the Engine's lock on it) never stops someone at home switching into the same profile. A remote caller whose address cannot be determined is refused with a plain message rather than counted in a shared bucket. A configured reverse proxy that connects to the main port hides every visitor behind its own address, so sign-in and setup are refused for it with "use the proxy port", and the Dashboard logs one warning per process; use the dedicated proxy port (`remote.proxy_port`) so real visitor addresses are used. On the Engine, profile switching has its own per-session allowance (30 a minute) so one signed-in person switching quickly cannot use up the shared 300 a minute that Dashboard sign-ins rely on.
- **The Engine only answers to loopback names.** Its `AllowedHosts` is `localhost;127.0.0.1;[::1]`, so a web page that rebinds a hostname to the user's machine (DNS rebinding) is refused with 400. The Dashboard (`Engine:BaseUrl`), the Docker healthcheck and the installer all use one of those names; a guardrail test fails if the setting goes back to `*`.
- **Passkeys belong to the public address.** `PublicAddressPasskeyOptions` sets the passkey relying-party domain to the host of `network.remote.public_hostname` and accepts only that exact origin, read again for every request scope so a changed address applies without a restart. With no valid public address no origin is accepted.
- **Pairing links ignore forwarded headers.** The device-pairing link is built from the public address when set, otherwise the Engine's own request origin; `X-Forwarded-Proto` and `X-Forwarded-Host` are never trusted. The Dashboard edge still replaces it with its own host-allow-listed origin.
- **The Engine treats the Dashboard as many people.** Because every Dashboard-forwarded sign-in reaches the Engine from the Dashboard's own address, requests carrying the Dashboard service credential get their own partition of the `authentication` policy (300 a minute, `AuthenticationRateLimitPartition`); the credential is recognised from memory so the limiter still runs before authentication. Any other caller keeps 10 a minute per address.
- **Account lockout only counts internet failures.** After 5 failures the password or PIN is locked for 15 minutes, but only failures made with `original_client_ingress = remote` count, and the lockout blocks only `remote` attempts. The same rule covers profile-switch PIN guesses (using the session's issuing place). Home and this-computer attempts never count toward or get blocked by the lockout, and a home success never rewrites a lock recorded meanwhile; they are limited per address by the Dashboard instead. Callers that do not say where a request came from are treated as remote. A stranger who knows an email address can therefore no longer lock the owner out at home.

## Plugin and event boundaries

Plugin execution receives host-bound identity and declared capabilities. External service permissions are separate from host capabilities. Unimplemented capabilities remain unavailable with a reason. The Fandom Lore service exposes one bounded typed operation with authorization before effects.

Application events use a dedicated `/application-events` hub and durable outbox. Envelopes contain `event_id`, `event_type`, `version`, `occurred_at`, `server_id`, `subject`, and `payload`; the server ID is a persisted opaque identity. Delivery rechecks the current principal and resource scope. Replay is bounded and reports gaps. Dashboard Intercom recipients also receive live account/resource checks. Its frozen Lore/Universe contract pair is unchanged; the reviewed ingestion event contracts add optional asset and pre-removal provenance fields so event authorization remains possible after deletion. Durable projection precedes best-effort Dashboard delivery, and one failed recipient does not discard the event or block other recipients. Exact live-connection and producer evidence is tracked in the execution status.

Webhooks belong to service Applications. Endpoint configuration, selected event permissions, and delivery are revalidated. HTTPS is the public default; explicit local-network approval permits private destinations. Loopback, link-local/metadata, redirects, proxy routing, mixed unsafe DNS answers, and DNS rebinding are rejected. Delivery signs the timestamp plus the exact UTF-8 body with HMAC-SHA256. Signing secrets appear once and are protected at rest. Retries retain a stable delivery identity, bounded queue, attempt count, and expiry; errors expose sanitized status without credentials or response bodies.

## Secrets, limits, and lifecycle

Provider definitions live under `config/providers/`; long-lived provider credentials belong in gitignored overlays under `config/secrets/`. Authentication provider secrets use their dedicated protected overlay. Credentials, PINs, passwords, invitation/recovery tokens, and webhook signing secrets must not enter logs or ordinary DTOs.

Rate limits apply to authentication, credential operations, streaming, general API access, and real-time connections according to the registered policies. Folder and managed-asset operations validate intended roots and provenance before disk access; authorization does not waive existing-source protection.

Pre-beta obsolete database/configuration state fails fast and is rebuilt from configured sources. Do not add compatibility authorization schemas, old role readers, or automatic key conversion. This permission covers disposable application state only; original and read-only source media remain protected. The [Access cutover procedure](../../engineering/plans/access-architecture-2026-09-08/execution/cutover.md) covers fresh identity state, native re-pairing, and protection of existing Personal Space directories.

## Related

- [Access implementation plan](../../engineering/plans/access-architecture-2026-09-08/plan.md)
- [View privacy and storage](view-personal-media.md)
- [Build and verification](../guides/running-tests.md)
