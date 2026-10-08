---
title: "View Shared Library Contributions"
summary: "Replace family-specific terminology and add a private, reviewable workflow for promoting selected personal originals into the server-owned Shared Library."
audience: "developer"
category: "proposals"
product_area: "view"
---

# View Shared Library contributions

Status: implemented September 8, 2026. This plan supersedes the Family Library terminology and contribution details in earlier View proposals. The runtime now uses Shared Library terminology, explicit access/submit/review grants, durable contribution and event records, asynchronous verified transfers, recovery, and contribution breadcrumbs.

## Product decision

Use **Shared Library**, not Family Library. A Tuvima server may belong to a family, club, team, school, studio, archive, or other group. Shared Library describes the durable ownership and visibility boundary without assuming who uses the server.

Use these terms consistently:

| Concept | Product label | Meaning |
| --- | --- | --- |
| Profile-owned storage | Personal Space | Private media and sources owned by one stable profile. |
| Server-owned storage | Shared Library | Accepted originals owned by the server community and stored beneath `View/Shared`. |
| Visibility reference | Shared with me | A Gallery or resource another profile has permitted the viewer to access; ownership and files remain unchanged. |
| Candidate action | Submit to Shared Library | Ask an authorized reviewer to accept selected personal items into shared ownership. |
| Direct privileged action | Add to Shared Library | A Shared Library curator promotes an item they are authorized to manage while creating the same audit record as an accepted submission. |
| Submitter permission | Can submit | May submit owned items but cannot review or accept them. |
| Review permission | Shared Library curator | May review only submitted items and accept or decline them. This is a View-specific grant, not a new global profile role. |

Internal and user-facing names should use `shared`, `shared_library`, `contribution`, `contributor`, and `curator`. Remove `family`, `household`, and family-shaped ownership names from this feature during the pre-beta cutover. Unrelated family relationships in universe data remain unchanged.

The Shared Library is one server-level resource in the first release. It must not be represented by a fake profile or login. Its stable internal identity must not depend on the current administrator, server display name, or physical root path.

## Boundary between sharing and ownership

The Shared Library contains only items deliberately accepted into server ownership or directly imported there by an authorized curator. It is not an automatic union of personal profiles.

- **Gallery sharing** grants access to a specific Gallery and its authorized assets. It does not move files or change ownership.
- **Shared with me** presents those references. It does not imply that an original is stored under `Shared`.
- **Shared Library contribution** changes durable ownership after review and invokes the physical transfer policy.
- **Direct Shared import** writes to Shared storage from the start and records shared ownership without a personal contribution.
- **Profile access to Shared Library** controls browsing. **Can submit** and **Shared Library curator** are separate grants.

Retire the current blanket `Include in Shared View` behavior as part of the cutover. A profile-level switch must never turn every eligible personal item into Shared Library content. Existing disposable pre-beta policy state may be reset rather than migrated.

## User journeys

### Contributor

1. In Mine, select one or more owned assets and choose **Submit to Shared Library**.
2. Preview every selected logical item, compound-file count, total bytes, proposed Shared destination, and the eventual result: managed originals move after verification; linked read-only originals copy and remain in place.
3. Choose Shared Timeline or a named Shared folder, add an optional note, and submit.
4. The submission immediately appears in **My submissions** as Pending. No file, ownership, Gallery membership, or Timeline placement changes while it waits.
5. The contributor may cancel a Pending submission. They may resubmit a Declined or Cancelled item as a new revision.
6. Accepted submissions show transfer progress and the final Shared location. After completion, shared ownership is independent of the contributor's account or Personal Space.

### Shared Library curator

1. Open **Contributions** from the Shared Library scope. A pending count appears only for profiles with the curator grant.
2. Review a paged queue containing only explicitly submitted items. The preview may expose the selected asset, submitted compound members, contributor identity, source mode, proposed destination, byte count, capture time, and note. It grants no access to sibling files, the source folder, or the rest of the contributor's Personal Space.
3. Accept, decline, or adjust the Shared destination before acceptance. A changed destination is shown in the confirmation.
4. Acceptance records the decision before starting the physical transfer. Each item reports its own transfer state so one unavailable file does not hide successful items in the same batch.
5. A declined contribution leaves every original and personal reference unchanged. The curator may add a concise reason visible to the contributor.
6. A curator may use **Add to Shared Library** on an item they already have management authority over. This creates an auto-approved contribution record and uses the same transfer engine.

Curators may review their own submission, but the record must identify that the submitter and reviewer are the same. Installations that want separation of duties can grant submission and review to different profiles; this is policy rather than a hard-coded family rule.

## View placement and navigation

Keep the primary View navigation as Photos, Folders, Galleries, People, and Places. Contributions is a workflow surface within Shared Library scope, not a sixth media destination.

- `/view/contributions` uses one page with **My submissions** and, when authorized, **Review** tabs.
- `/view/contributions/{id}` opens a submission detail surface with its item list, decision, transfer state, and audit timeline.
- Shared Library Photos, Folders, and Galleries show only completed shared items. Pending, declined, failed, or partially transferred items never appear in ordinary Shared Library browsing.
- The Shared Library header exposes **Contributions** with a pending badge for curators and an active-submission badge for contributors when either count is nonzero.
- Preserve breadcrumbs: `View > Shared Library > Contributions` and `View > Shared Library > Contributions > <submission label>`. On mobile, breadcrumbs may wrap but must retain all ancestors and Back/Forward behavior.

Use **Shared Library** in the scope selector. Use **Shared with me** only for reference-based Gallery access. Avoid the ambiguous label **Shared View**.

## Permissions

Add independent administrator-managed View grants:

- `view_enabled`
- `access_shared_library`
- `submit_to_shared_library`
- `review_shared_library_contributions`
- `allow_gallery_sharing`

The global profile role continues to control system administration. Shared Library curator is a resource grant and must not grant metadata administration, settings access, private-profile browsing, source management, or file deletion outside accepted contribution execution.

Authorization rules:

1. Only the current owner of an authorized personal item may submit it. Gallery access or duplicate-content knowledge is insufficient.
2. Submission creates a narrowly scoped review grant for the selected logical items and their required compound members.
3. Curators can read a submission only while authorized to review the Shared Library. Revocation takes effect on the next request.
4. Acceptance revalidates contributor ownership, source authority, file fingerprints, destination containment, free space, and current policy. A stale preview cannot authorize a move.
5. Curator access does not reveal unsubmitted filenames, folder names, thumbnails, duplicate existence, or profile counts.
6. Direct Shared imports and direct curator additions require the curator grant; changing Shared Library access alone is insufficient.
7. Shared original deletion is a separate administrator or Shared Library management operation with its own impact preview. It is never implied by declining, cancelling, removing Gallery membership, or deleting a contributor account.

## Persistence model

Use a clean pre-beta schema cutover:

- Rename `view_family_assets` to `view_shared_assets` and family-oriented model/service/contract names to Shared Library terminology.
- Keep `view_shared_transfers` as the physical transfer journal, but make it reference a contribution item when the transfer began through this workflow.
- Add `view_shared_contributions` for the submission envelope: stable ID, contributor profile, status, destination intent, optional note, revision, submitted/decided timestamps, reviewer profile, and decision reason.
- Add `view_shared_contribution_items` for ordered logical items: contribution ID, item ID, submission-time owner, source mode, manifest/fingerprint snapshot, item status, transfer ID, and error/retry state.
- Add `view_shared_contribution_events` for append-only submit, cancel, accept, decline, transfer, recovery, and administrative events.
- Replace ambiguous profile policy columns with the explicit grants above.

Contribution decision state and physical transfer state must remain separate. Recommended decision states are `pending`, `accepted`, `declined`, and `cancelled`. Recommended item execution states are `waiting`, `transferring`, `completed`, `cleanup_pending`, `needs_attention`, and `failed`. An accepted batch may therefore report partial execution honestly without changing its recorded decision.

Use optimistic revision checks for accept, decline, cancel, destination edits, and retries. Add idempotency keys to submission and decision commands. Prevent two active submissions for the same owner/item/destination intent while allowing a new revision after decline or cancellation.

Retain original contributor and source provenance after promotion even if the profile is later removed. Profile deletion must not cascade shared ownership or completed contribution history.

## API contract

Replace the current family-named per-item routes and types; do not add aliases:

| Method | Route | Purpose |
| --- | --- | --- |
| POST | `/view/shared/contributions/preview` | Preview a batch, compound members, move/copy outcomes, conflicts, and destination paths. |
| POST | `/view/shared/contributions` | Submit an idempotent batch using the preview revision. |
| GET | `/view/shared/contributions` | Return only the caller's submissions, or the review queue when `mode=review` and authorized. Cursor-page and filter by decision/execution state. |
| GET | `/view/shared/contributions/{id}` | Return the authorized submission, item states, decision, and audit events. |
| POST | `/view/shared/contributions/{id}/cancel` | Cancel a still-Pending caller-owned submission using its revision. |
| POST | `/view/shared/contributions/{id}/decision` | Curator accept/decline command with expected revision, optional reason, and confirmed destination. |
| POST | `/view/shared/contributions/{id}/retry` | Retry only items in a recoverable execution state after revalidation. |
| POST | `/view/shared/items/direct` | Curator direct-add path that creates and accepts the same durable contribution record. |

Commands return the persisted submission representation. Do not make the Dashboard loop over item endpoints to simulate a batch. Long transfers return accepted work state and update through the existing activity/SignalR infrastructure; HTTP completion must not pretend filesystem work finished.

Use not-found parity for missing and unauthorized contribution IDs. The first contribution detail implementation presents indexed item identity and transfer state without exposing original paths or private sibling assets; contribution-scoped thumbnail/original grants remain a later enhancement.

## Physical transfer and recovery

Reuse the implemented verified Shared transfer semantics after renaming them:

1. Persist the accepted decision and immutable source manifest.
2. Reserve collision-free destinations under `Shared/Timeline` or `Shared/Folders/<name>`.
3. Copy to excluded staging, verify length and SHA-256 for every compound member, then finalize without overwrite.
4. Publish `view_shared_assets` membership only when the complete logical item is usable at final destinations.
5. For managed personal sources, reverify source and destination before removing the authorized personal occurrences. For linked/read-only sources, retain the originals and report Copy.
6. Keep `cleanup_pending` when Shared is safe but managed-source removal fails. Retry cleanup from the durable manifest and never delete the last verified copy.
7. Coordinate watcher events so staging and intermediate destination files cannot create duplicate logical items.

Submitting or accepting a contribution is not permission to reorganize unrelated files. Shared transfer destinations remain predictable so administrators can manage the filesystem with ordinary storage tools.

## Notifications and audit

Use in-app state only for the first release:

- Curators receive a pending count in View and the unified activity surface.
- Contributors receive state changes for their own submissions.
- Transfer failures and cleanup-pending items appear to authorized curators and administrators.
- Do not add email, push, webhook, or external notification claims without a real delivery provider.

Every command records actor, target, prior revision, resulting state, timestamp, and a safe error/reason. Logs and API responses must not expose raw service credentials, private sibling paths, or content hashes as user-facing identifiers.

## Delivery phases

### Phase A — Neutral terminology and ownership cutover (complete)

Rename Family Library UI, routes, contracts, services, schema, tests, and documentation to Shared Library. Replace household/family ownership language with server-owned shared storage. Separate Shared Library from Shared with me. Replace the blanket Shared View inclusion policy with explicit access, submit, review, and Gallery-sharing grants.

Exit gate: no family terminology remains in this View feature; existing universe family-tree behavior is untouched; current direct promotion still works through shared-named contracts; breadcrumbs and physical `View/Shared` layout remain intact.

### Phase B — Submission foundation (complete)

Add contribution tables, repository/service contracts, batch preview and submit/cancel APIs, contributor UI, My submissions, idempotency, revisions, scoped preview grants, and truthful pending state. Submission must not modify originals or ordinary Shared Library browsing.

Exit gate: an owner can submit and cancel a mixed batch; unauthorized profiles cannot infer the submission or its assets; page reload and session/profile switching preserve correct state.

### Phase C — Curator review and execution (complete)

Add administrator grant controls, review queue, contribution detail, accept/decline, destination adjustment, direct curator add, durable events, asynchronous transfer dispatch, recovery, and partial-item reporting.

Exit gate: decline is non-mutating; accept revalidates and physically transfers through the existing verified engine; linked originals remain; managed originals disappear only after Shared verification; two curators cannot decide the same revision twice.

### Phase D — Shared lifecycle and release validation (core complete)

Finish Shared Library browsing labels, contribution badges, completed-item provenance, profile-deletion behavior, shared-removal separation, accessibility, responsive breadcrumbs, cursor paging, filters, and operational diagnostics.

Implemented gates cover accepted-only Shared browsing, retained contributor/curator names, responsive breadcrumbs, offset-paged filters, recoverable transfer state, focused ownership/decline/idempotency tests, strict documentation validation, and the full solution regression suite. Large-queue cursor paging, SignalR progress, contribution badges, contribution-scoped media previews, and destructive Shared removal stay outside this contribution workflow and require their own product review.

## Explicit exclusions

- No offsite backup feature, readiness score, scheduling, or provider integration.
- No assumption that server members are related or live in one household.
- No automatic contribution based on profile membership, folder name, face, location, or Gallery sharing.
- No use of the catalogue Review Queue.
- No external notifications in the first release.
- No public links, anonymous submissions, or federation between servers.
- No compatibility routes, dual family/shared schemas, or migration shims for disposable pre-beta state.

## Product-owner summary

Call the feature Shared Library so it works for any group using a Tuvima server. People keep private Personal Spaces and explicitly submit chosen items. Shared Library curators review only those submissions, and acceptance safely moves managed originals or copies linked originals into the server-owned Shared folder. Sharing a Gallery remains a separate visibility feature and never turns personal media into shared-owned files.
