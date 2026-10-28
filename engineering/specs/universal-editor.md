# Spec: Universal editor (Details, Artwork, Match)

> **Status:** In progress (Locked by Shy, 2026-10-27; Waves 1-3 built) · **Version:** 1 · **Last updated:** 2026-10-27 · **Related docs:** [Product overview](../product/overview.md) · [Details overlay and Editing Mode](details-overlay-and-edit-mode.md) (partly superseded, see section 3a) · [Batched write-back](batched-writeback.md) · [Library setup groups](library-setup-groups.md) · Bug log items F13, F14, F17, F18, F19, F35, F48, F49, F50, F51, F61 · Mockups: 8 screens supplied by Shy, 2026-10-27 (Le Continental series and season, Orbital, Post-Grunge album, Songs list, Review changes)

## 1. Goal
One editor for everything. Whether you are fixing a movie, a season, a song or twelve songs picked from a list, you get the same bar at the top and the same panel on the right, over the page you were already on. You change things, see them on the page, and nothing is written until you press Save changes and have seen a plain list of what will happen. This replaces the pop-up editor, the pencils-on-the-page Editing Mode and the separate track multi-select editor, which today work three different ways.

## 2. Scope
**In scope**
- One editing bar, one right-hand panel and one floating artwork picker, used on every detail page and every library list.
- Three tools: **Details**, **Artwork**, **Match**.
- Moving between related items (show, season, episode; album, track; book series, book) without leaving edit mode.
- Choosing several items from a list and editing them together.
- Staged changes, a Review changes step, and one save.
- One write-back control with one behaviour (F61).
- One shared dialog style for Unsaved changes and every confirmation (F35, F51).
- A Match search that works across providers and fields (F50).
- People, series and curated-collection editors moving onto the same bar (D17).
- One look for every dialog in the app, done first as its own prerequisite (D20, WP1).
- Retiring: the old pop-up editor on detail pages, pencils-per-field, the section slot below the hero, and the separate track multi-select editor.

**Out of scope**
- Needs Review: keeps its own list and flow, but opens this same panel for the fix (see D16).
- Phones, tablets and TV: still read-only. Editing stays desktop-only.
- Deleting from the library (Delete spec): the entry moves into this bar's "..." menu, behaviour unchanged.
- Photos (their own View index).

## 3. Decisions made
Approved by Shy on 2026-10-27 with the answers recorded in section 8.

- **D1. One editor, three tools.** Details, Artwork and Match are the only tools. Anything an old editor did that fits none of them is either dropped or becomes a field inside one of them. Reason: Maintenance (one thing to keep working) and Ease of use (one thing to learn).
- **D2. It lives on the page you are on.** The page, its scroll position, filters and selection do not move. The right panel covers the right side only; closing it returns the whole page without leaving edit mode.
- **D3. Nothing is saved until Save changes.** Edits are held as a list of pending changes. Cancel throws them away; Save changes shows Review changes first (D12). *This replaces "pencils save straight away".*
- **D4. No pencils on the hero.** All structured fields are edited in the Details panel. The page shows your pending values live (title, year, genres) so you see the result as you type.
- **D5. Four separate kinds of change, never mixed silently.** (1) Library overrides of details, (2) retail match (which provider record this is), (3) parent relationships (which album, season or series it belongs to), (4) physical file moves. Each has its own switch in Match and its own section in Review. Provider-supplied facts stay read-only and are shown beside your overrides, so a provider refresh never overwrites what you typed and what you typed never pretends to be provider data. *Matches rule 5 and 8 of the brief and the existing "locks on provider facts".*
- **D6. Matching a record does not move it.** Choosing a retail match updates identity and details only. Changing the album or season is a separate switch, off by default. Moving files is a further switch, off by default and greyed out when the folder is As-is, protected or read-only, with the reason shown. (As in mockup 6.)
- **D7. The item bar.** A trail under "Editing" shows where you are (Le Continental > Season 1 > Episode 1). Each step is a dropdown listing the item and its children with small pictures. Clicking an item on the page also switches to it. Pending changes on every item are kept as you switch. (Mockups 2 and 4.)
- **D8. Artwork is a floating picker, not a panel.** It can be dragged, minimised and closed. Tabs depend on the item (Background, Poster, Logo for a movie; Season poster for a season). It offers provider artwork, upload, paste a link and already-saved pictures. Picking updates the page at once as a preview only. (Mockups 3 and 4.)
- **D9. Artwork the page does not normally show.** For a season poster or episode still, the page switches to a clearly labelled "Temporary editing view" showing the poster large with the normal backdrop dimmed behind it, and a "Return to series view" link. It never creates a season page. Leaving Artwork restores the page.
- **D10. One panel for Details and Match.** About 450 px wide for Details, growing to about 650 px for Match results and Review. Same header, close button and scrolling rules everywhere; no stray scroll bars inside it (F17). Dropdowns are sized to their text, never full-width giants (F19).
- **D11. Match is two steps in one panel.** Step 1 Find match: search box pre-filled from the item, a "Search in" provider picker, result tabs by what you are matching (for a song: Tracks, Albums, Artists), results showing artwork, title, creator, parent, year, edition and provider. Step 2 Review selected match: current beside proposed, then the four switches from D5/D6, then "Use this match" which only stages it. Confidence is used to order results, shown quietly (a small "Best guess" tag), not as a percentage. *Fixes F17, F18 and F50.*
- **D12. Review changes before every save.** Opens a dialog grouped Metadata, Identity (recording and retail matches), Artwork, Relationships (album or season changes), Physical files. Each row shows item, before and after, with Edit (jumps back to that change) and a menu to remove it. Destructive file moves carry an amber warning. Footer: Back to editing, Discard changes, Save N changes. A line says provider records are never modified. (Mockup 8.)
- **D13. Save is checked first, then applied in a safe order.** The Engine first checks every pending change (permissions, read-only folders, name clashes, the target still existing). Details, identity and relationship changes are then saved together as one unit: either all of them or none. File moves run last, one at a time; if one fails, the data changes stay saved and Review reports exactly which files did not move and why. Nothing fails silently. Reason: Reliability. A file move on disk cannot be un-done like a database change, so the plan is honest about it.
- **D14. Several items at once.** Ticking rows in a list (or episodes in a season) shows a selection bar: Edit, Match, Move to album/season (when meaningful). The panel says "Edit 4 songs". Fields where the selected items differ say "Mixed, leave unchanged" and only fields you change apply. Music also gets "Renumber sequentially" and Change album. Bulk Match is offered only where it makes sense (music and episodes of one season first). This replaces the current track multi-select editor (F14). Pending changes use the same list and Review as a single item.
- **D15. One write-back control.** A single "Save changes into the files" setting per library, set in Settings (existing Library settings), shown in the editor as a small read-only status with a link ("Files will be updated: On"), plus one per-session override in Review only when the library allows it. It appears in exactly the same place for every item type and every way into the editor. Whether write-back is allowed per folder follows the Library setup groups spec. (F61.)
- **D16. Needs Review uses the same panel.** The Needs Review list stays; "Fix" opens the item's own detail page in edit mode on the Match tool, or, from the list, this same right-hand panel beside the list. The old modal is not kept. *This reverses the CLAUDE.md exception for `MediaEditorLauncherService.OpenAsync` and needs Shy's yes (open question Q3).*
- **D17. People, series and curated collections move over too.** (Shy, 2026-10-27: fix holistically, not piecemeal.) Their editors use the same bar, panel and Review changes. They get Details, and Artwork where they have pictures (person photo, series and collection covers); Match only where a provider record exists (people). Their Details fields come from the same per-type configuration, so they need configuration, not new screens. After this build the old pop-up editors are gone.
- **D17a. Universes and characters (Shy, 2026-10-27).** Universes are canonical (they come from Wikidata), so the editor never overwrites them. A Universe opens in the same bar and panel with a trail listing its Series, but offers only **Details** (a local display name and description override, shown as such, with a "Reset to canonical value" link; the Series list is read-only with each grouping's source) and **Artwork** (cover and background). There is no **Match** tool for a Universe, and Series are never moved in or out by hand. Characters stay read-only (Universe spec D6): no editing, no hide action; a wrong link is fixed by re-matching the item that caused it. Desktop admins only, as in the Universe spec.
- **D18. Players use the editor too.** The lyrics button in the audio and video players opens this editor in the page behind the player (or a plain message when lyrics are not editable), not the old pop-up (F48).
- **D19. Track "..." menu works.** On the album page, Edit in a track's "..." menu starts edit mode with that track selected (F13).
- **D20. One dialog look.** Every dialog and confirmation in the app uses the Unsaved changes design Shy supplied: card, amber "!" icon, title, one line of text, close X, divider, buttons right-aligned with the outlined one left and the purple primary right (F35, F51). Applied to every dialog in the app in this build (Shy, 2026-10-27). Because that touches many unrelated screens, it is owned by the existing thread "Unify the app's look and controls" (about 30 dialogs counted so far), which inventories every dialog and confirmation, builds the one shared dialog, converts them all and proves each with a test or screenshot. This editor build depends on it and does not duplicate it; the dialog design is agreed with that thread.
- **D21. Desktop only, and permissions.** Same rules as today (`EditorDeviceRule.CanEdit`; the Engine decides who may edit). Read-only libraries show the editor with fields disabled and the reason.
- **D22. Unsaved-changes guard.** Cancel, Esc, leaving the page or closing the panel with pending changes asks "Unsaved changes" (D20). Closing only the panel keeps edit mode and the pending list; no prompt is needed for that.
- **D23. No new libraries.** Built with the existing App* components. (The brief mentions MudBlazor; Library does not use it, so this is built on its own components.)

### 3a. What this changes in earlier decisions
| Earlier decision | Now |
|---|---|
| Details overlay D4 banner with Manage Artwork / Match and Identity / History / Done | Replaced by the editing bar (Details / Artwork / Match, Cancel, Save changes). History moves into Details as a "History" block at the bottom. |
| D6 pencils save straight away | Replaced by D3 staged changes |
| D7 section slot below the hero | Replaced by the right panel and floating picker |
| D8 batch section above the rows | Replaced by D14 |
| D9 Save or discard on Done | Replaced by D22 |
| D12 to D16 (Match first, then move; Add anyway; taken slot; wrong kind blocked) | Kept. They become rules inside Match and the Relationships switch. |
| D17 shared confirmation dialog, D18/D19 Delete placement | Kept; dialog look follows D20 |
| CLAUDE.md "Never" item 1 (Editing Mode wording) | Needs rewording once this is Locked; Shy to confirm |

## 4. UI behaviour

### The editing bar
Always visible while editing. Left: "Editing" and the item trail (D7). Centre: Details, Artwork, Match (the active tool highlighted; Artwork and Match are hidden when the type does not support them). Right: Cancel, Save changes (shows a count once something is pending, e.g. "Save 3 changes"; disabled with a tooltip when there is nothing to save). A "..." menu holds Delete (D3 of the Delete spec) and, if write-back is relevant, nothing else.

### Entering and leaving
Edit is in the page "..." menu ("Edit this show / album / book") and on a track's "..." menu (D19). In a list, ticking rows reveals the selection bar with Edit and Match. Deep links never start in edit mode. Leaving edit mode: Save changes (after Review) or Cancel.

### States
| Situation | What the person sees |
|---|---|
| Normal | Bar on top, page as before, panel closed until a tool is chosen. |
| Nothing pending | Save changes disabled ("No changes yet"). |
| Pending changes | Count on Save; changed fields marked with a purple dot in the panel; the page shows the new values. |
| Item still processing | Tools disabled with the reason ("Still gathering details"). |
| Read-only library or file | Fields disabled with the reason; file-move switch greyed. |
| Provider down or no results | Match shows a plain message and "Try another provider"; nothing is blocked. |
| Search loading | Results list shows skeleton rows, search box stays active. |
| Save fails | Review stays open, failed rows are marked with the reason, saved rows are marked done; nothing is lost. |
| Item removed meanwhile | "This item is no longer in your library"; its pending changes are dropped from Review with a note. |
| Phone / tablet / TV | No Edit entry; "Edit on a computer" line (unchanged). |

### Details panel, by type
Same layout; fields differ per type from one configuration list (title, year, status, genres as removable chips, description with character count, tags, source, plus type-specific: season number, track number, disc number, artist, album, authors, series position and so on). Provider facts show a small lock and the provider's name; local override fields show a purple dot when changed and a "Reset to provider value" link.

### Bulk Details
A short note on top: "Only fields you change apply to selected songs." Mixed fields show "Mixed, leave unchanged" until touched.

### Match panel
As D11. In bulk, results are a table mapping each selected item to its proposed entry; rows with no match are highlighted and Review lists them under "Not matched" (Add anyway rule from the Details overlay spec D13 stays).

### Artwork picker
Floating, draggable by its header, can be minimised to a small chip to inspect the page. "Use this artwork" stages it. "Remove artwork" asks first.

## 5. Data / model changes
- A **pending change list** held in the Dashboard while editing (one entry per item and field, identity change, relationship change, artwork choice or file move). It is not stored; closing the browser loses it, with the unsaved-changes guard (D22) the only protection. Reason: simplest, and nothing half-saved ever reaches the library.
- One new Engine action: **check and apply an edit session**. It takes the whole pending list, can be asked to only check ("preview") and returns per-change results. It reuses the existing separate actions (field overrides, artwork, retail match, moves, batch edit) internally rather than replacing them. The Dashboard never calls those directly during an edit session any more.
- Contracts: new wire types for the pending list, preview result and per-change result (with Contracts tests and fixture snapshot update).
- Nothing changes in how provider claims versus local overrides are stored (they already differ); this build only surfaces the difference.
- Pre-release: a reset or reingest is acceptable if anything about stored edit state needs to change; no compatibility layer.
- Per-type editor configuration (fields, tools and match rules by type) moves into one shared registration so new media types add a configuration, not a screen.

## 6. Work packets
Built in the cloud and validated locally as one batch (see Local checks). Max two packets at once.

### WP0 — Map the current editor (scout)
- **What it does:** Writes a one-page map of what the current pop-up editor, Editing Mode, the track multi-select, and artwork/match actions each do today, by media type, mapped to Details/Artwork/Match, with every capability that has no home in the new design listed for Shy's decision.
- **Files likely touched:** none (notes in `.tmp/`), reads `Components/MediaEditor/*`, `Components/Details/Editing*`, `Services/Editing/*`, `MediaEngine.Api/Endpoints/MetadataEndpoints.*`
- **Depends on:** none · **Size:** Standard (scout)
- **Done when:** a gap list exists and every capability is marked kept, moved, or dropped.

### WP1 — App-wide dialog style (prerequisite, owned by the "Unify the app's look and controls" thread; not built here)
- **What it does:** That thread inventories every dialog and browser pop-up, builds the one shared dialog per D20 (amber icon, title, one line, divider, right-aligned buttons, outlined left and purple primary right), converts them all, and adds a guardrail test plus a before/after screenshot sheet.
- **This spec's part:** agree the dialog design with that thread, and start Wave 3 (the editing bar and panel) only after the shared dialog has merged.
- **Depends on:** none · **Size:** Complex (owned elsewhere)
- **Done when:** the shared dialog is merged and the Unsaved changes and Review dialogs here use it.

### WP2 — Edit session state and pending list
- **What it does:** Keeps active item, active tool, view context and pending changes as four separate pieces of state (brief rule 2). Item trail data from the existing navigator endpoint.
- **Files likely touched:** `Services/Editing/EditingModeContext.cs`, new `EditSessionState`, Contracts pending-change types
- **Depends on:** WP0 · **Size:** Complex
- **Done when:** switching items keeps pending edits; unit tests for add, replace, remove and discard.

### WP3 — Edit session Engine action
- **What it does:** Preview and apply (D13): validate everything, save data changes as one unit, run file moves last, return per-change results.
- **Files likely touched:** `MediaEngine.Api/Endpoints/*`, new service beside `Services/BatchEdit/`, `MediaEngine.Contracts`, tests in Api.Tests
- **Depends on:** WP0 · **Size:** Complex
- **Done when:** a test applies a mixed list and rolls back data changes on a failed check; a move failure reports the file but keeps data saved.

### WP4 — Editing bar and right panel host
- **What it does:** The bar, item trail dropdowns, tool tabs, Cancel and Save, and the right panel that hosts Details and Match. Replaces the banner and section slot.
- **Files likely touched:** `Components/Details/EditingBanner.razor`, `EditingSectionSlot.razor`, new `EditingBar`, `EditorPanelHost`
- **Depends on:** WP1, WP2 · **Size:** Complex
- **Done when:** the page does not move or scroll when the panel opens; bUnit test for bar states.

### WP5 — Details tool (one configuration per type)
- **What it does:** Per-type field configuration, provider-lock display, reset to provider value, live page preview.
- **Files likely touched:** `Components/MediaEditor/Sections/EditorDetailsSection.razor`, `Services/Editing/MediaEditorDetailsPresenter.cs`
- **Depends on:** WP4 · **Size:** Complex
- **Done when:** changing a title shows on the page before saving and nothing is written until Save.

### WP6 — Artwork tool and temporary views
- **What it does:** Floating draggable picker, provider search, upload, paste link, saved assets; temporary poster or still preview view (D9).
- **Files likely touched:** `Components/MediaEditor/Sections/EditorArtworkSection.razor`, new `FloatingArtworkPicker`, hero preview hook in `Details/*`
- **Depends on:** WP4 · **Size:** Complex
- **Done when:** choosing a season poster shows it large without creating a season page, and Return to series view restores the page.

### WP7 — Match tool: search and review
- **What it does:** Provider picker, field-aware search, results with detail, review with four switches (D5, D6, D11); fixes disabled Save match (F18), stray scroll bars (F17), and the missing providers (F50, e.g. MusicBrainz).
- **Files likely touched:** `Components/MediaEditor/Moves/MatchStep.razor`, `Sections/EditorMatchSection.razor`, `MetadataEndpoints.*`, provider search services
- **Depends on:** WP4, WP3 · **Size:** Complex
- **Done when:** Smile Empty Soul album from MusicBrainz is found and stageable; moving a track to another album enables "Use this match".

### WP8 — Review changes dialog
- **What it does:** D12, using preview from WP3.
- **Files likely touched:** new `ReviewChangesDialog`
- **Depends on:** WP1, WP3, WP4 · **Size:** Standard
- **Done when:** grouped list, remove a change, back to editing, and per-row failure display all covered by tests.

### WP9 — Lists and bulk editing
- **What it does:** Selection bar on library lists and season episode lists, "Edit N items" panel, Mixed handling, music renumber and change album; retires the track multi-select editor (F14).
- **Files likely touched:** `Components/Browse/*`, `Pages/ListenPage.razor.cs`, `Services/Editing/DetailBatch*.cs`
- **Depends on:** WP5, WP7, WP8 · **Size:** Complex
- **Done when:** four songs edited in one pass, only changed fields apply, filters and scroll stay.

### WP10 — Single write-back control
- **What it does:** One status and one setting location (D15); removes the extra copies.
- **Files likely touched:** `Sections/EditorDetailsSection.razor`, `SharedMediaEditorShell.razor`, `Settings/LibraryWriteSchedulePanel.razor`
- **Depends on:** WP5 · **Size:** Standard
- **Done when:** the control appears identically from detail page, list, and Needs Review.

### WP11 — Entry points and clean-up
- **What it does:** Track "..." Edit (F13), player lyrics button (F48), Needs Review (D16), retire the old pop-up editor and pencils, update docs and guardrail tests, reword CLAUDE.md Never item 1 once Shy confirms.
- **Files likely touched:** `Services/Editing/MediaEditorLauncherService.cs`, `Pages/ListenPage.razor.cs`, `Components/Listen/PlaybackLyrics.razor`, `Settings/NeedsReviewPageContent.razor`, docs
- **Depends on:** WP9, WP10 · **Size:** Standard
- **Done when:** no route opens the old pop-up for a single item and a guardrail test says so.

### WP11b — People, series and collection editors
- **What it does:** Adds configurations for person, series and curated collection to the shared editor (Details, Artwork where relevant, Match for people) and retires their pop-up editors (D17).
- **Files likely touched:** `Components/MediaEditor/PersonEditorDialog.razor`, `Components/Collections/CollectionEditorShell.razor`, `Services/Editing/CollectionEditorLauncherService.cs`, per-type configuration
- **Depends on:** WP5, WP6, WP7, WP8 · **Size:** Complex
- **Done when:** a person, a series and a curated collection can each be edited, reviewed and saved through the shared bar, and the old dialogs are removed.

### WP11c — Universe editing
- **What it does:** Universe configuration for the shared editor (D17a): Details as local overrides, Artwork, Series trail, no Match; replaces the Universe spec's separate "Edit Universe" entry.
- **Files likely touched:** `Components/Pages/UniversePage.razor`, per-type configuration
- **Depends on:** WP5, WP6 · **Size:** Standard
- **Done when:** a Universe can have its description overridden and artwork changed through the shared bar, the canonical value is untouched and restorable, and no Match tab shows.

### WP12 — Final review pass
- **What it does:** Reviewer agent against this spec, accessibility (keyboard, focus, screen reader) and the visual proof required for Dashboard changes.
- **Depends on:** all · **Size:** Standard

Suggested order: **Prerequisite:** WP1 (dialogs), done in the "Unify the app's look and controls" thread; Waves 1 and 2 can run beside it, Wave 3 waits for it. **Editor waves:** Wave 1 (WP0) · Wave 2 (WP2, WP3) · Wave 3 (WP4, WP5) · Wave 4 (WP6, WP7) · Wave 5 (WP8, WP9) · Wave 6 (WP10, WP11, WP11b, WP11c) · Wave 7 (WP12).

## 7. Local checks (SHY-DESKTOP, appended to the central list)
- Edit a TV show, season and episode; change one field on each; switch between them; confirm all changes appear in Review and save together.
- Change season poster and a movie background; confirm the temporary poster view and that Cancel restores everything.
- Match a song to a MusicBrainz release that is not on Apple Music; turn switches on and off and confirm album, files and details change only as selected.
- Select four songs in Listen > Songs; edit one shared field and Match; confirm filters, sort and scroll survive.
- A real file move on real media, including a refused move (read-only or protected folder).
- Write-back on and off on a real library; confirm the file tags change only when On.
- Look and feel on the real screen: panel width, no stray scroll bars, dropdown sizes, dialog design.

## 8. Risks and open questions
- **Risk: size.** The current editor is about 19,000 lines. This is the largest UI change planned. Mitigation: WP0 first, waves, and keeping the Engine's existing actions underneath.
- **Risk: staged edits are lost** if the browser closes. Mitigation: unsaved-changes guard; a saved draft is possible later.
- **Risk: file moves cannot be rolled back.** Mitigation: they run last and failures are reported per file (D13).
- **Risk: collision** with the in-progress Details overlay spec and the Delete spec; both adapt to the new bar (section 3a).
- Answered (Shy, 2026-10-27): Needs Review uses the new panel (D16); staged edits get a leave-page warning, no saved draft (D22); people, series and collections move over now (D17); every dialog converted, as a prerequisite thread (D20).
- **Risk:** the app-wide dialog change touches many screens; mitigated by it being done first, in its own thread, with an inventory and a guardrail test.
- Open: none. Ready for Shy to lock.

## Plain English summary
Instead of three different edit screens, there will be one: a bar at the top and a panel on the right, laid over the page you are already looking at. You can change details, pick artwork in a small floating picker, or fix a match, switching between a show, its season and its episodes without leaving. Nothing is saved as you go. When you press Save changes you first see a short list of exactly what will change (details, which provider record it is, which album or season it belongs to, and any files that will move) and can remove anything you do not want. Picking the right match no longer quietly moves things around; each of those is its own switch, off unless you turn it on. The same editor also handles several songs or episodes at once from a list, and every dialog in it uses the new Unsaved changes look.
