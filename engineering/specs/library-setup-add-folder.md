# Spec: Library setup and add-folder flow (fewer steps, fewer info buttons, one screen for setup and Settings)

> **Status:** Locked (Shy, 2026-10-29) · **Version:** 1 · **Last updated:** 2026-10-29 · **Related docs:** [Library pages](library-pages.md) (v14, Built; D3, D4, D5, D10) · [Simpler library access](library-access.md) (v2, Locked) · [One shared look for pages](one-shared-look.md) · [First-run setup flow](first-run-setup-flow.md) · [Library setup](library-setup-groups.md) (D13)

> Written 2026-10-29 from Shy's local-review notes on the setup wizard's Libraries step, the Add folder dialog, the number of steps, the "go to music" prompt and the many "i" buttons. It was checked against the code on main (after PR #533). **Builds after packet 4 of One shared look**, because it reuses the shared tabs, cards and buttons.

## Plain-English product walkthrough

**What it feels like today (read in the code).**
1. **Adding a library** is one dialog with five blocks: media area, name, content type, access, first folder. Three of them carry an "i" button, and there are four small grey notes under the folder. Choosing the folder opens a second pop-up.
2. **Adding a folder to an existing library** is a different dialog: the folder picker is drawn *inside* the dialog (not as its own pop-up), with help text above and, for administrators, Vault switches below. So the same job looks different in two places.
3. The old "go to music?" question is already gone (Library pages D4). What Shy may still be seeing is the long dialog and the pop-up on top of it, which feels like the same question in another form.
4. **First-run setup** already shows the same Libraries table and the same Add library dialog as Settings (Library pages D13, "Simple" mode). What differs is only what Simple hides (access and Vault). Shy found the older look; this spec checks the new one and makes the match exact.
5. **Access in the dialog** still offers "Standard / Selected profiles / Vault-protected". The Locked access spec removes "Selected profiles" and the library Access tab.

**What it feels like after this spec.**
1. **Add library is three things on one calm screen**: *what is it* (pick a content type from the area; the name fills itself in and can be changed), *where is it* (one Browse button), and **Add library**. The media area is a tab you are already standing in, so it is pre-chosen from where you clicked. No "i" buttons: the two sentences that matter are plain lines of text under the folder, and anything longer lives in the docs.
2. **Add folder is the same pop-up as the folder step of Add library**: a "Choose source folder" window with the folder names, the live check ("Folder accessible, 126 files found" or the plain reason it cannot be used), and one **Add folder** button. Nothing else on it except, for administrators only, one line "Vault" (Library pages D10).
3. **No question afterwards, anywhere.** Add library lands on the new library's page with the first scan running. Add folder returns to the table, highlights the new row and shows one toast, "Added Movies folder, scanning now". Same in setup, except setup stays on its step and shows the row.
4. **Setup and Settings are literally one screen.** Setup shows the Libraries table with the same four area tabs, the same dialog and the same pop-up. The only differences are what a first-time install must not show: no Vault option, no Access choice, and a "Continue" button below the table. There is no separate setup version of any of it.
5. **At most one "i" per page, and none in these dialogs.** Where something needs a sentence it is a visible sentence. Where it is a rare detail, it is an "i" that opens on top of the dialog (the "behind the dialog" bug is fixed, which the earlier work already covers by drawing tooltips in the top layer).
6. **Access matches the Locked access spec.** Add library has no Standard / Selected profiles choice. An administrator sees only one switch, **Vault**, which is the Vault-protected option as before (fixed at creation, as Library pages D10 says). Everyone else sees nothing about access.

**What stays the same:** the folder checks and refusals, the content types, what a scan does, Add files, the Folders / File handling / Activity tabs, the Vault rules, administrators only for Vault choices.

**Acceptance criteria (observable)**
- **A1.** From the Libraries page, adding a library with an existing folder takes: click Add library, click a content type, click Browse, click the folder, click Add folder in the pop-up, click Add library. No more than six clicks and no question afterwards.
- **A2.** Add folder opens the same pop-up as the Add library folder step (same title, same checks, same button wording).
- **A3.** The Add library dialog and the pop-up contain no "i" buttons.
- **A4.** In setup, the Libraries step shows the same table, dialog and pop-up as Settings; a test fails if setup has its own copy of any of them.
- **A5.** Neither dialog mentions Selected profiles or Standard; a non-administrator sees no word "Vault" in them.
- **A7.** Opening the pop-up on a local drive lists folders in about a second.
- **A6.** Adding a folder or library never navigates unless it is a new library; no "go to" prompt exists.

## 1. Goal
Make adding a library or folder short and obvious, and make first-run setup show exactly the Settings screen. Reason: Ease of use and Uniformity.

## 2. Scope
**In scope**
- Add library dialog: fewer blocks, content type first, name auto-filled, no "i" buttons, Vault switch replaces the Access block (to fit the Locked access spec).
- Add folder: the shared "Choose source folder" pop-up instead of a dialog with an embedded picker.
- Setup Libraries step: confirm it is the shared screen; add the guardrail.
- Completion feedback: toast and highlighted row.

**Out of scope**
- The cause of picker slowness is found first on Shy's PC (Engine log, Local check in the batch); the fix is packet 6 here. Out of scope: the "no storage location" first-run bug, folder file counting.
- The look (tabs, cards, buttons): One shared look.
- Removing the library Access tab and per-library access: the access spec's build packet does that; this spec only avoids re-adding it.
- Changing what happens after a scan, naming rules or Managed/As-is behaviour.

## 3. Decisions made
Shy's answers (2026-10-29): the access switch is fine but "Vault" is too wordy, so it is labelled just **Vault**; no toast, the new row **glows briefly** instead; the problem with today's flow is **too many things on one screen**, so Add library is **two screens, one question each** (the approved page-by-page mockup has eight screens). Proposed items below stand until Shy locks.
- **D0c (Shy, 2026-10-29, confirmed "agree"). The Vault is a fifth area, beside Watch, Listen, Read and View.** Anything private is added under it with the same two screens (Movies, TV Shows, Music, Books, **Adult content**, Other), so the normal Add library has no Vault switch and no Adult content type. **Adult content exists only in the Vault.** The Vault button is drawn only for people with Vault access; for everyone else the area, its libraries, counts and search results do not exist, so no per-library access is needed. This **reverses Shy's earlier choices** and supersedes Library pages D10 (Vault is an option, not a section) and library-access D6 (Adult content as a Watch library with a Vault tick); both need a new version (a new version of the access spec is needed before this builds), and the Vault sections spec's per-section access and Move to Vault are to be re-checked against it before building. Adding a Vault folder to an existing Vault library uses the same pop-up.
- **D0b (Shy, 2026-10-29).** Libraries page and setup show the four areas as icon toggle buttons (Watch, Listen, Read, View), not tabs. Every content type has an icon. The types are Movies, TV Shows, YouTube, **Adult content**, and **Other** (replaces Home videos; its line says "like home videos, recordings or clips"). The **Vault** switch is shown to anyone who has Vault access, not only administrators: the Vault is private and needs its own access, and without it the option does not exist. This revises Library pages D10 (which said administrators only); to be recorded in that spec's next version, and administrators-only management of Vault *settings* is unchanged unless Shy says otherwise.
- **D0d (Shy, 2026-10-29).** Screen 2 is titled "Choose your <type> folder" (e.g. Movies). Two labelled fields: FOLDER (path, **Browse** button, live check, one line "Library scans this folder and its subfolders. Your files are never changed.") then LIBRARY NAME (pre-filled, "You can rename it later"). The button always says Browse, never Change. The shared pop-up keeps the whole existing picker (back, forward, up, refresh, editable path bar, search, live check).
- **D0 (Shy, 2026-10-29).** Add library = screen 1 "What is it?" (type only), screen 2 "Where is it?" (folder, name as a quiet editable line, Vault switch for administrators). The folder pop-up is shared with Add folder. No toast anywhere; a new row glows for about two seconds.
- **D1 (proposed).** Content type is chosen first, the name is filled from it, and the media area is taken from the tab you came from but can still be changed.
- **D2 (proposed).** One folder pop-up for Add library and Add folder.
- **D3 (proposed).** No "i" buttons in these dialogs; two plain sentences stay as visible lines.
- **D4 (proposed).** The Access block is replaced by a single administrator-only "Vault" switch.
- **D5 (proposed).** Setup reuses Settings' components; a guardrail test enforces it.

## 4. UI behaviour
| Situation | What the person sees |
|---|---|
| Empty (no libraries) | The table's empty state with one **Add library** button. |
| Add library, no folder yet | **Add library** is disabled with the quiet line "Choose a folder to continue". |
| Folder being checked | Spinner inside the pop-up's result card; the pop-up stays usable. |
| Folder cannot be used | Amber text with the plain reason; **Add folder** disabled. |
| Slow folder (network drive) | The count shows "counting…", capped; never blocks choosing. |
| Saved | New library: opens its page with a scan running. New folder: highlighted row and a toast. |
| Phone | Editing is desktop-only; phones see the table read-only (unchanged). Tablet and desktop: the dialog and pop-up are centred, one column. |
| Setup | Same, plus a Continue button below the table. |

## 5. Data / model changes
None. Dialog and component structure only. (Vault and managed-folder flags keep their current meaning.)

## 6. Work packets
1. **Shared folder pop-up for Add folder (Standard).** Replace the embedded picker in `LibraryAddFolderDialog` with the same pop-up Create library uses; keep the Vault line and managed-Vault switch inside the pop-up footer. Files: `Settings/Libraries/LibraryAddFolderDialog.razor`, `Shared/ServerFolderPicker.razor`, tests. Depends on: none. Done when: A2.
2. **Shorter Add library (Standard).** Reorder (type, name, folder), pre-fill the area and name, remove the three "i" buttons and the Access block, add the administrator-only Vault switch. Files: `Settings/Libraries/CreateLibraryDialog.razor(.css)`, `LibraryAccessOptions.razor`, tests. Depends on: One shared look packet 4; and the access spec's removal of "Selected profiles" for the final form (until it lands, keep the current choices behind the same switch). Done when: A1, A3, A5.
3. **Feedback after saving (Mechanical).** Toast plus highlighted row for Add folder; keep "no navigation" rule. Files: `LibraryFolderAddService.cs`, `LibrariesOverview.razor`. Depends on: 1. Done when: A6.
4. **Setup guardrail (Mechanical).** A test that `SetupMediaStage` renders only `LibrariesOverview` and `CreateLibraryDialog`, and that Simple hides Vault and Access. Files: `tests/MediaEngine.Web.Tests`. Depends on: 2. Done when: A4.
5. **Docs (Mechanical).** Update Library pages in tuvima-docs (`content/products/library/`) and the walkthrough text. Depends on: 3, 4.

6. **Fast folder picker (Standard).** Folder names appear first; the "N files found" check loads afterwards, capped and cancellable, and never blocks browsing. Cause to be confirmed from the Engine log on SHY-DESKTOP (Local check) before building. Files: Engine folder-listing action and `Shared/ServerFolderPicker.razor` (to be confirmed). Depends on: the log finding. Done when: on a local drive the names show in about a second; a slow network drive shows "counting…" and stays usable (A7).

## 7. Local checks
On SHY-DESKTOP with a small test library: add a library from Settings and from first-run setup, add a second folder, try a folder that is already used, try a Vault folder as an administrator. Count the clicks (A1). Screenshots at desktop and a narrow window.

## 8. Risks
- The Access block removal depends on the access spec being built first; if it is not, the Vault switch ships first and Selected profiles is removed later.
- Pre-filling the name must not overwrite a name the person typed.

## 9. Open questions
Asked in the thread; defaults above are the recommendations.

## Plain English summary
Adding a library becomes: pick what it is, pick the folder, done. Adding a folder uses the same folder window, so the two never look different. Nothing asks you "where to now?", and the little "i" buttons leave these dialogs because the two sentences that matter are simply written on the screen. First-run setup is held to show the very same screen as Settings, so it cannot drift again.
