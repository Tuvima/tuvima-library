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

## WP13 results (dead code sweep)

> Built 2026-11-07. Every item below was deleted or changed with a build after each batch; the whole Dashboard test project passes.

### Deleted

- **Addresses and forwards (D17).** `Pages/Operations.razor` (the `/operations/*` shim and its five addresses); `LibrariesTab` `LegacyAddress()`, `RedirectSavedLink()` and `RenamedTypeKeys`; `SettingsRouteResolution.ShouldRedirect` / `IsCanonicalRoute` and the "replace the address with the canonical spelling" block in `Settings.razor`; `DetailTabRouteResolution.ShouldRedirect`; the Settings page address aliases (`profile`, `household`, `account`, `provider-tester`, `tester`, `dev-harness` and three more: the `Aliases` list on `SettingsItemDef`); the lenient punctuation matching of Settings slugs and detail tabs (only the exact spelling answers now, in any letter case; an unknown tab shows the first tab and the address stays); the four `/view/artwork/media|people|universes|assets` addresses and the legacy query reader; the `/for-me?view=my-list` and all-areas My List / Liked forwards in `ForMe` (the top-bar bookmark now opens `/my-library` directly); the `/account/security` GET forward in the sign-in endpoints (and the `/settings/account` alias targets there now point at `/settings/security`).
- **Parts nothing renders (15).** `RecentlyAddedPageContent`, `IngestionMediaDrawer` (only drawn by it), `AppCompactPager` (only drawn by it), `AvailablePluginsPanel`, `LibraryActivityTab`, `PlaybackPopoutShell`, `AppSummaryStrip` (with `AppSummaryItem`), `PlaybackPositionRow`, `PlaybackPositionList`, `PlaybackTimelineMetaRow`, `AppWarningStrip`, `AppFormFieldRow`, `ProviderAccessCard`, `HeroProgressBlock`, `SetupAreaCards`; each with its `.razor.css`, its string-table keys (36 keys in `SharedStrings*.resx`) and the tests that only tested it.
- **Script.** `wwwroot/js/watch-player.js` (referenced by nothing, including the service worker and tests).
- **Styles.** 61 classes defined in `app.css` and component sheets that no app code names (listed by the tightened `NoStyleRuleTargetsAClassNothingUses`), and the blank runs in `MediaBrowseShell`, `ViewArtworkPage`, `Settings`, `SequencePlacementPanel`, `NetworkRemoteAccessSettings`, `SidebarPageHeader`, `ListenPage` and `app.css`. `maplibregl-ctrl-bottom-*` and `notifications-page__channel--needs_attention` stay (built at runtime).
- **Old vocabulary file.** `Shared/AppLegacyUiTypes.cs` is gone. The 25 enums in use now live in files named for what they are: `AppAppearanceTypes.cs` (variant, colour, size, text style, underline, severity, object fit, margin), `AppLayoutTypes.cs` (align, justify, position, anchor, placement, origin, max width), `AppFieldTypes.cs` (input type and mode, adornment, button type), `AppDialogTypes.cs`, `AppDrawerVariant.cs`, `AppTableCardsMode.cs`, `AppChartType.cs`. `AppBreakpoint` had no use and was deleted. No enum was renamed: none has an equivalent in `AppUiTypes.cs` with the same members (`AppButtonStyle` matches `AppVariant` in names but `AppVariant` styles text fields and chips, so merging them would change meaning); renaming 134 and 148 call sites was not worth the churn.

### Kept, with the reason

| Item | Why it stays |
|---|---|
| `Pages/DesignSystemPreview` | The proof page for the Look rules (a page, so the never-rendered check skips it; named in the allowlist). |
| `DevHarnessTab`, `EnrichmentTesterToolTab`, `ProviderTesterToolTab` | `Settings.razor` draws them by type name (`RenderInternalTool("…ProviderTesterToolTab")`) for a development build. |
| `EditorPopover`, `ViewEditorPopover`, `ViewSavedRibbon`, `OverviewTab`, `NsfwQuickCheck`, `VaultArea`, `VaultHiddenReminder`, `VaultProfileSettings`, `VaultSceneView`, `PwaInstallAction`, `SessionEndedRedirect`, `IngestionMediaCard`, `AlsoAppearsInPanel` | The audit's scripted search missed them: they are drawn by fully-qualified name (`<MediaEngine.Web.Components.Vault.VaultArea />`), by static members (`IngestionMediaCard.MediaIcon`) or from a namespace-qualified markup tag. They are rendered. |
| `/vault/scene/{id}` | Linked: `VaultViews.ScenePath` is the route of every Vault scene tile (`EngineVaultCatalogue`). |
| Query-string `replace: true` in browse, search, View all, Needs Review and the Libraries area chip | A page rewriting its own filter in its own address, not a forward (named in `NoRedirectsGuardrailTests`). |
| `ProfilesTab` (member opens their own profile), `ProfileDetailPage` (back to the list when the person is gone or not manageable), `Settings.razor` Kids/disabled-page gate and `/not-found` | Role-dependent landing, profile delete and access gates, not old addresses. Flagged for Shy: `ProfilesTab` is a landing forward by role. |
| `POST /account/security` in `DashboardAuthenticationEndpoints` | A form handler with no in-app caller left; not an address forward, so untouched. Candidate for a follow-up. |
| `style-ownership-baseline.json` (test fixture) | A finalized historical baseline; stale file names in it are harmless. |

### Guardrails added or tightened

- `NoRedirectsGuardrailTests` (new): no `@page` that only forwards; no `replace: true`, `Results.Redirect`, `Response.Redirect` outside a written allowlist (file and reason, stale entries fail); no file or method named `Legacy*Address`, `*Redirect*` or `*Alias*` outside a written allowlist; the retired forwards and helpers are absent.
- `LookRatchetGuardrailTests.EveryComponentIsRenderedByTheAppSomewhere` (was `EveryComponentIsUsedSomewhere`): it used to count a test, a comment or a document as a use, which is why thirty parts survived. It now counts only the app's own code, with comments removed.
- `LookRatchetGuardrailTests.NoStyleRuleTargetsAClassNothingUses`: same tightening (app code only), ceiling still 0.
- `DetailsOverlayGuardrailTests` replace allowlist: ForMe, Operations and LibrariesTab entries removed, Settings count 7 to 6.

### "Legacy" in the code (22 places)

| Where | What it is | Finish or delete |
|---|---|---|
| `wwwroot/app.css:47` | "Legacy layout rules" header | Delete the word when WP7/WP14 re-sections the sheet; no code change. |
| `wwwroot/app.css:152` | "legacy surface alignment for provider/settings panels and playback sheets" | Finish: fold the `!important` block into the parts' own sheets (WP7 ratchet) and delete the comment. |
| `wwwroot/app.css:204` | "Legacy Hybrid Paces + Metronic dark admin migration layer" | Delete: the migration is over; reword to what the block now does. |
| `wwwroot/app.css:782` | "maps legacy app variables to the canonical token file" | Delete with the last `--app-*` aliases (WP7 count 159). |
| `wwwroot/app.css:946` | "Migration-safe primitives… Legacy settings/dialog classes" | Delete the sentence; the classes it names are gone. |
| `wwwroot/app.css:2536` | "so legacy the shared UI layer overrides" | Delete (garbled comment). |
| `wwwroot/tuvima.tokens.css:5` | "legacy --app-* aliases in app.css" | Delete with the alias block (WP7). |
| `Models/ViewDTOs/LibraryDtos.cs:102` | "Legacy compact views still use these combined stages" | Finish: check the combined stages are still read; delete them and the note if not. |
| `Services/Editing/ArtworkPickerSession.cs:653` | `UsesLegacyPersonImage: false` | Finish: remove the always-false parameter. |
| `Services/Integration/EngineApiClient.cs:1143, 1164, 1192, 1331, 1333` | "legacy LastError-only" PUT shape | Finish: move the PUTs to the ProblemDetails shape, then delete the notes (Engine contract; separate packet). |
| `Services/Integration/ViewMediaEngineClient.cs:16` | "temporary legacy Engine media route behind the Dashboard proxy" | Finish: needs an Engine change (not in this packet); delete when the Engine route is replaced. |
| `Services/Playback/PlaybackSessionController.cs:2930` | "the legacy stream" route fallback | Finish: remove the fallback once no catalogue manifest lacks the resource route. |
| `Services/Playback/PlaybackSessionController.cs:3408-3411` | `legacyBookId` for restored book queues | Finish: decide with the playback owner whether restored old queues still exist (pre-release data may simply reset); rename `legacyBookId` to what it is. |
| `Services/Playback/PlaybackArtworkUrl.cs:5` | "restored legacy book queues" | Same decision as above; delete the phrase. |
| `Components/Artwork/ArtworkWorkspace.razor:58, 60, 109, 111, 205, 484` | `LegacyRoleUrl` / `LegacyRoleUrlFor`: the role image taken from the item itself | Finish: rename to `ItemRoleUrl` (no behaviour change). |
| `Components/Pages/ListenPage.razor.css:1680` | "overrides the legacy native-select chrome" | Delete the comment; the rule is the select's current look. |
| `Components/Shared/AppSelectItem.razor:21` | "Legacy child-content options have no text parameter" | Delete if no call site still passes child content; otherwise reword to "child-content options". |
| `Components/Details/DetailPage.razor.css:611` | "replaces its legacy…" navigation logic | Delete the word; the replacement is built. |
| `Components/Details/DetailPage.razor.css:844` | "Keep after every legacy density rule" | Delete with the density rules in WP7/WP14. |

## 4. Page walk (WP14), part A

Read-and-test walk (no screenshots; the Local check list covers those). Rules judged: D1 to D15.

- **Settings, Libraries** (`/settings/libraries`): Fixed: status filter is full height like the search box (was compact beside a normal search); Titles column right-aligned with tabular numerals (D15.4).
- **Settings, Playback / Reading and Listening** (`/settings/playback`, `/settings/reading`, `/settings/listening`): Pass (built on AppSettingsGroup and AppSettingRow in WP12).
- **Settings, Providers and plugins** (`/settings/plugins`): Pass (list uses the shared filter row); the Plugin file tab is owned by the Editor v5 build only where it embeds the editor panels.
- **Plugin page** (`/settings/plugins/{id}`, Set up): Fixed (#711): Test and Test again buttons are 44 px like the mock (were 36); "What it provides" tiles use the mock's neutral card with a coloured icon only (230 px columns, 10 px gap, 12 px muted sentence); the connection detail line is one line with an ellipsis and the cut-off tooltip (D15.6). Left: the "Official" pill beside the name and the Version row in the side column (data the page model does not give yet); the page title is 24 px, the mock 22 px (off the type ladder, so not hand-typed).
- **Plugin page, plugin-provided pages** (`/plugin/{id}/{key}`): Pass (no local colour, size or alignment rule).
- **Settings, Security / Access / Household** (`/settings/security`, `/settings/users`): Fixed (D15.6): the "Saved" line in the Kid settings and the household library ceiling is always present, on the same line, never wraps or moves a control. Left: none.
- **Settings, Needs Review** (`/settings/needs-review`, #694, #695): Fixed: rows sit on the 64 px media step of the row ladder on desktop; the page's own filter-row flex rule is removed (the shared AppFilterRow owns it). The filter row is already one line with hidden labels. Left: "Last tried" stays left-aligned text (relative words, not a number).
- **Settings, Recently Added** (`/settings/recently-added`) and **Recently added** (`/recently-added`): Fixed (#695): the type pills are the shared AppToggleChip (hand-typed white and purple rgba and glow removed), one centre line.
- **Settings, Activity** (`/settings/activity`) and **My activity** (`/my-activity`): Fixed (#695): From and To no longer draw labels above while the search box has none; the dates sit on the search row's centre line with the words From and To beside them and a "Dates" group name.
- **Settings, Recycle bin** (`/settings/recycle-bin`) and **Recycle bin** (`/recycle-bin`): Pass ("Showing: X" with its toggle is one line on one centre line).
- **Settings, Vault settings / System / Devices**: Fixed (Devices): the Household and Profile filter names sit beside their selects, not in small type above (D15.1, #695). Other panels Pass. Left: Local AI usage table now right-aligns Requests and Busy time (Fixed, D15.4).
- **Setup** (`/setup`): Fixed: the intro lead sentence is left-aligned (D15.4). Left: the animated intro stays centred as in the approved first-run mock.
- **Profile picker** (`/who`): Pass (centred choice tiles are the allowed exception).
- **My profile, Overview** (`/my-profile`): Fixed (#712): genre chips are capped at the card width with an ellipsis and a tooltip (shared AppChip rule in app.css, so every chip in the app now behaves the same).
- **My profile, Taste** (`/my-profile/taste`): Fixed (#712, #695): the legend label wraps to at most two lines with the tooltip; the legend column can no longer widen past its card; Key tastes chips are capped with an ellipsis; percentages right-aligned; the Scope filter name sits beside its select. Left: comma-joined provider genre strings are still one long label (splitting them is Engine logic, not done here).
- **My profile, Activity** (`/my-profile/activity`): Fixed (same page as My activity above).
- **For me** (`/for-me`): Pass (area buttons and Sort sit on one centre line; the Sort name is beside its select).
- **Downloads** (`/downloads`), **Companion** (`/companion`), **Vault** (`/vault`), **Vault scene** (`/vault/scene/{id}`), **Error** (`/Error`), **Not found** (`/not-found`): Pass (no local colour, size, icon or alignment rule found; the Companion position picker's centred cell labels are tile text).
- **Collections** (`/collections`): Pass (filter bar is the browse-shell surface, every select labelled, shared with part B). Left: the browse-shell filter surface is part B's; the editor window is the Editor v5 build's.
- **Search** (`/search`): Fixed: seven hand-typed colours in the page sheet moved to tokens. Left: the empty-state blocks stay centred (the shared empty-state pattern).
- **Design preview** (`/design-system/components`): Pass: shows the four window kinds, the settings row with the status slot, the Match row, the footer order, the number field and now a filter row; its tables use the numeric column class.
- **Window kind, Window (S/M/L) and Sheet**: Pass (Setup, Household and Needs Review dialogs use AppDialogOptions.Size).
- **Window kind, Pop-out Menu / Panel**: Pass (Activity filters use AppPopover).
- **Window kind, Viewer**: Pass (not touched).
- **Shared parts touched**: `.app-table__num` and `.app-numeric-field` (right-aligned, tabular numerals, D15.4); `.app-chip` (max width and ellipsis); `Common_Dates` string. Ratchets lowered: hand-typed hex 853 to 852, forced overrides 1301 to 1293, centred text 12 to 11 (eight dead `!important` rules in Settings.razor.css removed with the unused danger-card and reading-preview rules).

## 4. Page walk (WP14), part B

Read against D1 to D15 from the code, the page styles and the guardrail tests (no Engine run; screenshots are a Local check).

- **Home / Library browse** (`/`): Fixed: breakpoint 720 moved to the 640 step, hand-typed hex in tile, shelf and hero styles moved to tokens, hero Play already the shared HeroActionRow | Left: the first-run empty state is centered (a lone empty state, not body copy); phone hero card keeps a local layout block.
- **Watch browse** (`/watch`): Fixed: the shared browse shell's filter row is one line on one centre line, "Browse as", "Filter by", "Sort" and "Display" names are hidden (screen reader only) and shown only in the phone filter sheet (bug 695), sort select named for screen readers, 18 hex colors to tokens | Left: the shell still has 20 `!important` and about 30 rgba values (shared with Read and Listen).
- **Read** (`/read`): Fixed: same shell fixes as Watch | Left: same as Watch.
- **Listen browse** (`/listen`): Fixed: same shell fixes | Left: same as Watch.
- **Listen** (`/listen/{Tab}`, playlists: albums, artists, songs, audiobooks): Fixed: album and audiobook filter rows are one line on one centre line with no small names above the selects (bug 695), Play / Shuffle / Back / Save buttons are AppButton, row Queue / Edit / Remove are AppIconButton (Plain, Remove in the error tone), all page-local `listen-button` and `listen-icon-button` rules deleted (and their dead touch-floor entries in app.css), 15 hex colors to tokens | Left: about 90 rgba values and the rail, tracks table and audiobook cards keep page-local styling (needs a second pass, no mock to compare).
- **My library** (`/my-library`): Pass (no filter row, no hand-typed colors, sizes or icon rules).
- **Shelf grid** (`/all/{Lane}/{ShelfKey}`): Fixed: the Sort and filter selects sit on one centre line and carry no label above them (labels return inside the phone sheet) (bug 695).
- **Unified detail: movie, show, season, episode, book, album, person** (`/details/{EntityType}/{Id}`): Fixed: hero and tab styles, primary module, credits, Extras and Contents use tokens instead of 90 hand-typed hex colors (gold maps to the collection accent, purple to the accent tokens), album and audiobook track rows use AppIconButton (Like is Filled + Soft), page-local `tl-audio-item-table__icon-button` rules deleted | Left: DetailHero and DetailPage keep 43 `!important` and about 70 rgba values; the editor window and its tabs are owned by Editor v5.
- **Universe** (`/universe/{qid}`): Pass (tokens only in the page style); Fixed in its parts: tree and cast comparison hex colors to tokens, five off-ladder font sizes to the ladder.
- **Universe thing** (`/universe/{Qid}/things/{ThingId}`): Fixed: seven off-ladder font sizes to the ladder, grey hex colors in the tree, path finder and family views to tokens.
- **Chronicle explorer** (`/universe/{Qid}/explore`): Fixed: empty-state panel and muted text use tokens | Left: empty state is centered (empty state, not body copy).
- **View** (`/view`): Fixed: 8 hex colors to tokens; the filter bar is the shared AppDiscoveryFilterBar (one line, no labels).
- **View artwork**, **View contributions**: Fixed: hex to tokens, 720 breakpoint to 640 | Left: none.
- **View document rules**, **View documents**: Pass for filter rows (the document filter rail is a vertical form rail: a label above each field is the D15.2 shape, every group has one) | Left: the rail's styles live in app.css and still use `--app-control-*` variables (WP7 count).
- **View galleries**, **View gallery detail**, **View people**: Fixed: hex to tokens where present; people search is a single labelled-by-placeholder field.
- **View places**: Pass for filter row (search, time, type and Filters on one grid row, centre-aligned, no labels); the map and places styles are on the colour allowlist (colour is data) | Left: two icon font sizes typed by hand (1.2rem, 1.05rem).
- **View photo timeline / immersive viewer / selection toolbar** (parts of View): Fixed: 21 hex colors to tokens, one off-ladder font size.
- **EPUB reader** (`/read/{AssetId}`): Pass (page is a thin host for the foliate reader; no local rules in the page).
- **Extra viewer** (`/extras/{ExtraId}`): Fixed: white document backdrop, video backdrop and focus ring use tokens | Left: centered "state" block (empty/loading state).
- **Watch player** (`/watch/player/{WorkId}`): owned by Player v2, consumes Look v3; not edited. The page style is 5 lines and clean. Rules its parts break: VideoPlaybackHost keeps 5 hand-typed hex colors and 7 rgba values, and still has page-local button rules (on the LookV3IconButtonTests pending list), as does PlaybackControlStrip.
- **Listen pop-up player** (`/listen/player-popup`): owned by Player v2, consumes Look v3; not edited. The page style is 4 lines and clean. Rules its parts break: ListenNowPlayingBar keeps page-local icon-button rules (on the pending list) and two 720/640 px breakpoints.

Window kinds touched: none changed. The Browse filter sheet (phone bottom sheet) keeps its group names visible as section headings; its shared layout block in app.css is part A's.
