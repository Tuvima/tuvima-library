# Tuvima documentation index

> **Status:** Current · **Last updated:** 2026-10-17 · Maintained by Claude Code: update this file whenever a doc is added, moved, or changes status.

Every documentation file in the repository, one line each, with its status. Two homes:

- **`engineering/`** (this folder) — internal planning: product and architecture overviews, decisions, design system, specs. Not published.
- **`docs/`** — the public documentation website, https://tuvima.github.io/tuvima-library/ (built by `website/`).

**Statuses.** Specs: Draft → Locked → In progress → Built. Other internal docs: Current (kept up to date) or Historical (a record; not maintained). Public pages use the site's own values: current, early-access, target-state.

## Start here (planning anchors)

| Doc | Status |
|---|---|
| [Tuvima Library — product overview](product/overview.md) | Current |
| [How Tuvima is built](architecture/overview.md) | Current |
| [Tuvima design system](design/design-system.md) | Current |
| [Spec template](specs/_template.md) | Current |
| [The Tuvima planning project (claude.ai)](planning-project.md) | Current |

## Specs

| Spec | Status |
|---|---|
| [Spec: Developer tools as one family (Local AI, Plugins, Integrations, Developer Tools)](specs/developer-tools.md) | Built, awaiting your Local checks (v3; all waves built 2026-10-21; WP8 waits for the first community plugin; checks in [the batch list](validation/batch.md); handoff: [waves 4–8](handoffs/2026-10-20-developer-tools-waves-4-to-8.md)) |
| [Spec: Universes (page, Series grouping, companion, clean-up)](specs/universe.md) | In progress (v4; U-WP0 to U-WP3 built 2026-10-19; U-WP3b, U-WP4 to U-WP8, U-WP9 part 1, U-WP10 to U-WP12 built 2026-10-20, and U-WP9 part 2, U-WP13 (Dashboard) and U-WP14 built 2026-10-21, awaiting Local checks; rest in the [2026-10-20 Universes handoff](handoffs/2026-10-20-universe-remaining.md)) |
| [Spec: Details overlay and page-based Editing Mode](specs/details-overlay-and-edit-mode.md) | In progress (v2): Waves 1–5 merged; WP8 left, see the 2026-10-21 handoff |
| [Spec: Videos in Watch and the Vault](specs/videos.md) | Built (v30; Waves 1 to 3 merged 2026-10-22, Vault in #374; awaiting Local checks in `validation/batch.md`). Open follow-ups: Vault tile in the Add folder wizard (library setup WP6), per-section Vault permissions and a Vault per media type (D54), PHASH licence check, StashDB terms check (D55d). Stays here until they are done. |
| [Spec: Simpler access (areas, ratings, untick a library, folder checkbox)](specs/simpler-access.md) | In progress (v1, 2026-10-23; WP1 in review; next WP2 and WP3, WP4, then WP5 and WP6) |
| [Spec: Vault sections and Move to Vault](specs/vault-sections-and-move.md) | Locked (v4, 2026-10-22; not yet built; start with the central access service, WP0) |
| [Spec: Delete a show, season, album or Series, and the Recycle bin](specs/delete-groups.md) | Locked (v7, 2026-10-21) |
| [Spec: Safer deleting in View](specs/safer-deleting-in-view.md) | Locked (v2, 2026-10-21) |
| [Spec: Simpler library setup (Watch, Listen, Read, View, Vault)](specs/library-setup-groups.md) | In progress (v29; everything except WP7b-3 and WP10 merged (WP12 in #445); next WP7b-3 (D60, after Simpler access WP5), then WP10 docs) |
| [Spec: Batched write-back (all files, on a schedule, with ExifTool)](specs/batched-writeback.md) | Built, awaiting Local checks (v2; #416, #419, #420, #422, BW5 docs) |
| [Spec: Encrypted plugin and integration keys](specs/encrypted-plugin-keys.md) | Locked (v1, 2026-10-21, change note 2026-10-22; EK1 and EK3 before plugin install WP19, EK2 with a 4-digit PIN after Backup and restore WP3) |
| [Spec: Backup and restore (settings and personal state)](specs/backup-and-restore.md) | Built, awaiting your Local checks (v8, 2026-10-22; [handoff](handoffs/2026-10-21-backup-and-restore.md) done; WP1 to WP6 built: #397, #405, #421, #424, #428, #431; checks L1 to L6 in [the batch list](validation/batch.md)) |
| [Spec: Right-sized pictures on every screen](specs/archive/right-sized-pictures.md) | Built (v1, archived 2026-10-22; awaiting Local checks L1-L4; features to consider later (WebP, F1-F3) in its section 13) |
| [Spec: Person credits and series search without the whole-library credit list](specs/archive/person-credits-and-series-search-speed.md) | Built (v2, archived 2026-10-21; follow-ups R1 and R2 in its section 8) |
| [Archive](specs/archive/README.md) | Built specs |
| [Spec: The live ingestion page and when items appear](specs/archive/ingestion-and-readiness.md) | Built (v4, archived 2026-10-20; bug 13 still needs the log line from that moment) |
| [Spec: Audiobook year on the remaining screens](specs/archive/audiobook-year-other-reads.md) | Built (v3, archived 2026-10-20; follow-ups: series search and person credits read the whole credit list) |
| [Spec: Home and item detail speed](specs/archive/home-and-detail-speed.md) | Built (v2, archived 2026-10-20; the seven other reads were moved by the audiobook year spec) |
| [Spec: Library list speed](specs/archive/library-list-speed.md) | Built (v2, archived 2026-10-19; Home, detail and the audiobook year look-up done in the Home and detail speed spec) |
| [Spec: Settings revamp (one look, one menu, one attention banner)](specs/archive/settings-revamp.md) | Built (v1.1, archived 2026-10-19; Logs > Events is filled by the ingestion spec's WP12) |
| [Spec: Editing Mode on the item page (as built)](specs/archive/editing-mode.md) | Built (v1, archived 2026-10-19) |
| [Spec: Profiles and setup (as built)](specs/archive/profiles-setup-redesign.md) | Built (v2, archived 2026-10-19) |
| [Spec: First run and prerequisites](specs/archive/first-run-prerequisites.md) | Built (v1, archived 2026-10-18) |
| [Plan: First-run ingestion fixes (2026-10-10)](specs/archive/first-run-ingestion-fixes.md) | Built (archived 2026-10-17) |
| [Spec: One file covering several TV episodes](specs/archive/multi-episode-files.md) | Built (v2, archived 2026-10-17) |
| [Spec: Item Detail page](specs/archive/item-detail.md) | Built (v5, archived 2026-10-17) |
| [Spec: Registry retirement — Needs Review is the home for exceptions](specs/archive/registry-retirement.md) | Built (v5, archived 2026-10-17) |
| [Spec: Delete from library](specs/archive/delete-from-library.md) | Built (v5, archived 2026-10-20) |
| [Spec: Deleting personal files in View](specs/archive/view-personal-file-deletion.md) | Built (v3, archived 2026-10-20) |
| [Spec: Open from the start — retire "Who can connect"](specs/archive/open-from-the-start.md) | Built (v3, archived 2026-10-18) |
| [Spec: Numbered data store upgrades and contributor guide](specs/archive/data-store-upgrade-scripts.md) | Built (v2, archived 2026-10-18) |

## Handoffs

| Doc | Status |
|---|---|
| [Codex handoff: remaining build work (2026-10-17, refreshed)](handoffs/2026-10-17-codex.md) | Current |
| [Handoff: finish the outstanding work, end to end (2026-10-19)](handoffs/2026-10-19-finish-outstanding-work.md) | Current |
| [Handoff: finish the Details overlay and Editing Mode spec (2026-10-21)](handoffs/2026-10-21-details-overlay-finish.md) | Current: WP8 per-type pass and docs, plus follow-ups |
| [Handoff: Developer tools waves 4 to 8 and wave 3 follow-ups (2026-10-20)](handoffs/2026-10-20-developer-tools-waves-4-to-8.md) | Current |
| [Handoff: finish the Universes spec (2026-10-20)](handoffs/2026-10-20-universe-remaining.md) | Current (threads A to G: U-WP3b, U-WP13, U-WP14, U-WP9 part 2, U-WP4 priority, U-WP15, closing the spec) |
| [Handoff: library read speed follow-ups (2026-10-21)](handoffs/2026-10-21-library-read-speed-follow-ups.md) | Current — T1 to T7; T4 and T5 wait on Shy's decisions |
| [Handoff: developer tools after wave 2, and the follow-ups it turned up (2026-10-21)](handoffs/2026-10-21-developer-tools-and-follow-ups.md) | Current |
| [Handoff: stop filing media into a doubled category folder (2026-10-20)](handoffs/2026-10-20-doubled-category-folder.md) | Current |
| [Handoff: Dapper vs Entity Framework benchmark](handoffs/2026-10-17-ef-vs-dapper-benchmark.md) | Done — Shy chose to stay on Dapper (2026-10-17) |

## Architecture decisions

| Decision | Status |
|---|---|
| [Architecture decisions](architecture/decisions/README.md) | Current — list of all decisions |
| [0001 — SQLite with hand-written SQL (Dapper), not Entity Framework](architecture/decisions/0001-sqlite-and-dapper.md) | Accepted — reaffirmed by Shy 2026-10-17 after the EF benchmark |
| [0002 — First-party `App*` UI components; MudBlazor removed](architecture/decisions/0002-first-party-ui-components.md) | Accepted |
| [0003 — Storage epoch `guid-blob-v1`; reset instead of migrate](architecture/decisions/0003-storage-epoch-guid-blob.md) | Accepted |
| [0004 — `MediaEngine.Contracts` alone owns Engine↔Dashboard wire types](architecture/decisions/0004-contracts-own-the-wire.md) | Accepted |
| [0005 — Local-first; no Tuvima-hosted account](architecture/decisions/0005-local-first.md) | Accepted |
| [0006 — All-in-one management workspace retired; shared editor + Needs Review](architecture/decisions/0006-management-workspace-retired.md) | Accepted |
| [0007 — Accounts own access; profiles own experience; no guest keys](architecture/decisions/0007-accounts-and-profiles-access.md) | Accepted |
| [0008 — Public docs site in `docs/`; internal planning in `engineering/`](architecture/decisions/0008-public-docs-vs-engineering-docs.md) | Accepted |
| [0009 — Open from the start; apps always allowed](architecture/decisions/0009-open-from-the-start.md) | Accepted |
| [0010 — Missing helper tools: report them, hold the files, offer a checked download](architecture/decisions/0010-helper-tools-report-hold-offer.md) | Accepted |
| [0011 — Personal activity is its own lasting record; one scope check decides who sees whose](architecture/decisions/0011-personal-activity-record-and-scopes.md) | Accepted |

## Design

| Doc | Status |
|---|---|
| [Tuvima design system](design/design-system.md) | Current |
| [Mockups](design/mockups/) | Historical — 18 HTML previews and a Dashboard UI kit from spring 2026; older token values (see design system) |

## Reports

| Doc | Status |
|---|---|
| [Dapper vs Entity Framework Core: benchmark results](proposals/ef-vs-dapper-benchmark-results-2026-10.md) | Current — outcome recorded in ADR 0001 |
| [Universe U-WP0: what really mixes up The Expanse](reports/universe-expanse-check-2026-10-19.md) | Current — feeds U-WP3 |
| [Slow library and Home reads at 4,000 works](reports/slow-reads-2026-10.md) | Current — causes 1–2 fixed by the library list speed spec; causes 3–4 fixed by the Home and detail speed spec; follow-ups in the 2026-10-21 read speed handoff |
| [Which stored name wins when a field accepts several](reports/stored-name-order-2026-10.md) | Current — recommendations 1–3 approved and built (stored-name-order-fix); addendum recommendations 6–9 approved and built; open remediation: the combined artist view's artist reader (see the report's last section) |
| [Which library reads are slow or break on very large libraries](reports/library-read-speed-measurements-2026-10.md) | Current — measured 2026-10-21; people search, presence counts and library-sized id lists fixed |
| [Slow pages from the 2026-10-21 local check (B17, B29, B31)](reports/slow-pages-b17-b29-b31-2026-10-21.md) | Current — measured before and after; summary tiles, detail pages and People section fixed; People credit reads and the collection catalog left for T3 |
| [Large upload spike: dropping 1, 10 and 50 GB files on a library page](reports/large-upload-spike-2026-10.md) | Current — WP11 of the library setup spec, measured 2026-10-21; feeds WP12 (resumable 64 MB pieces, 50 GB warning threshold pending the real-network check) |

## History (pre-2026-10-17 engineering records)

Kept as a record of what was planned, decided and verified. Not maintained; newer specs and decisions win. Engineering plans written before the spec template stay here unless still active (active ones moved to `specs/`).

### Architecture notes (archived)

| Doc | Status |
|---|---|
| [Account, Profile, and Authentication Plan](architecture/archive/account-profile-authentication-plan.md) | Historical |
| [API Boundary Debt](architecture/archive/api-boundary-debt.md) | Historical |
| [OpenAPI Migration Plan](architecture/archive/openapi-migration.md) | Historical |
| [Storage Policy Review](architecture/archive/storage-policy-review.md) | Historical |

### Plans

| Doc | Status |
|---|---|
| [Access runtime verification](plans/access-architecture-2026-09-08/execution/access-runtime-verification.md) | Historical |
| [Crash verification before Access refactoring](plans/access-architecture-2026-09-08/execution/crash-verification.md) | Historical |
| [Access cutover and verification](plans/access-architecture-2026-09-08/execution/cutover.md) | Historical |
| [P00 endpoint, principal, and resource matrix](plans/access-architecture-2026-09-08/execution/endpoint-matrix.md) | Historical |
| [Endpoint registration ledger](plans/access-architecture-2026-09-08/execution/endpoint-registration-ledger.md) | Historical |
| [Ingestion visual verification](plans/access-architecture-2026-09-08/execution/ingestion-visual-verification.md) | Historical |
| [P00 ownership and interface manifest (P01-P05)](plans/access-architecture-2026-09-08/execution/ownership-and-interface-manifest.md) | Historical |
| [P01 access schema handoff](plans/access-architecture-2026-09-08/execution/p01-schema-design.md) | Historical |
| [P02 Applications handoff](plans/access-architecture-2026-09-08/execution/p02-applications-handoff.md) | Historical |
| [P02 authority and identity handoff](plans/access-architecture-2026-09-08/execution/p02-handoff.md) | Historical |
| [P02 review gate](plans/access-architecture-2026-09-08/execution/p02-review-gate.md) | Historical |
| [P04 View privacy preflight](plans/access-architecture-2026-09-08/execution/p04-preflight.md) | Historical |
| [P04 review gate](plans/access-architecture-2026-09-08/execution/p04-review-gate.md) | Historical |
| [P05 Dashboard Access preflight](plans/access-architecture-2026-09-08/execution/p05-preflight.md) | Historical |
| [Plugin host permission preflight](plans/access-architecture-2026-09-08/execution/p08-preflight.md) | Historical |
| [Permission-to-service map](plans/access-architecture-2026-09-08/execution/permission-service-map.md) | Historical |
| [Prepared squash description](plans/access-architecture-2026-09-08/execution/squash-description.md) | Historical |
| [Access execution status](plans/access-architecture-2026-09-08/execution/status.md) | Historical |
| [Repository inventory and migration risk map](plans/access-architecture-2026-09-08/inventory.md) | Historical |
| [Access architecture: review and implementation plan](plans/access-architecture-2026-09-08/plan.md) | Historical |
| [Worker packets and acceptance schedule](plans/access-architecture-2026-09-08/workers.md) | Historical |
| [Independent Claude review: Tuvima access, identity and remote access](plans/access-identity-remote-access-claude-review-2026-10-08.md) | Open review (pre-template) |
| [Tuvima Library access, identity, and remote access review plan](plans/access-identity-remote-access-review-2026-10-08.md) | Open proposal (not approved; pre-template) |
| [Audiobook dock and shared sidebar refinement](plans/audiobook-dock-sidebar-refinement-2026-10-02.md) | Historical |
| [Dashboard style ownership: reviewed delivery plan](plans/css-ownership-reviewed-plan-2026-10-05.md) | Historical |
| [Documentation overhaul reviewed implementation plan](plans/documentation-overhaul-reviewed-plan-2026-10-08.md) | Historical |
| [Editor matching by the identity being edited](plans/editor-hierarchical-matching-default-order-2026-09-29.md) | Historical |
| [Ingestion restart recovery verification](plans/ingestion-restart-recovery-verification-2026-09-08.md) | Historical |
| [Playback implementation contract](plans/playback-surface-contract-2026-10-01.md) | Historical |
| [Player surface visual and interaction correction plan](plans/playback-surface-visual-correction-2026-09-29.md) | Historical |
| [Player and shared controls implementation plan](plans/player-and-controls-reviewed-plan-2026-10-05.md) | Historical |
| [Player update implementation plan](plans/player-update-reviewed-plan-2026-10-03.md) | Historical |
| [Setup, Operations, View settings, and install behavior](plans/setup-operations-view-polish-2026-09-08.md) | Historical |
| [Setup, Operations, and View verification](plans/setup-operations-view-polish-verification-2026-09-08.md) | Historical |
| [Shared AI storage and repository cleanup](plans/shared-ai-storage-and-repository-cleanup-2026-09-11.md) | Historical |
| [SubDL subtitle provider replacement plan](plans/subdl-subtitles-provider-2026-09-28.md) | Historical |
| [Multi-level media matching plan](plans/tvdb-editor-scope-matching-2026-09-29.md) | Historical |
| [TV episode catalog and editor matching plan](plans/tvdb-tv-editor-ordering-2026-09-28.md) | Historical |
| [Unified media editor cleanup tickets](plans/unified-media-editor-2026-09-30/cleanup-tickets.md) | Open tickets (pre-template) |
| [File synchronization capability audit](plans/unified-media-editor-2026-09-30/file-capabilities.md) | Historical |
| [Unified media editor implementation ledger](plans/unified-media-editor-2026-09-30/implementation.md) | Historical |

### Proposals

| Doc | Status |
|---|---|
| [Universe enrichment, identity, View Artwork Library, and unified artwork editor plan](proposals/2026-09-21-universe-artwork-view-plan.md) | Historical |
| [View Photos, shared media viewer, Places, and Artwork Library implementation plan](proposals/2026-09-22-view-photos-media-viewer-places-plan.md) | Historical |
| [View Library remediation and visual acceptance plan](proposals/2026-09-24-view-library-remediation-plan.md) | Historical |
| [Shared library timeline controls](proposals/2026-09-25-shared-library-timeline-plan.md) | Historical |
| [Editor header and shelf editing plan](proposals/editor-header-and-shelf-editing-plan-2026-09-19.md) | Historical |
| [Home and shared media cards: implementation plan](proposals/home-cross-media-cards-plan-2026-09-28.md) | Historical |
| [Home media cards: approved implementation contracts](proposals/home-media-cards-contract-freeze-2026-10-03.md) | Historical |
| [Ingestion and Activity Experience Plan](proposals/ingestion-activity-experience-plan-2026-09-05.md) | Historical |
| [Ingestion, identity, and editor review](proposals/ingestion-editor-review-2026-09-04.md) | Historical |
| [Libraries Pages Update Plan](proposals/libraries-pages-update-plan-2026-09-07.md) | Historical |
| [Libraries: Immediate Actions and View Settings](proposals/libraries-view-and-immediate-actions-plan-2026-09-07.md) | Historical |
| [Media readiness, playback, setup, and UI performance remediation](proposals/media-readiness-performance-remediation-plan-2026-09-27.md) | Historical |
| [Implementation and verification evidence — 27 September 2026](proposals/media-remediation-implementation-evidence-2026-09-27.md) | Historical |
| [Dashboard component migration](proposals/mud-removal-implementation-2026-10-06.md) | Historical |
| [Operations Live and Historical Ingestion Plan](proposals/operations-live-history-performance-plan-2026-09-06.md) | Historical |
| [Operations Simplicity Remediation Plan](proposals/operations-simplicity-remediation-plan-2026-09-05.md) | Historical |
| [Operations UI quality pass plan](proposals/operations-ui-quality-pass-plan-2026-09-05.md) | Historical |
| [Audiobook details, Watch playback, and review recovery](proposals/playback-detail-review-root-cause-plan-2026-09-27.md) | Historical |
| [Real-media harness: protected existing-library ingestion](proposals/real-media-harness-plan-2026-09-26.md) | Historical |
| [Real-media remediation: identity, availability, playback, and reading](proposals/real-media-remediation-plan-2026-09-26.md) | Historical |
| [Real-media remediation implementation and verification](proposals/real-media-remediation-verification-2026-09-27.md) | Historical |
| [Settings and Ingestion UX Remediation Plan](proposals/settings-ingestion-remediation-plan-2026-09-07.md) | Historical |
| [TV Episode Consistency Plan](proposals/tv-episode-consistency-plan-2026-09-05.md) | Historical |
| [TVDB Setup, TV Season Matching, and People Enrichment Plan](proposals/tvdb-setup-tv-season-editor-plan-2026-09-28.md) | Historical |
| [Unified Media Editor: edition and release extension](proposals/unified-media-editor-edition-extension-2026-09-30.md) | Historical |
| [Unified Media Editor Implementation Plan](proposals/unified-media-editor-plan-2026-09-29.md) | Historical |
| [Unified playback and reader implementation plan](proposals/unified-playback-reader-implementation-plan-2026-09-27.md) | Historical |
| [View: Timeline, Folders, and Galleries](proposals/view-folder-browsing-plan-2026-09-07.md) | Historical |
| [Libraries and View: Phased Delivery](proposals/view-phased-delivery-plan-2026-09-07.md) | Historical |
| [View Shared Library Contributions](proposals/view-shared-library-contributions-plan-2026-09-08.md) | Historical |
| [View Storage and Folder Organization](proposals/view-storage-organization-plan-2026-09-07.md) | Historical |
| [Wikidata matching: retain coverage, require identity validation](proposals/wikidata-match-validation-review-2026-09-27.md) | Historical |
| [Tuvima.Wikidata Improvement Proposals](proposals/wikidata-recon-library-improvements.md) | Historical |

### Reports and evidence

| Doc | Status |
|---|---|
| [Audiobook refinement implementation evidence](reports/audiobook-refinement-2026-10-02.md) | Historical |
| [CSS and interop cleanup evidence](reports/css-cleanup-2026-10-05.md) | Historical |
| [Dashboard CSS ownership implementation — 6 October 2026](reports/css-ownership-2026-10-06.md) | Historical |
| [Home and shared media cards implementation review](reports/home-media-cards-2026-10-03.md) | Historical |
| [Dashboard component migration: consolidated review](reports/mud-removal/index.md) | Historical |
| [P0 â€” Safety net and baseline](reports/mud-removal/p0-safety-net.md) | Historical |
| [P1 â€” Theme, icons and layout atoms](reports/mud-removal/p1-theme-icons-layout.md) | Historical |
| [P2 â€” Dialogs, popups, toasts and focus](reports/mud-removal/p2-overlays.md) | Historical |
| [P3 â€” Native form controls](reports/mud-removal/p3-form-controls.md) | Historical |
| [P4 â€” Structural components and shell](reports/mud-removal/p4-structural-components.md) | Historical |
| [P5 â€” Dependency removal and Release CSS](reports/mud-removal/p5-release-css.md) | Historical |
| [MudBlazor removal review fixes](reports/mud-removal/review-fixes-2026-10-06.md) | Historical |
| [Playback surface implementation evidence](reports/playback-surface-correction-2026-10-01.md) | Historical |
| [Player and shared controls: implementation evidence](reports/player-controls-2026-10-05.md) | Historical |
| [Player update verification report](reports/player-update-2026-10-03.md) | Historical — still cited as current player evidence by the Dashboard rules |
| [Player visual consistency](reports/player-visual-consistency-2026-10-05.md) | Historical |
| [Real-media harness implementation and execution](reports/real-media-harness-2026-09-26.md) | Historical |
| [Home Browse View and Player Remediation](reports/remediation-2026-10-04.md) | Historical |
| [Dashboard UI bugfix round — October 6, 2026](reports/ui-bugfix-round-2026-10-06.md) | Historical |

### Docs overhaul record (2026-10-08)

| Doc | Status |
|---|---|
| [Documentation overhaul inventory and factual baseline](docs-overhaul/README.md) | Historical |
| [Exact cleanup actions](docs-overhaul/cleanup-actions.md) | Historical |
| [Documentation overhaul implementation and acceptance](docs-overhaul/completion.md) | Historical |
| [Independent final review](docs-overhaul/final-review.md) | Historical |
| [Protected consumers and historical reference exceptions](docs-overhaul/protected-consumer-audit.md) | Historical — its protected-file rule is still current |

### Reference

| Doc | Status |
|---|---|
| [Sequence Dataset Gap Report (2026-07-15)](reference/sequence-dataset-gap-report-2026-07-15.md) | Historical |

### UI

| Doc | Status |
|---|---|
| [UI consistency audit compatibility record](ui/ui-consistency-audit.md) | Historical |

## Public documentation website (`docs/`)

Published pages are listed in `website/publication.json`; status comes from each page's front matter. Links below open the source file.

### Guides — Start here

| Page | Status |
|---|---|
| [Tuvima Library](../docs/index.mdx) | early-access |
| [Page Not Found](../docs/404.md) | current |
| [What is Tuvima Library?](../docs/explanation/what-is-tuvima.md) | current |
| [Getting started](../docs/tutorials/getting-started.md) | current |
| [Your first library](../docs/tutorials/first-library.md) | current |
| [Troubleshooting](../docs/guides/troubleshooting.md) | current |

### Guides — Install

| Page | Status |
|---|---|
| [Install with Docker Compose](../docs/install/docker.md) | current |
| [Install on Windows](../docs/install/windows.md) | early-access |
| [Install on Unraid](../docs/install/unraid.md) | current |
| [Install on Synology DSM](../docs/install/synology.md) | current |
| [Install on QNAP](../docs/install/qnap.md) | current |
| [Install on TrueNAS SCALE](../docs/install/truenas-scale.md) | current |
| [Run from source](../docs/install/from-source.md) | current |

### Guides — Use Tuvima

| Page | Status |
|---|---|
| [Find your way around](../docs/guides/dashboard-tour.md) | current |
| [Keep your place with For Me](../docs/guides/for-me.md) | current |
| [Play, listen, and read](../docs/guides/playback-and-reading.md) | current |
| [Use your Personal Space in View](../docs/guides/view-personal-space.md) | early-access |
| [Understand shelves, Collections, and universes](../docs/explanation/how-universes-work.md) | current |
| [Correct an item](../docs/guides/editing-items.md) | current |
| [Resolve review items](../docs/guides/resolving-reviews.md) | current |
| [Accounts, profiles, and recovery](../docs/guides/account-security.md) | current |
| [Set up a household](../docs/guides/households.md) | current |
| [Use Plugins](../docs/guides/using-plugins.md) | current |

### Guides — Set up and manage

| Page | Status |
|---|---|
| [Manage library settings and operations](../docs/guides/library-settings.md) | current |
| [Add media to your library](../docs/guides/adding-media.md) | current |
| [Configure metadata providers](../docs/guides/configuring-providers.md) | current |
| [Set language preferences](../docs/guides/language-setup.md) | current |
| [Roll out a local AI model](../docs/guides/local-ai-model-rollout.md) | current |
| [Secure Remote Access](../docs/guides/remote-access.md) | current |
| [Configure External Authentication](../docs/guides/external-authentication.md) | current |
| [Operations and recovery](../docs/guides/operations-and-recovery.md) | current |

### Guides — How it works

| Page | Status |
|---|---|
| [How the Entire Pipeline Works](../docs/explanation/how-the-pipeline-works.md) | current |
| [How File Ingestion Works](../docs/explanation/how-ingestion-works.md) | current |
| [How Two-Stage Enrichment Works](../docs/explanation/how-hydration-works.md) | current |
| [Understand which metadata wins](../docs/explanation/how-scoring-works.md) | current |
| [Understand local AI](../docs/explanation/how-ai-works.md) | early-access |
| [Documents in View](../docs/explanation/documents-in-view.md) | current |
| [How documents are read](../docs/explanation/how-documents-are-read.md) | current |
| [How documents are filed](../docs/explanation/how-documents-are-filed.md) | current |
| [Understand privacy and network access](../docs/explanation/privacy-local-first.md) | current |

### Develop — Contribute

| Page | Status |
|---|---|
| [Contribute to Tuvima Library](../docs/develop/contributing.md) | current |
| [Developer setup](../docs/tutorials/dev-setup.md) | current |
| [How to Build, Test, and Verify Changes](../docs/guides/running-tests.md) | current |
| [Keep repository builds small](../docs/guides/repository-storage.md) | current |
| [Shared AI storage](../docs/guides/shared-ai-storage.md) | current |
| [Writing documentation](../docs/develop/writing-docs.md) | current |

### Develop — Extend Tuvima

| Page | Status |
|---|---|
| [Build a Plugin](../docs/guides/building-a-plugin.md) | current |
| [Plugin directory format](../docs/reference/plugin-catalog.md) | current |
| [How to Add a New Metadata Provider](../docs/guides/adding-a-provider.md) | current |
| [Provider onboarding contracts](../docs/architecture/provider-onboarding.md) | current |
| [How to Write a New File Format Processor](../docs/guides/writing-a-processor.md) | current |

### Develop — Architecture

| Page | Status |
|---|---|
| [Technical Overview](../docs/architecture/technical-overview.md) | current |
| [Architecture Summary](../docs/architecture/architecture-summary.md) | current |
| [Project Boundaries](../docs/architecture/project-boundaries.md) | current |
| [API Boundaries](../docs/architecture/api-boundaries.md) | current |
| [Configuration Safety](../docs/architecture/configuration.md) | current |
| [Libraries, Sources, and Intake](../docs/architecture/library-model-and-intake.md) | current |
| [Ingestion Pipeline](../docs/architecture/ingestion-pipeline.md) | current |
| [Ingestion, identity and enrichment](../docs/architecture/ingestion-identity-enrichment-pipeline.md) | current |
| [Durable media operations](../docs/architecture/durable-media-operations.md) | current |
| [Hydration Pipeline, Provider Architecture and Enrichment Strategy](../docs/architecture/hydration-and-providers.md) | current |
| [Provider enrichment strategy](../docs/architecture/provider-enrichment-strategy.md) | current |
| [Priority Cascade Engine](../docs/architecture/scoring-and-cascade.md) | current |
| [Universal Parameterized Collection System](../docs/architecture/collections.md) | current |
| [Universe Graph](../docs/architecture/universe-graph.md) | current |
| [Canonical artwork architecture](../docs/architecture/artwork.md) | current |
| [View Personal Media Architecture](../docs/architecture/view-personal-media.md) | current |
| [Dashboard UI Architecture](../docs/architecture/dashboard-ui.md) | current |
| [Playback Architecture](../docs/architecture/playback.md) | current |
| [Inline Media Editing](../docs/architecture/inline-media-editing.md) | current |
| [JavaScript Interop Lifecycle](../docs/architecture/js-interop.md) | current |
| [Settings Architecture](../docs/architecture/settings.md) | current |
| [Dashboard Localization](../docs/architecture/localization.md) | current |
| [Security Architecture](../docs/architecture/security.md) | current |
| [Network and Remote Access](../docs/architecture/network-and-remote-access.md) | current |
| [Local AI Intelligence Layer](../docs/architecture/ai-integration.md) | current |
| [Local AI model portfolio](../docs/architecture/local-ai-model-portfolio.md) | current |
| [Performance and Large Libraries](../docs/architecture/performance-and-large-libraries.md) | current |

### Develop — Decisions and future state

| Page | Status |
|---|---|
| [Hybrid Asset Storage ADR](../docs/architecture/storage-policy-adr.md) | current |
| [Target State](../docs/architecture/target-state.md) | target-state |
| [Cross-Platform Display API](../docs/architecture/display-api.md) | target-state |

### Develop — Design system

| Page | Status |
|---|---|
| [UI Consistency Standard](../docs/design-system/ui-consistency-standard.md) | current |
| [Visual QA Checklist](../docs/design-system/visual-qa-checklist.md) | current |

### Reference — Configuration

| Page | Status |
|---|---|
| [Configuration Reference](../docs/reference/configuration.md) | current |
| [AI configuration reference](../docs/reference/ai-configuration.md) | current |
| [Providers Reference](../docs/reference/providers.md) | current |

### Reference — Media

| Page | Status |
|---|---|
| [Supported Media Types and Formats](../docs/reference/media-types.md) | current |
| [Artwork Types](../docs/reference/artwork-types.md) | current |
| [Secondary Title Text](../docs/reference/secondary-title-text.md) | current |
| [Wikidata Property Map](../docs/reference/wikidata-property-map.md) | current (protected file; status supplied at build) |

### Reference — Engine

| Page | Status |
|---|---|
| [Engine API Reference](../docs/reference/api-endpoints.md) | current |
| [Consumer Display API](../docs/reference/display-api.md) | target-state |
| [Database Schema Reference](../docs/reference/database-schema.md) | current |

### Reference — Project

| Page | Status |
|---|---|
| [Product Status](../docs/product/status.md) | current |
| [Beta Roadmap and Priority Order](../docs/product/beta-roadmap.md) | current |
| [Feature Truth Inventory](../docs/product/feature-truth-inventory.md) | current |
| [Native Client Delivery](../docs/product/native-clients.md) | early-access |
| [Current Presentation and Product Rules](../docs/product/presentation-rules.md) | current |
| [Why I built Tuvima Library](../docs/product/story.md) | current |
| [Report a security vulnerability](../docs/product/security.md) | current |
| [License](../docs/product/license.md) | current |
| [Attributions](../docs/reference/attributions.md) | current |
| [Glossary](../docs/reference/glossary.md) | current |
| [Supported devices and widths](../docs/reference/ui-supported-devices.md) | current |

### Unpublished files in `docs/`

| Doc | Status |
|---|---|
| [UI Consistency Audit](../docs/ui/ui-consistency-audit.md) | Historical — kept at this path because a test reads it |

## Root, agent and tooling docs

| Doc | Status |
|---|---|
| [README.md](../README.md) | Current — public project front page |
| [CLAUDE.md](../CLAUDE.md) | Current — instructions for every Claude session |
| [AGENTS.md](../AGENTS.md) | Current — developer code tour |
| [CONTRIBUTING.md](../CONTRIBUTING.md) | Current — mirrored to the docs site |
| [SECURITY.md](../SECURITY.md) | Current — mirrored to the docs site |
| [CODE_OF_CONDUCT.md](../CODE_OF_CONDUCT.md) | Current |
| [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) | Current |
| [src/MediaEngine.Web/CLAUDE.md](../src/MediaEngine.Web/CLAUDE.md) | Current — Dashboard code map and UI rules |
| [.claude/agents/builder.md](../.claude/agents/builder.md) | Current — helper agent |
| [.claude/agents/mechanic.md](../.claude/agents/mechanic.md) | Current — helper agent |
| [.claude/agents/reviewer.md](../.claude/agents/reviewer.md) | Current — helper agent |
| [.claude/agents/scout.md](../.claude/agents/scout.md) | Current — helper agent |
| [.claude/agents/verifier.md](../.claude/agents/verifier.md) | Current — helper agent |
| [.claude/rules/architecture-boundaries.md](../.claude/rules/architecture-boundaries.md) | Current — Claude rule file |
| [.claude/rules/build-and-ai-runtime.md](../.claude/rules/build-and-ai-runtime.md) | Current — Claude rule file |
| [.claude/rules/cloud-first.md](../.claude/rules/cloud-first.md) | Current — Claude rule file |
| [engineering/validation/batch.md](validation/batch.md) | Current — local validation batch list |
| [.claude/rules/dashboard-surfaces.md](../.claude/rules/dashboard-surfaces.md) | Current — Claude rule file |
| [.claude/rules/dashboard-surfaces-collections-tv.md](../.claude/rules/dashboard-surfaces-collections-tv.md) | Current — Claude rule file (split from dashboard-surfaces) |
| [.claude/rules/dashboard-surfaces-editor.md](../.claude/rules/dashboard-surfaces-editor.md) | Current — Claude rule file (split from dashboard-surfaces) |
| [.claude/rules/dashboard-surfaces-home-player.md](../.claude/rules/dashboard-surfaces-home-player.md) | Current — Claude rule file (split from dashboard-surfaces) |
| [.claude/rules/dashboard-surfaces-settings-setup.md](../.claude/rules/dashboard-surfaces-settings-setup.md) | Current — Claude rule file (split from dashboard-surfaces) |
| [.claude/rules/data-store.md](../.claude/rules/data-store.md) | Current — Claude rule file |
| [.claude/rules/dependencies.md](../.claude/rules/dependencies.md) | Current — Claude rule file |
| [.claude/rules/docs.md](../.claude/rules/docs.md) | Current — Claude rule file |
| [.claude/rules/model-routing.md](../.claude/rules/model-routing.md) | Current — Claude rule file |
| [.claude/rules/product-owner.md](../.claude/rules/product-owner.md) | Current — Claude rule file |
| [.claude/rules/security.md](../.claude/rules/security.md) | Current — Claude rule file |
| [clients/android/README.md](../clients/android/README.md) | Current |
| [clients/apple/README.md](../clients/apple/README.md) | Current |
| [clients/contracts/v1/README.md](../clients/contracts/v1/README.md) | Current |
| [clients/roku/README.md](../clients/roku/README.md) | Current |
| [clients/tv-web/README.md](../clients/tv-web/README.md) | Current |
| [clients/tv-web/platforms/tizen/README.md](../clients/tv-web/platforms/tizen/README.md) | Current |
| [clients/tv-web/platforms/webos/README.md](../clients/tv-web/platforms/webos/README.md) | Current |
| [deploy/tailscale/README.md](../deploy/tailscale/README.md) | Current |
| [scripts/css/README.md](../scripts/css/README.md) | Current |
| [scripts/visual-qa/css-ownership/README.md](../scripts/visual-qa/css-ownership/README.md) | Current |
| [scripts/visual-qa/home-media-cards/README.md](../scripts/visual-qa/home-media-cards/README.md) | Current |
| [scripts/visual-qa/player-update/README.md](../scripts/visual-qa/player-update/README.md) | Current |
| [src/MediaEngine.Web/wwwroot/lib/foliate-js/PINNED.md](../src/MediaEngine.Web/wwwroot/lib/foliate-js/PINNED.md) | Current |
| [src/MediaEngine.Web/wwwroot/vendor/hls/README.md](../src/MediaEngine.Web/wwwroot/vendor/hls/README.md) | Current |
| [src/MediaEngine.Web/wwwroot/vendor/pdfjs/README.md](../src/MediaEngine.Web/wwwroot/vendor/pdfjs/README.md) | Current |
| [tests/smoke/README.md](../tests/smoke/README.md) | Current |
| [tools/ffmpeg/README.md](../tools/ffmpeg/README.md) | Current |
| [tools/tesseract/README.md](../tools/tesseract/README.md) | Current |
| [tools/test-data/SCENARIOS.md](../tools/test-data/SCENARIOS.md) | Current |
| [tools/test-data/TEST-RESULTS.md](../tools/test-data/TEST-RESULTS.md) | Historical — test runs from March 2026 |
| [tools/test-data/VIEW_PHOTO_HARNESS.md](../tools/test-data/VIEW_PHOTO_HARNESS.md) | Current |
| [website/LICENSES.md](../website/LICENSES.md) | Current |
| [website/README.md](../website/README.md) | Current |
| [tools/reports/](../tools/reports/) | Historical — 471 generated QA report files |
