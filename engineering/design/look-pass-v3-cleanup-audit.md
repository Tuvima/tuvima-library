# Look v3 cleanup audit: pages, windows, parts and dead code

> Read-only audit, 2026-11-07, Fable, on main at `70e9ab48`. Companion to [Look v3](../specs/look-pass-v3.md) (WP13 and WP14). Counts come from scripted searches over `src/MediaEngine.Web`; "verify" marks a finding a builder confirms by removing the thing and building, because a scripted search cannot see a part reached only through code that builds its name.

## 1. What exists

| Thing | Count |
|---|---|
| Razor files (pages, parts, layouts) | 660 |
| Pages (files with an address) | 42, answering 63 addresses |
| Shared parts in `Components/Shared` | about 90 |
| Floating surfaces (dialogs, sheets, pop-outs, pickers, viewers) | 82 files build one; 9 hand-build their own dialog; 2 draw a raw `<dialog>` |
| Scripts in `wwwroot/js` | 60 |
| Global style sheet | 6,510 lines, 705 `!important`, 1,026 blank lines (longest empty run 25) |
| Component style files | 577 `!important`; seven files hold empty runs of 9 to 49 lines (`MediaBrowseShell`, `ViewArtworkPage`, `Settings`, `SequencePlacementPanel`, `NetworkRemoteAccessSettings`, `SidebarPageHeader`, `ListenPage`) |

Pages: Chronicle explorer, Collections, Companion, Design preview, Downloads, EPUB reader, Error, Extra viewer, For me, Library browse, Listen browse, Listen, Listen pop-up player, My activity, My library, My profile, Not found, Operations (redirect shim), Plugin, Profile picker, Read, Recently added, Recycle bin, Search, Settings, Setup, Shelf grid, Unified detail, Universe, Universe thing, Vault, Vault scene, View artwork, View contributions, View document rules, View documents, View galleries, View gallery detail, View, View people, View places, Watch, Watch player.

## 2. Dead or deprecated (delete candidates)

**Addresses nobody links to (6).**
- `/operations/ingestion`, `/operations/recently-added`, `/operations/needs-review`, `/operations/activity`, `/operations/timeline`: `Pages/Operations.razor` is a 32-line redirect shim that forwards old addresses to Settings. Settings spec D19 says retired addresses are simply not found, so the shim contradicts a decision and goes.
- Other forwards (spec D17, Shy 2026-11-07: no redirects anywhere, pre-beta): `Components/Settings/LibrariesTab.razor` `LegacyAddress()` / `RedirectSavedLink()` and its `RenamedTypeKeys` map (old `?type=` and `other-…` addresses forwarded to the current library page); the canonical-spelling replace driven by `SettingsRouteResolution.ShouldRedirect` (`Models/ViewDTOs/SettingsNav.cs`) and `DetailTabRouteResolution.ShouldRedirect` (`Services/Navigation/DetailTabNavigation.cs`). All go; a wrong spelling is not found. Kept, because they are actions rather than forwards: `VaultExit.Leave`, `DetailsOverlayState.ReplaceAddress` (the shown item was removed), profile switch and profile delete, the culture cookie return, and `PasswordChangeRedirectMiddleware` (a security gate).
- `/view/artwork/media`, `/view/artwork/people`, `/view/artwork/universes`, `/view/artwork/assets`: sub-addresses of the View artwork page with a "legacy query" reader; only `/view/artwork` is linked. Verify, then drop the four.
- `/vault/scene/{id}`: `VaultScenePage` is reachable only by typing it. Verify against the Vault spec (scene pages may be reached from the Engine's scene list) before deleting.

**Parts never rendered by app code (30; 14 are used only by tests).** Verify each by deleting it and building:
`Details/EditorPopover`, `Details/HeroProgressBlock`, `Details/OverviewTab`, `Details/ViewEditorPopover`, `Details/ViewSavedRibbon`, `Navigation/PwaInstallAction`, `Navigation/SessionEndedRedirect`, `Pages/RecentlyAddedPageContent`, `Settings/AvailablePluginsPanel`, `Settings/DevHarnessTab`, `Settings/EnrichmentTesterToolTab`, `Settings/IngestionMediaCard`, `Settings/Libraries/LibraryActivityTab`, `Settings/ProviderTesterToolTab`, `Setup/SetupAreaCards`, `Shared/AppFormFieldRow`, `Shared/AppSummaryStrip`, `Shared/AppWarningStrip`, `Shared/PlaybackPopoutShell`, `Shared/PlaybackPositionList`, `Shared/PlaybackPositionRow`, `Shared/PlaybackTimelineMetaRow`, `Shared/Providers/ProviderAccessCard`, `Universe/AlsoAppearsInPanel`, `Vault/NsfwQuickCheck`, `Vault/VaultArea`, `Vault/VaultHiddenReminder`, `Vault/VaultProfileSettings`, `Vault/VaultSceneView`. (`Pages/DesignSystemPreview` is in the list because it is a page nobody links to; it stays, as the proof page.) Three of these (the Dev harness and the two tester tabs) may be reached through the Settings menu builder by type in a development build; the builder checks.

**Scripts (1 confirmed, 2 cleared).** `wwwroot/js/watch-player.js` is referenced by nothing. `companion-device.js` and `vault-preferences.js` are loaded by their stores.

**Styles.** About 126 class names in the global sheet and 414 in component sheets are defined but never written in any page, part, script or other sheet. Part of the global number is false: modifier names such as `app-card--compact` and `app-tone--primary` are built from an enum at runtime. The builder removes a class only after a search for its stem and a build; the expected real figure is roughly 80 global and 300 component classes.

**Old names kept for a migration that is over.** `Shared/AppLegacyUiTypes.cs` holds 26 enums from the move off the old component library (`AppVariant`, `AppColor` and others), still used 118 and 141 times. They are not dead but they are a second vocabulary beside the `App*` parts' own names; WP13 maps each to the current name and deletes the file. The `--app-*` alias block (34) and the `--tl-*` alias block (24) are in WP7.

**"Legacy" in code comments (22 places).** The Engine media route behind the Dashboard proxy ("temporary legacy route"), `LibrariesTab.LegacyAddress`, the artwork workspace's "legacy role URL", the restored legacy book queues in playback. Each is a note that a transition was never finished; WP13 lists them for a decision (finish or delete) rather than deleting blind.

## 3. Not checked page by page

The Look v3 rules were derived from the shared parts and from searches over every style file, which touch every page's styling. The 42 pages were not each opened and judged. WP14 does that walk against the rules, page by page, with the preview page as the reference, and records a one-line verdict per page and per window kind in this file.

## Plain English summary

The app has 42 pages and about 660 building blocks. Thirty of those blocks are never used by the app (half only by tests), one script is dead, six addresses are reachable only by typing them (five of them an old redirect page that a decision already retired), and three more places quietly forward old or misspelled addresses to new ones, which Shy has ruled out for the whole app, and a few hundred style rules point at nothing. One file still carries 26 naming lists from the move off the old component library. Cleaning this up is mechanical, with a build after each removal to prove nothing needed it. What has not happened yet is a page-by-page walk of all 42 pages against the new rules; that is packet WP14 and its results get recorded here.
