---
paths:
  - "src/MediaEngine.Web/Components/Settings/**"
  - "src/MediaEngine.Web/Components/Setup/**"
  - "src/MediaEngine.Web/Components/Library/**"
  - "src/MediaEngine.Web/Components/LibraryItems/**"
  - "src/MediaEngine.Web/Components/View/**"
  - "src/MediaEngine.Web/Services/Settings/**"
---

# Dashboard rules: Libraries settings, Setup and View storage

Current per-surface rules for the Dashboard, moved verbatim from AGENTS.md on 2026-10-17 (dated headings replaced by subject headings) and split by area on 2026-10-20 so only the relevant file loads. Public per-surface behaviour lives in `docs/product/presentation-rules.md`; where the two disagree, stop and ask.

## Libraries settings

Settings > Libraries has three levels, each its own address (library setup spec, WP1 and WP2): the front door of group cards (Watch, Listen, Read, View; Vault only while Vault mode is on) with counts, the worst folder health and the Import folder row (`/settings/libraries`); a group page listing its types (`/settings/libraries/{group}`); and the type page (`/settings/libraries/{group}/{type}`), which is `LibraryFoldersPanel` for that one type without its left-hand type list. Groups and types come from `Components/Settings/Libraries/LibraryCatalog.cs`; View's folder settings sit at `/settings/libraries/view/photos`; old `?type=` links redirect. First-run setup still shows the panel with its type list and the Import folder. There is no Library settings page and no detail tabs. A folder is Managed or As-is (`management_mode`), each type has one New media goes here folder, and there is no priority. Advanced holds per-type naming and tags plus the global duplicate and artwork rules. Header and list rows share one full-width column grid.

There is no Add Library wizard: a type's library is created when its first folder is added through the folder picker (full-width paste-a-path bar, single-click selects and double-click opens, copyable selected path, media locations listed before drives, paths as the Engine sees them, folders only, Tuvima's own folders never listed). Personal Media configures the existing shared View root and reserves profile identities through View services; it must never add user-facing personal library rows. Changing a populated View root is rejected; no relocation is implied. Convert to Managed, Merge into and the Import folder run as durable organising jobs (`folder_jobs`, ledger `folder_organise_log`); see `docs/architecture/library-model-and-intake.md`. Setup has six steps (Name your server, Your account, Libraries, Language, Enhance your library, All set); the server check runs silently unless a Required tool is missing, when it lists "A few things to install" with Check again and Continue (never blocks). Tuvima's own library folder is created under `TUVIMA_DATA_DIR` (shipped config uses the `{TuvimaData}` placeholder).

Existing-file protection applies to managed originals through the shared mutation gate. Incoming staging files and destination use remain eligible; existing-library sources remain read-only. Naming and duplicate policies must be enforced by ingestion, with unique-name filename collisions preserving existing files.

### Readable View storage

Libraries now includes All/Read/Watch/Listen/View filters, a single View summary row, and a dedicated View settings page. Scope changes filter the mounted list without repeated library loads or filesystem probes. Add folder belongs inside Folders and saves immediately; detachment asks for confirmation and keeps files. Names and custom templates use focused Apply dialogs, while other library settings save individually with conflict detection. There is no page-wide Save. Breadcrumbs remain shell-owned and retain the selected scope.

New Personal Spaces use `View/Profiles/<stable-readable-label>/Timeline` and named managed folders beneath `Folders`. Persisted label reservations survive profile deletion so another profile cannot silently inherit the former folder. Display-name changes do not rename storage. `View/Shared` is the server-owned Shared Library beside Profiles. Existing obsolete View source state fails closed rather than moving originals or using compatibility paths. View source additions queue reconciliation in its dedicated hosted worker.

## Setup, Operations and View storage

Setup offers an optional 4–12 digit profile PIN alongside the administrator password, live password validation and reveal controls, and readable recovery codes. Media locations can be explicitly deferred and configured later through Libraries. Reloading setup must render fetched stage data after asynchronous initialization.

The live page and the menu indicator consume the same Engine queue progress (`IngestionQueueProgressDto`), including outstanding identity and enrichment work. The Now panel shows at most 10 in-flight items, each held at least 3 seconds, rotating as items finish; photos and personal videos are counts, never rows. Comic covers remain issue-scoped. TV and comic ingestion counts never display a provider denominator. There are no batches anywhere a person can see: no batch history, no Open batch, no run links (spec ingestion-and-readiness D12). Review counts remain visible throughout Settings through the administrator banner. Processing details is removed.

View uses a common library-detail header and Folders, Organization, File Handling, Sharing & Access, and Advanced Settings sections. The configurable View library root contains Shared and Profiles; Shared Library is an authorization scope distinct from the physical base. Logical personal-space/source registration never creates directories. Uploads and accepted transfers create destination folders only at actual write time. Merely visiting View or adding a profile must leave unused storage empty.

The install banner records dismissal in browser/site local storage. Subsequent install events and navigation respect it. Settings > Overview > Apps offers an independent manual installation action ("Install on this device").

_Note 2026-10-19: Operations pages now redirect; review lives in Settings → Needs Review (`/settings/review`, an off-menu page under Library and Ingestion), opened from the administrator banner; Recently Added is no longer a menu row and holds no review list._
