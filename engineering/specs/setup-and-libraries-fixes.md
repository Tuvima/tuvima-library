# Spec: Setup and Libraries fixes (local check 2026-11-11)

> **Status:** Built, awaiting your Local checks (L1 to L8 in `engineering/validation/batch.md`; built 2026-11-12) · **Version:** 1 · **Last updated:** 2026-11-11 (locked by Shy, 23:55Z, "agree on all questions") · **Related docs:** [First-run setup rework](first-run-setup-rework.md) (v1, amended here: Vault step navigation and wording) · [Libraries v3, fixed names and one folder picker](libraries-fixed-names.md) (amended here: D23 and D24 replaced, D21 layout) · [Libraries page v2 and the folder picker](libraries-and-folder-picker.md) (the "In your collection" card) · [View folders](archive/view-folders-in-libraries.md) (v4 D1 amended: one Photos and videos row) · [Design system](../design/design-system.md) · Issues #854, #855, #856, #857, #858, #859, #860, #861, #865, #696, #867 · Mockups: `../design/mockups/setup-and-libraries-fixes.html`

> Source: the local check of 2026-11-11. Shy's answers: rows for both Add library steps (1A), folder status icons (2A), keep the Import folder row (3A), and row icons in the same color as the card (23:53Z). Nothing here is built. Three neighboring plans own parts this spec only uses: the shared look plan owns dropdowns, dialogs, menus, tooltips and the font sizes; the Editor and Match plan owns the shared form row (label, control, message); the Activity plan owns the one "need review" number.

## Plain-English product walkthrough

**Today.** Setting up libraries mostly works, but the local check found rough edges at almost every step. A Cancel button is cut off. The Vault step tells you to "go back" to a step you have not reached, and once you pass it you can never return. Add library shows uneven tiles with a scroll bar that scrolls nothing and hover cards that repeat the tile and sometimes hide behind the window. The Add folder window for photos shows the drives, but opening one appears to show nothing. The Libraries list shows libraries you never set up, the summary card does not match the approved picture, and the Folders pop-up wraps its text.

**After this spec.**

1. **Cancel reads "Cancel"** beside Network name on the first setup step (#854, WP1).
2. **The Vault step points forward**: "On the next step, press Add library and choose Vault to add a private library." (#855, WP1).
3. **Back reaches the Vault step again.** It stays in the step list for the person it applies to. If you said Not now, you see the offer again and can change your mind. If you turned it on, you see "Your Vault is on" and carry on (#856, WP2).
4. **Add library is a short list of full-width rows**, for the area and then for the kind. Each row has its icon, name and one line saying what belongs there, so there is nothing to hover for. Vault is simply a fifth row. No scroll bar unless the list is really longer than the window (#859, WP4).
5. **The folder window uses its whole height for the folder list.** The header says what you are adding to ("View · Photos and videos"). The Read-only or Managed choice sits directly under the list, and any message (such as "You can't upload photos and videos until you add a Managed folder.") has its own line under the choice. A folder with nothing inside says so (#860, #861, WP3).
6. **The Folders pop-up keeps each folder on one line**, with small icons in place of the words "Online", "Read-only" and "Managed" (#857, #858, WP5).
7. **One "Photos and videos" row**, not separate Photos and Personal videos rows. **Libraries with no folders are not listed** anywhere until a folder is added; Add library is how one appears (#865, WP6).
8. **The "In your collection" card matches the approved picture**: one equal cell per kind with the icon above the count and the name, each kind in its own color (#696, WP7).
9. **The Settings menu shows a small count next to Libraries** when items are waiting in Needs Review, for administrators only (#867, WP8).

**What stays the same.** Step order, the Vault's PIN rules, library names fixed by kind, friendly folder names, the Read-only and Managed meanings, the check that runs once when you press Add, the Libraries page order (header, card, filter row, table), the photo and video pages inside View, and how files are filed.

**Journeys.**
- *First run, wants a private library.* Turns on the Vault, reads "On the next step...", presses Continue, presses Add library, sees five rows, picks Vault.
- *First run, unsure about the Vault.* Presses Not now, adds Movies, presses Back, lands on the Vault step, turns it on, continues.
- *Adding a phone dump.* Add library, View, Photos and videos. The window lists the drives; opening one fills the window with its folders. Picks a folder, leaves Read-only, reads the upload line, presses Add folder. One "Photos and videos" row appears.

**Acceptance (what Shy can observe).** A1 Cancel is fully readable at 800 px wide. A2 the Vault "on" screen never says "go back". A3 Back from Libraries lands on the Vault step. A4 Add library steps show rows of one height and no scroll bar with five areas. A5 in the folder window at 1440 by 900 at least five folder rows are visible at once and the upload line is not covered. A6 no folder name or status wraps in the Folders pop-up. A7 a fresh install lists no libraries until one is added. A8 the card cells line up and each kind has its color. A9 the count beside Libraries equals the Needs Review tab.

## 1. Goal

Make first-run setup and the Libraries screens work cleanly from the first click: nothing cut off, nothing hidden, no dead ends, and only the libraries a person actually set up.

## 2. Scope

**In scope**
- The ten issues above, plus #858 (folder status icons), in first-run setup, Add library, the folder window, the Libraries list, the "In your collection" card and the Settings menu.

**Out of scope**
- Tooltip, dropdown, dialog and menu styling and the font sizes (shared look plan). This spec states where a tooltip is needed and relies on that plan for how it looks and stacks.
- The shared form row for label, control and message alignment (Editor and Match plan).
- How the "need review" number is worked out (Activity plan, its D28).
- The Photos, Personal videos and Documents pages inside View, and how files are sorted into photos and videos.
- Whether a Managed View folder should be the folder you picked instead of a new folder in View's storage (open elsewhere).
- Phone editing. Adding libraries and folders stays desktop-only.

## 3. Decisions made

- **D1. Cancel keeps its full width** beside Network name and stays a plain outlined button. Red is only for destructive actions (#854). Reason: Reliability of the look.
- **D2. The Vault "on" screen points forward** (#855): "On the next step, press Add library and choose Vault to add a private library." It says "Vault", not "the Vault tab", because Add library shows a Vault row.
- **D3. The Vault step can be reached again with Back** (#856). For the person it is offered to, the step stays in the step list for the whole of setup. After Not now it is marked skipped and shows the offer again. After it is turned on it shows "Your Vault is on" with Continue, never a second turn-on and never a PIN prompt. Turning the Vault off is a Settings job, not a setup one. This replaces the earlier rule that Not now removes the step from the list. Reason: Ease of use; a choice made in setup should be changeable in setup.
- **D4. The Folders pop-up uses icons and one line per fact** (#857, #858), as in the folder status mock: a green check for a folder Library can reach, an amber triangle when it cannot, a grey lock for Read-only, a purple pencil for Managed, and Copy. Each icon has a tooltip and a spoken name. The folder's friendly name and its path are one line each; a long path is shortened in the middle and its tooltip shows the whole path. When a folder cannot be reached, the reason stays as words under the path. (Shy, 2A.)
- **D5. Add library steps are full-width rows** (#859). Step 1 (area) and step 2 (kind) each show one row per choice: the icon on its tinted square in the area's color, the name, and one line that says what belongs there with two or three examples. A kind that already has a library is greyed, with "Add a folder to Movies instead" as a button at the end of its row. The Vault row adds "Locked behind its own PIN. Hidden from Kids profiles." in its line. There are no hover cards and no tooltips on these rows, because the row already says it. The list scrolls only when it is longer than the window; no space is reserved under it. Rows still come only from the kind list in config, so a new kind is one new entry. This replaces Libraries v3 D23 (square tiles) and D24 (hover cards) and returns to setup rework D7. The Change kind and file-drop windows use the same rows. (Shy, 1A.) Reason: Ease of use and Extensibility; five or fifteen rows look equally tidy.
- **D6. The folder window gives its height to the folder list** (#860, #861). Cause found in the code: the folder browser inside Add library and Add folder is given a fixed height of about 390 px inside a 680 px window. The friendly name, the toolbar, the "you are inside" line and an always-reserved empty message area use almost all of it, leaving the list about 40 px in the View window, which is the header row and nothing else. The folders are loaded but cannot be seen. The Engine answers correctly, which is why its log is clean. New layout, top to bottom: header; friendly name; the browser (locations on the left; toolbar, "you are inside" line and list on the right); the Read-only or Managed choice as one compact row; one message line; the buttons. The list takes every pixel the fixed parts do not need and never shows fewer than five rows in the 680 px window. On a screen too short for that, the window body scrolls instead of squeezing the list. No empty space is reserved for messages. This applies to every caller of the one folder window. Reason: Reliability.
- **D7. Messages in the folder window have one home**: the line under the handling choice. It carries the check result (Looks right, Heads up, Already used), the Managed warning, and the View upload line. The grey "Your files are never changed." footer line is dropped while the handling choice is shown, since the Read-only option says the same. Nothing is ever drawn behind the buttons.
- **D8. The folder window header names the target**: a small line above the title with the area and kind ("View · Photos and videos", "Watch · Movies"), then "Add folder". Add library keeps its step title and shows the same line.
- **D9. An empty folder says so**: "This folder has no folders inside. You can still choose it." A folder Library cannot open says "Library can't open this folder." and offers Back.
- **D10. One "Photos and videos" row** (#865, decided by Shy 2026-11-11). Photos and Personal videos appear as one row in the Libraries list, the setup list and the "In your collection" card, with one Edit window listing all their folders. The name is written "Photos and videos" everywhere, including Add library (today "Photos and Videos"). Documents stay their own row. This amends View folders v4 D1.
- **D11. A library with no folders is not listed** (#865, decided by Shy 2026-11-11). It applies to the Libraries list, the setup list, the card, the area chip counts, the footer count and any other Settings list of libraries. Removing a library's last folder asks once and says the library will leave the list; nothing is deleted from disk. With no libraries at all, the page shows "No libraries yet" with Add library. The Import folder row is the one exception: it stays listed even before a folder is chosen, because that row is where it is chosen (Shy, 3A; Libraries v2 D15 unchanged).
- **D12. The "In your collection" card is a grid of equal cells** (#696): icon above, then the count, then the kind's name, left-aligned and wrapping to more rows when needed. Each kind uses its own color from the design system (movies, TV, music, audiobooks, books, comics, documents); a kind with no color of its own (YouTube, Videos, Other audio, Other reading, Photos and videos) uses its area's color. **The icon on each row of the Libraries list and the setup list uses the same color as its cell in the card** (Shy, 2026-11-11: Audiobooks was green in the card and yellow on the row). The area tag keeps the area's color. Add library rows keep the area color, because there the choice is still the area.
- **D13. The Settings menu shows the review count beside Libraries** (#867): a small amber pill at the end of the row, administrators only, hidden at zero, "99+" above 99, spoken as "Libraries, 8 items need review". It is the same number as the Needs Review tab and the banner (Activity plan D28). The row still opens Libraries.

## 4. UI behaviour

Editing is desktop-only, so the Add library and folder windows have no phone or TV form. Phones and tablets see the lists read-only, as today.

| Screen | Normal | Empty | Loading | Error | Partly ready | Phone / TV |
|---|---|---|---|---|---|---|
| Setup, Vault step (revisited) | Offer again after Not now; "Your Vault is on" after turning on | n/a | as today | turn-on failure shows the reason, as today | n/a | step is not offered on phones (unchanged) |
| Add library, steps 1 and 2 | Full-width rows | An area with no kinds left: "Every kind here already has a library" with the greyed rows | rows appear at once (config) | "Could not load" with Try again | Vault row only when the Vault is on and unlocked | not available |
| Folder window | List fills the window | "This folder has no folders inside. You can still choose it." | a thin progress bar in the list area; the last list stays until the new one arrives | "Library can't open this folder." or "Could not reach the server" with Try again | Check result on the message line after Add | not available |
| Folders pop-up | One line per fact, icons | n/a (only shown with folders) | n/a | unreachable folder: amber triangle and the reason in words | n/a | bottom sheet, same rows |
| Libraries list | Only libraries with folders; one Photos and videos row | "No libraries yet" with Add library; per chip "No Listen libraries yet" | skeleton rows | "Could not load" with Try again | scanning rows as today | read-only cards |
| "In your collection" card | Equal cells | card hidden when nothing is counted | hidden until counts arrive | hidden | a kind still importing shows its count so far | two cells per row on phones |
| Settings menu | Count pill beside Libraries | no pill at zero | no pill until the number is known | no pill | n/a | same pill; not shown to non-administrators or Kids profiles |

## 5. Data / model changes

None to stored data. The Libraries list gains two display rules (combine Photos and Personal videos; leave out libraries with no folders). The Vault setup step can be opened again after it was decided; its decision is still stored the same way. The review count is the number the Engine already gives the Needs Review tab.

## 6. Work packets

### WP1: Setup wording and the Cancel button
- **What it does:** D1, D2.
- **Files likely touched:** `src/MediaEngine.Web/Components/Setup/SetupWelcomeStage.razor.css` (`.setup-server-form__host`), `src/MediaEngine.Web/Resources/SharedStrings.resx` and `.fr/.de/.es` (`SetupVaultOffer_AddFromLibraries`), setup tests.
- **Depends on:** none
- **Size:** Mechanical
- **Done when:** a test shows the Cancel button cannot shrink below its label; the Vault "on" note reads the D2 sentence in all four languages; `Fixes #854`, `Fixes #855`.

### WP2: Back reaches the Vault step
- **What it does:** D3.
- **Files likely touched:** `src/MediaEngine.Web/Components/Pages/SetupPage.razor` (`NavigableSteps`, `VaultOpen`, `ShowVault`, `GoBackAsync`), `src/MediaEngine.Web/Components/Setup/SetupVaultOffer.razor` (open on the "on" screen when already passed), the setup stepper, `SetupVaultEligibility.cs`, tests. Engine step rules only if a decided step cannot be re-decided (to be confirmed).
- **Depends on:** WP1 (same strings)
- **Size:** Standard
- **Done when:** tests show Back from Libraries selects the Vault step after both Not now and turn-on; after Not now the offer can be accepted; after turn-on no PIN field or turn-on button is drawn; a person the Vault is not offered to still never sees the step; `Fixes #856`.

### WP3: Folder window layout
- **What it does:** D6, D7, D8, D9. Starts by reproducing #860 in a rendered browser: open Add folder for Photos and videos on a small generated library and measure the folder list's height (expected today: under one row).
- **Files likely touched:** `src/MediaEngine.Web/Components/Shared/AppBrowserFrame.razor.css` (`.app-browser--inline` fixed height), `ServerFolderPicker.razor` and `.razor.css` (reserved notices area, footer, handling row, message line), `Settings/Libraries/AddViewFolderDialog.razor` and `.css`, `CreateLibraryDialog.razor` and `.css`, `LibraryEditDialog.razor`, `wwwroot/app.css` (`.app-browser-host`), a rendered-layout check beside `tools/validate-touch-targets.js`, bUnit tests. The supporting-files browser shares the frame and must not change.
- **Depends on:** none
- **Size:** Complex
- **Done when:** the rendered check shows at least five folder rows visible in Add folder (View) and in Add library step 3 at 1440 by 900 and 1280 by 720; no element overlaps the footer; the header shows "Area · Kind"; an empty folder shows the D9 sentence; the supporting-files browser's own tests still pass; `Fixes #860`, `Fixes #861`.

### WP4: Add library rows
- **What it does:** D5.
- **Files likely touched:** `Settings/Libraries/CreateLibraryDialog.razor` and `.css` (`.create-library__tiles`, the 140 px bottom padding), `LibraryTile.razor`, `LibraryPeek.razor` (no longer used by Add library), `LibraryKindCards.razor`, `config/library_kinds.json` (`explainer`, `examples` as the row line), tests including the fake extra kind test.
- **Depends on:** WP3 (same window)
- **Size:** Standard
- **Done when:** steps 1 and 2 render one full-width row per choice at one height; no hover card or tooltip is attached to a row; with five areas the body has no scroll bar at 680 px; a test with 15 kinds scrolls inside the window; `Fixes #859`.

### WP5: Folders pop-up
- **What it does:** D4.
- **Files likely touched:** `Settings/Libraries/LibraryFoldersPopover.razor` and `.css`, `FolderHandlingWords`, tests. Uses the shared tooltip as the shared look plan delivers it.
- **Depends on:** none
- **Size:** Standard
- **Done when:** name, path and status never wrap at 1024, 1280 and 1440; every icon has a tooltip and an accessible name; an unreachable folder shows its reason as text; `Fixes #857`, `Fixes #858`.

### WP6: One Photos and videos row; hide libraries with no folders
- **What it does:** D10, D11.
- **Files likely touched:** `Settings/Libraries/LibraryListing.cs`, `LibrariesOverview.razor`, `Setup/SetupLibrariesTable.razor`, `LibraryAreaChips.razor`, `LibraryCatalog.cs` (`PhotosAndVideosLabel`), `ViewKindEditDialog.razor`, `LibraryEditDialog.razor` (last-folder confirmation), strings, tests.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a fresh configuration lists no rows and shows "No libraries yet"; folders under Photos and under Personal videos show as one "Photos and videos" row with one Edit window; removing the last folder confirms and the row disappears; chip and footer counts count only listed libraries; `Fixes #865`.

### WP7: "In your collection" card
- **What it does:** D12.
- **Files likely touched:** `Settings/Libraries/LibrariesSummary.razor` and `.css`, `LibraryKindLook.cs` (one color per kind, used by the card and the row icon), `LibrariesOverview.razor`, `Setup/SetupLibrariesTable.razor`, tests.
- **Depends on:** WP6
- **Size:** Standard
- **Done when:** cells are equal width with icon, count and name stacked; each kind renders with its media color token or its area color; a test shows a row's icon and its card cell resolve to the same color for every kind; Mock and Live match at 1024, 1280, 1440 and phone; `Fixes #696`.

### WP8: Review count in the Settings menu
- **What it does:** D13.
- **Files likely touched:** `Services/Navigation/SettingsMenuBuilder.cs`, `Components/Settings/SettingsNavRow.razor`, `Models/ViewDTOs/SettingsNav.cs`, `wwwroot/app.css` (row styles), tests.
- **Depends on:** none (reads the number the tab already uses; the Activity build makes it the one number)
- **Size:** Standard
- **Done when:** an administrator with 8 items waiting sees "8" beside Libraries and the row's accessible name says so; zero shows nothing; a household member and a Kids profile see nothing; `Fixes #867`.

### WP9: Docs, older-spec notes, batch list
- **What it does:** updates the public setup and Libraries pages in `tuvima-docs`; adds a version note to the four amended specs; appends the Local checks below to `engineering/validation/batch.md` with their issue numbers; posts closure notes.
- **Files likely touched:** `tuvima-docs` `content/products/library/` setup and libraries pages, `engineering/specs/*.md` notes, `engineering/README.md`, `engineering/validation/batch.md`.
- **Depends on:** all
- **Size:** Mechanical
- **Done when:** docs build passes; the batch list has rows L1 to L8.

Waves (one build thread, two packets at a time): 1 = WP1, WP3. 2 = WP2, WP4. 3 = WP5, WP6. 4 = WP7, WP8. 5 = WP9.

## 7. Local checks

On a fresh data folder and a small test library, at 1024, 1280, 1440 and phone width where the screen exists there, Mock beside Live.

- **L1 (#854).** Setup step 1 at about 800 px wide: press Change beside Network name. Cancel is fully readable. Fail: any clipping.
- **L2 (#855, #856).** Turn the Vault on: the note says "On the next step...". Continue, then Back: "Your Vault is on". Second run: Not now, Continue, Back: the offer is there and can be accepted.
- **L3 (#859).** Add library with the Vault on: five equal rows, no scroll bar, nothing to hover. Pick Watch: kind rows, a kind that exists is greyed with its button.
- **L4 (#860, #861).** Add library, View, Photos and videos on the real drives: opening a drive lists its folders, at least five visible; the header says "View · Photos and videos"; with Read-only picked the upload line is fully visible; a folder with nothing inside says so. Repeat for Movies and from Edit, Add folder.
- **L5 (#857, #858).** Open Folders on a library with a long network path, a Managed folder and an unplugged drive: no wrapping, icons with tooltips, the offline reason in words, the full path in the tooltip.
- **L6 (#865).** Fresh install: no rows. Add a Photos and videos folder: one row. Remove it: the row goes. Settings shows the same.
- **L7 (#696).** The card beside the mock: equal cells, a color per kind, and each row's icon in the same color as its cell (Audiobooks green in both).
- **L8 (#867).** With items in Needs Review, the Settings menu shows the same number as the tab; resolve them all and it disappears; a non-administrator never sees it.

## 8. Risks and open questions

- **Risks.** The cause of #860 comes from reading the code and working out the sizes, not from seeing it on screen; WP3 reproduces it first and stops to report if the list is not the problem. Hiding empty libraries removes the "Empty" status as a prompt, so the "No libraries yet" state must be clear. The folder window, the shared look plan and the Editor plan touch the same window; their builds should not run at the same time on those files.
- **Open questions.** None. Q1 to Q3 were answered by Shy on 2026-11-11 (1A, 2A, 3A) and are recorded in D4, D5 and D11.

## 9. Gaps and drift

- **Built differently:** Libraries v3 D23 asked for square tiles and was built; Shy's 11 Nov decision goes back to rows (D5): change code. View folders v4 D1 lists Photos and Personal videos separately and was built; Shy decided one row on 11 Nov (D10): change code.
- **Built and broken:** the fixed-size folder window (v3 D21) left the embedded list almost no height (D6): change code.
- **In a spec but not built:** the v2 "In your collection" card with a large colored icon per kind (D12): change code.

## Plain English summary

This is a clean-up of setup and the Libraries screens. The Add folder window gets fixed so you can actually see and pick folders; that fault was the window leaving no room for the list, not the Engine failing. Add library becomes a simple list of rows, the Vault step can be revisited and points the right way, the Libraries list shows only what you set up with photos and videos as one row, the summary card matches your picture, and the Settings menu shows how many items need review. Approved by Shy on 2026-11-11; the build starts when Shy says "build it".
