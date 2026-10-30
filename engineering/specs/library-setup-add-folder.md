# Spec: Library setup and add-folder flow (fewer steps, fewer info buttons, one screen for setup and Settings)

> **Status:** In progress (Version 2 locked by Shy 2026-10-29; packets 1 to 5 of Version 1 are Built, awaiting Local checks; packet 6 is waiting on a local check of the faster checks in PR #543; packets 7 to 10 are Built, awaiting Local checks) · **Version:** 2 · **Last updated:** 2026-10-29 · **Related docs:** [Library pages](library-pages.md) (v14, Built; D3, D4, D5, D10) · [Simpler library access](library-access.md) (v3, Built, awaiting Local checks) · [One shared look for pages](one-shared-look.md) · [First-run setup flow](first-run-setup-flow.md) · [Library setup](library-setup-groups.md) (D13)
> Version 3 note (2026-10-30): the Browse button and separate pop-up (D0, D0d) and packets 1, 2, 3 and 6 are superseded by the [Libraries redesign](libraries-redesign.md) (Built): Add library step 2 is the folder pop-up itself with the name in the footer, and Add folder uses the same pop-up. D13 to D21 (setup View and Vault step) stand.


> Version 3 note (2026-10-30): the Locked [Libraries redesign](libraries-redesign.md) spec supersedes D0 and D0d (no Browse button; step 2 of Add library is the folder pop-up itself, with area tabs on step 1 and View as a fourth tab) and packets 1, 2, 3 and 6. D13 to D21 (View and the optional Vault step in setup) stand.

> Written 2026-10-29 from Shy's local-review notes on the setup wizard's Libraries step, the Add folder dialog, the number of steps, the "go to music" prompt and the many "i" buttons. It was checked against the code on main (after PR #533). **Builds after packet 4 of One shared look**, because it reuses the shared tabs, cards and buttons.

> Version 2 (2026-10-29) answers Shy's choice "add both View and Vault to first-run setup" (B). It changes only what first-run setup offers: **View** becomes a fifth button beside Watch, Listen and Read, and the **Vault** becomes an optional last step. It replaces the part of Library setup D13 and Library pages D13 that limited setup to Watch, Listen and Read with no Vault. Packets 1 to 6 of Version 1 stand unchanged; packets 7 to 10 are new.

## Plain-English product walkthrough

**Version 2 additions (first-run setup).**
1. **View is in setup.** The Libraries step shows Watch, Listen, Read and View. View is not a normal library, so its Add screen is the View folder form you already know from Settings (View keeps its photos and videos in its own storage), shown in the same two-screen style and with the same shared folder pop-up. In setup it is always "shared with everyone"; choosing per-profile sources is a Settings job.
2. **View needs somewhere to keep its files.** If no View storage location exists yet, the View screen asks for it right there, pre-filled with a sensible folder, so a fresh install cannot end up with a View folder that has nowhere to live. This also closes the open "View storage must reference a configured storage location" bug for setup.
3. **The Vault is the last setup step, "Turn on the Vault?"** It appears once, after the libraries, only to the person running setup (the first administrator). How it works: (a) one sentence says what the Vault is and that it stays hidden until turned on; (b) **Set up the Vault** shows a PIN offer ("Protect it with a PIN?") with the PIN typed twice and a **Skip PIN** link, because a PIN is recommended but not required; (c) the Vault is then enabled **for that administrator only**; (d) the Vault area appears with the usual two-screen Add library so a first private library can be added now; (e) when setup finishes it locks, and the Vault is **kept off the main menu** until the person turns on "Show Vault in the menu" in Settings. **Not now** leaves no trace anywhere.
3b. **A reminder, not a pop-up.** If the Vault was set up but is not shown in the menu, one quiet dismissible line on Home and in Settings says "The Vault is set up but hidden. Turn on Show Vault in the menu to use it." No toast. If Not now was chosen, there is no reminder and no word.
3c. **Access is always an explicit choice.** Nobody gets Vault access by default: not a new person, and not a new profile, even when the main profile has it. An administrator grants it per profile, each time (Vault sections D18 modes: Off by default). Kid's profiles can never be given it.
4. **Everyone else sees nothing.** Profiles created in setup afterwards, Kid's profiles, and anyone without Vault access see no Vault step, button or word, as Vault sections requires.

**What it feels like today (read in the code).**
1. **Adding a library** is one dialog with five blocks: media area, name, content type, access, first folder. Three of them carry an "i" button, and there are four small grey notes under the folder. Choosing the folder opens a second pop-up.
2. **Adding a folder to an existing library** is a different dialog: the folder picker is drawn *inside* the dialog (not as its own pop-up), with help text above and, for administrators, Vault switches below. So the same job looks different in two places.
3. The old "go to music?" question is already gone (Library pages D4). What Shy may still be seeing is the long dialog and the pop-up on top of it, which feels like the same question in another form.
4. **First-run setup** already shows the same Libraries table and the same Add library dialog as Settings (Library pages D13, "Simple" mode). In Version 1 it offered only Watch, Listen and Read; Version 2 adds View and the optional Vault step. Shy found the older look; this spec checks the new one and makes the match exact.
5. **Access in the dialog** still offers "Standard / Selected profiles / Vault-protected". The Locked access spec removes "Selected profiles" and the library Access tab. *Note (2026-10-29): [library-access v3](library-access.md) is built; the Access choice is gone.*

**What it feels like after this spec.**
1. **Add library is three things on one calm screen**: *what is it* (pick a content type from the area; the name fills itself in and can be changed), *where is it* (one Browse button), and **Add library**. The media area is a tab you are already standing in, so it is pre-chosen from where you clicked. No "i" buttons: the two sentences that matter are plain lines of text under the folder, and anything longer lives in the docs.
2. **Add folder is the same pop-up as the folder step of Add library**: a "Choose source folder" window with the folder names, the live check ("Folder accessible, 126 files found" or the plain reason it cannot be used), and one **Add folder** button. Nothing else on it except, for administrators only, one line "Vault" (Library pages D10).
3. **No question afterwards, anywhere.** Add library lands on the new library's page with the first scan running. Add folder returns to the table, highlights the new row and shows one toast, "Added Movies folder, scanning now". Same in setup, except setup stays on its step and shows the row.
4. **Setup and Settings are literally one screen.** Setup shows the Libraries table with the same four area tabs, the same dialog and the same pop-up. The only differences are what a first-time install must not show: no Vault option, no Access choice, and a "Continue" button below the table. There is no separate setup version of any of it.
5. **At most one "i" per page, and none in these dialogs.** Where something needs a sentence it is a visible sentence. Where it is a rare detail, it is an "i" that opens on top of the dialog (the "behind the dialog" bug is fixed, which the earlier work already covers by drawing tooltips in the top layer).
6. **Access matches the Locked access spec.** Add library has no Standard / Selected profiles choice. An administrator sees only one switch, **Vault**, which is the Vault-protected option as before (fixed at creation, as Library pages D10 says). Everyone else sees nothing about access. *Note (2026-10-29): [library-access v3](library-access.md) replaces the administrator Vault switch with the Vault as a fifth area; Add library offers no Vault option.*

**What stays the same (Version 1):** the folder checks and refusals, the content types, what a scan does, Add files, the Folders / File handling / Activity tabs, the Vault rules, administrators only for Vault choices.

**Acceptance criteria (observable)**
- **A1.** From the Libraries page, adding a library with an existing folder takes: click Add library, click a content type, click Browse, click the folder, click Add folder in the pop-up, click Add library. No more than six clicks and no question afterwards.
- **A2.** Add folder opens the same pop-up as the Add library folder step (same title, same checks, same button wording).
- **A3.** The Add library dialog and the pop-up contain no "i" buttons.
- **A4.** In setup, the Libraries step shows the same table, dialog and pop-up as Settings; a test fails if setup has its own copy of any of them.
- **A5.** Neither dialog mentions Selected profiles or Standard; a non-administrator sees no word "Vault" in them.
- **A7.** Opening the pop-up on a local drive lists folders in about a second.
- **A6.** Adding a folder or library never navigates unless it is a new library; no "go to" prompt exists.
- **A8.** In setup the area buttons are Watch, Listen, Read and View. Adding a View folder in setup uses the shared folder pop-up and the same View storage as Settings.
- **A9.** On a fresh install with no View storage, adding a View folder in setup either asks for the storage location on that screen or is refused with a plain line; it can never save a View folder with no storage.
- **A10.** After the libraries, an administrator sees "Turn on the Vault?" once. Not now leaves no Vault area, switch, reminder or wording anywhere (page source included). Set up the Vault offers a PIN typed twice (mismatch shows one amber line; Skip PIN allowed), enables the Vault for that administrator only, locks it when setup finishes, and leaves it out of the main menu until "Show Vault in the menu" is on, with one dismissible reminder line.
- **A12.** A profile created after setup has Vault Off even when the main profile has it; only an explicit administrator grant changes that.
- **A11.** A person who is not the first administrator, or any Kid's profile, gets no Vault step and no word "Vault" in setup. If both a profile PIN and a Vault PIN exist they cannot be the same digits.

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

- **D14 (Shy, 2026-10-29, "B"). Setup includes View and the Vault.** Replaces the part of Library setup D13 and Library pages D13 that kept setup to Watch, Listen and Read with no Vault. Those two specs need a note in their next versions.
- **D15 (proposed). View in setup reuses the View folder form.** One component for Settings and setup (D5 still applies: no second copy). In setup the source is always Shared; per-profile sources stay in Settings.
- **D16 (proposed). View storage is settled on the View screen.** If none is configured, the screen asks for it, pre-filled, before the folder can be added.
- **D17 (Shy, 2026-10-29). The Vault is the last setup step, offered to the first administrator only,** with "Not now" leaving no trace.
- **D18 (Shy, 2026-10-29). A PIN is strongly suggested but never required.** Setup asks for it with double entry; Skip PIN shows one plain line first ("Without a PIN, anyone using this profile can open the Vault. Skip anyway?"). Administrators can always reset or add one from the library settings. This **supersedes Vault sections D25** (administrator PIN required before any Vault library exists), which needs a new version noting that a Vault library may be created without a PIN; the same strong suggestion appears wherever a Vault library is created without one.
- **D19 (Shy, 2026-10-29). Vault access is never given by default.** New people and new profiles start with Vault Off, even if the main profile has it; every grant is an explicit administrator choice.
- **D20 (Shy, 2026-10-29). The Vault stays off the main menu by default** and a quiet reminder line says it must be enabled (see 3b). Reminder placement (Home and Settings) is my proposal.
- **D21 (proposed). The Vault stays unlocked until setup ends, then locks** (switching profile or closing the app also lock it).

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
| Setup | Same, plus a Continue button below the table. Area buttons: Watch, Listen, Read, View. |
| Setup, View, no storage yet | The View screen shows FOLDER, then "Where View keeps its files" pre-filled with a suggested folder and a Browse button; Add is disabled until both are valid. |
| Setup, last step | "Turn on the Vault?" with **Set up the Vault** and **Not now**, one sentence of explanation, shown to the first administrator only. |
| Setup, Vault PIN screens | Your PIN (only if none), then Vault PIN typed twice. A wrong repeat or matching digits shows one amber line. |
| Setup, Vault on | The fifth area button appears (lock icon), unlocked until setup ends. |

## 5. Data / model changes
None new in Version 2: the View storage location, profile PIN, Vault PIN and "PIN to unlock" mode already exist. Dialog and component structure only. Dialog and component structure only. (Vault and managed-folder flags keep their current meaning.)

## 6. Work packets
1. **Shared folder pop-up for Add folder (Standard).** Replace the embedded picker in `LibraryAddFolderDialog` with the same pop-up Create library uses; keep the Vault line and managed-Vault switch inside the pop-up footer. Files: `Settings/Libraries/LibraryAddFolderDialog.razor`, `Shared/ServerFolderPicker.razor`, tests. Depends on: none. Done when: A2.
2. **Shorter Add library (Standard).** Reorder (type, name, folder), pre-fill the area and name, remove the three "i" buttons and the Access block, add the administrator-only Vault switch. Files: `Settings/Libraries/CreateLibraryDialog.razor(.css)`, `LibraryAccessOptions.razor`, tests. Depends on: One shared look packet 4; and the access spec's removal of "Selected profiles" for the final form (until it lands, keep the current choices behind the same switch). Done when: A1, A3, A5.
3. **Feedback after saving (Mechanical).** Toast plus highlighted row for Add folder; keep "no navigation" rule. Files: `LibraryFolderAddService.cs`, `LibrariesOverview.razor`. Depends on: 1. Done when: A6.
4. **Setup guardrail (Mechanical).** A test that `SetupMediaStage` renders only `LibrariesOverview` and `CreateLibraryDialog`, and that Simple hides Vault and Access. Files: `tests/MediaEngine.Web.Tests`. Depends on: 2. Done when: A4.
5. **Docs (Mechanical).** Update Library pages in tuvima-docs (`content/products/library/`) and the walkthrough text. Depends on: 3, 4.

6. **Fast folder picker (Standard).** Folder names appear first; the "N files found" check loads afterwards, capped and cancellable, and never blocks browsing. Cause to be confirmed from the Engine log on SHY-DESKTOP (Local check) before building. Files: Engine folder-listing action and `Shared/ServerFolderPicker.razor` (to be confirmed). Depends on: the log finding. Done when: on a local drive the names show in about a second; a slow network drive shows "counting…" and stays usable (A7).

7. **View in setup (Standard).** Add View to the setup area buttons; render the View folder form in the two-screen style with the shared pop-up; ask for View storage inline when none exists; Shared source only. Files: `SetupMediaStage`, `LibrariesOverview`, `ViewFoldersTab`/`ViewKindPage` (shared), View storage service (to be confirmed). Depends on: packet 2 and Version 1 packet 4. Done when: A8, A9.
8. **Vault step in setup (Complex).** The "Turn on the Vault?" step: PIN offer with double entry and Skip, enable for the administrator only, Vault area unlocked until setup ends then locked, kept out of the main menu with a "Show Vault in the menu" setting and the reminder line; plus a test that new profiles default to Vault Off. Uses the existing PIN and Vault mode services; the Engine still decides who may. Files: setup stage components, Profiles/Vault settings services, main menu (to be confirmed). Depends on: Vault sections and library access v3 (both Built); Vault sections D25 gets a new version first (or in the same change). Done when: A10, A11, A12, plus a test that the page source of a skipped setup contains no Vault wording.
9. **Setup guardrail update (Mechanical).** Rewrite the A4 test: setup renders only the shared Libraries components, the View form and the Vault step, and the Vault step is absent for non-administrators and Kid's profiles. Depends on: 7, 8.
10. **Docs (Mechanical).** tuvima-docs setup and Library pages updated for View and the optional Vault step; note in Library setup and Library pages next versions. Depends on: 7, 8. Companion edits: Library pages D13 and Library setup D13 get a "superseded in part by add-folder v2" note.

## 7. Local checks
On SHY-DESKTOP with a small test library: add a library from Settings and from first-run setup, add a second folder, try a folder that is already used, try a Vault folder as an administrator. **Version 2:** run first-run setup on a clean data folder: add a View folder (storage asked inline), add a Movies folder, then try Not now (check that no Vault wording shows anywhere, then restart setup) and Set up the Vault (both PINs, add an Adult content library inside the Vault, confirm it locks after setup and the Vault is invisible when switching to a Kid's profile). Count the clicks (A1). Screenshots at desktop and a narrow window.

## 8. Risks
- The Access block removal depends on the access spec being built first; if it is not, the Vault switch ships first and Selected profiles is removed later. *Note (2026-10-29): the access spec is built, so the Access block is gone.*
- Pre-filling the name must not overwrite a name the person typed.
- **Version 2:** the Vault step is the one place a first-time person is introduced to private content. It is limited to the first administrator and defaults to Not now, so it cannot surprise anyone; if Shy later prefers the Vault only in Settings, packet 8 is the only thing to remove.
- View in setup touches the area of the open "View storage" bug; packet 7 must not ship before its storage check (A9).
- Performance: none; setup screens are tiny and nothing runs until a folder is added.

## 9. Open questions
Version 1: asked in the thread; defaults above are the recommendations.

Version 2: (a) Decided by Shy: the PIN is never required, only strongly suggested (D18); Vault sections D25 is revised to match. (b) The reminder line placement (Home and Settings) is a proposal. No other questions are open.

## Plain English summary
Adding a library becomes: pick what it is, pick the folder, done. Adding a folder uses the same folder window, so the two never look different. Nothing asks you "where to now?", and the little "i" buttons leave these dialogs because the two sentences that matter are simply written on the screen. First-run setup is held to show the very same screen as Settings, so it cannot drift again.

**Version 2:** first-run setup also lets you add a View folder (asking where View should keep its files if that is not set yet) and, as an optional last step, turn on the Vault. If you skip the Vault, the app behaves as if it did not exist, and nobody but the first administrator is ever offered it.
