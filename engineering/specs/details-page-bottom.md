# Spec: Details page bottom (v4.2 revision)

> **Status:** Locked · **Version:** 1 (of this revision) · **Last updated:** 2026-11-14 (locked) · **Amends:** [Editor and details page revision v4.1](universal-editor-v4-1-details-page.md) (Locked; its body is not rewritten) · **Issues:** #981, #982, #984, #985, #998 (#997, video frames, stays with the local bug review) · **Mockups:** `design/mockups/details-page-bottom.html` · **Related docs:** [Look pass v3](look-pass-v3.md), [Universal editor v6](archive/universal-editor-v6-match-and-aligned-rows.md)

## 1. Goal

The bottom of a details page sticks to the title: who is in it, and what your copy of it is like. It reads at a glance (chips), and a person who wants the technical detail opens one panel. Plumbing (History, file paths, many source links) leaves the page.

## 2. Scope

**In scope**
- Overview tab (two columns), Credits tab, a full-width "Your copy" row with an expandable "File details" panel, and a quiet Sources row. Movie, TV show, album; the same block for books, comics and other kinds with their own facts.
- Remove History from the page (it stays in the editor's History tab).
- Remove tick marks and tick-to-select from Series and episode rows (#984).
- Season dropdown on the same row as the Episodes title, no divider (#985).
- **Extras row** placement and look on the page, and the **Show missing** check mark on Series rows.
- Window width on wide screens (#981), as a recommendation to confirm by measuring.

**Out of scope**
- Hero, Watch button, extras, the editor itself (except where multi-select moves into it).

## 3. Decisions made

Shy decided these in the live review (2026-11-13/14); the mock follows them.
- **D1.** Tabs are **Overview** and **Credits** (v4.1 D10 said Overview and Cast; Credits replaces Cast).
- **D2.** Overview is two columns. Left: description with Read more, then Released and Language (and one or two more kind-specific facts), then the description source line. Right: Cast and crew with the director or lead credit first (a slightly larger row), then a two-column grid of people, and "View full credits".
- **D3.** Credits use one tile everywhere (Overview and Credits tab): rounded square photo, name, role or character. One size per breakpoint; only the number of columns changes (v4.1 D16 kept).
- **D4.** "About this" becomes **Your copy**: full width, an icon, the label, chips for key facts (resolution, video codec, audio, container; HDR, Dolby Vision or Atmos when present), and a **File details** control at the right.
- **D5.** File details opens one panel, three groups with icons: Video, Audio and subtitles, File. The filename sits at the bottom of the panel with a second expander, **Show file location**. The path is never shown until asked. (Your reference picture has Filename inside the panel; this follows it.)
- **D6.** History leaves the page; it is in the editor's History tab.
- **D7.** The details-list rows (Type, Library, Series) are gone from the page bottom.
- **D8.** **Sources row stays** (decision 2026-11-13, option A): only links for this title. One Wikidata link (never the Series' link, never both the Q-number and a named link), plus Wikipedia or TMDB or another provider's title link, one each. No Series, Universe or person links.
- **D9.** (#984, decision A) No tick marks or tick boxes on episode or Series rows. The thin progress line under the picture is the only watched marker. Choosing several episodes to edit moves into the editor's own episode list (amends the "ticked rows open Edit N items" line in CLAUDE.md "Never" 1 and in v4/v5).
- **D10.** (#985) Season dropdown sits on the Episodes title row (Shy 2026-11-13: the row heading (Episodes, Videos, More in this world, Series) stays, on its own line, with the dropdown or dropdowns directly under it, both pinned left; the page's existing owned-items progress bar and Show missing sit at the right, level with the dropdowns). Cards start straight underneath and share the title's left edge. Applies to book and album Series rows with a dropdown. Phone: the dropdown may drop under the title.
- **D11.** **Extras** (v4.1 D14 kept; this shows how it looks): one row between the top of the page and the tabs, built from the groups the person named in the editor. Each group has a heading with its count (Trailers and clips · 3), 16:9 cards with a small kind label and a title, and "See all N" at the right end of the Extras heading. Documents get a paper icon and open read-only in the Documents reader. No extras means no row. A show's Extras sit under the episodes; per-episode extras live on the episode page.
- **D12.** **Show missing** is one plain check mark on the Series row heading line, beside the dropdown. The "Use media default" button is removed. The choice is remembered per title and per profile (the Engine already stores a per-profile, per-Series choice). **Decided 2026-11-14:** a Series always starts unticked and the Library's general missing-items setting is **removed entirely** (its Settings control, the `library-preferences` default and schema entry, the policy reads in the Series panel and Universe page, and the "inherit" state), so the tick is the only thing that decides. Missing titles show dimmed with "Not in your library".
- **D13.** (new bug, Shy 2026-11-13) On a folder of videos, the dropdown lists the folders as the editor does, in their real order with a video count each; a sub-folder reads "Parent / Child"; the current folder is ticked. Today every row repeats the collection name: confirmed live in issue #998 (videos carry no section name, so both selectors show the collection title). Mock section J.
- **D14.** **Extras belong where their folder says** (editor v5 rules, unchanged): an episode's own folder or matching file name belongs to that episode (shown as a chip under it and in its own Extras row); a season or section folder to that season or section; otherwise to the title. **See all N** opens the supporting-files list read-only (search, Where and type filters, group, role, Belongs to, type); ticks and bulk actions stay in the editor. Mock section K.
- **D15.** Picture rule for video frames is tracked separately in #997 (extraction works for TV episodes; the 109 Videos items have no stored picture records, so the details hero, rows and editor show nothing). Not part of this spec's packets.
- **D16.** (Shy, 2026-11-13) A Series row can carry **two dropdowns** on its title row: which Series, then which section of it (Main sequence, Companion stories, Collected editions). Series first, section second, directly under the row title, pinned left; the second appears only when the chosen Series has more than one section. Show missing sits at the right end. On a phone they stack, full width. Mock section L. (The code already supports a Series chooser plus a group chooser on the same panel; this only fixes where they sit.)
- **D17.** (#981, Shy 2026-11-14, option A) On wide screens the details window width follows its height up to a cap of 1920 px, with the page still visible around it; up to about 1500 px of screen width nothing changes. The landscape background keeps filling the hero (it scales to fit the new window shape and crops a little top and bottom if needed, never the sides). Build must record the measured window size at 1440, 1920 and 2560 wide.

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Extras | One row per named group; See all opens the supporting-files list. Phone and tablet: groups stack and cards scroll sideways. TV: hidden. |
| Show missing | Check mark at the end of the Series row title line, same height as the dropdown. On phones it sits on the line under the title. |
| Movie, normal | Overview two columns; Your copy row with chips; File details closed. Sources row. |
| File details open | Panel in three groups; Filename row at the bottom; Show file location reveals the path in a small mono line. |
| TV show | Show-level facts: chips for the common quality ("62 episodes", 4K, HDR10, Atmos); panel lists exceptions ("12 episodes in 1080p") and totals. An episode's own page shows that episode's file facts. |
| Album | Chips: format, bit depth and sample rate, track count, channels. Groups: Audio, Tracks, Files. |
| Book (follow the same pattern) | Chips: format, pages, language. Groups: Book, Reading, File. |
| Several files for one title (two editions) | One Your copy row per edition, labelled by edition; each opens its own panel. |
| Missing facts (not yet scanned) | Chips that are unknown are left out; if none are known the row shows "Reading file facts" with no chips. File details is hidden until facts exist. |
| Credits empty | Right column shows "No credits yet"; the left column spans the width on Overview. |
| Enrichment running | Credits and Sources fill in as they arrive; Your copy appears as soon as the scan finishes. |
| Not allowed to see file facts (Kid profile, Vault rules) | Your copy is hidden entirely for those profiles. |
| Desktop 1280 to 1920 | Two-column Overview, three-column panel. |
| 1024 | Same, tighter tiles. |
| Tablet (about 720) | Overview one column; panel groups stack. |
| Phone | One column; cast list shows four people then View full credits; the whole Your copy row is the tap target; groups stack. Edit shows only if the person can edit. |
| TV (10-foot) | Larger type; File details is a focus-ring button; no Filename or file location. |

## 5. Data / model changes

None to storage. The page reads facts the Engine already has (video, audio, subtitle streams, container, bitrate, size, duration, added date). Need to confirm in build: HDR type, frame rate and sample rate are exposed to the Dashboard; if not, they are added to the details wire type (Contracts tests and fixtures updated). Show-level and album-level summaries are computed by the Engine (most common quality, exceptions, totals), not by the Dashboard.

## 6. Work packets (outline, sizes to confirm at lock)

- **WP1 Your copy and File details [Standard].** Replace About this; chips, panel, two expanders; remove History, details list, file path from the page. Depends on: none.
- **WP2 Show, album and book summaries [Standard].** Engine summary for groups of files; wire fields. Depends on: none.
- **WP3 Credits tab and tile [Standard].** Rename tab, one tile, director-first. Depends on: none.
- **WP4 Sources row [Mechanical].** One link per provider for this title only. Depends on: none.
- **WP5 Tick marks and select [Standard].** Remove ticks and tick boxes; move multi-select into the editor's episode list; update CLAUDE.md and guardrails. Depends on: editor owners agree (see Q3).
- **WP6 Season row [Standard].** Dropdown on title row, no divider, left edges aligned, phone fallback. Depends on: none.
- **WP7 Window shape [Standard].** Apply D17; measure at 1440, 1920, 2560 and set the width rule. Depends on: none.
- **WP5b Show missing [Standard].** Remove the Library's general missing-items setting end to end (Settings control, config default and schema, policy reads, validator) and the reset button and its tooltip text; tidy the spacing and overlap in `SequencePlacementPanel.razor` and its styles, keep saving per profile and Series. Depends on: none.
- **WP5c Extras row look [Standard].** Match `ExtrasSection.razor` to the mock (group headings with counts, kind labels, See all). Depends on: none.
- **WP6b Video folder dropdown [Standard] (tracks #998).** Give video-folder groups their folder names and counts in the Engine's group build and show them in the dropdown. Depends on: none.
- **WP8 Docs [Mechanical].** Public docs and spec amendments. Depends on: all.

## 7. Local checks

- **L1.** A movie: chips match the file; File details opens with correct Video, Audio and File values; file location hidden until asked.
- **L2.** A show with mixed quality episodes: the summary and exceptions are right.
- **L3.** An album and a book show their own groups.
- **L4.** No tick marks anywhere on a show page; progress line still shows.
- **L5.** Season dropdown on the Episodes row at 1024, 1280, 1440 and phone.
- **L6.** Window at 1920 and 2560 wide shows the landscape background without sides cut off.
- **L8.** Tick Show missing on one show, reload and sign in as another profile: the first profile keeps it, the other does not; no overlap or stray button.
- **L9.** A title with Extras shows groups, counts and See all; a title without shows no Extras row.
- **L10.** A course folder with sub-folders: the dropdown shows each folder name and count, matching the editor.
- **L7.** A title with no Wikipedia or TMDB link shows only the Wikidata link; no Series link.

## 8. Risks and open questions

- **Risk:** show-level summaries could mislead when the library is mixed; the "exceptions" line mitigates that.
- **Q1 (Subtitles).** Settled by Shy 2026-11-13: subtitles sit under Audio (D5 as mocked).
- **Q2 (Episodes row).** Settled by Shy 2026-11-13: dropdowns on the left, straight after the title, always.
- **Q3 (Multi-select).** Settled by Shy 2026-11-13: yes, multi-select only in the editor's own list (D9). Original question: The tick boxes also gave the way into "Edit N items". Decision A (2026-11-14) moves it into the editor's own list. Confirm we also keep a "Select" entry for tablets and phones inside the editor only.
- **Q4 (Window shape).** Settled by Shy 2026-11-14: option A. The hero picture scales to fill the hero and crops only a little top and bottom, never the sides, because the window now keeps a roughly 16:9-or-wider hero (D17). Original: Recommendation: width follows height up to 1920 with the page visible around it. The issue's measured 1440 × 1104 on 2560 × 1440 does not match 90% height (1296); measure on the live page before locking.
- **Q6 (Show missing first view).** Settled by Shy 2026-11-14: always starts unticked; the general setting is removed (D12). Original: Recommendation: the first time a profile sees a Series it follows the library's general setting, then remembers the tick. Alternative: always start unticked.
- **Q5 (Credits for a Series or Universe).** Not covered by this mock; same tile, different lead credit.

## 9. Gaps and drift

- v4.1 D10 ("Overview and Cast"), v4.1 item 12 ("About this" and the facts list), v4.1 D12 and CLAUDE.md "Never" 1's "ticked rows open Edit N items" change. Each needs your explicit decision recorded when you lock: keep, change code, change spec, drop.

## Plain English summary

The bottom of a details page becomes: the story and the people on top, then one calm "Your copy" row with small chips for the things you would check (1080p, H.264, AAC 5.1, MKV). A "File details" button opens a neat panel with the full video, audio and file facts, and the file name and location stay hidden until you ask. History, tick boxes and the extra links leave the page; the Sources row keeps one link per site for this title only. The Season dropdown moves up next to the Episodes title. All of it works on desktop, tablet, phone and TV, and a very wide screen gets a wider window so landscape pictures are not cut off.
