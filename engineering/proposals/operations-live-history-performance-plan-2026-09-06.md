---
title: "Operations Live and Historical Ingestion Plan"
summary: "Complete the shared Operations experience and validate responsive browsing of large ingestion histories."
audience: "developer"
category: "proposals"
product_area: "ingestion"
tags:
  - "ingestion"
  - "activity"
  - "performance"
---

# Operations live and historical ingestion plan

Status: implementation in progress, updated September 7, 2026. Server-side paging, shared historical/live cards, URL state, and the primary responsive layout are implemented. Product review consolidated batch history into Ingestion and removed the separate Activity & Audit route. A sustained real-ingestion observation and the full multi-sample browser performance matrix remain outstanding.

Initial implementation evidence:

- Historical batch pages now select a bounded page of group identities before loading title, artwork, facets, children, and operations. Operation projection is limited to entities represented on that page.
- Finished-batch previews select at most the requested preview groups per listed batch. Expanded detail loads six preview groups instead of constructing every group in the run.
- Cross-run Recently Added filtering and paging now occur before expensive media-card projection. Activity summary counts use aggregate SQL rather than rebuilding all historical cards.
- Operations artwork URLs request small renditions for history previews and medium renditions for grids; browser image decoding is asynchronous.
- Expanded batch media now uses the same `IngestionMediaCard`, facet renderer, and Operations drawer as live ingestion.
- Selecting a finished batch now opens that shared media browser directly. Search, lane, newest/oldest sort, Cards/List, page, and selected-item state are encoded in the URL and survive refresh/navigation.
- Ingestion shows the three most recent batches beneath live work, provides search and quick outcome filters, and appends ten older rows at a time. There is one batch list and one batch detail destination.
- Historical child labels use only batch-scoped facts. Catalogue totals are no longer presented as ingestion denominators, and a facet says queued only when a durable queued operation exists.
- Completed child counts use distinct media identities instead of asset rows or track positions. Duplicate Queen assets therefore report five tracks added, while a single-part audiobook such as Dune no longer shows a vague files-added line.
- Groups without a real display title are withheld even when artwork has arrived; internal `Identifying media` placeholders no longer enter finished media lists or counts.
- Green, blue, purple, amber, and red state accents are implemented on the shared media cards and finished-run rows. Completed metadata and artwork indicators are green, and ready cards omit the redundant `Ready` label. Authenticated browser inspection confirmed working small history thumbnails, medium grid renditions, responsive type alignment, 50-card DOM bounds, truthful labels, and the computed state colors.
- A dedicated 10,000-group completed-run fixture measured a warm deep 50-group page at 256–473 ms across local runs and enforces the existing two-second performance guardrail. The broader 30-sample/browser matrix below is still required before the full plan is complete.

This proposal updates the remaining presentation and verification work described in the September 5 Operations proposals. Existing lifecycle, audit, authorization, and recovery requirements remain applicable.

## Outcome and visual interpretation

An administrator can see real ingestion progress, quickly open a large previous run, and browse exactly what that run added. Large history must remain responsive while a new ingestion is running.

- Image 1 defines the shared View All composition: prominent search, scope and filters, compact totals, artwork-led cards, and paging. Its example counts and arbitrary file/chapter denominators are illustrative, not product rules.
- Image 2 defines compact completed-run rows and the restrained summary/filter hierarchy within the single Ingestion page; do not reproduce the mockup's alternative global sidebar.
- Image 3 defines live card hierarchy: dominant correctly proportioned artwork, thin state accent, title/byline/type, truthful child progress, and small labeled facet indicators. Apply the attached written semantic color rules where the images differ.
- Use existing tokens and Material Outlined icon helpers. Processing and finishing details use blue/purple; ready uses green; ordinary waits use muted neutral colors; actionable review uses amber; terminal failures use red. Text and icons always accompany color. No status pills or large colored backgrounds.
- Preserve durable audit facts in backend records and expose relevant details from the selected batch or diagnostics. Do not add a separate page-level timeline mode.

## What repository inspection established

These are source findings, not measured attribution of the reported lag.

| Finding | Evidence and implication |
| --- | --- |
| Media paging occurs after full materialization | `IngestionPresentationReadService.GetCurrentMediaAsync`, `GetBatchMediaAsync`, `GetRecentAdditionsAsync`, and `GetChildrenAsync` load full scopes before `Page`. A 50-card response does not bound database or server work. |
| Small previews still project whole runs | `GetBatchPresentationAsync` loads all groups before taking six; `GetBatchMediaPreviewsAsync` loads all additions across the selected batches before trimming each preview. |
| Live and history summaries load full history | `GetSnapshotAsync` loads current and historical groups; `GetActivitySummaryAsync` loads all historical groups for a few totals. `ActivityBatchExplorer` awaits that summary before loading its list or requested run. |
| Group projection repeats expensive work | `LoadRowsAsync` includes broad latest-log/operation window queries and per-row metadata subqueries. `BuildGroup` filters the complete operations list for every group. Query plans must establish which scans SQLite actually performs. |
| Operations images request unsized artwork | `BuildGroup` emits `/stream/artwork/{id}`. The stream endpoint supports `size=s/m/l`, but missing renditions currently fall back to the original. Merely adding a query parameter is insufficient validation. |
| Full grids differed | Before implementation, live ingestion used `IngestionMediaPagedView` while Activity rendered a separate historical grid. The shared cards, facets, drawer, and pager needed to serve both routes. |
| URL state is incomplete | History parses `runId` during initialization, but row expansion and View All remain local state. Live uses `view=current/history`; paging/filter state is local, and `OnParametersSetAsync` reloads page zero. |
| Live refresh has concrete gaps to test | Presentation signatures omit artwork, expected count, and facet fields; group timestamps derive from file/log timestamps. Reconnect currently notifies without explicitly refreshing. Trailing debounce can defer refresh during continuous events. The backend sorts by `UpdatedAt` before selecting the visible set, limiting what the client can stabilize. |
| Historical results are reconstructed | Addition membership is inferred from logs and `presented_at`, then joined to current catalogue and operation state. Later metadata changes, pruning, or deletion can alter the reconstructed history. |
| Progress labeling is partly corrected | The current Ingestion page explicitly labels its bar File intake. Preserve this improvement while verifying complete-run semantics and separating readiness from optional enrichment. |

Existing grouping and pagination tests use small fixtures and validate returned counts. They do not demonstrate large-run performance, database-level pagination, or live browser progression. Existing local edits to cards, CSS, and the adding-media guide must be preserved during implementation.

## Delivery sequence

### 1. Capture a reproducible performance baseline

Before implementation, stop Engine/Web as required by the repository, then start a controlled baseline. Capture the current desktop surfaces at 1920×1080. Use isolated disposable test databases and synthetic/copied media fixtures; never alter user-owned source media.

Measure the finished-run list, expansion of the largest available run, its first and deep View All pages, a selected item's children, and filter changes. Repeat while another ingestion is active. Separate database time, API time, payload bytes, browser image transfer/decode, Blazor rendering, and visible interaction delay. Include the summary request that currently blocks the initial history list.

Create reproducible fixtures with 100, 1,000, and 10,000 groups in one run, including albums/episodes with substantial child counts, plus 100,000 groups spread across history. Include a run with roughly 100,000 files/operation records and multiple operations per item. Record machine, build, fixture seed, exact URLs, and warm versus application/browser-cold conditions. Do not claim an OS disk-cold measurement unless actually controlled.

Deliverable: baseline results and browser traces sufficient to distinguish server delay from image and rendering lag.

### 2. Establish durable run facts and bounded storage queries

Extend existing batch/artifact infrastructure with normalized, indexed run-group and member projection records where existing rows cannot express the required facts. Keep the current durable batch ID across interruption/resume. Write membership and outcome facts idempotently when an item is admitted/presented, and settle historical results when required run work finishes. Do not maintain a second competing ingestion lifecycle.

Store stable run/group/member identity, first-seen order, lane/type, run-scoped additions, distinct child unit/count, denominator provenance/scope when known, and historical display/result facts. Persisted membership and historical counts must survive log retention and later catalogue removal; deleted media may retain historical text with unavailable navigation/artwork. Current unresolved follow-up is a separate live fact, so a recovered retry does not permanently warn on an otherwise successful historical run.

Keep file, group, work, and child counts separate. An album has one card; alternative encodings do not create extra tracks. TV uses canonical show identity with run-specific season/episode counts, never a lifetime show total as a season denominator. Audio file counts must not be labeled chapters without actual embedded-track evidence. Comics show issues added without a speculative completion target. Unknown denominators remain null. Do not group by normalized display titles.

Move touched SQL-heavy behavior into Storage repositories behind inward-facing contracts. API read services map repository projections into the canonical `MediaEngine.Contracts` DTOs.

- Filter scope and choose group IDs in SQL before loading display details. Page groups, not file rows; do not split an album across pages.
- Use the same predicates for total counts and page contents, with deterministic sort tie-breakers. Bound page sizes to 50 by default and 100 maximum.
- Return five small preview records per listed run and six to eight for the expanded run through bounded SQL, with aggregate counts calculated separately.
- Fetch item children using their selected run/group directly and page them in SQL.
- Scope metadata/operation lookups to the selected IDs, and use indexed joins/lookups instead of scanning all operations once per group.
- Compute summary counters from indexed aggregate/run projection records. Live polling must not rebuild historical media cards.
- Inspect `EXPLAIN QUERY PLAN`, including deep pages and search. Reuse existing indexes; add composite indexes only where the access pattern requires them. Avoid wrapping indexed time predicates in conversion functions unnecessarily.
- Add cancellation through endpoints, read services, and Dapper commands. Do not make status GET requests mutate ingestion completion.

Follow the pre-beta cutover rule for any incompatible schema change: update bootstrap/schema validation, fail clearly on obsolete disposable state, and reingest fixtures. Do not add compatibility migrations or reconstructive legacy fallbacks.

Deliverable: bounded list/preview/detail/media queries and durable, truthful historical additions with API/storage regression coverage.

### 3. Complete authoritative live state and progress

Keep availability separate from enrichment activity: an item can be Ready while optional artwork/lyrics work continues. Required identity, readiness, child processing, retries, and organization/finalization continue to block successful run completion until discovery is closed and required work is settled. Review and terminal failure outcomes must remain explicit; unrelated optional maintenance must not keep the run active forever.

Continue to label the determinate bar File intake. For the whole run, show an indeterminate working indicator and active/queued/waiting counts unless one authoritative denominator covers every required descendant. Do not average stage units, choose the maximum job percentage, or cap synthetic progress at 99%.

Extend shared contracts with typed availability/facet state, count provenance, stable order, and a monotonic projection revision scoped to a run/snapshot. A timestamp taken after a slow query is not sufficient ordering protection. Return counters and rows from a consistent read snapshot. Preserve existing wire conventions and update contract snapshots explicitly.

Make SignalR changes invalidate the authoritative current summary and the visible live page. Use a bounded throttle/coalescing interval, not a debounce that can starve under continuous events; retain polling as recovery. Force a fresh snapshot on reconnect. Reject stale responses by query generation and projection revision. Release live rendering from unrelated audit/review requests, and load technical panels on demand.

Merge visible cards by stable `(batchId, groupId)`, updating counts, artwork, expected totals, and every facet in place. Preserve admission order before selecting the bounded eight-card live preview. Admit new groups to available slots; retain completed cards briefly before replenishing slots. In View All, preserve page, scroll, focus, and drawer state. For status-filtered pages, update visible states immediately and show a refresh-results cue when membership changes rather than abruptly reflowing the page. A refresh adopts the new membership and count.

Deliverable: a real or representative ingestion visibly advances album and TV child counts and facet state, including under sustained events and reconnects.

### 4. Finish the shared surfaces and URL state

Extend `IngestionMediaPagedView` to accept a typed live/run scope and use it from both live Ingestion and Activity's historical run route. Reuse `IngestionMediaCard`, `IngestionFacetIndicators`, and `IngestionMediaDrawer`. Extract run row/preview pieces only where this removes repeated markup; reuse central media/icon formatting.

- Live summary: compact truthful totals, eight active cards with larger art, one Scan now action, operational waits distinct from actionable attention, then the three latest batch rows with a link to complete Activity history.
- Finished list: 25 paged rows with date/time, result icon/text, run name, files processed/media added, five small covers and remainder count, and View details.
- Finished batch selection: open the shared historical media browser directly. Keep run-level technical records out of the primary browse flow; individual card details remain available in the Operations drawer.
- View All: identical cards and filters for both scopes, 50 groups per page, accurate range/total, page navigation, search, source/run, All/Read/Watch/Listen, applicable status, and sorting. Implement the mockup's Cards/List control as two layouts over the same query and row model.
- Keep one semantic card target opening its Operations drawer; the drawer provides canonical media navigation and supported review context. Provide loading, empty, unavailable/deleted, error/retry, and disconnected states.

Use these canonical URLs; replace current internal links without adding legacy aliases:

| State | URL |
| --- | --- |
| Live summary | `/settings/ingestion` |
| Live View All | `/settings/ingestion?view=all` |
| Batch history | `/settings/ingestion` |
| Historical batch | `/settings/ingestion?runId=<guid>&view=all` |
| Selected group | Add `itemId=<groupGuid>` and explicit `runId` when needed in live scope |

Serialize `q`, `lane`, `status`, `sort`, `page`, `layout`, and list date range in query state. Route/source selects navigate to the appropriate live or run scope. Keep any retained Recently Added day browse explicitly date-scoped; it must not masquerade as one completed run. Query changes are observed after initialization. Push meaningful navigation and replace debounced search updates to avoid filling browser history. Back/Forward/refresh restore the expanded run, filters, page, layout, and drawer; closing the drawer removes only its selection parameters. Guard asynchronous responses when switching runs quickly.

### 5. Use actual image renditions and verify layout

Use the existing managed artwork route and size helper, with small renditions for collapsed history (48–72 CSS px), small or medium for expanded previews (90–130 px), and medium for live/View All (180–240 px). Preserve square album, portrait cover, and wide still ratios. Reserve image space, decode asynchronously, lazy-load below the fold, and show settled placeholders on missing or failed images.

Audit the stream endpoint's original fallback: Operations must receive a suitable rendition or a placeholder while rendition repair completes, not silently decode an original. Verify actual response dimensions/bytes, not only URL parameters. Reuse rendition repair and caching infrastructure; artwork URLs change when the managed asset revision changes, not on every poll.

Validate all affected desktop surfaces at 1920×1080, a shorter desktop viewport, and mobile. Check keyboard navigation, focus retention, expanded-row placement, tooltip/accessible facet labels, reduced motion, and fixed image geometry. Mobile must retain scan access and the same authoritative state.

## Acceptance gates: performance is part of completion

The following are proposed implementation targets on the recorded local reference machine, not results already achieved. Collect at least 30 samples for each warm interaction and five application/browser-cold runs; report p50/p95, payload bytes, query counts, and memory/render observations. Timings are a dedicated benchmark gate; avoid fragile wall-clock assertions in ordinary unit tests.

| Scenario | Target |
| --- | --- |
| Batch list, expanded summary, or 50-group page API | Warm p95 at most 500 ms, including the largest fixture |
| Cold navigation to usable history/list/grid | At most 2 seconds; text and controls must not wait for all artwork |
| Warm expand/page/filter interaction | Visible result within 1 second after request/debounce; loading feedback within 100 ms |
| Large-history scaling | The same selected page stays within these targets as unrelated history grows; no full-scope row materialization or per-item request fan-out |
| Payload/DOM bounds | At most 50 grid cards, eight expanded previews, five covers per listed run; technical records and children load only when requested |
| Image transfer | No original artwork responses for Operations cards/previews; no fetches for unrequested media pages; lazy loading may fetch the browser's near-viewport buffer |
| Image/layout stability | No geometry shift as images resolve; measured layout shift below 0.1 |
| Browser responsiveness | No repeatable main-thread stall above 200 ms from opening/paging runs; trace and remove long tasks caused by bulk rendering or image decode |
| Live update latency | Committed state visible within 2 seconds under healthy SignalR, within one documented fallback polling interval otherwise; reconnect refresh begins immediately |
| Repeated navigation | After 20 expand/collapse/page/drawer cycles, request subscriptions and retained card state remain bounded; no monotonic memory growth from abandoned pages |

Run the entire performance matrix while ingestion writes continue. If a gate fails, retain the task as incomplete, identify the slow layer, and repeat after correction. A faster small batch or a static screenshot does not close the reported performance issue.

## Correctness and regression matrix

- Album progression 1/17 → 2/17 retains one card identity; TV child counts behave equivalently. Duplicate formats, unknown totals, comics, incremental seasons, and embedded audiobook tracks retain correct units and scope.
- File intake reaches its total while required descendants/retry waits remain: run still working, no overall 100% or success state. Optional enrichment can continue while the item is Ready.
- Artwork/facet-only changes render even when file counts and file timestamps do not change. Continuous SignalR traffic does not starve refresh; reconnect, missed events, delayed older requests, and rapid scope switches preserve the latest state.
- Historical membership/counts remain unchanged after later ingestion, metadata regrouping, log pruning, or removal of catalogue media. Current follow-up clears after recovery without rewriting historical outcome facts.
- Returned counts, ordering, and filters are correct across first, middle, final, empty, and deep pages. Tests prove bounded materialization/query behavior, not just a 50-item response.
- Deep links, refresh, Back/Forward, expansion persistence, selected drawer, and preserved page/scroll pass browser tests for both live and historical views.
- Rendition tests cover missing renditions and cache refresh as well as URL selection. Network traces verify response dimensions and lack of page-wide original downloads.

Extend the existing API presentation/activity tests, Web state/component tests, and contract guardrails. Run `dotnet restore MediaEngine.slnx`, `dotnet build MediaEngine.slnx --no-restore`, and `dotnet test MediaEngine.slnx --no-build` for implementation, followed by the dedicated benchmark and visual/live browser checks. Update current product guides and relevant AGENTS/CLAUDE guidance when behavior changes; run documentation checks for those changes.

Completion evidence consists of before/after timing tables, SQL plans and bounded query evidence, browser network/performance traces, visual captures, and an observed multi-step live ingestion sequence. Until that evidence exists, describe the lag as investigated, not rectified.

## Product-owner summary

The plan makes old ingestions quick to open by loading only the visible results and appropriately sized covers. It gives live ingestion and past runs the same clear media cards, preserves shareable views, and shows honest progress. Delivery is complete only when large runs are measured to be responsive and live cards are observed updating correctly.
