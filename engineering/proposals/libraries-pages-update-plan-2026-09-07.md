---
title: "Libraries Pages Update Plan"
summary: "Refresh Libraries, library settings, creation, and folder selection while preserving page and path breadcrumbs."
audience: "developer"
category: "proposals"
product_area: "settings-libraries"
tags:
  - "settings"
  - "libraries"
  - "navigation"
  - "accessibility"
---

# Libraries pages update plan

Status: implemented September 7, 2026. The specifications below record the approved design and its acceptance criteria. See the delivery record for checks performed and product limits.

## Intent and reference interpretation

Update the administrative Libraries experience using the four supplied images: Libraries overview (image 3), consolidated library settings (image 4), Add Library (image 1), and Select Folder (image 2). Preserve the existing Settings shell and make breadcrumbs a release requirement, even where the references omit them.

The images are design references. Their sample counts, paths, IDs, dates, health indicators, permission claims, and control labels are not evidence of actual application state or authorization to modify files. Proposed behavior below reconciles those references with the current product model.

Use their dark surfaces, restrained borders, purple selection and primary actions, compact tables, and clear section hierarchy through shared application controls and theme tokens. Maintain the existing administrator permissions and device restrictions: desktop/tablet editing, mobile read-only presentation, and unavailable states on television/automotive.

## Current implementation and gaps

| Surface | Existing implementation | Planned change |
| --- | --- | --- |
| Settings shell | `Settings.razor` owns `AppBreadcrumbs`; arbitrary library route segments currently fall through to formatted route text. | Keep one shell-owned breadcrumb and resolve library names instead of displaying GUIDs. |
| Libraries | `LibrariesTab.razor` already renders lane filters, metrics, a library table, and View storage. | Refine hierarchy and spacing to image 3, audit counts and health, and clarify View storage saving. |
| Library details | Overview, Folders, Organization, and Advanced are separate `tab` query states. | Consolidate them into the single settings page in image 4. |
| Add Library | `AddLibraryWizard.razor` supports structured types; managed sources add a separate Organization step. | Use three stable steps and add a Personal Media branch backed by existing View storage. |
| Folder picker | `ServerFolderPicker.razor` already supports approved roots, path breadcrumbs, search, manual paths, access validation, and cancellation of obsolete requests. | Refine layout, grouped conflict presentation, and validation behavior while keeping the shared picker. |
| File handling | Policies include duplicates, source management/access, organization participation, preserve-originals, and source writeback overrides. Filename collisions currently receive a unique suffix in `FileOrganizer`. | Define and enforce exact semantics before exposing the reference's protection and filename-conflict controls. |

## Required navigation contract

1. Preserve `AppBreadcrumbs` in `Settings.razor`, above the page heading. Child pages must not add duplicate page breadcrumbs.
2. Retain the existing Settings group ancestor. Expected trails are `Settings > [existing group] > Libraries`, then `> Add Library` or `> Audiobooks` for the relevant page. The final label uses the configured library name, including after rename.
3. Ancestors are real links; the current page is non-linked and has `aria-current="page"`. While loading an identity, use a neutral label such as `Library`; an unavailable/deleted library uses a clear unavailable label, never its raw GUID.
4. The wizard stepper supplements the breadcrumb. Advancing steps does not remove or replace it. Breadcrumb navigation, Back, and Cancel honor unsaved draft handling.
5. The folder picker retains its separate path breadcrumb: storage location plus clickable ancestor folders. Distinguish the browsed path from the selected folder summary. Opening a modal must leave the underlying page location intact.
6. At narrow widths, allow wrapping or accessible overflow without hiding the current page, removing ancestor access, or colliding with page actions. Keep visible keyboard focus and meaningful navigation labels.
7. Keep canonical routes `/settings/libraries`, `/settings/libraries/new`, and `/settings/libraries/{id}`. Remove the retired library tab navigation and its application-generated links; do not introduce compatibility aliases or parallel old/new editors.

## Page specifications

### Libraries overview — image 3

- One Libraries heading, concise description, and right-aligned Add library action below the breadcrumb; avoid duplicate shell/child headings.
- All, Read, Watch, and Listen filter the structured library table. Make selected scope URL-backed so returning from a library restores it.
- Keep three metrics: total structured libraries, total configured folders, and items needing review. Keep these library-wide while filters scope the table, and label that scope explicitly. Exclude internal personal bridges. Review links to the existing Review Queue.
- Audit current ingestion-snapshot-derived counts against persisted library inventory. Item totals must represent library contents, not only the current or latest ingestion run. Missing data displays unavailable/unknown rather than a fabricated zero.
- Show Library, Area, Folders, Items, Health, and an open affordance. Each row has one semantic library link and remains keyboard accessible. Health reflects actual checks and source requirements; a deliberately read-only source can be healthy.
- Retain a separate View storage card: approved storage location, relative View root, Choose root, and a readable folder hierarchy preview. Use an explicit save action and dirty state for root edits. Explain the hierarchy in normal language; put the exact technical path in secondary expandable/copyable detail.
- Explain that each enabled profile has one Personal Space, with multiple sources inside it. Shared View is a permission-filtered virtual view and does not duplicate files. Avoid promising access or sharing independently of administrator policy.
- Present loading, empty scope, no libraries, partial health failure, and Engine unavailable states. Root changes must not silently relocate or abandon existing personal files; reject an unsafe change with an actionable explanation unless a separately supported relocation workflow exists.

### Library settings — image 4

- Replace the current detail tab strip with one page: identity header and Save changes/Add folder actions, then Folders, Organization, File Handling, and collapsed Advanced Settings. Move the internal ID into Advanced Settings.
- Use one compact health summary, with truthful unknown/read-only/unavailable states. Keep action placement consistent; if Add folder appears in both the header and section, both invoke the same handler and permissions.
- Folders shows name/path, role, management, include subfolders, measured access, status, and change-path/test/remove actions. Remove detaches configuration only and never deletes source files. Validate the required primary destination for managed imports; an all-read-only library does not need one.
- Organization uses three cards: Tuvima Standard, Keep original names, and Custom. Custom reveals a validated naming template and media-aware preview. Preserve original structure according to the selected policy. Saving naming settings does not immediately reorganize existing files; reuse the explicit reorganization preview/apply workflow.
- File Handling contains existing-file protection, duplicate handling, filename-conflict behavior, and metadata writeback. Each control must correspond to persisted, Engine-enforced policy. Existing/read-only sources remain protected regardless of library-level choices.
- Define protection to cover moving, renaming, replacement, and writeback of pre-existing media; do not assume `PreserveOriginals` already guarantees all four. Protection defaults on and writeback defaults off. Disabling protection makes eligible managed operations possible, but saving settings alone must not launch bulk file changes.
- Keep duplicate detection separate from a filename collision with different content. Preserve current safe collision behavior unless an explicit policy changes it. Label it truthfully, for example `Keep existing file; give incoming file a unique name`, rather than implying the incoming item will be skipped.
- Preserve source-level writeback settings and global inheritance. If sources differ, show Mixed/inherited state and expose source overrides; do not flatten them into a misleading On/Off switch. Protection wins where writeback would modify protected files.
- Advanced Settings holds name, visibility, catalogued kind, internal ID, and metadata policy/inheritance with a link to the correct media-type settings. Local-only/manual behavior remains respected.
- Track one draft and baseline for the page. Save persists the validated change set and refreshes the baseline; failure retains edits. Discard restores it. Cover internal navigation and browser departure with unsaved-change handling. Apply disabled/read-only rules to every input and action.

### Add Library — image 1

- Keep three steps for both branches: Choose type, Add folders, Review. Use `Add folders or root` as the second step's supporting copy when appropriate.
- Choose type presents Structured Media and Personal Media cards. Structured Media then requires one of Books, Comics, Movies, TV Shows, Music, or Audiobooks. This creates one selected structured library per run, not six implicitly.
- Personal Media configures the shared View storage root and profile provisioning through the existing Personal Space services. It never creates a user-facing personal library row, a new library kind, or multiple Personal Spaces per profile. If View storage is already configured, show that state and explain that the action edits its root.
- Structured Add folders uses the shared picker, supports multiple sources and explicit source modes, and validates primary destination requirements. Place compact Organization options in this step for managed sources, eliminating the fourth step without losing configuration.
- Personal Add folders selects one approved writable root and previews the profile/source layout. Existing external archives remain linked read-only through the owning profile's settings. Only enabled/eligible profiles are provisioned; verify future-profile creation uses the same path.
- Review shows exactly what will be created or configured, paths, source protections, primary destination, and organization policy. For Personal Media, show root/provisioning and policy-dependent sharing information rather than structured source roles.
- Use branch-specific completion labels: Create library or Save View storage. Revalidate on commit; prevent double submission. Back retains draft values, branch changes clear incompatible state, and Cancel before commit performs no persistence or folder creation.
- Cover the existing embedded setup use of the wizard and its setup-session authorization. Do not break initial setup while updating the normal Settings flow.

### Select Folder — image 2

- Reuse `ServerFolderPicker` everywhere: creation, library folders, View root, profile archives, and setup. Keep the storage sidebar, path breadcrumb, Up/Refresh, search, folder list, selected path/access summary, and footer.
- Keep select and navigate behavior explicit: selecting a row chooses a candidate; its open affordance enters that folder. Search stays within the current location and does not silently change the selected candidate.
- Group overlap issues under a clear warning/error panel with affected library names. Extend the canonical validation contract with structured conflict information if needed; do not parse human-readable messages for IDs or labels.
- Distinguish exact reuse, being inside a configured source, and containing configured sources. The Engine determines blocking severity and `CanSelect`; selection remains disabled during validation or for blocking issues.
- Preserve approved-root containment, protected-folder checks, source-edit exclusions, and mode-specific read/write requirements for both browsing and manual entry. Validate symlinks/reparse points and path traversal through the existing server boundary.
- Revalidate on Refresh and confirmation, and invalidate old validation as soon as candidate/path/mode changes. Cover manual-entry races as well as browsing/search races so stale permission results cannot enable selection.
- Display disk space, filesystem, and modified dates only when returned. Manage dialog focus, Escape/Cancel, long paths, empty folders, inaccessible roots, and narrow-screen layout.

## Delivery sequence

1. **Navigation and policy foundation.** Extend the existing breadcrumb work without overwriting the unrelated Settings/Ingestion edits already present in the working tree. Document the exact file-handling policy mapping and gaps. Add required contract/domain/Engine enforcement before interactive controls. No legacy readers, schemas, or route shims.
2. **Shared folder picker.** Implement grouped conflict presentation, responsive layout, and validation freshness. Verify all picker consumers, including setup and Users & Access.
3. **Overview and consolidated settings.** Refactor `LibrariesTab.razor` into focused overview/detail/section components as needed, keeping shared `App*` controls. Remove dead detail-tab fragments and rename touched retired `media-management` CSS hooks to Libraries-specific names. Wire counts, health, and draft/save behavior.
4. **Three-step wizard.** Add the View branch, move organization into folder setup, retain embedded setup support, and wire branch-specific review/commit behavior.
5. **Regression and visual acceptance.** Update tests that currently require the old tab and four-step structures. Verify every affected surface in the running app, then update product documentation for the delivered behavior.

Primary implementation areas: `Components/Pages/Settings.razor` and its CSS; `Components/Settings/LibrariesTab.razor` and `AddLibraryWizard.razor` and their CSS; `Components/Shared/ServerFolderPicker.razor` and its CSS; the typed Engine client; canonical Contracts; library configuration/policy validation; `ServerFolderBrowserService`; `ViewStorageService`; and the relevant organization/writeback execution paths.

## Acceptance and verification

- Breadcrumbs remain present on overview, Add Library, and library settings, including loading/error states. Name resolution, rename, direct links, keyboard navigation, returning to the filtered list, and mobile layout work. The picker has functional ancestor navigation independently of the page breadcrumb.
- Complete creation for all six structured types, managed/read-only/mixed sources, and the Personal Media branch. Verify one Personal Space per eligible profile, future-profile provisioning, and no cross-profile permission expansion.
- Verify save/discard, failed saves, invalid/custom naming, last-source removal, primary-source changes, duplicate and filename-conflict differences, mixed writeback policies, and source protection. Tests for file-handling changes must exercise actual temporary filesystem outcomes, not only rendered labels.
- Verify counts and health with no run, active ingestion, historical runs, deliberately read-only sources, failed checks, and Engine outage. Unknown state must not masquerade as healthy or empty.
- Extend `SettingsBreadcrumbGuardrailTests`, `ServerFolderPickerUiTests`, and focused View/library/API tests. Include behavior tests for breadcrumb name resolution, wizard draft transitions, stale validations, and policy enforcement. If contracts change, update boundary/round-trip tests and reviewed snapshots.
- Before implementation, stop the running Engine/Dashboard processes as required by repository instructions. For the completed code change, run `dotnet restore MediaEngine.slnx`, `dotnet build MediaEngine.slnx --no-restore`, and `dotnet test MediaEngine.slnx --no-build`.
- Capture before/after rendered states at 1920×1080 and verify a lower-height desktop, tablet, and mobile read-only viewport. Cover both wizard branches, every step, expanded Advanced Settings, and the folder picker in valid, conflict, and long-path states. No overlapping controls, hidden breadcrumbs, inaccessible save actions, or unintended nested scrolling.
- Update relevant README/docs/AGENTS/CLAUDE and `.agent` guidance with the implemented flow. Record any unfinished acceptance items explicitly; a static reference match alone does not establish functional completion.

## Product owner summary

Administrators will get a clearer Libraries overview, one complete settings page per library, and a consistent three-step setup flow for structured media or Personal Media storage. Folder selection will explain access and conflicts before saving. Breadcrumbs will remain visible throughout so users always know where they are and can return to Libraries. File protection and sharing will reflect real system behavior.


## Delivery record

- Preserved the shell breadcrumb, resolved configured library names, and retained lane scope when returning from details. Consolidated the library editor and introduced the Structured/Personal three-step wizard.
- Corrected the Listen library row alignment using the same full-width grid as the heading. Status badges retain compact widths; narrow layouts scroll inside the table card.
- Folder selection groups conflicts by library, clears stale manual-path validation, and validates again at confirmation. Library saves validate the proposed configuration's sources and naming templates on the server.
- Library counts now query current non-orphaned assets rather than summing ingestion history. Missing folder snapshots remain unavailable. Deliberately read-only sources can be healthy.
- Connected protection, duplicate handling, and library naming to ingestion. Protection covers managed originals while allowing incoming staging operations and destination use. Filename collisions keep the existing file and use a unique incoming name. Duplicate content is indexed once even when both physical files are retained.
- Personal Media configures one View root and provisions enabled profiles through existing services. Prospective roots can be validated without creation; changing a populated root is rejected. Relocation remains unavailable.

Verification includes solution restore/build/test, rendered library and wizard tests, failed Personal Media save/retry, stale manual path selection, read-only health, protected original/staging mutation, per-library naming, prospective View root validation, and populated-root rejection. Browser checks cover the Libraries overview/Listen filter, named detail breadcrumb, consolidated settings, Personal Media steps/review, and named folder conflicts, with desktop (1920×1080), tablet (1024×768), and mobile (390×844) layouts. Before/after screenshots were inspected in the task. Live checks did not create or reorganize user media; configuration commit behavior is covered by automated tests. Exhaustive creation of all six types against real media is not claimed.

Final gates: restore succeeded; solution build completed with zero warnings and errors; solution tests passed with **3,244 passed, 0 failed, 37 skipped** across 13 test projects. The skips are existing provider integration tests. Mobile library details are available read-only; creation remains desktop/tablet only.
