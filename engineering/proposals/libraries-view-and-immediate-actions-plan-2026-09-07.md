---
title: "Libraries: Immediate Actions and View Settings"
summary: "Make lane filters immediate, save folder actions directly, and present View as a peer library with dedicated personal-media settings."
audience: "developer"
category: "proposals"
product_area: "settings-libraries"
---

# Libraries: immediate actions and View settings

Status: proposed September 7, 2026. Planning only. This revises the delivered Libraries refresh; it does not change application behavior yet.

The [phased delivery plan](./view-phased-delivery-plan-2026-09-07.md) is authoritative for implementation order, per-profile optional logins, the Profiles/Shared layout, and physical promotion of accepted managed personal files into Shared. Earlier conflicting ownership, layout, rollout, or reference-only retention details below are superseded.

The [View storage and organization refinement](./view-storage-organization-plan-2026-09-07.md) supersedes this proposal's physical-layout and View wizard details, adding readable per-user storage labels, calendar organization, and managed or read-only mixed folders.

## 1. Fix filter latency first

The source shows an avoidable reload chain:

1. `LibrariesTab.SetLibraryFilter` navigates to a new `scope` query.
2. `Settings.OnParametersSetAsync` calls `EnsureElevationForCurrentRouteAsync`, whose cache key includes `PathAndQuery`.
3. A query change clears `_adminElevationResolved` and awaits a server elevation check. The shell's loading branch can remove the Libraries component during that wait.
4. Recreating Libraries repeats configuration and ingestion snapshot requests, followed by read/write probes for every source. Initial content waits for those probes.

This is a source-based diagnosis; elapsed timings have not been measured. Instrument component mounts, request counts, and filter-to-render timing to confirm it before editing.

Keep the mounted Libraries component and filter its loaded summary locally. Preserve readable `scope` URLs and browser Back/Forward without resetting the page, scroll position, or data. Query-only scope changes must not invalidate an otherwise valid administrator session. Retain expiry, profile-switch, revocation handling, and server authorization for every protected operation.

Load configuration independently of health and inventory. Render rows immediately; fill in bounded background status results without hiding the list. Replace dependence on the full Operations snapshot with a small Libraries summary where needed. Reuse recent health results rather than probing the filesystem on navigation.

Acceptance: after initial load, switching All/Read/Watch/Listen/View causes zero library reloads and zero filesystem probes. Target the next render after the normal Blazor event round trip, with no additional awaited data requests. Verify under delayed API and inaccessible network-folder conditions.

## 2. Make folder actions complete in place

Move **Add folder** into the Folders section header on every library detail. Remove the floating Add folder action and global Save changes / unsaved-configuration bar.

- **Add:** the dialog selects a path and any required folder settings. Its final **Add folder** action validates and persists that one addition. Closing before confirmation changes nothing. Show the row after success, with an in-progress indicator during the request and a useful retry message on failure.
- **Remove:** show one confirmation naming the folder and explaining that detaching does not delete files. Confirming persists the removal immediately. Removing a primary destination requires an explicit replacement when remaining managed sources need one; include that selection in the confirmation. Never silently choose a destination.
- **Last folder:** allow detachment, retain the library with **No folders configured**, and disable incoming operations that require a destination. Update current validators accordingly. This avoids making removal secretly mean deleting the library.
- **Other settings:** selections and switches save individually with Saving/Saved/error feedback. Names use a small inline edit action. Custom templates and coupled policy changes use focused dialogs with one Apply action, so incomplete text or interdependent values are never saved piecemeal. No page-wide Save button.

Use targeted Engine commands for folder add/update/detach and policy edits. Each command reads current configuration, validates the affected invariants, persists atomically, and returns the authoritative result. Add concurrency protection so simultaneous edits cannot overwrite unrelated libraries. Do not use a stale whole-configuration PUT as an autosave mechanism. Validate the changed path and relevant conflicts, not every unrelated source. Prevent double submissions; retain state on failure. Watcher refresh and any copy/indexing jobs report their actual completion separately from configuration save.

## 3. Put View in the Libraries list

Add **View** alongside All, Read, Watch, and Listen. All includes the structured libraries and one **View** row. View scope shows that row alone. Remove the separate View storage card from the overview.

Use the same columns: Library, Area, Folders, Items, Health, and open affordance. The View row is named **View**, described as **Personal photos, videos, documents, and recordings**, and opens `/settings/libraries/view`. The root appears as secondary folder information; folder count means attached content sources, excluding the structural root and profile directories. A missing root shows **Not configured** rather than hiding the row or fabricating health.

Keep metrics truthful: count the user-facing View entry once, never internal profile bridges; count attached content sources consistently; use a dedicated View asset total without double-counting multiple source paths for one asset. Review Queue remains catalogue-only and is labeled accordingly. Unavailable counts stay unavailable. Aggregate View statistics must come from an authorized administrator summary and must not expand access to private asset contents.

This is a unified administrative presentation. Preserve the existing single View root and one Personal Space per enabled profile. Adding folders must not create extra user-facing libraries, change ownership, or automatically share content.

## 4. Dedicated View detail page

Use the same page width, typography, spacing, section cards, status badges, and shared controls as other libraries. Header: **View**, subtitle **Personal media · Local files**, and truthful health. No floating configuration buttons.

| Section | Contents and behavior |
| --- | --- |
| **Folders** | Section-owned **Add folder**. First show the single managed root, approved storage location, measured access, and capacity when available, with Choose root / Change root in this section. Then list actual sources with folder/name, owner profile, managed or linked read-only mode, include-subfolders state, access/status, and row actions. Root/profile directories are structural, not extra content sources. |
| **Personal Spaces & Sharing** | A compact profile table with View enabled, Personal Space status, Shared View access, contribution to Shared View, and Gallery-sharing policy. Reuse the canonical Users & Access editor through a focused Manage access action rather than inventing a second policy store. Explain that access, contribution, and Gallery sharing are independent. |
| **Local Processing** | Read-only explanation of supported behavior: index local files, extract embedded dates/file details and available GPS, group related files, detect duplicate content, and generate supported thumbnails. Show real activity/failures when available. No provider, Wikidata, identity matching, confidence, or Review Queue controls. Local metadata exists; external metadata enrichment does not apply. |
| **File Handling** | Explain managed copies versus linked originals, duplicate-content/source preservation, and what disabling or detaching a source does. Linked originals always remain read-only. Reuse established View trash/recovery behavior; do not introduce catalogue naming templates or generic replacement/writeback switches. Only expose editable policies actually supported by View. |
| **Advanced Settings** | Collapsed technical root hierarchy and source identifiers, links to relevant diagnostics, and any existing recovery action that has a real backend. Keep ordinary browsing preferences in View itself. No placeholder controls for OCR, face recognition, maps, cloud sync, or mobile backup. |

**View Add folder dialog:** choose the owning enabled profile first, then **Copy into Personal Space** or **Link existing folder read-only**, using the shared server folder picker. Show the destination/source summary before committing. Persist the source on confirmation; managed copying may continue as a separately reported operation. Reuse the existing profile-source services and do not change sharing policy as a side effect. If no profile is eligible, provide a route to enable View access instead of silently assigning ownership.

**Root selection:** use the existing root service. A prospective root can be validated before creation. A populated root cannot be changed until a supported relocation workflow exists; show that limitation next to Change root. Selecting a root is not authority to move existing personal files.

**Detach:** confirmation identifies the profile and source, states that files remain on disk, and explains the effect on that source's visibility/indexing according to existing View semantics. Managed and external sources may need different explanatory copy. Confirm those semantics in the existing source service before finalizing copy; do not silently convert detachment into physical deletion.

## 5. Breadcrumb and route requirements

- Preserve the one shell-owned breadcrumb above every heading: `Settings > Administration > Libraries > View` or the configured structured-library name.
- Libraries ancestor restores the previous scope, including `scope=view`; active crumb is non-linked and uses `aria-current="page"`.
- Keep the folder picker's independent storage/path breadcrumb.
- Support direct loading, Back/Forward, expired sessions, and mobile wrapping. View details follow the same read-only mobile policy as structured library details.
- Personal Media in Add Library opens this same View configuration flow. If configured already, make that explicit; do not imply another View library will be created.

## 6. Delivery and verification

1. Confirm and remove the filter reload chain; add request-count and mounted-component regressions.
2. Implement targeted, concurrency-safe mutations and immediate folder actions. Test success, failure, cancellation, double submission, primary replacement, last-folder removal, and unrelated-library preservation.
3. Add the View summary row, scope, route, and breadcrumbs; remove the overview storage card.
4. Build View details from existing root/profile/source services. Reuse access editing and shared folder selection. Add only the aggregate status contracts required by the page.
5. Verify profile ownership, read-only originals, permission boundaries, duplicate counts, missing/offline roots, and asynchronous copy status. Ensure View content never enters catalogue enrichment or Review Queue.
6. Compare all scopes and details at desktop, tablet, and mobile sizes, including header/row alignment. Run solution restore, build, and tests; update product documentation with measured latency results and supported behavior.

## Product-owner summary

Switching media areas will filter the list immediately. Folder actions will finish when confirmed, with no second Save step. View will appear beside the other libraries and open a familiar settings page focused on personal folders, profile ownership, sharing, and local processing. Breadcrumbs remain part of every page.
