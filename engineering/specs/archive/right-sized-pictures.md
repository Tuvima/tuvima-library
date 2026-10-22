# Spec: Right-sized pictures on every screen

> **Status:** Built (2026-10-22, awaiting Local checks L1-L4 in [the batch list](../../validation/batch.md)) · **Version:** 1 · **Last updated:** 2026-10-22 · **Related docs:** [Product overview](../../product/overview.md) · [Design system: Pictures](../../design/design-system.md#pictures) · [Presentation rules: Picture sizes](../../../docs/product/presentation-rules.md#picture-sizes-right-sized-pictures-october-2026) · [Artwork architecture](../../../docs/architecture/artwork.md#picture-sizes-and-browser-keep) · Local check bugs B31, B32, B23

## 1. Goal

Every picture on every screen loads at about the size it is drawn, not the size it was downloaded. A 46x46 face loads a few KB instead of ~200 KB, grids of covers load quickly, and a picture seen once is not fetched again on the next visit. Ease of use and speed on phone, desktop and TV come before anything else here.

## 2. Scope

**In scope**
- One size rule that every surface follows (faces, tiles, lists, heroes, backgrounds, details, phone, TV).
- Making the Engine create the small sizes for every kind of picture, including people's faces.
- Telling the browser it may keep pictures, so repeat visits are instant.
- A guardrail test so a new screen cannot quietly ask for the full-size original.

**Out of scope**
- Changing which picture is chosen (Priority Cascade) or how artwork is edited.
- Photos in View (they already have their own sized copies: 160, 320, 640, 1280, 2048).
- Video poster frames and playback quality.

## 3. What is true today (checked in the code)

- The Engine already makes three copies of most artwork when it is saved: small (320 px on the long side), medium (960) and large (2160). Logos are the exception. This exists and works for covers, backgrounds and similar.
- Many screens already ask for a copy by size (tiles, heroes, backdrops, the Listen page, the browse shell: about 25 places).
- **People faces do not.** The face circle on credit cards (`PersonAvatar`) and the People list load the face address with no size, so the Engine sends the full original (the cause of the ~200 KB for 46 px). The Engine side supports `?size=s|m|l` for faces, but nothing in the Dashboard asks for it.
- **Some faces have no small copy at all.** If a face came from the old direct-download path (`LocalHeadshotPath` or an on-demand download), the Engine serves that one original whatever size is asked for, and never makes a small one.
- **The smallest copy is still big for a face.** 320 px is about 7 times the width of a 46 px circle (about 3.5 times on a sharp 2x screen). A 96 px copy would be about 4-8 KB.
- **No keep-it instruction.** The Engine and the Dashboard picture relay send no "keep this for a while" header, so browsers re-check or re-fetch pictures far more than needed. Pictures can change (artwork edits), so the address must change when the picture changes.
- Browsers are not told which size suits their screen on most surfaces (a sharp phone and a 4K TV both get the same copy).

Not yet checked: exact on-screen sizes of each surface, and the actual file sizes on Shy's library. A first work packet measures these before anything is changed.

## 4. Decisions made (proposed, for Shy)

- **D1. Four sizes, one rule.** Add a tiny size to the existing three: **tiny 96 px** (faces, small list thumbnails, badges), **small 320** (tiles, cards, lists), **medium 960** (details posters, TV tiles, phone heroes), **large 2160** (full-screen backgrounds, 4K TV). Reason: Performance. The rule for every screen: ask for the smallest size that is at least the drawn size times the screen's sharpness (phone and laptop screens are usually 2x).
- **D2. Let the browser choose where a screen can change size** (hero on phone vs desktop vs TV). Those use the browser's own "pick the best copy" feature so a phone never downloads the TV picture. Fixed-size things like faces just ask for one size.
- **D3. Every picture gets small copies, including faces and old-path faces.** Missing copies are made the first time they are needed and remembered, with a one-time background catch-up for existing libraries. Pre-release data can also simply be reset. Reason: Reliability (no surface falls back to the original).
- **D4. Pictures are kept by the browser for a long time, with a version in the address.** Address includes a short change marker so an edited picture shows up immediately and an unchanged one is never re-downloaded. Reason: Performance and less load on the Engine.
- **D5. Never fall back to the original silently.** If the size asked for is missing, serve the nearest bigger copy and make the missing one in the background, rather than sending the original. Original sizes are only sent when something truly needs them (download, export, editor preview).
- **D6. Guardrail.** A test fails the build if a picture address on a screen is built without a size, so this stays fixed. Reason: Maintenance ("prevent, don't patch").
- **D7. Format stays as the source (JPEG/PNG/WebP) for now.** Switching small copies to WebP would save roughly a quarter more; revisit after WP1 shows real byte counts (was Q1; Shy locked the recommendation, 2026-10-21).
- **D8.** Existing libraries get a background catch-up job that makes missing sizes (was Q2, locked as recommended).
- **D9.** The tiny size is 96 px (was Q3, locked as recommended).

## 5. Proposed size per surface

| Surface | Drawn size (approx.) | Size requested |
|---|---|---|
| People faces: credit cards, People list, search results | 46-96 px | tiny (96) |
| Small list rows, Events (Added) thumbnails, table covers | 32-64 px | tiny (96) |
| Tiles and shelves on Home, Watch, Listen, Read, browse grids | 140-220 px | small (320); medium on very sharp screens via browser choice |
| Series and Universe tiles, album/book cards | 180-260 px | small (320), medium on sharp screens |
| Details overlay poster / cover | 250-400 px | medium (960) |
| Hero carousels and details backgrounds | full width | medium on phone, large on desktop and TV, by browser choice |
| Now playing bar, mini-player | 48-64 px | tiny (96) |
| Editor artwork picker previews | 150-300 px | small / medium |
| Artwork picker "full view" and downloads | full | original |

Sizes are estimates; WP1 replaced them with measured values in section 11.

## 6. UI behaviour

| Situation | What the person sees |
|---|---|
| Normal | Pictures appear fast and sharp at their size; the People list fills in quickly. |
| First time a small copy is missing | Briefly a neutral placeholder or the next bigger copy, never a long wait. |
| Picture edited | New picture appears straight away (new address), old one is forgotten. |
| Offline / slow connection | Already-seen pictures still show from the browser's keep. |
| TV and large screens | Larger copies are chosen automatically so nothing looks soft. |

## 7. Data / model changes

- Add a "tiny" copy path for each artwork record (and for faces). Pre-release: existing libraries get a background catch-up or a reset, no migration code.
- Add a change marker to picture addresses (derived from the picture's last-changed time).

## 8. Work packets

### WP1 — Measure and agree the size table
- **What it does:** Measure drawn sizes and today's bytes per surface (People list, tiles, heroes, details, phone, TV); replace section 5 estimates with real numbers; report the biggest wins.
- **Files likely touched:** (read-only) Dashboard components under `src/MediaEngine.Web/Components/`, `MediaTileArtworkUrl.cs`; output a table in this spec's follow-up notes.
- **Depends on:** none
- **Size:** Standard
- **Done when:** a table of surface, drawn px, bytes today, size to request is posted for Shy.

### WP2 — Engine: tiny size and complete copies
- **What it does:** Add the 96 px size; make faces use the same managed copies as other artwork (no direct-original path); generate missing sizes on demand; D5 fallback behaviour.
- **Files likely touched:** `ArtworkVariantHelper.cs`, `PersonEndpoints.cs` (headshot), `StreamEndpoints.cs`, `EntityAsset` and its repository, `ArtworkRenditionRepairStartupService.cs`.
- **Depends on:** WP1
- **Size:** Complex
- **Done when:** tests prove a face requested at tiny returns a file of 96 px or less, even for a face that started as a direct download.

### WP3 — Keep-it headers and change markers
- **What it does:** Engine and Dashboard relay send long keep headers; addresses carry a change marker; an edited picture changes address.
- **Files likely touched:** `StreamEndpoints.cs`, `PersonEndpoints.cs`, `ApiImageUrls.cs`, `Program.cs` (`ProxyEngineImageAsync`), `EngineImageProxyPath.cs`.
- **Depends on:** WP2
- **Size:** Standard
- **Done when:** a second load of the same page sends no picture requests (checked in a test and a page read); an edited picture shows immediately.

### WP4 — Dashboard: every surface follows the table
- **What it does:** Faces, lists and the remaining unsized places request the right size; hero and detail pictures use the browser's size choice.
- **Files likely touched:** `PersonAvatar.razor`, `PersonCreditCard.razor`, People page, `MediaTileArtworkUrl.cs`, hero/backdrop components, Listen/Browse components.
- **Depends on:** WP1 (and WP2 for tiny)
- **Size:** Standard
- **Done when:** the People list requests tiny copies and loads under about 10 KB per face; no surface requests an unsized address (WP5 test).

### WP5 — Guardrail test
- **What it does:** A test fails if a screen builds a picture address without a size.
- **Files likely touched:** `tests/MediaEngine.Web.Tests/`
- **Depends on:** WP4
- **Size:** Mechanical
- **Done when:** adding an unsized picture to a component fails the test.

### WP6 — Docs
- **What it does:** Add the size table to the design system and the presentation rules.
- **Files likely touched:** `engineering/design/design-system.md`, `docs/product/presentation-rules.md`
- **Depends on:** WP4
- **Size:** Mechanical
- **Done when:** the table is published and linked from the spec (header links: Design system and Presentation rules).

## 9. Local checks

- **L1.** On the real library, open People: faces fill in fast; browser network panel shows each face well under 10 KB.
- **L2.** Reload Home twice: the second load fetches almost no pictures.
- **L3.** Edit a cover in Editing Mode: the new picture shows right away on the tile and the details page.
- **L4.** Phone and TV web: heroes look sharp, not soft, and do not load the largest copy on phone.

## 10. Risks and open questions

- **Risks:** a stale picture shown after an edit if the change marker is missed (caught by L3); extra disk use for a fourth copy (small: a 96 px file is a few KB per picture); a first-open delay while missing copies are made (D5 shows the next size meanwhile).
- **Open questions:** none. Q1 to Q3 were settled as D7 to D9 when Shy locked the spec.

## 11. Follow-up notes: WP1 measurements (2026-10-21)

Measured in the cloud from the Dashboard CSS and the Engine code. Byte figures are estimates from generated photo-like test pictures saved the way the Engine saves them (JPEG quality 88), because the cloud cannot reach Shy's library or the provider picture servers; L1 checks real numbers. Shy's own report (B31: about 200 KB per face) matches the estimate for a face original.

**Bytes per copy (estimated)**

| Picture | Original | tiny 96 | small 320 | medium 960 | large 2160 |
|---|---|---|---|---|---|
| Face (Wikimedia-like, 1000x1250) | ~250 KB | ~3 KB | ~19 KB | ~118 KB | (original) |
| Poster (2000x3000) | ~1.2 MB | ~3 KB | ~21 KB | ~130 KB | ~510 KB |
| Backdrop (3840x2160) | ~1.7 MB | ~3 KB | ~19 KB | ~116 KB | ~480 KB |
| Square cover (1400x1400) | ~400 KB | ~4 KB | ~24 KB | ~155 KB | (original) |

**Per surface** (drawn size from the component CSS; "today" is what the Dashboard asks for now; the rule is the smallest copy at least the drawn size, at 1x and at 2x screen sharpness)

| Surface | Drawn px | Today | Bytes today (est.) | Request (1x / 2x) | Bytes after (est.) |
|---|---|---|---|---|---|
| People list (Collections > People) | 48 (50 under 920 px) | original | ~250 KB | tiny / tiny | ~3 KB |
| Universe cast list, cast comparison, family tree | 36-48 | original | ~250 KB | tiny / tiny | ~3 KB |
| Search results faces and covers | ~48-64 | original | 250 KB-1.2 MB | tiny / small | 3-20 KB |
| Credit card face and character picture (details People tab) | up to 140 square | original | ~250 KB | small / small | ~19 KB |
| Person details portrait | up to 288 x 432 | original | ~250 KB | small / medium | 19-118 KB |
| Recently Added / Events rows, Settings ingestion rows | 50 | small | ~20 KB | tiny / small | 3-20 KB |
| Library table covers, song table, Listen rows and rails | 32-64 | original (raw address) | 0.4-1.2 MB | tiny / small | 3-20 KB |
| Now playing bar, queue and history rows | 48-64 | small | ~20 KB | tiny / small | 3-20 KB |
| Home, Watch, Listen, Read tiles and shelves | 128-228 wide | small, medium by browser choice when the size is known | ~20 KB | small / medium (browser choice) | 20-130 KB |
| Tile hover panel | 320-440 | medium/large by browser choice | 130-500 KB | medium (browser choice) | ~130 KB |
| Details overlay cover, album and phone hero cover | 250-400 | medium | ~130 KB | medium | ~130 KB |
| Hero carousel and details backgrounds | full width | medium, large by browser choice when the size is known | 116-480 KB | medium on phone, large on desktop and TV (browser choice) | same, chosen per screen |
| Full player (phone, popout, desktop) | 220-520 | medium | ~130 KB | medium | ~130 KB |
| Editor artwork picker preview | up to 640 | large | ~500 KB | medium | ~130 KB |
| Editor thumbnails (header, match, artwork lists) | 64-128 | small | ~20 KB | tiny / small | 3-20 KB |
| Artwork picker "full view", downloads | full | original | as is | original (unchanged) | as is |

**Biggest wins**

1. Faces: People list, cast lists, search and credit cards download the full original (about 250 KB) for a 36-140 px circle. Tiny or small copies cut that by 90-99%, and the People list goes from about 50 MB to under 1 MB for 200 people.
2. About 40 places on Listen, library tables, search, Universe and Settings put the Engine address on screen with no size at all, so they get the original too.
3. Browser keep: the Engine sends "keep for 1 day, shared" on stored artwork and nothing on faces, character pictures and the editor's artwork; nothing tells the browser when a picture changed, so an edited cover can show the old picture for up to a day.
4. Several surfaces already build a "pick the best size" list that the browser never sees, because it is only made when the picture's own size is known. Fixed-size places switch to "1x / 2x" lists, which need no picture size.

## 12. Build notes: defaults picked where the spec left a choice (2026-10-21)

Recorded by the building session. None changes what the person sees; each is the safer or simpler option.

- **B1. Where the tiny copy lives.** Stored like the other three: one more file per picture and one more column on the artwork record (data store upgrade step 0029). Logos still get no copies, so they keep their exact shape.
- **B2. A missing copy.** If a bigger copy exists, that one is sent at once and the missing one is made in the background (D5). If only the original exists (old faces, older libraries), the copies are made on the spot, a few pictures at a time, before answering, because sending the original is exactly what this work removes. Making a face's copies takes a fraction of a second, once.
- **B3. Faces.** Every saved face becomes a normal managed picture with all four copies, wherever it came from (provider, Wikidata, the old direct download or an upload). Existing faces are adopted at the next Engine start (the D8 catch-up) or the first time they are shown.
- **B4. Character pictures** (the picture next to an actor's role) get their copies made the first time each size is asked for and kept beside the managed files; they have no artwork record, so no data store change.
- **B5. Keep rules.** Pictures are kept only by the viewer's own browser, never by a shared cache (they belong to signed-in people). A picture is kept for a year without asking only when its address carries the change marker that matches the picture now on disk and the exact size asked for was sent. Everything else is kept but checked on each use, which costs a tiny "not changed" answer and no picture bytes. Faces are always checked on each use (independent review, 2026-10-21): their address belongs to the person, and an uploaded or newly picked face does not always change it, so a year-long keep could show an old face. The "no picture" stand-in is never kept, so the real picture appears as soon as it exists. Before this work the Engine let any cache keep artwork for a day, so an edited cover could look unchanged for up to a day.
- **B6. Change marker.** A short code made from the original file's size and last-changed time, not from the data store's clock, because some refresh paths overwrite a picture file in place. The plain address without a size keeps no marker, because other parts of the Engine compare it as the picture's identity; screens always draw a sized address.
- **B7. Editor artwork library** (the administrator's picture store in the editor) answers a tiny request with its small copy; it has no tiny copy of its own.
- **B8. Screens.** Fixed-size pictures (faces, rows, thumbnails) ask for one copy and offer the next copy up to sharper screens (a 1x/2x/3x list). Screen-wide pictures (heroes, backgrounds) let the browser choose between medium and large by screen width (D2).
- **B9. Not yet marked.** About fifteen picture addresses are built by Engine lookups that know only the picture's id, not its file; they work and are re-checked on each use (no bytes when unchanged), but are not kept for a year. Adding the marker there means reading the file path in those lookups; listed as a follow-up.

## Plain English summary

Today the app already makes smaller copies of most pictures, but people's faces skip that system and the browser is told nothing about keeping pictures, so the People list downloads full-size originals for tiny circles. The plan adds a very small size for faces and thumbnails, makes sure every picture has all its sizes, tells the browser to keep pictures it has seen, and fixes each screen to ask for the size it actually draws. A test then stops new screens from forgetting. Result: faster pages, less data on phones and TVs, and no change to how pictures look.
