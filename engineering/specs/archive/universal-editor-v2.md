# Spec: Universal editor v2 (the editor window)

> **Status:** Built, awaiting your Local checks (waves 1-5 merged; a few items not built, see the Local checks rows in `validation/batch.md`) · **Version:** 2 (replaces the as-built v1 "Universal editor (Details, Artwork, Match)") · **Last updated:** 2026-10-30 · **Related docs:** [Product overview]((../../product/overview.md) · [Universal editor v1]((../universal-editor.md) · [Details overlay and Editing Mode]((../details-overlay-and-edit-mode.md) · [Library setup groups]((../library-setup-groups.md) · Mockups: [`engineering/design/mockups/universal-editor-v2/`]((../../design/mockups/universal-editor-v2/) (11 screens, 1440 wide; also https://claude.ai/artifact/FkiJYpZg6DpbwSK1K6eBRc) · Bug log items F13, F14, F17, F18, F19, F35, F48, F49, F50, F51, F61 and Shy's 2026-10-30 editor review

## Why a v2 (what went wrong with v1)

Shy's verdict on the built v1 (2026-10-30): "I can't match an episode to a show, I can't match a show and have it update, nothing really seems to work and I have no confidence what I did actually had any impact." Reading the code behind each complaint gives four root causes. Every design below starts from them.

| # | What Shy saw | Root cause (verified in code) | Fixed by |
|---|---|---|---|
| R1 | An episode cannot be matched to a show | When you type another show's name, TheTVDB's search hands back *shows*, never episodes (its search flow only lists episodes when a season number is already known). The new Match panel lets you pick that show and "Use this match", the Review check passes (it only runs cheap local checks), and the save then fails because an episode needs an exact show + season + episode record. There is no second step to open the chosen show and pick the episode. The Engine still has the old "browse this show's seasons and episodes" lookup, but the new panel never calls it. The "Move into Season N" switch is also always greyed for a show result, because a show result names no season. | D4, WP1, WP3, WP6 |
| R2 | Re-matching a show does nothing visible | For TV the Engine writes the new identity and queues the real work (title, artwork, cast, episode alignment) as a background job. Unlike movies, books and albums, a show's artwork is **not** swapped straight away. The page refreshes once, immediately, before the job has run, and never listens for the job finishing. The show's displayed title reads the `title` value first, while a show match writes `show_name`, so even the name can stay old until the background job rewrites it. | D6, D7, WP2, WP9 |
| R3 | "Find match / Review" labels repeat numbers and overlap | The step strip component has no styling at all for its steps, numbers or labels; the numbers and words render as raw inline text. | D2 (the strip is gone), WP4 |
| R4 | Details and Artwork too small, stray scroll bars, nothing aligned | Details and History still render the **old 7,000-line editor** inside the new 450 px panel (an editor inside an editor, two scroll areas). Match is a new component but squeezed into 650 px with a two-column compare. The Artwork picker is a 416 px floating box over the page. Dropdowns are the old full-width ones. | D1, D2, D3, D10, WP4, WP5, WP7 |

Also confirmed: the old pop-up shell still exists and is used for Details/History/Artwork inside the panel, so right now the app runs **both** editors at once, which is the Maintenance problem v1 set out to remove.

## 1. Goal

One editor that is big enough to use at 1440 px, whose buttons never move, that can match an episode to any show, season and episode in three clicks, and that proves every save took effect on the page within seconds. Replaces the as-built v1 bar-and-panel and the last remains of the old pop-up editor.

## 1a. Plain-English walkthrough

1. On any show, season, episode, movie, album, song, book or comic page, "..." → **Edit** opens the **Editor window**: a large window over the page (about 1160 by 820 px at 1440 wide; the page behind is dimmed). Top: a thumbnail, "Editing", and the item trail as small dropdown pills (The Continental › Season 1 › E1 · Brothers in Arms) so you can hop to the show, another season or another episode without leaving. Left: four tools, Details, Artwork, Match, History. Bottom: "Files will be updated: On · Change" on the left; **Cancel** and **Save N changes** on the right. They are always in the same place.
2. **Details** is a two-column form. Provider facts carry a small lock with the provider's name and cannot be typed over. Your own values show a purple dot and a "Reset to provider value" link. Dropdowns (season, episode number, track number) are sized to their text.
3. **Match** depends on what you are editing. For TV it is a three-column browser: **Show** (search box pre-filled; type any show), **Season** (that show's seasons), **Episode** (that season's episodes, with "This file now", "Has a file" and "Missing" tags). Your current match is pre-selected. For music it is Songs / Albums / Artists with Title, Artist and Album fields and a "Search in" picker (All providers, Apple Music, MusicBrainz). For movies, books and comics it is title + year + provider. Under the results sits **What will happen**: before → after, then four switches: Keep my own edits (on when you have any), Move into … (off; greyed with the reason when it cannot), Rename and move the files (off; greyed with the reason), Fetch artwork and cast now (on). **Use this match** stages it; the Save button count goes up.
4. **Artwork** lists every slot the item has on the left (Poster, Backdrop, Logo, Banner, then Season poster, then Episode stills) with what is there now, its size and language. On the right: tabs per source (TheTVDB, other artwork plugins, Already saved, Upload, Paste a link), a language filter defaulting to your language, a size sort, a grid of pictures, a small live preview of the page hero, and **Use this picture**.
5. **Save N changes** turns the window into **Review changes**: grouped Details / Match / Artwork / Files, one row per change with before → after, Edit and Remove, a line from the Engine saying everything was checked (or which row is refused and why), the write-back override, then Back to editing · Discard all · Save N changes. A file rename asks once more in a red dialog.
6. After saving the window closes. A slim ribbon under the top bar says "Saved 5 changes · 21:14" and shows the finishing work live ("title and poster done · cast done · episode stills running, 2 of 3"). The changed poster and rows glow for a few seconds. "See what changed" opens the History card listing each change with a tick, or an amber reason if one failed (that item then appears in Needs Review). No toasts.
7. Several ticked rows in a list open the same window titled "Editing 4 songs", with a chip per item, "Mixed, left unchanged" fields, Renumber and Change album.

**What stays the same:** desktop-only editing; nothing written before Save; provider records never modified; the four separate kinds of change (details, match, parent, files) and their switches; Needs Review opening the same editor on Match; one dialog style; the Engine's permission check.

**Scope boundaries:** no phone or TV editing; no undo journal; no new providers (the picker lists the provider plugins already enabled for that media type); the Universe explorer's lore editor is decided separately (v1 open item).

**Acceptance criteria** (each maps to a work packet):
- A1. From an episode page, typing another show's name, choosing it, a season and an episode, then Save, re-homes the file and the page shows the new show, season, number, title and still within 10 seconds. (WP1, WP3, WP6, WP9)
- A2. Re-matching a show changes its title, poster, logo (in the user's language) and cast before the window closes, and its seasons and episodes re-align in the background with visible progress. (WP2, WP9)
- A3. Review never accepts a change the save then refuses for a reason the Engine could have known in advance. (WP3)
- A4. At 1440 and at 1280 px wide, no tool shows a nested scroll bar, no text overlaps, Cancel and Save sit bottom-right of the window on every screen. (WP4, WP11)
- A5. A song can be matched to a MusicBrainz release that Apple Music lacks, with the album move and file move as separate switches that start off. (WP6)
- A6. Every entry point (page "...", episode row, track "...", lyrics button, Needs Review Fix, list selection) opens this one window; no route opens the old pop-up or the old shell. (WP10)

## 2. Scope

**In scope**
- The Editor window (or, if Shy picks Option B, the fixed side panel) and its four tools for every media type v1 covered, plus people, series, curated collections and Universes as v1 D17/D17a defined them.
- The TV Show › Season › Episode match browser; music field search with provider choice; generic search for the rest.
- Synchronous visible refresh after a TV re-match; background progress shown on the page; History "what changed".
- Review inside the window; the shared dialog style; file-rename confirmation.
- Bulk editing in the same window; all entry points; removal of the old shell and the floating picker.
- Visual proof at 1440 and 1280.

**Out of scope**
- Phones, tablets, TV (read-only as today).
- Deleting items (Delete spec; stays in the page's "..." menu).
- Photos (own View index).
- New metadata providers or provider settings (Provider settings spec).

## 3. Decisions made

Approved by Shy on 2026-10-30 with the recommended answer to every question in section 8: Q1 A (editor window), Q2 A (Save waits up to about 10 seconds), Q3 A (re-align by number, odd ones to Needs Review), Q4 A (History tab), Q5 A (enabled plugins per type, TV stays TheTVDB), Q6 A (keep the switch, on by default).

- **D1. One Editor window (Option A).** A modal window of 1160 × 820 px when the viewport is at least 1280 wide, otherwise 94 vw × (100 vh − 48 px). The page behind is dimmed and not scrollable. Reason: Ease of use at 1440 (room for a three-column match and a two-column form), one scroll area, and buttons that never move. Option B (the side panel kept, widened to 560/820 px and fixed) was drawn for comparison and not chosen (Q1).
- **D2. Fixed anatomy.** Header: thumbnail, "Editing", trail pills (each a dropdown with pictures; clicking a page item behind is not needed any more), a "N changes waiting" chip, close X. Left rail: Details, Artwork, Match, History (Artwork and Match hidden when the type has none; Universes have no Match). Footer: write-back status with a Change link on the left; Cancel and Save N changes on the right. No step strip anywhere.
- **D3. Details is a real form.** Two columns; full-width for title and description; provider facts read-only with a lock and provider name; overrides with a dot, a "provider says …" hint and Reset; character count on description; dropdowns sized to content; one "Reset all to provider values" at the top right. History is its own tab, not a block at the bottom of Details.
- **D4. Match is built per kind of item.** TV: Show › Season › Episode columns backed by the Engine's TheTVDB scoped lookup, with the show column searchable across all shows; a show page shows only the Show column plus a details card; a season page starts in its show with the Season column active. Music: Songs / Albums / Artists segments, Title / Artist / Album fields, "Search in" provider picker. Movies, books, comics: title + year + provider. Results show picture, title, creator, parent, year, edition and a provider badge; the top result carries "Best guess"; no percentages.
- **D5. "What will happen" replaces step 2.** Always visible under the results once something is selected: before → after for the facts that change, then the four switches (Keep my own edits · Move into … · Rename and move the files · Fetch artwork and cast now). "Use this match" stages; a staged match shows as a chip in the header count and in Review. For a show re-match a fifth switch "Re-match seasons and episodes" (on) re-aligns by number in the background; items that do not fit go to Needs Review.
- **D6. A TV match refreshes what you can see before Save returns.** Title, poster, backdrop and logo (user's language first) and cast are fetched inside the save, bounded to about 10 seconds with a progress line; everything else (episode details, stills, relationships, write-back) continues in the background. Season and episode matches get the same treatment for their own title, number and still. This brings TV level with movies, books and albums, which already swap artwork synchronously.
- **D7. The page proves the save.** After Save the window closes and a ribbon under the top bar shows the saved count, the time, and live progress of the background job by named step, fed by the Engine's job events. Changed hero art and rows glow for a few seconds (no toasts). "See what changed" opens the History card. A failed step shows amber with the reason and sends the item to Needs Review.
- **D8. Review is a page inside the window**, not a second dialog. Groups Details / Match / Artwork / Files; each row names the item, the field, before and after, with Edit (jumps to the tool) and Remove. The header states the Engine's check result. The write-back override sits above the footer. Footer: Back to editing · Discard all · Save N changes.
- **D9. Check equals apply.** The Engine's check for a staged match runs the same verification the save runs (for TV: the show, season and episode exist and belong together), so Review never passes a change that the save then refuses for a knowable reason. Network failures are still reported at save time, per row.
- **D10. Artwork is a tool in the window** (the floating picker is retired). Slots grouped by level; sources as tabs; language and size filters; a small live hero preview inside the tool; "Use this picture", "Remove", "Open full size". Season and episode lookups take their provider identity from the show (fixes F40), language preference applies to logos (F38), and "Fetch all" fills missing episode stills.
- **D11. Bulk uses the same window.** Title "Editing N items", a chip per item (click to edit alone), Mixed fields, Renumber, Change album; Review lists one row per item per field.
- **D12. Every entry point opens this window** on the right tool: page and row "..." → Edit (Details), episode and track rows → that item, Needs Review Fix → Match, player lyrics button → Details with the Lyrics field focused (F48), list selection bar → bulk. Deep links never open it.
- **D13. The old shell goes.** The 7,000-line shared editor shell is no longer rendered for a single item or inside any panel; what Details, Artwork, Match and History need from it is moved into the new tools or dropped. A guardrail test keeps it out. Reason: Maintenance.
- **D14. One dialog style** (v1 D20 stands): amber for a question, red for something that cannot be undone (file rename), buttons right-aligned, outlined left, primary right.
- **D15. Keyboard and focus.** Focus is trapped in the window; Esc closes it (asking Unsaved changes when needed); Tab order follows header → rail → content → footer. Screen readers hear the window title and the tool name.
- **D16. Desktop only, Engine decides permission** (unchanged). Read-only libraries open the window with fields disabled and the reason in the footer.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal | Window on Details, page dimmed behind, Save disabled with "No changes yet". |
| Pending changes | Count on Save, chip in header, dots on changed fields and on the rail tool that holds them. |
| Loading an item (trail switch) | Content area shows skeleton rows; header pills stay usable; Cancel works. |
| Match: searching | Skeleton rows in the active column; the other columns keep their lists. |
| Match: provider down | Plain line in the column ("TheTVDB did not answer. Try again.") with Retry; nothing else blocked. |
| Match: nothing fits | "Nothing called 'X' on TheTVDB. Try other words." No Add-anyway here; that stays in the Move flow. |
| Switch unavailable | Switch greyed with the reason beneath it (already there · no such album in your library · folder is read-only). |
| Review: a row refused by the check | Row marked amber with the Engine's reason; Save button says "Save N of M changes"; refused rows stay staged. |
| Saving | Footer shows "Saving… fetching poster and cast (3 s)"; buttons disabled; Esc ignored. |
| Saved | Window closes; ribbon with count, time and live steps; glow on changed parts; History card. |
| Save partly failed | Window stays on Review; failed rows amber with reasons; saved rows ticked; nothing lost. |
| Item removed meanwhile | "This item is no longer in your library"; its changes drop out of Review with a note. |
| Read-only library | Fields disabled; footer says why; Match shows but "Use this match" is disabled. |
| Narrower than 1280 (desktop) | Window 94 vw wide; Match columns become two rows (Show on top, Season and Episode below). |
| Phone / tablet / TV | No Edit entry; "Edit on a computer" line (unchanged). |

## 5. Data / model changes

- **No new stored data.** Pending changes stay in the Dashboard until Save (v1).
- **One new Engine lookup:** "TV match browser": search shows; list a show's seasons; list a season's episodes, each row carrying the full identity (TheTVDB show id, episode id, season and episode numbers, show name) and whether a library file already holds that slot. It wraps the existing TheTVDB scoped-match lookup.
- **Engine save changes:** the TV retail match gains the synchronous visible refresh (title, artwork in the user's language, cast) and the "re-match seasons and episodes" option; the edit-session check runs full verification (D9); the background job publishes named step progress on the existing live connection.
- **Contracts:** new wire types for the TV match browser, job step progress and the per-row check result; Contracts tests and fixture snapshot updated.
- **Removed:** the old shared editor shell from single-item paths, the floating picker, the step strip component.
- Pre-release: no compatibility layer; a reingest is acceptable if the TV identity state needs a clean start.

## 6. Work packets

Built in the cloud, two at a time, validated locally as one batch. Option B changes only WP4 (and the sizes in WP5–WP8); everything else is the same.

### WP1 — TV match browser (Engine)
- **What it does:** One lookup for Show › Season › Episode: show search (any show, TheTVDB), seasons of a show in default order, episodes of a season with "has a file" / "missing" from the library; every row carries the full identity the save needs. Cross-show results are real episode records, so R1 cannot happen.
- **Files likely touched:** `src/MediaEngine.Api/Endpoints/MetadataEndpoints.TvdbMatching.cs` (reuse), new endpoint beside it, `src/MediaEngine.Contracts/Metadata/*`, `tests/MediaEngine.Api.Tests/*`
- **Depends on:** none · **Size:** Complex
- **Done when:** a test asks for episodes of a different show than the item's and gets rows with show id, episode id, season and episode numbers; a second test proves a show-only result is never offered as an episode match.

### WP2 — TV save that shows (Engine)
- **What it does:** Synchronous title, artwork (language-aware) and cast refresh for show, season and episode matches, bounded in time; "re-match seasons and episodes" option for a show; named step progress events for the background job; the show's displayed title follows the new match at once (R2).
- **Files likely touched:** `src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.RetailMatch.cs`, `src/MediaEngine.Api/Services/EditSession/IdentityEditSessionChangeHandler.cs`, `src/MediaEngine.Providers/Workers/*` (artwork, cast), `src/MediaEngine.Contracts/Realtime/*`
- **Depends on:** none · **Size:** Complex
- **Done when:** after a show re-match in a test host, the detail read returns the new title and poster before the save call returns, and three progress events arrive with step names.

### WP3 — Check equals apply (Engine)
- **What it does:** The edit-session check for identity and relationship changes runs the same verification the apply runs (minus writes), so Review can mark refused rows in advance (D9).
- **Files likely touched:** `src/MediaEngine.Api/Services/EditSession/*ChangeHandler.cs`, `ItemCanonicalEndpoints.RetailMatch.cs` (validateOnly path), tests
- **Depends on:** WP1 · **Size:** Standard
- **Done when:** staging a show record on an episode is refused at check time with the plain reason, and a valid episode record passes both check and apply.

### WP4 — Editor window shell (Dashboard)
- **What it does:** The window (D1, D2, D15): header with trail pills, rail, content host, footer, dimmed page, focus trap, Esc and unsaved-changes guard, bulk title; retires the editing bar, the right panel host, the floating picker and the step strip.
- **Files likely touched:** `src/MediaEngine.Web/Components/Details/EditingBar.razor`, `EditorPanelHost.razor`, `FloatingArtworkPicker.razor`, new `EditorWindow.razor`, `wwwroot/app.css`, `Services/Editing/EditSessionFlow.cs`
- **Depends on:** none · **Size:** Complex
- **Done when:** bUnit tests for open, trail switch, Esc with and without pending changes; a screenshot at 1440 and 1280 shows the footer buttons in the same place on every tool.

### WP5 — Details tool
- **What it does:** Two-column form per type from the shared field configuration (D3); locks, dots, resets, sized dropdowns; History tab; no old shell inside.
- **Files likely touched:** `Components/Details/EditorDetailsPanel.razor` (becomes the tool), `Services/Editing/MediaEditorDetailsPresenter.cs`, `ItemFieldEditSession.cs`, new `EditorHistoryTool.razor`
- **Depends on:** WP4 · **Size:** Complex
- **Done when:** no `SharedMediaEditorShell` renders for Details or History (guardrail test) and a dropdown's width equals its longest option.

### WP6 — Match tool
- **What it does:** TV browser columns (WP1), music fields and provider picker, generic search, results rows, "What will happen" with the switches, staging (D4, D5); fixes F18 and F50.
- **Files likely touched:** `Components/Details/EditorMatchPanel.razor`, `Services/Editing/EditorMatchRules.cs`, `Services/Integration/EngineApiClient.*`, tests in `MediaEngine.Web.Tests/EditorMatchPanelTests.cs`
- **Depends on:** WP1, WP3, WP4 · **Size:** Complex
- **Done when:** tests cover: episode → another show's episode stages identity + relationship; show result never stageable on an episode; MusicBrainz release found and stageable; album move switch enabled when the album exists.

### WP7 — Artwork tool
- **What it does:** Slots by level, source tabs, language and size filters, grid, hero preview, Use / Remove / Open full size, Fetch all stills; show identity inherited by season and episode lookups (D10; F38, F40).
- **Files likely touched:** new `EditorArtworkTool.razor`, `Services/Editing/ArtworkPickerSession.cs`, artwork lookup endpoints in `MetadataEndpoints.*`
- **Depends on:** WP4 · **Size:** Complex
- **Done when:** a season poster lookup succeeds with only the show matched; the logo list is ordered by the user's language; no floating picker remains.

### WP8 — Review page and dialogs
- **What it does:** Review inside the window (D8), check status per row, write-back override, red rename confirmation, shared dialog (D14).
- **Files likely touched:** `Components/Details/ReviewChangesDialog.razor` (becomes a page in the window), `Services/Editing/EditReviewRows.cs`, shared dialog component
- **Depends on:** WP3, WP4 · **Size:** Standard
- **Done when:** a refused row shows the Engine's reason before Save; Discard all asks once; a rename asks in red.

### WP9 — After-save proof
- **What it does:** Ribbon with count, time and live steps; glow on changed hero art and rows; History "what changed" card; failures amber and to Needs Review (D7).
- **Files likely touched:** `Components/Details/DetailPage.razor`, `Services/Integration/UIOrchestratorService.cs` (job step events), new `SaveProgressRibbon.razor`, History read
- **Depends on:** WP2, WP4 · **Size:** Standard
- **Done when:** a test host emits three step events and the ribbon shows "2 of 3" then "done"; the History card lists each saved change.

### WP10 — Bulk, entry points and clean-up
- **What it does:** Bulk in the window (D11); every entry point (D12; F13, F48); removal of the old shell, pop-up launcher and dead CSS (D13); guardrail tests; docs.
- **Files likely touched:** `Components/Details/BulkEditHost.razor`, `Pages/ListenPage.razor.cs`, `Components/Listen/PlaybackLyrics.razor`, `Settings/NeedsReviewPageContent.razor`, `Components/MediaEditor/*` (delete), `docs/`
- **Depends on:** WP5–WP9 · **Size:** Complex
- **Done when:** a guardrail test lists zero references to the old shell from single-item paths; lyrics button opens the window.

### WP11 — Visual proof and final review
- **What it does:** Screenshots of every tool and state at 1440 and 1280 published as one page for Shy before merging (project rule); reviewer against this spec; accessibility pass.
- **Depends on:** all · **Size:** Standard
- **Done when:** the screenshot page exists and Shy has seen it.

Suggested waves: Wave 1 (WP1, WP2) · Wave 2 (WP3, WP4) · Wave 3 (WP5, WP6) · Wave 4 (WP7, WP8) · Wave 5 (WP9, WP10) · Wave 6 (WP11).

## 7. Local checks (SHY-DESKTOP, 1440 and about 1280 wide, real media)

- **L1.** The Continental, episode 1: open Match, type "Shimmer and Shine", pick it, Season 1, episode 3, Use this match, Save. The file should move under Shimmer and Shine S1E3 with that title and still within 10 seconds; the ribbon shows progress; History lists it. Failure: the save is refused, or the page still shows The Continental.
- **L2.** Shimmer and Shine (show): Match, pick the 2015 series, Save. Title, poster, English logo and cast change before the window closes; seasons re-align in the background with progress. Failure: anything still shows the old match after the ribbon says done.
- **L3.** A song on a mixed album: Match, Search in MusicBrainz, pick the Smile Empty Soul release; turn "Move into album" on; Save. Song moves, file stays. Then repeat with "Rename and move the file" on and confirm the red dialog.
- **L4.** Details on a show, a season, an episode, an album, a song, a book, a comic: no nested scroll bars, dropdown widths fit, Cancel and Save bottom-right every time. Check at 1440 and at about 1280.
- **L5.** Artwork: change a season poster and the show logo language; confirm the hero preview and the saved result; Fetch all stills on The Continental.
- **L6.** Edit four songs from Songs; Renumber; Change album; Review shows one row per song per field.
- **L7.** Lyrics button in the audio player opens the window on Lyrics; track "..." → Edit opens the window; Needs Review → Fix opens Match.
- **L8.** Close the window with Esc with changes pending: the Unsaved changes dialog matches the shared style.

## 8. Risks and open questions

**Risks**
- Synchronous artwork and cast fetch slows Save when TheTVDB is slow. Mitigation: bounded to about 10 seconds with a visible progress line; whatever did not finish continues in the background and the ribbon says so.
- Re-aligning seasons and episodes by number after a show re-match can mis-file odd specials. Mitigation: anything that does not fit by number goes to Needs Review instead of being guessed.
- Removing the old shell touches people, series, collection and Universe editing paths built on it in v1 Wave 6. Mitigation: WP10 last, with a guardrail test and screenshots per type.
- The size of the change is comparable to v1. Mitigation: Engine packets first (they fix R1 and R2 whichever option Shy picks), then the shell, then tools.

**Open questions:** none. Answered by Shy on 2026-10-30 (chosen):
- **Q1. Window or side panel?** A) Editor window over the page (chosen). B) Keep the side panel, fixed and widened (keeps live preview on the page, but squeezes the page to 620 px when Match is open).
- **Q2. How long may Save wait for artwork and cast?** A) Up to about 10 seconds with progress, rest in background (chosen). B) Never wait; always background with the ribbon. C) Wait for everything.
- **Q3. After a show re-match, seasons and episodes:** A) Re-align by number automatically, odd ones to Needs Review (chosen). B) Ask per season in Review. C) Leave them for the next ingestion.
- **Q4. History:** A) Its own tab in the window (chosen). B) A block at the bottom of Details as in v1. C) Only on the page's "what changed" card.
- **Q5. Providers in the Match picker (F50):** A) Every enabled provider plugin that can search that media type, TV limited to TheTVDB for identity (chosen). B) A fixed list per type chosen in this spec. C) All providers including ones not enabled, prompting to enable.
- **Q6. Should "Fetch artwork and cast now" be a switch at all?** A) Yes, on by default (chosen). B) No switch; always on.

## Plain English summary

The editor you tested is cramped because the new panel still runs the old editor inside it, and matching feels broken because of two real defects: TheTVDB only returns shows when you search by name (so an episode can never be matched into another show), and a show re-match does its real work in the background with nothing on the page to show for it. This v2 fixes both in the Engine first, then replaces the panel with one large Editor window: Details, Artwork, Match and History on the left, Cancel and Save always bottom-right, a Show › Season › Episode browser for TV, proper field search with a provider choice for music, a "what will happen" panel with four honest switches, Review inside the same window, and a ribbon on the page afterwards that shows the save landing step by step. Shy chose the window on 2026-10-30; the mockups show both at 1440 for the record.
