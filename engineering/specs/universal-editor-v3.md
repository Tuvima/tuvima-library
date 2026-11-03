# Spec: Universal editor v3 (the pop-over editor returns)

> **Status:** Draft (Q1-Q3 answered by Shy 2026-11-03; waiting for "lock it") · **Version:** 3 (replaces v1's bar-and-panel and v2's "window over an edit mode"; v2 stays Built) · **Last updated:** 2026-11-03 · **Related docs:** [Universal editor v2 (archived)](archive/universal-editor-v2.md) · [Universal editor v1](universal-editor.md) · [Libraries redesign](libraries-redesign.md) · [Product overview](../product/overview.md) · Local check list `validation/local-check-2026-11-02.md` items 18b, 18c, 37, 38 · Mockups: [`engineering/design/mockups/universal-editor-v3/`](../design/mockups/universal-editor-v3/popover-and-needs-review.html) (TV episode and Needs Review) and [every item type](../design/mockups/universal-editor-v3/every-item-type.html) (film Details and Match, book in a series, album and songs, Artwork, several songs, Review, after Save)

## 0. Why a v3

Shy's verdict after the 2 Nov local check: the editor is "still terrible". What Shy wants: press Edit and a pop-over opens **immediately**, over the page, with no editing bar and no "edit mode" first. Inside it you move show > season > episode (or album > track) **within that one item**, match properly, and re-match anything that failed. Shy confirmed on 2 Nov (23:09Z) that this reverses the Locked rule "no pop-up editor for a single item" (v1 section 1, CLAUDE.md Never 1).

What is built today (verified in code on `main`): v2 put a modal window on top of v1's **page-wide edit mode**. Edit switches the page into an editing state (the sticky editing bar, pencil leftovers, a temporary artwork view), and only then shows the window (`EditorPanelHost` draws it when `EditingModeContext` is on). So the person sees two things at once: a bar on the page and a window over it. The window also belongs to the item's own page: a single item is edited only after you are on its page (a list row or Needs Review sends you there first), and the window only knows that page's item trail.

## 1. Goal

Press Edit anywhere (an item page, a list row, Needs Review) and a pop-over editor opens at once on that item. A tree down the left shows this show (or album, or series) with its seasons and episodes (or tracks), so you can hop to any of them, see which are matched, and fix any one. Nothing is written until Save changes and a plain Review.

## 1a. Plain-English walkthrough

1. **Click "..." then Edit** on a show, season, episode, film, album, song, book or comic. The pop-over opens straight away (about 1240 by 830 px at 1440 wide; the page behind is dimmed and does not scroll). The page behind does **not** change: no bar, no pencils, no "editing" state.
2. **Left: "This show"** (or "This album", "This series"): a tree of that item's own family only. The Continental > Season 1 > the episodes. Each row shows a tick when matched, an amber "not matched" when it failed, and a purple dot when it has changes waiting. Click a row and the right side switches to that item. Changes you made on other rows are kept.
3. **Right: four tools** across the top: **Details, Artwork, Match, History**. The pop-over opens on Details, or on **Match when the item is not matched**.
4. **Match** for TV is the three-column browser (Show, Season, Episode) that v2 built, now pre-filled from the file name, with a "Best guess" on top, thumbnails, "Search all shows" (so an episode can go to a different show) and a switch "Match the other episodes in this season by number". Music: Songs, Albums, Artists with a provider picker. Movies and books: title, year, provider.
5. **Re-match is the same screen.** A failed or wrong item is opened the same way and simply searched again. There is no second "fix" flow.
6. **Save N changes** turns the right side into **Review changes** (before and after for every row, Edit and Remove), then Save. Cancel or X with changes asks "Unsaved changes". After Save, a slim ribbon on the page says what was saved and shows the finishing work live; changed parts glow briefly.
7. **From a list** (Needs Review, a library table, Recently Added) the same pop-over opens **over the list**, on Match, and the list updates behind it when you finish. You are never taken away from the list.
8. **Several ticked rows** open the same pop-over as "Editing 4 songs" (unchanged from v2 D11).

**What stays the same:** desktop-only editing; nothing written before Save; provider facts read-only; the four kinds of change (details, match, parent, files) and their switches (a match never moves files unless its own switch is on); synchronous artwork and title refresh after a re-match; Review before every save; one dialog style; deep links never open the editor; the Engine's permission check.

**Scope boundaries:** no phone or TV editing; no undo journal; no new providers; the Universe explorer's lore editing keeps working through the same pop-over; Settings stays configuration only.

**Acceptance criteria** (each maps to work packets):
- A1. On any editable item, "..." > Edit opens the pop-over with no extra click (a skeleton shows at once) and no change to the page behind it. (WP1)
- A2. From an episode, you can reach its show, any season and any other episode without closing, and unsaved changes on each survive switching. (WP2)
- A3. An unmatched or wrongly matched episode, show, season, album or song can be matched or re-matched in the pop-over in three clicks, including to a different show. (WP4)
- A4. Fix on a Needs Review row (and Edit on a library list row) opens the pop-over over that list and the list reflects the result. (WP3, WP7)
- A5. No sticky editing bar, pencil, edit-mode toggle or "temporary editing view" remains anywhere. (WP5)
- A6. The eight leftover editor items from the v2 build are done (section 3, D8). (WP6)
- A7. Visual proof at 1440 and 1280 for show, season, episode, film, album and song. (WP8)

## 2. Scope

**In scope**
- Open-at-once pop-over for every item type v2 covered, plus people, series, curated collections and Universes.
- The family tree (navigator) and per-item staged changes.
- Match improvements: start from the file name, best guess, re-match of failed items, episode to any show, match-the-rest-by-number.
- Entry points over lists, including Needs Review.
- Removal of the page edit mode and bar.
- The Needs Review / Unidentified decision (D9) and the eight v2 leftovers (D8).
- Rewording CLAUDE.md Never 1 and the docs when this is Locked.

**Out of scope**
- Why episodes are not being matched in the first place (local check 18b is also a bug: no episode art, no episodes matched). That is the "Fix bugs from the 2 Nov review" thread. This spec only guarantees the editor can fix them by hand.
- Phones, tablets, TV; Delete (stays in "..."); Photos; new providers.

## 3. Decisions made

*Nothing here is approved until Shy locks it. Items marked **Shy asked** come from Shy's 2 Nov words; the rest are recommendations.*

- **D1. A pop-over, not an edit mode. (Shy asked.)** Edit opens the editor directly. The page behind keeps its state and is not put into any "editing" state. Reason: Ease of use (one step, nothing to learn) and Maintenance (one surface; v2 carried two).
- **D2. Same size and anatomy as v2's window, plus a family tree.** About 1240 × 830 px at 1440 wide (94 vw by 100 vh minus 48 px below 1280). Header (thumbnail, "Editing: name", "N changes waiting", X), left tree (about 300 px), top tool tabs, one scroll area, footer with write-back status on the left and Cancel and Save on the right, always in the same place.
- **D3. The tree is the navigation. (Shy asked: "navigate show > season > episode within that show only".)** TV: show > seasons > episodes (files without an episode shown too). Music: album > tracks. Books and comics: series > books or issues. Films: no tree (the left column is hidden and the tools use the full width). Rows carry matched, not matched and "changes waiting" marks. Pending changes on every row are kept as you switch. Reason: Ease of use, and it doubles as the place you spot the one unmatched episode.
- **D4. Match is the place to fix and re-fix. (Shy asked.)** One Match tool for first-time match and re-match; it opens on Match when the chosen row is not matched. Search is pre-filled from the file name (parsed show, season, episode). The v2 TV browser stays (Show, Season, Episode columns, "Search all shows" so an episode can go to another show). New: a "Match the other N episodes in this season by number" switch (on by default when a show is chosen), and a "Best guess" tag on the top result.
- **D5. Reuse what worked; leave behind what did not.** Reuse from v2's built code: the staged-changes and Review machinery, the TV match browser, the Artwork and History tools, the bulk chips, the saved ribbon, the Engine's synchronous refresh after a TV re-match. Reuse from the **older pop-up editor** (the "SharedMediaEditorShell" era, Aug to Oct 2026, last whole in commit `afa6439a3^`): (a) its item-level selector (`EditorContextNavigator`: a dropdown per level with pictures), now drawn as the tree; (b) its paged child browser (the Dashboard's `MediaEditorOwnedChildBrowser`, removed in v2 wave 5b and recoverable from `afa6439a3^`, plus the Engine's `MediaEditorOwnedChildReadService`, which still exists), which already solved listing hundreds of episodes or tracks without loading them all; (c) the "Move to another season or show" episode placement flow with a destination preview (Sept 2026 plan "Editor matching by the identity being edited"); (d) per-level matching rules (an episode lists only its season's episodes, Season 0 is explicit, a music track matches an exact release track the Engine verifies); (e) empty parents are kept, not deleted, after a move. **Not** reused: the 7,000-line shell itself, its Options, Links and File tabs (Details stays lean), and the "apply artwork immediately" behaviour (nothing saves before Save).
- **D6. Everything opens it, including from lists. (Shy asked: "same for music, movies, etc.")** The pop-over is hosted by the app layout, not by the item page, so it can open over any list. It needs only an item id and a starting tool. Deep links never open it (unchanged).
- **D7. The page proves the save.** Unchanged from v2 D7: after Save the pop-over closes, a slim ribbon shows the saved count and live finishing work by named step, and the changed picture and rows glow briefly. No toasts.
- **D8. The eight v2 leftovers are part of this build** (they are one row in `validation/batch.md`): (1) named step-by-step progress; (2) glow on changed parts and "See what changed"; (3) the "Re-match seasons and episodes" switch and cast in the save; (4) "Fetch all" stills, and slots grouped by level; (5) lyrics field focused when opened from the player's lyrics button; (6) the dimmed page's own scrollbar (fixed by design: the page is locked while the pop-over is open); (7) "Reset all" only for saved overrides; (8) a Before value on every Review row. Also verify show, season and album Match on screen.
- **D9. Needs Review and Unidentified: option B, agreed by Shy 2026-11-03 (Q1).** Shy's reason: Needs Review must be central because an item can be filed under the wrong media type entirely, so a per-library list cannot hold it. Today there are two admin lists: **Needs Review** (items the Engine identified but was unsure about) and each library's **Unidentified files** tab (files it could not identify, with Try again, Keep as it is, Remove). Now that unmatched files also appear in the library list, three paths:
  - **A. Fold both into the library lists.** A "Needs attention" filter on each library list; Needs Review page and Unidentified tab removed. *For:* least duplication. *Against:* library lists are for everyone, Needs Review is admin-only; reasons, bulk Try again and "Keep as it is" have no natural home; the product still names "Needs Review" as a concept.
  - **B. One Needs Review list, with the Unidentified tab removed. (Recommended.)** Needs Review lists both kinds together (a Why column that says "Not matched" or "Not sure" in words, a Best guess with one-click "Use this", Last tried, Fix). Filters All / Not matched / Not sure and a library filter; bulk Try again and Keep as they are. Library lists keep a small "Not matched" mark and a filter chip that links here. Fix everywhere opens the pop-over on Match. The library page's "N files not identified" link goes to this list filtered to that library. *For:* keeps the named concept and the admin-only control, removes the weak Unidentified view, no loss of the useful actions, cheapest to build on the pop-over. *Against:* one more place than option A.
  - **C. Keep both, just redo the Unidentified view.** *For:* smallest change. *Against:* the duplicate remains and the confusion Shy noticed stays.
  Mockup screen 2 shows option B.
- **D13. A Needs Review notifier. (Shy, 2026-11-03.)** When anything needs review, a small notifier with the count shows on the Settings entry in the top-bar dropdown and on Needs Review in the Settings menu, and disappears at zero. It is the one place the app says "something needs you"; library lists only carry the quiet "Not matched" mark. Admin-only, like the list itself.
- **D10. Re-matching is always allowed and always reviewable.** Any item can be matched again; Review shows before and after; a TV re-match refreshes title, poster and cast before the pop-over closes (v2 D6 stands).
- **D11. Keyboard and focus.** Focus is trapped in the pop-over; Esc closes (asking "Unsaved changes" when needed); tree rows are a single tab stop with arrow keys; Tab order is header, tree, tools, content, footer; screen readers hear "Editing" plus the item's name.
- **D12. Desktop only; the Engine decides permission** (unchanged). Read-only libraries open with fields disabled and the reason in the footer. A phone or TV never shows Edit.

## 3a. How each kind of item behaves (see the every-item-type mockups)

| Item | Tree on the left | Opens on | Match fields |
|---|---|---|---|
| TV show, season, episode | Show > seasons > episodes (and files with no episode) | Match if unmatched, else Details | Show, Season, Episode columns |
| Film, standalone book, standalone comic | None; tools use the full width | Match if unmatched, else Details | Title, year (author for books), provider |
| Book or comic in a series; audiobook variant | Series > books or issues; audiobook shown as a variant of its book | As above | Title, author, provider; moving to another series is a separate switch |
| Album, song | Album > songs | As above | Songs, Albums, Artists; Title, Artist, Album; provider picker |
| Several ticked rows | None; a chip per item | Details | Mixed fields left unchanged |
| Person, series, curated collection, Universe | None | Details | Match only for people (provider record) |

**Moving a song to another album** (for example from Greatest Hits to its real album) is done in the song's Match tool on the Albums segment: pick the correct album, see its songs with this one placed on its matched track, and the switch "Move to that album" is on. The old album is kept even if it empties. Several songs at once use Change album in the multi-edit (mockup screen 4b). With several songs, each is matched to a track on the chosen album by title (and track number from its tags). Any song that is not on that album is flagged in Review as "Not on this album" with the reason, before saving, and is left where it is; the others still move. You can remove that row, or pick a track for it by hand. Nothing fails silently or after the save. The same pattern moves an episode to another season or show and a book to another series.

Artwork and Review work the same for every type; only the slots in Artwork differ (poster, backdrop, logo for a film; cover for a book or album; season poster and episode stills for TV).

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal | The pop-over over the dimmed page; tree on the left (when the type has one), tools on the right. |
| Opened from a list row | The same pop-over over the list; list refreshes the row when it closes. |
| Loading the tree | Tree rows show a short skeleton; the right side is usable at once for the clicked item. Long seasons and albums page in as you scroll (reusing the child browser). |
| Item not matched | Opens on Match; the search box already has the file's parsed name; the Match tab shows "Not matched yet". |
| Nothing found | "Nothing matches that." with a link to widen to all providers, and the match-by-hand fields for show, season and episode numbers. |
| Engine unreachable | Pop-over opens with a plain error line and "Try again"; nothing is lost if changes were already staged. |
| Provider down | The Match tool says which provider failed and offers the others; Details and Artwork still work. |
| Partly ready after Save | Ribbon shows named steps with ticks; amber with the reason if one step failed, and the item then appears in Needs Review. |
| Item deleted while open | The existing "Removed while open" notice replaces the item; staged changes on other items stay. |
| Device stops qualifying mid-session (window resized, pointer lost) | The pop-over closes through Unsaved changes; nothing is lost silently. |
| Browser Back or route change with changes staged | The existing navigation guard asks Unsaved changes. |
| Edit pressed again while changes wait on another item | Opens the same pop-over on the new item; the staged changes stay in the one session. |
| Vault items in the tree while Vault mode is off | Not listed; the tree never reveals them. |
| Read-only library | Fields disabled; the reason in the footer, from the Engine's answer. |
| Narrow window (below 1280) | Tree collapses into a dropdown at the top of the right side. |

## 5. Data / model changes

- **None to stored data.** Pending changes stay in the existing staged-changes session (held in the Dashboard until Save).
- **Engine:** a read that lists an item's family (parent chain plus paged children, with matched state and a "not matched" reason) for the tree. Most of this exists as the older owned-child read; exact reuse is to be confirmed in WP2. A scout confirmed the navigator and owned-children reads (`/metadata/{id}/navigator`, `/owned-children`) still exist in the Engine; WP2 checks they return matched state and the not-matched reason. Reads for Needs Review gain one list that merges the review queue and the unmatched-files list (only if D9 option B is chosen).
- **Wire types:** any new type goes in `MediaEngine.Contracts` with fixtures and tests updated.
- Pre-release data: no migration needed.

## 6. Work packets

Waves follow "depends on". At most two at once.

- **WP1. Pop-over host without edit mode** (Complex). Today nothing editing-related is registered app-wide: `DetailPage.razor` (about lines 236-252) creates `EditingModeContext`, `ItemFieldEditSession`, `EditSessionState`, `EditSessionSaver`, `EditPendingPreview` and `ArtworkPickerSession` per page, and the panels (`EditorDetailsPanel`, `EditorMatchPanel`, `FloatingArtworkPicker`) take the mode object. So WP1 adds a scoped `EditorHost` service that owns the field session, pending changes, saver, picker session and the permission answer, rebinds the panels to it, and exposes `OpenAsync(entityId, tool, trail?)`. The pop-over is itself a modal `<dialog>` opened with `showModal` through `app-dialog.js`, so it stacks above the details overlay (`DetailsOverlayHost`), which makes everything outside it inert; when the overlay closes with Back while the editor is open, the editor closes first (asking Unsaved changes). Universe and Chronicle (`UniversePage`, `ChronicleExplorer`, which use the bar and host with History off) move to the same host in this packet; the History tool is hidden where it does not apply. Files: `Components/Details/EditorPanelHost.razor`, `EditingBar.razor`, `Services/Editing/EditingModeContext.cs`, `EditSessionFlow.cs`, `ItemFieldEditSession`, `ArtworkPickerSession`, `EditPendingPreview`, `DetailPage.razor`, `UniversePage.razor`, `ChronicleExplorer.razor`, layout. Depends on: none. Done when: "..." > Edit on a movie, a Universe and a Chronicle character opens the pop-over with the page behind unchanged (no bar, no pencils, same scroll position) and Save works.
- **WP2. Family tree** (Complex). Left tree for TV, music, series; paged children; matched, not-matched and pending marks; switching keeps staged changes. Reuse `EditTrailCatalog`, the old child browser (recover from `afa6439a3^`) and the Engine's `MediaEditorOwnedChildReadService`. Depends on: WP1. Done when: from an episode you can open its show, another season and a third episode with a change staged on each, and the Save count covers all three.
- **WP3. Entry points over lists** (Standard). The edit permission comes from the Engine's per-item answer: list rows and Needs Review get it from the row actions (or one small read before opening); the pop-over never opens without it. Lists today use a separate bulk host (`BulkEditHost`, `BulkEditSession`); WP3 replaces it with the one service so there is a single bulk path. Row "..." Edit, episode and track rows, ticked-row selection bar, Needs Review Fix, library Unidentified Identify, player lyrics button all call the one service; list refreshes on close. Depends on: WP1. Done when: Fix on a Needs Review row opens the pop-over over the list on Match and the row updates after Save.
- **WP4. Match for failed items and episode to any show** (Complex). Includes the Engine work: an action for "match the other N episodes by number" and a read that returns the parsed file name and best guess, with Contracts types, fixtures and tests. Open on Match when unmatched; pre-fill from parsed file name; "Best guess"; "Match the other N episodes by number"; "Move to another season or show" placement with destination preview (reuse the Sept 2026 flow). Depends on: WP2. Done when: an unmatched episode is matched to a show other than its folder's in three clicks and the page shows the new show, season, number, title and still within about 10 seconds.
- **WP5. Retire the old surfaces** (Standard). Delete, not just hide, everything the pop-over replaces: the sticky bar, the per-field pencils and their hover styling, the on-page live preview of pending values, the temporary artwork view, the floating artwork picker, the edit-mode state and any component, style, string, test or guardrail that only served them; guardrail test that nothing renders them; reword CLAUDE.md Never 1, `docs/product/presentation-rules.md` and the tuvima-docs editor pages (a pair of changes). Depends on: WP1, WP3. Done when: guardrails pass and a text search finds no live use.
- **WP6. The eight leftovers** (items 1 and 3 are Engine-side, job events and the re-match cascade with cast, so Complex; the rest Standard; split in two packets). D8 items. Depends on: WP1. Done when: each item's own check in `validation/batch.md` is met.
- **WP7. Needs Review (option B only)** (Standard). Merge the two lists into one table with Why, Best guess, Last tried, Fix; filters; bulk actions; remove the Unidentified tab and re-point its links and counts. If Shy picks A or C, this packet changes accordingly. Depends on: WP3 and Shy's answer to Q1 (blocked until then). It supersedes the Unidentified tab built under Libraries redesign D6 before its Local checks have run. It must keep everything Needs Review does today: Wikidata reviews with "Keep provider match", the movie-looks-like-TV card with "Keep as film", Dismiss, and "Find in recent history"; so the Why column carries the real trigger in words, not just two values. Includes the D13 notifier (top-bar dropdown and Settings menu). Done when: the library page's "N files not identified" opens Needs Review filtered to that library and lists exactly N, and the notifier count matches the list.
- **WP8. Visual proof** (Mechanical). Screenshots at 1440 and 1280 of show, season, episode, film, album, song, and the Needs Review list, one page for Shy before merge. Depends on: all above.

## 7. Local checks (for `validation/batch.md`)

On SHY-DESKTOP with the small generated library (never the real library): open Edit on a show, season, episode, film, album and song and see the pop-over appear at once with the page unchanged behind it; hop around the tree with changes staged; match an unmatched episode to another show; Fix from Needs Review; check the eight leftovers; and on real media (`Z:\Development\testing\media\real_media`) try re-matching a wrongly matched show and a file with no episode number. Failure: any bar or pencil on the page, a lost staged change when switching, or a match that does not show on the page.

## 8. Risks and open questions

**Risks.** (1) Reversing a Locked rule: CLAUDE.md, v1 and the public docs must change together (WP5). (2) The tree on very large families (hundreds of episodes) must page, not load all. (3) The editor depends on matching actually working; the separate bug (episodes not matching) can make the tree look all-amber until it is fixed. (4) The Needs Review change touches counts shown on the Libraries overview.

**Open questions for Shy**
- **Q1. ANSWERED (Shy, 2026-11-03): B, plus a notifier (D13).** Was: Needs Review and Unidentified. Note this changes what you locked on 30 Oct (Libraries redesign: Unidentified files get their own tab) and differs from your 2 Nov lean (merge into the library list and redo Unidentified, closest to A or C).  A fold into library lists, **B one Needs Review list (recommended)**, C keep both and redo the Unidentified view (see D9).
- **Q2. ANSWERED: A, tree on the left.** Was: Tree on the left, or a trail of dropdowns on top?** **A tree (recommended)**: you see everything and spot the unmatched one. B dropdown trail (v2 style): less space used, but you cannot see which episodes are unmatched.
- **Q3. ANSWERED: A, on when a show is chosen.** Was: "Match the other episodes by number" on by default?** **A yes when a show is chosen (recommended)**, B off.

## 9. Gaps and drift

- v1 D2 and D3 (edit lives on the page, pencils) and v2 D1 (window over edit mode) are superseded by D1 above (Shy asked; record on lock).
- v1 D16 (Needs Review opens the panel) is satisfied by D6.
- CLAUDE.md Never 1 wording ("sticky editing bar", "no pop-up editor") is out of date once this is locked (WP5).

## Plain English summary

Today Edit first puts the page into a special editing state with a bar, and only then shows a window. That is why it feels clumsy. This spec makes Edit open a pop-over straight away, like the older editor did, with a small tree down the left showing the show, its seasons and episodes (or an album and its tracks) so you can jump to any one and fix it. A failed or wrong match is fixed on the same Match screen, including sending an episode to a different show. We keep the good parts built last week (the show, season, episode browser, Review before saving, the saved ribbon) and the best ideas from the older editor (a long list that loads in pages, and "move this episode to another season or show"). We also finish the eight small leftovers. For Needs Review, I recommend keeping one list with a clear reason for each item and one-click fixes, and dropping the separate Unidentified tab. Nothing is built until you say it is locked.
