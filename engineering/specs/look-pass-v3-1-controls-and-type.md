# Spec: Look v3.1, shared controls and one type scale (dropdowns, dialogs, tab rows, menus, the details window, tooltips on top, Collections colors, text sizes)

> **Status:** Locked (Shy, 2026-11-12; questions answered 1A, 2A, 3B) · **Version:** 1 · **Last updated:** 2026-11-12 · **Related docs:** [Look v3](look-pass-v3.md) (Built, awaiting Local checks; this is a new version, not an edit) · [Details overlay](details-overlay-and-edit-mode.md) · [Design system](../design/design-system.md) · [Mockups](../design/mockups/look-pass-v3-1/look-v3-1-mockups.html) · Issues 900, 901, 896, 875, 859 (tooltip comments), 903, 870 (font comment) · Planning thread: "Plan the shared look and controls" (2026-11-11) · Sister plan: Universal editor v6 (Match and aligned rows), which owns the one shared form row
>
> Everything below was read from the code on main at `ebc883cf` on 2026-11-11. "Verified" means read in the code; "likely" means a strong reading that the build confirms first.

## 1. Goal

The controls people touch on every screen behave and read the same way: a dropdown's list is never narrower than the dropdown, every dialog is one calm roomy box, every tab row and pop-up menu has the same room and the same icon-and-label pattern, a title's details float over the page instead of covering it, nothing that hovers is ever hidden behind a window, Collections uses the same colors as the rest of the app, and every piece of text takes its size from one short scale with a stated job for each size.

## 2. What is wrong today (findings)

1. **Dropdown lists (issue 900, verified).** Two shared parts set the list width. The typed dropdown pins its list to exactly the dropdown's width, so a long option wraps or is cut. The menu-style dropdown (the Episodes dropdown on a TV show, the folder pop-up) uses the fixed 272 px menu width even when its button is wider, which is the "narrower than the dropdown" case.
2. **Dialogs (issue 901, verified).** The shared dialog card draws a line under its header and another above its footer, the header and footer have their own tight padding, and the message text is 16 px. In a confirmation the destructive button is an outlined red button and the safe button is the solid purple one.
3. **Tab rows (issue 896, verified).** The shared tab is 20 px text, 44 px tall, 16 px side padding and a 2 px underline; icons are optional and most rows have none. Five screens hand-build their own tab row (the artwork workspace, the collection editor, the discovery filter bar, the browse mode selector, the cinematic surface bar).
4. **Pop-up menus (issue 896, verified).** Rows are 44 px with 8 px vertical padding and a fixed 272 px width, so long labels are cut and the list feels tight.
5. **Details window (issue 875, verified cause).** The Details overlay spec (D1) asks for a large centered window at about 90 % of the screen. Look v3 (D14) then listed the details overlay among the full-screen Viewers, and the shared Viewer part fills the screen; the details window's own 90 % rule no longer reaches the element it was written for. The two specs disagree; Shy's issue 875 settles it: it floats.
6. **Tooltips behind windows (issue 859, likely).** The tooltip itself is already drawn in the browser's top layer inside an open window. The hover cards and info tips on choice tiles are a second, older mechanism that is layered by number, so a window covers them. The same tiles also repeat their own description in the hover text.
7. **Collections colors (issue 903, verified).** The Collections section, the collection editor and the playlist rule builder still carry 68 hand-typed colors from the old navy and slate palette (54 of them in the collection editor). Look v3's color check only stops the app-wide count from rising, so these were never forced onto the shared colors.
8. **Text sizes (Shy, issue 870 and the local check; verified).** The scale itself is fine (12, 13, 14, 16, 20, 24, 32). What is missing is a job for each size, so parts picked their own:

| Text | Today | Why it feels wrong |
|---|---|---|
| Tab labels (every tab row, including the editor's) | 20 px | Larger than section headings in the content below them |
| Text typed into fields, dropdown values, list rows | 16 px | Buttons beside them are 14 px and body text is 14 px, so forms look oversized; this is most of "the editor feels large" |
| Dialog message | 16 px | Reads as loud as the title |
| Editor window title | 24 px | Every other window title is 20 px |
| Match: result card name 20 px, "Search results" heading 16 px bold, Quick Facts values 16 px bold | mixed | Bigger than the Details tab beside it and bigger than the details page (which is mostly 12 to 14 px) |
| Editor header label and provider links | about 11.4 px | Written as a fraction of the page's base size by mistake; below the 12 px floor |
| Other | 252 size declarations that are not a named step: 28 written as fractions of the base size, about 30 hand-typed pixel sizes (11, 15, 17, 18, 22 px), 105 fluid sizes | Each one is a place the next change will miss |

## 3. Scope

**In scope**
- The shared dropdown, menu, tab row, dialog, details window frame and tooltip layer, and every screen that uses them.
- Every hand-typed color in the Dashboard's styles moved onto the shared colors (Shy, 3B: the whole app now), starting with Collections, the collection editor and the playlist rule builder.
- One type scale with a job for each size, applied to the shared parts and to every screen outside the editor window.
- The proof page, the design-system document and the public UI consistency page.

**Out of scope**
- The one shared form row (label line, control line, message line) and everything inside the editor window, including the Match redesign: Universal editor v6 owns them and uses the sizes in D8 by name. This spec builds no row part.
- Player layout (the video player and Music and Listen plans). Their panels use the tab row, menu and sizes from here.
- The top navigation and the wordmark (Tuvima Sans, unchanged), tiles, the readers' book text.

## 4. Decisions made

Standing decisions this spec keeps: the page base size stays 14 px; pop-up menus are 16 px; Inter is the interface font; Look v3 owns shared styling and feature threads consume it.

- **D1 (Shy, issue 896, A). One tab row.** Every tab row is: a 20 px outline icon, then the label, 48 px tall, 28 px between tabs, inactive tabs in muted text and icon, the active tab in bright text with a purple icon and a 3 px purple underline, and one thin divider under the whole row. Every tab has an icon; a tab row without icons fails a check. Applies to Settings and profile tabs, Libraries and Activity tabs, the editor's tabs, the details page tabs, the collection editor, Vault, and the five hand-built rows in finding 3. On a phone the row scrolls sideways with a soft fade at the cut edge; icons stay. The amber Advanced tab keeps its color and place. Not tab rows, so unchanged: the top navigation, the phone bottom bar, and two-or-three-way switches inside a form (segmented controls).
- **D2 (Shy, issue 896, A). Roomier pop-up menus.** Rows 48 px (52 px in a phone sheet, as today), a 24 px icon then the label with a 12 px gap, 8 px of space around the list and between groups, text 16 px. A menu is at least 240 px wide and grows with its longest label up to 360 px; beyond that the label is shortened and its full text shows in a tooltip.
- **D3 (issue 900). Dropdown lists.** An opened list is at least as wide as its dropdown and grows to fit its longest option, up to 480 px or the window edge less 16 px. Options stay on one line; an option that still cannot fit is shortened and shows its full text in a tooltip. The chosen option carries a check at the right and the soft purple wash. One rule in the shared pop-over, used by every dropdown, autocomplete and menu-style dropdown.
- **D4 (Shy, issue 901). One dialog style.** A dark rounded box with a thin border and no lines inside. 28 px padding all round (20 px on a phone). Title 20 px semibold; the message 8 px under it in muted 14 px; 28 px before the buttons; buttons bottom right, 44 px tall with a 12 px gap. In a confirmation the safe choice is a neutral outlined button ("Keep editing") and the destructive choice is a **solid red** button, last ("Discard changes"). A confirmation that destroys nothing keeps the neutral button and a purple primary. This replaces Look v3 D14's "destructive is outlined red, left of the primary" **for confirmations only**; in a larger window's footer where Delete sits beside Save (the editor), Delete stays outlined red. Larger windows also lose their header and footer lines; a hairline appears only while content is scrolled underneath.
- **D5 (Shy, issue 875). The details window floats.** On a tablet or desktop a title's details open as a centered window: 90 % of the screen's width and height, never wider than 1440 px, never closer than 32 px to an edge, 16 px corners, the page dimmed behind it. On a phone it is the bottom sheet, as today. This restores Details overlay D1 and removes the details overlay from Look v3 D14's list of full-screen Viewers (the readers, photo and document viewers and search stay full screen).
- **D6 (issue 859). One top layer for everything that hovers.** Tooltips, info tips and hover cards all go through the one tooltip layer, which draws above any open window. Nothing that hovers is layered by number. A hover text never repeats what the control already says (Look v3 D8): choice tiles that show their description lose the hover text.
- **D7 (issue 903; Shy 2026-11-12, 3B). Every color comes from the shared set, across the whole app, in this build.** The Collections section, the collection editor and the rule builder go first; then every other screen. Today component styles hold about 1,285 hand-typed colors (Details 471, pages 181, shared parts 96, browse 78, Collections 68, the hero carousel 63, artwork 56, search 43 and smaller amounts elsewhere) and the global sheet 123. About 400 of them are plain black or white washes (dimming over artwork, hairlines, hover washes); these become a short named set of wash and scrim colors so the look does not change. Each remaining color maps to the shared color with the same job; where none fits, a named color is added to the shared set instead of leaving the value in place. Area and type colors appear only on chips and icon tiles (Look v3 D3). The check changes from "the count may not rise" to **zero outside a written list** of real exceptions: colors taken from a cover's artwork, charts, subtitle color choices, the map, avatars and the readers' page themes. Anything that ends up visibly different is listed for the visual review rather than hidden.
- **D8 (Shy 2026-11-12, with 1A and 2A). One type scale, a job for each size.** The seven sizes stay. Each has named jobs, and a part picks a job, never a number:

| Step | Size and weight | Used for |
|---|---|---|
| Hero | 32 / 700 | The title in a details hero and the Home hero (the only place a size may scale with the screen) |
| Page title | 24 / 700 (28 at 1180 px and wider) | The one title of a page |
| Heading | 20 / 600 | Every window title, including the editor's; section and shelf headings |
| Subheading | 16 / 600 | Group headings in Settings, card and panel headings ("Search results"), a card's title |
| Menu | 16 / 500 | Rows in pop-up menus and side menus |
| Control | 14 / 500 | Buttons, tab labels, dropdown values and their options, chips, a row's title in a list or table |
| Body | 14 / 400 | Reading text: descriptions, dialog messages, table cells, text typed into a field |
| Secondary | 13 / 400 (labels 13 / 500) | Field labels, helper and message lines, meta lines (author, year), counts, tooltips |
| Caption | 12 / 400 (overline 12 / 600) | Badges, pills, time stamps, table column headings, small captions |

- **D9 (Shy 2026-11-12, 1A). Control text is 14 px on tablet and desktop.** Fields, dropdowns, buttons and tabs share one size, the same as body text. On a phone, text typed into a field stays 16 px, because phones zoom the page when a smaller field is tapped. Control heights do not change (44 px), so nothing gets harder to press.
- **D10 (Shy 2026-11-12, 2A). A dropdown's options match the dropdown (14 px).** The chosen value does not change size when it moves from the list into the field. Pop-up menus ("..." menus, the profile menu) stay 16 px as decided.
- **D11. The scale is enforced.** A style may only name a step. Sizes written as fractions of the base size, hand-typed pixel sizes and fluid sizes outside the hero are retired, and a check fails any new one. The design-system document lists the jobs table, and its existing drift test covers it.
- **D13 (Shy 2026-11-12, standing rule). The phone is equal to the desktop.** Most people use only their phone, so every shared control here works fully on a phone, and nothing is left out or hidden there (setup is the one exception, and editing is coming to phones through the editor plan). Concretely: a dropdown's list and every pop-up menu open as a bottom sheet with 52 px rows, one line per option, the chosen option checked, and the sheet scrolls when the list is long; a dialog is a bottom sheet with the same title, message and spacing, its buttons stacked full width with the destructive or primary one first; a tab row scrolls sideways and keeps every tab and icon; the details window is the sheet; text typed into a field is 16 px; everything pressable is at least 44 px. **Hover never carries information on its own:** anything a mouse user learns from a tooltip or hover card, a phone user gets by a long press (tooltips on icon-only buttons) or sees on the screen (a choice tile's description is always visible; cut-off text opens in full on a tap). The checks for each packet run at phone width (390 px) as well as desktop.
- **D12. Ownership.** Shared parts and every screen outside the editor window are changed here. Screens inside the editor window are changed by the Universal editor v6 build, using D8's job names: window title Heading; state words beside the dot, field labels, meta lines, the basis note and the line under Apply match Secondary; tab labels, control text, result row titles and the verdict line Control; "Search results" and the card title Subheading; descriptions and compare cells Body; the "Your file" caption and compare column headings Caption. If v6 has not started when this build reaches its last wave, this build applies those sizes to the editor as it stands and v6 inherits them.

## 5. UI behaviour

See the mockups (numbered markers, notes under each screen).

| Screen | Today | After |
|---|---|---|
| Any dropdown | List as wide as the dropdown or 272 px; long options wrap | List at least as wide as the dropdown, wider when an option needs it; one line per option |
| Any confirmation | Lines above and below the message, tight padding, purple safe button, outlined red destructive | One roomy box, no lines, neutral safe button, solid red destructive button last |
| Any tab row | 20 px labels, few icons, 2 px underline | Icon and 14 px label, wide spacing, 3 px purple underline, thin divider |
| Any "..." menu | 44 px rows, fixed width | 48 px rows, more space, grows with its labels |
| A title's details at 1024 and wider | Fills the screen | A centered window with the page visible around it |
| Hover text inside a window | Sometimes behind the window; sometimes repeats the tile | Always on top; never a repeat |
| Collections, collection editor, playlist rules | Old navy and slate colors | The same colors as the rest of the app |
| Forms (Settings, Add library, setup, search, collection editor) | 16 px field text beside 14 px buttons | 14 px throughout on tablet and desktop; 16 px field text on phones |

Phone (D13): the same controls with the same content. Dropdown lists, menus and dialogs are bottom sheets (20 px padding, 52 px rows, buttons stacked full width); the tab row scrolls sideways with every tab and icon; hover text is reached by a long press or is already on the screen. TV: no change beyond the shared parts. Empty, loading and error states are unchanged.

## 6. Data / model changes

None.

## 7. Work packets

### WP1 — Type scale and its jobs
- **What it does:** Adds a named token pair (size and weight) for each job in D8; moves the shared parts onto them (tabs, fields, dropdowns, list rows, buttons, dialog body, typography helper classes); 16 px field text under 600 px; retires the fraction-of-base sizes, hand-typed pixel sizes and fluid sizes outside the hero in shared styles; adds the check from D11 with today's count as a ceiling that only goes down; updates the type table in the design-system document.
- **Files likely touched:** `src/MediaEngine.Web/wwwroot/tuvima.tokens.css`, `app.css`, `native-fields.css`, `native-structure.css`, `Components/Shared/AppDialogShell.razor.css`, `tests/MediaEngine.Web.Tests/LookRatchetGuardrailTests.cs`, `DesignSystemDocDriftTests`, `engineering/design/design-system.md`
- **Depends on:** none · **Size:** Standard
- **Done when:** a token test asserts each job's size and weight; the proof page shows every job; a text field's computed size is 14 px at 1280 and 16 px at 390; the new check passes.

### WP2 — Dropdown list width
- **What it does:** D3 in the shared pop-over and its script (minimum = the dropdown's width, grow to content, cap, one line, shorten with tooltip); the typed dropdown, autocomplete and menu-style dropdown use it; the selected-row check.
- **Files likely touched:** `Components/Shared/AppPopover.razor(.css)`, `AppTypedSelect.razor`, `AppSelect.razor(.css)`, `AppAutocomplete.razor`, `AppOverflowMenu.razor`, `wwwroot/js/app-popover.js`, `app-select-sizing.js`, a node test beside the existing ones
- **Depends on:** none · **Size:** Standard
- **Done when:** a browser test opens a 320 px dropdown with a 60-character option and measures a list at least 320 px wide with one-line rows; the Episodes dropdown's list is at least as wide as its button.

### WP3 — The details window floats
- **What it does:** D5: a details size on the shared window frame; the details overlay uses it and leaves the Viewer; phone sheet unchanged; the Look v3 guardrail list updated.
- **Files likely touched:** `Components/Details/DetailsOverlayHost.razor(.css)`, `Components/Shared/AppViewer.razor`, `AppDialogFrame.razor.css`, `wwwroot/app.css`, `wwwroot/js/app-dialog.js`, overlay tests
- **Depends on:** none · **Size:** Standard
- **Done when:** a browser test measures the window at 1024, 1280 and 1440 wide: at least 32 px of page on every side, 90 % of the screen up to 1440 px; at 390 it is the sheet; Back, the close button and Esc still dismiss.

### WP4 — Roomier menus
- **What it does:** D2 through the menu tokens (row height, padding, icon gap, minimum and maximum width); every menu reads them; no local menu sizes remain.
- **Files likely touched:** `tuvima.tokens.css`, `app.css` (menu block), `Components/Shared/AppOverflowMenu.razor`, `AppRowMenu.razor`, `AppTickMenu.razor.css`, `Components/Navigation/TopNavAccountMenu.razor.css`
- **Depends on:** WP1, WP2 · **Size:** Mechanical
- **Done when:** a token test asserts 48 px rows and the 240 to 360 px width; the proof page shows a menu with a long label growing and then shortening.

### WP5 — One tab row
- **What it does:** D1 in the shared tab part (icon required, sizes, underline, divider, phone scroll with edge fade); an icon for every tab in every row; the five hand-built rows move onto the shared part; a check fails a tab without an icon and a hand-built tab row.
- **Files likely touched:** `Components/Shared/AppTabs.razor`, `AppCountTabs.razor`, `AppTabPanel.razor`, `app.css` (tab block), `Components/Settings/Frame/SettingsPageTabs.razor`, `Settings/ProfileTabs.razor`, `Editor/EditorWindowChrome.razor`, `Details/DetailPrimaryModule.razor`, `Artwork/ArtworkWorkspace.razor`, `Collections/CollectionEditorShell.razor`, `Shared/AppDiscoveryFilterBar.razor`, `Browse/AppBrowseModeSelector.razor`, `Cinematic/SurfaceTabBar.razor`, `Vault/VaultArea.razor`, a guardrail test
- **Depends on:** WP1 · **Size:** Standard
- **Done when:** bUnit renders a tab row with an icon in every tab and the active tab's underline at 3 px; the checks pass; the profile page shows Overview, Activity, Taste, Linked accounts and Import each with its icon.

### WP6 — One dialog style
- **What it does:** D4 in the shared dialog card and the confirmation, message box and delete dialogs (no inner lines, padding, gaps, button styles and order); the scroll hairline for larger windows; phone sheet padding.
- **Files likely touched:** `Components/Shared/AppDialogShell.razor(.css)`, `AppConfirmDialog.razor(.css)`, `AppMessageBox.razor(.css)`, `DeleteFromLibraryDialog.razor(.css)`, `Components/Editor/EditingUnsavedChangesDialog.cs`, `EditingSaveOrDiscardDialog.cs`, `app.css` (footer rule)
- **Depends on:** WP1 · **Size:** Standard
- **Done when:** bUnit renders "Discard changes?" with a neutral outlined "Keep editing" and a filled red "Discard changes" last; the computed styles show no border on the header or footer and 28 px padding.

### WP7 — One top layer
- **What it does:** D6: info tips and tile hover cards go through the tooltip controller; hover text that repeats a tile's description is removed; a check fails a hover surface layered by number.
- **Files likely touched:** `Components/Shared/AppInfoTip.razor(.css)`, `AppChoiceTile.razor(.css)`, `AppChoiceCard.razor(.css)`, `wwwroot/js/playback-tooltip.js`, the Add library step components, a guardrail test
- **Depends on:** none · **Size:** Standard
- **Done when:** a browser test opens the Add library window, hovers the last tile and finds the hover text in the top layer above the window; no tile's hover text equals its visible description.

### WP8 — Collections on the shared colors
- **What it does:** D7's first step: the named wash and scrim colors added to the shared set; the Collections folder, the collection editor and the rule builder moved onto the shared colors; the check gains its written exception list and a per-folder zero lock, starting with these folders.
- **Files likely touched:** `Components/Collections/*.razor.css`, `Components/Rules/SharedRuleBuilder.razor.css`, `app.css` (collection rules), `LookRatchetGuardrailTests.cs`
- **Depends on:** WP1 · **Size:** Standard
- **Done when:** the three folders contain no hand-typed color, the lock test lists them, and a token test asserts the wash and scrim colors.

### WP9 — Type walk, screen by screen
- **What it does:** Moves each screen outside the editor window onto D8's jobs, in three parts: Settings, Libraries and setup · Home, browse, details page and Collections · search, profiles, Vault, View and the players' shared parts. Records one line per screen (changed or already right). Applies D12's fallback to the editor only if v6 has not started.
- **Files likely touched:** component styles across `Components/`, the proof page
- **Depends on:** WP1, WP5, WP6 · **Size:** Standard (three parts)
- **Done when:** the D11 check is at zero outside its written allowlist (hero, readers' book text, charts); every screen has its line.

### WP10 — Proof page, docs, review
- **What it does:** The proof page shows the dropdown, dialog, tab row, menu, details window and the jobs table in every state; the design-system document and the public UI consistency page (`tuvima-docs`) match; the reviewer checks the whole against this spec.
- **Files likely touched:** `Components/Pages/DesignSystemPreview.razor`, `engineering/design/design-system.md`, `tuvima-docs/content/products/library/design-system/ui-consistency-standard.md`
- **Depends on:** all · **Size:** Standard
- **Done when:** the doc drift test passes and the review has no must-fix.

### WP11 — Whole-app color sweep
- **What it does:** D7 for everything else, in three parts by area so no two parts touch the same files: (a) the details page and its hero (471 colors, 27 files) · (b) pages, browse, the hero carousel and search (365 colors) · (c) shared parts, artwork, View, tiles, Library, Settings, navigation, players' shared parts, Universe, Listen, setup, and the global sheet (about 450 colors), then the editor's styles last, after Universal editor v6 has merged or, if it has not started, as they stand. Each part records the colors it could not map one to one, with the screen they are on, for the visual review. The check ends at zero outside the exception list and every folder is locked.
- **Files likely touched:** `Components/**/*.razor.css`, `wwwroot/app.css`, `tuvima.tokens.css`, `LookRatchetGuardrailTests.cs`, `engineering/design/design-system.md` (color table)
- **Depends on:** WP8 (the wash and scrim colors and the exception list), WP9 (so the two walks do not edit the same files at once) · **Size:** Standard (three parts, one after another where areas overlap)
- **Done when:** the color check passes at zero outside the written exception list; the design-system color table lists the new named colors and its drift test passes; each part's list of visible differences exists (it may be empty).

Waves: 1 = WP1, WP2, WP3 · 2 = WP4, WP5, WP6, WP7 · 3 = WP8, WP9 · 4 = WP11 · 5 = WP10. At most two packets at once.

## 8. Local checks (for `engineering/validation/batch.md`)

- L1 (#900). Open the Episodes dropdown on a TV show, the folder pop-up, a Settings dropdown and a player panel dropdown: every list is at least as wide as its dropdown and no option wraps.
- L2 (#901). Close the editor with unsaved changes: one roomy box, no lines inside, neutral "Keep editing", solid red "Discard changes" on the right. Delete an item: the same box.
- L3 (#896). Profile, Settings, Libraries, the editor and a details page: every tab has an icon, the active one a purple icon and underline. Open a "..." menu and the profile menu: taller rows, labels not cut.
- L4 (#875). Open a title from Watch at 1024, 1280 and 1440 wide: the page is visible around the window. On a phone it is the sheet.
- L5 (#859). Add library: hover the last tile; the hover text is in front of the window and does not repeat the tile.
- L6 (#903). Collections, a curated collection, a smart playlist's rules and the collection editor: no navy or slate, the same greys and purple as Settings.
- L6b (color sweep). Walk Home, a details page for each media type, Listen, Watch, Read, View, search and Settings beside the list of visible differences from the build: dimming over artwork, hover and selected states and borders look the same as before or as listed; nothing is tinted navy, slate or violet.
- L7 (text sizes). Editor Details and Match beside a details page: field text, buttons and tabs are the same size as body text; nothing in Match is larger than its neighbours; the editor title matches other window titles.
- L7b (phone, D13). On a phone: open a dropdown, a "..." menu and the Discard dialog (each is a bottom sheet with nothing missing compared with the desktop); scroll a tab row to its last tab; long-press an icon-only button to see its name; every choice tile shows its description without hovering.
- L8. Mock | Live at 1024 / 1280 / 1440 / phone for a dropdown, the Discard dialog, the profile tab row, a "..." menu and the details window.

## 9. Risks and open questions

- Smaller field text makes forms denser; control heights stay 44 px so touch use is unchanged. If 14 px reads too small on Shy's screen it is one token to change.
- The tab row change touches about 30 screens; icons are chosen once in this build and listed on the proof page for Shy's visual review.
- WP9 and the editor v6 build both touch text sizes; D12 keeps them on different screens.
- The whole-app color sweep touches about 130 style files. The risk is small visible shifts (a dim that is slightly lighter, a border slightly stronger). It is contained by mapping washes to named colors with the same strength, by doing it one area at a time, and by the list of visible differences Shy walks in the Local check. It runs after the other packets so it does not collide with them.
- No open questions.

## 10. Gaps and drift

- **G1.** Look v3 D14 listed the details overlay as a full-screen Viewer; Details overlay D1 says a 90 % window. *Decision (Shy, issue 875): change the code; it floats (D5).*
- **G2.** Look v3 D14 and D15 say a destructive button is outlined red, left of the primary; issue 901 asks for solid red, last, in confirmations. *Decision (Shy, issue 901): change for confirmations (D4).*
- **G3.** Look v3 D7 says "16 px for controls, menus and rows", but buttons were built at 14 px and tabs at 20 px. *Decision (Shy, 2026-11-12): change the spec; D8 to D10.*
- **G4.** One-shared-look set tab labels "one step up" (20 px). *Decision (Shy, issue 896 and 2026-11-12): replaced by D1 and D8 (14 px with an icon).*

## Plain English summary

Seven small annoyances from the local check share one cause: the shared building blocks each had a gap. This plan closes them once, in the shared parts, so every screen is fixed together. Dropdown lists will always be at least as wide as their dropdown. Every confirmation becomes one roomy box with a neutral safe button and a solid red destructive one. Every tab row gets an icon beside each label and a clear purple underline, and pop-up menus get taller rows and grow to fit their words. A title's details float over the page again instead of covering it. Hover text always appears in front. Collections takes the same colors as everything else, and so does the rest of the app: every color left over from older designs is replaced by one from the shared set, with a check that keeps it that way. Text sizes keep the same seven steps, but each now has a named job, and form text comes down to the same size as body text, which is what made the editor and Match feel oversized. Nothing changes in what Library stores or does; this is how it looks and feels.
