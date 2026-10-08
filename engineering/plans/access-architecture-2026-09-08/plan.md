# Access architecture: review and implementation plan

Status: P00–P13 implementation and acceptance are complete and have been collapsed into `main` as one reviewed change. The combined build, formatting, tests, and coverage gates pass: 3,632 tests passed, 37 existing provider tests skipped, zero failed. Isolated browser acceptance also passed for account/profile lifecycle, grants, administrator PIN protection, service applications, replaceable credentials, and signed webhooks. The normal development database has not been reset or cut over. See [execution status](./execution/status.md) for evidence and the [cutover procedure](./execution/cutover.md) for replacement-runtime startup requirements.

Reviewed on 2026-09-08 against `main` at `3af3ae3cf57dac270dd99b35a408af48e6c2b069`. Recheck the base before starting workers.

## Resumed next steps — 2026-09-08

Execution resumed after the product owner's “proceed.” The clean integration and identity worktrees start at `1b75af4e76ad4b4afc9ce23769877566f6ec10e2` under `.tmp/access-integration` and `.tmp/access-identity`, on `codex/access-integration` and `codex/access-identity`. Pending ingestion changes remain in the original checkout and must be incorporated and verified before the security cutover is accepted. Foundation definitions may proceed independently while runtime visual verification waits for administrator sign-in. See [execution status](./execution/status.md) for evidence and open gates.

The product owner restored this plan to the next-work sequence and then authorized implementation. The sequence below is retained as the delivery contract; current completion evidence and remaining gates are maintained in the execution status.

1. Finish verification of the pending ingestion changes, including rendered card/list alignment, scrolling child lists, duration formatting, and runtime stability. Confirm the crash cause and regression protection with evidence; a successful restart alone does not establish that recurrence is prevented. Preserve and review the existing changes before freezing an Access baseline.
2. Recheck the repository and refresh the inventory against the accepted baseline. At this resumption, `main` is `1b75af4e76ad4b4afc9ce23769877566f6ec10e2`, with uncommitted ingestion changes and existing unrelated worktrees. Neither that dirty state nor the original review SHA is a frozen worker base.
3. Begin with **P00 baseline/inventory**, then **P01 shared contracts**, then **P02 identity and persistence**, using the exact packets in [workers.md](./workers.md). Astra owns planning and contract decisions; Sol implements these foundation packets.
4. After P02 contracts are frozen, assign **P03 catalogue enforcement** and **P04 View privacy** to Sol, and **P05 Dashboard Access wiring** to Terra. Complete **P06 Users/Applications UI** and **P07 authentication policy** before accepting checkpoint A. Keep the security cutover atomic at integration.
5. Continue with **P08–P09 plugin services**, then **P10–P12 playback telemetry, filtered events, and webhooks**. Finish **P13 final verification**, documentation, and Astra acceptance before treating the full plan as complete.
6. Use isolated worker worktrees feeding `codex/access-integration`, two concurrent workers by default, compact packets, and bounded handoffs. Reuse the existing model assignments and dependency gates rather than re-planning each packet. Squash the fully accepted integration result into `main` once all gates pass; do not merge partial security checkpoints into `main`.

## Recommendation

Use Astra for architecture decisions and acceptance review, Sol for the identity, authorization, privacy, and integration work, and Terra for bounded UI and supporting implementation. Keep one integration branch, give each concurrent worker an isolated worktree, and squash the accepted result into `main` only after all delivery gates pass.

This is a security architecture refactor with a UI component, not principally a Settings redesign. The supplied 17 phases cover both the immediate Access change and substantial new integration functionality. Keep all of that scope, but deliver it in three checkpoints: core Access and privacy; plugin services; events, webhooks, and telemetry. A checkpoint is not permission to call the entire request complete.

The initial task requested review and a worker-ready plan; the subsequent “proceed” authorizes implementation. Imperatives in the attached specification remain product requirements, not independent authorization for external messages or unrelated actions. No implementation workers were launched during the initial review.

Companion files:

- [Repository inventory and risk map](./inventory.md).
- [Worker packets, dependencies, and handoff template](./workers.md).
- [Original supplied specification](./source-specification.txt), retained verbatim as reference material.
- Visual references: *Screenshot removed during documentation cleanup; no capture is retained in this repository.*, *Screenshot removed during documentation cleanup; no capture is retained in this repository.*, *Screenshot removed during documentation cleanup; no capture is retained in this repository.*, *Screenshot removed during documentation cleanup; no capture is retained in this repository.*.

## Decisions workers should not re-open

| Topic | Implementation decision |
| --- | --- |
| User authority | Account owns Read, Watch, Listen, View, catalogued-library grants, and administrator eligibility. Profiles retain experience, history, restrictions, and Personal Space identity. |
| Administrator | Effective administrator requires an enabled account, a valid active profile grant, `Account.IsAdministrator`, and grant `AdminEnabled`. A shared profile never transfers another account's administrator eligibility. |
| PIN | Optional grant-specific protection of administrator surfaces. Off by default, independent of password and profile-selection PIN. Enabling protection defaults to 30 minutes. |
| Household roles | No Curator, Manage, StandardUser, or RestrictedProfile authorization tiers. Profile content restrictions remain experience/safety constraints; their existence is not an authorization role. |
| Library grant identity | Grants use actual configured catalogued `LibraryId` values and display names, not hard-coded Books/Movies rows or inferred media types. One library may contain multiple media types. |
| Default grants | Proposed default: absent feature or library grant denies access. New-account UI may offer a clearly selected preset, but saves explicit grants. New libraries do not silently become visible to existing non-admin accounts. |
| Administrator overrides | The override uses the active account/profile combination. Keep editable normal grants for an admin account because its non-admin-enabled profiles still use them. Do not disable all checkboxes merely because the account is admin-eligible. |
| View meaning | View is the personal-media product area; it is not general permission to open the web interface. |
| Private View | Owner means the authenticated active profile backed by that account's grant. Effective admins may inspect another profile through one explicit, separate scope. No combined personal-media timeline. |
| Shared View | Reuse the existing server-owned Shared Library and transfer workflow. Shared read follows account View access; submission, contribution review, and intentional Gallery sharing remain separate policies. |
| Gallery sharing | Keep intentional, resource-specific Gallery shares. A share may authorize the Gallery and proven members/derivatives, never root, folder, sibling asset, or general Personal Space access. View must still be enabled. |
| Applications | Permissions belong to an Application, not its credentials. Multiple hashed, revocable credentials resolve the same live Application identity and grants. |
| Native clients | Preserve public route and wire shapes where practical. Re-pairing after the pre-beta cutover is acceptable; preserving obsolete token records or role semantics is not required. |
| Plugin authority | Host capabilities and externally consumable service permissions are separate. Plugin identity comes from a host-bound execution context, not a freely supplied plugin ID alone. |
| Unsupported functions | Permission IDs may be registered as unavailable with a reason. UI must not offer an enabled, working-looking control before its backend and authorization exist. |
| Pre-beta state | Follow AGENTS.md: destructive schema/config cutover with fail-fast obsolete-state detection. Do not implement the attachment's suggested legacy-key conversion or permanent dual schemas. Protect originals and existing source files. |

These are proposed defaults where the specification leaves mechanics open; the product owner can change them before execution. They provide a complete starting contract without requiring each worker to ask the same questions.

The screenshots supply spacing, hierarchy, tables, chips, menus, and right drawers. Their Curate/Manage columns, app lane badges, statement that profiles carry permissions, and description of View are superseded by the written target. Follow the existing Settings shell, typography, shared App controls, icons, and responsive behavior rather than copying the screenshot sidebar or broken glyphs.

## Target contracts

### Identity and persistence

Extend `Account` with `IsAdministrator` and a monotonically changing authorization version. Extend `AccountProfileGrant` with `AdminEnabled`, PIN protection state, unlock policy, and its own protection version. Keep account password/passkeys/external identities in the current account security system. Store the admin PIN hash and failed-attempt/lockout state in a grant-keyed credential record; never serialize them into a response.

Add account feature grants and catalogued-library grants with unique compound keys. Enforce the eight-profile maximum, valid default grant, non-admin rejection of `AdminEnabled`, and default reassignment inside transactions. Count distinct granted profiles, including existing-profile grants and invitations, with concurrency tests. Protect the last usable administrator account/grant from accidental removal; retain the host recovery path.

Local-only accounts have no invented email. The Users table shows a truthful local-only identity and badge when email is absent. Proposed target permits up to eight grants for local-only accounts as well. The existing local-entry/profile-PIN path must explicitly resolve one trusted local account before presenting its grants; never pick an arbitrary account by profile ID when several grants exist. Local-only authentication remains unavailable to remote sessions. Granting an existing profile is administrator-managed because it deliberately shares that profile's history and private storage identity.

Account or grant deletion revokes authority; it must not delete user-owned original files. Personal-space label reservations must survive deletion. Define the orphaned-space administration behavior without silently reassigning ownership.

Add `Application`, `ApplicationCredential`, and `ApplicationPermissionGrant`. Link native client devices/pairings/token families to Application, Account, and approved Profile context. Credentials carry identity and lifecycle only. Native delegated consent limits may narrow authority but must never become a replacement Application permission store.

Keep domain/policy types in Domain or Identity, storage implementations in Storage, and HTTP/SignalR DTOs in Contracts with explicit mappers. The schema owner alone edits bootstrap/schema/version checks and shared repository contracts during an integration wave.

### One request authority

Use a typed, server-resolved request context containing principal kind, account ID, active profile ID, grant, application ID where applicable, session/device binding, effective administrator state, and authorization version. Define distinct human, delegated user-client, service-application, and narrowly scoped host/dashboard transport identities. Do not manufacture an Owner profile for a service credential.

Authorization rules:

```text
Human catalogue read:
  authenticated enabled account + current valid profile grant
  AND (effective administrator OR (feature grant AND catalogued-library grant))
  AND applicable profile restrictions/resource rules

Delegated client:
  enabled Application + valid credential/token family
  AND live Application permission
  AND delegated consent limit
  AND current enabled account/profile authority
  AND the same feature/library/private-resource rules as the human

Service integration:
  enabled Application + valid credential
  AND live available service permission (or explicit admin Application override)
  AND operation-specific resource restrictions
```

An admin Application overrides permissions only for available registered services, not disabled plugins or absent implementations. It may inspect private View without a user binding as explicitly allowed by the specification, but must select one profile scope per request and be audited. An ordinary Application with `view.personal.read` must have a validated account/profile binding. User-client Applications always retain the human intersection, even if the Application has broad privileges.

Keep a separate administrator-surface requirement:

```text
effective administrator
AND (grant PIN protection disabled OR valid unlock for this session/account/profile/grant version)
```

Normal content override is independent of an administrator-settings unlock. Leaving settings can revoke a lock-on-leave unlock; fixed expiries are enforced by the server; all modes clear on profile switch, sign-out, grant revocation, or protection change. A browser flag alone cannot unlock an API. The exit signal for lock-on-leave needs a server operation and a bounded lease for crashes/disconnects. This PIN is a convenience protection within an authenticated session, not a sandbox against a compromised browser.

Keep `RequireAdmin` only as a clearly named helper for this new policy if useful. Remove role-based decisions and fallback role state. Classify endpoints as public, authenticated self-service, human/admin, delegated client, or service permission. Do not grant service applications `/accounts/me` or profile preferences by treating them as ordinary authenticated people.

Authorize before counts, grouping, pagination, search suggestions, artwork lookup, detail assembly, and caching. Filtering a final response is insufficient. Cache keys must include relevant account/profile/application authority or a safely reusable permission projection. Stream, range, HLS manifest/segment, download, artwork, and Web proxy paths must reapply the same resource decisions. Specify a bounded revocation interval for already-running streams; do not leave issued URLs valid indefinitely after revocation.

### Permission registry

Use immutable definitions with ID, category, name, description, risk, allowed application types, user-context requirement, built-in/plugin provenance, sort order, availability, and unavailable reason. The provided core catalogue is the minimum registry inventory. Preserve its existing ten native-client permission IDs.

Add typed endpoint metadata and a common evaluator. Definitions inform the UI, API guards, pairing consent, audit diagnostics, and generated documentation. Presets are editable initial selections, not stored authorization roles. Any manual change after applying a preset displays Custom. Administrator Application is an explicit sensitive capability, includes all currently available registered permissions, and continues to include newly available registered permissions; its UI warning must explain that behavior.

Permission availability requires a real endpoint/service, policy mapping, and tests. Examples needing new work include durable analytics, external events, and webhooks. Map existing functionality first; do not implement every advertised scope as a new subsystem inside the registry packet. Plugin IDs must be namespace-validated, collision-free, and incapable of overriding built-ins. On unload retain an identifiable definition/history record but mark the service unavailable.

Authentication failure is 401; a valid principal missing an application service permission is 403. After the outer authentication/permission checks, private resource denials use the same not-found body/status as missing resources. Do not accidentally turn unavailable resource identifiers into an existence oracle.

### View boundaries

Reuse `ViewScopeResolver`, resource authorization, Personal Spaces, Shared Library contributions, and managed storage labels. Replace profile-role inputs with the typed authority. Add admin profile options one at a time; bind queries to the selected profile's exact Personal Space library. Shared is a server-owned scope, not a union of personal libraries.

Remove `AuthorizedProfileIds`/household visibility as ways to browse private roots. Retain intentional Gallery membership checks and contribution policies. Keep Mine/Shared fallback only for harmless stale saved UI selection. Direct requests for unauthorized profile/library/resource IDs must fail not-found-equivalently, rather than silently query Mine and return a misleading success.

Cover folders and breadcrumbs, search/people/places, thumbnails, originals, upload destinations, gallery members, contribution/retry paths, collection-linked Gallery/rule expansion, and the Dashboard proxy. Admin read access to other profiles does not imply arbitrary destructive original-file operations; mutations still obey ownership and the shared filesystem mutation gate.

### Authentication settings

Reuse the existing account identity service, passkey store, recovery code/token flows, SMTP delivery, OAuth/OIDC registration, and sessions. Add real persisted server policy and readiness responses for the controls that currently have no configurable backend.

Expose local password, passkeys, remote policy, invitations/expiry, local-only accounts, session policy, canonical origin, provider callback readiness, and recovery delivery. Provider secret values are write-only; responses report configured state. Google/Microsoft/Generic OIDC and GitHub/Facebook OAuth use verified provider configuration, not invented protocol mappings. Report restart-required settings truthfully if runtime handler reload is not implemented. Provider tests need no real external account or outbound email; live tests are optional explicit acceptance work.

Keep Account & Security as self-service, separate from administrator Access. External identity linking must derive issuer/subject from a verified authentication callback and link transaction, never accept arbitrary browser-supplied identity claims as proof. Prevent disabling the last usable sign-in/recovery path. Admin grant PIN controls belong to profile/grant management, not global Authentication.

### Plugins and integration expansion

Bind HTTP, process, tool download, AI role, media, and private-storage wrappers to a host-created plugin execution identity. Gate before side effects. Update bundled plugin callers atomically with interface changes. Manifest checks govern privileged host APIs; in-process .NET plugins remain trusted code and these checks do not provide OS-level isolation from malicious code using framework APIs directly. An untrusted-plugin sandbox would be separate scope.

Expose plugin application operations only through a Tuvima-owned gateway with registered operation descriptors, request/response contracts, permission checks, health/enable checks, cancellation, and bounded payloads. Demonstrate it with one real existing plugin capability, not a fake endpoint.

For events, define the supplied versioned envelope and an event-to-resource/read-permission map. Require `events.subscribe` plus the underlying read permission and resource scope for every delivery. Recheck current grants on subscriptions and queued dispatch; revocations must affect open connections. Keep raw internal Intercom broadcasts inaccessible to external applications. Also examine existing Dashboard broadcasts for ordinary-account data leakage instead of assuming a first-party connection is automatically entitled to all operations.

Durable telemetry attaches to actual player lifecycle/heartbeats and stream delivery decisions, not ingestion Activity. Persist the requested account/profile/application/device/media linkage and playback/delivery facts. Handle repeated heartbeats, crash/stale sessions, seek versus played time, resume, and retention. Use truthful unknown values for metrics the pipeline cannot observe. Event publication for playback depends on these lifecycle facts, so telemetry precedes playback event acceptance.

Webhooks reuse exactly the event authorization/filtering path. Use signed raw-body payloads, timestamps, stable event/delivery IDs, bounded retries/timeouts, and persistent failure state. Recheck permission at send time, including retries. Validate destinations to prevent credentials or private media reaching unintended hosts: allow explicitly configured LAN automation destinations, reject metadata/link-local service targets, revalidate DNS destinations, and do not follow redirects into unapproved hosts. Keep webhook signing secrets separate from application credentials and out of DTOs/logs.

## Execution and cost control

Use current callable IDs `gpt-6-astra`, `gpt-5.6-sol`, and `gpt-5.6-terra`. The session supports these workers; [official model guidance](https://developers.openai.com/api/docs/models/compare) describes their capability/cost positioning. This plan uses Terra as the default for bounded implementation and Sol where rework from an authorization mistake would be expensive. Model choice alone does not guarantee fewer tokens or predict subscription usage.

- Astra reviews the plan, resolves cross-cutting decisions, and reviews delivery gates. It does not duplicate routine worker implementation or independently reread the entire repository after every packet.
- Sol owns the integration worktree and final integration fixes. Use high reasoning for identity/privacy; medium for wiring with settled contracts.
- Terra uses medium reasoning for UI, concrete service adapters, and documentation; low for mechanical cleanup only after the target is established.
- At most three workers run alongside the coordinator. Use two by default. Expand to three only for independent file sets; idle workers do not save tokens.
- Spawn workers with `fork_turns="none"`, an explicit model, an assigned worktree, the relevant packet, decisions, and acceptance checks. The tool's default full-history fork must not be used for mixed-model workers.
- Workers do not spawn more workers. Reuse a worker for the next related packet, but reset context when unrelated accumulated history becomes expensive.
- Workers read their named files and dependencies first. They receive the whole source specification only when a disputed detail requires it. UI workers alone need screenshots.
- Return changed paths, commit SHA, concise decisions, tests run/results, unresolved dependencies, and a product-owner summary. Do not paste patches or complete logs into the coordinator conversation.
- Use focused tests while iterating, then required solution gates on an integrated checkpoint. Avoid three concurrent full-solution test runs. Investigate failures once and assign one owner.
- Escalate to Astra after two failed attempts or an ownership/architecture conflict; report the precise issue. Do not spend repeated Sol/Terra turns re-planning the system.

There is no explicit numerical token budget in the request, so none is invented or configured. Track actual usage where available and reassess at each checkpoint.

## Worktree and merge protocol

1. Recheck `git status`, current `main` SHA, and active worktrees. Preserve unrelated work and the existing detached worktrees. Stop only this repository's running Engine/Web processes before implementation builds, as required by AGENTS.md.
2. Commit the accepted plan and reference assets as a planning change, then create `codex/access-integration` from that base. Do not change branches in a working directory shared with active workers.
3. Create new worker branches such as `codex/access-identity`, `codex/access-view`, and `codex/access-ui` in fresh worktrees from the recorded integration SHA. Pre-provision writable paths and branch/worktree operations before dispatch. Collaboration agents share the original working directory by default; spawning an agent is not worktree isolation. Every worker command must explicitly select its assigned worktree.
4. One Sol integrator owns `schema.sql`, schema version/startup cutover, shared Domain/Contracts signatures, project/package files, `Program.cs`, and DI during a wave. Settings navigation is reserved to P05 during its packet, then returned to the integrator. Other workers propose changes to reserved files through a small integration request. Allocate new uniquely owned files where practical.
5. Workers commit only their packet files on their own branches. The integrator cherry-picks accepted commits in dependency order. Do not both merge and cherry-pick the same work. If shared contracts change, stop dependent work, update integration, and provide the new base once.
6. Security cutover packets form one acceptance checkpoint: no runnable integrated checkpoint with old persisted roles/API-key authorization coexisting as fallback. Private worker commits may be incomplete, but cannot be advertised as completed phases. Run the build and behavior gates before advancing the shared checkpoint.
7. Keep `main` unchanged until the complete agreed scope passes the final gates. If `main` advanced, incorporate it into the integration branch and rerun affected checks plus final required gates. Never reset or force-push `main` to make integration easier.
8. Collapse accepted worker history into one squash change from `codex/access-integration` to `main` (prefer a reviewable PR if that is the repository's normal workflow). The final diff includes implementation, tests, docs, and cutover instructions; it excludes worker runtime data and scratch logs. This planning turn does not perform that merge or publish anything.
9. After merge verification, remove only the new clean worktrees created for this effort, and only after their commits are safely reachable in the integration branch/history. Do not touch pre-existing worktrees. Retain the integration branch until the product owner is satisfied with acceptance evidence.

## Delivery gates

The [worker schedule](./workers.md) maps all source phases to three delivery checkpoints. UI must consume working APIs; do not publish placeholders for later functionality.

Each integrated checkpoint must pass:

```powershell
dotnet restore MediaEngine.slnx
dotnet build MediaEngine.slnx --no-restore
dotnet test MediaEngine.slnx --no-build
```

Before integration, the owner runs the changed projects and focused tests. Before final merge, also run the repository CI equivalents: warnings-as-errors build, formatting verification, non-live coverage gate, and vulnerable dependency check. Run native-client scripts when those contracts are touched. Update package feeds only if evidence requires it; a missing local Tuvima.Wikidata feed is not permission to replace the package architecture.

Add behavior tests for the full supplied security matrix and the additional cases in each worker packet. Strengthen route coverage with actual mapped endpoint metadata (including nested groups and public exemptions), plus real authorization request tests. Existing regex checks alone cannot prove the correct policy ran.

Validate clean bootstrap, obsolete-schema failure, re-pairing, host recovery, and no filesystem change to existing originals. Use isolated disposable configuration/database paths, disable watchers/AI downloads for authorization fixtures, and do not point parallel runtimes at the real View root. A database reset with existing Personal Space directories must not let newly generated profiles claim old directories; stop and resolve reservations before any write.

For every changed Dashboard surface, inspect 1920x1080, a shorter desktop viewport, and a narrow/mobile viewport. Cover Users, Applications, Authentication, all drawers and PIN states, account menu, profile switch, and admin View scopes. Test keyboard opening/closing, focus return, scroll containment, sticky drawer actions, error/loading/empty states, long emails, eight chips, and truthful local-only labels. Capture actual rendered evidence; screenshots in this folder are design references, not QA results.

Update security/account-profile/plugin/API/Access/View docs, README, AGENTS.md, CLAUDE.md, and relevant `.agent` guidance as implemented behavior changes. Run the docs context generator and strict docs build in the final documentation packet. Do not rewrite current architecture documents to pretend this proposal is already implemented.

## Planning verification and limits

This review inspected the source boundaries and tests listed in the inventory and preserved all four supplied images. It did not run application code, live provider authentication, security exploits, or runtime UI verification. Source searches identify migration candidates, not proof that every match is an active request path. The implementation's baseline packet must enumerate mapped routes and supported registry entries before switching authority.

Planning artifact checks: all local Markdown links resolve, the retained specification/images match their original file hashes, and the added Markdown passes whitespace checks. A full documentation build was not run because MkDocs is absent from the available Python runtime. No .NET tests were run for this documentation-only change.

## Product-owner summary

People will sign in as users, choose profiles, and receive one consistent set of account permissions. Administrators can choose which profiles expose settings and optionally protect those settings with a PIN. Private View libraries stay separate. External applications receive explicit service access with replaceable credentials. Workers build these changes in controlled stages, and only the verified combined result returns to main.
