# Plan: First-run ingestion fixes (2026-10-10)

Source: a fully wiped first-run import of `C:\Temp\real_import` (508 catalogue files) on 2026-10-10, plus the Product Owner's walkthrough of the result. Each bug below has a confirmed root cause with code anchors. No code has been changed yet.

Status: **approved 2026-10-10 — in progress.** Split out: multi-episode files, profiles/setup redesign.

---

## 1. Product walkthrough: what changes for you

### 1.1 First run and setup
- After setup finishes, every configured provider is tested automatically. Settings shows "Checking…" and then a real status, never "Unknown".
- If a provider goes down, it is re-tested every few minutes and recovers on its own. Today a provider marked "Down" stays down forever, even across restarts.
- The "Secure account" banner can be dismissed. After that it lives in Settings → Security and in the account menu.
- Adding a profile is reachable from the account menu ("Manage profiles") and from Settings → Profile, not only through the hidden "⋯" menu in Users & Access.
- The account menu no longer shows your name twice.
- Settings never flashes the wrong page ("User", Personal-only menu) while it loads.

### 1.2 Watching an import
- The progress bar reflects the whole journey (found → read → matched → enriched), so it no longer jumps to nearly done straight away. Beside it: "312 of 508 items ready · Matching with providers · about 6 min left".
- The Live Ingestion page is cleaner and denser: one compact status header, then a single list of what's being added now, with less padding and fewer boxes.
- Recently Added fills in as each item finishes, instead of staying empty until the whole import ends.
- "Scan now" becomes "Check folders for changes", moves into the page's "⋯" menu, and has a one-line explanation. Folders are already watched automatically; this is the manual re-check for network drives or changes made while the Engine was off.
- The top bar always shows one activity icon, in the same outline style as Search and My List. It shows a subtle progress ring while something is being added. The bar no longer has an awkward gap or shifts when the icon appears.
- In the library (Home, Read, Watch, Listen), an item still being matched or enriched shows a small, quiet marker on its tile caption (a soft dot, with the tooltip "Still being matched"). It disappears when the item is settled.

### 1.3 Matching quality
- Books match on Apple Books, by ISBN and by title and author.
- Audiobooks match on Apple Books. A file language written as "English", "eng" or "en-US" no longer breaks the request.
- TV shows get TVDB data on a fresh data store (Solo Leveling and every other show).
- A file in the Movies library with no TMDB movie match is also looked up in TMDB's TV catalogue (miniseries, web series, TV film, or a single TV episode/special). It is **not accepted automatically**. It goes to the Review Queue as an explicit suggestion, for example: *"TMDB lists this as the miniseries 'Dr. Horrible's Sing-Along Blog' (2008, 3 parts). Use it for this film?"*, with **Move to TV** / **Search again** / **Keep as unmatched film**. Until you decide, the item keeps its file details in Movies. *(Approved 2026-10-10; review-first and move-to-TV added on the same date.)*
  - **Move to TV** makes it a TV item in Tuvima's TV library (media type TV, library = TV), applies the TMDB/TVDB match, and shows it under Watch → TV. **The file is never moved on disk**; it stays exactly where it is (your libraries are read-only). It leaves Movies.
  - **For now, a file covering a whole miniseries is attached as Season 0 (Specials), episode 1** of that show; the real episodes show as not owned. Proper "one file covers several episodes" support is a separate plan: `engineering/plans/multi-episode-files-2026-10-10.md`. A single-episode match attaches to that exact episode.
- Titles keep abbreviations: "Dr. Horrible", not "Dr  Horrible".
- The same book in two formats in one folder (EPUB + AZW3) becomes one Work with two Editions, not two separate entries.
- A music track keeps the album it is tagged with ("90's Rock Ballads", "City of Angels"). It is no longer filed under whichever MusicBrainz release happens to be earliest ("VH1 Storytellers"). Albums that share a name no longer produce dead "Detail page not found" links.
- A folder `cover.jpg`, `cover.png` or `folder.jpg` is used as artwork for audiobooks, books and music when the file has no embedded cover (comics keep the cover inside the archive; PO decision 2026-10-10). Artemis gets its cover even with no provider.
- Multi-file audiobooks no longer fail to save their cover because several tracks try to write it at once.

### 1.4 Browsing and details
- Home no longer tucks its top under the menu bar before the hero picture has loaded.
- The mouse wheel scrolls every Settings page. The Settings menu stays fixed while the content scrolls.
- Opening any item (album, movie, book) from Home starts at the top of its details page. Pressing Back returns you to where you were on Home.
- A standalone movie (Lamborghini) no longer shows a "series" panel containing only itself.
- Movie and TV hover cards stay portrait until real landscape art exists.
- Settings toggles look right: the knob sits inside the track, ON is coloured, OFF is neutral, and all switches share one design.

### 1.5 My List
| Place | Not in My List | In My List |
|---|---|---|
| Details page button | **+** "Add to My List" | **✓** "In My List" (plain tick, same round button) |
| Tile hover action | **+** | **✓** |
| Top bar (opens My List) | list-with-tick icon (`PlaylistAddCheck`, outlined) | same |

The bookmark shape stays reserved for real bookmarks, and the planet icon is removed. *(Approved 2026-10-10.)*

### 1.6 What stays the same
- Original media files are still never modified; libraries stay read-only.
- No change to the Priority Cascade, Review Queue rules, routes, or the editor workflow.
- No new tools or libraries.

### 1.7 Decisions I've assumed (change any before go-ahead)
- **A. Book formats:** files in the same folder with the same normalised title and author but different formats (EPUB, AZW3, PDF, MOBI) become one Work with one Edition per format. This happens before provider matching.
- **B. Music albums:** the file's album tag wins when a MusicBrainz release with a matching title contains the recording. With no matching release, the track stays on a local album named after its tag instead of adopting an unrelated release.
- **C. Netflix/Disney+-style setup and profiles (your design item D4):** split into its own plan, `engineering/plans/profiles-setup-redesign-2026-10-10.md`. It is a substantial redesign (a "Who's watching?" screen, an add-profile tile, an avatar picker, simplified setup steps) and deserves its own session. This plan covers only the quick wins: the profiles shortcut, the name shown once, and the dismissable banner.
- **D. "Still being matched" marker:** a 6 px soft dot before the tile caption title, with a gentle pulse that is off when your system asks for reduced motion, plus a tooltip. No overlay on the artwork.

---

## 2. Acceptance criteria (verified by a second wiped first-run import of `C:\Temp\real_import`)

| # | Observable result | Work |
|---|---|---|
| AC1 | `metadata_providers` contains every `WellKnownProviders` id on a fresh data store; Solo Leveling episodes carry a `tvdb_id` and a QID or a TVDB-sourced cover | WP1 |
| AC2 | A retail persistence failure is logged and the job retries; it is never reported as "Retail match found" | WP1 |
| AC3 | Books (Artemis, Project Hail Mary, The Martian, Winners Take All, Regretting You) retail-match on Apple | WP2 |
| AC4 | Audiobooks with language "English" or "eng" produce `lang=en_us`; no Apple 400s in the log | WP2 |
| AC5 | A provider HTTP failure leaves the job queued for retry ("Waiting for provider"), never `RetailNoMatch` | WP3 |
| AC6 | A provider marked Down is re-probed using its configured endpoint and recovers without a restart (tested with a fake endpoint) | WP3 |
| AC7 | After setup completes and at Engine start, every enabled provider has a connection-check row; Settings shows no "Unknown" | WP3 |
| AC8 | Artemis audiobook shows its folder `cover.jpg`; no "Failed to persist embedded cover art" lines | WP4 |
| AC9 | Winners Take All is one Work with two Editions; Home and Read link to the same details page | WP5 |
| AC10 | "Uninvited" tracks sit on "90's Rock Ballads" and "City of Angels"; every album tile on Home opens a details page (no 404) | WP5 |
| AC11 | Dr. Horrible lands in the Review Queue with reason "Found as a TV title" and a suggestion card naming TMDB tv/5739 (Miniseries, 2008, 3 parts). **Move to TV** turns it into a TV item in the TV library: the show "Dr. Horrible's Sing-Along Blog" appears under Watch → TV with TMDB/TVDB artwork, the file is attached as Season 0 Episode 1 (Specials). It is gone from Movies, the review item clears, and the source file is untouched (source snapshot unchanged). **Search again** opens the existing editor search. **Keep as unmatched film** dismisses. Title reads "Dr. Horrible's Sing-Along Blog". A Movies item matched as a single TV episode gets the same treatment and attaches to that episode | WP6 |
| AC12 | Lamborghini details shows no series panel; series books still show theirs | WP7 |
| AC13 | Opening a details page from scrolled Home lands at the top; Back restores the Home position | WP7 |
| AC14 | Mouse wheel scrolls all Settings pages; the Settings menu stays fixed | WP8 |
| AC15 | Home with no hero: first shelf fully visible below the top bar | WP8 |
| AC16 | Movie/TV hover cards without landscape art keep a portrait hover | WP8 |
| AC17 | Toggle ON is coloured and OFF is neutral; the knob sits inside the track in both states (measured) | WP8 |
| AC18 | Account menu shows the account (email) in its header and the profile list only when there is more than one profile; "Manage profiles" is reachable from the menu | WP8 |
| AC19 | Top bar: a fixed-width activity icon; equal spacing between icons idle and busy; My List uses the list-with-tick icon | WP8 |
| AC20 | Details page My List button shows + / ✓ | WP8 |
| AC21 | Settings loads with a neutral skeleton, never the wrong page, name or menu | WP8 |
| AC22 | The progress bar's percentage tracks the whole pipeline (within 10 % of items fully settled ÷ total during the run) | WP9 |
| AC23 | Recently Added lists finished items while the import is still running | WP9 |
| AC24 | Live Ingestion refreshed layout; "Check folders for changes" in the ⋯ menu with an explanation | WP9 |
| AC25 | Tiles of unsettled items show the marker; it clears when the item settles; respects reduced motion | WP10 |
| AC26 | "Secure account" banner can be dismissed (per account) and reappears as a card in Settings → Security | WP8 |
| AC27 | `subtitle_languages` is stored in `canonical_value_arrays`; no packed `|` values in `canonical_values` | WP11 |
| AC28 | 0 errors, 0 warnings, all tests passing; docs build passes | WP12 |

---

## 3. Root causes (for the implementers)

The full diagnosis with anchors is kept in the session bug list. Summary:

- **B1** Home: `MainLayout.razor:240` cinematic shell + `LibraryBrowsePage.razor.css:27` margin -1rem; `--with-hero` keyed on `_page.Hero` while the carousel uses `HomeHeroCandidates`.
- **B2** Settings is not in `MainLayout.IsLaneScrollRoute` (`:238`). `MediaSectionShell` grows to content height, and its inner `main` (`overscroll-behavior: contain`) swallows the wheel.
- **B3** Two Works for EPUB + AZW3 (no format-sibling grouping; neither matched retail).
- **B4** `ConfigDrivenAdapter.ResultSelection.cs:45-52` and `IsEditionCompatible :204-212`: Apple Books results require an `isbn` key that Apple never returns, so every result is rejected, even `isbn_lookup` hits.
- **B5** `NormalizeLocalePart` (`LocaleFallback.cs:170`) gives `english` / `eng` / `pt`, and Apple answers 400. `FetchAsync` swallows HTTP errors, so the worker records `RetailNoMatch`.
- **B6** No cover from B4/B5, plus a concurrent `WriteAllBytesAsync` of one work cover by many tracks (`IngestionEngine.Pipeline.cs:920`).
- **B7** `MediaTile.razor:587` forces `is-banner-popover` for Movie/TV, ignoring the resolver's `HoverLayout`.
- **B8** Shared scroller `.context-sidebar-shell__main` is not reset on navigation. `detailOrigin.fresh` (`app.js:3250`) only runs for playback navigation, and `capture` records the non-scrolling element.
- **B9** MusicBrainz release selection (earliest Official) ignores the album tag (`musicbrainz.json:262-281`). The detail view groups albums by `lower(name)` with `MIN(RootWorkId)` (`CollectionBrowseReadService.cs:762-776`) while tiles link to per-work roots (`DisplayWorkProjectionReader.cs:59-62`), so same-named albums 404 (`DetailCompositionOrchestrator.Collections.cs:761-773`).
- **B10** `SchemaMigrator.cs:~1405` seed omits `Tvdb` and `Pseudonym`. TVDB claims hit an FK failure, yet the worker still logs "Retail match found".
- **B11/B13** Profiles only reachable through Users & Access ⋯. `TopNavAccountMenu.razor:37-44` and `:88` show the name twice.
- **B12** `Settings.razor.css:509-595` resizes the track but not the thumb geometry from `native-fields.css:46-53`. The ON rule targets removed MudBlazor classes (`.tl-switch-base.tl-checked`).
- **B14** Provider status comes only from `provider_health` (side effect of real lookups) or a manual test (`SettingsEndpoints.cs:547-578`); nothing checks automatically.
- **B15** `ProviderHealthMonitorService` probes `HttpClient.BaseAddress`, which no provider client sets. The probe is skipped, `IsDown` blocks real requests, and Down is permanent (persisted).
- **B16** TMDB has Dr. Horrible only as TV (tv/5739, Miniseries); Wikidata Q842253 P31 = web series; the Movies pipeline searches `/search/movie` only. Title cleaner splits on "." in "Dr.".
- **B17** `ResolveLocalSequenceContainerOptionAsync` falls back to the work's own title as a series container.
- **B18** Folder cover images are never ingested (only moved by `AutoOrganizeService.cs:474`).
- **B19** `BuildOverallProgress` (`IngestionLiveDashboardState.Projection.cs:880-920`) only sums stages whose totals are already known.
- **B20** `LoadHistoryGroupKeysAsync` filters `WHERE NOT IsActive` (`IngestionPresentationReadService.cs:~784`).
- **B21** The Settings first render precedes session/permission resolution.
- **B22** The activity slot is always rendered but empty when idle (`MainLayout.razor:104`, `SystemActivityIndicator.razor:8`).
- **Side:** `subtitle_languages` is packed with `|` in `canonical_values` (guardrail violation). `core.json` `data_root` still points at the old harness folder.

---

## 4. Work packages, agents and sequence

Model routing follows CLAUDE.md §5. Opus (main session) writes each brief from this plan, reviews every diff, and does the final visual pass. At most 2 helpers run in parallel, and only on disjoint files.

| WP | Scope (bugs) | Agent / model / effort | Files (primary) | Tests to add | Depends on |
|---|---|---|---|---|---|
| **WP1** | B10 seed + guardrail; AC2 persistence failure ≠ success | implementer · Sonnet · **high** | `Storage/SchemaMigrator.cs`, `Providers/Workers/Internals/RetailMatchWorker.JobProcessing.cs` | Storage: seed equals `WellKnownProviders` (reflection guardrail); Providers: persistence failure → retry state | none |
| **WP2** | B4 Apple ISBN gate; B5 language normalisation (names, ISO-639-2, regions → 2-letter; Apple `lang` = storefront-supported `en_us`) | implementer · Sonnet · **high** | `Providers/Adapters/Internals/ConfigDrivenAdapter.ResultSelection.cs`, `…LocaleFallback.cs`, `…ClaimExtraction.cs`, `config/providers/apple_api.json` (if needed) | Adapter tests with recorded Apple payloads (no isbn key); language-mapping theory tests | WP1 |
| **WP3** | B5b HTTP failure → provider failure (retry, not no-match; 4xx caused by our request ≠ provider down); B15 probe via configured endpoint + `next_check_at`; B14 connection checks after setup + at start | implementer · Sonnet · **high** | `ConfigDrivenAdapter.cs` (FetchAsync), `Api/Services/ProviderHealthMonitorService.cs`, `Api/Services/ProviderCredentialService.cs`, setup-completion hook, `RetailMatchWorker.JobProcessing.cs` (failure path only) | Health monitor recovery test with fake handler; worker retry classification; startup check | WP2 (shares adapter file) |
| **WP4** | B18 folder cover source; B6 cover write race (per-work lock or write-once) | implementer · Sonnet · **high** | `Ingestion/IngestionEngine.Pipeline.cs`, processors' cover extraction, artwork source ranking | Folder-cover precedence; concurrent writers produce one cover, no IOException | none · **parallel with WP2** |
| **WP5** | B3 format siblings → one Work; B9 album tag-aware release selection; B9 album detail grouping by root id | implementer · Sonnet · **high** (identity pipeline) | Ingestion work/edition assignment, `MusicBrainzReleaseClient.cs`, `RetailMatchWorker.MusicManifest.cs`, `CollectionBrowseReadService.cs`, `DetailCompositionOrchestrator.Collections.cs` | Sibling grouping; compilation track keeps tagged album; two same-named albums both resolve | WP3 |
| **WP6** | B16 Movies → TMDB TV fallback (miniseries/web series/TV film/single episode or special, strong title+year), **never auto-accepted**: new review trigger `MovieMatchedAsTv` (`Domain/Constants/ReviewTrigger.cs`) with the candidate stored as the suggestion; Review Queue suggestion card (Move to TV / Search again / Keep as unmatched film) reusing the existing review dialog and `SharedMediaEditorShell` search; **Move to TV** = reassign media type + library to TV through an explicit aggregate method (logical only; the file is never moved), attach as S00E01 of the show (whole-series file) or the exact episode, re-run TV identity (TMDB/TVDB → Wikidata TV classes); title abbreviation cleaning | implementer · Sonnet · **high** (identity pipeline) | `config/providers/tmdb.json` (fallback strategy), adapter strategy gating, `RetailMatchWorker` outcome path + outcome factory, `ReviewTrigger.cs` + palette/labels, Review Queue suggestion UI, media-type/library reassignment (Domain aggregate method + repository), title cleaner | Fallback only when movie search is empty and type is allowed; fallback hit → review item (not RetailMatched); Move to TV leaves the source file untouched and the item appears as S00E01; "Dr." preserved | WP3, WP5 |
| **WP7** | B17 no self-series; B8 scroll reset + capture of the real scroller | implementer · Sonnet · medium | `DetailCompositionOrchestrator.SequencePlacement.cs`, `wwwroot/app.js` (detailOrigin), `Shared/MainLayout.razor` (navigation hook) | Standalone work has no placement; series still does | none · **parallel with WP5/WP6** |
| **WP8** | Dashboard shell: B1, B2, B7, B12, B13+B11 shortcut, B21, B22 static activity icon, D7 My List icons (Engine action icon `check`), D5 dismissable banner | implementer · Sonnet · medium | `MainLayout.razor(.css)`, `LibraryBrowsePage.razor(.css)`, `MediaTile.razor`, `Settings.razor.css`, `native-fields.css`, `TopNavAccountMenu.razor`, `SystemActivityIndicator.razor`, `HeroActionRow.razor`, `DetailCompositionOrchestrator.ViewModelBuilder.cs:706`, banner component + Settings Security card | bUnit/guardrail tests where they exist (account menu, switch markup) | WP7 (both touch MainLayout) |
| **WP9** | B19 progress model; B20 per-item recently added; D1 Live Ingestion visual refresh; D3 rename/move "Scan now" | implementer · Sonnet · medium (high for the B20 query) | `IngestionLiveDashboardState.Projection.cs`, `IngestionLiveDashboard.razor(.cs/.css)`, `IngestionPresentationReadService.cs`, `IngestionTasksTab.razor` | Progress projection tests; recent-additions includes settled items of an active batch | WP8 |
| **WP10** | D2 "still being matched" tile marker (Engine exposes a settled/pending flag on tile/display contracts → Dashboard renders the dot) | implementer · Sonnet · **high** (Contracts/wire) | `Contracts/Display/*`, display composer/projection, `MediaTile.razor(.css)`, wire fixtures | `WireContractSnapshotTests` + boundary tests updated; tile renders marker only when pending | WP9 |
| **WP11** | Side: `subtitle_languages` → `canonical_value_arrays` (startup migration owned by `SchemaMigrator`); `core.json` `data_root` tidy | implementer · Sonnet · **high** (data store); config edit by Haiku | `SchemaMigrator.cs`, the video processor writer, readers of `subtitle_languages`; `config/core.json` | Guardrail: no packed `|` in canonical_values for array keys | WP1 |
| **WP12** | Docs: `presentation-rules.md` (details series panel, My List icons, tile marker, hover rule, Settings scroll), `providers.md` + `configuring-providers.md` (Apple language, TMDB TV fallback, folder covers, health recovery), `configuration.md` (any new config keys), `media-types.md` (folder cover sidecars), Review Queue explanation page + `glossary.md` (new "Found as a TV title" review reason) | implementer · **Haiku** (mechanical, from a brief listing exact edits) | `docs/…` | `scripts/docs/build-docs.ps1` | all |

**Verification per package:** a `verifier` (Haiku) runs the targeted test project after each WP and returns a short pass/fail triage.

**Review:** a `reviewer` (Opus, high) reviews WP3+WP5 together (identity pipeline, provider health), and separately WP10 (wire contract) and WP11 (data store) before the final run.

**Visual proof (Opus):** text-based page reads and computed styles while iterating, then one final screenshot set for B1, B2, B7, B8, B12, B13, B22, D1, D2 and D7.

**Final run:**
1. Stop the Engine and Dashboard; the verifier runs `dotnet restore` / `build` / `test` on the full solution.
2. Wipe `C:\temp\tuvima-library\.data` and run a fresh first-run import of `C:\Temp\real_import`.
3. Check AC1–AC27 against the log and data store.
4. Release `dotnet.exe` locks.
5. Commit and push (specific files only).

**Estimated effort:** 12 packages, mostly sequential with two parallel pairs (WP2 ∥ WP4, WP6 ∥ WP7); order WP1 → WP2∥WP4 → WP3 → WP5 → WP6∥WP7 → WP8 → WP9 → WP10 → WP11 → WP12. Fable 5.1 escalation only with your approval, if WP5 (identity grouping) or WP11 (data store change) fails twice.

---

## 5. Trade-offs and risks
- **WP5 identity grouping** changes how Works form. Wrong grouping could merge two genuinely different books that share a folder; guarded by an exact normalised title + author match and different formats only.
- **WP6 TV fallback** could suggest a real TV series for a film file. That's harmless, because nothing is applied without your confirmation in the Review Queue; the suggestion is limited by type, strong title + year, and only when movie search is empty. Cost: one extra review item per film-like series in your library. Files are never moved on disk by this feature.
- **Interim special:** until the multi-episode plan lands, Dr. Horrible shows as a special with its 3 real episodes listed as not owned.
- **WP3** changes what counts as "Down": request errors (400s) no longer take a provider offline, so a misconfigured strategy would retry rather than block. Retries are capped by the existing backoff.
- **WP2** Apple language: Apple's search only accepts a few `lang` values. We'll send `en_us` (or `ja_jp`) and rely on the country storefront for regional results.
- **Pre-release:** WP11 migrates existing rows idempotently; no compatibility shims needed.
- **D4** (Netflix-style profiles/setup) is deliberately left out; it gets its own plan with mockups.

## 6. Plain English summary
The first real import exposed 22 faults. A handful of them cause most of the damage. Apple's book results are all thrown away; audiobook searches send a language code Apple refuses; TVDB was left off the list a new library starts with; and any provider marked "down" never comes back. Together these are why books, audiobooks and TV shows came in without matches or covers. The rest are visible polish problems: the scroll position, toggles, the top bar, the progress bar, Recently Added, and the "series" panel on a single movie. This plan fixes all of them in 12 ordered pieces. Cheaper helper models do the building and testing, and the expensive model plans, reviews and checks the final screens. It finishes with a second fresh import to prove every point above.
