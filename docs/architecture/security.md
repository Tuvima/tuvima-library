---
title: "Security Architecture"
summary: "Live account, profile, application, and resource authorization in Tuvima Library."
audience: "developer"
category: "architecture"
product_area: "security"
tags:
  - "security"
  - "authentication"
  - "authorization"
---

# Security Architecture

This describes the Access implementation under final integration. Delivery evidence and remaining acceptance gates are recorded in the [Access execution status](../plans/access-architecture-2026-09-08/execution/status.md). The replacement is accepted as one cutover; individual worker checkpoints do not represent deployment.

## Accounts and profiles

An account authenticates a person and owns Read, Watch, Listen, View, actual catalogued-library grants, and administrator eligibility. A profile owns experience, restrictions, history, and Personal Space identity. Each account may hold at most eight profile grants. Sharing a profile deliberately shares that profile's experience and private space; it does not transfer another account's administrator eligibility.

Effective administration requires an enabled account, a valid active account/profile grant, account administrator eligibility, and `AdminEnabled` on that grant. An optional grant-specific PIN protects administrator surfaces independently of sign-in and profile-selection PINs. Unlock expiry and authorization revisions are enforced by the Engine. The Dashboard consumes projected navigation/actions and never trusts a locally stored profile role or a seed-owner fallback. Editor entry uses the shared unlock prompt and revalidates before opening.

Local-only accounts have no invented email address and require an explicitly trusted local entry path. A localhost request alone is never an administrator identity. Public setup, authentication, recovery, and health routes are explicitly designated exceptions; other endpoints require an authenticated authority and the applicable operation/resource checks.

## Applications and native clients

Applications own registered service permissions. Their credentials are hashed, independently revocable, and shown once when created. Rotating a credential does not change Application permissions. `X-Api-Key` resolves the live Application and exact credential rather than a role embedded in a key.

Server integrations and automation act as service principals. User clients additionally bind the current account, active profile grant, device, consent, and native token. Effective delegated access is their intersection. Disabling an account, grant, Application, credential, device, or token must revoke dependent access. The Dashboard service identity only authorizes its narrow transport duties; it cannot substitute for the signed-in human.

Interactive Dashboard clients use `DashboardCircuitHttpClientFactory` to forward the current circuit's session outside the pooled HTTP-handler scope. The existing handler pipeline still resolves the protected service credential at send time. A cleared established session suppresses stale ambient-request fallback; an explicit identity-validation token remains a separate supported request. Client configuration and pooled transport lifetime remain owned by the normal HTTP factory.

Profile loading initializes the existing principal and awaits validated authority before its first request. Empty failures remain retryable, and delayed results cannot publish after session identity or access changes. Repeated validation with equivalent authority does not emit a false change event or cancel active playback tools; genuine session, grant or capability changes retain the revocation path. The live refinement evidence is recorded in [the implementation report](../reports/audiobook-refinement-2026-10-02.md).

`TuvimaAuthentication` establishes identity; `IRequestAuthorityResolver` resolves current authority; `IAuthorizationEvaluator` decides registered operations. Endpoint metadata exposes those decisions for mapped-route guardrails. A valid credential alone is insufficient to authorize an operation.

## Resource scope

Catalogue access applies feature and actual library grants to concrete assets before representative selection, counts, grouping, pagination, and serialization. Every artwork, download, reader, HLS, queue, history, bookmark, and progress path verifies its resource independently. Multiple assets of one work do not let an allowed library reveal another library's variant. HLS grants retain exact live authority bindings, and personal writes require the current profile.

View has its own feature and resource policies. Private access resolves the exact active profile. Explicit administrator inspection remains a separate scope. Shared Library access, contributions, review, and Gallery sharing remain separate policies. A Gallery share authorizes proven Gallery members and derivatives, never sibling assets or an entire private source. View assets never enter catalogue provider enrichment. Deleting identity records must not delete original media or transfer a private space to another person.

## Authentication and recovery

Settings > Access contains Users, Applications, and Authentication. Authentication controls local passwords, passkeys, remote sign-in, invitation policy, local-only access, session policy, and external providers. Readiness is derived from real configuration; unavailable sign-in methods carry reasons. Verified external identities use provider, canonical issuer, and immutable subject. Email alone never silently links an identity. See [external authentication](../guides/external-authentication.md).

Administrator password recovery uses one-time recovery codes or the elevated host command:

```powershell
dotnet run --project src/MediaEngine.Admin -- auth reset-password --config-dir config
```

The command takes the password through a non-echoing interactive prompt, requires operating-system administration, and refuses to create a missing database. Recovery invalidates existing security credentials/sessions according to the account recovery service. There is no anonymous localhost password-reset bypass.

## Plugin and event boundaries

Plugin execution receives host-bound identity and declared capabilities. External service permissions are separate from host capabilities. Unimplemented capabilities remain unavailable with a reason. The Fandom Lore service exposes one bounded typed operation with authorization before effects.

Application events use a dedicated `/application-events` hub and durable outbox. Envelopes contain `event_id`, `event_type`, `version`, `occurred_at`, `server_id`, `subject`, and `payload`; the server ID is a persisted opaque identity. Delivery rechecks the current principal and resource scope. Replay is bounded and reports gaps. Dashboard Intercom recipients also receive live account/resource checks. Its frozen Lore/Universe contract pair is unchanged; the reviewed ingestion event contracts add optional asset and pre-removal provenance fields so event authorization remains possible after deletion. Durable projection precedes best-effort Dashboard delivery, and one failed recipient does not discard the event or block other recipients. Exact live-connection and producer evidence is tracked in the execution status.

Webhooks belong to service Applications. Endpoint configuration, selected event permissions, and delivery are revalidated. HTTPS is the public default; explicit local-network approval permits private destinations. Loopback, link-local/metadata, redirects, proxy routing, mixed unsafe DNS answers, and DNS rebinding are rejected. Delivery signs the timestamp plus the exact UTF-8 body with HMAC-SHA256. Signing secrets appear once and are protected at rest. Retries retain a stable delivery identity, bounded queue, attempt count, and expiry; errors expose sanitized status without credentials or response bodies.

## Secrets, limits, and lifecycle

Provider definitions live under `config/providers/`; long-lived provider credentials belong in gitignored overlays under `config/secrets/`. Authentication provider secrets use their dedicated protected overlay. Credentials, PINs, passwords, invitation/recovery tokens, and webhook signing secrets must not enter logs or ordinary DTOs.

Rate limits apply to authentication, credential operations, streaming, general API access, and real-time connections according to the registered policies. Folder and managed-asset operations validate intended roots and provenance before disk access; authorization does not waive existing-source protection.

Pre-beta obsolete database/configuration state fails fast and is rebuilt from configured sources. Do not add compatibility authorization schemas, old role readers, or automatic key conversion. This permission covers disposable application state only; original and read-only source media remain protected. The [Access cutover procedure](../plans/access-architecture-2026-09-08/execution/cutover.md) covers fresh identity state, native re-pairing, and protection of existing Personal Space directories.

## Related

- [Access implementation plan](../plans/access-architecture-2026-09-08/plan.md)
- [View privacy and storage](view-personal-media.md)
- [Build and verification](../guides/running-tests.md)
