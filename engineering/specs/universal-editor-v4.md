# Spec: Universal editor v4 (the editor overhaul)

> **Status:** Locked (Shy, 2026-11-04) · **Version:** 1 · **Last updated:** 2026-11-04 · **Related docs:** [Universe contents](universe-contents.md) · [Universal editor v3](universal-editor-v3.md) · [Universal editor v3.1, video series and order](universal-editor-v3-1-video-series-and-order.md) · [Delete from library (archived)](archive/delete-from-library.md) · [Delete groups (archived)](archive/delete-groups.md) · [Universes](universe.md) · [Product overview](../product/overview.md) · Mockups: [`engineering/design/mockups/universal-editor-v4/editor-v4-mockups.html`](../design/mockups/universal-editor-v4/editor-v4-mockups.html) · Planning notes: review and questions, 2026-11-04 (project thread "Overhaul the editor")

## 0. Why a v4

Shy's verdict at the 3 Nov local check: the pop-over editor (v3, PR 636, plus v3.1 video series, PR 639) "needs an overhaul by itself". There is also a bug: pressing Edit on a second title shows the first title until the page is refreshed. Shy supplied seven reference screenshots (kept on SHY-DESKTOP, never committed; their pills, icons and colours are representative, not decisions) and these principles:

1. The family navigation is **hidden until it is needed** (shows, music, series, Universes); otherwise the editor gives quick information and an easy way to change it.
2. **Artwork management is robust**: it understands image sizes and shapes (portrait, landscape, logo) and is easy to see and move around in.
3. **Matching is a deeper, integrated experience** that still feels simple: picking the right episode should not mean reading 20 fields side by side.
4. **The same editor handles several items** (for example songs ticked in a list) and offers only the changes that make sense for all of them.
5. **Universes use the same editor**, always available, so characters, places and factions can get pictures and logos.
6. **Deleting is clear and quick** from the editor.

What v3 built and what changes: v3 already opens a pop-over at once over any page, with Details, Artwork, Match and History tools, a family tree on the left, staged changes and a Review before Save. v4 keeps the pop-over, the staged changes, Review and the saver, and replaces the layout and three of the tools (navigation, Artwork, Match), adds Contents, Files, Quick Facts and Delete, and moves Universes onto the same editor.

## 1. Goal

One editor, opened from Edit anywhere, that looks like the item it is editing (its picture and title at the top), lets you change anything in a couple of clicks, matches files to the right show, episode, album or book with a clear before-and-after, manages artwork you can actually see, edits several items at once sensibly, and deletes cleanly. It always opens on the item you clicked.

## 1a. Plain-English walkthrough

1. **Press "..." then Edit** on an episode of The Expanse. The pop-over opens straight away over the dimmed page. At the top, a **rail of dropdowns** shows where you are: *The Expanse ▾ › Season 2 ▾ › Static ▾*. Each dropdown has a small picture. Choose another episode or the season from them to switch; your unsaved changes on the first episode are kept.
2. Under the rail is the item's **header**: its picture, kind ("The Expanse · TV episode"), title, one line of facts, and links to its provider records (TheTVDB, Wikidata). A film has no rail; its header sits at the top.
3. **Tabs:** Details, Artwork, Match & Identity, Files, History, and **Contents** on a show, season, album or series. A Universe shows its own tabs (section 3a).
4. **Details** is a clean form. Every field has a **lock** (keep my value when the provider refreshes) and, once changed, a **revert** arrow. On the right, **Quick Facts** suit the item: for a season, episodes owned (13 of 13), total runtime, total size, and "Read-only library" if that applies.
5. **The footer** always says where changes go ("Changes save to Library · File write-back off") and how many are waiting, with **Discard** and **Save changes**. Save shows **Review changes** first (unchanged from v3).
6. **Contents** (show, season, album, series) lists the children with a picture, number, title, matched mark and size. Tick several and the editor becomes **"Editing 5 episodes"**: only the fields every ticked item can take are offered (section 3c). Ticked rows can also be matched together, reordered (where order is yours to set) or deleted.
7. **Match & Identity** shows what the item is matched to now, then lets you change it:
   - **One episode:** a short list of the show's episodes for the chosen season, each row showing still, number, title, air date and runtime, with a **confidence** bar. Your file sits beside the highlighted row as a **before and after**: "Your file: S02E03.mkv, 42 min → S02E03 Static, 42 min, aired 8 Feb 2017". One click picks it.
   - **Several files** (a season, a whole show, an album): step 1 choose the show or album; step 2 a table lines up **your files against the provider's episodes or tracks**, with confidence and a status (Ready, Needs choice). Only the deciding facts are shown. "Apply to: Selected files only / All files in this season."
   - Here you also see the item's **Universe** with an **Open Universe** link, and its place (Season 2, Episode 3) with Change placement.
8. **Artwork** (section 3b): a card per role (Poster, Background, Logo, plus per-type extras such as Season poster or Episode still), each saying how many versions you have and which is **Preferred**. A large preview with zoom, file facts (dimensions, size, type), and **Add artwork** to choose from the provider (filtered by language and size, with shape-correct thumbnails), upload or paste a link. A line under the preview says exactly what changes ("Only this episode's still changes").
9. **Files** lists the item's files: name, folder, size, quality, and whether the folder is read-only. Each file has Delete. An audiobook's files show here in order.
10. **Delete** (from the editor's own "..." on the current item, from ticked rows in Contents, or from a file in Files) asks **"Are you sure?"** with what will go and how much space is freed, and then happens at once. It does not wait for Save.
11. **From a list** (a song list, Needs Review, a library table), ticking rows and pressing Edit opens the same editor as "Editing 12 songs", without a rail.
12. **A Universe** opens the same editor, with the same layout as everything else: across the top, the Universe and its seven kinds as searchable dropdowns with counts (Characters, Places, Factions, Events, Objects, Species, and Concepts when Fandom Lore is on), each dropdown with "Add <kind>" at the top. Pick a character and the right side shows its Details, Pictures (main picture, logo, background), Match (Wikidata, or "Not on Wikidata, added by you") and History.

**What stays the same:** desktop-only editing; nothing except Delete is written before Save; Review before every save; provider facts read-only; the four kinds of change (details, match, parent, files) and their switches; a match never moves files unless its own switch is on; deep links never open the editor; the Engine decides permission; the Needs Review list and notifier from v3 D9 and D13; the v3.1 video series and order behaviour (it moves into Contents and Match).

**Scope boundaries:** no phone or TV editing; photos and documents keep their own editor (separate thread "Update the photos and documents editor"); new Universe kinds come from the thread "Expand what a Universe can hold" (the editor is built so new kinds slot in); the Universe icon on covers and backgrounds is decided in "Make Universes easy to find".

**Acceptance criteria** (each maps to work packets):
- A1. Edit on title A, close, Edit on title B: the editor shows B every time, without a refresh. (WP1)
- A2. From an episode, the rail reaches its show, another season and another episode; changes staged on each survive switching; a film shows no rail. (WP3)
- A3. Every type in section 3a opens with its own header, tabs and Quick Facts. (WP4, WP5)
- A4. An unmatched episode is matched to the right episode in at most three clicks, seeing a before and after with confidence. (WP7)
- A5. A season of 13 files is matched in one pass through the two-step table, with any doubtful row marked Needs choice before Save. (WP8)
- A6. Artwork shows every role with its versions and Preferred, a zoomable preview, file facts, and provider choices filtered by language and size. (WP6)
- A7. Ticking 12 songs offers album, album artist, genre, year and disc, but not title or track number; saving changes all 12. (WP9)
- A8. Delete of an episode, a season, ticked rows and a single file each asks once and happens at once; the editor moves to the parent when its own item was deleted. (WP10)
- A9. A Universe's character gets a portrait and a faction a logo through the same editor. (WP11)
- A10. Fable's review of the built work finds no must-fix items left open. (WP13)

## 2. Scope

**In scope**
- The wrong-title bug.
- New layout: rail of dropdowns, header, tabs, Quick Facts, footer.
- Details, Contents, Artwork, Match & Identity, Files, History, Delete for every type in section 3a.
- Multi-item editing for any group of the same kind.
- Per-type rules (section 3a), using folder and file names as hints.
- Universes and their characters, places and organisations in the same editor, built to accept new kinds.
- "Open Universe" from Match & Identity.
- Retiring the v3 layout pieces this replaces (left tree, old Artwork tool, old Match panel).
- Docs (Library repo and tuvima-docs), including the "why" of each behaviour.

**Out of scope**
- Phones, tablets and TV (desktop only stays).
- Photos and documents (separate thread).
- New Universe kinds, Timeline (separate thread).
- The Universe icon on covers and backgrounds (separate thread).
- New metadata providers; TV stays TheTVDB only.
- Why the Engine fails to match some items in the first place (bugs go to their own threads); this spec guarantees the editor can fix them by hand.

## 3. Decisions made

Shy's answers, 2026-11-04, unless marked as a recommendation.

- **D1. Pop-over only (Q1).** The item page itself does not gain editor tabs. Reason: one place to edit; the page stays for enjoying.
- **D2. A rail of dropdowns, shown only when there is a family (Q2).** Show › Season › Episode; Artist › Album › Track; Series › Book or Issue; Film series › Film; Universe › kind › entity. Each dropdown lists its siblings with a small picture and a matched mark, and is searchable when long (paged, so a 500-episode show stays quick). A film, standalone book or person has no rail. Below 1280 px wide the rail keeps only the last two levels with a "…" for the rest. Reason: the form keeps the full width; the full list with ticks appears in Contents and Match, where it is needed.
- **D3. Tabs (Q3).** Details, Artwork, Match & Identity, Files, History for every item; Contents added for show, season, album, series and film series. No separate Organization tab: where files live and what they are called sits in Files. Universes have their own set (section 3a). Reason: fewer tabs; Contents is the one place for seeing, ticking, ordering and deleting children, which the rail cannot do.
- **D4. Quick Facts on every type (Q4).** A side panel with three to five facts that suit the item (section 3a), plus "Read-only library" or "File write-back off" when true. Facts are read-only.
- **D5. Matching shows only the deciding facts (Q5).** For a single item: a short candidate list with a confidence bar and a before-and-after line (your file's facts → the candidate's facts) on the highlighted row. For several files: two steps (choose the show or album, then a table lining up files with candidates, confidence and status). The facts shown per type are in section 3a; nothing else is compared on screen. Confidence comes from the Engine and is explained by a small (i) ("Episode number and runtime match; title differs").
- **D6. Artwork, as in the mockup (Q6; Shy reviewed it 2026-11-04).** Role cards with version count and Preferred; large zoomable preview; file facts; Replace, Delete; clicking a version makes it Preferred (D19); a versions strip; Add artwork opening Provider, Upload or Link with language and size filters. Thumbnails keep each role's real shape (portrait poster, landscape background, wide logo). The line under the preview says which items the change reaches.
- **D7. Several items, driven by rules (Q7).** Any group of the same kind can be edited together, from a list or from Contents. A field is offered only when every ticked item can take it and setting one value for all makes sense (section 3c). Differing values show "Mixed, leave unchanged". Match becomes "Match these N" with the two-step table.
- **D8. TV provider stays TheTVDB only (Q8).**
- **D9. Each media type has its own rules (Q9),** and folder and file names are hints: for example a folder named after a show and a season sets the starting search; an audiobook's files in one folder are one audiobook; "Disc 2" in a folder name sets the disc number. Hints pre-fill; they never save on their own.
- **D10. Universe editing is always available (Q10, Shy's correction), and follows the Locked Universe contents spec, section 4.4.** The same pop-over and layout as every other item (Shy, 2026-11-04, choice A, overriding Universe contents 4.4's left tree): the Universe and its seven kinds as searchable dropdowns across the top, "Add <kind>" inside each, and tools Details (name, kind, description, aliases, belongs to), Pictures (main picture, logo, background; no gallery, Universe contents P8), Match (Wikidata or added by you, with the usual before and after) and History. Own edits always win over provider facts. The Timeline is a read-only view and is not edited here. The editor reads kinds, fields and picture slots from the Engine, so later kinds need no editor change.
- **D11. Photos and documents are out (Q11).** They keep their own editor, updated in a separate thread.
- **D12. Desktop only (Q13).**
- **D13. Delete is instant after one confirmation (Q14).** From the editor's "..." (the current item, including a whole show, season, album or series), from ticked rows in Contents, and from a single file in Files (Q14b, taken as yes from Shy's "delete a file/episode/etc"). One "Are you sure?" box names what goes, how many files, the space freed, any hand-picked collection affected, and says plainly that removed files cannot be recovered (or that they go to the Recycle bin when that media type has it switched on). Files in read-only folders are never deleted; the item just leaves the Library and is excluded from scans, as today. It does not wait for Save. Unsaved changes on a deleted item are dropped; changes on other items stay staged. When the item you are on is deleted, the editor moves to its parent (or closes if there is none). Reuses the existing delete preview and confirm actions; single-file delete is new.
- **D14. The wrong-title bug is fixed first.** Opening the editor always loads the item it was opened for, whatever was loaded before.
- **D15. Fable reviews the build (Shy, 2026-11-03).** Planning is on Opus; the implementation is reviewed by Fable before the final merge (WP13). That review is in addition to the usual reviewer after each wave.
- **D16. Recommendation: the editor is about 1280 × 860 px at 1440 wide,** keeps its size while switching tabs, and scrolls only inside the tab content. Header, rail, tabs and footer never scroll away.
- **D18. More ways to search in Match (Shy, 2026-11-04).** Besides the pre-filled title search: search by provider ID or pasted provider link (TheTVDB, IMDb, TMDB, ISBN, MusicBrainz, as each type allows), year, a different title or show ("Search another title"), episode title words within the chosen show, episode by number including absolute numbering (anime) and air date (daily shows), and type the season and episode numbers by hand when the provider has nothing. Each type shows only the search options that apply to it.
- **D19. Preferred artwork is one click (Shy, 2026-11-04).** Clicking a version in the strip makes it Preferred and moves it to the front; the rest stay in the order they were added. There is no "Set preferred" button. Like every change except Delete, it waits for Save.
- **D20. "What is this file?" for files that aren't the main thing (Shy, 2026-11-04).** Any file the Engine could not place, or any file you open, can be told what it is from Match & Identity: **an episode (or track, or book)** → normal Match; **an Extra** → pick its kind (Trailer, Featurette, Behind the scenes, Deleted scene, Interview, Document, Pictures, Other clip) and where it belongs (the whole show, a season, or one episode; for a film, the film; for an album, the album or a track); **belongs to another title** → search any title in the Library and attach it there; **ignore** → hidden from the Library and future scans, file left on disk, listed in Settings with Add back. The kind is guessed from the file name ("Season 1 trailer.mkv" → Trailer, Season 1) and only pre-fills. A folder's other unsorted files are listed together so they can be sorted in one pass. Extras still never count as episodes (Extras D3). Use cases this covers:
  - "Season 1 trailer.mkv" in a show folder → Trailer, Season 1.
  - Workbook PDFs in a course like Stephen Curry's MasterClass → Document, whole show or one lesson.
  - Featurettes, deleted scenes, interviews on a film or show → Extra of that kind, on the film, season or episode.
  - A PDF booklet or music video in an album folder → Document or Other clip on the album or a track.
  - "sample.mkv", a stray .txt or .url → Ignore.
  - A film or another show's episode dropped in the wrong folder → Belongs to another title.
  - A real episode with an odd name → Episode → Match.
- **D17. Recommendation: Open Universe** sits in Match & Identity under "Universe", showing the Universe's name and picture. It closes the editor (asking about unsaved changes) and opens the Universe page.

## 3a. How each kind of item behaves

| Item | Rail | Tabs (besides Details, Artwork, Files, History) | Quick Facts | Match: deciding facts shown |
|---|---|---|---|---|
| Film | none | Match & Identity | runtime, year, size, quality | title, year, runtime, poster |
| Film series | Series › Film | Contents (order), Match & Identity | films owned, total runtime | (series is grouping; films match one by one) |
| TV show | Show | Contents (seasons), Match & Identity | seasons, episodes owned of total, size | show title, year, network, poster |
| TV season | Show › Season | Contents (episodes), Match & Identity | episodes owned of total, runtime, size | (matches episodes through the table) |
| TV episode | Show › Season › Episode | Match & Identity | air date, runtime, size, quality | S/E number, title, air date, runtime, still |
| Book (ebook) | Series › Book (if in a series) | Match & Identity | pages, edition, ISBN, size | title, author, year, cover, ISBN |
| Audiobook | Series › Book (if in a series) | Match & Identity | narrator, length, files, size | title, author, narrator, length, cover |
| Comic issue | Series › Issue | Match & Identity | issue number, pages, size | series, issue number, cover date, cover |
| Comic or book series | Series | Contents, Match & Identity | issues or books owned, size | (members match through the table) |
| Album | Artist › Album | Contents (tracks), Match & Identity | tracks owned of total, length, size | album, artist, year, track count, cover |
| Song | Artist › Album › Song | Match & Identity | length, track and disc, quality | track number, title, length, album |
| Personal video (v3.1) | Video series › Video | Contents for a video series (order) | length, size | no provider match |
| Person | none | Match & Identity | roles, works in library | name, born, known for, photo |
| Curated collection | none | Contents | items, owner | none |
| Universe and its things | Universe, then one dropdown per kind (seven kinds) | Details, Pictures, Match, History (no Files) | works in library, counts per kind | name, description, Wikidata picture, before and after |

Artwork roles per type are read from the Engine, as today: film (Poster, Background, Logo), show (Poster, Background, Logo), season (Season poster, Season thumbnail), episode (Episode still), book, audiobook, comic and album (Cover, Background), person (Photo); every Universe thing has main picture, logo (factions, places, objects, species) and background, as the Universe contents spec sets.

## 3b. Artwork in detail

- **Role cards** across the top: picture, role name, "Preferred" or "No artwork", number of versions.
- **Preview**: the chosen version large, at its real shape; zoom in, zoom out, full screen.
- **Actions** beside it: Add artwork (Provider, Upload, Link), Replace, Delete.
- **File facts**: file name, dimensions, file size, type, source (Saved, the provider, Uploaded).
- **Versions strip** below, with Preferred marked, and "Add version". Clicking a version makes it Preferred and moves it to the front; the others keep the order they were added in (D19).
- **Provider choices** open in place of the preview: language filter (the app's language first), size filter (All, Large, Medium), thumbnails at the role's shape with dimensions under each. Small pictures (below the role's useful size) show a quiet "Small" mark.
- **What it reaches**: "Only this episode's still changes" or "This show's poster; seasons without their own poster use it too".
- Episode stills keep "Fetch all" for a season (v3 D8).
- Like every change except Delete, nothing is written before Save.

## 3c. Editing several items: the rules

- Fields that identify one item are never offered for several: title, track or episode number, ISBN, file name.
- Fields that describe a group are offered when every item belongs to the same kind: album, album artist, genre, year, disc number (songs); season, show (episodes, through Match); series, author (books); tags (everything); Explicit (songs and videos).
- Fields that would make items collide are blocked with the reason ("These 3 songs would all be track 4").
- Read-only items stay in the selection but are greyed with the reason; the change reaches the others.
- Artwork for several: only roles they share (for example "Set one cover for these 12 songs" sets the album's cover, said in words).
- The header says "Editing 12 songs" with small pictures; Review lists each item.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal | Pop-over over the dimmed page; rail (when there is a family), header, tabs, content, Quick Facts, footer. |
| Opening | The frame, header and rail appear at once with skeleton fields; fields fill in when loaded. Always the clicked item. |
| Item not matched | Opens on Match & Identity with the search pre-filled from the folder and file names. |
| Nothing found | "Nothing matches that." with fields to search by hand (show, season, episode numbers). |
| Doubtful matches in the table | Rows marked Needs choice with a dropdown of candidates; Save counts them and Review lists them. |
| Engine unreachable | A plain error line and Try again; staged changes kept. |
| Provider down | Match says which provider failed; Details, Artwork from Saved, Files and Contents still work. |
| Read-only library | Fields disabled with the reason in the footer; Delete removes from the Library only. |
| Delete in progress | The confirm button shows progress; on success the row disappears and a short line says what was freed; on failure, the reason, and nothing half-deleted is shown as gone. |
| Several items with one locked | That row is greyed with the reason; the change reaches the others. |
| Very long families | Rail dropdowns and Contents page in as you scroll. |
| Narrow window (below 1280) | Rail shortens (D2); Quick Facts move under the form. |
| Leaving with changes | "Unsaved changes" (unchanged). |

## 5. Data / model changes

- **Extras gain a kind and a place** (D20): Trailer, Featurette, Behind the scenes, Deleted scene, Interview, Document, Pictures, Other clip, and an optional season, episode or track they belong to. Pre-release: existing Extras can be re-read on reset.
- **Ignored files** reuse the existing excluded-files list (delete-from-library D15), with the reason "Ignored by you".
- **Engine: search by provider ID or link, episode title, absolute number and air date** for Match (D18); some already exist per provider, to be confirmed in WP2.

- **No change to stored data** for layout, Details, Artwork or multi-edit; staged changes stay in the Dashboard until Save.
- **Engine: match confidence with reasons** for single-item candidates and for file-to-episode or file-to-track lining up (existing scoring is reused; the reason text is new). To be confirmed in WP2.
- **Engine: delete one file** of an item (new; the title and group deletes exist). Same safety gate, preview and confirm as today.
- **Universe kinds and picture slots** come from the Locked Universe contents spec, which owns that data work; this spec only uses it.
- **Engine: Quick Facts** per item (most facts exist on the detail read; a small read may be added).
- New wire types go in Contracts with fixtures and tests. Pre-release data: no migration.

## 6. Work packets

Waves follow "Depends on"; at most two packets at once. After every wave the usual reviewer runs. WP13 is Fable's review of the whole build.

### WP1 — Fix "Edit opens the wrong title"
- **What it does:** Opening the editor always loads the item it was opened for. Today the field session is shared by the whole browser session and is reused if anything is already loaded. Add a test: open A, close, open B, fields show B.
- **Files likely touched:** `src/MediaEngine.Web/Services/Editing/EditingModeContext.cs` (`EnterAsync`), `EditorHost.cs`, `ItemFieldEditSession.cs`, `tests/MediaEngine.Web.Tests/`
- **Depends on:** none · **Size:** Standard
- **Done when:** the new test passes, and a second Edit on another title shows that title without a refresh. Can merge on its own before the rest.

### WP2 — Engine reads and actions
- **What it does:** Confidence with reasons for candidates and for lining up files; single-file delete (preview and confirm); Universe kinds description; Quick Facts read if needed. Contracts types, fixtures, tests.
- **Files likely touched:** `src/MediaEngine.Api/` (metadata, delete, universe endpoints), `src/MediaEngine.Contracts/`, `tests/MediaEngine.Contracts.Tests`, `tools/Update-ContractFixtures.ps1` output
- **Depends on:** none · **Size:** Complex
- **Done when:** contract tests pass; a test deletes one of two files of an episode and the episode stays with one file.

### WP3 — New frame: rail, header, tabs, footer
- **What it does:** Replaces the left tree with the rail of dropdowns (paged, searchable), adds the header with provider links, the tab bar (per section 3a), the fixed footer, size and scrolling (D16).
- **Files likely touched:** `Components/Details/EditorPopover.razor`, `EditorFamilyTree.razor` (removed), `Services/Editing/EditorFamilyState.cs`, `EditTrailCatalog.cs`, new rail component
- **Depends on:** WP1 · **Size:** Complex
- **Done when:** A2 holds in a component test and on screen.

### WP4 — Details and Quick Facts for every type
- **What it does:** The form with lock and revert on every field, Quick Facts per section 3a.
- **Files likely touched:** `EditorDetailsPanel.razor`, `MediaEditorDetailsPresenter.cs`, new Quick Facts component
- **Depends on:** WP2, WP3 · **Size:** Standard
- **Done when:** each type in 3a shows its facts in a test.

### WP5 — Contents and Files tabs
- **What it does:** Contents lists children with picture, number, title, matched mark, size; ticking switches to several-items editing; ordering where it is the person's (v3.1). Files lists files with folder, size, quality, read-only mark, and an audiobook's order.
- **Files likely touched:** new `EditorContentsTab`, `EditorFilesTab`; `EditorVideoSeriesPanel.razor` (folded in), `VideoOrderEditor.cs`
- **Depends on:** WP3 · **Size:** Complex
- **Done when:** ticking 5 episodes in Contents shows "Editing 5 episodes".

### WP6 — Artwork tool
- **What it does:** Section 3b in full; replaces the old Artwork tool.
- **Files likely touched:** `EditorArtworkTool.razor`, `ArtworkPickerSession.cs`, `ArtworkCandidateFilter.cs`
- **Depends on:** WP3 · **Size:** Complex
- **Done when:** A6 holds on screen for a film, a season and an album.

### WP7 — Match & Identity for one item
- **What it does:** Current identity, candidate list with confidence and before-and-after (D5), Universe with Open Universe (D17), placement, search pre-filled from folder and file names (D9).
- **Files likely touched:** `EditorMatchPanel.razor`, `EditorTvMatchBrowser.razor`, `EditorMatchRules.cs`, `MediaEditorMatchRule.cs`
- **Depends on:** WP2, WP3 · **Size:** Complex
- **Done when:** A4 holds.

### WP7b — "What is this file?" and Extras sorting
- **What it does:** D20: the four choices, Extra kinds and placement, belongs to another title, ignore; name-based guesses; the folder's unsorted files listed together. Extras section on pages shows the new kinds and places (Extras spec's section, extended).
- **Files likely touched:** Engine Extras service and its Contracts types, `EditorExtrasMovePanel.razor` (replaced), Match & Identity tab, show/film/album Extras section
- **Depends on:** WP2, WP7 · **Size:** Complex
- **Done when:** "Season 1 trailer.mkv" becomes a Season 1 Trailer in two clicks and shows under Extras; a MasterClass workbook PDF is a Document; "sample.mkv" can be ignored and added back.

### WP8 — Match for several files (the two-step table)
- **What it does:** Choose show or album, then line up files with confidence and status; Needs choice rows; Apply to selected or all. TV first, then albums, comic and book series.
- **Files likely touched:** new match table component, `EditorTvMatchCandidates.cs`, `EditSessionSaver.cs`
- **Depends on:** WP7 · **Size:** Complex
- **Done when:** A5 holds, and an album of 12 songs is lined up the same way.

### WP9 — Several items by rules
- **What it does:** Section 3c rules per kind, from a list or from Contents.
- **Files likely touched:** `BulkEditFields.cs`, `BulkEditRules.cs`, `BulkEditStager.cs`, `ListEditLauncher.cs`
- **Depends on:** WP4, WP5 · **Size:** Standard
- **Done when:** A7 holds in a test.

### WP10 — Delete from the editor
- **What it does:** D13 from the editor's "...", Contents and Files.
- **Files likely touched:** editor header menu, `EditorContentsTab`, `EditorFilesTab`, the existing delete dialog
- **Depends on:** WP2, WP5 · **Size:** Standard
- **Done when:** A8 holds.

### WP11 — Universes in the same editor
- **What it does:** Universe contents spec section 4.4 in this editor, with D10's layout: kind dropdowns across the top with Add in each, Details, Pictures, Match, History, Review before Save. Universe and Chronicle pages open it. Uses that spec's Engine reads and picture slots (it owns the data work).
- **Files likely touched:** `UniversePage.razor`, `ChronicleExplorer.razor`, editor rail
- **Depends on:** WP3, WP6, and the Universe contents spec's data packets · **Size:** Complex
- **Done when:** A9 holds, and a hand-added faction gets a logo.

### WP12 — Retire the replaced pieces, docs
- **What it does:** Deletes the left tree, the old Artwork tool, the old Match panel and anything only they used; guardrail that they stay gone; CLAUDE.md Never 1 wording, `docs/product/presentation-rules.md`, tuvima-docs editor pages (including why each behaviour works the way it does).
- **Depends on:** WP3 to WP11 · **Size:** Standard
- **Done when:** guardrails pass; a text search finds no live use.

### WP13 — Fable review and visual proof
- **What it does:** Screenshots at 1440 and 1280 of every row of 3a and the delete confirm, on one page for Shy. Then an independent review by **Fable** of the full diff against this spec; every must-fix is fixed and re-checked before the final merge.
- **Depends on:** all · **Size:** Standard
- **Done when:** Fable's review reports no open must-fix, and the screenshot page is shared.

## 7. Local checks

- **L1.** Edit one film, close, Edit another: the second film shows. Failure: the first film's fields.
- **L2.** On real media (`Z:\Development\testing\media\real_media`), match an unmatched episode and a season of files through the table; see before and after and confidence. Failure: wrong episode saved, or a doubtful row saved without a choice.
- **L3.** Artwork on a film, a season and an album: pictures are large enough to judge and at their real shapes. Failure: anything cropped or too small to see.
- **L4.** Tick 12 songs in the Songs list and change the album artist. Failure: title or track number offered, or a song not changed.
- **L5.** Delete one episode file, one episode, and a whole season in a managed folder; try the same in a read-only folder. Failure: a second confirmation, a file deleted from a read-only folder, or space not freed.
- **L6.** Give a Universe character a portrait and a faction a logo.

## 8. Risks and open questions

- **Risks:** (1) This is large; WP1 ships first so the bug is not held up. (2) Instant delete inside an editor that otherwise waits for Save could surprise someone; the confirm box says plainly that it happens now. (3) Confidence reasons depend on Engine scoring quality; weak scores show as Needs choice rather than guessed. (4) Universe editing depends on the Universe contents build landing its data first; WP11 waits for it.
- **Open questions:** none. Q1 (Universe layout) answered by Shy 2026-11-04: A, dropdowns across the top (D10).

## 9. Gaps and drift

- v3 D2 (left tree) and D3 (tree is the navigation) are replaced by D2 here.
- v3 "Delete stays in '...'" (scope) is replaced by D13.
- delete-from-library D14 (no single-file delete) is replaced by D13.
- Universe contents 4.4 (left tree) is replaced by D10 (Shy, choice A); that thread updates its spec.
- Universe spec D1 (Universe reached only from the Library) is relaxed by D17 (Open Universe from the editor), as Shy asked.

## Plain English summary

The editor gets a clean rebuild. It always opens on the item you clicked, which fixes the wrong-title bug first. Across the top, a row of dropdowns lets you move between a show, its seasons and episodes (or an album and its songs) without leaving. Each item gets a clear form, a few quick facts, and an artwork section that shows pictures large, at their real shape, with all their versions. Matching shows only what matters, such as episode number, title, air date and runtime, with a confidence bar and a before-and-after. A whole season can be lined up in one table. Ticking several items edits them together, offering only the changes that make sense. Delete asks once and happens straight away. Universes use the same editor, so characters and factions can finally get pictures and logos. Fable reviews the finished build before it merges.
