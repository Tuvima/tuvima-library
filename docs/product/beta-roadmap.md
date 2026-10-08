---
title: "Beta Roadmap and Priority Order"
description: "The implementation order for a dependable Tuvima beta, followed by photo intelligence and ecosystem work."
audience: administrator
category: "reference"
product_area: "product"
tags:
  - "beta"
  - "roadmap"
  - "architecture"
status: current
---

# Beta Roadmap and Priority Order

Use this roadmap to understand the order of work toward a dependable beta. It puts library safety and accurate feature status before extra features. Items marked implemented describe source-level work, not a promise that every installer or device release has been accepted. See [Product Status](status.md) for what you can use now.

## P0 — Beta Safety and Architectural Boundaries

### 1. Reproducible installation and startup — implementation present, release verification pending

- Restore from public package sources without a developer-specific local feed.
- Seed container configuration only when the mounted configuration folder is empty.
- Use portable container paths for catalogued libraries and the internal
  `personal` library that backs each View Personal Space.
- Preserve user configuration during Windows upgrades.

**Rationale:** every later feature is irrelevant if a clean machine cannot install, start, and retain its settings predictably.

No Windows installer release is currently published. Packaging, AI runtime setup, updates and removal still need verification on a clean Windows host; the [Windows guide](../install/windows.md) lists the current limits.

### 2. First-class library policy — implemented

- Give every library a stable ID, kind, source paths, intake mode, and metadata policy.
- Keep exactly `catalogued` and `personal` as library kinds. Each enabled
  profile has one user-facing Personal Space backed by a `personal` library;
  sources and devices do not become separate browsing destinations in View.
- Support enriched, local-preferred, local-only, and manual metadata policies.
- Bypass retail, provider, Wikidata, and review-queue work for local-only and manual libraries.
- Keep photo assets outside the work/edition/canonical-claim graph.

**Rationale:** metadata bypass cannot be a late pipeline flag. It changes identity, persistence, readiness, review, and presentation. Making it a library policy prevents home videos and private content from leaking into assumptions made for commercial works.

### 3. Backup and recovery — implemented

- Create consistent SQLite snapshots through the Engine's single-writer boundary.
- Include non-secret configuration and a manifest in each archive.
- Validate archive shape and data-store integrity before staging a restore.
- Apply a staged restore before the data store opens, while preserving the previous file as a recovery copy.
- Expose create, list, download, and restore actions in System settings.

**Rationale:** beta users will test ingestion and matching against real collections. Recovery must exist before encouraging that risk.

### 4. Honest capability surface — implemented

- Remove public actions that return a permanent “not implemented” response.
- Keep unfinished workers private and unregistered until they have a durable workflow.
- Do not expose simulated success or fallback data in the Dashboard.

**Rationale:** an unavailable feature is manageable; a visible action that cannot complete damages trust and makes support harder.

## P1 — Beta Experience and Quality Gates

### 1. Library onboarding and management — implemented

- Provide an empty-library path from Home to Libraries and ingestion.
- Create, edit, and remove custom libraries in Settings.
- Default personal/home-video libraries to local-only behavior.
- Make source path, recursion, read-only behavior, and matching policy visible.

### 2. View Personal Space MVP — core workflows implemented, release verification pending

- Resolve Shared, Mine, and permitted Profile scopes from trusted profile
  identity before querying content.
- Index linked personal sources in place without provider matching or file
  moves. Managed imports copy originals beneath stable profile/source paths;
  accepted Shared Library contributions follow their verified transfer policy.
- Cursor-page a date-grouped timeline and search deterministic local metadata.
- Proxy thumbnails and originals through short-lived, profile-bound,
  same-origin grants rather than exposing Engine credentials to the browser.
- Persist favorite, hidden, archive, trash, and restore state without mutating
  originals.
- Persist Manual and Smart Galleries; Manual Gallery membership is unique and
  Smart Galleries use the shared versioned rule model.
- Read captured date, dimensions, camera details, document text, and GPS when
  locally available.
- Keep administrator scan as a recovery/diagnostic endpoint. Normal View
  onboarding is attaching sources to the profile's Personal Space, not choosing
  among sources or repeatedly starting a manual scan.

The Dashboard already supports selection, lifecycle actions, Manual and Smart
Galleries, authorized People annotations, GPS-based Places, and View-backed
Collection sources. These workflows are described in the [feature truth
inventory](feature-truth-inventory.md).

Remaining P1 acceptance work verifies those workflows on clean installations and
supported desktop, tablet, and phone browsers. Check permissions, empty and
unavailable states, interrupted transfers, restore paths, keyboard access,
focus, and responsive layouts. Places also needs explicit network/privacy
verification because Atlas can request external map resources. Production face,
object, and OCR processing remains separate P3 work, not an unconnected P1 action.

### 3. Regression and release gates — required for every beta candidate

- Restore, warning-free build, and full automated test suite.
- Contract snapshots and storage/integration guardrails.
- Formatting verification and vulnerable-library audit.
- Docker configuration/build and documentation build.
- Representative 1920×1080 screenshots for every changed Dashboard surface, including typography, focus, overflow, empty, and overlay states.

**Rationale:** this is the minimum useful and supportable personal-local-media
experience. It deliberately favors deterministic local indexing over
computationally expensive intelligence.

## P2 — Beta Polish After Real-Library Feedback

Prioritize these during beta when diagnostics, issue reports, or real collections demonstrate the need.

### 1. Photo operations

- Improve folder/file watching and reconciliation under large-library workloads.
- Refine existing batch Gallery placement, visibility changes, and Manual Gallery ordering after real-library feedback.
- Better timezone normalization and explicit “date unknown” grouping.
- RAW/HEIF support based on verified cross-platform decoder availability.
- Thumbnail cache retention, regeneration, and storage diagnostics.

### 2. Library operations

- Per-source indexing history, pause/resume, and more precise failure reporting.
- A preview of how metadata policy affects a file before saving a library.
- Safer path conflict detection across overlapping libraries.
- Import/export of library definitions without secrets.

### 3. Existing media lanes

- Continue data-quality polish for discovery, search ranking, playback, and collection truth.
- Raise automated coverage thresholds as the high-risk ingestion and recovery paths stabilize.

**Rationale:** these features improve efficiency and clarity but do not justify delaying a beta that already indexes safely and exposes truthful states.

## P3 — Post-Beta Photo Intelligence and Sharing

### 1. Local search intelligence

- Local-only face clustering with explicit opt-in, model provenance, and a way to merge or forget identities.
- Object, scene, and OCR indexing with explainable search results.
- A local semantic-search index that can be rebuilt independently of source files.

### 2. Places and memories

- Extend the existing GPS-based Atlas with accepted map privacy controls, coarse-location options, and richer local geocoding.
- Event clustering, trips, “on this day,” and configurable memory generation.
- Editing for captured time, timezone, and location without modifying originals by default.

### 3. Sharing and multi-device workflows

- Public-link Gallery sharing only after authentication, authorization,
  expiration, revocation, and audit logging are production-ready. Selected-profile
  Gallery sharing is the only persisted sharing model in the beta foundation.
- Mobile upload/sync, conflict handling, and resumable transfers.
- Broaden personal-video delivery and motion-photo format support beyond the current verified playback and Live Photo pairing paths.

**Rationale:** Google Photos/Immich-style intelligence is a separate privacy, model-quality, compute, and security program. It should build on View's isolated local-asset model, not be embedded into catalogue ingestion or block beta safety.

## Release Decision

A beta candidate is ready when P0 and P1 are green on a clean installation and the visual evidence shows no layout regressions. P2 is feedback-driven beta polish. P3 must not hold the beta and should not be marketed as complete until its privacy and accuracy controls are tested with real libraries.

## Next steps

- Check [current product status](status.md) before choosing a feature.
- Use the [installation guide](../install/docker.md) for available setup paths.
- Review the [feature truth inventory](feature-truth-inventory.md) for implementation evidence.
