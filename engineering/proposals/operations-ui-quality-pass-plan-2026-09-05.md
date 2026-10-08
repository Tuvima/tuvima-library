# Operations UI quality pass plan

**Status:** Implemented for currently available operational instrumentation  
**Visual targets:** the September 5, 2026 Ingestion and Activity & Audit reference screens supplied by the product owner

## Outcome

Make Operations feel like one product area with two complementary views:

- **Ingestion** answers “What is happening now?” with truthful progress, a connected pipeline, compact current-stage detail, and a lightweight recent-run history.
- **Activity & Audit** answers “What happened?” with one row per durable logical operation and progressive, server-paged inspection of media, enrichment, data, and technical events.

The screenshots define hierarchy, density, alignment, and interaction patterns. They are not a source of sample data or a requirement to add controls with no real action behind them.

## Current-state findings

### Useful foundations to retain

- `IngestionTasksTab` already reads the shared `GET /ingestion/operations` snapshot and follows SignalR updates.
- Ingestion already avoids presenting file discovery completion as whole-run completion.
- Four pipeline phases and phase-specific work are already represented in the contract.
- Activity already groups its top-level query by `BatchId`, supports exact-run lookup, and lazily loads expanded content.
- Batch items are already server-paged at 25 rows and item detail is fetched only when a row expands.
- Files and classified media items are separate counts.
- Resumed tracked file operations retain their persisted batch ID.

### Gaps to address

- The Ingestion toolbar becomes absolutely positioned at large widths, which can disconnect it from the page header and create overlap risk.
- The pipeline connector is built from four independent pseudo-elements, so the visual line can look broken as stage geometry changes.
- The stage detail panel points toward a percentage position rather than sharing one layout system with the selected stage.
- Ingestion has no content-width policy, and several sections stretch into low-density empty space on very wide displays.
- Recent runs omit a stable result column and use a two-column layout at wide sizes, weakening scan order.
- Activity uses four competing per-row inspection buttons and a separate overview panel. The target uses the batch row as the summary and one stable tab strip inside the expanded batch.
- Activity batch rows use a brittle eight-column grid with multiple breakpoint-specific column definitions.
- The item endpoint currently exposes media type and sort only; search, result, and source-folder filtering are not wired through the service contract.
- Enrichment insights do not distinguish every required total, such as unique people versus associations or asset count versus recorded bytes.
- There is no dedicated server-paged technical-event endpoint. Raw timelines must not be loaded as one large collection.
- The existing compact pager has no page-size selection, which limits 25/50-row audit workflows.

## Product and status semantics

1. One user-initiated or scheduled scan receives one durable logical batch ID. All source folders discovered by that scan are dimensions inside that batch.
2. A restart resumes persisted work under its original batch ID. New unrelated watcher activity receives a new batch ID; the UI must not merge runs by time, title, or folder name.
3. A batch is **Completed** when required pipeline work finishes. Metadata uncertainty or a review-queue entry is an amber attention signal on that completed batch, not a failed result.
4. **Failed** is reserved for unrecoverable required work. **Interrupted** means the operation stopped before completion and has not resumed.
5. Color and icon carry the fast visual cue. In pipeline stages, avoid redundant visible status words. In audit result columns, pair the icon with one short word for accessibility and diagnostic scanning; use no pill-shaped status badges.
6. Show an overall percentage only when the Engine can provide a defensible numerator and denominator for the entire logical run. Otherwise use an indeterminate bar with the active phase and known counts.
7. Never invent byte totals, provider counts, media classifications, or completion percentages. Use a compact “Not collected” state when instrumentation is unavailable.

## Implementation sequence

### 1. Establish shared Operations layout primitives

- Add a bounded Operations content frame using the existing settings shell, with a target maximum width around 1,600px and responsive page gutters.
- Introduce shared Operations spacing, radius, row-height, icon-container, and semantic-state variables based on existing `--tl-*` tokens.
- Build or extend small shared components only where both pages need them:
  - `OperationsActionGroup`
  - `OperationsStatusIcon`
  - `OperationsMetricCard`
  - `OperationsEmptyState`
  - `OperationsPager`
  - `IngestionPipelineStepper`
- Keep `AppSelect`, `AppButton`, `AppIconButton`, and other existing controls as the control layer.
- Remove page-specific absolute positioning for action groups. Use a header grid with a flexible title column and a dedicated wrapping action column.

### 2. Complete the operation contracts before the detailed UI

- Extend the batch-item query with `search`, `result`, `sourceFolder`, `mediaType`, `sort`, `sortDirection`, `offset`, and `limit`.
- Return available filter facets for a batch, especially source folders and media types, without loading all items.
- Expand insights with explicitly sourced fields:
  - media added and updated
  - unique people and media-person associations
  - artwork count and recorded bytes
  - relationships created and updated
  - identity/canonical updates
  - metadata updates
  - subtitles and recorded bytes
  - lyrics
  - provider operations, retries, and terminal failures
- Add a paged technical-event query scoped to one batch. It must support server-side search and filtering by event type, provider, result, media type, time, source folder, item/path, and correlation ID.
- Add page-size support for 25 and 50 rows, with an enforced server maximum.
- Keep exact-run lookup independent of the current date filter.

### 3. Lock durable batch behavior

- Trace every scan entry point—manual scan, scheduled scan, startup scan, watcher flush, and restart recovery—to the point where the batch ID is created or reused.
- Ensure one scan coordinator creates one batch ID before enumerating folders and passes it through every file, identity, enrichment, organization, operation, and activity record.
- Preserve a resumed operation’s existing batch ID after server restart.
- Add repository/service tests proving:
  - a multi-folder scan produces one Activity row;
  - mixed media remain one batch;
  - restart recovery continues the same batch;
  - a later independent scan creates a new batch;
  - Activity grouping never relies on labels or timestamps.

### 4. Recompose Ingestion to match the target hierarchy

- Move page actions into the title row: **Processing settings**, **View activity**, and one primary **Scan all folders** action.
- Keep automatic refresh. Do not add a manual Refresh button unless it performs a real recovery action when live updates are paused.
- Rebuild the hero as a balanced two-area grid:
  - left: semantic status icon, title, plain-language current work, and truthful progress;
  - right: elapsed, started, active, queued, and retry counts in a fixed metric grid.
- For idle/complete state, show “Library is up to date,” relative completion time, and the last run’s identified/review/failed counts without an empty progress track.
- Replace the segmented connectors with one pipeline track behind four equal stage nodes. Stage buttons sit on the same grid as the connector.
- Render the selected-stage detail immediately below that same grid so its notch derives from the selected grid column. Use a purple selected surface, green completed nodes, muted future nodes, and reduced-motion-safe activity animation.
- Keep the selected-stage panel compact:
  - show only relevant work groups;
  - align active, queued, completed, and progress values;
  - replace empty work with a concise completion message.
- Normalize the four outcome cards. Make **Need review** link to `/settings/review` and **Failed** link to the current run’s Activity attention view.
- Render **Media in this run** as a compact six-type list when data exists and a designed centered empty state when it does not.
- Keep **Up next** compact and truthful, with separate queued-work and scheduled-refresh rows.
- Change **Recent runs** to one full-width ordered list with operation name, counts, icon-led result, relative time, and one chevron. `View all activity` remains unscoped.

### 5. Recompose Activity & Audit around one expandable operation row

- Reduce the page-level controls to a date-range selector and Refresh. Keep item/media/provider search inside the expanded batch where its scope is clear.
- Use a stable table grid for collapsed operation rows: operation, date/time, duration, files, media items, result/attention, and disclosure action.
- Make the full row a semantic disclosure target while preserving explicit focus and keyboard behavior.
- Remove the four per-row Media/Enrichment/Data/Attention buttons and the redundant summary panel.
- When an ingestion batch opens, default to **Media** and show one in-flow tab strip:
  - Media
  - Enrichment
  - Data & Downloads
  - Technical Log
- Do not add inactive selection checkboxes or bulk controls merely because they appear in a reference image.

### 6. Build the Media batch view

- Show only nonzero batch metrics in a compact row: media added/updated, people, artwork, relationships, subtitles, and lyrics.
- Use a narrow media-type rail on large screens and a horizontally scrolling compact selector at tablet widths.
- Add a compact item filter row for search, result, source folder, and sort. Media type is selected in the rail/selector and is not duplicated in the filter row.
- Render aligned item columns for title, type, source, result, enrichment, and activity count. Use media-type icons instead of artwork thumbnails.
- Keep item result distinct from enrichment result. Use icon plus concise text, without status pills.
- Expand one item inline and fetch its detail on demand. Show only relevant categories: identification, metadata, artwork, people/contributors, relationships, subtitles, lyrics, files, and a technical-event count.

### 7. Build Enrichment, Data, and Technical Log views

- **Enrichment:** render left-aligned category summaries. Each category can open a server-paged drill-down. People must distinguish unique people from media-person associations and allow associated media to expand.
- **Artwork:** summarize asset type, count, recorded bytes, and failures. Keep it an audit table, not a gallery.
- **Subtitles/Lyrics:** reuse the same compact drill-down structure and expose only recorded language/provider/item/result fields.
- **Data & Downloads:** show only collected operational metrics and provider rollups. Use “Not collected” for absent byte instrumentation.
- **Technical Log:** load nothing until the tab is selected. Query a paged endpoint, default to 25 rows, and keep raw payload/stack details behind a per-event disclosure. Never render thousands of entries at once.

### 8. Responsive behavior

- **Above 1,600px content width:** keep the frame centered; spend extra width on readable columns rather than oversized cards.
- **1,280–1,599px:** preserve the full operation table and four-stage pipeline; reduce column gaps and secondary copy before shrinking actions.
- **Tablet:** wrap the page action group as one unit, keep the four pipeline nodes on one line, turn the Activity media rail into a horizontal selector, and allow item tables to simplify secondary columns.
- **Mobile/narrow:** provide the compact operational summary already expected by the product direction. Ingestion stages become an accessible disclosure list; Activity uses operation cards with files, media items, result, and attention. Deep batch inspection may show a larger-screen message rather than compressing audit tables into unusable layouts.
- Use container queries for page-owned arrangements and media queries only for application-shell behavior.

### 9. Accessibility and interaction verification

- Give every icon-only control an accessible name and tooltip.
- Ensure row and stage disclosures use buttons with `aria-expanded` and `aria-controls`.
- Preserve a visible focus ring on dark and selected surfaces.
- Announce lazy loading and errors without moving the user’s focus unexpectedly.
- Verify semantic status text is available to assistive technology even when the visual stage uses only an icon.
- Disable spinner/pulse motion under `prefers-reduced-motion`.

## Validation matrix

### Automated

- Contract approval tests for all new response/query fields.
- API tests for batch grouping, status semantics, filter combinations, deterministic sorting, paging boundaries, and exact-run lookup.
- Web component guardrails for one scan action, no false 100% state, no redundant stage labels, default Media expansion, lazy panel loading, and no unbounded technical log.
- Interaction tests for stage selection, batch expansion, tab switching, filter reset, item expansion, and 25/50-row paging.

### Visual and manual

Validate with real and purpose-built deterministic fixtures at:

- 1280×720
- 1440×900
- 1920×1080
- 2560×1440
- 3840×2160
- one tablet width
- one narrow/mobile width

Cover Ingestion idle, active determinate, active indeterminate, warning/review, failure, no-media, and large mixed-media states. Cover Activity collapsed, expanded Media, expanded Enrichment, Data unavailable/available, paged Technical Log, 18/100/1,000+ item batches, review attention, warnings, and terminal failure.

For every viewport, record screenshots and assert no horizontal document overflow, action overlap, clipped controls, broken pipeline connectors, changing batch-row heights, excessive empty width, duplicate status labels, or content shift when a panel loads.

## Main files expected to change

- `src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor`
- `src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor.css`
- `src/MediaEngine.Web/Components/Activity/ActivityBatchExplorer.razor`
- `src/MediaEngine.Web/Components/Activity/ActivityBatchExplorer.razor.css`
- `src/MediaEngine.Web/Components/Activity/ActivityBatchInspector.razor`
- `src/MediaEngine.Web/Components/Activity/ActivityBatchInspector.razor.css`
- `src/MediaEngine.Web/Components/Activity/ActivityBatchItemInspector.razor`
- `src/MediaEngine.Web/Components/Pages/Settings.razor.css`
- shared Operations components under `src/MediaEngine.Web/Components/Shared/Operations/`
- `src/MediaEngine.Contracts/Activity/ActivityBatchDtos.cs`
- `src/MediaEngine.Application/ReadModels/ActivityReadModels.cs`
- `src/MediaEngine.Application/Services/IActivityReadService.cs`
- `src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs`
- `src/MediaEngine.Api/Services/ReadServices/ActivityBatchReadService.cs`
- ingestion orchestration/repository files only if the batch-identity audit exposes a real gap
- corresponding API, contract, Web, and ingestion tests

## Definition of done

- The two pages match the supplied targets in hierarchy, density, spacing, and interaction behavior while showing only real data.
- Ingestion communicates current work accurately at a glance and never equates file completion with run completion.
- Activity presents exactly one row per durable logical operation and opens directly to useful media detail.
- Review/warning exceptions remain distinct from terminal failures.
- Large histories and technical logs stay responsive through server-side paging and filtering.
- Every target viewport passes visual, overflow, keyboard, focus, and reduced-motion checks.

## Implementation record

Completed the structural and visual quality pass across the Engine contracts, Activity API, Dashboard orchestration layer, and both Operations screens.

- Ingestion now uses a bounded responsive layout, a single automatic-refresh toolbar, truthful whole-run progress, a connected four-stage pipeline, selected-stage detail anchored to its stage, icon-led outcomes, a mixed-media summary, compact queued work, and unscoped recent-run navigation.
- Activity now presents one expandable row per durable `BatchId`, defaults expanded batches to Media, and exposes Enrichment, Data & Downloads, and a lazy Technical Log through one in-flow tab strip. Batch rows include both file and media-item counts.
- Batch item search, outcome, source, media-type, sorting, and pagination run on the server. Technical events also use a dedicated server-paged endpoint, so large logs no longer render thousands of records at once.
- Enrichment summary categories are selectable and reveal retained batch-scoped records. People uses the existing batch-aware people audit query; the other categories use filtered technical events.
- Completion, attention, review, and terminal failure use separate icon and color semantics. Review exceptions do not turn a successful batch into a failed batch.
- The batch-identity audit confirmed multi-folder scans use one ID and recovered persisted work retains its original ID. Activity groups by that durable ID. Historical rows written with unrelated IDs are not heuristically merged.

Validated the layouts at 1280×720, 1440×900, 1920×1080, 2560×1440, 3840×2160, tablet, and narrow mobile widths. The largest retained development batch exposed 1,021 technical events and loaded only the current 25-row page.

Current instrumentation limits remain visible instead of being estimated: byte totals display **Not collected** when the Engine did not record them, and older batches can have empty category drill-downs when their detailed activity was not retained.
