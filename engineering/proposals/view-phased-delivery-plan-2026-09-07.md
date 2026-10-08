---
title: "Libraries and View: Phased Delivery"
summary: "Deliver library settings first on profile-owned storage foundations, then View browsing, direct logins, and physical promotion into the server-owned Shared Library."
audience: "developer"
category: "proposals"
product_area: "view"
---

# Libraries and View: phased delivery

Status: delivery in progress September 8, 2026. The implementation checkpoint below records completed work and the remaining release gates. This is the controlling delivery plan for the related Libraries immediate-actions, View storage-organization, and View folder-browsing proposals. The dedicated [Shared Library contributions plan](./view-shared-library-contributions-plan-2026-09-08.md) supersedes this document's Family Library terminology and defines the remaining contribution workflow.

## Implementation checkpoint

The first Phase 1 increment implements scoped/conflict-aware library edits, section-owned folder actions, View summary/filter/settings, independent initial rendering and health checks, readable per-profile storage labels, Timeline upload destination, named mixed folders, a reserved Shared directory, and queued View reconciliation. Existing media remains in place. Browser verification covered 1920 x 1080 desktop and 390 x 844 mobile; Read/Watch scope changes added zero library/operations requests or path probes, with one observed Read switch around 230 ms including the automation observation.

Validation: solution restore/build succeeded with zero build warnings/errors; strict documentation build passed. The regression run plus affected-suite reruns passed 3,254 tests, with 37 skipped. One unrelated series-manifest storage test hit a disposed SQLite connection during the full run; the complete 391-test storage suite passed on rerun. The final 910-test Web suite passed after dialog and wizard refinements. Browser checks also verified immediate save success and actual View source attach/detach.

The remaining-phase increment adds the five-destination View shell, authorized indexed Folders with nested URL-backed breadcrumbs and private pins, inherited branch-level Photos inclusion, readable calendar paths for browser uploads, a first-class Shared Library folder root, physical keeper promotion, and durable recovery state. Managed originals are deleted only after every Shared compound member verifies; linked originals are copied and retained. Direct profile sign-in, explicit account/profile grants, invitation enrollment, password recovery, rate limiting, and session revocation already satisfy the Phase 3 identity foundation without creating duplicate profile storage.

Validation for this increment: the full solution builds with zero warnings or errors, strict documentation validation passes, and the full regression run passes 3,258 tests with 37 provider/network fixtures skipped as expected.

Phase 4 currently supports owner-approved direct promotion. Contributor submission and Shared Library curator review follow the dedicated contribution plan. Comprehensive large-archive, interruption-at-every-checkpoint, recovery-copy, and browser/mobile acceptance remain release gates. The wizard reviews the concrete Profiles and Shared paths and their transfer consequences; additional source creation remains in Libraries > View.

## Required product contract

- One View experience indexes explicitly configured supported personal/mixed-media sources. It does not automatically absorb the Read/Watch/Listen catalog or scan every disk.
- One server installation has a primary administrative account and multiple stable profiles. Each enabled profile owns one Personal Space and provisioned folder, whether reached through authorized profile switching or its own optional login.
- Login credentials are separate from profile/storage identity. An optional email/password login resolves directly to the existing profile; it creates no second folder or library. A subordinate profile is not necessarily a minor, and direct login does not grant administrative authority.
- The Shared Library has durable server ownership, designated curators, and a physical Shared destination independent of any profile. Shared ownership must not be implemented as a fake user profile. Retain catalogued/personal media kinds; Shared Library ownership is a scope within personal media, not a third kind.
- Settings governs configured storage, ownership, indexing and organization policies. View owns everyday Timeline, Folders, Galleries, selections, contributions, and family curation.
- Accepted promotion from managed personal storage physically relocates the selected file group into Shared. A logical ownership/reference change alone does not satisfy this requirement. Shared is the deliberate keeper archive with a stable, predictable filesystem layout that administrators can manage with their own storage tools.
- Preserve shell-owned settings breadcrumbs, wizard breadcrumbs, folder-picker path breadcrumbs, and View's hierarchical folder breadcrumbs.

## Canonical storage layout

```text
D:\PersonalMedia\                         configured central root
  Profiles\                               structural, not a scan source
    shaya\                                stable profile storage label
      Timeline\2024\11 - Nov\...
      Folders\Car Videos\...
    alex\
      Timeline\...
      Folders\...
  Shared\                                 curated server-owned originals
    Timeline\2024\11 - Nov\...
    Folders\Home Movies\...
```

Using Profiles as an explicit namespace separates profile labels from the reserved Shared destination. Labels map to immutable profile IDs, remain stable on email/display-name changes, and are validated for collisions, case rules, reserved names and traversal. Profile creation reserves/provisions its path through the same service used by settings. Login creation is not required for provisioning.

Additional managed or read-only folders can remain elsewhere and have explicit personal or Shared Library ownership. Root/profile containers are not redundant recursive sources. Timeline and named folder sources have nonoverlapping scan coverage; indexing separates physical file occurrences from asset identity and device provenance. Readable calendar and preserve-layout policies retain the previous proposal's date, Undated, collision and compound-file rules.

Shared is the canonical destination for retained server-owned originals. Externally linked Shared Library sources must be labeled **Linked — outside Shared**, not represented as Shared originals. Moving an accepted contribution to a managed destination outside Shared does not satisfy promotion. Thumbnails, transcodes, temporary work, and database internals stay outside the originals tree; excluded transfer staging must not appear as finalized keeper content.

## Phase 1 — Library settings on the final storage foundation

This is the first user-visible delivery. Implement only the ownership/storage foundations necessary to make these settings real before exposing the controls; full View browsing and authentication redesign are later phases.

1. Establish stable profile and server-owned Shared Library source ownership, readable storage labels, central Profiles/Shared layout, managed destination routing, path validation and nonoverlapping source registration. Adapt the existing personal ingestion path to the new profile folders so uploads and indexing continue to work after cutover. New profile provisioning uses the same contract. Represent the Shared Library as a server resource without requiring new direct-login UI.
2. Confirm filter latency with request/mount measurements; keep Libraries mounted and filter loaded summaries for All/Read/Watch/Listen/View without repeat filesystem probes. Retain authorization expiry and revocation checks.
3. Fix/retain shared header-row column geometry in every scope, including Listen. Add one View row and dedicated settings page; remove the separate overview View Storage panel.
4. Use matching Folders, Organization, File Handling, Personal Spaces & Sharing, and Local Processing/Advanced sections. Put Add folder inside Folders. Show actual owner, path, mode, policy and health. Distinguish the root from indexed sources and show Shared provisioning status.
5. Replace global Save with targeted atomic, concurrency-safe mutations. Final Add persists immediately; detach requires confirmation and leaves files intact. Coupled edits use a focused Apply dialog. Preserve last-folder and primary-destination rules from the immediate-actions proposal.
6. Update the three-step wizard: choose View storage or additional source; configure central root/profile labels or source owner (profile/Shared Library), path, recursive indexing and managed/read-only organization; review actual paths and consequences. No second root per login and no silent sharing. Do not expose future promotion/contribution controls before Phase 4.
7. Retain all breadcrumb contracts and scope-restoring navigation. Use current authorization to limit administration; future login controls link to Users & Access only when implemented.

Exit gate: settings changes survive reload; actual managed uploads land in the correct profile source; Shared can be provisioned as an owned destination; direct Shared Library imports land inside Shared; linked external sources remain unchanged; counts are truthful; scope switches cause no data reload/probes after initial load. Verify simultaneous settings edits, cancellation/errors, offline paths, traversal/overlap/reparse-point rejection, profile naming collisions, and responsive column/breadcrumb behavior.

## Phase 2 — View browsing and organization

- Deliver Timeline / Folders / Galleries / People / Places with consistent authorized personal/Shared Library scope, modifying the existing four-tab contract and documentation together.
- Build indexed, paginated folder hierarchy and file-occurrence queries. Support root versus immediate-child presentation, root-level files, nested breadcrumbs, pins, scoped search and include-descendants views. Adding a parent is one source, not hundreds of libraries or auto-created Galleries.
- Personal Timeline defaults on; additional sources explicitly opt in, with inherited branch overrides. Excluded sources remain available in authorized Folders/search/Galleries. Keep timeline deduplication distinct from physical occurrence browsing.
- Manual/dynamic Galleries organize existing items without moving files. Sharing a Gallery remains separate from keeper promotion. Local metadata and processing stay outside catalog enrichment and Review Queue.

Exit gate: independently added Car Videos/Home Movies and their combined parent both browse correctly; unknown dates, duplicate occurrences, offline roots, shared-Gallery private siblings, large archives and mobile breadcrumbs behave as specified. Do not imply contribution retention before Phase 4.

## Phase 3 — Account hierarchy and optional profile logins

- Extend Users & Access with a primary administrative account, stable profile associations, role-based administration and designated Shared Library curators. Keep account hierarchy, age controls, media ownership and private-content access separate.
- Add optional direct sign-in to an existing profile using verified unique email and securely hashed passwords through the application's authentication system. Include enrollment/invitation, recovery, credential change, rate limiting, session revocation and clear account linking; do not store plaintext credentials in profile configuration.
- Direct sign-in selects its bound profile and cannot switch into private siblings without authorization. Primary-account management powers do not silently bypass profile media privacy. Define explicit grants for profile switching and supervised access.
- Removing a login leaves profile/media intact. Profile deletion and Shared Library ownership reassignment are separate operations; prevent removal of the last responsible administrator or Shared Library curator without a replacement. Email/display-name changes never move storage or reassign files.

Exit gate: profile-only and direct-login entry reach the same asset/folder identities; password reset and disabled access revoke sessions appropriately; cross-profile access is rejected server-side; credentials and duplicate/private metadata are not leaked; removing one account does not orphan the Shared Library.

## Phase 4 — Shared Library contributions and physical promotion

The [Shared Library contributions plan](./view-shared-library-contributions-plan-2026-09-08.md) is authoritative for neutral terminology, permissions, submission/review states, UI, API, schema cutover, and release gates. The physical transfer requirements below remain mandatory.

View provides **Share Gallery** for visibility and **Submit to Shared Library** for reviewed promotion. Contributors may submit candidates; designated Shared Library curators accept or decline. Submission authorizes the explained future move on acceptance, while final execution rechecks source authority. Curators may add directly only where authorized. This is a View contribution workflow, not the catalog Review Queue.

Before submission or direct execution, preview file/group count, destination path, collisions, total bytes, and consequences: accepted originals leave the personal folder, become server-owned and visible in the Shared Library, and reside under Shared. Gallery membership alone does not make every gallery item a keeper. Calendar destinations use capture dates and deterministic policy; named mixed collections preserve chosen topic boundaries. A reference in a contributor's Gallery may remain only when that user retains authorized access to the shared item; it is not a second personal file.

Required transfer behavior:

1. Validate current ownership/permissions, writable managed source, destination containment beneath Shared, free space, actual files, compound membership, policy version and path conflicts. Never overwrite. Lock/reserve the affected occurrences against concurrent import/delete/move work.
2. Write a durable, idempotent transfer record containing each original/destination and transaction state. Coordinate watcher events so intermediate source disappearance or destination creation cannot duplicate assets or publish a partially transferred group.
3. Use a safe same-volume relocation where possible. Across volumes, copy to excluded destination staging, verify every original/sidecar byte length and content hash, finalize without overwrite, and only then remove the authorized personal originals. Preserve original filename/source attribution and filesystem timestamps where supported.
4. Treat Live Photo pairs, RAW/JPEG variants and relevant sidecars as one planned group. Filesystem and database changes are not one atomic transaction: use persisted checkpoints and recovery to reconcile paths, occurrences, Gallery references and Shared Library ownership after interruption. Publish shared membership only when the complete group is usable at its final location.
5. A transfer is fully complete only after required source cleanup succeeds. If destination is safe but cleanup fails, show **Copied to Shared — personal cleanup pending**, retain retry state, and never claim the item was fully moved. Recovery never deletes the last verified copy. Revalidation must catch source changes during copying.
6. Deduplicate only against a verified authorized existing Shared Library occurrence. Do not overwrite different bytes sharing a filename or delete another owner's independent occurrence. Independent duplicates in other profiles require their own contribution decisions and may remain in those profiles.
7. Read-only/external sources cannot be physically moved by Tuvima. Offer an explicit **Copy to Shared Library**, explaining that the original remains outside Shared; never label it Move or delete that origin. Protected existing sources require explicit move authority, not a blanket bypass of original protection.

Separate Remove from Gallery, Remove personal reference, Detach source, and Delete shared original. Once promoted, personal account deletion cannot delete the server-owned original. Shared deletion requires Shared Library authority and a separate explicit operation. No implicit undo that deletes a now-shared file; a future return-to-personal operation must validate Shared Library authority, current references and a new physical move.

Exit gate: verify same/cross-volume transfers, occupied names, compound files, changed originals, denied cleanup, unavailable destinations, disk-full, concurrent watcher activity, double submissions and process termination at each checkpoint. Accepted keepers must physically exist beneath Shared; completed moves leave no selected personal occurrence. Read-only contributions retain the external original and accurately report copying.

## Phase 5 — End-to-end acceptance

- Keep Shared, Profiles, transfer staging, generated derivatives, and application data in clearly separated paths. Tuvima owns this logical layout; administrators choose and operate any storage protection tooling outside the product.
- Verify transfer recovery using copied test media without altering original sources. Validate Shared Library references, profile isolation, and retained compound files after recovery.
- Run solution restore/build and appropriate regression tests; verify desktop/mobile Library and View journeys, breadcrumbs, latency, account switching, source permissions and transfer recovery. Record actual results and update AGENTS/product documentation to the new implemented contracts.

## Delivery constraints

Each phase must expose supported, truthful behavior with no placeholder switches. Phase 1 is settings-first but includes the minimum real storage backend, not a UI-only facade; Phase 4 physical promotion depends on the earlier storage and authorization contracts. Under the pre-beta rule, replace obsolete disposable config/index state cleanly rather than adding compatibility/migration layers. Inventory existing media and require explicit reimport/relocation before adopting a new layout; this plan itself authorizes no filesystem moves or deletions.

## Product-owner summary

First make Libraries fast and simple to configure, backed by one folder per profile and a separate Shared archive. Then build folder browsing and shared organization in View, followed by independent profile logins. Finally, accepting a personal item into the Shared Library physically moves its managed files into Shared, leaving administrators with a clear and logically structured filesystem.
