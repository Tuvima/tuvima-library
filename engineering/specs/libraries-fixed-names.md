# Spec: Libraries v3 — fixed library names, folder names, and one folder picker

> **Status:** Built, awaiting Local checks (L1 to L9 and L11 in `engineering/validation/batch.md`; L10 and WP9 come from the local-check fix build) · **Version:** 3 of the Libraries design (revises D1/D2 naming parts of v2 and the rail of [libraries-redesign](libraries-redesign.md) v1 D1; nothing else in v2 changes) · **Last updated:** 2026-11-06 · **Related docs:** [Libraries page v2 and the folder picker](libraries-and-folder-picker.md) · [Libraries redesign](libraries-redesign.md) · [Photo and video filing](photo-video-filing.md) · [Library access](library-access.md) · [Design system](../design/design-system.md) · Local check: `engineering/validation/local-check-2026-11-05.md` items 4 to 7, 11 and 13 (not yet on main; written from Shy's answers 8A, 9A, 10A, 12A, 13A) · Mockups: `../design/mockups/libraries-fixed-names.html`

> **Version note (2026-11-10):** parts superseded by [universal-editor-v5](universal-editor-v5-header-navigation-and-supporting-files.md) (section 9): D21's one folder picker moves onto the shared browser frame (D32 and WP8a): the folder you are inside is the selection, named above the list, and "Type a path…" lives in the Locations list; every other rule stays.

> Locked by Shy on 2026-11-06 ("lock it", after answering questions 30 to 35). The Locked v2 stays as history. Mockups: `../design/mockups/libraries-fixed-names.html`.

## Plain-English product walkthrough

**What happened.** On 5 Nov, Shy added libraries and found that Library asks you to *type a name* for a Movies library, Edit shows a different folder picker than Add library, nothing says whether Library will only *read* a folder or *keep it tidy*, and the left rail on Settings → Libraries squeezes the table. Shy's call: a library is a *kind of media*, so its name is fixed ("Movies are Movies"); only the *folders* underneath can have names, and only matter when there is more than one, and then filter the library.

**What it feels like after this spec.**

1. **Library names are never typed.** Add library is **three steps** in one fixed window: 1 "Where does it belong?" (square tiles: Watch, Listen, Read, View), 2 "What is it?" (square tiles of that area's kinds), 3 "Where is it?" (the folder). Today it is four steps (area rows, kind rows, folder, name); the name step goes away. Choosing Movies creates the library named **Movies**. TV Shows, YouTube, Videos, Music, Audiobooks, Other audio, Books, Comics, Other reading, **Photos and Videos**, Documents work the same. There is no "Library name" box in Add library or Edit, and no Rename. Pressing Add on a kind that already has a library offers "Add a folder to Movies instead".
2. **Every folder gets a name by default, and it is only used when there is more than one.** The **folder step asks for it at the top**, as a friendly name ("Give this folder a friendly name", for example "Family NAS" or "4K shelf"), pre-filled from the last part of the path, so you can just accept it. It is the name of the *folder*, never of the library (the library is always "Movies"); you can edit it any time. With one folder the name is stored but never shown. Once a library has two or more folders, the name appears in the Folders tab, the Edit window and the folder pop-over above the path, **and the library page's filter row (under the library header, for example under Movies) gets a Folders filter**: All folders or one specific folder, so you can see only the titles from that folder.
3. **Every folder says how Library uses it.** Two plain labels with a tag in the picker, the Edit window, the folder pop-over and the Folders tab: **Read-only** ("Library only reads this folder. Your files are never changed.") and **Managed** ("Library keeps this folder tidy: new files are filed and named here."). The folder step has a two-choice control to pick one, defaulting to Read-only; Managed is explained in one sentence and asks the existing "empty or willing" check. (Code today: every kind is Read-only by default, and the dialog never showed it. Hence the silence.)
4. **Photos and Videos follow the same pattern.** One fixed name, no name field, optional folder names, the same handling labels.
5. **One folder picker, one size, everywhere.** Add library, Add folder from Edit, the Folders tab, the View folder dialog and setup all open the same window: 1100 px wide by 680 px high on desktop (full width minus 32 px on phones, view only). The window never changes size between steps; only its contents change and the folder list scrolls inside it.
6. **No left rail on Settings → Libraries.** Top to bottom (Shy's picture, 5 Nov): the page header with **Add library at the top right**; the "In your collection" card; then the **filter row under the collection card** (area chips All, Watch, Listen, Read, View, and Vault for an unlocked administrator with the Vault on, plus search and the status filter); then the table. The chips, search and status filter are the only way to narrow the list. The table uses the full width of the page. Clicking a row opens the library's own page (with a "Libraries" breadcrumb back).

**What stays the same.** Everything else in v2: chips and colors, summary card, folder pop-over, Edit as one window with Review, check only when you press Add, Import folder row, Home chips, setup polish. The page header, "In your collection" card, scan card with progress bar and percent, per-row scanning bars and footer are in v2 but not yet built; they ship in the fix build for v2 and are *not* part of this version. A parallel "Plan the Libraries activity page" thread owns activity and scan progress; this spec only keeps the scan card's "View activity" link where v2 put it, pointing to the single Activity page that thread proposes (the top-bar spinner opens it; a library's Activity tab is the same page filtered to that library). No overlap with the rail, names or picker.

**Scope boundaries.** Out: the Activity page and scan progress, renaming old libraries (pre-release data may be reset), the Needs Review redesign.

## 1. Goal

Make adding and editing a library impossible to get wrong: nothing to name, nothing hidden about what Library will do to a folder, and the same folder window every time.

## 2. Scope

**In:** switching a folder to Managed from Edit (D27); fixed library names; optional folder names; Read-only / Managed labels and choice; one picker component and one fixed window size; rail removal and full-width table; Vault as a chip; docs.

**Out:** Activity page; scan progress UI; View photos model; Needs Review; merging one folder into another (exists already, unchanged).

## 3. Decisions made

Carried over from earlier decisions: square pills; US spelling; no toasts; editing desktop-only; delete after one "Are you sure"; check once on Add (v2 D6).

Shy's answers 5 Nov (8A, 9A, 10A, 12A, 13A) recorded as D17 to D22. New numbers continue from v2's D16.

- **D17. A library's name is fixed by its kind and never typed.** Names come from `config/library_kinds.json` labels (Movies, TV Shows, YouTube, Videos, Adult content (Vault only), Music, Audiobooks, Other audio, Books, Comics, Other reading, Photos and Videos, Documents). The name field and Rename are removed from Add library, Edit and the library page "..." menu. At most one library per kind per household; a second place for the same kind is a second folder. Replaces v1/v2 name rules (v2 D5 name field, v1 D4 Rename, the "unique within the area" check). Reason: Ease of use; Shy found the choice unexplainable.
- **D18. Every folder has a name, added by default, used only with two or more folders** (Shy, 5 Nov). Stored on the folder source (new `Label` on `LibrarySourceDto`, max 60 characters, unique within the library). The folder step shows it as the first field, at the top, labeled "Friendly name" with the hint "Names this folder, not the library", pre-filled from the last path segment; it stays editable in Edit. With one folder it is not shown. With two or more it is shown in the Folders tab, Edit and pop-over, and the library page gets a **Folders filter** (All folders or one folder) in its filter row; choosing a folder narrows the titles and files to that folder. Reason: Ease of use; naming costs nothing and the filter answers "what is on which drive".
- **D19. Handling is always visible and chosen when the folder is added.** Labels **Read-only** and **Managed** as in the walkthrough (item 3). The folder step has a two-choice control, default Read-only; Managed is offered for kinds whose Engine rules allow it (filing, naming) and for the Photos and Videos Import. Existing check results stay (Looks right / Heads up / Already used), with one added Heads up when Managed is chosen on a folder that already holds files ("Library will move and rename files here. Choose Read-only to leave them alone."). Reason: Reliability and trust; the person should know before files move.
- **D20. Photos and Videos follow the same pattern** (no name field; fixed name; optional folder names; handling labels). v2 D9 ("name field shows Photos and Videos, editable, optional") is replaced by this.
- **D21. One folder picker.** The shared `ServerFolderPicker` is the only folder window. Its dialog options are one constant: 1100 px wide by 680 px high at desktop widths (v2 D5 said "up to 1100 px"; now fixed), full width minus 32 px on phones. Add library step 1 (What is it?) and step 2 (Where is it?), Add folder (Edit, Folders tab, View folder dialog, setup) use it with the same layout: step line (setup only), area and kind chips (read-only), **Friendly name field (folder step only, at the top)**, path box with Go, drives and folder list, the folder step's handling control, then the safety line and buttons. The "kind" step uses the same frame so the window does not jump. Callers differ only in what they prefill. Inline copies and variant option sets (`LibraryEditDialog`, `AddViewFolderDialog`, `ViewFoldersTab`, `LibraryShell` each build their own parameters today) are removed.
- **D22. The left rail is removed from Settings → Libraries.** (Shy, 5 Nov: only this page's rail goes; the rails inside the Watch, Listen, Read, View and Vault area pages stay untouched.) `LibrariesRail` and its two-pane layout go. Order is header with Add library top right, "In your collection" card, filter row (chips, search, status filter) under it, then the table (today the chips sit above the card and Add library sits beside the search box, so v2's picture was not followed); the chips, search and status filter are the only filter; the table, search and the status filter use the full page width. The Import folder pinned row (v2 D15) and the "Rules for every library" card stay. Vault libraries are ordinary rows in the table, shown only to an unlocked administrator with the Vault on; a Vault chip after the other chips filters to them (replaces the old rail's Vault group). Reason: Maintenance (one navigation, not two) and Shy's full-width table request.

- **D23. Add library is three steps of square tiles, drawn only from the kind list** (proposed, question 34). In the real dialog today, step 1 is the area (Watch, Listen, Read, View) as full-width rows and step 2 is the kinds as icon tiles; it jumps straight to step 2 when you click Add library from inside an area, which is why the area step is easy to miss. In v3: step 1 shows the areas as square tiles (area icon and color, name); step 2 shows that area's kinds as square tiles (icon, name); both are auto-fill grids with one fixed tile size that wrap naturally (no centering, per Shy's 4 Nov rule). Tiles come only from `config/library_kinds.json` (area, label, icon, order, plus a new short `explainer` text per kind and area); nothing in code lists kinds or areas. Adding Podcasts is one new entry with area "listen" and appears with no code change; an area with 1 or 20 kinds lays out correctly; the dialog keeps its fixed size and the grid scrolls inside it. Every tile is the same size and carries its icon: each area uses its one area icon and color from the design system's area tones (Watch screen, purple; Listen headphones, pink; Read book, green; View picture, teal; the same ones as the chips, table tags and row tiles), and each kind uses the `icon` from the kind list in its area's color. Icon size and tile size are fixed constants, so no tile looks bigger than another. A kind with no icon falls back to its area icon. A kind that already has a library stays greyed with "Add a folder to it instead". Step 1 is skipped only when the dialog is opened from an area chip that is selected, and the step line then reads "Step 2 of 3" so it is clear a step was filled in; a "Change" link beside the area name goes back. Reason: Ease of use and Extensibility.
- **D24. Hover explainers are a card, not tooltip text.** Hovering or focusing a tile opens a small styled card beside the tile (not the browser/AppTooltip bubble): the tile's name, one plain sentence about what belongs in it, two or three examples, and, for kinds, one line on what Library does with it ("Looks up posters and details" or "Keeps your files as they are"). It closes on mouse-out, focus-out or Esc, is reachable by keyboard, and has no (i) icon. The text lives in the kind list's `explainer` fields, so a new kind brings its own explainer. This replaces the (i) tooltip in `LibraryKindCards` (the Change kind and file-drop dialogs use it too). Editing is desktop-only, so there is no touch behavior to design. Reason: Shy finds tooltip text poor.

- **D25. App-wide rule: our own tooltips, always on top, never the browser's** (Shy, 5 Nov). Every hover hint comes from the one shared tooltip component (`AppTooltip`), never from a browser `title=` attribute (today 134 of them across 85 Razor files). Hints open almost at once (about 100 ms, not the browser's second-plus delay), use the app's look, and always render in the topmost layer above dialogs, menus, tiles and sticky bars, so nothing ever clips or hides them. They flip to the other side or slide along the edge when there is no room, and they also open on keyboard focus. A guardrail test fails the build if a Razor file adds a `title=` hint. This v3 applies it to every control it touches (Libraries page, Add library, Edit, picker, hover cards of D24); the sweep of the remaining files is a separate work packet (WP9), which the local-check fix build is also doing app-wide (if that merges first, WP9 shrinks to the guardrail tests). The rule for menus, pop-overs and everything else that pops up is D26 below; D25 and D26 are one rule. Reason: Reliability of the look and Ease of use.

- **D26. App-wide rule: everything that pops up stacks correctly** (Shy, 5 Nov). Tooltips, hover cards, "..." menus, dropdowns, the folder pop-over and any other pop-up render in one shared top layer, above whatever opened them (a dialog, a table row, a sticky bar, a tile), and are never clipped by a scrolling or overflow-hidden parent. One layer order is defined once in the design system: page, sticky bars, dialogs, pop-ups (menus, dropdowns, pop-overs), then tooltips on top of everything. A pop-up opened inside a dialog sits above that dialog; a tooltip on a pop-up's item sits above that pop-up. Pop-ups flip or slide to stay on screen, close on Esc and outside click, and never leave a second one open beneath. Buttons that open pop-ups use the same shared components. A guardrail test rejects new raw z-index values in component CSS (only the layer tokens are allowed). The Libraries surfaces in this version follow it; the app-wide sweep joins WP9. Reason: Reliability of the look.

- **D27. A Read-only folder can be switched to Managed later, from Edit** (Shy 5 Nov, answer 33: yes). Existing Engine mechanic, reused: the folder's "..." menu in Edit gets "Switch to Managed". It first shows a **Preview** (how many files will be renamed and moved, how long it should take) and a **Review** that says in plain words: Library will rename and reorganize the files *inside this same folder* to its standard layout; nothing is copied to another place; it can take a while for a big folder; the folder shows "Converting" until it finishes; it cannot be undone automatically, but a full old-name to new-name log can be saved. The job can be paused, resumed and cancelled (a cancelled job keeps what was done and the rest is tidied by normal scans). Switching back to Read-only stops the job and leaves files where they are. Reason: Reliability and trust; the choice stays in the person's hands and nothing moves silently. **Open point for Shy: how much of the library is unavailable while it converts (question 35 below).**

## 4. UI behavior

| Situation | What the person sees |
|---|---|
| Add library, kind already has a library | Kind card greyed with "Add a folder to Movies instead" (button opens the folder step for Movies). |
| Folder step | "Friendly name" at the top, pre-filled from the path, editable; hint "Names this folder, not the library". Handling choice, Read-only selected. |
| Library page, one folder | No Folders filter; the name is not shown. |
| Library page, two or more folders | Folders filter in the filter row: All folders plus one entry per folder name; picking one narrows the list; remembered per profile in the browser. |
| Edit, one folder | Path and handling tag; name not shown. |
| Edit, two or more folders | Each folder: name (editable, optional), path, handling tag, "..." menu (Open in picker, Remove). |
| Folder pop-over | Name above path when named and when the library has 2+ folders; handling tag; online or offline; copy. |
| Managed chosen on a folder with files | Heads up line (D19); Add stays on. |
| Libraries page, empty chip | "No Listen libraries yet" with Add a Listen library (unchanged). |
| Phone / TV | View only. Table scrolls as cards, no Edit, no picker. Vault chip hidden. |

## 5. Data / model changes

- `LibrarySourceDto`: add optional `Label` (string, max 60). Contracts tests and wire fixtures update (`tools/Update-ContractFixtures.ps1`).
- Library name is derived from the kind; stored `Name` is no longer user-entered (pre-release: reset acceptable, no migration).
- `ServerFolderPicker.DialogOptions` fixed size constant; remove per-caller overrides.
- Handling per folder already exists (`AccessMode`, `ManagementMode`); labels only read it.

## 6. Work packets

### WP1: Fixed library names (Standard)
Files: `CreateLibraryDialog`, `LibraryEditDialog`, library page header "..." menu, library creation call. Depends on: none. Done when: no Library name field in Add library or Edit; Rename is gone; adding Movies creates "Movies"; a second Movies library cannot be created (offers Add folder); tests updated.

### WP2: Folder names (Standard)
Files: Contracts `LibrarySourceDto`, Engine settings, Folders tab, `LibraryFoldersPopover`, Edit dialog, folder step. Depends on: WP3. Done when: every new folder gets a default name from its path; the name shows only with 2+ folders (pop-over, Edit, Folders tab); a library with 2+ folders has a Folders filter that narrows its titles to the chosen folder; Contracts fixture updated.

### WP3: One folder picker, fixed size (Complex)
Files: `ServerFolderPicker.razor` and `.css`, `CreateLibraryDialog`, `LibraryEditDialog`, `AddViewFolderDialog`, `ViewFoldersTab`, `LibraryShell`, setup media stage. Depends on: none. Done when: a test asserts every launcher uses the single dialog options; at 1440 the window is 1100 by 680 at every step; Add library and Add folder look identical except the prefilled kind; old duplicate parameter builders are deleted.

### WP4: Handling labels and choice (Standard)
Files: folder step, Edit dialog, `LibraryFoldersPopover`, Folders tab, check result card. Depends on: WP3. Done when: every folder row and the picker show Read-only or Managed with the one-sentence explanation; Read-only is the default; Managed on a folder with files shows the extra Heads up.

### WP5: Remove the rail, full-width table, Vault chip (Standard)
Files: `LibrariesTab.razor`, `LibrariesRail` (delete), `LibraryAreaChips`, layout CSS, tests. Depends on: none. Done when: no rail renders on any Libraries level; Add library is top right of the header and the filter row sits directly under the collection card; the table is the page width at 1280 and 1440; an unlocked administrator with the Vault on sees a Vault chip; a library page has a Libraries breadcrumb back.

### WP8: Square tiles and hover cards from the kind list (Standard)
Files: `CreateLibraryDialog` steps 1 and 2, `LibraryKindCards`, new tile and hover-card component, `config/library_kinds.json` and its schema (`explainer`), tests. Depends on: WP3, WP1. Done when: areas and kinds render as square tiles in an auto-fill grid; hovering or focusing a tile shows the explainer card (no tooltip bubble, no (i)); a bUnit test with a fake extra kind shows it, with its explainer, and no code change; 1 and 12 kinds both fit the fixed window; the dialog is three steps.

### WP9: One tooltip and pop-up layer system (Delivered by the local-check fix build, bug 28)
Files: `AppTooltip` and its script/CSS (top layer, 100 ms open, flip, focus), every Razor file using `title=` as a hint, guardrail test. Depends on: none. Not built here: the combined fix build delivers the shared tooltip, the replacement of every browser `title=` hint, top-layer pop-ups and the guard test; D25 and D26 stay as the rule this spec relies on. Done when: one layer-order token set exists in the design system and component CSS has no raw z-index; menus, dropdowns, pop-overs and tooltips render in the shared top layer and are never clipped inside a dialog, table or scroll area (test with a pop-up opened from a dialog row); no `title=` hint remains outside `<title>` and Title parameters; a test proves the tooltip renders in the topmost layer above an open dialog; hints appear within about 100 ms and on keyboard focus.

### WP6: Photos and Videos alignment (Mechanical)
Files: kind catalog, View folder dialog. Depends on: WP1, WP3. Done when: Photos and Videos has no name field, uses the shared picker, shows handling.

### WP7: Docs and tests (Mechanical)
Files: docs repo Libraries pages and setup page, `engineering/validation/batch.md`, v2 note pointing here. Depends on: all. Done when: public docs explain why (fixed names, folder names, handling labels, one picker); batch list updated.

Waves: A = WP3, WP5 (two at a time); B = WP1, WP4; C = WP2, WP6; D = WP7. Built in the cloud; look checks are Local checks.

## 7. Local checks

L1 Add library for every kind: name never asked, result named correctly. L2 Add a second Movies folder: both names appear, and the Folders filter on the Movies page narrows to one folder. L3 Read-only and Managed tags in picker, Edit and pop-over; Managed warning on a folder with files. L4 Compare Add library and Edit → Add folder side by side: identical size and layout at every step. L5 Libraries page at 1280 and 1440: no rail, full-width table, chips filter, Vault chip as an unlocked administrator. L6 Photos and Videos on a real phone dump. L7 Setup run: same picker, fixed size. L8 Hover and keyboard-focus every tile: explainer card appears and closes. L11 Switch a copy of a real folder to Managed from Edit: Preview counts match, files are renamed in place on the same drive, pause, cancel and Engine restart all behave, and note what can and cannot be played while it runs. L10 Open a "..." menu, the folder pop-over and a dropdown inside Edit and the Libraries table: each sits above its parent, is not clipped, and tooltips on their items sit above them. Hover icon buttons inside a dialog, a menu and a sticky bar: the hint is always visible on top and appears quickly. L9 Add a test kind (for example Podcasts) to the kind list only: it appears as a tile in Add library with no code change.

## 8. Risks and open questions

- Old libraries with typed names (pre-release): reset acceptable; if Shy keeps data, a one-time rename to the kind name is needed (not planned).
- Fixed names mean a household cannot have "Kids Movies" separate from "Movies" except as a named folder. Access by household is on/off per library (library-access v4), so a Kids split is not possible by library. Confirm this is acceptable (question 31).
- Switching to Managed renames and reorganizes real files in place. It is covered by Engine tests (below) but only with temporary test folders; it needs a Local check on real media (L11).
- Earlier folder-behavior decisions (Oct 12) already allow converting a folder to Managed as a visible background job, and call the two states Managed and As-is; this draft uses "Read-only" as the user-facing word for As-is. Shy answered yes (D27).
- A fixed 680 px height is tight on 768 px-tall laptop screens; the folder list scrolls inside and the buttons stay pinned.

**Gaps and drift:** v2 D5 ("up to 1100 px") and D9 (editable photos name) change here; v1 D1 (rail never goes away) and D4 (Rename) change here. v2 and v1 stay as history.

## Answers so far and open questions (numbered from 30)

Answered by Shy on 5 Nov: **30A** show the Read-only / Managed choice on the folder step, default Read-only (D19). **31A** one library per kind; a second place for a kind is a second folder, and "that's why we can create collections" (D17). **33** yes, switching to Managed later is offered in Edit (D27).

**34A** square tiles for both Add library steps with hover cards (D23, D24). **35A** converting keeps titles available, and the Review says one may briefly fail while its file is renamed (D27).

Open:

32. **Answered (Shy, 5 Nov): Vault libraries display like any other library.** Only administrators can see the Vault area, so they are ordinary rows in the one table (also under All, with the Vault area's own icon and color), and a Vault chip filters to them. Both the rows and the chip appear only while an administrator has the Vault on and unlocked. (My reading of "just displays like any other library"; tell me if you meant no Vault chip.)

All questions are answered. Ready for Shy to lock.

## Plain English Summary

Library names stop being something you type: Movies is Movies, Music is Music, and photos and videos are one fixed choice. If a library has more than one folder, each folder keeps a name (filled in for you) and you can filter the library by folder so you can tell them apart. Everywhere a folder appears, it now says whether Library only reads it or keeps it tidy. Every "add a folder" action opens the same window at the same size. The left list on Settings → Libraries goes away so the table can use the whole page, with the Watch, Listen, Read, View chips as the filter.
