# Spec: Home, detail page and tile redesign

> **Status:** Built · **Version:** 1 · **Last updated:** 2026-11-03 (locked by Shy) · **Related docs:** [One shared look](one-shared-look.md) · [Collections and lists](collections-and-lists.md) · [Universal editor](universal-editor.md) · [Video player controls](video-player-controls-and-bookmarks.md) · Local check 2026-11-02 items 16, 17, 18, 20 to 24, 34, 35 (items 34 and 35 are owned by the build thread "Fix bugs from the 2 Nov review") · Mockups: [home-detail-and-tiles.html](../design/mockups/home-detail-and-tiles.html)

> **Built (2026-11-03):** every packet is built, including the cover-level "+" and "..." buttons. Download in the icon row (D19) moved to the native-app pass; Local checks are in [validation/batch.md](../validation/batch.md).

## 0. Product walkthrough (what you will see)
1. **Home looks calmer and bigger.** Every cover is portrait. Covers in Watch, Read and Listen rows are larger (about six across on a desktop instead of eight). Nothing grows or turns landscape on hover; the cover gets a thick white border with a purple ring.
2. **Continue is its own landscape row.** Movie and show tiles use the backdrop picture with the title or logo on it. Books and audiobooks show the cover on a soft landscape background. A playlist or one album is one tile; "All songs" on shuffle is a collage of random album art. Each tile has an X; playing it again brings it back.
3. **The hero is shorter** (about 55% of the screen height, down from about 80%), shows book covers at full height, and has only two buttons: the main action and +. The "Featured content" label is gone. New music appears as one "Recently added music" collage slide, never as single albums.
4. **Small square labels.** "New" and "New Episode" are small square tags at the top-left of a cover. Every pill in the app (genres, kinds, facts) becomes square-cornered. Round stays only for avatars, switches, progress bars, the player dock and seek-rail bubbles.
5. **One short "..." menu** on every cover and on the detail page: Restart (only if started), Edit (desktop, if allowed), Delete (if allowed). Larger text, plain wording.
6. **Top bar bookmark.** The My List button in the top bar becomes a bookmark icon, gold when the list is open; click again to close. On covers, adding to My List is a + icon.
7. **Detail page stack.** Title, then facts (rating, seasons, Dolby Vision), then the main button, then one row of small icons (+, thumbs up, "...", plus Download where it applies). Download (phone and native) joins that same icon row.
8. **People.** The person page opens with a large portrait. "Works in your library" uses a small square kind label (no word "Movie") and a filter that appears only when the person has more than one kind of work. People pictures are smaller and square-cornered everywhere else. Collections > People gets search and sort.

## 1. Goal
Make Home, tiles, the hero and the detail page look and behave like one family, closer to HBO Max and Netflix: bigger covers, one clear Continue row, fewer buttons, shorter menus (Uniformity, Simplicity). Fix the bugs found on 2 Nov: blank Continue tiles, tiny book covers in the hero, Watch missing from the hero.

## 2. Scope
**In scope:** Home, Watch/Read/Listen lane tiles, Continue tiles, hero, tile and detail "..." menus, detail page action layout, square pills app-wide, My List icons, person page, Collections > People, people picture size on the detail page, hero diagnosis (section 3).
**Out of scope:** the editor (own spec), the video player (own spec), Needs Review (own planning thread), Settings pages, native apps (they follow after the Dashboard version is checked).

## 3. Diagnosis: no Watch items in the hero (found in code, not yet reproduced)
Home picks hero slides in `DisplayComposerService.Spotlights.cs` then `HomeHeroPicker.cs`. Four causes could each explain it; the build must reproduce with a small generated library before fixing:
- **A. No balance between kinds.** Order is "in the middle of" first, then newest, and only 5 slides are kept. Books and albums (and the Continue row) can fill all five. Most likely cause when the library has recent books or music. Movies do enter the pool, so "no Watch at all" means they are out-ranked, not excluded.
- **B. TV shows are skipped silently** (`Spotlights.cs:47-51`) when no owned episode with a file and parsable season/episode numbers resolves as the target. This matches Shy's report that no episodes appear matched (item 18b), so shows drop out.
- **C. A slide needs some stored artwork (any kind, including collection art) and must not be still settling.** Less likely: the check is lenient.
- **E. Slide count.** The profile's slide count may be below 5.
- **D. Profile hero settings.** Watch could be switched off (default is on).
Fix: reproduce, then add a rule that the slides take turns across Watch, Read and Listen so no kind is shut out, and log (Engine log, debug) why a Watch candidate was dropped.

## 4. Decisions made
Taken from Shy's 2 Nov review (D1 to D12) and the recommended defaults for the rest (D13 onward need Shy's confirmation, see section 9).
- **D1.** All tiles portrait; no hover growth or portrait-to-landscape change. Hover = thick (3px) white border plus purple ring. Keyboard focus uses the same look.
- **D2.** Watch and Read tiles larger; Listen unchanged (square albums stay square, since they are album covers).
- **D3.** Continue rows are landscape for Watching, Reading and Listening.
- **D4.** Hero: shorter, book covers fill the height, one "Recently added music" slide, buttons only main action and +, no "Featured content" label.
- **D5.** Square pills app-wide.
- **D6.** My List on covers and the detail page is a + icon; top bar button is a bookmark, gold when on.
- **D7.** Small square "New" / "New Episode" label top-left.
- **D8.** "..." menu has only Restart, Edit, Delete. Removed: Mark watched/unwatched, Hide from Continue (replaced by X), Add to collection (collections are admin-only, made on the Collections screen), viewing history.
- **D9.** Detail page order: facts, then main button, then icon row. Smaller buttons.
- **D10.** Person page: bigger portrait; kind filter only when more than one kind; square label without the word "Movie".
- **D11.** Collections > People: search and sort; smaller picture size, shared with the detail page.
- **D13 (Shy, 2026-11-03).** Keep one "Mark as watched" in the detail "..." menu, only when the item is not finished (supersedes the removal in item 20).
- **D14 (Shy, 2026-11-03).** Viewing history leaves detail pages and menus; Settings > Activity stays.
- **D15 (Shy, 2026-11-03).** Large people picture only in the person page hero; one small square size (88px) everywhere else.
- **D16 (Shy, 2026-11-03).** Hero slides take turns across Watch, Read and Listen, in-progress first inside each kind. Supersedes Home page redesign D1/R1.
- **D17 (Shy, 2026-11-03).** Round stays for avatars, switches, progress bars, the player dock and seek bubbles.
- **D18 (Shy, 2026-11-03).** Music Continue uses the plain square label as in the mockup.
- **D19 (Shy, 2026-11-03).** Download is added to the same icon row, not given its own row. The main button is separate from the row, so the row can hold whichever buttons apply to the item.
- **D20 (Shy, 2026-11-03).** No trailer button. Trailers are not fetched, and one added by the person appears as an Extra. A trailer button can return in a later spec.
- **D12.** Fable review of continuity requested by Shy (section 10).

## 5. UI behaviour
**Tile sizes (desktop):** portrait width about 180 to 220px (today 176 to 240px tall-based) giving about six per row at 1440px; landscape Continue about 320px wide, four per row. Tablet 4 portrait or 3 landscape; phone 3 portrait or 1.5 landscape (peek of the next tile).
**Continue picture order for a Movie or Show:** backdrop, then episode still, then the cover on a blurred background. **Title:** show the title's logo if there is one, otherwise the title as text, always (fixes the blank tiles). Progress bar at the bottom. X at the top right on hover and always visible on touch.
**Books / audiobooks:** cover left on a blurred, darkened copy of itself; title, author and "Chapter 12 of 40" on the right.
**Music:** one tile for the playlist or album being played; "All songs" with shuffle shows a collage of random album art (eight tiles from the library, picked once per session so it does not flicker). Albums played in the past are not added individually.
**Empty:** a row with nothing to continue is hidden. **Loading:** grey tiles with the final proportions. **Error:** the shared error state per row. **Partly ready:** a tile still being identified shows its file name and a "Getting details" label and cannot be the hero.
**Hero:** about 55svh on desktop (min 22rem), phones unchanged (fixed-height card). Slide labels such as "Continue Watching" stay as small square labels; "Featured content" is removed. The Recently added music slide shows an eight-cover collage and opens Music > Recently Added.
**Menus:** bigger text (16px), 44px row height, no icons or descriptions.
**Detail page:** facts line wraps; main button about 40px tall (today about 56px); icon buttons 34px; Download (phone and native) is one more button on the icon row; the row takes whatever buttons suit the item.
**People:** pictures 88 to 96px on the detail page and Collections; person page portrait about 340px wide on desktop, full width on phone.
**Desktop/tablet/phone/TV:** Edit never shows below desktop (existing rule). TV keeps its own focus look.

## 6. Data / model changes
- **Hero rule** (Engine only): balanced rotation across Watch, Read, Listen, "Recently added music" collage item. New field on the hero data: a list of collage cover addresses. Wire change goes through Contracts tests and fixtures (CLAUDE.md Never 3).
- **Continue X:** reuses the existing per-profile `hide_continue` mark (confirmed per profile). Playing the item again does NOT clear it today, so packet 3 adds that reset. No new table.
- **Continue landscape art:** Engine adds backdrop address to Continue cards where missing (cards already carry background addresses for the hero).
- **Mark watched / viewing history:** UI removed only. Engine actions stay (Activity and sync rely on them) until Shy confirms in D13.
- Nothing is stored in the data store beyond the above; no migrations.

## 7. Work packets
1. **Square pills and shared look (Complex).** Redefine the pill radius token to 3px, replace the literal 999px pill uses (135 in 77 files; two tests assert 999px and must change) that are pills (keep avatars, switches, progress), thicker hover border, remove hover grow and the portrait-to-landscape hover (`MediaTileHoverPolicy`, `MediaTile.razor.css`, `app.css` `.tl-card-hover--media`, `AppChip`, `AppMediaPill`). Depends on: none. Done when: no pill on Home, detail or Settings has rounded ends and no tile changes size on hover.
2. **Bigger tiles and New label (Standard).** Larger portrait sizes, "New / New Episode" square label top-left, "..." button on every cover. Files: `MediaTile.razor(.css)`, `MediaTileShelf`. Depends on: 1.
3. **Continue landscape tiles (Complex).** New landscape Continue tile (movie, show, book, audiobook, album, playlist, shuffle collage), title or logo always present, X action. Files: `ContinueAcrossMediaSection`, `ContinueWatchingOrListeningTile`, `PickUpCard`, Engine Continue cards in `DisplayComposerService`. Depends on: 1.
4. **Hero (Complex).** Reproduce Watch missing (section 3) with a test, add balanced rotation, shorter height, cover-fill for books, Recently added music slide, remove "Featured Content" and extra buttons. Files: `DisplayComposerService.Spotlights.cs`, `HomeHeroPicker.cs`, `CinematicHeroCarousel.razor(.css)`, `PhoneHeroCard`, Contracts. Depends on: 1 (shares label styles).
5. **Menus and My List icons (Standard).** Short "..." menu, remove Mark watched, Hide from Continue, Add to collection, viewing history; top bar bookmark; + on covers. Files: `HeroActionRow.razor`, `ManageActionsMenu`, `OverflowActionMenu`, top bar. Depends on: 1.
6. **Detail page stack (Standard).** Facts above the button, icon row below, smaller buttons, Download row on phones. Files: `DetailHeroContent`, `HeroActionRow`, `DetailHero`. Depends on: 5.
7. **People (Standard).** Person page hero portrait, kind filter, square kind label; Collections > People search and sort; shared smaller people size. Files: `PersonTile(.css)`, person detail components, `Collections.razor`. Depends on: 1.
8. **Docs and guardrails (Mechanical).** Update `presentation-rules.md`, design system, docs site, a guardrail test that fails if a pill uses a round radius or a tile scales on hover. Depends on: 1 to 7.
9. **Owned elsewhere.** The blank page after closing the detail page (item 34) and Continue tiles without a title or logo (item 35) are being fixed in the bug-fix build thread. This spec does not touch them; packet 3 builds on that fix and must be started after it merges.
Waves: 1 then (4, 9), then 2, 3 one after another (both edit `MediaTile.razor`, give the overlay buttons "...", X, + to packet 2 only), then (5, 7), then 6, then 8. Packets 3 and 4 both change Contracts, so refresh fixtures one at a time.

## 8. Local checks
On SHY-DESKTOP with a small test library (never the real one): Home at 1440 and 1920 widths (tile size feels right), hover and keyboard focus border, Continue row for each kind, X then play again, hero with Watch items present, hero on a library with many new books, Recently added music slide, detail page on a show and a movie, phone width, person with several kinds of work.

## 9. Risks and open questions (need Shy)
1. **Mark watched removal (D13).** Without it, someone who watched a film elsewhere cannot tell the Library. Recommend: finish automatically at 90% watched (already so) and keep a single "Mark as watched" in the detail page "..." menu only when the item is not finished. A: remove fully as asked. B (recommended): keep one entry.
2. **Viewing history (D14).** Removing the screen also removes how a person sees what they watched. Recommend: remove from detail pages and menus but keep Settings > Activity. A: remove everywhere. B (recommended): keep Activity.
3. **People picture size (D15).** Person page wants large; lists want small. Recommend large only on the person page hero, one small square size everywhere else.
4. **Hero balance (D16), reverses Home page redesign D1.** Recommend slides take turns Watch, Read, Listen (items in progress first inside each kind). Alternative: keep recency but guarantee at least one Watch slide.
5. **Square pills exceptions (D17).** Recommend avatars, switches and progress bars stay round.
6. Native apps (Android, Apple, Roku) get the same changes in a later pass.

## 10. Gaps and drift (Fable review, 3 Nov)
Each needs Shy's decision (recorded in section 4 once answered).
- **Supersedes Locked/Built decisions:** Home page redesign D1/R1 ("strictly newest, in-progress first, no per-type cap") if D16 is chosen; Universal editor D2/D7 ("Edit this show/album/book") and Details overlay D5/D18 (icon column, red Delete last) by D8; design system "pill 999px", presentation-rules "round for a person" and Library area pages WP4 (round photo) by D5 and D15. Packet 8 updates all of them.
- **Heart clash is real:** My List and Favourite share one heart today (`HeroActionRow.razor:127-129`, hero, My Library empty text). Packet 5 keeps the heart for Listen Favourites only and renames the My Library wording.
- **People size:** one number, 88px, on the detail page and Collections; person page hero separate.
- **Item 16 "use the pills from earlier screenshots"** for music Continue is unclear; mockup uses the plain square label. Shy to confirm.
- **"Parity with the player"** cannot be checked until the player spec exists; the "..." menu here is not the player's Settings menu.

## Plain English summary
Home gets bigger, calmer covers that never change shape when you hover, with a thick highlight instead. Continue becomes its own wide row that always shows what each thing is, including books, audiobooks, playlists and shuffle. The big banner gets shorter and simpler. Menus shrink to Restart, Edit and Delete. The detail page puts facts first, then the play button, then a small row of icons. People pages get a big photo and a filter. I also found likely reasons Watch never shows in the banner (the rotation is purely newest-first, and shows with no matched episodes are skipped), which I will confirm with a test before fixing.

## Gaps and drift (audit 2026-11-03)

- Delivered by evidence. Open decisions: trailer button dropped (D20) and one Mark as watched kept (D13) against Shy's 2 Nov notes; person works kind filter and the log of skipped Watch hero candidates not found. Suggested status: Built, with open decisions. Full report: [spec-audit-2026-11-03](../validation/spec-audit-2026-11-03.md). Status unchanged until Shy decides.
