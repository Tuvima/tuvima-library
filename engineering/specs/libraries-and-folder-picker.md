# Spec: Libraries page v2 and the folder picker

> **Status:** Built, awaiting your Local checks (PR #669) · **Version:** 2 of the Libraries design (replaces D2, D8 in part, D10 in part of [libraries-redesign](libraries-redesign.md) v1) · **Last updated:** 2026-11-05 (locked by Shy, all recommendations) · **Related docs:** [Libraries redesign](libraries-redesign.md) · [First-run setup rework](first-run-setup-rework.md) · [Look pass](look-pass.md) · [Photo and video filing](photo-video-filing.md) · [Design system](../design/design-system.md) · Audit: `engineering/validation/spec-audit-2026-11-03.md` · Local check: `engineering/validation/local-check-2026-11-03.md` (items 1-12, 18) · Mockups: `libraries-and-folder-picker.html`
>
> **Amended 2026-11-12** by [Setup and Libraries fixes](setup-and-libraries-fixes.md): the "In your collection" card is a grid of equal cells with one color per kind, shared with the row icons (D12). Where the two differ, the newer spec wins.

> **Version note (2026-11-10):** parts superseded by [universal-editor-v5](archive/universal-editor-v5-header-navigation-and-supporting-files.md) (section 9): the picker's selection rule changes: the folder you are inside is the selection, named above the list, and "Type a path…" lives in the Locations list instead of a path box (D32); the dialog, rows, toolbar and search move onto the shared browser frame, and every other selection rule stays.

> **Note (2026-11-06):** [Libraries v3](libraries-fixed-names.md) changes the library naming and the left rail described here; this spec stays as history for the rest.

> Locked by Shy on 2026-11-05 ("agree on all"). Mockups: `../design/mockups/libraries-and-folder-picker.html`. Shy's reference pictures stay on SHY-DESKTOP and are not committed. Answers recorded: Home chips yes (D16), Photos and Videos one choice (D8), Region removed from setup (D10), Vault tab in the one Add library list (D11 item 5), Import folder as a permanent first row (D15).

## Plain-English product walkthrough

**What Shy asked for, and what was never written down.** After the 2 and 3 Nov reviews, the Libraries page still shows "1 folder" as plain text, Edit only renames, there are no Watch / Listen / Read / View filters, and the folder picker is narrow, slow to feel, puts the name at the bottom and shows "overlaps Movies" while you are only browsing. The audit found these were never in a spec (or were specified the other way round: v1 decision D2 says "nothing to toggle"). This spec writes them down.

**What it feels like after this spec.**

1. **Libraries has area chips.** Under the page title: All, Watch, Listen, Read, View, each in its own color (Watch purple, Listen pink, Read green, View teal). Clicking one shows only that area's libraries in the same single table. This *replaces v1 decision D2* ("one table, nothing to toggle"); the table, search and status filter stay.
2. **A summary strip** above the table (one count per kind, in the area's color, including Photos & videos), then a scan card (when nothing is running it reads "Idle · Last completed scan 12 minutes ago", no bar; View activity is always there; while a scan runs it is: "Scanning TV", where, a progress bar with the percent, and View activity. Chips show a count (All 5, Watch 2). Each row has an icon tile, name with a short subtitle, an area tag, contents, the folder link with a chevron, a status dot with a word (a scanning row shows its own small bar), one labeled Edit button and nothing else on the row (no "..." menu). The footer says "5 libraries · 9 folders" and when the last scan finished. (Shy's reference pictures, 5 Nov.) The page header is "Library overview — Your collection and the folders that keep it growing"; the summary is a gradient "In your collection" card with a big colored icon per kind; each area has one icon (Watch screen, Listen headphones, Read book, View picture) used on its tag, row tile and chip; Edit is a labeled button; no Cards view and no "Your libraries" heading, only chips, search and the status filter; the summary card shows every kind at once and shrinks its text to fit; View libraries are ordinary rows in the table; the picture's separate View row is not copied (Add library already covers it).
3. **The folder count is a button.** "3 folders" opens a small list of the library's real paths, each with its online / offline state and a copy button. It never navigates away.
4. **Edit is the one action.** The row has no "..." menu: Scan now, Move to Vault (administrators) and Remove library (asks once) sit in the Edit window.
4b. **Edit manages folders.** Edit opens one dialog: library name, the folder list (each with a "..." menu: Open in picker, Remove), Add folder, Cancel and Save changes. Nothing is saved until Save changes, then the usual Review of what will happen (removing a folder never deletes files; it asks "Keep the titles for now or remove them?").
5. **The folder picker is wide and plain.** One wide window. At the top: library name (large, the first thing you type), then the path box with a **Go** button. Left: drives and media locations. Right: the folder list (name, modified, arrow) with back, up, refresh and search. While you browse, *nothing is checked and no warning appears*. The only thing at the bottom is "Your files are never changed", Back, and **Add library**. When you press Add (or "Choose this folder"), the check runs once and says Looks right / Heads up / Already used, with the one fix offered.
6. **View offers "Photos and Videos" as one choice** (a dump of your phone), plus Documents. The Engine still sorts each file by what it is.
7. **Setup uses the same table, chips and picker**, with the 9 polish fixes from the 3 Nov check (below). Region is removed from the Welcome page.
8. **Home's shared rows** ("New in your library", "Continue where you left off") get the same Watch / Listen / Read / View chips so you can narrow the mixed rows to one area (D16).

**What stays the same:** two-pane rail on desktop, library pages and tabs, household on/off access, Vault group, no toasts, desktop-only editing, "Your files are never changed".

**Scope boundaries.** Out: the Activity page (next spec), the "View photos model" (shared photos plus per-profile locations, needs a Fable review), app-wide colors and square pills (look pass follow-up), Metadata page tiles.

## 1. Goal

Make Libraries (Settings and setup) match the design Shy approved in pictures: filter by area, see and edit folders without leaving the table, and add a folder through a fast, wide, plain picker that only checks when you choose.

## 2. Scope

**In scope:** area chips and colors; summary strip; folder count popover; Edit dialog with add/remove folder; wide folder picker v2; "Photos and Videos"; setup/Settings sharing one table; setup polish items 1-9 of the 3 Nov check; Region removed from Welcome; Home shared-row chips; phone and TV behavior.

**Out of scope:** Activity page; View photos model; Needs Review redesign; global color scheme beyond the area colors; Metadata provider tiles; Universe/editor work.

## 3. Decisions made

Shy's earlier decisions carried over: square pills; Vault step before Libraries in setup; folder picker is a plain list, check after you choose; US spelling; no toasts (row glow); delete is instant after one "Are you sure"; editing is desktop-only.

- **D1. Area chips replace v1 D2.** One table stays; chips filter it. Chip colors come from the merged design system's area tones (`AppIconTone` area); the table has no second set of color rules. *Revises Locked v1 D2, so this is Version 2 and is played back to Shy for approval.* Reason: Ease of use; with many libraries, finding one area fast beats scrolling.
- **D2. Chip selection is remembered per profile in the browser** and defaults to All. Rail still lists areas; clicking a chip and clicking an area in the rail select the same thing.
- **D3. "N folders" opens a popover** with paths (copyable, offline folders flagged). Keyboard: Enter/Space opens, Esc closes. It never navigates.
- **D4. Edit = name + folders in one dialog** with the standard fixed footer (Cancel, Save changes) and Review. Removing a folder asks once what to do with its titles. Adding uses the same folder picker. A Managed folder cannot be removed while a folder job runs ("Wait until the move finishes").
- **D5. Picker v2 layout.** Width up to 1100 px (full width minus 32 px on phones). Order top to bottom: step line ("Step 3 of 4" in setup only), area + kind chips (read-only), **Library name** field (autofocus, larger type), path box + **Go**, then drives list and folder list, then the safety line and buttons. Replaces the 760 px cap and the name-in-footer layout (v1 D8, D9).
- **D6. Browsing is dumb.** Clicking, opening, going up, Go, refresh and search only list names. No validation call runs on browse. v1 D10's "check runs afterwards while browsing" is replaced by: **the check runs once, when you press Add / Choose.** It still can't hang: capped at 10,000 files or a few seconds ("10,000+"), and never blocks Cancel.
- **D7. The check result.** Looks right (green), Heads up (amber, e.g. contains photos), Already used (red, names the library, e.g. "This folder holds a folder that the library 'Movies' already uses"). Already used blocks Add; the others don't.
- **D8. View choices:** "Photos and Videos" and "Documents". Personal videos and Photos stop being separate choices in Add library (answered: one choice). Existing libraries keep working (pre-release: reset acceptable).
- **D9. No folder name is asked for a View photos folder** (name field shows "Photos and Videos", editable, optional).
- **D10. Region is removed from setup Welcome.** What Region does (checked in code): it is the *spelling variant* of the display language (English US / UK / Canada / Australia, French variants), set by the language picker; it only changes UI wording. **Country** is the other field and is what providers use for ratings and storefronts. They are different things, but the spelling variant is a detail that doesn't belong on the first page. Setup defaults the variant from the browser (US English otherwise); it stays in Settings, General, Language and region. (Shy confirmed.)
- **D11. Setup polish** (3 Nov check items 1-8, 9 folded in above): (1) no box around the welcome animation; (2) Find / Connect / Enjoy and Private-by-default icons use area/accent tones; (3) the setup background uses the same purple as the animation, and the stray green is traced and removed (likely Read's green; it appears only where Read is meant); (4) Region removed (D10); (5) the Vault step's own "add libraries to the Vault" controls are removed: once the Vault is on, Vault-only kinds appear in the one Add library list under a Vault tab (Shy confirmed); (6) empty Libraries state: heading, a gap, one sentence, an obvious Add library button; (7) every Add library step stays centered; (8) the folder picker is the new one (D5); (9) folder count and Edit (D3, D4) work in setup too.
- **D12. Setup and Settings share one table component** (v1 D13 setup flag stays). Setup shows no rail and no library pages.
- **D13. Phone:** chips scroll sideways, rows become cards, folder popover becomes a bottom sheet, **view only** (no Edit, no Add folder). **TV:** Libraries is not shown (administration is desktop). Home chips (D16) work on phone, tablet and TV as a focusable row; D-pad moves between chips, Select toggles.
- **D15. The Import folder is a permanent first row in the table** (Shy, 5 Nov). It handles new files of every media type, so it is shown as a pinned first row: name "Import folder" with a small (i), area tag "All media", contents "Nothing waiting" (or "N waiting"), folders "Choose a folder" until set, status "Not set" (amber) until set, then "Up to date". Edit opens the same folder window (one folder only). It stays visible under every chip. The (i) says what it is (the landing place for new files of any kind, checked and filed into the right library), what it does, and how it is managed (change the folder here; files are never changed until filed). Replaces v1's last rail entry and its separate page; the Photos and Videos filing details stay on the View library. Pending Shy's confirmation of the exact (i) wording.
- **D16. Home shared rows get the same chips** (Shy, 2026-11-05): "New in your library" and "Continue where you left off" can be narrowed to Watch, Listen, Read or View; remembered per profile; defaults to All.
- **D14. Metadata-source warning when adding Movies or TV with no key:** out of this spec (the audit lists it separately), but the picker's result card has room for one Heads up line when it ships.

## 4. UI behavior

| Situation | What the person sees |
|---|---|
| Normal | Chips, summary strip, one table. Folder count button. Edit and "..." per row. |
| Empty (no libraries) | Heading "No libraries yet", a gap, "Add a library for each kind of media you own. You can add more later in Settings.", primary Add library button. |
| Chip with no libraries | "No Listen libraries yet" with Add a Listen library. |
| Loading | Skeleton rows; chips already usable. |
| Error (Engine unreachable) | Existing page error state with Try again; the picker keeps the last list and says "Can't reach your Library server". |
| Folder offline | Status "Folder offline", folder popover marks the path, Edit lists it with Remove. |
| Picker: slow drive | Names appear as they arrive ("Loading folders…"); Go and Cancel stay usable. |
| Picker: no access | "Library can't open this folder. Pick another or check its permissions." The Add button stays off. |
| Check running | "Checking…" under the Add button; Cancel works. |
| Partly ready (scan running) | Summary strip bar with "View activity"; rows show Scanning. |

## 5. Data / model changes

- Library kind list for View: one "Photos and Videos" kind replaces two (migration not needed pre-release; reset acceptable).
- Chip choice saved as a per-profile preference (local to the browser).
- Wire: the folder check endpoint is only called on choose; Contracts tests and fixtures update with any shape change.
- Region (display spelling) default derives from the browser at first run; no schema change.

## 6. Work packets

### WP1: Area chips, colors and summary strip (Standard)
Files: `Settings/Libraries/LibrariesOverview`, new chip row using `AppIconTone`, tests. Depends on: none. Done when: bUnit test shows the chip filters the table and keeps search/status filter; chips use area tone tokens with no raw colors; guardrail test passes.

### WP2: Folder count popover (Standard)
Files: `LibrariesOverview`, new popover. Depends on: none. Done when: clicking "3 folders" lists 3 paths with offline flags and a copy button; Esc closes; phone width shows a bottom sheet.

### WP3: Edit dialog with folders (Standard)
Files: Library edit dialog, `LibraryFolderTable`, Engine folder remove action reuse. Depends on: WP5 (picker). Done when: rename + add + remove then Save shows Review, nothing saved before Save changes, a removed folder's files stay on disk.

### WP4: Folder picker v2 layout (Standard)
Files: `ServerFolderPicker.razor` and `.css`, add-library dialog. Depends on: none. Done when: dialog is 1100 px at 1440, name field at top with focus, Go button lists the typed path, list works with search/back/up/refresh.

### WP5: Check only on choose (Standard)
Files: `ServerFolderPicker.razor`, folder check call site. Depends on: WP4. Done when: a test with a spy proves zero check calls while browsing and exactly one on Add; "overlaps" text never appears while browsing.

### WP6: Photos and Videos as one View choice (Standard)
Files: Add library kinds, View kind mapping, filing hooks. Depends on: none. Done when: Add library > View shows two choices; files in the folder are still sorted into photos and videos; existing photo/video filing tests pass.

### WP7: Setup alignment, polish and Region removal (Standard)
Files: `SetupPage`, `SetupLocaleStage`, `SetupMediaStage`, intro stage CSS, Vault stage. Depends on: WP1-WP5. Done when: items 1-9 of D11 each pass in a bUnit/CSS test; Welcome shows language and country only; browser default sets the variant.

### WP8: Home shared-row chips (Standard)
Files: Home shelves state, `HomeSpotlightState`. Depends on: WP1. Done when: choosing Listen narrows "New in your library" and "Continue where you left off" to Listen; choice is remembered; TV focus moves chip to chip.

### WP9: Import folder row (Standard)
Files: `LibrariesOverview`, `LibrariesRail` (retire the Import entry), Import folder page reuse. Depends on: WP1, WP4. Done when: the overview's first row is "Import folder" with (i), "All media", "Not set" until a folder is chosen, stays under every chip, and Edit opens the one-folder window; the old rail entry and page are gone.

### WP10: Docs and tests (Mechanical)
Files: docs repo Libraries and setup pages, batch.md, v1 spec note. Depends on: all. Done when: public docs explain why (chips, check-on-choose, Region), batch list updated.

Waves: A = WP1, WP2, WP4, WP6 (two at a time); B = WP5, WP3, WP9; C = WP7, WP8; D = WP10. Built in the cloud; look checks are Local checks.

## 7. Local checks

L1 Chips and colors at 1440 and 1280 (look). L2 Folder popover with an offline network drive. L3 Edit: add and remove on real media folders. L4 Picker on `Z:\` network share: browsing is instant, no warning until Add. L5 View Photos and Videos with a real phone dump. L6 Setup run from a fresh state, all 9 polish items. L7 Phone view of Libraries. L8 TV Home chips (device). L9 Import folder row: Not set, then set, then waiting count.

## 8. Risks and open questions

- Chips on Home follow Shy's "shared rows" note, read as filtering the mixed Home rows (D16).
- Home chips and the Import row are new surfaces; both need a Local look check.
- Removing the browse-time check means a bad choice is found one click later; mitigated by a fast, capped check.
- Merging Photos and Videos hides a choice some may want (videos in a different place); they can add two libraries of the same kind.

**Gaps and drift:** v1 D2, D8 (name in footer), D9, D10 (check while browsing) change here; v1 stays Locked as history, this is Version 2. Setup rework D10 (Region kept) changes by D10 here.

## Plain English Summary

The Libraries page gets colored Watch / Listen / Read / View filters, a clickable folder count that shows the real folders, and an Edit window that adds or removes folders. The folder picker becomes wide, puts the library name first, has a Go button, and stays quiet while you browse, checking only when you press Add. Photos and videos become one choice. Setup uses the same pieces and gets the polish fixes, and the confusing Region box leaves the Welcome page.
