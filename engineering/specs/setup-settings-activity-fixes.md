# Spec: Setup, settings menu and Activity fixes

> **Status:** Locked · **Version:** 1 · **Last updated:** 2026-11-17 · **Related docs:** [Product overview](../product/overview.md) · [Library activity](library-activity.md) · [Setup and Libraries fixes](setup-and-libraries-fixes.md) · issues #1038, #1039, #1041, #1060, #1066, #1067

## 1. Goal

First-time setup never gets stuck after going back to change folders. Activity tiles line up at one height with one plain status word. The folder picker feels more open. The Settings menu and the profile menu stop shouting: a small true-red dot instead of a pink box, smaller text, nothing cut off.

## 2. Scope

**In scope**
- Setup footer shows only Back and Continue (the "Go back to the current step" button is removed); Continue after revisiting Libraries moves forward; All set always loads its checklist or offers Try again (#1038).
- Activity tiles: one height for every tile, one status word on top, no bottom details, batch entries shrunk to the standard tile and aligned, finished batch shows how long it took (#1039).
- Folder picker layout: path trail with a pencil, folder name lower, compact Read-only / Managed choice (#1041).
- Security & Devices red dot, profile menu "Add a password" row, one truer red, narrower profile menu, smaller Settings menu text with full names (#1060, #1066, #1067).

**Out of scope**
- Any Engine or wire change; the picker's choosing, typing and validation behaviour; other rows' attention shading (Local AI, Plugins, Integrations, Tools keep it).

## 3. Decisions made

- **D1 (Shy, issue 1038).** Setup shows only Back and Continue on every step. Removing the button also removes the endless-spinner path.
- **D2.** After the folder check passes, Continue moves to the step after Libraries and loads that step's data. All set always requests its checklist when opened and shows Try again if it fails.
- **D3 (Shy 2026-11-14).** Every Activity tile has the same height. Albums keep square art at that height and may be wider; nothing is cropped.
- **D4.** Each tile shows one plain status word at the top (Matching, Enriching, Getting artwork and similar). The bottom label (counts, track count, type) is removed everywhere, including History.
- **D5 (Shy 2026-11-14).** "Batch tiles" are the grouped batch entries in History; they use the standard tile size and align in the row.
- **D6.** A finished batch shows "Took 12 min" (under a minute: "Took under a minute"), computed from the run's start and end times already served. No Engine change.
- **D7 (Shy).** The picker: path trail at the top with a pencil that turns it into a text box (replaces the "Enter path" link); folder name moves below the selected path; Read-only / Managed is one compact choice with one line of help: "Read-only never changes your files. Managed can move and rename them." The Managed "files will move" warning stays. Drives list, Back and Add folder are unchanged.
- **D8.** Security & Devices loses the red outline and fill. It shows a small solid red dot at the row end and looks like every other item; the "No password" wording stays on the Security page. On phones the row keeps its plain dot.
- **D9.** The profile menu row reads "Add a password" with a small red dot; the long explanation lives on the Security page. The menu gets a maximum width so it is no wider than its other rows need.
- **D10 (Shy 2026-11-14).** One truer red replaces the pinkish danger colour across the app (shared tokens), so every red gets the fix.
- **D11.** Settings menu labels use the same size as the other menus; names are never cut off (the column widens slightly if needed). Tags and dots do not squeeze the label.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Setup, back to Libraries, add folder, Continue | Moves to the next step; All set loads its checklist within seconds, or Try again |
| Activity, mixed library running | All tiles one height; one status word on top; no bottom text |
| Activity, finished batch | Same-size tile entry with "Took 12 min" |
| Folder picker | Path trail with pencil; name field below the path; compact access choice |
| No password | Red dot on Security & Devices and in the profile menu "Add a password" row |
| Phone | Tiles at the phone tile height; picker and menus follow the same rules |

## 5. Data / model changes

None.

## 6. Work packets

### WP1 — Setup footer and Continue
- **What it does:** removes the return button and its parameters from the setup steps; moves forward after the folder check; All set always loads readiness with Try again.
- **Files likely touched:** `src/MediaEngine.Web/Components/Pages/SetupPage.razor`, `Components/Setup/Setup*Stage.razor`, `SetupStageFooter.razor`, `Resources/SharedStrings.resx`, `tests/MediaEngine.Web.Tests`
- **Depends on:** none · **Size:** Standard
- **Done when:** a test shows Continue from a revisited Libraries step lands on the next step and All set requests readiness; no setup screen renders the old button.

### WP2 — Activity tiles
- **What it does:** one tile height; status word on top; bottom labels removed; batch entries use standard tiles; finished batch "Took" text.
- **Files likely touched:** `Components/Settings/LibraryActivity/LibraryActivityTile*.razor(.css)`, `LibraryActivityWorkingNow`, `LibraryActivityRunEntry`, `LibraryActivityHistory`, `LibraryActivityText.cs`, tests
- **Depends on:** none · **Size:** Standard
- **Done when:** component tests show no bottom label and the "Took" text; styles give one height at desktop and phone.

### WP3 — Red and menu styling
- **What it does:** true red token; Security row red dot; profile menu row and width; Settings menu label size and width.
- **Files likely touched:** `wwwroot/tuvima.tokens.css`, `Services/Navigation/SettingsMenuBuilder.cs`, `Components/MediaHub/MediaSectionShell.razor(.css)`, `Components/Navigation/TopNavAccountMenu.razor(.css)`, resx, tests
- **Depends on:** none · **Size:** Standard
- **Done when:** menu builder tests show the dot and no blocked shading for Security; the profile row is one short line.

### WP4 — Folder picker layout
- **What it does:** D7.
- **Files likely touched:** `Components/Shared/ServerFolderPicker.razor(.css)`, resx, tests
- **Depends on:** WP1 (setup uses the picker; avoid overlapping edits) · **Size:** Standard
- **Done when:** picker tests still pass for choosing, typing a path and adding a folder; layout matches the mock.

## 7. Local checks

- **L1.** Fresh data store: finish setup to All set, go back to Libraries, add a folder, Continue. It moves forward and All set shows its checklist.
- **L2.** Activity with a mixed library running: all tiles one height, one status word, no bottom text; a finished batch says how long it took. Mock | Live at 1024, 1280, 1440, phone.
- **L3.** Folder picker from setup and Settings: pencil, name lower, compact choice. Mock | Live at four widths.
- **L4.** Profile with no password: red dot on Security & Devices and "Add a password" row; narrow menu; Settings labels in full at 1024, 1280, 1440 and phone.

## 8. Risks and open questions

- **Risks:** the shared red changes every red in the app slightly; removing the bottom label hides counts people liked (the batch box still shows counts). The picker mock image was not readable during planning, so the layout follows the issue text.
- **Open questions:** none.
