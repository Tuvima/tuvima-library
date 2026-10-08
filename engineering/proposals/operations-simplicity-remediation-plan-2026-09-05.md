---
title: "Operations Simplicity Remediation Plan"
summary: "Simplify Activity into operation rows with local summaries and rebuild Ingestion around a responsive connected stage strip."
audience: "developer"
category: "proposals"
product_area: "ingestion"
tags:
  - "ingestion"
  - "activity"
  - "settings"
---

# Operations Simplicity Remediation Plan

Status: implemented, September 5, 2026. The operation-first Activity list, exact-run navigation, bounded media inspection, phase-specific Ingestion summary, connected stage controls, outcome signals, and compact monitoring layouts are in the current implementation.

The implementation deliberately omits a page-level Activity timeline, global Media/Enrichment/Data view modes, the raw technical-event ledger, synthetic whole-run progress, duplicate refresh controls, and the Processing details disclosure. Successful results use a completion icon; review, warning, interruption, and failure states retain their own colored visual signals. Selecting an Ingestion stage or Activity category highlights its trigger and points into the in-flow panel below it.

This supersedes the page-level detail lenses and presentation decisions in [the earlier plan](./ingestion-activity-experience-plan-2026-09-05.md). Existing lifecycle requirements remain acceptance criteria, not proof of completion. The earlier implementation report overstated completion: only a compact rendered viewport was checked, and code inspection reveals unresolved navigation, counting, and inspection defects.

## Product decisions

- Ingestion answers what is happening now. Activity answers what happened in an operation and lets the user inspect the relevant part of that operation.
- Keep the screenshot's alignment, compact rows, restrained colors, connected stages, and panels immediately below their trigger. Do not copy its repeated tabs, duplicated percentages, sample counts, technical log, or every nested control.
- Activity has no page-level Overview / Media / Enrichment / Data & Downloads switch. Each operation owns labeled icon-and-count buttons that open its summary below that row. Icons are useful entry points, not unlabeled puzzles.
- True filters change the operations in the list. Opening Media or People in one row changes only that row's detail.
- Keep one Operations destination and the adjacent Ingestion, Needs Review, and Activity & Audit navigation. Keep the unresolved review count beside Operations and Needs Review.
- Mobile remains an optional, read-only monitoring summary; full investigation and review resolution remain for larger screens.

## Confirmed gaps in the current code

| Finding | Evidence and consequence | Required correction |
| --- | --- | --- |
| View all activity opens one run | `IngestionTasksTab.razor` binds that label to `CurrentActivityHref`, which appends `runId`. | All-activity navigation must use `/settings/activity`; reserve run links for explicitly scoped labels. |
| Run selection becomes a hidden filter | `ActivityBatchExplorer.OnInitializedAsync` copies `runId` into `_query.Search`, retains the last-seven-days restriction, then expands only if that query returned the batch. | Separate exact run selection from list search. Old-run links must work independently of the default date range. |
| Global detail modes add a second navigation layer | Explorer owns `_lens`; Inspector tells the user to change the control above every operation. | Replace global lens state with selection keyed by operation and local category. |
| The history is a stack of summaries rather than a scannable list | Each card repeats a date, duration, ID, badges, sentence, and media mix before expansion. | Use aligned desktop rows with concise, consistently positioned facts. Move breakdowns into the selected row. |
| Icons do not open summaries | Media counts and overview metric icons are rendered as spans/articles. | Make relevant icon/label/count groups semantic buttons with selected and expanded state. |
| Inspection stops at item 25 | Inspector requests offset 0, limit 25 and renders a note when `HasMore`; it has no next-page action. | Add working server paging and batch-local item search, result, and media filters. |
| Media counts can actually be file counts | Read service maps `files_total` to `ItemCount`; UI takes the maximum of that, titles, and identified counts. | Define distinct media membership independently from files; remove maximum-of-different-units presentation. |
| Review status replaces media identity | `GetGroupsAsync` uses `Needs Review` as a media-type group. | Review is an outcome dimension; a book needing review remains a book in the media mix. |
| Download/enrichment numbers describe operations | Insights counts operation-type name matches without restricting them to successful results. | Distinguish scheduled work, completed output, attempts, assets, people, and bytes. Do not label an attempted artwork operation as an asset downloaded. |
| Filters lack reliable scope and recovery | Source choices come from the current page; no Clear filters action; activity type maps to raw event names; requests have no selection revision guard. | Use actual operation kinds and complete filter facets, explicit reset, and protection against stale responses. |
| Generic operation naming hides job identity | History reads `ingestion_batches` and titles every row Library Ingestion. | Inventory actual recorded job types; display typed names. Include admin/review jobs only when backed by real durable records. |
| Stages intentionally wrap | Ingestion CSS switches four stages to two columns at 1180px. One absolute connector spans the strip, with transparent per-stage pseudo-connectors. | Four stage nodes remain in one row at desktop/tablet widths, with three distinct connector segments. |
| Stages cannot open a detail panel | Stage markup is noninteractive; active-work cards sit above it and are repeated in Work in progress below. | Select a stage to open one in-flow panel immediately below the strip. Remove repeated live-work cards. |
| Stage state and counts are assembled from different scopes | Stages use positional slices of `Model.Steps`; `DisplayBatch` is selected from four recent batches; overall progress comes from broader dashboard projections. | Use stage keys and one selected-run snapshot for all visible state, counts, media mix, and activity. Fetch active runs independently of recent history limits. |
| Overall percentage is still misleading | `BuildOverallProgress` sums stage counts with different units, can infer 100% from file settlement, and caps active results at 99%. | Remove the synthetic overall percentage; show measured progress only for the labeled phase/category until an authoritative run denominator exists. |
| Batch reuse is heuristic | Watcher reuses a running batch based on source-path matching, including Multiple source folders. Resume correctly uses tracked `BatchId`, but unrelated work can still be joined by scope. | Preserve explicit intake/run ownership and define when intake closes. Do not merge history visually by title or time. |

Principal files: `Components/Activity/ActivityBatchExplorer.razor`, `ActivityBatchInspector.razor`, `ActivityBatchItemInspector.razor` and their CSS; `Components/Settings/IngestionTasksTab.razor` and CSS; `Services/Integration/IngestionLiveDashboardState.Projection.cs`; API `ActivityBatchReadService.cs` and `IngestionOperationsStatusService.cs`; ingestion `IngestionEngine.Watching.cs`.

## Activity: one list, one local inspection surface

### Default screen

Use the shared Operations shell at the screenshot's compact scale. One filter area sits above a single aligned list. The initial controls are Search, Date, and Result; More filters contains Media type, Operation type, and Source. Media type remains a real optional list filter, separate from the removed Media detail mode. Refresh sits once in the header; Clear filters appears when state differs from defaults.

Use a shared-control composition that can omit the embedded Refresh button: `AppFilterBar` currently always renders it. Avoid hiding duplicate controls through page CSS.

Desktop columns: Operation, Started, Duration, Files, Media items, Summary/attention, Expand. The title may have one source/trigger subtitle. Long titles truncate with the full text accessible; numbers align consistently. Batch IDs belong inside detail with a copy action, not every collapsed row.

Each row includes a small set of local entry points: Media, Enrichment, Data & downloads, plus Attention when nonzero. Each is an icon with a short label and a count only when the unit is meaningful. If the available width cannot fit those actions alongside the facts, place them on a deliberate second row within that operation, preserving the column alignment above. Do not let arbitrary wrapping dictate the layout.

Successful completion uses a green check and an accessible status label. Attention adds a warning/review cue and its count. A completed scan with an uncertain match stays complete for automation. Reserve red failure for terminal technical failure. Waiting, interrupted, cancelled, and unknown must not appear as successful completion. Use plain text where an icon would otherwise be ambiguous; do not restore status pills.

### Click behavior

| Trigger | Result |
| --- | --- |
| Expand chevron or operation title | Open a compact overview directly below that row. |
| Media icon/label/count | Open that operation's media summary; View items opens its paged item list inside the same panel. |
| Enrichment icon/label/count | Open a left-aligned summary of people, artwork, relationships, and other recorded outputs. |
| Data & downloads icon/label | Open actual downloaded assets/bytes and provider outcomes when recorded. Show unavailable metrics as unavailable, never zero. |
| Attention count | Open the affected items and reasons for that operation; review resolution uses the existing review/editor flow. |
| Selected trigger again | Collapse its panel. |
| Another category or operation | Replace the single open panel; do not stack multiple expanded dashboards. |
| Item details | Show one concise item inspector within that operation, with Back to items and preserved paging/filter state. |

The panel has a heading, close action, selected-trigger treatment, and subtle top marker associated with the clicked entry point. It is in normal document flow and spans the row width. It does not float over adjacent operations. Keyboard Enter/Space opens it; `aria-expanded` and `aria-controls` expose the relationship; closing restores trigger focus. No hover-only content and no nested tabs or sidebars.

Overview contains the meaningful batch totals plus compact Media, Enrichment, and Data summary rows that can open those categories. Avoid repeating the complete header statistics and six large metric boxes. Category summaries align icon, label, result, and count in rows; avoid centered multi-card arrangements. Normal admins should not need technical logs to answer what changed.

Item inspection is progressive: batch summary, paged items, selected item's result. Search/filter/paging must cover all items, including item 26 and beyond. Use server-side paging with 25 initial rows, cancellation/revision guards, per-panel error/retry states, and explicit empty/not-recorded states. A missing detail response must not leave an endless spinner. Do not fetch item evidence, people arrays, or raw events for collapsed rows.

## Navigation contract

- **View all activity** always opens `/settings/activity` with the standard date scope and no inherited run selection or hidden search term.
- **View this run** or a named recent-run link opens `/settings/activity?runId=...` and selects that exact run. An explicit run-detail lookup must work for a run older than seven days or outside the current list page; show the selected run in a clearly scoped panel with Back to all activity. Do not silently substitute run ID text into search.
- A failure/attention link may additionally select the relevant local category. Invalid or missing runs produce a clear message and an all-activity link.
- Persist actual filters in readable query parameters; distinguish selected run/category/item from list filtering. Browser Back restores the prior list scope, page, and selection. Clear filters clears filters; Back to all activity clears explicit run selection.
- Navbar ingestion activity continues to open Ingestion. Operations/Needs Review counts reflect the global pending-review count; run review summaries use that run's own outcomes.

## Ingestion: connected stages and useful panels underneath

### Visual hierarchy

1. One compact shared header and action area: Processing settings and one manual Scan action. Ingestion refreshes automatically. Keep the existing navigation link to Activity; avoid repeating it beside every section.
2. Current-work card: plain-language state, named current phase/work, elapsed time, active/queued/retry counts, and start time. Use the screenshot's left identity/right facts balance with a clear divider and restrained icon treatment.
3. A separate connected stage panel: Discover → Identify → Enrich → Organize, with its selected-stage detail immediately underneath.
4. Compact outcomes: Files checked, Added or updated, Needs review, Failed, all scoped to the same run. No percentage decorations on outcome cards.
5. Media in this run and Up next/Needs attention form the lower summary. The selected-stage panel becomes the one primary place for Work in progress; remove the duplicate active-work tiles/list.
6. A short recent-results area may show at most five useful changes or named run summaries when available. It must not repeat the stage progress or introduce per-person log noise. Its View all activity link is unscoped.

Preserve the screenshot's compact density and alignment, not its duplicate donut-plus-bar percentage. Until a whole-run denominator is trustworthy, use the circle for active/completed/waiting state and put any measured bar in the selected phase's detail with its unit. If a truthful aggregate is later available, choose one percentage visualization and label its scope.

### Stage structure and responsiveness

Implement a reusable stage strip with four keyed semantic buttons and three dedicated connector elements. Prefer a grid with explicit alternating node/connector tracks. Lines stop at nodes; they never cross labels. Complete connectors may be green, an active node purple, and unstarted work muted. Color supplements accessible status. Stage completion depends on required work settling, not on selection or the next stage starting; stages may overlap.

Size against the Operations content container, including the Settings rail and padding, rather than viewport width alone:

- At 1100 CSS pixels or more of content width: full stage labels, short descriptions below, 40–48px visual icons inside 44px-or-larger controls, and clear connector space.
- At 600–1099px of content width: keep all four nodes on one line, retain unbroken labels, reduce spacing, and move descriptions/status explanations into the selected panel. Never use a two-by-two stage grid. Allow the detail panel's own facts to use two columns.
- Below 600px: the read-only mobile summary uses a Stages disclosure with compact vertical rows. This is an intentional layout switch, not wrapped desktop nodes. Do not force horizontal page scrolling or shrink labels into unreadable text.

Use one stage dataset and shared rendering primitives across compact/full presentations. An optional marker points from the selected stage into a full-width detail panel underneath; it is positioned relative to the stage grid, and remains within the panel bounds. Detail stays in flow, including at 200% zoom. Connector behavior and no-wrap labels need browser layout checks, not just source-string tests.

### What each stage opens

| Stage | Summary underneath |
| --- | --- |
| Discover | Watched/source scope, files discovered/checked, changed/unchanged/skipped where recorded, active file checks, and whether discovery is still open. |
| Identify | Media matched, queued/active identification, uncertain results handed to review, and relevant provider wait. |
| Enrich | Compact People & cast, Artwork, Metadata, Relationships, and other enabled/recorded work rows; each has its own unit and active/queued/completed state. |
| Organize | Library placement and shelf/group finalization counts, relationship finalization where actually owned by this stage, and remaining organization work. |

Assign each operation to one canonical stage/category. Relationship discovery and final organization must not double-count the same work. Clicking a category can replace the selected stage's summary with a short category breakdown and Back to stage; named items belong in Activity.

Initially select the active stage with an intelligible summary. When multiple stages are active, indicate each and choose a primary focus without implying strict serial execution. After manual selection, live updates preserve that selection until the user chooses Follow current stage or changes runs. Completion must not dismiss a panel while the user is reading it.

## Structural work required for truthful summaries

1. **Shared selected-run projection.** Give Ingestion, Activity, and navbar the same durable run identity, revision, lifecycle, stage keys, count units, and outcomes. Replace positional stage mapping and selection from a four-row recent-history slice. Explicitly identify multiple active runs; never combine one run's heading with another run's counts.
2. **Independent measures.** Count files, distinct media identities/members, new/updated items, unique people, associations, successful downloaded assets, requests, and bytes separately. Define media membership at the owned item/edition level consistently with the item list; unclassified inputs remain explicitly unclassified, not invented media. Sidecars, retries, and repeated paths cannot inflate media totals. Deduplicate aggregation joins and use canonical media classification with separate review state.
3. **Correct progress contract.** Known phase totals support a phase-labeled percentage; growing/unknown totals show active/queued counts and an indeterminate state. No mixed-unit average or artificial 99% ceiling. Whole-run completion requires closed discovery and settled required descendants, including retry waits and finalization. Unavailable/stale values remain unknown. Historical outcomes and current pending-review counts must remain distinct.
4. **Bounded reads.** Keep compact batch pages and server aggregates; add typed category result summaries and paged detail queries only where the UI needs them. Remove string-matching classification from the new presentation contract. Report unavailable capture explicitly. Keep heavy SQL in Storage-backed readers when touching these queries, per repository boundaries.
5. **Explicit run ownership.** Audit start/recovery/watcher producers. Resume, retry, and provider chunks retain their original logical run. A new explicit scan after completion is a new run; continuous watcher intake needs a persisted, bounded intake window and closure rule. Shared source paths are not sufficient ownership evidence. One logical run may have internal child batches, but Activity groups by its persisted parent run ID. Historical rows must not be guessed together by title, proximity, or matching folders.
6. **Job scope.** Distinguish actual ingestion runs from metadata refreshes and recorded administrative/review operations using durable kind/trigger/actor fields. Do not create generic admin batches merely to fill a mockup. Wire filters to the supported operation kinds and full-scope facets.

Retain existing useful foundations: durable tracked operation IDs, retry/lease records, batch/item API boundaries, compact aggregates, shared controls, and review navigation. No raw technical-log viewer returns. No pre-beta compatibility reader or guessed historical merge is part of this plan.

## Delivery and exit criteria

| Step | Scope | Exit condition |
| --- | --- | --- |
| 1 | Fix navigation/state contract and define counting units. | Implemented: View all is unscoped, exact run links and Back work, and media membership is distinct from file counts. |
| 2 | Correct run projection, identity gaps, typed categories, and bounded queries. | Implemented for the operation and item reads used by these pages; resume keeps the durable run ID already carried by tracked work. |
| 3 | Replace Activity layout and global lenses with local icon-triggered detail. | Implemented: one concise list, one expanded panel, bounded item paging, and no duplicate navigation layer. |
| 4 | Rebuild Ingestion current card, connected stage strip, and stage panels. | Implemented: one four-stage strip at compact/desktop widths, selected-stage panel, and no duplicate Work in progress block. |
| 5 | Browser, accessibility, performance, and documentation checks. | Compact rendered interaction checks and automated solution gates completed; the resolution matrix remains the ongoing regression checklist. |

## Verification matrix

- Capture actual rendered before/after states for Ingestion, Activity collapsed, Activity summary, each category, paged items, and Needs Review navigation alignment at **1920×1080, 1536×864, 1440×900, 1366×768, and 1280×720**. Test the Settings rail in its normal state and verify usable content width.
- Check **1024×768 and 768×1024** for intentional compact layouts; **390×844 and 360×800** for monitoring summaries. Inspect 200% zoom, long titles/paths, large counts, keyboard-only navigation, focus restoration, reduced motion, and no horizontal body overflow.
- Desktop assertions: exactly one stage row and three visible connectors; labels not wrapped; panel below and within the stage/list bounds; aligned row values and filter baselines; no arbitrary centered enrichment tiles. Visually inspect screenshots, not just computed CSS.
- Lifecycle scenarios: file checks complete while people/artwork remain, provider wait, overlapping stages, two independent runs, restart mid-run, completion, partial technical failure, all-review outcome, empty library, stale/disconnected status. No all-green stages while required work remains.
- Navigation scenarios: View all activity from active and completed states; explicit run older than seven days; invalid run; source/filter change; browser Back; item/category switch during a slow request. Old responses must not replace the current selection.
- Count scenarios: several files for one media item; all six media types; retries; unclassified/review item; deduplicated people; attempted versus completed artwork; assets without recorded byte totals. Media count equals the paged media membership, not the file total.
- Scale fixture: a run with 3,000+ internal events and at least 100 media items. Initial list loads no event rows or item evidence. Selecting Media fetches one bounded page; item 26 and the last page are reachable. Measure request size, rendering cost, and responsiveness; establish and record a baseline before accepting performance.
- Follow repository gates during implementation: restore, solution build, solution tests, and documentation checks. Add behavior tests for navigation/paging/units/recovery, not merely markup string assertions.
- A compact 728px capture does **not** satisfy desktop validation. If desktop browser sizing is unavailable, record the specific missing evidence and leave that acceptance item open rather than claiming the whole UI is validated.

## Product owner summary

Activity becomes a short list of real operations. Users click a clearly labeled icon in one row to see what happened to its media, enrichment, downloads, or exceptions, directly below that row. Ingestion becomes a connected four-stage summary with useful information beneath the selected stage. Navigation, counts, and progress use the same run so the cleaner interface also tells a consistent story.
