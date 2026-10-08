# Worker packets and acceptance schedule

Status: execution authorized. P00 artifacts are available under `execution/`; P01 is assigned to an isolated Sol worker. Follow `execution/status.md` for actual completion and open gates.

Read [the decision contract](./plan.md) and the relevant [inventory entry](./inventory.md). These packets implement all phases 0-16 of the supplied specification. A packet is a bounded worker assignment; a delivery checkpoint is the tested, internally consistent phase boundary. Parallel packets inside an atomic cutover are not independent releases.

## Schedule

| Order | Packet | Worker | Prerequisites | Delivery |
| --- | --- | --- | --- | --- |
| 1 | P00: baseline and ownership manifest | Sol, medium | Accepted plan | Planning/verification baseline |
| 2 | P01: shared contracts and registry definitions | Sol, high | P00 | Tested foundation; no live authorization switch |
| 3 | P02: persistence, identity, and application authority | Sol, high | P01 | Starts atomic cutover bundle |
| 4a | P03: catalogue and service endpoint enforcement | Sol, high | P02 contract/schema commit | Atomic cutover bundle |
| 4b | P04: View privacy and scoped administration | Sol, high | P02 contract/schema commit | Atomic cutover bundle |
| 4c | P05: Dashboard identity and functional Access wiring | Terra, medium | P02 contract/schema commit | Atomic cutover bundle |
| 5 | Integrate P02-P05; security gate | Sol integrator; Astra decision review | All three workers done | One authority, no legacy fallback |
| 6a | P06: Users and Applications presentation | Terra, medium | Cutover gate | Core Access checkpoint A |
| 6b | P07: authentication policy and settings | Sol, medium | Cutover gate | Core Access checkpoint A |
| 7 | Core UI/privacy acceptance | Sol integration; Astra acceptance review | P06-P07 | Checkpoint A accepted |
| 8 | P08: plugin host gates | Sol, high | A | Plugin checkpoint B |
| 9 | P09: plugin application gateway | Terra, medium | P08 and registry | Plugin checkpoint B |
| 10 | Plugin acceptance | Sol integration; Astra only for new decisions | P09 | Checkpoint B accepted |
| 11a | P10: playback telemetry and read APIs | Sol, medium | B | Integration checkpoint C |
| 11b | P11: filtered external event delivery | Sol, high | B; telemetry contract agreed | Integration checkpoint C |
| 12 | P12: webhook transport | Terra, medium | P11; P10 playback facts integrated | Integration checkpoint C |
| 13 | P13: final audit, docs, runtime verification, squash preparation | Sol, high; Terra docs after code freeze | P10-P12 | Checkpoint C and complete scope |

P03, P04, and P05 may run together only after their request/response interfaces are fixed. The P02 author becomes P03/integration owner; do not retain a fourth idle worker. P06 and P07 use disjoint settings components and client files. P10 and P11 agree on a small lifecycle event contract before parallel work; playback events cannot pass acceptance until the real telemetry source is integrated.

Core checkpoint A covers source phases 1-9, with active legacy removal from phase 15. B covers phases 10-11. C covers phases 12-14 and final phases 15-16. Source phase 0 is this review plus P00. Audit events and tests are delivered with each packet; they are not deferred to P13.

## P00 — Baseline and ownership manifest

Owner: Sol integration worker. Scope: read-only inventory, baseline results, and small documentation/verification artifacts.

- Record the exact base SHA and worker paths. Run the required baseline restore/build/test once. Classify pre-existing failures; do not erase failing assertions to obtain green output.
- Produce a method/route/policy/principal/resource/scope/owner matrix from the real endpoint registrations, including Web proxies, player/HLS, SignalR connection routes, nested groups, setup/recovery, and admin/CLI paths. The source candidate list in inventory.md is a starting list, not a completed route audit.
- Map every requested permission to its existing service, planned packet, or unavailable reason. Preserve all requested IDs from the source; generated permission documentation should use this registry after implementation.
- Distinguish inbound Tuvima keys from outbound provider credentials. Inventory native clients, test scripts, Docker/config bootstrap, Dashboard service credential, and signed profile/Intercom assertions separately.
- Freeze changed-file ownership for P02-P05, especially Domain, Contracts, schema, DI, SettingsNav, and shared client partials. Record exact filenames before dispatch.

Acceptance: baseline log paths and results, complete endpoint/permission inventory, no runtime mutation. If SDK/feed or documentation tooling is unavailable, report the precise dependency rather than changing package versions.

## P01 — Shared contracts and registry definitions

Owner: Sol. Primary paths: new Domain authorization definitions/interfaces; `src/MediaEngine.Contracts/Authentication`; new Contracts access/application/event definition files; focused Domain/Contracts tests.

- Implement typed principal kinds, authorization decisions/reasons, feature identifiers, registry definitions, permission IDs, presets, and availability semantics.
- Specify account/grant/Application responses and all mutations consumed by later packets. DTOs contain no secret hashes or secret values. Credential creation returns plaintext once in a dedicated response.
- Define native-client binding/consent shape without changing its existing external JSON. Registry becomes the source of existing native scope definitions; avoid introducing dependency cycles from Domain to Contracts.
- Record exact interfaces for account decisions, private-resource decisions, mutation invalidation, grant PIN unlock, and audit writers. Add schema design notes for the integrator, not a parallel replacement schema.

Acceptance: foundation compiles, registry/contract tests pass, no UI or live request path switches to an incomplete implementation. Unsupported service definitions are explicitly unavailable. Frozen wire exceptions do not grow.

## P02 — Persistence, identity, and Application cutover

Owner: Sol integrator. Primary paths: `MediaEngine.Domain/Entities/Account.cs`, identity entities/contracts; `MediaEngine.Storage/Schema/schema.sql`, `DatabaseConnection`, account/identity/client repositories; `MediaEngine.Identity`; `MediaEngine.Api/Security`; account/auth/client/admin endpoints; composition root and DI. Reserve exact files from P00.

- Apply the pre-beta schema cutover; add feature/library grants, grant admin/PIN data, Application/credentials/permissions, native Application/account binding, authorization versions, and corresponding indexes/constraints.
- Bootstrap one enabled administrator account with one admin-enabled default grant. Preserve optional existing setup profile PIN semantics separately. Implement maximum-eight/default/last-administrator invariants transactionally.
- Replace human and native `Profile.Role` authority with validated account/grant context. Replace legacy inbound API-key authorization with Application credential lookup; remove the key Role store and consumers. Keep the `X-Api-Key` transport only if useful for new credentials, with no legacy reader.
- Implement account Application CRUD, permission mutations, issue/rotate/revoke, real usage/session summaries, disable/delete, account/grant mutation auditing, and revocation/version invalidation. Rotate credentials without touching permission grants.
- Implement optional grant admin unlock, rate limiting/lockout, fixed and profile-switch expiry, lock-on-leave lifecycle, and separate admin-surface enforcement. Remove mandatory session elevation when protection is off.
- Migrate local-only, passkey, external-login, setup, self-service, host recovery, and native pairing paths to the same identity. Credential/service-only principals cannot impersonate an account or acquire a default profile.
- Publish one contract/schema commit for P03-P05, then switch this worker to P03. This is a development boundary, not a completed green phase; integrate all cutover packets before acceptance or further feature work.

Tests: complete admin truth table; shared-profile/different-account behavior; disabled account; concurrent ninth grant; duplicate/default/revoke behavior; final administrator preservation; optional PIN off; correct/incorrect/locked PIN; expiry/profile switch/version change; one-time secrets; multiple credentials; app disable/revoke/expiry/rotation; delegated refresh/replay/re-pairing; fresh bootstrap and obsolete schema rejection.

## P03 — Catalogue, personal operations, and service API enforcement

Owner: Sol, continuing from P02. Primary paths: non-View endpoint families and their read/query services; client/player/stream/HLS paths; authorization metadata and route guardrails. Does not edit View-owned endpoints or Dashboard components.

- Replace old role decisions with declared request policies and real registry service permissions. Administrative APIs may admit a permitted Application without making an ordinary account a curator.
- Implement feature plus library filtering before browse, search, counts, grouping, person attribution, collections, and caches. Apply it consistently to details, artwork, reading, streams, segments, and downloads.
- Preserve profile-owned progress, reactions, favorites, queue/playlists, supported personal organization, and per-profile restrictions. Canonical edits/curated library-wide collections remain admin for humans.
- Apply native Application/consent/account/library/profile intersection on each resource. Restrict Dashboard transport-only calls; a service header cannot stand in for an interactive account session.
- Use the P00 endpoint matrix to eliminate gaps and remove role helper metadata/branches from these paths. Public exemptions must be narrow and justified.

Tests: each feature disabled; two libraries in the same lane with only one granted; mixed-media library; omitted grants; effective admin override; same account through a non-admin grant; restricted results/totals/caches; direct asset/stream/range/HLS/download denial; stale permission/token/session changes; wrong principal kinds; normal personal writes allowed and canonical writes denied. Preserve native wire/fixture tests.

## P04 — View privacy and Shared sources

Owner: Sol. Primary paths: `MediaEngine.Api/Services/View`, View endpoints including discovery, collection personal-media expansion; Domain personal-media policies/evaluator; owned View Storage and Contracts files declared in P00. UI requests go to P05.

- Replace role-based View request profiles with the P02 typed context and account View decision.
- Extend admin selection to separate exact-profile scopes. Preserve ordinary Mine/Shared options. Distinguish stale UI selection fallback from unauthorized direct requests.
- Remove generic root-sharing permissions. Keep resource-specific Gallery sharing and contribution policies with provenance checks. Audit personal-media references in Collections.
- Reuse Shared Library storage/transfer behavior; support multiple intentionally shared folder sources through the existing View/library administration model. Do not create duplicate user-facing libraries or merge personal assets into Shared.
- Reapply privacy to counts, folders/breadcrumbs, discovery/search/people/places, assets/derivatives/originals, galleries, uploads, admin source endpoints, and contribution operations.

Tests: A cannot enumerate B through any identifier surface; unauthorized/missing shapes match; B-selected admin sees no A/C assets; admin without an admin-enabled grant is ordinary; denied View even for owner; Shared reads follow View grant; specific Gallery share does not expose siblings/root; revoked share removes derivatives; upload destination cannot be spoofed; transfers preserve existing file-safety tests; no directories on read/profile registration.

## P05 — Dashboard identity and functional Access wiring

Owner: Terra. Primary paths: Dashboard identity/session/profile accessors, typed client partials, `MainLayout`, account menu, Settings shell/SettingsNav, existing UsersAccess/Accounts/Users/API-keys components, minimal profile/PIN and View scope components. Shared DTO signatures are P02-owned.

- Consume server authority/capability responses for navigation and actions. Remove Profile.Role assumptions from menus, Settings, detail edit launchers, setup, and profile switching.
- Wire real `/settings/access/users`, `/settings/access/applications`, and `/settings/access/authentication` routes. Consolidate duplicate account/profile/key pages and retire aliases according to repository policy. P06 refines their presentation; this packet must already use actual backend data.
- Implement the admin unlock entry/exit/profile-switch lifecycle and functional grant protection management using shared App controls. No mandatory setup prompt.
- Keep self-service Account & Security and profile experience settings separate. Add P04's explicit admin View scopes using typed responses; never build the list by trusting browser profile IDs.
- Adapt Web stream/artwork/View proxies to forward the authenticated session correctly and respect errors. Do not fall back to seed Owner.

Tests: server-provided navigation authority; admin eligibility versus selected grant; unlock lifecycle/profile switch; direct denied route; local-only labels; eight profiles; expired session; trusted proxy/assertion behavior. Perform minimum rendered verification for every changed surface at the cutover gate; P06 does not replace this obligation.

## P06 — Users and Applications UI

Owner: Terra. Primary paths: Access Users/Applications components and CSS, shared permission drawer if needed, dedicated typed clients and UI tests. Does not change SettingsNav or authentication/provider components during P07.

- Users table: email or truthful local-only identity, small account admin badge, compact grant profile chips, status, actual last activity, three-dot actions. No Role column.
- User drawer: Read/Watch/Listen/View and actual catalogued libraries; no Curate/Manage or other private-space grants. Explain that override applies on admin-enabled grants; preserve editable normal permissions.
- Profile management: create/grant/revoke/default/edit; at most eight; per-grant Allow admin dashboard and optional Protect admin settings. Show PIN state without exposing the hash or existing PIN.
- Applications: name/type, preset, credential count, last used, enabled state; create flow, registry-generated granular drawer, dynamic unavailable/risk states, credential create/rotate/revoke, one-time reveal.
- Use the screenshots as layout references within the current design system. Reuse the existing right-popout interaction pattern; use scrollable body, fixed/sticky footer, focus management, keyboard escape, and responsive width. Do not reuse curator-specific business logic just to obtain drawer CSS.

Tests/visual acceptance: real API success/error/conflict/loading/empty states; denied mutations; long emails; eight chips; no role tiers; changed preset becomes Custom; unavailable permission cannot be submitted; plaintext not redisplayed; keyboard and viewport matrix from plan.md. Capture each affected surface.

## P07 — Authentication policy and Authentication settings

Owner: Sol. Primary paths: AuthSettings/non-secret configuration responses, relevant account/auth endpoints, identity/provider registration, `SecurityTab` replacement, AccountSettings self-service controls, dedicated client methods/tests. P06 owns sibling Access pages.

- Turn currently informational policies into real configurable backend behavior where requested. Implement invitation expiry/creation, local/remote eligibility, sign-in toggles, session limits, and availability/readiness results.
- Configure Google, Microsoft, GitHub, Facebook, Generic OIDC with secret-safe responses and verified callback linking. Never claim configured/ready based only on the presence of a form value.
- Show canonical HTTPS/passkey readiness, recovery/SMTP readiness, test-email operation, session policy, and optional-admin-protection explanation. Test email is user-triggered; automation does not send mail as part of implementation tests.
- Preserve self-service password/passkeys/external identities/recovery/sessions and prevent last-method lockout. Expose restart-required provider changes truthfully.

Tests: each policy changes actual sign-in/session behavior; remote local-only denial; stale/replayed/unverified external identity link denied; invitation expiry/single use; trusted local-entry binding; disabled provider; configured versus ready; redacted secrets/logs; self-service ownership; no forced admin PIN. Use fake mail/provider handlers in automated tests and perform visual QA.

## P08 — Plugin host permissions

Owner: Sol. Primary paths: `MediaEngine.Plugins`, API plugin services, bundled `MediaEngine.Plugin.*` callers/manifests, plugin tests.

- Implement host-bound plugin identity wrappers and `IPluginPermissionGate` for media, HTTP, process, download, AI plus role, and plugin storage.
- Gate before side effects, including installation paths, and fail closed for unknown/disabled identities. A plugin cannot simply pass another plugin's string ID to obtain its privileges.
- Update all bundled plugins and validate manifest permissions. Preserve checksums and path containment; reject writes outside allowed plugin working/storage paths.
- Document the trusted in-process runtime limit. Do not claim hostile plugin code is sandboxed by these wrappers.

Tests: every missing permission denied; no process/network/file side effect on denial; spoofed identity; allowed operation; AI role/resource constraints; disabled plugin; tool install/execute separation; bundled-plugin regression tests.

## P09 — Plugin application services

Owner: Terra. Primary paths: new plugin service contract/descriptors, gateway/registry integration, one real bundled service adapter, Applications plugin section, tests. Sol owns any shared security-interface revision.

- Register namespaced operations and permissions; validate collisions and provenance. Mark unavailable on unload while preserving audit identity.
- Add the Tuvima-owned authenticated gateway with Application permission, plugin health/enable state, typed payload validation, limits, timeout, and cancellation.
- Expose one real supported bundled capability. Applications UI discovers permissions dynamically; no page-local permission list or arbitrary plugin-mapped routes.

Tests: permission missing/correct, unknown/unavailable plugin, healthy/disabled transitions, namespace collisions, invalid operation/payload, cancellation, no bypass route, dynamic UI registration/removal.

## P10 — Durable playback telemetry

Owner: Sol. Primary paths: Domain playback telemetry, new Storage repository, player/stream lifecycle services, read/analytics endpoints, Contracts and lifecycle events agreed with P11. Schema changes go through the integrator.

- Extend actual playback facts with account/profile/application/device/asset/library identity, start/end/duration/reason, delivery mode and observed media/client/network attributes.
- Support active sessions/history and the six requested read/analytics permission families. Keep other-session control separately permissioned.
- Define stale-session closure, retention, idempotency, session versus unique-user counting, and seek versus played time. Do not reconstruct this data from ingestion Activity.

Tests: start/pause/resume/seek/complete/crash; duplicate/out-of-order heartbeats; multi-device context; direct/remux/transcode facts; missing metadata; aggregates within authorized scope; no history leakage without permission; retention behavior. Publish real playback lifecycle events for P11.

## P11 — Filtered real-time Application events

Owner: Sol. Primary paths: event registry/envelope, external transport, SignalR publishers and connection/subscription authorization, event tests. Coordinate only the lifecycle contract with P10.

- Add the exact requested event envelope and event-family definitions. Implement backed library, ingestion, metadata, provider, plugin, health, and playback events; unsupported families remain unavailable until their producer is integrated.
- Require `events.subscribe` plus resource-read permission on subscription and dispatch. Bound queues/backpressure; define ordering, reconnect cursor/gap behavior, and payload versioning.
- Prevent external apps connecting to the raw internal Intercom stream. Inspect the Dashboard's existing `Clients.All` publishers and apply audience restrictions where account permissions require them, preserving the frozen wire pair.
- Apply revocation/current authority to open connections and avoid broadcasting payloads before filtering them.

Tests: no subscription permission, missing underlying permission, wrong account/profile/library, admin separate View scope, revoked app/token/grant while connected, mixed subscribers, reconnect/gaps, supported event facts, absence of unfiltered external payloads. Playback acceptance waits for P10.

## P12 — Webhooks

Owner: Terra. Primary paths: new delivery repository/service, application webhook endpoints/contracts/UI, signing/retry tests. Schema and shared policy changes via Sol integrator.

- Persist URL/categories/enabled/signing-secret reference/failure state; reuse P11's envelope and authorization decision.
- Implement signed timestamped payloads, stable delivery IDs, bounded retries/backoff and timeout, replay documentation, destination validation, redacted logs, and current-permission recheck on every send/retry.
- Provide real application webhook management and failure status. Distinguish failed delivery from unauthorized/skipped delivery.

Tests: raw-body signature, rotation, repeated delivery ID, bounded retry/timeout, revoked grant before retry, disabled app, approved LAN endpoint, unsafe destination/redirect/DNS change denied, secret redaction. Use local fake receivers rather than sending to real third parties. Perform UI verification.

## P13 — Final acceptance and collapse preparation

Owner: Sol integrator. Terra may write docs from verified facts in parallel after code freeze; Astra reviews the concise evidence and unresolved decisions.

- Complete the supplied security matrix and the plan's extra concurrency, cache, stream, identity-linking, principal-kind, and original-file tests. Replace stale role-based test expectations with behavior assertions; do not merely delete them.
- Enumerate mapped endpoints again and reconcile every permission with a real service or truthful unavailability. Check production uses of Profile.Role/AppRoles/ApiKey.Role, old user-facing roles/routes, blanket Dashboard client-scope bypasses, and unfiltered external events.
- Run required solution, CI, native-client, docs, and relevant packaging gates. Record exact commit, commands, result/log paths, screenshots, and any unavailable environmental checks. Unrun checks remain open acceptance items.
- Update architecture and product docs plus README/AGENTS/CLAUDE and synchronized `.agent` files. Include destructive state cutover and re-pairing instructions that protect originals.
- Prepare the complete squash diff/PR description and product-owner summary. Follow plan.md's merge protocol; no worker independently merges to main or deletes worktrees.

Acceptance: every source phase accounted for, no failing required gate, no placeholder enabled UI, no unauthorized original-file operations, and final behavior visually verified. A partial A/B delivery is explicitly partial if C remains unfinished.

## Copyable dispatch brief

```text
Implement packet <Pxx> from docs/plans/access-architecture-2026-09-08/workers.md.
Model: <gpt-5.6-sol or gpt-5.6-terra>, reasoning <medium/high>.
Your worktree: <absolute path>. Your branch: <codex/access-...>.
Base integration commit: <SHA>.
Read AGENTS.md in that worktree, this packet, the decision sections of plan.md,
and only the inventory entries/source files relevant to the packet.
Owned files: <explicit list>. Reserved shared files: <explicit list>.
Do not edit reserved files; send the integrator the smallest required change.
Use the assigned worktree explicitly for all commands. Do not spawn agents,
change the shared root checkout, merge to main, or access production runtime data.
Implement the full packet, add behavior tests, and run its focused checks.
No legacy fallback, fake UI, caller-supplied authority, or original-file changes.
Commit owned changes. Return at most ~350 words: SHA, changed paths, decisions,
tests and results/log paths, schema requests, remaining dependencies, and a
plain-English product-owner summary. Escalate a contract conflict or two failed
attempts with concrete evidence instead of independently redesigning the plan.
```

Use this brief with an explicit model and `fork_turns="none"` on the collaboration spawn tool. Do not create user-owned sidebar tasks for these internal workers. Worktree creation is a separate, necessary orchestration step.

## Product-owner summary

The workers have separate jobs and clear checks. They first establish who can access what, then build the Access screens, then add plugin and monitoring integrations. Their changes are combined and verified before the finished result is collapsed into main.
