---
title: "Settings and Ingestion UX Remediation Plan"
summary: "Add consistent Settings breadcrumbs, truthful empty-batch behavior, stronger ingestion hierarchy, and unmistakable live activity."
audience: "developer"
category: "proposals"
product_area: "settings-ingestion"
tags:
  - "settings"
  - "ingestion"
  - "navigation"
  - "accessibility"
---

# Settings and ingestion UX remediation plan

Status: implemented September 7, 2026.

This plan is a focused follow-up to the consolidated Ingestion history work. It keeps `/settings/ingestion` as the single live and historical Operations destination. It does not restore Activity & Audit or add a compatibility route.

## Product outcome

An administrator can always tell where they are in Settings, return from a selected ingestion batch in one click, understand what a completed scan actually did, and see when ingestion is actively working. The ingestion header and live summary use the visual hierarchy in the supplied reference while retaining Tuvima's existing tokens and shared components.

The attached screenshots are visual references. Their sample values and labels are not data requirements.

## Findings from the current implementation

- `Settings.razor` renders one shared `SidebarPageHeader`, but it does not render a route-aware breadcrumb. A few nested components add their own breadcrumbs, which creates inconsistent coverage and styling.
- A selected batch is still the Settings Ingestion route with `runId` and `view=all`. `IngestionMediaPagedView` supplies a Back button inside its local header, but the shared page header gives no location context.
- `IngestionBatchHistory` always makes a batch row open the media browser, including when `AddedGroupCount` is zero.
- The row currently labels `FilesDiscoveredCount` as “files processed.” The API populates that field from `ingestion_batches.files_total`; it is not the processed count.
- `AddedGroupCount` is populated from the same presentation query family as the batch media page, but the contract is not explicit about empty-batch navigation. A count/detail disagreement still needs an invariant test.
- The active summary already rotates a Sync icon while `Model.IsRunning`, and it already disables that motion for reduced-motion users. The effect is isolated to a small icon, so the overall card can still look static.
- Settings currently overrides the shared header to a 30–32 px title and 16 px description. The page header, Operations tabs, toolbar, and summary card use independent spacing and type rules, which weakens the hierarchy.

## Delivery plan

### 1. Add one route-aware Settings breadcrumb

Create a small typed breadcrumb model in the Settings navigation layer and render `AppBreadcrumbs` once in `Settings.razor`, immediately before `SidebarPageHeader`.

- Generate section and subsection labels from `SettingsNav`; do not duplicate route labels in page components.
- Use the hierarchy `Settings / <group> / <page> / <subsection or detail>` where each ancestor is a real canonical route.
- For Operations, use `Settings / Operations / Ingestion`. A selected batch appends its display name and local date/time as the current, non-linked crumb. The Ingestion crumb returns to `/settings/ingestion` without carrying batch query parameters.
- Feed batch identity to the shell through a lightweight batch-summary lookup keyed by `runId`, or pass a typed page-context callback from `IngestionTasksTab`. Do not load the 50-card media page merely to name the crumb.
- Use `aria-current="page"` for the final item, visible focus styling for links, and compact ellipsis behavior on narrow screens. Breadcrumb links must remain reachable by keyboard and preserve browser Back/Forward behavior.
- Remove page-local breadcrumb or Back-button duplication after the shared breadcrumb covers that route. A mobile-only compact Back action is acceptable when the full trail cannot fit, but it must use the same canonical target.

Primary files: `Components/Pages/Settings.razor`, `Components/Pages/Settings.razor.css`, `Models/ViewDTOs/SettingsNav.cs`, `Components/Shared/AppBreadcrumbs.razor`, and the nested Settings components that currently render their own trails.

### 2. Make batch totals truthful and define the zero-addition state

Treat discovered files, processed files, and media groups added as separate facts.

- Extend the batch summary contract and query to expose `files_processed` explicitly. Keep `files_total` as the discovered/total count where that fact is useful.
- Render completed-run copy from the actual outcome, for example `38 files checked · No new media added` or `38 of 38 files checked · 6 library items added`. Use “checked” for completed scans because files may be unchanged, duplicates, unsupported, or already present.
- Before changing the observed 38-file row, inspect its batch record and compare `files_total`, `files_processed`, `files_registered`, review/failure counters, and the batch-media query total. Record which zero-addition cause applies; do not infer that processing failed from an empty additions list.
- When `AddedGroupCount == 0`, show an intentional empty-result treatment in the history row and do not offer `View media`. Keep the batch visible because the completed scan itself is useful history.
- If a zero-addition batch has review items or terminal failures, show a specific `Review items` or `View issue details` action backed by real data. Do not route those actions to an empty media grid.
- When `AddedGroupCount > 0`, require the first batch-media page and its total to agree with the summary count. Fix any predicate/snapshot inconsistency in the shared read service instead of masking it in the UI.

Primary files: `MediaEngine.Contracts/Activity/ActivityBatchDtos.cs`, `MediaEngine.Api/Services/ReadServices/ActivityBatchReadService.cs`, `Components/Settings/IngestionBatchHistory.razor`, and the presentation read service used by batch media.

### 3. Make `View media` the clear primary action

For batches that added media, render `View media` as a real `AppButton` link using `AppButtonStyle.Filled` and the primary Tuvima tone, matching the established Play/Read action weight.

- Refactor the row into a semantic article/layout with one explicit link action rather than styling a span inside a full-row button.
- Keep the state-colored left border and restrained dark surface. The solid control supplies the action hierarchy; it does not change the status color system.
- Reserve space so rows align with and without cover previews. Define hover, focus-visible, pressed, disabled, and loading states through the shared control layer.
- At compact widths, make the action full width after the summary while retaining a sensible reading and tab order.

Primary files: `Components/Settings/IngestionBatchHistory.razor` and `.razor.css`.

### 4. Rebuild the page and live-summary hierarchy from shared type rules

Align the Settings page header and ingestion summary with the reference's strong title, calm secondary copy, compact operational facts, and clear card grouping.

- Put breadcrumb, eyebrow, page title, description, Operations tabs, and actions on one spacing scale. Use shared header variables rather than ingestion-only hard-coded sizes.
- Keep the page title responsive around the current 30–32 px desktop target, but correct weight, line height, subtitle measure, and baseline alignment. Verify the actual configured UI font is loaded before adjusting fallback metrics.
- Give the live card title (`Adding media` or `Library is up to date`) a clear second-level size and weight, with its explanation directly below. Put operational counts in a compact aligned fact region with consistent numeric typography.
- Move `Scan All Folders` into the page-header action slot so it aligns with the page title and does not float independently above the summary.
- Keep the summary and Needs Attention panels visually balanced at desktop widths and stacked in source order on small screens. Use existing dark surfaces, borders, semantic colors, and spacing tokens.
- Audit every Settings route at desktop and mobile after the shared header change to catch wrapping, clipped selects, or oversized blank space.

Primary files: `Components/Shared/Shell/SidebarPageHeader.razor` and `.razor.css`, `Components/Pages/Settings.razor` and `.razor.css`, and `Components/Settings/IngestionTasksTab.razor` and `.razor.css`.

### 5. Make active ingestion visibly active

Drive all motion from authoritative `Model.IsRunning` state so it begins and stops with the live ingestion lifecycle.

- Keep the rotating Sync glyph and add a restrained animated progress ring or perimeter around the icon container.
- Add a subtle moving highlight to the active progress bar. Use determinate width only for truthful file-intake progress; use an indeterminate motion when the total is unknown.
- Pulse the small activity indicator beside `Being Added Now`, and update the accessible text/count through the existing polite live region. Do not flash or continuously reannounce decorative changes.
- Stop every animation immediately for idle, complete, interrupted, and failed states. Apply `prefers-reduced-motion: reduce` to the ring, progress highlight, pulse, and existing spinner, leaving clear static state text and icons.
- Confirm SignalR/polling updates change counts and cards while the animation continues. Motion must not be used to conceal stale data.

Primary files: `Components/Settings/IngestionTasksTab.razor`, `.razor.css`, `IngestionFacetIndicators.razor.css`, and the shared progress component only if its existing API cannot express the active treatment.

### 6. Regression and visual validation

Add focused tests before broad validation.

- Settings navigation tests: every visible Settings route has the expected breadcrumb hierarchy; deep links and selected-batch URLs resolve to the correct canonical ancestors.
- Component tests: a batch with additions renders one filled `View media` link; a zero-addition batch renders no media link; review/failure outcomes expose only supported actions.
- API tests: total/discovered/processed/registered counts map to distinct fields; summary `AddedGroupCount` equals the unfiltered batch-media total; first, empty, and deleted-media cases remain deterministic.
- Accessibility tests: breadcrumb navigation name, `aria-current`, keyboard order, focus visibility, live-region behavior, and reduced-motion coverage.
- Performance guardrails: retain the current three-row initial history request, ten-row `Show older` paging, six-cover preview bound, and 50-card detail page. The breadcrumb lookup must be one bounded summary read and must not materialize batch media.
- Visual review: capture Settings overview, nested settings details, Ingestion idle, Ingestion active, a batch with media, and a zero-addition batch at 1920×1080, a short desktop viewport, and mobile. Compare type hierarchy and panel proportions with the supplied reference.
- Live review: observe a controlled ingestion through at least three committed updates, then completion. Verify animation starts/stops correctly and count changes are visible within the existing two-second refresh target.

Run `dotnet build MediaEngine.slnx`, the focused Web/API tests, then `dotnet test MediaEngine.slnx --no-build`. Re-run the existing large-batch performance fixture to ensure the navigation and summary changes do not regress historical browsing.

## Acceptance criteria

- Every visible Settings page has a consistent breadcrumb generated by the shared shell.
- A selected batch shows a current batch crumb and a working Ingestion ancestor link.
- A zero-addition scan remains in history, explains that it added no new media, and cannot open an empty media browser.
- A batch that claims additions always returns those additions from its detail endpoint, subject only to an explicit user filter.
- Discovered, checked/processed, identified, reviewed/failed, and added counts are never presented as interchangeable units.
- `View media` is a filled primary action only when media exists to view.
- Page and summary typography match the supplied hierarchy across desktop and mobile without clipping or misalignment.
- Active ingestion has clear, state-driven motion; idle ingestion is static; reduced-motion users receive an equivalent static status.
- Existing history and media paging bounds remain intact, and the 10,000-group performance guardrail still passes.

## Implementation evidence

- The shared Settings shell now renders route-aware breadcrumbs for every visible Settings route. Selected ingestion batches append their batch name and local date/time while the Ingestion ancestor returns to `/settings/ingestion`.
- Batch summaries expose `files_processed_count` separately from discovered-file and added-media counts. Completed history rows use checked-file wording and render a media action only when media was added.
- The observed 38-file batch (`1ba50588-d573-4f98-956f-0cdb7862aa4a`) contained 38 duplicate outcomes, zero registrations, zero review items, zero failures, and zero batch-media results. It now remains visible as `38 files checked · No new media added` without an empty detail link.
- Populated batches use a filled primary `View media` action. Review-only zero-addition batches direct administrators to the Review Queue instead of an empty media grid.
- The scan action now lives in the Settings page-header action slot. The live card uses running and idle titles, aligned operational facts, state-driven ring/progress/dot motion, and a complete reduced-motion fallback.
- Browser verification covered the Ingestion page, a selected batch, and Metadata Providers. The accessibility tree confirmed canonical breadcrumb ancestors, the dynamic batch crumb, Queen's truthful `5 tracks added` count, the empty-batch state, and the populated-batch media link.
- `dotnet build MediaEngine.slnx --no-restore` passed with zero warnings and errors. The full solution test suite passed sequentially with 3,228 tests passed, 37 provider integration tests skipped, and zero failures. The 10,000-group historical-ingestion performance guardrail remained under its two-second limit.

## Product-owner summary

Settings now gives administrators a consistent trail back to where they came from, and ingestion history reports what each scan actually accomplished. A scan that checks 38 duplicate files says that it added nothing and no longer opens an empty page. Batches with new media have a clear solid action, the page hierarchy matches the intended design more closely, and active ingestion visibly moves while real work is happening.
