# Setup, Operations, View settings, and install behavior

Status: implemented and verified, 8 September 2026. The original plan and investigation are retained below. See the [implementation and verification report](./setup-operations-view-polish-verification-2026-09-08.md) for final behavior, additional root causes, automated results, and browser acceptance evidence.

## Intended outcome

Setup explains account requirements while people type, offers a profile PIN, makes recovery codes readable, and permits adding media later. Operations shows current work and truthful batch progress, with historical runs kept separate. View explains one server storage base and automatically creates profile folders only when needed for real files. Dismissing the install banner remains effective across browser restarts.

## Root-cause findings

| Report | Evidence and confidence | Implementation direction |
| --- | --- | --- |
| Password cannot be revealed; short passwords lack live errors | Confirmed: `SetupAdministratorStage.razor` fixes both fields to `InputType.Password`. It disables submission below eight characters but supplies no password-length field error. `AppTextField` already supports immediate changes and adornment clicks. | Add accessible reveal controls, live length/mismatch feedback, and align client validation with the existing server policy of 8–128 characters. |
| No PIN during administrator creation | Confirmed: neither `SetupAdministratorRequest` nor `BootstrapAdministratorAsync` accepts a PIN. The identity service already supports hashed profile PINs and validates 4–12 ASCII digits. | Add an optional, explicit profile PIN section with confirmation; use the existing profile credential mechanism. |
| Recovery box too short | CSS conflict identified; rendered effect needs verification: setup sets a 250px minimum on the outer `AppTextarea` wrapper. Shared `.app-control` CSS fixes nested input-container height, while `.app-textarea` overrides input/slot/textarea heights but omits that container. | Fix multiline sizing in the shared control, then give the actual recovery text area sufficient readable height. |
| Media locations cannot be deferred | Confirmed: the validator rejects zero sources; the decision endpoint accepts deferral only for providers; both API readiness and `OnboardingRepository.CompleteAsync` require media locations to pass. | Implement real persisted deferral across UI, API, readiness, and completion. Renaming the button alone would leave setup blocked. |
| Comic artwork absent or wrong despite completed artwork work | Exact image defect is unconfirmed. Ingestion presentation selects artwork across asset/work/parent/grandparent identities without preferring the intended owner identity. Group artwork comes only from the newest member row. Facet completion can come from operation success independently of the selected cover. Embedded comic covers are persisted later in the write-back stage; the processor initially extracts the first archive page. | Trace one affected CBZ/CBR through extraction, persistence, selected managed asset, rendition, and browser rendering before fixing the responsible layer. |
| Being Added Now looks static | Confirmed contributors: `LoadCurrentGroupKeysAsync` ranks by `ingestion.file`/log timestamps rather than current enrichment operations. `MergePresentation` deliberately preserves the previous order of surviving cards. Current membership is batch-wide, so finished members can crowd out actively changing ones. Polling exists, but normally waits 15 seconds during active work. | Rank by current activity and latest relevant operation time, and preserve the server's new ordering when merging. |
| Page progress jumps to 100% while navbar shows partial completion | Confirmed: `IngestionTasksTab` calculates `FilesProcessed / FilesDiscovered`; `ShellActivityState` reads `BatchProgress.ProgressPercent`. They measure different things. | Use the same batch identity, progress snapshot, and completion rule on both surfaces. |
| View all needs a refresh | Strong source-level cause: `IngestionTasksTab` manually parses `view` only in lifecycle methods, has no parameters or location subscription, and the Settings route binds `runId` but not `view`. Query-only navigation can leave the child displaying its old mode. The paged child subscribes to location changes only after it mounts. | Bind route query state explicitly at the route owner and pass typed state to the child. Verify query-only navigation without a reload. |
| TV shows X of X episodes | Confirmed: the presentation query uses provider `episode_count` as `ChildExpected`; both card and drawer render a denominator and progress bar when it exists. Totals are cleared only for historical views. | Emit count-only TV progress for active and historical views, including drawer headings. |
| History routes to Review items | Confirmed: `IngestionBatchHistory` sends review-only batches to `/settings/review` and has no open action for some empty batches. | Give every batch one consistent Open batch action to its run-scoped browser. |
| Active run appears in history | Confirmed: default activity batch queries do not exclude active runs. | Separate active and historical membership server-side, including pagination and counts. |
| Operations review badge appears only when selected | Confirmed: `Settings.BuildNavigationItem` adds the count only when `IsOperationsWorkspace` is true. The shell already loads and subscribes to the count. | Render the badge for the visible Operations item regardless of selected section; preserve unknown/error state instead of converting refresh failures to a false zero. |
| Processing details is unnecessary | Confirmed: a dedicated details block in `IngestionTasksTab` repeats active/queued information already above it. | Remove the disclosure and its unused presentation styling. |
| View settings differ from other library details | Confirmed: `ViewLibrarySettings` has separate markup and isolated CSS rather than the catalogued library header, row grid, organization cards, and policy rows. Its source row declares five grid columns for six direct children. | Share the library detail presentation components and correct the source table structure. |
| Empty personal folders are created prematurely | Confirmed: `EnsurePersonalSpaceAsync` creates profile and Shared directories and the browser-upload source. Callers include View reads, profile settings, library-root changes, and upload. `EnsureManagedSourceAsync` also creates directories eagerly. | Separate logical identity/source registration from physical directory creation; create directories at actual write time. |
| Install dismissal does not persist | Confirmed: `Dismiss` only changes the Razor component's `_state`. The JS bridge has no stored dismissal and can notify the component that installation is available again. | Persist banner dismissal per browser/site and check it on initial load and subsequent install events. |

## Workstream 1: Setup

1. Add show/hide controls to password and confirmation with keyboard access and changing accessible labels. Display the length rule before input; after interaction, show an inline error while invalid and clear it immediately when corrected. Keep mismatch feedback and submission eligibility synchronized.
2. Add **Profile PIN (optional)** and **Confirm PIN**, explaining that the PIN unlocks/switches the local profile and does not replace the administrator password. Preserve leading zeroes, mask by default, and use the existing 4–12 digit policy. Validate all credentials before bootstrap writes. Coordinate PIN persistence with account creation so setup is not marked complete if a requested PIN failed to save; test failure/retry behavior and recovery-code delivery.
3. Correct the shared textarea container sizing. Present one recovery code per line in selectable monospaced text, with a useful minimum visible height and the existing Copy all / Download actions. Keep the saved-codes acknowledgment clearly visible at shorter viewport heights.
4. Replace **Verify and continue** with **Continue** when folders have been configured and **Do this later** as the explicit deferral action. Keep **Add a library**. Continue still validates configured paths; deferral records a deferred state rather than pretending validation succeeded. Retain folder safety checks when saving configuration. Finish setup with an empty library and offer a normal Add library entry point later.
5. Update readiness copy and the storage completion predicate together so reload/resume honors the decision. Preflight and administrator setup remain required.

Primary files: setup stage components, `SetupPage`, `AppTextarea`, shared `app.css`, setup contracts/client/endpoints, `FirstPartyIdentityService` and its interface, `OnboardingRepository`.

Acceptance: typing seven/eight characters updates feedback immediately; reveal works without losing focus/value; optional PIN can be omitted or saved and used; invalid PIN creates no partially configured administrator; all recovery codes are readable; media deferral survives refresh and allows setup completion; invalid configured folders are never silently accepted.

## Workstream 2: Operations and ingestion presentation

### Shared progress and live selection

Use one Engine-owned batch progress definition for the navbar and page. Snapshot loading must provide the same state available through live events, so opening the page midway through a run does not depend on having received earlier events. Keep file intake as a secondary factual count if useful. Unknown discovery totals should produce an indeterminate state. Required identity, enrichment, and organization work must prevent premature completion. Handle multiple active runs explicitly rather than mixing one run's numerator with another run's total.

For **Being Added Now**, prioritize actively worked groups, then recently updated unfinished groups, then queued groups. Use the latest relevant operation timestamp, with a stable identity tie-breaker. Apply ordering before the bounded preview limit. Merge updated card content in the returned order instead of keeping the first-render order. Use throttled relevant-event refreshes and a bounded polling fallback during ingestion. A proposed acceptance target is visible updates within five seconds of a relevant event, and within the fallback interval after a missed event.

Keep the opened drawer pinned to its selected group when cards move. Maintain a bounded preview and a complete paged current-run browser. Avoid moving a keyboard-focused target out from under the user during interaction. Test completion transitions and reconnects, including when operation counts stay unchanged but the active item changes.

### Comic artwork investigation and fix

For an affected CBZ/CBR and a control book/movie, record these facts during a fresh ingest:

1. Extracted page identity and bytes; check archive ordering and any explicit cover metadata before concluding the extracted page is wrong.
2. Persisted `entity_assets` owner, preferred flag, source, local path, dimensions, and rendition paths.
3. Operation/facet completion time versus the time a valid cover can actually be served.
4. The presentation DTO's chosen URL, the streamed original and requested `size=m` rendition, and the card/drawer rendered image.

The target is one canonical managed cover selection policy, with comic issue ownership respected even when the issue uses a series-level Wikidata identity. A parent's newer image must not displace the issue's selected cover. Handle a valid cover on another representative group member when the newest member lacks one. If image bytes change behind a reused asset ID, use the existing asset version/rendition invalidation mechanism or add an explicit revision; do not append arbitrary timestamps on every render. Operation completion and display-cover availability must be represented truthfully. Do not add title-specific comic fixes or direct provider URLs.

### Navigation, counts, and history

- Bind `view`, `runId`, and item selection at the Settings route owner and pass them explicitly into ingestion content. First click, browser Back/Forward, deep links, and returning to the summary must work in the mounted page.
- Show **N episodes added** everywhere on ingestion TV cards and drawers, without a provider denominator or episode-completion bar. Retain actual batch progress separately. Preserve the existing count-only rule for comic issues as well.
- Use **Open batch** for historical runs, including review-only, failed, duplicate-only, and zero-addition runs. Open `/settings/ingestion?runId=<id>&view=all` with truthful empty/outcome state when needed. Review Queue remains its own destination.
- Keep the active run section at the top, visibly titled **Active batch** (or **Active batches** where necessary), with a subtle pulsing green activity icon and reduced-motion support. Do not duplicate those runs in Batch history.
- Apply the historical predicate before server paging and total counts. A run remains active while required operations are outstanding even if intake has finished. Move it into history once terminal, without a refresh. Keep the three newest historical rows and Show older behavior.
- Show the Operations review badge from any Settings page. Keep it live and permission-aware. Remove **Processing details**, retaining durable diagnostics behind existing batch/item detail flows.

Primary files: `IngestionTasksTab`, `IngestionLiveDashboardState`, `ShellActivityState`, `IngestionPresentationReadService`, `ActivityBatchReadService`, ingestion contracts/progress services, card/drawer/paged/history components, and `Settings.razor`.

Acceptance: navbar and page display matching progress for the same run; no 100% until required work settles; current cards change as activity advances; new covers appear without refresh; drawer selection remains stable; View all works on first click; TV copy never contains a total; active runs never consume history slots; all batches open; the review badge is visible from Libraries and other Settings sections.

## Workstream 3: View storage and library settings

### Proposed page structure

Keep the existing Settings breadcrumb and one continuous library-detail page. Reuse the same header, section spacing, column grids, organization treatments, and policy rows as Read/Watch/Listen library settings.

1. **View** — “Personal photos, videos, documents, and recordings. Each profile has a private space; accepted contributions appear in Shared Library.”
2. **Folders** — one configurable **View library root**, then clearly derived **Shared Library** and **Profile personal spaces** paths. Use “This folder is the base for Shared Library and each profile's private files. Profile folders are created automatically when files are first added.” Replace “Personal storage root,” “not provisioned,” and manual provisioning copy. Include a compact path example and Add folder in this section.
3. **Organization** — visual rows/cards for browser uploads organized by capture date, managed folders preserving their hierarchy, and linked folders retaining their original locations. Display these as the actual storage rules, not fake choices where behavior is fixed.
4. **File handling** — concise policy rows for linked read-only files, managed storage, detaching without deleting originals, and the existing accepted-contribution transfer behavior.
5. **Sharing & access** — distinguish shared access, submission, review, and gallery-sharing permissions; link to profile/access settings.
6. **Advanced settings** — retain supported local processing facts and diagnostics without putting technical implementation details in the main explanatory flow.

Recommended naming deliberately distinguishes the common physical **View library root** from the accepted-content **Shared Library** folder. They are not the same authorization scope. The storage layout remains:

```text
<View library root>/
  Shared/                              accepted shared originals
  Profiles/<stable profile label>/
    Timeline/<year>/<month>/...         browser uploads
    Folders/<named folder>/...          managed sources
```

Show configured destinations as expected paths before they exist. This is a plan of where files will go, not a reason to create those directories. Linked external sources are displayed with their own actual paths. Profiles without content can show **No files yet — created automatically when needed**, rather than an error or provisioning action.

### Storage lifecycle changes

- Split personal-space identity reservation and source registration from directory materialization. Logical records may be registered for permissions/source configuration without making an empty directory tree.
- Remove eager directory creation from View reads, profile creation/settings, root configuration, and managed-source registration. Audit every `EnsurePersonalSpaceAsync` / `EnsureManagedSourceAsync` caller, including the full settings endpoint and Add Library flow.
- Validate authorization, type, and destination before writing. Create only the required directories when the first valid file is being added. Failed/cancelled first uploads should leave no placeholder profile trees; clean only empty directories created by that operation and never remove existing content or directories used by concurrent writes.
- Linked read-only sources can register/index existing files without creating managed profile folders. Named empty managed sources may exist as configuration until a real write needs their directory.
- Create Shared storage when accepted-content transfer actually needs it. Preserve verified transfer, independent sharing permissions, and existing original-file protection.
- Keep stable readable labels and retired-label reservations. Logical source reservations must prevent path collisions even when two empty sources have no directories yet. First concurrent uploads must resolve to one profile space and one browser-upload source.
- Keep populated-root changes rejected. Do not relocate originals, rename profile storage, or delete existing folders as part of this change. Update the pre-beta model directly without compatibility layers.

Primary files: `ViewLibrarySettings`, reusable library-detail components/styles, `AddLibraryWizard`, `ViewStorageService`, `ViewLibraryService`, View/profile/library/settings endpoints, source reconciliation and contribution transfer callers, and personal-space repository tests.

Acceptance: configure a root, create/enable multiple profiles, visit View/Settings, and add empty source configuration without creating placeholder trees. Add one valid upload: only its required profile path appears. Add a linked source: no managed tree appears. Verify concurrent first writes, invalid/cancelled uploads, reserved-label collisions, and first accepted shared contribution. Compare View and catalogued library settings side by side at desktop and mobile sizes.

## Workstream 4: Install behavior

Persist the dismissal preference in browser-local storage for this site and consult it both during registration and later install-availability notifications. Keep banner visibility separate from installation capability. Add a subtle **Install Tuvima Library** action to System overview that remains usable after banner dismissal, with platform instructions where installation requires them. Hide or mark it Installed in standalone mode. Do not re-show the banner on navigation, refresh, new tabs, reconnect, or browser restart; normal browser site-data clearing resets local preferences.

Primary files: `PwaInstallPrompt.razor`, `wwwroot/app.js`, and `OverviewTab.razor`. If overview and banner both subscribe to installation state, replace the JS bridge's single-observer assumption with shared state/subscriptions so neither steals the other's updates.

Acceptance: dismiss and reload/restart; banner stays absent. Dismiss and then install from overview; action still works. Test iOS guidance, unavailable install capability, installed mode, later availability events, and storage-unavailable session behavior without breaking page initialization.

## Delivery order and verification

1. Reproduce affected rendered states and capture baseline screenshots before code changes. Capture the comic evidence before choosing its fix.
2. Implement Operations progress, ordering, route state, artwork, and history changes together with focused read-service/state/route tests. These address the misleading live behavior first.
3. Implement setup credentials, textarea sizing, and end-to-end media deferral. Extend identity and onboarding tests, including partial failures.
4. Implement lazy View storage before presenting its automatic behavior in the redesigned settings. Extend `ViewStorageServiceTests`, View API/service tests, and library-root-change tests.
5. Persist install dismissal and add the overview action. Update PWA behavior coverage.
6. Update README, relevant setup/storage/settings docs, AGENTS/CLAUDE, and applicable guidance to replace mandatory media setup and eager provisioning descriptions. This plan does not change current product documentation until implementation lands.

Use behavioral tests for state changes, authorization, paging, races, and persistence; update existing guardrails where product wording or layout intentionally changes. Reuse `IngestionPresentationReadServiceTests`, ingestion progress/contracts tests, web ingestion/history tests, identity tests, onboarding repository tests, and View tests. Avoid treating markup-string checks as proof of browser navigation or sizing.

Before implementation development, stop Engine/Web processes per repository guidance. For sequence/artwork fixes, follow the repository's fresh-ingest procedure using only its explicitly designated disposable fixtures, protecting source media. Verify database/API state before browser inspection. Validate every affected Dashboard surface, including setup's initial and recovery states, Operations summary and paged batch, Settings badges, View details, and install overview, at 1920×1080 plus a shorter desktop and mobile viewport. Include keyboard focus and reduced motion.

Run the repository's required final gates:

```powershell
dotnet restore MediaEngine.slnx
dotnet build MediaEngine.slnx --no-restore
dotnet test MediaEngine.slnx --no-build
```

Run documentation and formatting checks appropriate to the touched files. No build, tests, or runtime changes were needed to produce this source-based plan; the acceptance work above is still pending.

## Product-owner summary

The main issue is that several screens describe a different state from what the system is actually doing. This plan makes setup easier to finish, keeps Operations accurate and visibly live, explains where personal and shared files belong, creates folders only when files need them, and remembers when someone dismisses the install banner. Most causes are identifiable in the code; the exact comic-image defect still needs a fresh-ingest comparison before its fix is chosen.
