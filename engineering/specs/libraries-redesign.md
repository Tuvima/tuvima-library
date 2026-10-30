# Spec: Libraries redesign (two-pane Libraries, honest status, one folder pop-up)

> **Status:** Built, awaiting your Local checks · **Version:** 1 · **Last updated:** 2026-10-30 · **Related docs:** [Product overview](../product/overview.md) · [Library pages](library-pages.md) (v15, Built, awaiting Local checks) · [Library setup and add-folder flow](library-setup-add-folder.md) (v2, In progress) · [Simpler library access](library-access.md) (v3, Locked) · [One shared look](one-shared-look.md) · [Universal editor](universal-editor.md) (Match panel) · [Vault sections and Move to Vault](vault-sections-and-move.md) · Mockups: [`engineering/design/mockups/libraries-redesign.html`](../design/mockups/libraries-redesign.html)

> Written 2026-10-30 after Shy's review of the built Libraries pages: "the UI has gotten better, but the UX is still very bad." It was checked against the code on main (commit 90283826) and replaces piecemeal fixes with one redesign of the Libraries area in Settings, the library page, Add library, Add folder and the setup step. Locked by Shy on 2026-10-30 ("Agree lock it"), accepting every recommendation (Q1 to Q6: A). It supersedes parts of Library pages v15, Library setup and add-folder v2 and Library access v3 (section 9).

> *Companion note (2026-10-30):* [First-run setup v3](../../../engineering/specs/first-run-setup-flow.md) is Locked. It reuses this spec's components in setup mode: D13 (setup mode) is adopted as its D10, it adds a Cards / List switch to the shared overview (shared with Settings), and its WP4 depends on WP1, WP5 and WP7 here. Screen 8 of the mockups is replaced by the setup mockups.


## Plain-English product walkthrough

**What Shy saw, and why it happens (root causes read from the code; nothing was run).**

1. **"Needs review" is a number pill with no details, and opening it shows no files.** The pill counts *files the Engine could not identify* (its "unmatched files" list), but the link opens *Needs Review*, which is a different list: titles the Engine did identify but is not sure about. The two never overlap, so the filtered Needs Review page is empty. The unidentified files themselves have no page at all; they exist only as a count with "Retry now" and "Keep all as they are" buttons on the old folders panel.
2. **Watch / Listen / Read / View vanish inside a library.** The area buttons belong to the overview component. A library's page is a separate level that draws only a breadcrumb in the Settings frame, so there is no persistent structure to come back to.
3. **The Advanced tab "shows basically everything with no context" and repeats File handling.** The Folders tab embeds the previous generation's folders panel, which still carries the old global "Advanced: file handling and naming" section with one tab per media type for the whole server. The library's own File handling tab was built beside it instead of replacing it, so the same settings exist twice.
4. **File handling "is like a mobile view only".** Its four cards are stacked in one column and capped at 720 px wide, so on a 1440 px screen most of the width is empty.
5. **It is not obvious that folders and library groups are clickable.** Area buttons look like a filter; rows show a chevron only on hover; folder rows expand on click instead of opening anything.
6. **Add folder takes too many steps.** Add library is a two-screen dialog whose second screen has a Browse button that opens a *third* window (the folder picker). Add folder opens the picker as a dialog. So the same job looks different in two places and takes three windows in one of them.
7. **"i" bubbles appear behind the dialog.** The picker's result card and the dialog both carry info tips drawn by different layers.
8. **The folder picker "is still slow".** Likely cause from the code: every click on a folder name sends a check to the Engine that, before answering, probes write access, walks the whole folder tree to see whether it holds any file, runs the phone-footage detector over it, and counts supported files up to 10,000 or 6 seconds. On a network drive each click can take several seconds before the result card appears. Listing the folder names themselves also reads each subfolder's attributes one by one. *To be confirmed against the Engine log on Shy's PC (`.tmp/folderdiag/engine-captured.log`) before packet 6 is built.*
9. **Setup differs from Settings.** Setup already reuses the overview component in a "Simple" mode, but it is a different rendering (no pages, rows that at one point navigated into Settings and broke setup, bug F75). The code now blocks that navigation; the local check must confirm it.

**What it feels like after this spec.**

1. **Libraries is one page with two panes.** On the left, a rail lists every library under its area (Watch, Listen, Read, View, and Vault for an administrator who has unlocked it), each with a status dot. On the right, either the overview table (all libraries, grouped by area) or the library you clicked. The rail never disappears, so Watch, Listen, Read and View are always one click away, and it is obvious what opens: the rail rows highlight, the table rows highlight and end in a chevron. Area tabs are gone.
2. **Status says exactly what it is and goes exactly there.** A library shows one status word: Healthy, Scanning, Empty, Folder offline, or an amber count that names its kind: "12 files not identified" opens the library's **Unidentified files** tab; "3 need review" opens Needs Review filtered to the library. The two numbers are never mixed.
3. **A library page has one header, one attention strip, and three tabs.** Header: icon tile, name with a pencil to rename, content type, counts, status; actions Scan now, Add folder, Add files, "...". Under it, only when needed, a strip of plain sentences each with its one button ("12 files could not be identified · Review them", "Folder offline: … · Check again"). Tabs: Folders, File handling, Activity, plus Lookup where it applies and Unidentified only while there are such files. **Advanced is removed.** Its server-wide rules (duplicate default, write-back schedule, embed artwork) move to one card at the bottom of the overview; per-library naming already lives in File handling.
4. **File handling uses the width**: the four cards sit in two columns from 1180 px up, one below; explanations are each card's subtitle, not an "i"; one sticky Save/Discard bar.
5. **Unidentified files get a real list**: file name and folder, the reason in plain words (no match found, no episode pattern in the name, provider timed out, sample file), when it was last tried, and actions: Identify (opens the universal editor's Match panel so matching an episode to a show works the same as on an item page), Try again, Keep as it is, Change what this folder is, Remove from library. Bulk Try again / Keep all as they are stay at the top.
6. **Add library is two windows, one question each, and the second window *is* the folder pop-up.** Step 1 "What is it?": a row of area tabs (Watch, Listen, Read) pre-selected to where you clicked Add, with that area's three or four content types below (a type that already has a library is shown greyed with "Add a folder to it instead"). Step 2 "Where is it?": the folder browser, with the live check under the folder list and the library name as a pre-filled line in the footer. Add library saves, starts the scan and lands on the new library's page. Five clicks from the Libraries page; no Browse button; no third window; no "go to…" question; no toast.
7. **Add folder is the same pop-up** without the step label and name field. Three clicks: the folder, Add folder, done; the new row glows for two seconds.
8. **The pop-up is fast by design.** Folder names appear first from one cheap listing. The check of the selected folder runs afterwards, shows "Checking…" in the result card, is capped at a few seconds, and never blocks browsing. No "i" bubbles exist in the pop-up or the dialog, so nothing can hide behind them.
9. **Setup shows the same table and the same dialog in a setup mode**: no rail, no library pages, no Import folder, rows with an Add folder button instead of a chevron, Continue as the only way out. A guardrail test fails if setup grows its own copy.
10. **Narrower screens.** Below 1180 px the rail folds into a strip of library chips grouped by area above the pane. Phones keep today's read-only list and page.

**What stays the same:** the Engine's folder checks and refusals, Read-only protection of originals, Managed folders and the visible organising job, the import destination, Add files copying only, the Recycle bin, the Vault as a fifth area with its own access, the View kinds on these screens, desktop-only editing, Settings being configuration and never a correction workspace. "Identify" reuses the universal editor; this spec adds no editor of its own.

**Representative journeys.**

- *A new TV folder.* Rail: TV Shows → Add folder → open Z:\TV Shows\Drama → Add folder. The row glows, "Scanning" shows in the rail dot and the header, and the person is still on the TV Shows page.
- *Twelve files nobody can find.* The rail shows an amber 12 beside TV Shows from any Libraries page. Click it → the Unidentified tab → each file says why → Identify one episode, Keep a home recording as it is, Remove the sample file.
- *First install.* Setup's "What do you have?" shows the same table; Add library → TV Shows → the folder → Add library; the row appears; Continue.

**Observable acceptance criteria.**

- **A1.** From any Libraries page the rail shows every area and library; clicking a rail row opens that library; the area headings never leave the screen at 1180 px and wider.
- **A2.** A library with unidentified files shows "N files not identified" in the table and the rail; clicking it opens that library's Unidentified tab listing exactly N files with a reason each. A library with Needs Review items shows "N need review" and opens Needs Review filtered to it, listing N items.
- **A3.** No library page contains an "Advanced" section; a search of the Dashboard code finds no `LibraryAdvancedSettings`.
- **A4.** The File handling tab renders two columns at 1440 px and one at 800 px; its cards have no "i" bubble.
- **A5.** Add library: step 1 shows area tabs with the chosen area's content types and no scrolling at 1440 px; step 2 is the folder pop-up with the name in its footer; there is no Browse button and no third window. Add library with an existing folder is five clicks from the Libraries page.
- **A6.** Add folder opens the identical pop-up (same title pattern, same list, same result card, same button wording) without a name field.
- **A7.** In the pop-up, folder names appear before any check result; while a check runs the card says "Checking…" and clicking another folder is not blocked; on a local drive the names show in about a second.
- **A8.** Neither the dialog nor the pop-up contains an "i" bubble.
- **A9.** Setup renders the shared overview and dialog in setup mode; no row or heading navigates; a guardrail test enforces both.
- **A10.** Add folder and Add library never show a toast or a "go to…" question; a new row glows for about two seconds.

## 1. Goal

Make Settings > Libraries something a person understands at a glance and trusts: you can always see where you are and what else exists, every number leads to the thing it counts, adding a folder is one window, and nothing is repeated in two places. Reason: Ease of use and Reliability (confidence that an action did something).

## 2. Scope

**In scope**
- The Libraries overview, rail, library page header and tabs, Unidentified files tab, File handling layout, server-wide rules card.
- Add library (two steps, the second being the folder pop-up), Add folder (same pop-up), the pop-up's names-first, check-second behaviour and the speed fix once the log confirms the cause.
- Removing the Advanced section and the old folders-panel Unmatched bar.
- Setup's Libraries step using the shared components in setup mode, with the guardrail.
- Honest status words and their links (unidentified vs needs review).
- Companion version notes on Library pages v15, Library setup and add-folder v2, Library access v3 and Simpler access v5 (section 9).

**Out of scope**
- The universal editor itself and the Match panel's behaviour (own spec); this spec only opens it from Identify.
- The Vault's inner workings, Move to Vault, PINs.
- View's own storage, people and sharing rules (unchanged; View kinds only appear in the rail and table).
- The Import folder page (unchanged, reached from the rail's last entry).
- The Settings left menu and top bar (settings-flat-menu, built).

## 3. Decisions made

Locked by Shy on 2026-10-30. The questions marked **Q** were answered with the recommended option; the answers are recorded in D19.

- **D1. Libraries is a two-pane page: a persistent rail of areas and libraries on the left, the overview or a library on the right.** Replaces the area segmented control (Library pages D2) and the breadcrumb-only library level. The rail groups by area, shows one status dot per library, and the Vault group exists only while an administrator has the Vault unlocked (Library access D6, Videos D45). Reason: Ease of use (always know where you are, see every library's health from anywhere) and Uniformity. **Q1.**
- **D2. The overview is one table grouped by area, not four filtered tables.** Columns: Name, Content type, Folders, Titles, Status, chevron. Search and the status filter stay; the Access column is gone (Library access D1). Group headings are plain text with the count. Reason: nothing to lose, nothing to toggle.
- **D3. Two kinds of attention, never mixed.** *Unidentified files* (the Engine's unmatched-files list) show as "N files not identified" and open the library's Unidentified tab. *Needs Review items* (the review queue) show as "N need review" and open Needs Review filtered to the library (the existing `?library=` filter). The overview reads both counts from the Engine. Reason: Reliability; this is the root cause of the empty page.
- **D4. The library page has one header and an attention strip.** Header: area-coloured tile, name with Rename, content type, folder and title counts, one status word; actions Scan now, Add folder (primary), Add files, and "..." (Rename, Move to Vault for administrators, Remove library). The strip appears only with something to do and each line has one button (Review them, Check again, Keep going). Reason: Shy's style rules (warning bubbles, few controls).
- **D5. Tabs are Folders, File handling, Activity, plus Lookup where the kind has lookup settings and Unidentified only while the count is above zero.** The Advanced section (`LibraryAdvancedSettings` inside the folders panel) is removed. Its server-wide rules (duplicate default, write-back schedule, embed artwork, same-name rule) move to one "Rules for every library" card at the bottom of the overview; per-library naming stays in File handling (Library pages D6). Reason: Maintenance (one place per setting) and Shy's complaint.
- **D6. The Unidentified tab lists the files with a reason, last tried, and actions.** Per row: Identify (opens the universal editor's Match panel in a dialog for this file, desktop only, `EditorDeviceRule.CanEdit`), Try again, Keep as it is, Change what this folder is, Remove from library. Bulk: Try again for all, Keep all as they are. The reason text comes from the Engine's recorded cause and is worded per cause. Reason: the number finally leads somewhere; no new editor is built.
- **D7. File handling is two columns from 1180 px, one below; card subtitles replace "i" bubbles; one sticky Save/Discard bar.** Content of the four cards is unchanged (Library pages D6). Reason: Ease of use at desktop widths.
- **D8. Add library is two steps, one question each, and step 2 is the folder pop-up.** Step 1 "What is it?" has a row of area tabs (Watch, Listen, Read) at the top, pre-selected to the area the person clicked Add from (Watch from the toolbar), with only that area's three or four content types below so nothing scrolls (Shy, 2026-10-30: one list of every area is too much); its first available type is preselected; a type that already has a library is greyed with "You already have a Movies library. Add a folder to it instead." (catch-all kinds stay addable, Library pages D22). Step 2 "Where is it?" is the folder browser with the live check beneath and the name pre-filled in the footer ("You can rename it later."). Inside the Vault, step 1 lists Movies, TV Shows, Music, Books, Adult content, Other (Library access D6). **View** is a fourth tab if Q6 is answered A: Photos, Personal videos, Documents, then the same pop-up with View's extras in the footer (whose space; storage location only when missing), adding a folder to that View library rather than creating one. Revises Library setup and add-folder D0 and D0d (which kept a Browse button and a separate pop-up). Reason: fewer windows, same one-question rhythm Shy asked for. **Q2.**
- **D9. Add folder is the same pop-up** (title "Add a folder to Movies", no step label, no name field; for a Vault library the footer keeps its one "Let Library manage this folder" line). Revises add-folder packet 1 only in wording. Reason: one thing to learn.
- **D10. Names first, check second, nothing blocks.** The pop-up lists folders from one listing call; the selected folder's check runs afterwards, shows "Checking…", and is capped (file count stops at 10,000 or a few seconds and says "10,000+"); clicking another folder cancels the previous check. The Engine's check must not walk the whole tree before answering: the "contains any file" probe and the phone-footage detector are limited to a shallow look or dropped from this call, and the folder listing stops reading per-subfolder attributes when the drive is slow (to be confirmed from the log). Reason: Performance; Shy's "still slow".
- **D11. No "i" bubbles in the dialog or the pop-up; at most one per page elsewhere** (keeps Library pages D14). The two sentences that matter are the pop-up's subtitle: "Library scans this folder and its subfolders. Your files are never changed."
- **D12. No toast and no question after saving.** Add library lands on the new library's page with the scan started; Add folder returns to the table with the row glowing (library setup and add-folder D0, Shy's "no toasts" rule).
- **D13. Setup mode is a flag on the shared components, not a copy.** Setup renders the overview table and Add library with: no rail, no library pages, Add folder per row instead of a chevron, no Import folder line, no Vault group until the setup Vault step turns it on (add-folder v2 D14, D17). Rows and headings never navigate. A guardrail test asserts the component list and the absence of navigation. Reason: F6, F75.
- **D14. Narrow layouts.** Below 1180 px the rail becomes a chip strip grouped by area; between 1180 and 1440 the rail is 220 px. Phones keep the read-only list and library page (Library pages D18). Breakpoints stay 600 / 840 / 1180.
- **D15. Clickable means it looks clickable.** Rail rows and table rows are the only things that open a page; both highlight on hover and show focus; table rows end in a chevron. Folder rows do not open anything and therefore have no chevron; their actions are in "...". Area headings are text (with a quiet Add link). Reason: F33.
- **D16. Library access becomes on/off per household only** (Shy, 2026-10-30, via the coordinator: "a library is on/off per household; use the Vault to hide things"). The per-profile library ticks of Library access v3 D1 are removed; the household ceiling (D2) stays as the only library switch; Kid's profile and Kid levels (D4, D9, D10) stay; the Vault keeps its own access. This needs a new version (v4) of the Locked Library access spec, written when Shy locks this one. Reason: simplicity; Shy's decision. **Q3 asks about the per-profile area switches.**
- **D17. Adult content stays Vault-only** (Library access v3 D6, Library setup and add-folder D0c). This spec changes nothing there; it only confirms the rail's Vault group is where an Adult content library appears. **Q4 confirms.**
- **D19. Shy's answers (2026-10-30, "Agree lock it").** Q1: the two-pane rail (D1). Q2: Add library is two steps and step 2 is the folder pop-up (D8). Q3: the per-profile area switches are removed with the library ticks; Kid levels and the Vault are the ways to limit what a person sees (D16; Simpler access D1 superseded). Q4: Adult content stays Vault-only (D17). Q5: unidentified files live on a tab of each library (D6). Q6: View is a fourth tab on step 1 (Photos, Personal videos, Documents) leading to the same folder pop-up, whose footer carries View's two extras: whose space ("Shared with everyone" or one person) and, only when it is not set yet, where View keeps its files (D8; add-folder v2 D15 and D16 stand).
- **D18. YouTube key help stays on the Lookup tab as plain numbered steps** (already built: four steps and a link to the Google Cloud console). The library header for a YouTube library adds one quiet line, "Using the shared key · Add your own for descriptions and dates", linking to the tab. Reason: F8 is answered; make it findable.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Overview, normal (desktop) | Rail on the left; one table grouped by area; search, status filter, Add library; "Rules for every library" card below |
| Overview, an area empty | The group heading reads "Read · nothing yet · Add a Read library" |
| Overview, Vault locked or no access | No Vault group, row, count or word anywhere (page source included) |
| Library page, healthy | Header, no attention strip, tabs Folders / File handling / Activity (+ Lookup) |
| Library page, something to do | Amber strip with one sentence and one button per item; Unidentified tab with its count; rail dot amber with the count |
| Unidentified tab, list | Rows with file, folder path, reason, last tried, Identify and "..."; bulk Try again / Keep all |
| Unidentified tab, phone | Read-only list; actions say "Use a desktop to identify files" |
| File handling | Two columns at 1180 px and wider; sticky Save bar when there are edits |
| Add library step 1 | Area tabs on top, pre-selected; that area's content types below; greyed types with a reason; Next disabled until one is chosen |
| Add library step 2 / Add folder | Folder pop-up; result card "Checking…", then "Z:\… is ready · 2,406 video files found" or the amber reason; primary button disabled while checking or refused |
| Slow drive | Names appear first; the card says "Checking… counting video files" and settles on a count or "10,000+" |
| Saved | New library: its page with Scanning; new folder: the row glows two seconds |
| Loading / Error | Existing loading state; "Libraries could not be loaded" with Try again |
| 1180 to 1440 | Rail at 220 px |
| Below 1180 (tablet) | Chip strip above the pane; dialogs centred, one column |
| Phone | Read-only list and page, today's note; TV: unavailable |
| First-run setup | Table in setup mode; Add library in the same two steps; Continue only |

## 5. Data / model changes

- **Per-library Needs Review count.** The overview needs the number of review-queue items per library beside the existing unmatched count (today the libraries read returns only the unmatched count). One new field on the library overview wire type; Contracts tests and fixtures updated.
- **Unidentified file reasons.** The Engine already lists unmatched files per library and records why; the list action is extended to carry a reason code and the last attempt time if it does not already.
- **Folder check split.** The folder validate action gets a "quick" mode (existence, access, overlaps, refusals) and a separate count call; or the count becomes optional on the request. Wire types updated accordingly.
- **Access.** Removing per-profile library ticks drops the `profile_library_hidden` rule and keeps `household_library_hidden` (pre-release: fresh start, no conversion, per project rule 6).
- No change to the libraries file format beyond what Library pages v15 already has.

## 6. Work packets

Built in the cloud; waves one after another; the reviewer runs after each wave. Packets are observable by their done-when checks. Access changes get the security review (Library access rule).

### Wave A: structure and honesty

**WP1. Two-pane Libraries shell (Complex).** Rail with areas, libraries, status dots and Vault group; overview table grouped by area; the library page hosted in the right pane; breadcrumb and title unchanged; chip strip below 1180 px; phone unchanged. Files: `Settings/LibrariesTab.razor`, `Settings/Libraries/LibrariesOverview.razor(.css)`, new `LibrariesRail.razor(.css)`, `Settings/Libraries/LibraryShell.razor(.css)`, bUnit tests. Depends on: none. Done when: A1; the Vault group is absent in a non-administrator render; a test at 1024 px shows the chip strip.

**WP2. Honest status and the two counts (Standard, Engine + Dashboard).** Add the per-library Needs Review count to the overview wire type; status words "N files not identified" and "N need review" with their links; rail dot and header use them; remove the old Unmatched bar from the folders panel. Files: `MediaEngine.Contracts/Settings/*`, `Endpoints/SettingsEndpoints.cs`, `Services/Unmatched/*`, review read service, `Settings/Libraries/LibraryListing.cs`, `LibraryStatusCell.razor`, Contracts tests and fixtures. Depends on: none. Done when: A2 (both halves); fixtures updated.

**WP3. Unidentified files tab (Complex).** New tab listing the Engine's unmatched files for the library with reason, last tried, per-row and bulk actions; Identify opens the universal editor's Match panel for that file (desktop only); attention strip on the header. Files: new `Settings/Libraries/LibraryUnidentifiedTab.razor(.css)`, `LibraryShell.razor`, Engine `UnmatchedEndpoints.cs` and its service (reason and last-attempt fields), editor launcher, tests. Depends on: WP1, WP2. Done when: the tab lists exactly the Engine's unmatched files with a reason each; Keep as it is removes the row and the count drops; Identify opens the Match panel for the chosen file.

**WP4. Remove Advanced; server-wide rules card (Standard).** Delete `LibraryAdvancedSettings` and the `<details>` in `LibraryFoldersPanel.razor`; move duplicate default, write schedule, embed artwork and the same-name rule into one card under the overview table; File handling in two columns with card subtitles and the sticky bar. Files: `Settings/LibraryAdvancedSettings.razor(.css)` (deleted), `Settings/LibraryFoldersPanel.razor`, new `Settings/Libraries/LibraryServerRulesCard.razor(.css)`, `LibraryFileHandlingTab.razor(.css)`, tests. Depends on: WP1. Done when: A3, A4; the write schedule and embed artwork settings still save from the new card.

### Wave B: adding

**WP5. Add library as two steps with the pop-up as step 2 (Complex).** Step 1 with area tabs, pre-selected area, greyed types and reasons; the folder pop-up (`ServerFolderPicker`) restyled to the mockup (locations, history buttons, path bar and search kept; result card under the list); step 2 the `ServerFolderPicker` hosted inside the dialog shell with the name in the footer; Add folder reuses the same host without the name; no Browse button, no "i" bubbles, no toast; new row glows. Files: `Settings/Libraries/CreateLibraryDialog.razor(.css)`, `Shared/ServerFolderPicker.razor(.css)`, `LibraryShell.razor` (Add folder), `LibrariesOverview.razor`, tests. Depends on: WP1. Done when: A5, A6, A8, A10.

**WP6. Fast folder pop-up (Standard, Engine + Dashboard).** Names first; the check runs after selection, cancellable, "Checking…" state; the Engine's validate call stops walking the whole tree before answering (quick mode plus a separate capped count), and the listing avoids per-subfolder attribute reads on slow drives. **Cause confirmed first** from the Engine log on SHY-DESKTOP (`.tmp/folderdiag/engine-captured.log`, reachable by Remote Control at `E:\Source Code\tuvima-library`); the fix is scoped to what the log shows. Files: `Services/Settings/ServerFolderBrowserService.cs`, `Endpoints/ServerFolderEndpoints.cs`, Contracts, `Shared/ServerFolderPicker.razor`, tests. Depends on: WP5 and the log finding. Done when: A7; a test with a slow fake file system shows the names within one listing call and the count arriving later.

### Wave C: setup, access and docs

**WP7. Setup mode and guardrail (Standard).** The shared overview and dialog in setup mode (D13); rows with Add folder; no navigation; guardrail test rewritten for the new component list. Files: `Setup/SetupMediaStage.razor`, `LibrariesOverview.razor`, `tests/MediaEngine.Web.Tests` guardrails. Depends on: WP1, WP5. Done when: A9.

**WP8. Library access v4: household on/off only (Complex, security).** Remove per-profile library ticks and `profile_library_hidden`; keep the household ceiling and Kid levels; the person's page loses its library lists and its area switches (D19); Households page keeps the ceiling list; fresh start, no conversion. Files: Engine access service and storage, `Settings/Access/*`, Contracts, tests, guardrails. Depends on: Shy's lock of this spec and the v4 note on `library-access.md`. Done when: a library unticked for a household is absent for all its profiles everywhere; no per-profile library rule exists; the guardrail fails on a seeded per-profile rule.

**WP9. Docs, companion spec notes, batch (Mechanical).** tuvima-docs Libraries and setup pages; version notes on Library pages, Library setup and add-folder, Library access; Local checks appended to `engineering/validation/batch.md`. Depends on: WP1 to WP8.

## 7. Local checks

On SHY-DESKTOP at 1440 px wide and at about 1280 px, with the small test library first and `Z:\Development\testing\media\real_media` for the real-media rows:

- **L1.** Settings > Libraries: rail with every area and library; click Movies, TV Shows, Books in turn; the rail stays; the breadcrumb follows. Resize to 1280: rail at 220 px; to 1000: chip strip.
- **L2.** On the real TV folder with a few deliberately unmatchable files: TV Shows shows "N files not identified"; click it; the Unidentified tab lists N rows with reasons; Identify one episode through the Match panel; Keep one as it is; the count drops to N-2 in the rail and header.
- **L3.** A library with a Needs Review item shows "N need review" and opens Needs Review listing those items.
- **L4.** No Advanced anywhere; the "Rules for every library" card saves the duplicate default and write schedule; File handling shows two columns.
- **L5.** Add library → TV Shows → Z:\TV Shows → Add library: five clicks, no third window, lands on the page scanning. Add folder on Movies → folder → Add folder: the row glows; nothing asks "where next".
- **L6.** Folder pop-up on the Z: drive: names appear within about a second; the result card shows "Checking…" then a count; clicking another folder while checking is not blocked. Compare with the earlier engine-captured.log.
- **L7.** First-run setup on a clean data folder: the Libraries step shows the same table and dialog; clicking any row or heading never leaves setup.
- **L8.** Access: untick a library for Household 2; none of its profiles sees it anywhere; the person's page shows no library ticks.
- **L9.** Screenshots of every screen at 1440 and 1280 for Shy, published as one Artifact page before merging (Shy's rule, 2026-10-29).

## 8. Risks and open questions

**Risks**
- The rail makes Libraries the only Settings page with a second navigation column; it is justified here because libraries are the one place with many peers of one kind. If Shy prefers a flatter look, the alternative in Q1 keeps the table and adds a persistent area strip instead.
- Identify from the Unidentified tab depends on the universal editor's Match panel accepting a file that is not yet an item; if it cannot, WP3 ships Identify as "open the file's folder in the editor" and the gap is recorded.
- The picker speed fix is scoped by the log; building it blind risks fixing the wrong thing, so WP6 waits for the log (a Remote Control read of the file, or Shy pasting its tail).
- Removing per-profile library ticks is an access change; it gets the security review and a fresh start, and a household that relied on per-person hiding loses that (the Vault is the replacement, per Shy).

**Open questions:** none. Shy answered Q1 to Q6 on 2026-10-30 with the recommended option each (D19). The questions as asked:
- **Q1.** Two-pane rail (recommended) or keep the overview table with a persistent area strip on top of every Libraries page (lighter, but libraries of other areas stay two clicks away)?
- **Q2.** Add library: two steps with the pop-up as step 2 (recommended) or one window that puts the type strip above the folder browser (one window, four clicks, but two questions on one screen, which Shy disliked before)?
- **Q3.** With library ticks gone, do the per-profile *area* switches (Watch/Listen/Read/View per person) go too (recommended: yes, Kid levels and the Vault cover hiding) or stay?
- **Q4.** Confirm adult content stays Vault-only with no Watch option (recommended: confirm, matches Library access v3 D6).
- **Q5.** Should the Unidentified tab be the home of these files (recommended) or a separate Settings page "Unidentified files" across all libraries, with the tab linking to it filtered?
- **Q6.** View in Add library (Shy asked 2026-10-30): a fourth tab on step 1 showing Photos, Personal videos and Documents, going to the same folder pop-up with View's two extras as one footer line, "Shared with everyone ▾" (or one person) and, only when it is not set yet, where View keeps its files (recommended: one flow everywhere; add-folder v2 D15 and D16 already want the shared pop-up and the inline storage question); or keep View out of Add library and add folders only from the View rows?

## 9. Gaps and drift

- **Library pages v15 (Built, awaiting Local checks):** D2 (area tabs), D7 (folder columns and "Change in Advanced" hint), D14 (one "i" per section) and D17 (status words) are superseded by D1, D3, D5, D11 and D15 here; a v16 note points here when this is locked. Its L1, L2 and L7 Local checks are replaced by L1, L5 and L6 above.
- **Library setup and add-folder v2 (In progress):** D0, D0d (Browse button, separate pop-up) and packets 1, 2, 3 and 6 are superseded by D8, D9, D10 and WP5, WP6; D13 to D21 (setup View and Vault step) stand. A v3 note when locked.
- **Library access v3 (Locked):** D1 (ticks on the person) is superseded by D16 here; D2, D4, D5, D6, D7, D9, D10 stand. Needs v4, written on lock, before WP8 builds.
- **Simpler access v5:** its per-profile area switches (D1) are removed by D16 and D19 (Q3: A); a note on its next version.
- **Universal editor (Locked):** Identify from the Unidentified tab is a new launcher of the existing Match panel, on the library's own page, desktop only; no change to the editor's rules.

## Plain English summary

Libraries becomes one screen you can always read: a list of every library on the left, grouped under Watch, Listen, Read, View and the Vault, with a coloured dot that says how each one is doing, and the chosen library on the right. The numbers finally mean something: "12 files not identified" opens a list of those twelve files that says why each one failed and lets you fix, keep or remove it, and "3 need review" opens the review list for that library. The confusing Advanced section is removed because every setting in it already had a proper home. Adding a library is two windows, "What is it?" and "Where is it?", and the second one is the folder browser itself, so there is no third window and no Browse button; adding a folder is that same browser. Folder names show up first and the check runs underneath, so the window never freezes while it counts. First-run setup uses the very same table and dialog. Shy agreed to every recommendation and locked the spec on 2026-10-30; it is ready to build.
