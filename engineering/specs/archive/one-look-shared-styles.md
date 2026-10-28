# Spec: One look for the whole app (popups, menus, lists, icons, profile pictures)

> **Status:** Built (awaiting Local checks) · **Version:** 2 · **Last updated:** 2026-10-27 · **Related docs:** [Product overview](../../product/overview.md) · [Design system](../../design/design-system.md) · [Appendix A: dialog inventory](#appendix-a-dialog-inventory) · [Plugins and Integrations screens](plugins-and-integrations-screens.md) (applies the icon rule, F70) · bug log entries F3, F12, F15, F25, F26, F35, F42, F45, F46, F51, F52, F64, F69, F70 · Editing revamp thread (uses the popup in F35; F49 decides which editor is kept)

## 1. Goal

Every popup, menu, list and button in Library should look and line up the same way, so the app feels like one product instead of many screens built at different times. Today the same job (a "..." menu, a confirm box, a track list) is done in several different ways, and each way has its own small flaws: menus with centred text, popups with buttons on the wrong side, columns with no breathing room, a profile picture that is too big or off centre. The fix is to repair the shared pieces once and move the one-off versions onto them, so the flaws stay fixed.

## 2. Scope

**In scope**
- **One popup style** (F35, F51): the proposed card design with a warning icon, title, short line, close button, divider and right-aligned buttons, used by every popup.
- **One menu rule** (F25, F26): one look and one text alignment for every "..." and dropdown menu.
- **One list and table rule** (F46, F12, F15, F45, F52): default spacing between columns, one way to centre and align columns, one place for the play button.
- **Songs list clean-up** (F45): centred Favourite column, Delete moved into the "..." menu. Visual design of the whole Songs list stays with F64.
- **Profile pictures** (F3, F42): smaller inside the tile on Who's watching, initial letter truly centred.
- **One icon-and-button rule** (F70): when a button or switch gets an icon, and which ones.

**Out of scope**
- Redesigning the Library area pages (F69), the Songs list layout beyond F45 (F64) and the Home page (F71). They must follow the rules written here, but they are planned in their own threads.
- Deciding whether editing is a popup or the embedded Editing Mode (F49). This spec only makes whichever is kept use the shared popup.
- Plugins and Integrations pages (own spec; they apply the icon rule below).
- Phone layouts are kept as they are (menus stay bottom sheets, tables stay cards).

## 3. Decisions made

Proposed by this draft, for Shy to confirm or change.

- **D1.** Fix the shared parts, not each screen. Maintenance: a later change to popups, menus or tables happens in one place. Reliability: the same flaw cannot come back on the next screen built.
- **D2.** The popup design Shy supplied (F35) becomes the single popup style. Buttons are always right-aligned: the safe or cancelling choice on the left (outlined), the main choice on the right (purple). A destructive main choice is red.
- **D3.** Menu items are always left-aligned, icon first, text after, with the same height and padding. Destructive items are red and sit last, after a divider.
- **D4.** Every list and table gets a standard gap between columns (recommended: 16 px at desktop, about double today's 8 px in the songs list) and a standard row height. Columns of numbers and times are right-aligned; icon columns (favourite, play) are centred; text is left-aligned. A column never touches its neighbour.
- **D5.** Row actions that are rarely used (delete, edit, add to queue) live in the "..." menu. Frequent actions (play, favourite) stay visible as icons. Destructive actions never sit as a loose icon in a row.
- **D7 (Shy, 4B).** Every popup in the app moves to the new shared design in one holistic pass, not screen by screen. The full list is in the [dialog inventory (Appendix A)](#appendix-a-dialog-inventory): about 40 popups, in five groups. A guardrail test then stops new one-off popups. The edit panels thread owns the editor layouts (Group C) but they use this same frame and close prompt.
- **D8 (Shy).** Before anything is moved, a prerequisite packet (WP0) checks the inventory against the running app, one popup at a time, so nothing is missed or changed blind.
- **D9 (Shy).** The unused Curators drawer (and its never-called open action in Settings) is deleted as part of this work (done in WP3).
- **D10 (Shy).** Popup buttons are words only (Q4 answered A). Decisions on gap (16 px), songs-list Delete (menu only) and picture size (about 80%) are also confirmed.
- **D6.** Icon rule (F70): primary buttons and view switches (Cards / List, Add, Import, Export) show an icon plus a word. Repeated row actions are icon-only with a tooltip and an accessible name. Confirm buttons inside popups stay words only. Toggles that only switch something on or off keep the switch with its label and gain no icon.

## 4. UI behaviour

**Popups.** One card: amber warning icon for "unsaved or risky" questions (a blue information icon for plain notices), title, one line of explanation, a close X top right, a divider, then buttons bottom right. Closing with Esc, X or clicking outside counts as "cancel" and never decides anything by accident. On phones the same popup becomes a bottom sheet with full-width stacked buttons, main choice on top. Wording is sentence case ("Discard changes", "Save changes", "Keep editing").

**Menus.** Same box everywhere: rounded card, small shadow, items left-aligned with an icon, hover highlight, keyboard arrows work. The Users and sign-in Actions menu is the first to move over (its items are currently centred because they are plain text buttons).

**Lists and tables.** Wider default column gaps, one hover style, the play button always in its own narrow column so numbers and titles line up whether or not a row can be played. Missing tracks ("Show missing tracks" on) keep their row aligned: the play column is simply empty. Track number and track name never touch ("10 Ironic" not "10Ironic").

**Songs list.** Favourite column centred under its heading; the Delete column is removed and "Delete from library…" becomes the last, red item in the row's "..." menu (same permission check as today, so people who cannot delete never see it). Bulk delete after selecting several rows is unchanged.

**Profile pictures.** On Who's watching the picture sits inside its tile with visible breathing room (recommended: about 80% of the tile instead of filling it). The initial letter is centred both ways inside the circle.

**States.** Empty, loading and error states are unchanged by this spec. TV and large screens keep their current larger sizing; the new spacing scales with it.

## 5. Data / model changes

None. This is presentation only: no change to the data store, the Engine connection points or the wire types.

## 6. Work packets

### WP1 — Shared popup style (F35, F51)
- **What it does:** restyles the shared popup frame and confirm dialog to the supplied design (icon, title, line, close X, divider, right-aligned buttons), adds a tone option (warning, information, danger), and moves the editors' "Discard / Keep editing" prompt onto it.
- **Why it happens today:** the confirm dialog puts its buttons in a footer that is right-aligned, but several other popups (including the editor close prompt) build their own footers with no shared rule, and there is no icon or divider treatment, so popups differ.
- **Files likely touched:** `src/MediaEngine.Web/Components/Shared/AppConfirmDialog.razor` and `.css`, `AppDialogShell.razor` and `.css`, `AppDialogFrame.razor`; callers in `Components/Collections/CollectionEditorShell.razor`, `Components/MediaEditor/SharedMediaEditorShell*.cs`; to be confirmed.
- **Depends on:** none
- **Size:** Standard
- **Done when:** the Unsaved changes popup matches the supplied image at desktop and phone width; a component test confirms the buttons render in the footer in the order cancel then main; no popup that uses the shared shell has left-aligned buttons.

### WP0 — Dialog inventory and validation (prerequisite, own sub-thread)
- **What it does:** opens every popup in the starting list (the dialog inventory) in the running app on a small generated library, records its current look at desktop, tablet and phone width in `.tmp/`, adds any popup the scan missed (search for `ShowAsync<`, `AppDialogFrame`, raw `<dialog>`), and confirms each row's group. Output is the corrected inventory with a "checked" tick per row. Read-only: no code changes.
- **Files likely touched:** the Appendix A table of this spec only (the build thread updates it with the corrected list; this is not a rewrite of the Locked decisions) (plus screenshots in `.tmp/`).
- **Depends on:** none (needs the one Engine slot for a short check, `.claude/rules/one-engine-at-a-time.md`)
- **Size:** Standard (scout and verifier agents for the sweep)
- **Done when:** every row of the inventory is ticked, has a before screenshot, and no popup is found that is not on the list.

### WP1b — Popup guardrail
- **What it does:** a test that fails when a Razor file draws its own popup (`<dialog>`, `AppDialogFrame` with a hand-made footer) outside the shared popup components, with an allow-list for the Group E exceptions.
- **Files likely touched:** `tests/MediaEngine.Web.Tests` (new `*GuardrailTests.cs`); to be confirmed.
- **Depends on:** WP2 (so the list of exceptions is final)
- **Size:** Mechanical
- **Done when:** the test passes today with the allow-list and fails when a new raw popup is added.

### WP2 — Move every popup onto the shared shell (D7)
- **What it does:** moves Group B and Group D popups from the inventory onto the shared header, body and footer, keeping their content; confirms Group A looks right; hands Group C to the edit panels thread with the WP1 frame. Split into builders by area, at most two at a time: (1) Settings network, security and backup popups; (2) Details, artwork, sign-in and provider popups; (3) inline page popups and raw dialogs.
- **Files likely touched:** the components listed in the inventory; to be confirmed against WP0's corrected list.
- **Depends on:** WP0, WP1
- **Size:** Standard (three packets)
- **Done when:** every inventory row in Groups A, B and D shows the new frame in a screenshot at desktop and phone width, button order is cancel then main, and the old per-popup footer styles are deleted.

### WP3 — Shared menu rule (F25, F26)
- **What it does:** moves the hand-built menus (Users and sign-in Actions, Applications actions, account menu, phone hero card, rating control) onto the shared overflow menu and menu item, which already left-align text and carry icons.
- **Why it happens today:** `ManagedAccessUsers.razor` builds its own menu from plain text buttons (`AppButton` text style), which centre their label; the shared `AppMenuItem` already left-aligns. Other menus (`TopNavAccountMenu`, `PhoneHeroCard`, `MediaRateControl`) use raw buttons for the same reason.
- **Files likely touched:** `Components/Settings/ManagedAccessUsers.razor` and `.css`, `ManagedAccessApplications.razor`, `Components/Navigation/TopNavAccountMenu.razor`, `Components/Cinematic/PhoneHeroCard.razor`, `Components/Shared/MediaRateControl.razor`; to be confirmed.
- **Depends on:** none
- **Also:** deletes the unused Curators drawer (`Components/Settings/CuratorsDrawer.razor`) and its open action in `Pages/Settings.razor` (D9).
- **Size:** Standard
- **Done when:** every menu item in these menus is left-aligned at the same height; the bespoke menu CSS (`.access-action-menu`) is deleted; a guardrail test rejects new `role="menu"` markup outside the shared menu components.

### WP4 — Shared list and table spacing (F46)
- **What it does:** defines shared tokens for column gap, row height and column alignment, applies them to the shared table and the main list views (songs, tracks, library tables, queue), and documents the rule in the design system.
- **Why it happens today:** each list defines its own `grid-template-columns` and gap (165 style files use grid columns; the songs list uses 8 px, the album track list uses none), so there is no central place to make rows breathe.
- **Files likely touched:** `engineering/design/design-system.md`, shared table styles (`Components/Shared/AppTable.razor`), `Components/Library/LibraryConfigurableTable.razor`, `Components/Listen/ListenSongTable.razor.css`, `Components/Details/AudioItemTable.razor.css`, player queue styles; to be confirmed.
- **Depends on:** none
- **Size:** Complex (touches many lists, so migrate in steps: tokens first, then songs, then track list, then the rest)
- **Done when:** the design system documents the gap and alignment rule; the songs list and album track list read their spacing from the shared tokens; the scoped-style size check in CI still passes.

### WP5 — Album track list: number, play button, name (F12, F15)
- **What it does:** gives the track number, play button and track name their own columns with a column gap, so "10" and the name never touch and "Show missing tracks" rows stay aligned.
- **Why it happens today:** in the compact track list (`.tl-embedded-audio__row`) the row is a grid with no column gap, and the play button, the playing indicator and the number share one flexible cell; rows that are missing or playing show a different thing in that cell, so numbers and names shift. In the full table the number cell has the same shared-cell pattern.
- **Files likely touched:** `Components/Details/AudioItemTable.razor` and `AudioItemTable.razor.css`; to be confirmed.
- **Depends on:** WP4 (uses the shared gap), can start in parallel with the token step
- **Size:** Standard
- **Done when:** a test renders a 12-track album with two missing tracks and one playing track and asserts the number, play and title cells exist as separate cells in every row; visual check shows "10" and the name separated.

### WP6 — Songs list columns (F45)
- **What it does:** centres the Favourite heading and cells, removes the Delete column, and adds "Delete from library…" as the last red item of the row's "..." menu with the existing permission check.
- **Files likely touched:** `Components/Listen/ListenSongTable.razor` and `.css`, `Components/Listen/SongActions.razor`; to be confirmed.
- **Depends on:** WP3 (menu), WP4 (spacing)
- **Size:** Standard
- **Done when:** the Delete column is gone from the column list and the column picker; the existing delete tests pass through the menu item; the heart is centred under its heading.

### WP7 — Profile pictures (F3, F42)
- **What it does:** reduces the picture to about 80% of its tile on Who's watching (keeping the hover glow and lock badge in place) and centres the initial letter in the circle.
- **Why it happens today:** the picture fills the whole tile width (up to 168 px, 220 px on large screens), and the initial is drawn at a hand-set vertical position (y of 52 on a 100 grid with a "central" baseline), which sits slightly low because it centres the text box rather than the capital letter.
- **Files likely touched:** `src/MediaEngine.Web/wwwroot/app.css` (`.who-card__picture`), `Components/Shared/ProfileAvatar.razor`; to be confirmed.
- **Depends on:** none
- **Size:** Mechanical
- **Done when:** a test checks the initial is rendered with centred anchors and the exact vertical offset agreed in the visual check; screenshot at desktop and TV width shows even space around the picture and a centred letter on the Profiles page and Who's watching.

### WP8 — Icon-and-button rule (F70)
- **What it does:** writes the rule in D6 into the design system, adds icons to the main buttons and view switches that lack them (starting with filter rows, Add/Import/Export buttons and the Cards / List switch from F69), and checks icon-only buttons all have tooltips.
- **Files likely touched:** `engineering/design/design-system.md`, `Components/Shared/AppButton.razor` and callers; to be confirmed by a scout pass.
- **Depends on:** WP1 (popup buttons stay words only)
- **Size:** Standard
- **Done when:** the design system states the rule; the guardrail test for icon-only buttons (accessible name and tooltip) passes for the updated screens.

### WP9 — Review and records
- **What it does:** independent review against this spec, visual before/after proof for popups, a menu, songs list, album track list and profile pages, and the Local checks appended to `engineering/validation/batch.md`.
- **Depends on:** WP0 to WP8
- **Size:** Standard
- **Done when:** review has no must-fix items and the batch list is updated.

## 7. Local checks

- **L1.** Who's watching and Settings, Profiles: pictures have visible space inside their tiles and each initial looks centred. Failure: letter looks low or high, or glow overlaps the name.
- **L2.** Open any edit, cancel and discard prompt: card with warning icon, buttons at the bottom right. Failure: buttons on the left or no divider.
- **L3.** Users and sign-in, Actions menu: every item aligned left with the same spacing.
- **L4.** Album with 10 or more tracks and missing tracks: numbers, play button and names line up with "Show missing tracks" on and off, including while a track plays.
- **L5.** Songs list on real media: heart centred, no Delete column, Delete from library in the "..." menu for an administrator and absent for a non-administrator.
- **L6.** TV and large screen sizing of the above.

## 8. Risks and open questions

- **Risks:** touching many lists at once could shift column widths people are used to (Performance: wider gaps mean fewer columns fit on small laptops; the column picker lets people hide columns). Moving about 30 popups is mostly mechanical but easy to break visually, so WP2 is checked popup by popup. The scoped-style size ceiling in CI may need a shared-style move instead of new rules.
- **Open questions:** none. Decided by Shy on 2026-10-27: Q1 column gap 16 px (D4, D10); Q2 Delete only in the "..." menu (D5, D10); Q3 profile picture about 80% of its tile (D10); Q4 popup buttons words only (D10); Q5 menus, pictures and tables run beside the popup work after WP0; Q6 side drawers left alone except deleting the unused Curators drawer (D9).
- **Built 2026-10-28 (PRs #468, #470, #471, #472 and the icon pass).** Popups now share one card through `AppDialogShell`, `AppDialog` and `AppConfirmDialog`, and `PopupCardGuardrailTests` lists every popup that is not yet on it with a reason. The security settings sheet and the Profile access sheet moved onto the card in a follow-up (this change), so no popup is waiting to move. Phone sheets, editors and the player popovers keep their own shells on purpose. The Appendix A list is the starting scan; the guardrail exception list is the checked one.
- Note for the editing thread: it should use the popup from WP1 and not build a second one. The plugins thread already applies the icon rule from WP8.

## Plain English summary

Right now the app has many slightly different popups, menus and lists, so small flaws show up everywhere: buttons on the wrong side, menu text centred, track numbers jammed against names, a profile picture too big or off centre. This plan fixes the shared building blocks once (one popup, one menu, one table spacing rule, one icon rule) and moves the one-off versions onto them, so they all match and stay fixed. Nothing about your data changes. The roomier list spacing, Delete in the "..." menu and words-only popup buttons were all confirmed.


## Appendix A: Dialog inventory

> Starting list built by scanning the Dashboard source (`src/MediaEngine.Web`). It is a first pass: work packet WP0 re-checks every row by opening each popup in the running app and fixes this list before anything is moved. "Shell" means the shared popup frame (`AppDialogShell`); "Frame" means the lower-level `AppDialogFrame`.

### How each popup is opened (three paths)
1. **Dialog service** (`ShowAsync<Something>`): about 47 call sites, 38 distinct popups.
2. **Confirm helper** (`AppConfirmDialog.ShowAsync`): 8 callers (artwork removal, Metadata settings x2, match step, item move, details save-or-discard, batch move, details page, collection editor close prompt).
3. **Inline** (a popup that a page draws itself with `AppDialogFrame` or a raw `<dialog>`): about 20 places, mostly Settings network, backup and sessions screens.

### Group A: already on the shared shell (restyle comes for free from WP1; check only)
| Popup | Where used |
|---|---|
| Confirm dialog (save or discard, move, add anyway, remove artwork) | 8 callers above |
| Delete from library | `LibraryItemDeletionFlow` |
| Empty trash | View trash header |
| Vault PIN | `VaultPinPrompt` |
| Add folder wizard | Library setup, folders panel, upload zone, Setup |
| Convert folder, Merge folder, Change folder kind, Upload kind | `LibraryFolderAddService`, upload zone |
| Library text edit | Folders panel (2 uses) |
| Document rule editor | Document rules settings |
| Documents: bulk, details, inbox chip | Documents view |
| Shared contribution | View page |
| Artwork asset picker | Artwork workspace |
| Also appears in | Universe |

### Group B: popups with their own layout (must be moved onto the shell, WP2)
| Popup | Notes |
|---|---|
| Artwork URL | uses Frame, no shared footer |
| Artwork workspace dialog | wraps the artwork workspace |
| Media membership (add to list or collection) | used from Details and Songs |
| Personal history | used from Details |
| Administrator unlock, Confirm it's you, Approval | sign-in and approval steps |
| Provider onboarding | Metadata settings and Setup |
| Server folder picker | 3 uses |
| Message box (`AppMessageBox`) | the service's generic alert |
| Manual port forwarding, Network test, Network port change | Settings network |
| Paired devices, Signed-in sessions | Settings security |
| Backup and recovery, Advanced network | Settings (inline popups) |
| Activity page popup, Documents page popup, Gallery detail popup, Browse filter sheet | inline Frame popups |

### Group C: editors (owned by the edit panels thread; they must use the same shell and close prompt, but their layout is decided there, F49)
Media editor (`SharedMediaEditorShell`, `MediaEditorSurface`), Collection editor, Gallery editor, Person editor, shared entity editor workspace, Profile editor sheet.

### Group D: popups drawn with a raw dialog element (move to the shell)
Profiles admin unlock dialog, Profile access sheet, Security settings popup, Who's watching popup (PIN), Reconnect modal (connection lost; keeps its special behaviour but takes the same card look), Universal search overlay (keeps its large search layout; only the frame look is shared).

### Group E: not popups in the sense of this rule (listed so nobody moves them by mistake)
- Full-screen viewers and readers: Book reader, Document viewer, Media viewer, Vault image viewer.
- Playback popovers and sheets (volume, speed, seek rail, queue).
- Phone bottom sheets (phone navigation, phone hero card, row menus, overflow action menu): they keep the phone sheet behaviour but must use the same card styling and button order.
- Side drawers (Chronicle explorer, Curators, Users drawer, Applications drawer): not dialogs; follow the table and menu rules, not the popup rule. Decision needed if Shy wants drawers restyled too (Q6).
- Details overlay host (the details page overlay) and the Design System preview page (keeps the sample of the new popup).

### Validation (WP0 checklist, per popup)
Opened in the running app at desktop, tablet and phone width: header, close button, footer button order and alignment, Esc and click-outside behaviour, focus on the safe button when destructive, keyboard tab order, loading and error state, and a screenshot saved to the ignored `.tmp/` folder. The result of WP0 is this table with a "checked" tick per row and any popups that were missed.

### Side drawers: what exists today
| Drawer | Where | Used? | Notes |
|---|---|---|---|
| Users drawer (`ManagedAccessUsers`) | Settings, Users and sign-in | Yes | 1,595-line file, eleven modes; Settings-profiles plan S9 retires this scope in favour of Profiles |
| Applications drawer (`ManagedAccessApplications`) | Settings, Applications | Yes | Plugins and Integrations spec replaces it with a wizard |
| Chronicle explorer drawer | Universe explore page | Yes | Detail panel on the explorer |
| Curators drawer (`CuratorsDrawer`) | Settings page | No caller found (`OpenCuratorsDrawer` is never called) | Likely dead code |
| Old editing drawer (`EditingDrawer`) | Details page | Retired by the Details overlay spec | Not to return |
Reference: the Universe spec (2026-10-19) says Edit Universe uses "the same side drawer Editing Mode uses", while the Details overlay spec says no drawer anywhere (D3); these two disagreed. Settled (2026-10-27, universal editor spec WP11c): a Universe is edited with the new bar and right panel, not a drawer (details and artwork only, no Match).
