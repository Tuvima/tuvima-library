# Spec: Universal editor v6: Match view and aligned rows

> **Status:** In progress · **Version:** 1 · **Last updated:** 2026-11-12 (locked by Shy, 2026-11-12 02:00Z: "build it here") · **Related docs:** [Universal editor v5](archive/universal-editor-v5-header-navigation-and-supporting-files.md) · [Look pass v3](look-pass-v3.md) · [Look v3.1](look-pass-v3-1-controls-and-type.md) · [Mockups](../design/mockups/editor-v6/editor-v6-mockups.html) · [Product overview](../product/overview.md) · Issues 870, 871, 876, 878, 879, 881 (editor part), 899 (alignment part)

## 0. Why

The local check of 2026-11-11 found that the editor built from v5 works, but its Match tab is hard to read and its rows do not line up. Shy drew a target for the Match view (described in issue 870; screen A of the mockups follows it). This version redraws Match to that target, makes every editor row line up by construction, and fixes the editor bugs found in the same check. Everything else in v5 stays, with one large exception decided on 2026-11-12: the editor now works on phones and tablets too (Shy: "Most people use only their phones so we should build with this in mind and make the phone equivalent to the desktop in every way we can (other than setup).").

## 1. Goal

Fixing a wrong match should take one glance: your file on one line, the candidates on the left, the chosen one on the right with a large cover and a plain table that shows why it fits. Rows in the editor (and in other forms that share the problem) keep their controls on one line whatever text sits around them.

## 1a. Plain-English walkthrough

Numbers match the work packets in section 6.

1. **Rows that cannot go crooked (WP1).** Today each screen lines up its own rows, and a label or a helper line above one field pushes that field down while its neighbors stay put. One shared row part replaces that: a label line, a control line and a message line. Every control in a row sits on the control line. Helper, status and error text always goes on the message line under the whole row. Nothing can push a neighbor.
2. **Match gets room (WP2).** On the Match tab the large header shrinks to one line: a small cover, "Match book" (or movie, episode, song), and a colored dot with the state ("Unmatched", "Matched to Apple Books"). Under it are two tabs, **Media providers** and **Wikidata**. They replace the switch that used to sit at the right of the search row, so the search row has the full width.
3. **One search row (WP2).** Provider, then the fields for this kind of item (a book: Title, Author, ISBN (optional)), then a **Search** button with the word on it. All one height. The provider list shows the provider's mark and name only. A state appears only when something is wrong ("Needs a key", "Off"). Nothing is searched until you press Search or Enter.
4. **Your file, on one bar (WP2).** File icon, the file name, and what the file itself says: embedded title, author, format.
5. **Results on the left (WP3).** Small cover, title, then author · year · series and number. A tick marks the chosen row. Collections and box sets say "Collection". No pill and no description in the list. Six rows, then "Show 4 more results".
6. **The chosen result on the right (WP3).** A large cover to compare with your own, title, series and number, author and year, a link to the provider, two lines of description with "Read more". Then the verdict in words, and a table: Field · Your file · Selected result, with a tick where they agree and a dash where one side has nothing. A note says what the verdict is based on. **Apply match** sits at the bottom right with one line saying what it does ("Links this book to Apple Books").
7. **A matched item says so (WP4).** The tab opens on the current match in the same card, with Open source, Change match and Remove match. The bug where every item said "Not matched yet" is fixed first.
8. **ISBN search works (WP5).** An ISBN by itself, 10 or 13 digits, with or without dashes, is enough to search.
9. **TV, music and Wikidata use the same shape (WP6).** Same row, same bar, list left, card right. TV still picks the show and then the episode. Music still picks the release and then the track.
10. **Details tab tidy-up (WP7).** Fields line up (Sort title beside Rating). The file path leaves Quick Facts and shows once at the bottom of that panel on one line, shortened in the middle, with the full path on hover and a copy button. The Read-only and Write-back pills go, because the footer already says it.
11. **Header fixes (WP8).** The cover shows properly. The home and search icons are clean outlines. Episode pictures appear in the Season and Episode list.
12. **The series chooser and the folder view (WP10).** "Which series is this video in?" gets a sentence that explains it and a Cancel. The root folder line and up arrow at the bottom of the supporting files view go.
13. **The playlist rule rows (WP11).** The smart playlist editor's rule rows move onto the shared row from item 1.

14. **The editor on a phone (WP13 to WP17).** Edit appears in the "..." menu on a phone and tablet exactly as on a desktop, and opens the same editor as a full-screen sheet. Every tab is there. The layouts are in section 4a.

**What stays the same:** the header navigation on every other tab, locked fields, Review changes and Save changes, Apply only stages, Delete, Bookmarks, History, Artwork, the supporting files browser. Who may edit is still decided by the Engine's permission check. First-run setup stays a desktop job.

**Example.** Shy opens *The Sea of Monsters*, which arrived unmatched. Match shows "Match book · Unmatched", the file bar, and Title and Author already filled in. Search returns ten results. The first is chosen: its cover is large on the right, the table shows Title and Author agree, and it says "Strong match". Apply match, then Save changes.

## 2. Scope

**In scope**
- The Match tab for every kind of item, both sides, matched and unmatched.
- The whole editor on phones and tablets: every tab, Review changes, the supporting files browser, editing several items, Delete, and adding to a collection or playlist.
- One shared row part, used by the editor's Match and Details tabs and by the smart playlist rule rows.
- Editor bugs from the 2026-11-11 check: "Not matched yet" on matched items, header cover, icon fill, episode pictures in the dropdown, the series chooser, the folder line and up arrow, Quick Facts path, redundant pills, type sizes in Match.
- ISBN in the editor's search.
- Reorder videos in the editor header's Season, Section and item lists (issue 877, the editor part; Shy, 2026-11-12: "the reorder overflow was in the editor panel, where the dropdowns are for season/episodes").

**Out of scope**
- Why ingestion gave up after a wrong ISBN answer, and the title fallback (issue 881, ingestion part). It belongs to the combined ingestion fix.
- Fetching episode pictures that were never downloaded (its own thread, "Fix episode stills for every TV show").
- Shared dropdowns, dialogs and menus (bundle A, issues 896, 900, 901). This spec uses whatever bundle A delivers and defines none.
- The playlist editor narrowing its lists to the chosen media type (issue 899, second part).
- The Details view under the hero on a Series page, which still uses the old layout (issue 874). Shy, 2026-11-11: it is the details page itself, not the editor, and an approved mock for it was never built. It goes to its own thread.
- The curly braces in a Series name (issue 873).
- Settings pages that are also hidden on phones today (plugin setup, import, the ingestion flow editor) and the Vault's tools: listed in section 9 for their owners, not changed here. Shy: left to their own threads.
- First-run setup on a phone.

## 3. Decisions made

Shy confirmed every proposal below on 2026-11-12 01:04Z ("agree with all": answers 1A, 2A, 3A, 5A, 6A); items marked (Shy) were decided earlier in the issues.

- **D1. Media providers and Wikidata are tabs under the Match title, not a switch on the search row (Shy, issue 870).** Reason: the search row gets the full width. **As drawn in the mockups (screen A, note 3; Shy agreed 2026-11-12 02:00Z):** the editor keeps its own tab row (Details, Artwork, Match, Bookmarks, History), which the target picture did not show, so the two sources are drawn as a two-way selector on its own line under it, not as a second underlined row. It still takes nothing from the search row.
- **D2. On the Match tab the editor header is one line** (small cover, "Match <kind>", state dot and words). The header navigation stays reachable from that line. Reason: the target needs about 900 px of height; with the full header the results and the card would scroll inside a small box.
- **D3. One search row:** Provider, the fields of the kind, a labeled Search button. All controls 44 px, labels above. Search runs on the button or Enter only. This replaces v5's icon-only Search and search-on-change.
- **D4. Provider entries show a mark and a name; a state shows only when it is not ready (Shy).** Real provider marks ship where the provider's brand rules allow it, read per provider during the build; a letter badge otherwise.
- **D5. "Your file" is one bar** under the search row: file name, then embedded title · creator · format.
- **D6. Result rows carry no verdict pill and no description (Shy).** The verdict is said once, in the card.
- **D7. The card:** large cover (about 140 × 210 for portrait art, 16:9 for episodes, square for music), title, series and number, creator · year, provider link, two-line description with Read more, verdict line, compare table, basis note, Apply match with its one-line explanation.
- **D8. Verdict words: Strong match, Possible match, Weak match** ("Check" is retired). Apply stays allowed for all three (v5 D23).
- **D9. The compare table** has one row per fact the Engine compared for this kind (book: Title, Author, ISBN, Year; movie: Title, Year, Runtime; episode: Show, Season and episode, Title, Runtime; song: Artist, Album, Title, Length). Tick = agree, cross = disagree, dash = one side unknown. Unknown never counts against the match (v5).
- **D10. A matched item opens on the same card** filled with the current match, with Open source, Change match, Remove match. Change match reveals the search row and list.
- **D11. ISBN alone is a valid search.** Where the provider can look up an ISBN it does; where it cannot, the row says so in one line and asks for a title.
- **D12. One shared row part owns alignment.** Label line, control line, message line. The alignment check that already exists fails the build's proof when a row is off. Reason: Maintenance; the same bug stops recurring on each new screen.
- **D13. Type sizes come from the shared look's named scale, and this spec sets none** (Look v3.1, Locked, D8 to D12). In Match: "Match book" is Heading (20/600); the state words, field labels, counts, meta lines, the basis note and the line under Apply match are Secondary (13); tab labels, control text, result titles and the verdict line are Control (14/500); "Search results" and the card title are Subheading (16/600); the description and table cells are Body (14); the "Your file" caption and the table's column headings are Caption (12/600). The same scale applies to the rest of the editor window: its title becomes Heading and control text becomes Control. The tabs use the shared tab row (48 px tall, underline for the chosen one), and dropdowns, menus and confirmations are the shared look's parts as delivered.
- **D14. Quick Facts lose the file path; it shows once at the foot of the panel (Shy).**
- **D15. The Read-only and Write-back pills go; the footer line stays (Shy).**
- **D16. The supporting files view has no root folder line and no up arrow at the bottom (Shy).** Inside a subfolder, the path at the top is how you go back, and it never goes above the item's own folder. *The second sentence is this spec's reading of that decision.*
- **D17. The series chooser explains that it is for exceptions (Shy asked 2026-11-11: "if I have a folder of videos I've collected together, isn't that the series built by default?" Yes).** A folder of several videos is already a series, its sub-folders are sections, and nobody needs the chooser for that (Videos D4). The chooser is only for what the folders did not do: a video sitting loose in the library folder, a folder with a single video, or a video that belongs with a different series than the folder it sits in. Heading: "Move this video to another series". Sentence: "A folder of videos is already a series. Use this only for a video that sits on its own or belongs with a different series." Choices: the series it is in now (marked "from its folder" when that is where it came from), other series, "Start a new series", "On its own". Cancel and Esc go back with nothing changed; nothing is saved until Save changes. What happens to the file is D24.
- **D18. Icons in the editor header, tab row and search are outlined and use the normal icon color (Shy).**

- **D23. Reorder in the header lists fits the menu and can be backed out of (issue 877).** The "Reorder videos" and "Reorder sections" rows are one short line each, with no hint text inside the menu, so the menu never scrolls because of them. Reorder mode ends with **Save order** or **Cancel**, and Esc cancels; "Done" goes. Save order stages the new order like any change (Save changes still writes it); Cancel puts the list back as it was. The two buttons are the shared Reorder bar that the Collections and View work builds for the playlist rail, the gallery rail and collection items; this spec reuses that bar and builds none. Reason: one reorder behavior everywhere, and a way out of a mistake.
- **D24. A series change follows the editor's one rule for files (Shy, 2026-11-12: "I want B. The mock should match something like what the editor does today about the warning details on writebacks").** In a **Managed** folder, saving a changed series moves the file into that series' folder (a new series gets a new folder named for it; "On its own" moves it back to the library's top level). Before saving, the footer shows the existing amber move line, unchanged: "This file will move when you save" with "Managed folder · from → to", and Review lists the move. In a **Read-only** folder nothing on disk changes: the line under the choice says "Set by you. The file stays where it is.", the footer keeps its normal write-back line, and the series exists in Library only. Reason: disk and Library agree wherever Library manages the files, a hand-made series survives a fresh start there, and it is the same rule as a changed match and a changed Belongs to (v5 D30). **This reverses editor v3.1 D3** ("Series and order changes never move files") for series membership; order changes still move nothing unless the rename switch is on. Mock screen G.
- **D19. The editor works on phones and tablets (Shy, 2026-11-12, option C in the Needs Review thread).** The rule that hid every edit launcher and refused every edit handler on anything but a desktop with a mouse is retired for the editor, everything that opens it, Delete and collection membership. The Engine's permission check is unchanged. Reason: most people use only their phones. This reverses v4 and v5 and CLAUDE.md "Never" 1's last clause.
- **D20. Same editor, same rules, different arrangement.** A phone gets no cut-down editor: the same tabs, locks, Review changes, staged Apply, Save changes and Delete. Only the layout changes (section 4a). Reason: Maintenance (one editor to test) and the equivalence Shy asked for.
- **D21. Nothing on a phone depends on hover or a right-click.** Every tooltip that carries meaning (why a field is locked, a shortened path) is reachable by a tap. Touch targets are at least 44 px. Text typed into a field is 16 px on phones so the browser does not zoom.
- **D22. On a phone, Delete lives in the header's "..." menu** and the footer holds only Discard and Save changes. Reason: three stacked full-width buttons would cover a third of the screen, and a red button under the thumb invites accidents. The confirmation is unchanged.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Unmatched item | One-line header with a yellow dot and "Unmatched"; search row filled from the file; no results until Search. |
| Matched item | Green dot, "Matched to <provider>"; the card with the current match and its three actions. |
| Searching | The list area shows "Looking up…" on its own row; the search row does not move. |
| No results | "Nothing found for that. Try fewer words or the ISBN." The file bar and fields stay. |
| Provider needs a key or is off | The state shows beside its name; Search is disabled with the reason on the message line. |
| Provider did not answer | One line naming the provider and Try again; other tabs work. |
| ISBN typed, provider cannot look it up | Message line: "<Provider> cannot search by ISBN. Add a title." |
| No provider for this kind (course, supporting file) | "No provider matches this kind of item." No search row. |
| Window below 1280 wide | The card moves under the list; the search row wraps as whole fields, never mid-field. |
| Phone or tablet | The editor as a full-screen sheet (section 4a). |
| TV | No editor (unchanged). |

## 4a. The editor on a phone

Tablets in portrait use the phone arrangement; tablets in landscape use the desktop one at its narrow-window size.

| Part | On a phone |
|---|---|
| Window | A full-screen sheet. Close at the top right. Swiping down never discards; leaving with changes asks, as on desktop. |
| Header | Always the one-line form: small cover, title, state. Tapping the line opens the navigation as a bottom sheet: Home, Season or Section, the item list with pictures, supporting files, the folder button. |
| Tabs | The shared tab row, scrolling sideways when the tabs do not fit. |
| Details | One column. Each field keeps its label above and its lock at the right; tap the lock to unlock, tap it again for Revert. Quick Facts is a section under the form, closed by default, with the path line at its foot. |
| Artwork | Two pictures per row. Add from the phone's photos or camera as well as from providers. The picker is a full-screen step. |
| Match, search | The two tabs; then Provider, each field and Search stacked, one per line, Search full width. The "Your file" bar wraps onto two lines. |
| Match, results | The list fills the width. Tapping a row opens the card as a full-height sheet: large cover, facts, verdict, compare table, with Apply match fixed at the bottom. Back returns to the list with the tick kept. |
| Match, TV and music | The same steps as desktop, one step per screen: show, then season and episode; release, then track. |
| History | A list; each entry's details open on tap. |
| Bookmarks | Rows with the frame, time and name; row actions in "...". Export and Import use the phone's file picker. |
| Supporting files browser | Full screen. Search at the top; filters and the Folders view in a sheet. "Select" turns on ticking; the bulk actions appear in a bar at the bottom. |
| Reorder videos | Drag by the handle, or Move up and Move down in the row's "...". The shared Reorder bar with Save order and Cancel sits at the bottom. |
| Editing several items | Started from "Select" in a list (or a long press on a row). The same Details and History tabs. |
| Review changes | A full-screen step listing every change, with Back and Save changes. |
| Footer | The write-back line, then Discard and Save changes side by side. Delete is in the header's "..." (D22). |
| Keyboard open | The footer stays above the keyboard; the field being typed in scrolls into view. |
| Offline or Engine unreachable | The same plain error line and Try again; staged changes kept. |

## 5. Data / model changes

None to stored data. The Engine's answer for a search result gains the compare-table rows (field, your file's value, the result's value, agree / differ / unknown), a "collection" mark, and the series name and number where the provider gives them. Wire types and their fixtures change with it.

## 6. Work packets

File names are from a first read of the code and are to be confirmed by the build.

- **WP1. Shared row part (Standard).** A row component with label, control and message lines; preview page entry; the alignment check and its guardrail learn the new part. Files: `src/MediaEngine.Web/Components/Shared/` (new), `tools/validate-alignment.js`. Depends on: nothing. Done when: the check passes on the preview page with a row that has a helper under one field only.
- **WP2. Match frame (Complex).** One-line header on Match, the two tabs, the search row on WP1, labeled Search, provider entries per D4, the file bar. Files: `EditorMatchPanel.razor`, `EditorMatchRow.razor`, `EditorItemHeader.razor`, `EditorPopover.razor`, localization. Depends on: WP1. Done when: at 1280 and 1440 every control in the search row has the same top and height, and nothing searches before Search or Enter.
- **WP3. Results and card (Complex).** List rows per D6, "Show more", the card per D7 to D9; the Engine adds the compare rows, collection mark and series facts. Files: `EditorMatchResults.razor`, `EditorMatchSearchService.cs`, `EditorMatchVerdictCalculator`, `src/MediaEngine.Contracts/Editing/`, fixtures. Depends on: WP2. Done when: a book search shows the table with ticks and dashes and no pill in the list.
- **WP4. Matched state (Standard).** Start with a failing test that reproduces "Not matched yet" on a matched movie, book and episode; fix the cause; show the current match in the card. Suspected, not proven: the tab trusts the page's "unmatched" flag over the Engine's read, or the Engine reads the match identifiers from a different record than the one ingestion wrote them to. Files: `EditorCurrentMatchService.cs`, `EditorMatchPanel.razor`. Depends on: nothing (card layout after WP3). Done when: the test passes for all three kinds.
- **WP5. ISBN search (Standard).** ISBN alone searches; dashes and spaces accepted. Files: `EditorMatchSearchService.cs`, `config/providers/`. Depends on: nothing. Done when: a test with only an ISBN returns the provider's record or the D11 message.
- **WP6. TV, music and Wikidata on the frame (Complex).** Files: `EditorTvMatchFlow.razor`, `EditorMusicMatchFlow.razor`, `EditorWikidataMatch.razor`, `EditorGroupMatchPanel.razor`. Depends on: WP3. Done when: each flow uses the same row, bar, list and card, and the check passes on each.
- **WP7. Details tab (Standard).** Rows on WP1; path line at the foot of Quick Facts with middle shortening, hover and copy; pills removed. Files: `EditorDetailsPanel.razor`, `EditorQuickFacts.razor`, `EditorQuickFacts.cs`. Depends on: WP1. Done when: Sort title and Rating share a top edge at 1024, 1280 and 1440.
- **WP8. Header (Standard).** Cover, icon fill across header and tab row, episode pictures in the dropdown. Cause of the cover fault to be found with a test first. Files: `EditorItemHeader.razor`, `EditorHeaderNav.razor`, the header read in the Engine. Depends on: nothing. Done when: a movie, a book and an episode show their cover, and an episode row with a stored picture shows it.
- **WP10. Series chooser and folder view (Complex).** D16, D17, D24: the Engine's staged series change gains the file move for Managed folders (check then apply through the existing organise job, the same path a changed match uses, with a preview of from and to for the footer and Review); Read-only folders stay Library-only; a test proves a hand-set series survives a rescan in both kinds of folder. Files: `EditorVideoSeriesPanel.razor`, `EditorFileBrowserFolders.razor`. Depends on: nothing. Done when: Cancel and Esc return with nothing staged; the bottom line and arrow are gone.
- **WP10a. Reorder in the header lists (Standard).** D23: one-line rows, no hint text in the menu, the shared Reorder bar with Save order and Cancel, Esc cancels. Files: `EditorHeaderNav.razor`, `EditorVideoOrderList.razor`, `VideoOrderEditor.cs`, localization (`EditorReorder_*`). Depends on: the shared Reorder bar from the Collections and View build being on main. Done when: with 30 videos the menu shows the Reorder row without a scrollbar of its own, Cancel and Esc restore the order, and Save order appears in Review as one row.
- **WP11. Playlist rule rows (Standard).** The rule rows use WP1. Files: `CollectionEditorShell.Rules.cs` and its markup. Depends on: WP1. Done when: the check passes on a rule row with a helper line.
- **WP12. Mockups, docs, guardrails, versions (Standard).** HTML mockups for the screens in section 1a committed under `engineering/design/mockups/editor-v6/`; public editing guide updated; version notes on v5; closure notes on the issues. Depends on: all.

- **WP13. Lift the desktop-only rule (Complex).** Retire `EditorDeviceRule` for the editor, its launchers, Delete and collection membership; keep it only where section 9 lists another owner. About 40 Dashboard files call it today (list in section 9), with five test suites. Depends on: WP14 (so nothing opens an editor that is unusable). Done when: Edit, Delete and Add to collection appear and work at phone width for a profile the Engine allows, and are still refused for one it does not.
- **WP14. Phone frame (Complex).** Full-screen sheet, one-line header with the navigation sheet, scrolling tab row, footer per D22, Review as a step, keyboard behaviour. Files: `EditorPopover.razor`, `EditorWindowChrome.razor`, `EditorItemHeader.razor`, `EditorHeaderNav.razor`, `ReviewChangesView.razor`. Depends on: WP2. Done when: at 360 and 390 wide nothing scrolls sideways and every control is at least 44 px.
- **WP15. Phone Details, Artwork, History, Bookmarks (Complex).** Files: `EditorDetailsPanel.razor`, `EditorFieldLock.razor`, `EditorQuickFacts.razor`, `EditorArtworkTool.razor`, `EditorHistoryTool.razor`, the Bookmarks tab parts, `ViewEditorPopover.razor` and its panels. Depends on: WP7, WP14. Done when: a field can be unlocked, changed, reviewed and saved by touch alone.
- **WP16. Phone Match (Complex).** Stacked search, list, card as a sheet, TV and music steps, Wikidata. Depends on: WP6, WP14. Done when: a book, an episode and a song can each be matched by touch alone at 360 wide.
- **WP17. Phone browser, several items, reorder (Complex).** Files: `EditorFileBrowserDialog.razor` and its parts, `BulkEditPanel.razor`, `BulkSelectionBar.razor`, `EditorVideoOrderList.razor`. Depends on: WP14. Done when: three files can be ticked and given a group, and two videos swapped, by touch alone.

Waves: (WP1, WP4) → (WP2, WP5) → (WP3, WP7) → (WP6, WP8) → (WP10, WP10a) → (WP14, WP11) → (WP15, WP16) → (WP17, WP13) → WP12. The desktop work is complete and can be checked after the fifth wave; the phone work follows on top of it.

## 7. Local checks

- Mock beside Live at 1024, 1280, 1440 and phone width for: Match unmatched, Match matched, Details tab, playlist rule row.
- A real book with an ISBN: search by ISBN only, and by title.
- A real TV episode and a real song through their flows.
- In a Managed Videos folder: move a loose video into a new series, see the amber line with the right paths, save, and find the file in the new folder on disk. In a Read-only folder: the same choice moves nothing. Rescan both and check the series is kept.
- On a real phone (not only a narrow desktop window): open Edit from a movie, change a locked field, change artwork from the phone's photos, match a book, review and save; delete an item; tick three supporting files and group them. Repeat one pass on a tablet in each orientation.
- Matched items of each kind open as matched.
- Covers in the header for a movie, a book, an album and an episode; episode pictures in the dropdown.

## 8. Risks and open questions

- **Provider marks** are trademarks. Each built-in provider's brand rules must be read before its mark ships; where they do not allow it, a letter badge is used.
- **The header changing height between tabs** is a visible jump. It is the price of giving Match room.
- **WP4 and WP8 causes are not yet proven.** Each starts with a test that shows the fault.
- **Phone editing roughly doubles the size of this spec.** The desktop packets are ordered first so the fixes from the local check are not held up by it.
- **Touch makes mistakes easier** (a stray tap on Delete or Apply). Nothing saves without Review, and Delete keeps its confirmation.
- **Mockups:** `engineering/design/mockups/editor-v6/editor-v6-mockups.html` (screens A to H).
- **Open questions:** none.

## 9. Gaps and drift

| Document | What changes |
|---|---|
| Universal editor v3.1 (Built) | D3 "Series and order changes never move files": series membership now moves the file in a Managed folder (D24 here). Order is unchanged. |
| Universal editor v5 (Built) | D35 and 3c "the tab's first row": the Media providers · Wikidata switch becomes tabs; icon-only Search becomes a labeled button; search on change becomes search on demand. 3c results: no verdict in the list; "Check match" renamed; reasons become the compare table. Section 4: the header is one line on Match. D32's folder view loses its bottom line and up arrow. |
| Look pass v3 | D15.5's worked example (Match row with a right-hand switch) is replaced by the row in D3; the shared row part of D12 is the form-row primitive WP12 there described. |
| CLAUDE.md "Never" 1 | One clause added: Match shows a one-line header with Media providers and Wikidata as tabs. **The "Editing is desktop-only" sentence is replaced:** editing works on desktop, tablet and phone; the Engine's permission check decides who may edit. |
| `src/MediaEngine.Web/CLAUDE.md` | Its desktop-only editing rule is replaced the same way. |
| Universal editor v3, v3.1, v4, v4.1, v5; Photos and documents editor; Details overlay and edit mode; Universe and Universe contents; Videos; Library pages; View section; Music player and queue | Each says editing is desktop-only (or hides an edit action on phones). Each gets a version note pointing here. |
| Where the rule is enforced in the Dashboard today | The rule itself and the device reading behind it; the editor host, the View editor host, the list, gallery and collection launchers, the editing-mode context, library move rules, collection membership and item deletion; the details page's actions and "..." menu, tile actions, the bulk bar and list entry, the song table; the Watch, Read, Listen (browse and playlists), Collections, Universe, Universe thing, Chronicle, View galleries, gallery detail, documents, viewer and trash pages; the editor's own bulk panel and file browser; the lyrics footer in the player. **Changed by this spec.** |
| Same rule, other owners | Settings: plugin settings and setup, the import page, the ingestion flow editor. Vault: unmatched list, identify panel, gallery tools. **Not changed here** (question 6). |
| Tests | The device-rule suite, the Chronicle editing guardrail, song-list delete, delete-from-details and Vault feedback suites change with the rule. |
| Public docs (`tuvima-docs`) | The editing guides, "Editing photos, documents and galleries", "Documents in View", "How Universes work", "View, your personal space" and "Why the player works this way" each say or imply desktop-only editing and are updated. |

## Plain English summary

Match is redrawn to Shy's picture: your file on one bar, candidates on the left, the chosen one on the right with a large cover and a table that shows what agrees. The two sources become tabs, so the search row is one tidy line with a real Search button. Rows across the editor are rebuilt on one shared part so text can no longer push controls out of line, and the same part fixes the playlist rule rows. The editor bugs from the check are fixed along the way: matched items saying "Not matched yet", the header cover, filled-in icons, the confusing series chooser and the stray folder line. Nothing about saving, locking or deleting changes. The whole editor also comes to phones and tablets as a full-screen sheet with the same tabs and the same rules, built after the desktop fixes so those are not held up.
