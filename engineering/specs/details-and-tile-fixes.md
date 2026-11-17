# Spec: Details page and tile fixes (quadrant group and person tiles, Pick up tiles, Continue page, person page, collection page, episode frames)

> **Status:** Locked (Shy said "build the rest", 2026-11-17; every open question took the recommended option) · **Version:** 1 · **Last updated:** 2026-11-17 · **Related docs:** [One tile design](one-tile-design.md) · [Collections and lists](archive/collections-and-lists.md) (D18 fan, replaced here) · [Details page bottom](archive/details-page-bottom.md) (D10, changed by WP9) · Issues 1040, 1042, 1044, 1049, 1052, 1055, 1059, 1061, 1063, 1068 · Mockups: [details-and-tile-fixes.html](../design/mockups/details-and-tile-fixes.html)

## 1. Goal
Fix ten problems from the 2026-11-14 local check so groups, people and Pick up items read at a glance, and the details pages match each other. One quadrant tile design replaces the fan; one Continue page replaces two broken ones.

## 2. Scope
**In scope:** the ten issues above. **Out of scope:** native clients; the person-page "bottom-left block" (#1052) until Shy sends a screenshot (parked); the pixel layout of the episode area (#1042) beyond the written rules, which Shy checks against the mock-up in the batch review.

## 3. Decisions made
- **D1 (Shy).** Groups (collection, Series, Universe) use a **quadrant tile**: four covers in a 2 by 2 grid, kind word top left, count bottom left, standard poster size with portrait cells so no cover is cropped. A group of albums is square with square cells. A custom cover replaces the quadrants. The fan, the single-cover group fallback and the landscape and square fan styling are retired (D18 of collections-and-lists is superseded for the fan).
- **D2 (Shy).** A person with works uses the same quadrant tile: artists square (four albums), directors, authors and others poster-shaped. The person's square photo sits bottom left with the count beside it. A person with no works, and a cast member, is a square photo with the name on the art. The round person tile goes away everywhere (Listen, cast lists, people pages); the people-with-works tile also serves the contributor shelf tile (`FromContributorShelf`).
- **D3 (Shy).** Pick up tiles are all landscape, the same size as the show tile. Books and audiobooks show their cover on a wash with a kind word (Book, Audiobook) top left. The line under the tile is the **content rating** (R, PG-13, TV-MA and so on), never the star rating, for movies and TV; for books and audiobooks a rating only when one is available, otherwise author or narrator and progress. The season and episode label (S3 E8) sits just above the progress bar, never over it, so a few percent of progress stays visible. The progress bar stays full width on the bottom edge.
- **D4 (recommended, taken).** "Continue Watching" (and Continue Reading and Listening) in the left menu opens one Continue page listing everything in progress, newest first, once each, using the same tiles as the discover row. It replaces the Movies tab with an "In progress" filter and the compact strip.
- **D5.** The Rate pop-up is centered horizontally above the thumb button on every details page, kept inside the screen on a phone.
- **D6.** Person page: band members and "member of" groups show their local picture like aliases do (the Engine uses the same picture builder for all three).
- **D7.** Collection page moves onto the shared details layout so its Items row scrolls sideways (wheel, drag, arrows, swipe). Series and Universe pages that share it are checked.
- **D8.** Episode frames (the picture from the video) are used wherever an episode shows a picture: the list, the episode hero and the editor's episode dropdown, using the same effective-artwork rule.
- **D9.** Show details, episode area (#1042): remove the separate "Episodes" line; the season or episode count shows at the top of the page; "N episodes owned" sits under the row on its own line with the collection tile icon (the one the video collection page uses); the Jump to episode dropdown aligns to the right edge, level with the season dropdown. This changes D10 of details-page-bottom (heading on its own line); the written rules here govern.
- **D10.** Video collection details: the line under the title shows only the folder name; the count stays on the right.
- **D11 (parked).** #1052 waits for Shy's screenshot; nothing on the person page is removed until then.

## 4. UI behaviour
| Situation | Behaviour |
|---|---|
| Group with fewer than 4 covers | Cells filled with what exists; empty cells plain surface |
| Group with no covers | Plain tile with the kind icon |
| Person with works | Quadrant tile, photo bottom left |
| Person with no photo | Plain square with initials/icon |
| Continue page empty | Empty state, as other pages |
| Phone | Same tiles at 112 px width; Rate pop-up stays on screen |

## 5. Data / model changes
No data-store change. Engine: person related links use the shared headshot-URL builder. Wire shape unchanged (Contracts tests unaffected).

## 6. Work packets
- **WP1 Person pictures (#1068), Rate pop-up (#1049), video collection header (#1040).** Standard. Files: `DetailCompositionOrchestrator.PeopleBuilder.cs`, `MediaRateControl.razor.css`, video collection header. Done when: group members and member-of groups carry a picture URL in a test; pop-up centered above the thumb; header shows only the folder name.
- **WP2 Quadrant tile (#1063, D1, D2).** Complex. Files: `MediaTile.razor(.css)`, `PersonTile`, `CollectionSurfaceTileComposer`, `MediaPersonGroupTileComposer`, `PersonTileGrid`. Depends on: nothing. Done when: groups and people with works draw quadrants; old fan, single-cover fallback and round tile are removed; guardrail tests pass.
- **WP3 Pick up tiles (#1055, D3).** Standard. Files: `ContinueLandscapeTile.razor`, `PickUpCard.razor`, tile composer. Depends on: WP2 (shared CSS).
- **WP4 Continue page (#1059, D4).** Standard. Files: `ShelfGridPage.razor.cs`, `MediaBrowseShell.*`, left-menu route.
- **WP5 Collection page layout (#1061, D7).** Complex. Files: `Collections` page and its group detail view.
- **WP6 Episode frames (#1044, D8).** Standard. Files: `DetailHero*`, editor header nav, effective-artwork resolver.
- **WP7 Episode area (#1042, D9).** Standard. Files: `SequencePlacementPanel.razor(.css)`, `OverviewTab`.
- **WP8 Docs and batch.** Mechanical. Update tuvima-docs presentation rules for the quadrant tile, Continue page and Pick up tiles; add Local checks to `engineering/validation/batch.md`.

## 7. Local checks
On SHY-DESKTOP with a small test library, at 1024, 1280, 1440 and phone: (1) Collections page shows quadrant tiles with kind words; (2) Listen artists and a director show square-photo quadrant tiles; (3) Pick up: movie, episode (low progress, bar visible), book, audiobook; ratings not stars; (4) Continue Watching menu page shows movies and shows once each; (5) Evanescence members show pictures; (6) Rate pop-up centered; (7) collection Items row scrolls; (8) episode hero and editor dropdown show frames; (9) show page episode area against Shy's mock-up; (10) video collection header.

## 8. Risks and open questions
Removing the round person tile touches many pages (visual review). The episode area (#1042) was written from the issue text; the attached picture was not readable in the cloud. #1052 is parked.

## 9. Gaps and drift
D18 of collections-and-lists (fan) and D9 of one-tile-design (name on the art for groups) are superseded by D1.
