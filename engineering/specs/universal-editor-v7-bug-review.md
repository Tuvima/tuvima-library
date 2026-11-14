# Spec: Universal editor v7: one header, a plain Match view, artwork that applies at once

> **Status:** Locked (by Shy, 2026-11-14 07:42Z: "lock it") · **Version:** 1 · **Last updated:** 2026-11-14 · **Related docs:** [Universal editor v6](universal-editor-v6-match-and-aligned-rows.md) (Built) · [Universal editor v5](universal-editor-v5-header-navigation-and-supporting-files.md) · [Universal editor v2](universal-editor-v2.md) · [Look v3.1](look-pass-v3-1-controls-and-type.md) · [Mockups](../../design/mockups/editor-v7/editor-v7-mockups.html) · Issues 989, 990, 991, 992, 993, 994, 995. Issues 988 and 996 are built in other threads (section 2).

*This is a new version, not a rewrite: v6 stays as built and archived. Where this version reverses something v2, v5 or v6 locked, it says so in section 3 and in section 9.*

## 0. Why

Shy walked the built editor on 2026-11-14 and found it heavier than it needs to be: a broken cover in the header, artwork drawn far too large, a header that changes shape on Match, a "Matched" word that says nothing, odd link buttons, explanatory text and duplicated facts on the Match tab, portrait-only pictures in match results, a Review step that repeats itself, a floating "Saved" box, and artwork that waits for Save and, for a show, does not stick. Eight issues (988 to 995) record it; 988 is built elsewhere (section 2). Five of them (the header on Match, the artwork size, artwork waiting for Save, the Saved ribbon, the matched card's contents) reverse decisions Shy locked earlier, so they are recorded here as deliberate overrides.

## 1. Goal

Opening the editor should show the same calm header on every tab, with the match told by two small links. The Match tab of a matched item should show the match and two actions, nothing else. Artwork should be small enough to judge at a glance, change the moment it is chosen, and save should be about data only.

## 1a. Plain-English walkthrough

Numbers match the work packets in section 6.

1. **The header cover shows (not built here).** The broken header cover is fixed in the thread "Fix missing covers and video frames" (issue 988). This version assumes it has landed.
2. **One header, every tab (WP2).** Match no longer shrinks the header to one line. Cover, title, facts, Home and the item list look exactly as they do on Details. The "Media providers / Wikidata" choice moves to the top of the Match tab's body.
3. **The links are the match indicator (WP2).** The "Matched" tag is removed. At the top right, a link to TMDB (or the item's provider) and a link to Wikidata appear when the item is matched to them. An item with no match shows the words "Not matched" instead. A source with no page shows no link. The links are plain outlined buttons: the name and one small arrow, no letter badge.
4. **A matched item shows the match, plainly (WP3).** The Match tab opens with the match: cover, title, year, key facts and a short description. Under it, Remove match on the left and Change match on the right, far apart. Gone: "Strong match", the table that shows agreement, "Based on…", "TMDB is shown from what Library saved when it matched", "Fields you changed stay yours…", "Links this audiobook to apple_api", the provider's code name, and the Open source button (the header link does that). Product names only: Apple Books, TMDB, TheTVDB.
5. **Results keep their picture shape (WP4).** Audiobooks, albums and songs show square pictures in the results and the card; books, movies and shows stay portrait; episodes stay wide.
6. **Artwork fits (WP5).** The Artwork tab uses a small preview at the picture's real shape, a one-row set of version thumbnails, and a "View full size" link. No scrolling in the editor window.
7. **Artwork applies at once (WP6).** Choosing, uploading, adding from a link or from the Library, or making a picture preferred changes the item immediately. Earlier pictures stay in the versions row, so going back is just picking again. Save changes is for data only. The bug where a show's chosen picture did not stick is found with a test first and fixed.
8. **Review changes is compact (WP7).** One line per change, the same fact never repeated, one amber block for a file move, and a footer in one row: Back to editing on the left, Discard and Save changes on the right.
9. **No "Saved" box (WP8).** After Save the item shows the app's normal Updating state, the same as during ingestion. A failed step shows the app's normal warning on the item, and History says which step.
10. **The phone follows (WP2 to WP8).** The phone header was already one line on every tab; its state line goes, leaving only cover and name; the links sit on the Match tab. Everything else is the desktop change laid out for a phone (mockup screen H).

**What stays the same:** locked fields, Review changes and Save changes for data, Apply match only stages, Delete, History, Bookmarks, the supporting files browser, the TV, music and Wikidata match flows (they take the new header and the plain-text clean-up, nothing more), who may edit.

**Example.** Shy opens *Arrival*. The header is the same as on Details, with TMDB and Wikidata at the top right. Match shows the film, its facts and two buttons: Remove match, Change match. Artwork shows a small cover; Shy taps another version and the cover changes at once. No Save is needed.

## 2. Scope

**In scope**
- The header on every tab of the editor: cover fix, the match links, unmatched state, link styling.
- The Match tab for every kind: the matched view, removed text, provider names, picture shapes.
- The Artwork tab: size, apply at once, the show bug.
- The Review changes step and its footer.
- The Saved ribbon and its replacement by the item's Updating state.
- Phone layouts for all of the above.

**Out of scope**
- The editor's other tabs, the supporting files browser and the series chooser (v6 as built).
- New artwork sources or editing a picture (crop, resize).
- The broken header cover (issue 988): built in the thread "Fix missing covers and video frames". This spec depends on it; the header work (WP2) and the Local checks assume covers load.
- A hand match leaving a Needs Review row (issue 996): built in the thread "Fix manual match and Wikidata matching". Same dependency: the matched view (WP3) assumes a saved match settles correctly.
- The Updating state's own design, which belongs to the shared look; this spec reuses it.

## 3. Decisions made

Shy's review of 2026-11-14 is the source. **Deliberate overrides** are marked; each replaces a locked decision and nothing else about it.

- **D1. The editor header is the same on every tab (Shy, 2026-11-14 02:53Z: "the Match tab changes the header, why? Leave the header alone").** **Override of v6 D2 and v5 section 4** (one-line header on Match). Reason: a header that changes shape on one tab is a visible jump and was a trade-off v6 accepted for room. The Media providers and Wikidata choice moves into the tab body (v6 D1 keeps its two-way selector, drawn at the top of the body). The Match tab's list and card scroll inside the window body; the header and footer stay put.
- **D2. No "Matched" tag; the header's top-right links are the match signal (Shy, same message).** Replaces v6's state dot with "Matched to <provider>" and the "Matched" word in the facts line. A link means matched to that source and the source has a page; no link means nothing to open. An item with no match shows the plain words "Not matched" where the links would be. **New detail, not Shy's words: a source that matched but has no page shows nothing**, so the unmatched state is the only place a word appears.
- **D3. Link buttons are plain.** The source's name and one small outward arrow, outlined, 32 px high, the shared secondary look. No letter badge, no dark pill, no second icon (Shy: "the icons look strange"). Names are product names (TMDB, TheTVDB, Apple Books, MusicBrainz, Wikidata).
- **D4. Internal provider names never appear (Shy: "apple_api").** One lookup from provider key to product name, read from the provider config, used by the Match tab, the header links and the history lines.
- **D5. A matched item's Match tab shows the match and two actions (Shy, points 6 to 8, 11).** Cover, title, year, key facts, short description. Remove match and Change match, one at each end of one row. **Override of v6 D10** (Open source, Change match, Remove match) and **v6 D7's** verdict, compare table, basis note and the line under Apply match for an applied match. The verdict (Strong, Possible, Weak) and the compare table remain only on the candidate being considered in a search. The saved-from note, the "Fields you changed stay yours" line and the "Links this {kind} to {provider}" line are removed everywhere.
- **D6. Apply match has no explanation line under it.** The button stays; the line goes.
- **D7. Match results are drawn in their own picture shape (Shy, point 9).** Portrait for books, movies and shows; square for audiobooks, albums and songs; wide for episode stills. The provider's own shape wins when it says square or wide. Pictures are never cropped or stretched to fit.
- **D8. Artwork previews are small (Shy, point 2: "I don't need 1024+, I need to see it without scrolling").** **Override of the v2/v5 "large zoomable preview".** The preview is about 200 px on its long side at the role's real shape, the zoom steps go, the versions are one wrapping row of small thumbnails, and "View full size" opens one picture large for anyone who wants it. The whole tab fits a window of about 700 px high with no scrolling at 1024 and wider.
- **D9. Artwork applies the moment it is chosen (Shy, point 13: "it should be instant… the save should be for material changes to data, not artwork").** **Override of v2 and v5 "everything waits for Save changes except Remove".** Choose, upload, From URL, From Library and Make preferred all apply at once. Review changes lists no artwork lines. **No Undo (Shy, 2026-11-14 07:40Z: "we are just selecting a new picture… we don't need an undo for that").** The earlier pictures stay in the versions row, so a mistaken pick is reversed by picking the old one. Discard does not touch pictures. Remove keeps its confirmation. A picture applied in a Managed folder follows the folder's normal write-back rule (same as a changed match); nothing about write-back changes.
- **D10. A show's chosen picture must stick (Shy, point 13, first half; issue 990 part 1).** The cause is not yet found; WP6 begins with a failing test on a show, then fixes it, and checks that the show page and its tiles pick up the new picture without a stale copy.
- **D11. Review changes is a compact list (Shy, point 12).** One line per change (field, old value struck through, new value), the same fact merged into one line, one amber block for a file move or rename, and a footer in the usual order: Back to editing at the left, Discard and Save changes at the right in one row on desktop, stacked on a phone with Save changes first. Pictures never appear.
- **D12. The "Saved N changes" ribbon is removed (Shy, 2026-11-14 02:58Z: "remove it entirely and just show the item as 'updating' like we normally do across the app as things are ingested. It's really no different").** **Override of v2 D7 and the 2026-10-30 choice 1A.** After Save the window closes and the item shows the app's normal Updating state until the Engine finishes. If a background step fails the item shows the app's normal warning, and the editor's History tab says which step. "See what changed" is the History tab. Issue 995 option B.
- **D13. The header cover fix (issue 988) is not part of this version.** It is built in "Fix missing covers and video frames"; this version depends on it.
- **D14. On a phone the header shows no match links (Shy, 2026-11-14 07:38Z: "on a mobile device I don't think we need the tmdb/wikidata links in the header… it can simply be the movie name / cover art").** The one-line header is the small cover and the title, with the state line removed. The links move into the Match tab: a matched item's card ends with its source links, and an unmatched item says "Not matched" there. (Placement in the tab is this spec's reading, not Shy's words.) Remove match and Change match sit side by side. Artwork shows two previews per row. Review changes is the full-screen step with the stacked footer. Same rules as desktop (v6 D19 to D22).

## 4. UI behaviour

| Situation | What the person sees |
|---|---|
| Header, matched to a source with a page | That source's link, and Wikidata's if it has one. |
| Header, matched, a source has no page | No link for that source. |
| Header, not matched | "Not matched" in place of the links. |
| Match tab, matched | The match plus Remove match and Change match. Nothing else. |
| Match tab, Change match | The search row, Your file and results; the verdict shows on the candidate being considered. |
| Match tab, unmatched | The search row, Your file and results as in v6, minus the Apply explanation line. |
| Results, audiobook, album, song | Square pictures. Episode results: wide. |
| Artwork, a picture chosen | It applies and is outlined in the versions row; the item's page behind the window is unchanged until the window closes, then shows the new picture. |
| Artwork, applying | The preview shows a short busy state on its own; the buttons stay in place. |
| Artwork, a change fails | One plain line under the actions: what failed and Try again. The previous picture stays. |
| Review changes | The compact list; an amber block only if a file will move. |
| After Save | The item shows Updating until done; a failed step shows the item's normal warning. |
| Window narrower than 1024 | The Artwork tab keeps its one column of preview above actions and versions. |
| Phone or tablet in portrait | Header: cover and name only. Match tab carries the links or "Not matched". Section 3, D14; mockup screen H. |
| TV | No editor (unchanged). |

## 5. Data / model changes

None to stored data. Wire changes: the match search result gains the picture's shape (portrait, square or wide) where the provider says so (issue 993); the editor's artwork choice becomes an immediate action on the Engine; the header read supplies a sized cover address and the list of match links with product names. Contracts tests and fixtures change with them.

## 6. Work packets

File names come from a first read of the code and are to be confirmed by the build.

- **WP1. (Withdrawn.)** The header cover fix (issue 988) is built in another thread. Numbering is kept so the mockup notes and waves still read the same.
- **WP2. One header and the links (Standard).** D1 to D4, D14 header line. Files: `EditorItemHeader.razor`, `EditorWindowChrome.razor`, `EditorPopover.razor`, `EditorMatchPanel.razor`, the provider display names in config. Depends on: the header cover fix (issue 988, other thread). Done when: the header is identical on every tab at 1024, 1280, 1440; matched, partly matched and unmatched items show the right right-hand side; no code name appears.
- **WP3. The plain matched view (Standard).** D5, D6: remove the strings and rows listed; the two spaced actions. Files: `EditorMatchPanel.razor`, `EditorMatchCard.razor`, `EditorMatchApply.razor`, shared strings (`SharedStrings.resx`). Depends on: WP2. Done when: a matched movie, book, audiobook and episode show only the match and the two actions, and a search still shows the verdict on its candidate.
- **WP4. Square pictures (Standard).** D7. Files: `EditorMatchListRow.razor`, `EditorMatchCard.razor`, `EditorMatchSearchService.cs`, `src/MediaEngine.Contracts/Editing/`, fixtures. Depends on: nothing. Done when: an audiobook search and an album search show square pictures uncropped, a movie search portrait, an episode search wide.
- **WP5. Compact Artwork (Standard).** D8. Files: `EditorArtworkTool.razor` and its styles. Depends on: WP2. Done when: the tab fits a 700 px window at 1024 and 1280 with no scrollbar of its own; View full size opens one large picture.
- **WP6. Artwork applies at once (Complex).** D9, D10. Failing test first for a show's pick; an immediate apply action on the Engine; Review lists no artwork lines; the show page and tiles refresh. Files: `EditorArtworkTool.razor`, the editor's artwork Engine actions, the staged-change store, `ReviewChangesView.razor`, Contracts. Depends on: WP5. Done when: a pick, upload, URL, Library choice and Make preferred each show on the item page without Save, picking the previous version restores it, and the show test passes. Security: the immediate action uses the same permission check as Save.
- **WP7. Compact Review changes (Standard).** D11. Files: `ReviewChangesView.razor`, the shared dialog footer rule (use the footer variant already in the shared look; do not change it for others). Depends on: WP6. Done when: footer is one row at desktop and stacked in the phone order; the same fact never appears twice.
- **WP8. Remove the ribbon (Standard).** D12. Delete `EditSavedRibbon.razor` and its strings; after Save the item enters the app's Updating state; a failed step uses the item warning and History. Depends on: nothing. Done when: Save shows no box, the item shows Updating then clears, and a forced step failure shows the item warning.
- **WP9. (Withdrawn.)** Issue 996 is built in "Fix manual match and Wikidata matching".
- **WP10. Mockups, docs, versions (Standard).** Mockups under `engineering/design/mockups/editor-v7/`; version notes on v2, v5 and v6; the public editing guide; closure notes on 989 to 995. Depends on: all.

Waves: (WP4, WP8) → (WP2, WP5) → (WP3, WP6) → WP7 → WP10.

## 7. Local checks

- Mock beside Live at 1024, 1280, 1440 and phone width for: Match unmatched, Match matched, Artwork, Review changes, header link states.
- Header cover for a movie, a book, an audiobook, an album, a song, a show and an episode (checks the other thread's fix landed).
- A real movie with TMDB and Wikidata: both links open the right pages; an unmatched file shows "Not matched".
- Match tab on a matched movie, book, audiobook and episode: no explanatory lines, no code names, two buttons spaced apart.
- An audiobook search and an album search: square covers, uncropped.
- Artwork on a show, a movie and an album: pick another picture, see it on the page at once without Save, pick the old one again; Save shows no picture lines. In a Managed folder with write-back on, check the file on disk follows the normal rule.
- Save a change and watch the item: it shows Updating, then clears; no box appears. Break a background step and check the item's warning.
- On a real phone: the same pass.

## 8. Risks and open questions

- **The Match tab scrolls inside the window.** With the full header there is less room for results and the card. The body scrolls; the header and footer stay. This is the price of D1, which Shy chose.
- **Immediate artwork and Discard.** Discard does not undo pictures, which may surprise someone who expects it to. The versions row is the way back; the footer's wording stays "Discard" (data only).
- **Immediate artwork in a Managed folder** writes through the normal write-back rule at once rather than at Save. Same rule, earlier moment.
- **A source with no page shows nothing**, so a matched item with no pages at all would show no right-hand text. Rare; if it appears in practice, a quiet "Matched" can be reconsidered.
- **Mockups:** `engineering/design/mockups/editor-v7/editor-v7-mockups.html` (screens A to H). Screens for TV, song, Wikidata, Details and the series chooser are the v6 mockups under the new header.
- **Open questions:**
  None. (Undo and the phone header were answered by Shy on 2026-11-14; see D9 and D14.)
  2. (Answered by Shy 2026-11-14: no links in the phone header; see D14.)

## 9. Gaps and drift

| Document | What changes |
|---|---|
| Universal editor v6 (Built) | D2 (one-line header on Match) reversed by D1. D10 (Open source, Change match, Remove match) replaced by D5. D7 and D8's verdict and compare table for an applied match removed; the line under Apply match removed. State dot "Matched to <provider>" replaced by D2. Section 4a's phone header state line follows D14. |
| Universal editor v5 (Built) | Section 4's header-on-Match note reversed. "Large zoomable Artwork preview" replaced by D8. |
| Universal editor v2 (Built) | D7 (the save ribbon) removed by D12. "Everything waits for Save changes except Remove" replaced by D9. |
| CLAUDE.md "Never" 1 | Remove "Match shows a one-line header with Media providers and Wikidata as tabs"; add "the header is the same on every tab; the match links at its top right are the only match signal". Remove "Nothing is saved until Save changes" in favor of "Nothing about the item's data is saved until Save changes, which shows Review changes first; artwork applies at once". Note the saved-ribbon is gone. |
| `src/MediaEngine.Web/CLAUDE.md` | Same wording changes where it repeats the above. |
| Public docs (`tuvima-docs`) | The editing guide: the header, the Match tab, Artwork (applies at once), Save and Review changes, no saved box. |
| Editor mockups | `editor-details-revision/` (older) stays as history; `editor-v6` screens C, D, E, F, G stay valid under the new header. |

## Plain English summary

The editor keeps the same header on every tab, so Match no longer jumps. The word "Matched" goes; two small plain links at the top right (TMDB and Wikidata, for example) tell you what the item is matched to, and an unmatched item just says "Not matched". On Match, an already matched item shows the match and two buttons, Remove match and Change match, with all the extra explanation and internal names removed. Audiobook and album results show square covers. Artwork is shown small enough to fit without scrolling, and a new picture applies the moment you choose it; Save is only for real data changes, and its Review step is shorter and tidier. The "Saved" box disappears: the item simply shows "Updating" like it does while being ingested. No questions are open. The broken cover and the Needs Review match bug are being fixed in other threads.
