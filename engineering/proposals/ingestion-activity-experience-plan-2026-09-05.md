---
title: "Ingestion and Activity Experience Plan"
summary: "A shared lifecycle and reporting model for live ingestion summaries and detailed activity history."
audience: "developer"
category: "proposals"
product_area: "ingestion"
tags:
  - "ingestion"
  - "activity"
  - "settings"
---

# Ingestion and Activity Experience Plan

Status: partially implemented; remediation required, September 5, 2026. The [Operations Simplicity Remediation Plan](./operations-simplicity-remediation-plan-2026-09-05.md) supersedes the page-level detail lenses and presentation decisions below. Remaining lifecycle and verification requirements must be demonstrated before this plan can be considered complete.

## Product outcome

Ingestion answers **“What is happening now, is it healthy, and do I need to act?”** Needs Review answers **“Which items require a human decision?”** Activity & Audit answers **“What happened, to which items, why, and who initiated it?”** All three describe the same run, outcomes, and timestamps.

Use the supplied images for their visual hierarchy: a prominent current-work card, compact metrics, restrained colored icons, and grouped history with progressive drilldown. Their sample numbers, schedules, controls, and completion indicators are illustrative. The permanent Important changes sidebar is intentionally omitted so the selected batch remains the investigation focus.

## Findings that drove the implementation

This review traced source and existing tests. It was not a replay of the pictured batch and did not establish which particular background job was executing when that screenshot was taken.

### What works well

- The operational endpoint already exposes active jobs, current activities, stage counts, provider health and throttling, source information, recent batches, and generated time. Preserve those capabilities rather than inventing a separate reporting pipeline.
- `media_operations` already supports durable statuses, leases, heartbeats, retries, timestamps, batch references, and idempotency keys. These are useful foundations for reliable lifecycle tracking.
- The Dashboard already combines SignalR with polling and coalesces snapshot refreshes. Keep live responsiveness and recovery after missed events.
- Activity already supports paged batches, media groups, item drilldowns, people audits, and run-correlated events. Preserve that depth behind a clearer summary.
- Both pages already live within the shared Settings shell and administrator navigation. They need alignment within that shell, not a new administration workspace.
- Scheduled enrichment has a real durable calendar and due-work promotion worker. Reuse it for truthful schedule summaries.

### Why 100% appears while work continues

There are several different meanings of progress and completion today:

1. **The visible percentage is file progress.** `BuildLibraryUpdateStatus` determines running state from active jobs and current activities. Separately, `ResolveLibraryUpdateProgress` calculates `processedFiles / totalFiles` and takes the maximum of that and active-job percentages. Once the files reach their count, the headline stays at 100% regardless of remaining enrichment. Taking the maximum also allows a finished stage to dominate an unfinished one.
2. **The copy makes file progress sound like whole-run completion.** `ResolveMainLine` says “N of N files finished” even in the running state. `IngestionTasksTab` displays that with the percentage under Current activity, but does not surface the separate enrichment progress and queue context available in the view model.
3. **Existing tests encoded the contradiction.** They permitted Running, every file checked, and 100% while downstream work remained. The implementation replaced that contract with stage-aware progress tests.
4. **Multiple producers define completion differently.** Intake's `PublishQueuedBatchSnapshotAsync` can publish a complete batch event when file counters settle. `BatchProgressService` instead derives completion from terminal identity jobs, including Ready/ReadyWithoutUniverse, review, no-match, and failure. Its completion check does not independently account for every downstream operation.
5. **The status read itself changes state.** `GetSnapshotAsync` calls `ReconcileCompletedBatchesAsync`, which updates counters and completes or abandons batches. Its reconciliation query counts selected identity states and running operation rows; it is not a comprehensive lifecycle rule for every pending, leased, retrying, or shared child task. Reading a page should not be responsible for making a run finish.

Quick hydration does explicitly enqueue the universe stage, and the universe service waits for its own enhancer pass before completing an inline identity job. The problem is therefore not simply “enrichment is entirely untracked.” The missing piece is one authoritative completion rule spanning intake, identity, all required child work, and explicitly separate maintenance.

### Other gaps

| Area | Current behavior | User impact / required change |
| --- | --- | --- |
| Navbar | `SystemActivityIndicator.HandleClick` sends every authorized non-playback click to `/settings/activity`. Shell progress has its own event-derived calculations. | Ingestion and its enrichment should open the live summary and use its run state. |
| Results scope | `LatestBatch` selects the newest batch without requiring it to be completed. The summary also consumes library-wide review counts. | Label Current run versus Last run explicitly; never mix historical/global counts into current-run cards. |
| Added/updated | The page displays `RegisteredCount`; elsewhere the view model adds review counts into added/updated. | Define actual add/update outcomes. Registration or a review decision is not evidence of an update. |
| Recent activity | Ingestion shows five raw event descriptions from a filtered global recent list. | Person enrichment can dominate the summary and hide batch milestones. Aggregate here; retain individual events in Activity. |
| History hierarchy | Batch explorer leads with short IDs and a nested table. | Lead with named operations, outcomes, duration, warnings, and failures. Keep IDs in expanded technical details. |
| Event search | The prior ledger fetched 100 recent/type events, then applied dates locally. | Date filters cannot represent complete history reliably at scale. Add server-side filtering and pagination for events within an operation. |
| Audit semantics | Activity entries have action, actor, entity, changes JSON, and optional ingestion run ID; no explicit severity/source/category fields. | Add structured semantics before implementing the reference image's filters and summary counts. |
| Read cost | The overview loads operations, reviews, capability summaries, and activity separately; the status service performs extensive SQL and heuristic projections. | Use a compact, consistent summary read; fetch detailed queues, rows, and schedules on demand. |
| Schedules | Current UI is an entity-level enrichment refresh table; worker promotes up to 50 due entries hourly. | Do not depict unsupported daily/weekly scan jobs or generic enable switches. Show real due work and actual policies. |

## Target user experience

### Ingestion: a live summary

Keep the existing `/settings/ingestion` route under one **Operations** Settings destination with local **Ingestion**, **Needs Review**, and **Activity & Audit** navigation. Place **View activity** and the single **Scan all folders** action consistently with other Settings page actions. Status refreshes automatically through SignalR and snapshot polling, so do not expose a redundant manual Refresh action. Use a specific **Processing settings** link to the existing metadata ingestion-flow configuration; source-folder actions still lead to Libraries/Import Folders.

The desktop layout should follow this order:

| Region | Contents |
| --- | --- |
| Full-width Current activity card | Clear state, named run/source scope, current phase summary, stage strip, elapsed time, last meaningful update, active/queued/waiting counts. |
| Four compact outcome cards | Files checked; Added or updated; Need review; Failed. All use the same clearly labeled run scope. Secondary text can split added and updated or identify unchanged/skipped files. |
| Main summary panel | Up to three active runs, with grouped work such as “Matching titles,” “Fetching artwork,” and “Updating people and relationships.” When idle, show the last run and a few recent run summaries. |
| Secondary panels | Media mix for the selected run, and Up next / Scheduled with actual queued groups and earliest due times. On narrower screens, stack below the summary. |
| Collapsed refresh details | Existing entity schedule inspection and supported Run now actions, loaded on demand. |

Use four user-facing stages: **Discover, Identify, Enrich, Organize**. Organize includes configured library placement/writeback and relationship/group finalization as applicable. Each stage has its own state; phases may overlap. Do not infer that earlier stages are complete merely because a later stage has started. Disabled/not-applicable stages use a neutral “Not required” state rather than a success check.

The main message should describe work at batch scale, without rapidly cycling filenames or person names. For example, with illustrative counts:

> **Updating library details** · Books and Movies  
> Updating people and artwork · 3 groups active · 27 tasks queued.  
> Your available titles can already be opened. **View this run's activity**

Only show the availability sentence when the library state supports it. If counts are unknown, say “Updating people and artwork” with an indeterminate indicator; omit unsupported numbers.

Provider waits deserve useful copy: “Waiting for the metadata provider; retry scheduled in 2 minutes.” A slow but heartbeating task is active, not failed. Show an attention callout only for actionable problems, with a scoped Review or Activity link. Review remains the existing queue and shared editor.

Use the reference's purple progress/accent, subdued blue panels, green terminal success, amber waits/attention, and red terminal failures through shared design tokens. Colors supplement text and icons. Avoid gradients or charts that imply a quantitative measure without one.

### Truthful progress rules

- The headline represents run state and the overall bar uses measured counts from the actual pipeline stages. It does not reuse file-discovery completion as whole-run completion.
- Supporting text names the active stage and reports its own bounded task count. The Files checked count remains in its outcome card rather than being repeated as the headline.
- The overall percentage stays below 100 while an active stage remains. A terminal run reaches 100 even when some items ended in review or another explicit terminal outcome.
- Keep active, queued, and retry counts visible beside the percentage so uncertainty or newly registered downstream work is clear.
- Person tasks, files, works, provider requests, and batches are different units. Preserve `count_unit`; do not sum unlike denominators.
- Initially omit overall ETA and trend arrows. A later phase ETA requires a stable denominator and a measured recent rate. Any displayed rate must name its unit and measurement period.
- Idle says “No active ingestion” with the last run result. Disconnected/stale says “Status unavailable” with last observed time, never “Complete.”

### Activity & Audit: history with drilldown

Keep `/settings/activity` as one grouped experience. Every durable run is a top-level operation. One page-level lens—Overview, Media, Enrichment, or Data & Downloads—controls the information shown for every expanded run. Do not repeat those choices inside each run. Maintenance and retention remain a collapsed utility.

- Run cards lead with meaningful names such as “Library scan — Books,” trigger/actor, start time, duration, media-item count, and a concise result sentence. Internal event count is not a primary user metric.
- Use one leading state icon instead of a status pill: animated activity for running, green check for completed, gray interruption for resumable work, amber warning for unrecovered operational warnings, and red only when the run itself failed. Preserve equivalent accessible text.
- Keep run state separate from item outcomes. No match, incomplete metadata, and low confidence go to Needs Review without turning a completed run into “Completed with issues.” Recovered retries do not leave a warning. Nonzero review, warning, and failed-item counts appear as plain icon-and-count facts.
- Expand a card into the currently selected page-level lens. Media-type counts open the Media lens already filtered to that type, and enrichment categories open a cross-media view of the same run.
- Use search plus date range, run state, media type, trigger/source, and attention filters at run level. Apply the selected lens and filters to the list and server-side paging rather than rendering another filter row inside every run.
- Remove Technical Log from Activity. Keep raw operation events for durable audit, retry, and diagnostics, but do not request or render them in the normal page. Supply enrichment and provider sections from compact server-side grouped aggregates. If no diagnostics client needs the raw batch-events endpoint, remove that public endpoint during the pre-beta cutover.
- Summary counts use the same filter/date scope as the list: runs, warning events, failure events, administrative changes, and review actions. Explain whether a number counts events or affected items. No percentage deltas until the comparison periods and retained history support them.
- Administrative changes remain part of the audit event model. They do not occupy a permanent sidebar that competes with batch investigation.
- Do not let live arrivals reshuffle an expanded historical row. Show a “New activity available” affordance. The Ingestion link remains visible while matching work is active.

### Mobile: resilient summary access

Operations is primarily an administrator workflow for desktop and tablet. Keep the existing routes responsive so navbar links and bookmarked URLs remain usable on a phone, but do not build a separate full-featured mobile operations console. Mobile should offer a compact, read-only summary from the same run state and metrics as desktop. Detailed investigation and review resolution remain larger-screen workflows.

#### Mobile Ingestion

Use a single-column layout in this order:

1. **Compact Settings header and current status.** Show the page title, running/waiting/idle/unavailable state, named run or source scope, and one plain-language sentence describing the current work. Keep last-updated information visible; show the provider wait reason when applicable.
2. **Small progress summary.** Show the current phase, explicitly labeled file count, and active/queued work with their units. Replace the wide desktop stage strip with a **Stages** disclosure containing four compact status rows. Multiple active phases remain visible in the summary sentence and expanded rows.
3. **Outcome grid.** Use a two-by-two grid for Files checked, Added or updated, Need review, and Failed, with a shared Current run or Last run label. On a phone, Needs Review shows the count and larger-screen guidance rather than opening the editing workflow. An actionable blocking issue appears directly below current status rather than being buried beneath metrics.
4. **View activity.** Provide a link to the compact Activity summary. If several runs are active, show their count and up to three compact run rows. Deeper batch inspection remains available on a larger screen.
5. **Up next and last result.** Show one concise next-work/schedule summary and the latest completed run when relevant. Media mix and additional run summaries are collapsed; omit the desktop chart from the initial phone view.

Keep the automatic status update visible through changing timestamps and live values. The phone summary is read-only and omits Scan now, Processing settings, and technical diagnostics. Idle emphasizes the last result. Unknown or stale status remains explicit instead of showing reassuring zero counts.

#### Mobile Activity & Audit

- Start with a compact title, selected date range, and a **View live ingestion** link while work is active.
- Show a short, paged list of operation cards with state, relative start time, duration, media-item count, and nonzero attention counts.
- Keep search available in the filter disclosure. Desktop shows one page-level lens; phone cards remain a concise summary without deep inspection.
- Do not render raw technical events, wide item tables, provider ledgers, or the full desktop batch inspector on a phone.
- Preserve deep links, but direct users to desktop or tablet when an action requires detailed investigation or review resolution.

#### Shared mobile behavior and delivery requirements

- Keep the Settings mobile page selector and adjacent Ingestion/Activity navigation consistent. Navbar ingestion clicks open the compact Ingestion summary directly; run links preserve `runId`. Do not create separate mobile routes or a separate mobile completion algorithm.
- Needs Review exposes its unresolved count in Operations navigation, while its editing route is reserved for desktop and tablet.
- Use touch targets at least 44 CSS pixels high, wrapping titles, readable numbers, and disclosure buttons with expanded state. Avoid hover-only information, wide tables, nested scrolling, and fixed bottom controls that overlap persistent playback or device safe areas.
- Fetch compact summaries and the first page only. Hidden diagnostics, schedules, filters' result pages, and event details do not preload desktop-sized datasets. Refresh the authoritative snapshot after browser resume/reconnect, retaining the last known state with a stale label until it arrives.
- Treat responsive correctness as part of the initial delivery. Additional mobile-only drilldown is outside scope unless real usage shows that administrators need it.

### Settings and navbar alignment

- Place one Operations destination in the Administration rail and show its unresolved review count when nonzero. Within Operations, keep Ingestion, Needs Review, and Activity & Audit adjacent in a local tab strip with the same count. Retain the existing shared Settings shell, one page title, consistent content width, padding, cards, and action placement.
- Use the Settings header as the page identity. Use shared controls for selects, badges, filters, category drilldown, and technical disclosures.
- For ingestion/identity/enrichment primary activity, the navbar opens `/settings/ingestion`, optionally focused on a real run ID. After ingestion files settle, its downstream enrichment still routes there.
- Preserve playback's queue-panel action. Other primary activities use their existing relevant destination or Activity. Avoid a blanket rewrite that makes playback or unrelated maintenance open Ingestion.
- Use existing administrator authorization on both navigation and endpoints. Shared links carry the run/filter state; Back restores the previously viewed scope.
- Use one canonical `runId` query parameter across these updated surfaces and no new route aliases or compatibility readers. Keep the existing supported route structure and implement view/filter state explicitly.

## Structural changes

### 1. One durable run lifecycle

Extend the existing ingestion run/batch model and operation infrastructure rather than introducing a competing queue engine. A user-triggered scan needs a stable run ID across folder batches; retain child batch IDs where they represent actual execution groups. Watcher-driven intake needs a bounded run whose discovery closes when that intake batch is sealed. Internal provider chunk sizes must not create new user-facing scans.

Persist trigger, source/library scope, initiating actor, discovery-closed time, lifecycle, outcome summary, last heartbeat/transition, and completed time. Suggested lifecycle states are Discovering, Running, Waiting, Completed, Failed, Interrupted, and Cancelled. Stage names describe the work; Completed with issues is a terminal outcome presentation.

Define completion as: **discovery is closed, every registered required operation is terminal, and no producer can still enqueue an unregistered required descendant**. Registration of children and parent settlement must be transactionally safe. In-process queue emptiness or lack of recent events is not sufficient.

Use the existing operation statuses to count pending/queued, leased/running, retry waiting, and terminal outcomes. Failed-retryable work remains outstanding until retried or explicitly exhausted. A review handoff or confirmed no-result can be terminal for automation without being successful enrichment. Cancellation/interruption must not become green completion.

### 2. Carry run ownership through child work

Audit intake, retail/bridge matching, quick hydration, universe passes, metadata harvesting, people/images/descriptions, artwork, collection finalization, writeback, and enabled AI/plugin tasks. Each task created because of this run must carry its run/parent correlation and declare whether it is required to finish this run. Missing correlation is an instrumentation defect; do not infer ownership from matching titles or overlapping timestamps.

Some people/artwork tasks are deduplicated across titles or runs. Preserve one physical operation with explicit dependent-run links, instead of overwriting its batch owner or counting the same execution twice in global totals. Each dependent run waits for the shared operation's result; cancellation of one run must not cancel work still needed by another.

Future scheduled refreshes and unrelated ongoing maintenance are separate runs with their own trigger. A genuinely deferred failure records its outcome and linked follow-up explicitly; it must not silently disappear to make the original run look successful. Later rematches/review resolutions create linked follow-up activity rather than rewriting the original historical result.

### 3. Centralize settlement and history

Put lifecycle transitions in one application service using repositories and existing recovery infrastructure. Remove state mutation from operational GET handlers and remove competing completion calculations from intake progress and provider progress emitters. Move SQL-heavy touched read logic into Storage-backed read services, following repository boundaries.

Persist terminal summaries and durable transition events atomically, or through a transactional outbox, so a crash cannot mark a run complete without recording its outcome. Recovery uses leases/heartbeats and explicit retry policy; a quiet provider batch must not be abandoned because its UI stopped receiving per-item events. Emit one terminal transition per run even after duplicate delivery/restart.

Keep historical run totals stable. Present subsequent review decisions, new provider work, and changed library state as later events/follow-up runs. Normal retention must preserve run summaries and acknowledge when detailed events have expired; summary totals must not silently masquerade as retained-row counts.

### 4. Shared contracts and efficient reads

Define the wire types once in `MediaEngine.Contracts`, with explicit boundary mapping:

- Compact current summary: snapshot revision/generated time, active runs, per-stage status and count unit, active/queued/waiting counts, run outcomes, last transition/heartbeat, provider wait reason/retry time, media mix, and schedule summary.
- Run detail and paged events: run/operation/event IDs, parent correlation, trigger/actor, category, source/provider, severity, outcome, entity references, timestamps, and safe change details.
- History query/response: filters, deterministic pagination, exact matching totals, and summary aggregates for the same scope. Distinguish failed attempts from terminal failures and warnings from review counts.

Use the same authoritative projection for Ingestion, Activity run cards, and ingestion-related navbar state. SignalR carries a run revision/change notification or compact snapshot; HTTP recovers state after reconnect. Ignore older revisions and distinguish connection health from work health. Keep the frozen universe SignalR pair unchanged; use a new canonical contract if additional information is needed.

Query all active runs directly rather than deriving them from the latest 12 history rows. Read the overview in a consistent database snapshot, and add indexes informed by run/status/time/provider query patterns. No detailed reviews, event pages, full entity schedules, or operation rows should be required to draw the default overview. Keep coalesced live updates and adaptive fallback polling, while avoiding sustained-event starvation of authoritative refreshes.

## Delivery sequence

| Phase | Work | Exit condition |
| --- | --- | --- |
| 1. Correct the progress contract | Add failing behavior tests for active downstream work; separate file progress from run state/copy; show queue/phase context; route ingestion navbar clicks to Ingestion. | No headline implies completion while known work remains. Playback navigation is unchanged. This is an interim presentation correction, not the full completion fix. |
| 2. Make lifecycle authoritative | Inventory producers, persist run scope/child dependencies, unify terminal rules and recovery, remove GET-time reconciliation, make terminal history durable. | Run status is correct even if no Dashboard is open, including retry, restart, and shared-work cases. |
| 3. Build shared read contracts | Compact summary, run history/event queries, structured audit semantics, aggregates, schedule rollups, snapshot revisions and correlation. | API, SignalR, Activity, and navbar agree on the same run; full-history filters and counts are accurate. |
| 4. Rebuild Ingestion presentation | Shared Settings header/actions, current-work card, explicit stage strip, outcome cards, batch summaries, media mix, real Up next, and a compact mobile monitoring summary. | On desktop an administrator can inspect the run; on phone they can identify the current state and any attention count without a broken layout. |
| 5. Rebuild Activity and join navigation | Operation cards, page-level lenses, aggregate drilldown, filters, deep links, adjacent Settings placement, and compact mobile operation cards. | Desktop supports investigation while phone provides a concise history summary without loading diagnostic detail. |
| 6. Validate and document | Integrated fresh-ingest checks, desktop/mobile visual checks, lifecycle/query/performance tests, and product/architecture documentation. | All acceptance cases below pass; no placeholder metrics or unsupported controls ship. |

Deliver phases in dependency order. Trend comparisons, broad schedule editors, pause/cancel controls, and overall ETA are outside the initial scope unless the underlying services already support their exact semantics. Scan now must report server acceptance/failure and prevent accidental duplicate commands through server-side idempotency, not only a disabled button. Automatic snapshot refresh must recover cleanly after reconnect and browser resume.

## Acceptance and verification

1. **The reported case:** all 111 file checks settle while people/artwork/relationships remain queued or active. Ingestion says details are still updating, the appropriate stages remain active, Activity shows the same running run, and the navbar opens Ingestion.
2. **All real work settles:** completion happens without a page request, records its timestamp once, persists the outcome, and removes the ingestion busy indicator after the shared snapshot updates.
3. **Discovery and overlap:** unknown/growing totals, overlapping stages, multiple active runs, and an active run older than the latest 12 batches remain truthful. No arbitrary cross-stage percentage is displayed.
4. **Provider waiting:** throttling and retry backoff show Waiting with a reason; a healthy long-running lease stays active. Exhausted retries yield the correct terminal outcome and an inspectable event.
5. **Outcome accounting:** unchanged/duplicate/skipped files do not inflate added/updated. Current-run reviews exclude older pending reviews. All-review/no-match, zero-file, partial-failure, and wholly failed runs have clear outcomes.
6. **Crash safety:** restart between child registration and parent settlement cannot lose work or complete early. Repeated notifications do not duplicate counts or completion events. Shared work across two runs settles both correctly.
7. **Standalone background work:** scheduled enrichment is visible as its own run and does not hold an unrelated completed scan open. Follow-up review/rematch actions do not rewrite history.
8. **Live recovery:** missed/out-of-order SignalR events, reconnect, stale HTTP responses, and provider silence do not fabricate completion. Both pages recover from the canonical snapshot.
9. **History at batch scale:** search/date/severity/provider filters return complete matching results beyond 100 events; paging is stable; summary counts match the scope; expired details are disclosed. Large runs do not render thousands of rows on initial load.
10. **Navigation/authorization:** navbar, review counts, failure badges, run links, browser Back, and role restrictions work. Playback still opens playback controls.
11. **Visual/accessibility:** validate empty, active, waiting, attention, completed-with-issues, and disconnected states at 1920×1080, a lower-height desktop viewport, tablet, and mobile. Check keyboard expansion/focus, text contrast, reduced motion, non-color status cues, and restrained live announcements. Capture before/after desktop states. Verify Settings uses one content scroller and no clipped toolbar or duplicate heading.
12. **Mobile summary usefulness:** at 360×800 and 390×844 CSS pixels, Ingestion identifies current state, work phase, and attention count. Activity identifies the date scope and recent run outcomes. Check long titles, large counts, increased text size, phone landscape, and the larger-screen guidance for detailed review work.
13. **Mobile recovery:** verify navbar routing, Back behavior, background/resume, and reconnect. Network checks confirm mobile summaries do not fetch item detail or raw event datasets; mobile and desktop show identical outcomes for the same run revision.

Revise the existing progress tests that currently require Running + 100% headline, rather than preserving that expectation. Add behavior tests for lifecycle and snapshot consistency; retain the relevant boundary and wire-contract ratchets. During implementation run the repository-required restore, build, and full test commands, plus documentation checks. Fresh ingestion verification must use the repository's approved disposable targets and preserve source originals. This planning change itself does not require starting ingestion or resetting any data.

## Source map for implementation

Paths below are repository-relative code references.

| Concern | Principal files |
| --- | --- |
| Current page and visual wrapper | `src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor`, its CSS, and `IngestionLiveDashboard.razor` |
| Dashboard projections and refresh | `src/MediaEngine.Web/Services/Integration/IngestionLiveDashboardState.Projection.cs`, `IngestionLiveDashboardState.cs` |
| Intake/provider progress | `src/MediaEngine.Ingestion/IngestionEngine.Operations.cs`, `src/MediaEngine.Providers/Services/BatchProgressService.cs` |
| Enrichment lifecycle | `src/MediaEngine.Providers/Workers/QuickHydrationWorker.cs`, `src/MediaEngine.Api/Services/UniverseEnrichmentService.cs`, provider workers and `MetadataHarvestingService.cs` |
| Operational read/reconciliation | `src/MediaEngine.Api/Services/IngestionOperationsStatusService.cs`, `src/MediaEngine.Storage/IngestionBatchRepository.cs` |
| Run and operation persistence | `src/MediaEngine.Domain/Entities/IngestionBatch.cs`, `MediaOperation.cs`, `SystemActivityEntry.cs`, corresponding Storage repositories/schema |
| Activity views | `src/MediaEngine.Web/Components/Settings/ActivityTab.razor`, `src/MediaEngine.Web/Components/Activity/ActivityBatchExplorer.razor`, `ActivityEventsLedger.razor`, existing detail/people components |
| History contracts/read services | `src/MediaEngine.Contracts/Activity/`, `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs`, `src/MediaEngine.Api/Services/ReadServices/ActivityBatchReadService.cs` |
| Navbar and Settings | `src/MediaEngine.Web/Components/Navigation/SystemActivityIndicator.razor`, `Services/Integration/ShellActivityState.cs`, `Models/ViewDTOs/SettingsNav.cs`, `Components/Pages/Settings.razor` |
| Real schedules | `src/MediaEngine.Api/Services/EnrichmentRefreshScheduleService.cs`, `EnrichmentRefreshScheduleWorker.cs`, `src/MediaEngine.Web/Components/Settings/EnrichmentRefreshSchedulePanel.razor` |
| Existing regression coverage | `tests/MediaEngine.Web.Tests/IngestionOperationsPageGuardrailTests.cs`, `NavbarActivityStateTests.cs`, `ActivityTabGuardrailTests.cs`, API ingestion/activity tests, Contracts ingestion tests |

Update README, product/architecture docs, AGENTS/CLAUDE and applicable `.agent` guidance when implementation changes the product contract. Follow the pre-beta cutover rule: no compatibility schemas, legacy readers, or migration shims. Any disposable state rebuild is an implementation step under that rule, not a prerequisite for this plan.

## Product owner summary

Users will see whether Tuvima is finding files, identifying them, adding details, organizing the library, or waiting for a provider. Finishing file checks will no longer imply that the whole update is done. Ingestion will provide a calm, useful summary; Activity will explain each run and its exceptions in detail. On a phone, both pages will prioritize compact summaries, clear attention cues, and tap-to-open details. Shared tracking underneath both pages will keep their status, counts, and navigation consistent across screen sizes.
