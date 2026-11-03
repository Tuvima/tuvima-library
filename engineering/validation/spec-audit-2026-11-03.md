# Spec audit: what the specs say versus what is on main (2026-11-03)

Checked against main at 74d998c. This was done by reading the code, tests, styles and mockups; nothing was built or run, so "delivered" means the piece exists, not that it looks right on screen. Shy's 3 Nov check notes (`local-check-2026-11-03.md`, items 1 to 20) are on SHY-DESKTOP and not yet in the repository; their content was read from the review thread. The 2 Nov list is `local-check-2026-11-02.md`.

## The short answer to "what happened to all those changes?"

Three different things happened. Most of what you saw missing falls into the first two, and only a few items are true build misses.

1. **Never put into any spec (about half).** Your 2 Nov review items 14, 15 and 36 (Activity page, folder count with paths on click, Edit that adds folders, filter chips) were never turned into a spec or work packet. The "first-run-setup-rework" spec explicitly said "Settings keeps its table for now; aligning is a follow-up", and nobody picked the follow-up up. So the build threads did what the specs said, and the specs did not say this.
2. **The spec said the opposite of what you now want.** Libraries redesign decision D2 (Locked) says one table grouped by area with "nothing to toggle", so filter chips contradict it. The setup rework spec D10 says metadata tiles are left-aligned, never centred. Region on setup page 2 is kept by the spec and its mockup. The Home spec dropped the trailer button and kept one "Mark as watched" in the "..." menu, against your 2 Nov note.
3. **Built, but not to the standard of the picture (real misses).** The folder picker still checks for overlaps while you browse, has no Go button, is capped at 760 px wide, and puts the name at the bottom. The pill look has a token and a guardrail covering only four files, while about 80 places still use fully rounded corners. The Metadata page lost its provider tiles to Plugins in v3, so "square, aligned tiles with the key-review page" has no home on that page.

Your statuses ("Built") were also too generous: they described the packets that were written, not your later requests.

## Gap report by feature

Legend: Delivered / Partly / Missing, with evidence.

### 1. Settings Libraries page (libraries-redesign, library-pages)
- Two-pane Libraries with honest status counts: **Delivered** (`Settings/Libraries/LibrariesRail`, `LibrariesOverview`, `LibraryStatusCell`).
- Folder count: **Partly**. Shows "N folders · M titles" as plain text (`LibrariesOverview.razor`); clicking it does nothing.
- Paths on click: **Missing, in no spec.**
- Edit that adds and removes folders: **Missing, in no spec.** Folder add/remove lives on the library's own Folders tab; the Edit dialog changes the name only.
- Filter chips per area (Watch, Listen, Read, View) and area colours: **Missing, and contradicts decision D2.** Only the library-kind tile has a colour.
- Remove "Change what this folder is": the Unidentified tab it lived on was removed on 3 Nov, so check whether it survives elsewhere.
- Advanced removal, rules card, household on/off access: **Delivered.**
- Stale: WP3 (Unidentified tab) is superseded by Needs Review; WP7 (shared setup table) was replaced by a separate setup table.

### 2. Add library, folder picker (libraries-redesign WP5/WP6, first-run-setup-rework WP5)
- Drives and media locations list, path box, "Folder accessible" line, post-add check (Looks right / Heads up / Already used): **Delivered.**
- Go button: **Missing** (Enter only). Wide layout: **Missing** (760 px cap, `ServerFolderPicker.razor.css`). Library name at top: **Missing** (it sits in the footer; the mockup also puts it there).
- Check only after you choose: **Missing.** `ServerFolderPicker.razor` validates on every folder load and highlight, so the "overlaps Movies" error appears while browsing. This is the one that most directly breaks decision D10 and your 2 Nov item 10.
- Speed: **Partly.** Names-first listing with a time limit exists in the Engine; the Dashboard still validates on each step.
- Your image of the new picker was saved on 3 Nov but is not in any spec yet.

### 3. First-run setup (first-run-setup-rework)
- Vault before Libraries, welcome page with animation, splash, tools chips, All set warning: **Delivered** (`SetupContracts.cs` order and test; `SetupIntroStage`, `IntroAnimation`).
- Box around the animation, uncoloured icons, stray green, empty-Libraries spacing, off-centre pop-up after Next: **new findings from 3 Nov**, not in the spec.
- Region on page 2: **kept by the spec and mockup**; remove only if Country covers it (check what Region drives first).
- Settings Libraries not aligned with the setup table: **acknowledged deferral, never scheduled.**
- View "Photos and Videos" as one choice, and the managed Shared photos model (item 14): **Missing; needs a Fable review and a spec.**
- Vault libraries as their own add area (item 5): contradicts "one Libraries page"; **spec change needed.**

### 4. Metadata providers and the key-review page (first-run-setup-rework WP6, plugins v5)
- Keys-needed tiles first, equal tiles: **Delivered in setup only** (`SetupMetadataStage`). Odd-count tile centred: **not built, by design** (D10 says left-aligned).
- Key dialog with several fields: **Delivered as a dialog** (`ProviderOnboardingDialog`). It says "Test and save" as one button, not Test then Save; the spec accepted that.
- The Settings Metadata page is now only the Ingestion Flow; providers moved to Plugins. So the page you opened was **never in any spec's scope** for the new look. Plugins v5 covers the Plugins tiles (gear, 22 px tick, generated pages): **Delivered** (WP22 to WP31, `PluginPageGeneratorGuardrailTests`).

### 5. Activity page and navbar icon (nothing)
- **No spec, no mockup.** Navbar icon always goes to Libraries (`SystemActivityIndicator.razor:71`): **Missing.** "..." still there instead of a direct link: **Missing.** Artwork hover preview: **Missing.** Per-file stage progress bar: **Partly** (on the library Activity tab only). Navbar and page agreeing: **Partly.**

### 6. Pills and look (home-detail-and-tiles, one-shared-look)
- Square pill token (3 px), no hover growth, bolder hover ring, tests for four tile files: **Delivered.**
- App-wide square pills: **Missing.** About 80 uses of fully rounded corners remain (e.g. `PluginSettingsTab.razor.css:102`); no app-wide guardrail. Some are rightly round (avatars, switches, progress), so it needs a visual pass.
- "Aligned pills": **in no spec.**
- One colour scheme for icons and buttons, and matching the purple of the intro animation (3 Nov items 1 to 3): **not in any spec.**
- one-shared-look: shared tabs, cards, search row, summary strip and pager exist. Its "no page defines its own tab style" guardrail was not found.

### 7. Home, detail pages, tiles (home-detail-and-tiles)
- Bigger portrait tiles, landscape Continue tiles with logo or title, hero rotation across Watch/Read/Listen, album collage slide, Restart in "..." menu, detail stack, 88 px people: **Delivered** (`ContinueLandscapeTile`, `HomeSpotlightStateTests`, `HeroActionRow`).
- Conflict to decide: spec keeps one "Mark as watched" in "..." and drops the trailer button; your 2 Nov list wanted watched/unwatched gone and a trailer button. Download moved to the native pass.
- Not found: the "kind filter only when a person has several kinds of works" and the log of why a Watch hero candidate was skipped.

### 8. Players (player-controls-v3, video-player-controls-and-bookmarks, global-bookmarks, music-player-and-queue)
- Settings gear menu, vertical volume, chapter ticks off by default, B shortcut gone, per-type control sets, bookmark filters with Promote: **Delivered.** No dedicated tests for the settings menu or volume control. Diagnosis of private bookmarks not saving: **Partly.** PDF/comic bookmarks: parked by decision.
- Music spec packets: all **Delivered.**

### 9. Editor (universal-editor-v3 and v3.1)
- Pop-over host, family tree, list entry points, TV match browser, Needs Review merge, video series and order: **Delivered.** WP5 "retire old surfaces" is **Partly**: `EditingModeContext`, `ArtworkPickerSession` and `EditSessionFlow` still exist for Universe and Chronicle. WP8 screenshots at 1440/1280: **Missing.**

### 10. TV, Universe, links, videos and the rest
- tv-artwork-cast-and-matching: WP1, 2, 4, 5 **Delivered**; WP3 cast kept unproven; WP6 fixtures **Missing**; live checks open. Header says WP1/4/5 only: it is ahead of itself.
- universe: U-WP0 to U-WP14 **Delivered**; U-WP15 AI feasibility report **Missing**.
- outside-source-links: WP2, WP4 **Delivered**; WP1 live address check **Missing** (needs SHY-DESKTOP).
- videos: WP6 sidecars and WP7 thumbnails could not be found in code: **needs a closer look**; Vault and Stash-box packets **Delivered**.
- book-reader-images: reader replaced and fallbacks **Delivered**; iPhone check open.
- backup-and-restore, batched-writeback, photo-video-filing, vault-sections-and-move, plugin-architecture, developer-tools (except community repository WP8), settings-and-top-navigation, library-access, simpler-access: packets **Delivered** by evidence.
- language-aware-wording: **Partly**: de/fr/es have 263 of about 3,400 strings; 28 files still on the typed-wording baseline.
- docs-hub and every "docs" packet: not checkable here; docs live in `tuvima-docs`.
- encrypted-plugin-keys: Locked, not built, as stated.

## Recommended statuses (not applied)

| Spec | Now | Recommend |
|---|---|---|
| libraries-redesign | Built, awaiting checks | **In progress** (Edit dialog, folder count paths, chips, picker items open) |
| first-run-setup-rework | Built, awaiting checks | **In progress** (3 Nov findings, Settings alignment) |
| library-setup-add-folder | In progress | **Superseded** by libraries-redesign and the rework (fix stale "toast" text) |
| library-setup-groups | Built | **Built, partly superseded** (WP1 to 4, 6, 9, 14 replaced) |
| one-shared-look | Built | **In progress** (app-wide square pills and the tab guardrail) |
| home-detail-and-tiles | Built | **Built, with open decisions** (trailer, Mark as watched, kind filter) |
| universal-editor-v3 | In progress | stays **In progress** (WP5, WP8) |
| tv-artwork-cast-and-matching | In progress | stays; correct header to WP1, 2, 4, 5 |
| universe | In progress | stays (U-WP15 left) |
| plugins v5 | Built | stays **Built, awaiting checks**; add "Metadata page is not the Plugins page" note |
| the rest listed above | as is | no change |

## Recommended fix order

1. **Decide the Libraries design in one spec** (libraries-redesign v2): folder count with paths on click, Edit with add/remove folders, area filter chips and colours (changes D2), setup and Settings sharing one table, View "Photos and Videos". This unblocks items 6 to 13 of your 3 Nov list.
2. **Folder picker v2**: wide, name on top, Go button, plain browsing and the check only after you choose. Small, high-value, can ship with 1.
3. **Activity page spec** (never specified): summary view, direct link, hover preview, stage progress bar, navbar icon and page agreement.
4. **One look pass**: app-wide square pills with a guardrail, one icon/button colour scheme from the intro's purple, find the stray green, aligned pills.
5. **Setup fixes**: animation box, empty-Libraries spacing, centring, Region.
6. **Fable review, then spec**: the View/Shared photos model.
7. **Later**: remaining small packets (editor WP5/WP8, TV WP6, universe U-WP15, translations).

## Process fix

A spec should only read "Built" when every item in its Scope has been seen working. Add to `engineering/specs/_template.md` a "Scope items you asked for later" line so review requests become packets instead of notes.

## Plain English summary

Much of what you expected was never written into a spec, so the builds could not have delivered it: the Activity page, folder counts with paths, Edit that adds folders, and the chips. A few items were written the other way round and the builds followed that. A few are true misses: the folder picker still shows errors while browsing and is narrow, and rounded pills remain in about 80 places. The best next step is one Libraries and folder-picker spec that settles the design from your pictures, then an Activity spec, then a look-and-colour pass. I have marked each affected spec with a "Gaps and drift" note but left every Status as it is until you decide.

---

# Addendum: reported bugs that were never fixed or never assigned

Added the same evening. Sources: Shy's 2 Nov list (40 items), the 1 Nov fix list and the 26 to 27 Oct bug section of `batch.md`, and Shy's 3 Nov list (30 items). Checked by reading code and commits, not by running the app. Plain words; no bug codes.

## Reported before, and still not right today (we said "fixed" or never fixed it)

1. **Folder picker.** Asked for three times (26 to 27 Oct, 1 Nov, 2 Nov): wide, fast, plain browsing, check only after you choose. Today it is still capped at 760 px, still shows the overlap error while you browse, has no Go button and puts the name at the bottom.
2. **Navbar Activity icon.** Reported 2 Nov, again 3 Nov. It still always opens Libraries (`SystemActivityIndicator.razor:71`). A "Running now" block was added to the per-library Activity tab only. The reason the indicator ran for 1h30m with nothing shown was never found.
3. **Activity page redesign.** Reported 2 Nov. No spec, no mockup, never built (direct link instead of "...", artwork hover preview, per-file stage bar, cleaner summary).
4. **Square pills everywhere.** Reported 2 Nov. Only tiles were changed; about 80 rounded pills remain and pills are misaligned in Settings.
5. **Settings Libraries page.** Reported 2 Nov (folder count, one Add button, area chips, summary strip). Only the setup table was rebuilt; Settings was deferred on purpose and never scheduled.
6. **Folder count with paths on click.** First asked 26 to 27 Oct (make the count clickable), again 2 Nov and 3 Nov. Still plain text.
7. **Metadata providers: key page and tiles.** Reported 2 Nov. Built for setup only; the Settings page it was shown on no longer has tiles.
8. **"..." menu text too small.** Reported 2 Nov ("larger, cleaner") and again 3 Nov. Menu items are 14 px, hints 13 px, in a narrow popup (`native-fields.css:32`, `app.css:3338`).
9. **Watch/Read hero and Watch items missing from the hero.** 2 Nov. Rotation was built; the cause of "no Watch items" was never diagnosed and no log was added.
10. **Metadata-source warning when adding Movies or TV.** 1 Nov and 2 Nov. Only an "All set" warning exists; adding a TV or Movies folder with no key still adds files silently.
11. **Stephen Curry MasterClass.** 2 Nov. A fix for "Videos course page not found" went in, but the batch row says the cause is not confirmed.
12. **Wrong-language logo (The Continental), Shimmer and Shine cast loss, episode stills.** 26 Oct to 2 Nov. Spec question about the logo is still open; cast-kept and fixtures not built; live checks never run.
13. **Wikipedia descriptions in the household language.** 1 Nov. Not confirmed; foreign-language translations cover about 8% of the text.
14. **Binned film still counted as "in progress" after Put back.** Marked "known, left".

## Reported on 2 or 3 Nov and never assigned to any spec

15. Whole-app colour: one icon and button scheme, purple background like the intro animation, track down the stray green (3 Nov 2, 3).
16. Setup: animation box, empty-Libraries spacing, off-centre pop-up, Region field, "your details stay on your Library server" line still on profile page, Vault libraries wrapped into Libraries, SubDL showing "Key saved" (3 Nov 1, 4 to 7, 16).
17. View: "Photos and Videos" as one choice, no folder name for photos, Shared photos plus per-profile plus aggregated folders (3 Nov 13, 14; needs Fable review).
18. **Hero and tile sizing rules (3 Nov 22, 23).** Shy now finds the hero too short (it was built to 55% of screen height on purpose) and every shelf below Recently Added too small. New rule asked for: every tile in a row has the same height (square albums wider, equally tall), also on Watch, Listen and Read. The spec only said "bigger tiles"; no uniform-height rule exists.
19. **Badges on tiles (3 Nov 24).** A "New" badge overlaps the episode count and Watch adds a type badge. Wanted: at most one small badge per tile. Not in the spec.
20. **Tile "..." and hover (3 Nov 25, 26).** "..." beside "+" may offer only Edit, and hover styles still differ in Read > Books and the hover overlay can cover the title. The spec says one hover style; it is not true everywhere.
21. **Needs Review page and the Match page look poor (3 Nov 21).** Merged functionally on 3 Nov; no design target; needs specifics from Shy.
22. Aligned and uniform pills; "Mark as watched" and trailer decisions (see main report).
23. Missing from 1 Nov and never specced: the "Compare" feature question, offering Tesseract and Local AI downloads in setup, "Reading file 3 of 38" progress for slow drives, a "this is a movie" prompt when dragging large files.

## Detail page buttons (3 Nov 27 to 30), with measured causes

- **Play, Read, Watch button too short.** 24 px text inside a 40 px box with no top or bottom padding (`HeroActionRow.razor.css` line 4). Netflix-like balance means about 48 px high with 18 to 20 px text.
- **Resume button looks bad.** It uses the same 40 px box for two lines of text (Resume and "4% watched") plus a progress bar, so everything is crushed (`HeroActionRow.razor` lines 12 to 19). Fix: same size as Play, one line of text, thin progress bar under the button or along its bottom edge.
- **Thumbs up/down hover mismatch.** Drawn button is 34 px, hover circles are 44 px (`MediaRateControl.razor.css`). Make them the same size.
- **"..." menu fonts.** See item 8 above.

## Why these slipped through

The 2 Nov review was turned into specs on 3 Nov, but only the parts that fitted a spec's scope. Items outside any spec, and items where the spec chose differently, were not carried forward and the review notes were not checked off against the builds. Specs then said "Built" for the packets written, not for Shy's reports. Fix: a bug reported by Shy is one numbered line in the next spec or a fix brief, and a spec cannot be "Built" until each of those lines is delivered or explicitly dropped by Shy.

## Plain English summary (addendum)

About fourteen things Shy reported in earlier checks are still not right, even though the specs around them say "Built". Nine more from today were never written into a spec at all (colour scheme, tile heights, tile badges, hover, View photos model, setup polish). The button problems are simple and measured: the Play button is too short for its text, the Resume button crams two lines into the same small box, the thumbs hover is bigger than its button, and the "..." menu text is small. I recommend one quick "buttons and menus" fix brief, then the Libraries and folder-picker spec, then Activity, then the look pass.
