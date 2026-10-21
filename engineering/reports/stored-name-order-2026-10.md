---
title: "Which stored name wins when a field accepts several"
summary: "Where a display field reads one of several stored names, the data store picks the alphabetically first name present rather than the first one written. This report lists every such read, which ones show a person something different, and a recommendation for each."
audience: "product-owner, developer"
category: "report"
product_area: "engine"
---

# Which stored name wins when a field accepts several

October 20, 2026. Investigation only: no application code, configuration or data was changed, and no Engine was run. Read on `main` at `1b6cb469a`. Follows section 7 of the Home and detail speed spec (`engineering/specs/archive/home-and-detail-speed.md`, decision D1), which kept today's results on purpose.

## Summary

Many Engine reads fill one field from a list of stored names, for example a cover from `cover_url`, `cover`, `poster_url` or `poster`. The SQL is written with the preferred name first, but `SELECT value … WHERE entity_id = X AND key IN (…) LIMIT 1` returns the name that comes first **alphabetically** among those present, because the data store walks the `(entity_id, key)` primary key in order. The new one-pass reader (`DisplayWorkProjectionReader`) sorts names alphabetically on purpose so it gives the same answers.

Out of about 120 such reads, almost all have no visible effect: either only one of the names is ever written, or the names that coexist always hold the same value. **One pair matters a lot and two matter a little:**

1. **`cover` (the provider's web link) beats `cover_url` (the Engine's own copy).** Every matched movie, show, album, book and comic stores both. Alphabetically `cover` wins. Where a reader rewrites provider links to an Engine address (Home, library, detail lists), the person still sees the right picture, but at full size with no small or medium version, at an address that does not change when the cover changes. Where a reader does not rewrite it, **the browser fetches the poster straight from TMDB, Apple or Comic Vine**: the TV show page's poster, the profile overview rails, the Needs Review list and the browse group grids. The written order (Engine copy first) was clearly the intent, and it is what the item detail page already does.
2. **`series` (from the file or folder name) beats `show_name` (the provider's show title)** in the profile overview subtitle, for a TV show with no showrunner credit.
3. **`duration` (Wikidata or Apple) beats `runtime` (TMDB)** in the series and sequence rail on a detail page, where a movie has both.

Recommendation: switch the cover pair to the written order everywhere and stop provider links reaching the browser; switch the two small ones for consistency; keep everything else as it is (no visible effect). A plan is at the end.

## How the choice works today

- `key IN (…) LIMIT 1` with no `ORDER BY` returns the first matching row in primary-key order, which is alphabetical by name for one entity. The written order is ignored.
- `canonical_value_arrays … key IN (…) ORDER BY ordinal LIMIT 1` sorts by position; when two names both have a value at position 0, the alphabetical name wins the tie.
- Some reads pin the order on purpose (`ORDER BY CASE key …`, or a C# chain such as `GetValue(values, "cover_url") ?? GetValue(values, "cover")`). These follow the written order and are not affected.
- Two related patterns choose by something other than the name; see "Related patterns" below.

Each read tries entities in a fixed order (file, then item, then show or album, and so on). That order is always respected; the alphabetical rule only applies among the names on **one** entity.

## Which names are actually written

What decides whether the order matters is whether two names on the list can sit on the same record with different values. From the writers (`ArtworkCanonicalHelper`, `CoverArtWorker`, the file processors, `config/providers/*.json`) and the scope rules (`ClaimScopeCatalog`, `ScoringHelper.PersistAndScoreWithLineageAsync`):

| Names | Written by | Same record? | Same value? |
|---|---|---|---|
| `cover` and `cover_url` | `cover`: the matched provider's poster link (TMDB, Apple, Comic Vine via `CoverArtWorker`). `cover_url`: the Engine's managed copy, `/stream/artwork/{id}` (`ArtworkCanonicalHelper`); MusicBrainz also claims `cover_url` as a Cover Art Archive link | **Yes**, both on the artwork owner (show, movie, album, book or issue) | **No**: a web link versus an Engine address |
| `cover_url_s/_m/_l` | Engine copy only | — | — |
| `background` and `background_url`, `logo` and `logo_url`, `episode_still` and `episode_still_url`, `network_logo` and `network_logo_url` | Written together by `ArtworkCanonicalHelper.CreatePreferredAssetCanonicals` | Yes | **Yes**, identical |
| `poster`, `poster_url(_s/_m/_l)`, `still`, `still_url(_*)`, `hero`, `hero_url(_*)`, `banner`, `banner_url(_*)`, `square`, `square_url(_*)`, `clear_logo(_url)`, `broadcaster_logo_url` | Nothing found | — | — |
| `content_rating` and `certification` | `content_rating` by TMDB; `certification` by nothing found | — | — |
| `publisher` and `imprint` | `imprint` by nothing found | — | — |
| `network`, `studio`, `broadcaster`, `streaming_service`, `platform` | `network`, `studio` (TMDB, Wikidata); the rest by nothing found | Yes (`network` and `studio`) | No, but alphabetical order (`network` before `studio`) matches the written order |
| `source_service`, `source_platform` | Nothing found | — | — |
| `quality`, `video_quality`, `resolution`, `video_resolution`, `video_resolution_label`, `audio_quality` | Nothing found that stores them as item values | — | — |
| `artist` and `album_artist` | Music tags and MusicBrainz | **No**: the Engine files the track artist on the track and the album artist on the album (`ClaimScopeCatalog`, Music) | — |
| `show_name` and `series` | Both from the TV file or folder name (`VideoProcessor`, same value); `show_name` also from TheTVDB | **Yes**, both on the show | Usually the same; **different** when TheTVDB supplies the show title and the folder name differs |
| `runtime` and `duration` | `runtime` by TMDB; `duration` by Apple and Wikidata (P2047) | **Yes** for a movie (both land on the movie's own item) | Usually the same number; can differ, and the Wikidata value's format was not checked |
| `issue_number` and `series_position` | Comic Vine (both) and file tags | Yes, on a comic issue | Expected to be the same number |
| `duration`, `duration_sec`, `duration_seconds`; `description` and `overview`; `author`, `creator`, `writer`; `episode_description` and `short_description`; `explicit` and `is_explicit` | Various | — | Alphabetical order already equals the written order |

## The two display readers named in the spec

`src/MediaEngine.Api/Services/Display/DisplayWorkProjectionReader.cs` (Home, the library list and detail lists) and `DisplayVisibleWorkReader.cs` (which works a profile may see). The TV skeleton read in the same file (`LoadTvEpisodeSkeletonAsync`) uses the same content-rating rule.

| Field (column) | Names as written | Today's pick | Differs? | What a person sees | Recommendation |
|---|---|---|---|---|---|
| `CoverUrl` | `cover_url, cover, poster_url, poster, episode_still_url, episode_still, still_url, still` | `cover`, then `cover_url` | **Yes** | The provider link is rewritten to `/stream/{file}/cover`, so the right picture shows. Written order would give `/stream/artwork/{id}`, which also offers small and medium sizes and changes address when the cover changes. Home cards mostly use the separate small cover column, so the difference shows mainly where the full cover is used | **Switch** (see recommendation 1) |
| `RootCoverUrl`, `CollectionCoverUrl` | `cover_url, cover, poster_url, poster` | `cover` | **Yes** for `RootCoverUrl` (the show or album); series records hold no `cover` that was found | As above | **Switch** with recommendation 1 |
| Cover, episode still, banner and background sizes (`…SmallUrl` and so on) | e.g. `cover_url_s, poster_url_s, episode_still_url_s, still_url_s` | `cover_url_s`, then `episode_still_url_s` | Only in second place, and `poster_url_s` is never written | Nothing | Keep |
| `EpisodeStillUrl` | `episode_still_url, still_url` | same | No | Nothing | Keep |
| `BannerUrl`, `BackgroundUrl`, `LogoUrl`, `SquareUrl`, root and series versions | e.g. `background_url, background, …` | `background` first | Yes in name only | Nothing: the pairs that exist hold identical values; the rest are never written | Keep |
| `ContentRating` (here, in `DisplayVisibleWorkReader` and in the TV skeleton) | `content_rating, certification` | `certification` | Yes in name only | Nothing: `certification` is never written, so the TMDB rating always shows and the profile limit is checked against it | Keep |
| `Publisher` | `publisher, imprint` | `imprint` | Yes in name only | Nothing: `imprint` is never written | Keep |
| `Network` | `network, studio, broadcaster, streaming_service, platform` | `broadcaster` first | Yes in name only | Nothing: only `network` and `studio` are written, and they come out in the written order | Keep |
| `Source` | `source_service, source_platform` | `source_platform` | Yes in name only | Nothing: neither name is written, so the field is always empty | Keep |
| `Quality` | `quality, video_quality, resolution, …` | `quality` | Only in second place | Nothing: no writer found | Keep |
| `Duration` | `duration, duration_sec, duration_seconds` | same | No | Nothing | Keep |
| `Author`, `Artist` (joined lists) | `author, creator, writer`; `artist, album_artist, performer` | names joined by position, ties by name | `album_artist` before `artist` at the same position | Nothing: track and album artist are on different records | Keep |
| `Description` | `episode_description, short_description` | pinned in written order (`TextInOrder`) | No | Nothing | Keep |
| `CoverWidthPx` and the other size columns | `cover_width_px, episode_still_width_px` and similar | same | No | Nothing | Keep |
| `SearchNetwork`, `CountryOfOrigin`, `Franchise` | search only | — | — | Not displayed | Keep |

## Other Engine reads with the same pattern

Found with a sweep of `src/` for `key IN (…)` with `LIMIT 1` (264 reads with two or more names; 45 more pin their order with `ORDER BY CASE` and are fine). Developer-only reads in `src/MediaEngine.Api/DevSupport/` are left out.

### The cover pair: where it reaches the screen

| Where a person sees it | Read | After the pick | Effect today |
|---|---|---|---|
| **TV show page poster** | `DetailCompositionOrchestrator.Collections.cs:1080`, used at `:337` | Not rewritten | The poster is loaded from TMDB's servers, not from the Engine (only when no episode is in progress; an in-progress episode shows its still instead) |
| **Profile overview rails** (Settings → Overview) | `ProfileOverviewReadService.cs:114–115`, `:188–189` (books, music, movies; TV uses `:112`/`:186`, which is pinned) | Not rewritten; the Dashboard passes web links through unchanged (`EngineImageProxyPath.ToBrowserUrl`) | Covers load from the provider, full size |
| **Needs Review list** | `ReviewQueueReadService.cs:142–143`, `:256–257` | Not rewritten | Matched movies, books and comics in review show the provider-hosted cover |
| **Browse group grids** (`/collections/system-view-detail`) | `CollectionBrowseReadService.cs:400–401` (also uses "largest value wins"; see below), used at `CollectionEndpoints.cs:1098` | Not rewritten | Covers load from the provider |
| Detail page child lists (episodes, tracks, books in a series) | `DetailCompositionOrchestrator.Readers.cs:119–126` | Rewritten to `/stream/{file}/cover` | Right picture; full size only |
| Series and sequence rail on a detail page | `DetailCompositionOrchestrator.SequencePlacement.cs:199–203` | Rewritten (`:278`) | Right picture; full size only |
| Continue rails | `DisplayJourneyProjectionReader.cs:254–264` (a join, not `LIMIT 1`: when both names exist the read keeps whichever joined row the grouping happens to keep) | Rewritten | Right picture; which address it gets is not fixed |
| Owned formats list, series and universe pages | `DetailCompositionOrchestrator.Contributors.cs:46–47`, `Collections.cs:93`, `Entities.cs:193` | Not rewritten | No effect expected: no `cover` was found on file, edition or series records |
| `ArtworkFallback` and work detail | `DetailCompositionOrchestrator.Readers.cs:717–719`; `Work.cs:86` | — | `Work.cs` already uses the written order (`cover_url` first) |

The written order is clearly the intent here:

- Every list, from the first version of these reads (`f6735cbf8`, April 2026) onward, is written `cover_url` first, `cover` second.
- The C# chains that build the item detail page (`DetailCompositionOrchestrator.Work.cs:86`, `Collections.cs:337`), the entity cover route (`StreamEndpoints.cs:191`) and `CollectionMediaLookupReadService.cs:83` (pinned with `ORDER BY CASE`) all prefer `cover_url`.
- `CoverArtWorker.FindCoverUrl` prefers `cover`, but only to find a link to **download**, which is the opposite job.
- `docs/explanation/privacy-local-first.md` says managed artwork uses configured local paths. A browser fetching posters from TMDB or Apple goes against that, and shows the provider which titles are on screen.

### Other name sets

| Names | Reads | Differs? | Effect | Recommendation |
|---|---|---|---|---|
| `show_name, series` | `ProfileOverviewReadService.cs:104`, `:183` (subtitle) | **Yes**, both can be on the show with different values | A TV show with no showrunner credit shows the folder-derived name (for example "Doctor Who 2005") as its subtitle instead of TheTVDB's title, when TheTVDB is on. The other `show_name`/`series` reads (`IngestionPresentationReadService`, `HierarchyAlignmentService`, `CollectionBrowseReadService`) pin their order | **Switch** (small) |
| `runtime, duration` | `DetailCompositionOrchestrator.SequencePlacement.cs:213–214` | **Yes**, for a movie with both | In a movie series rail, a movie's time can come from Wikidata instead of TMDB. The detail page's own list reads `runtime` first (`Readers.cs:102–105`), so the two can disagree | **Switch** (small) |
| `series_position, issue_number`; `series_position, ordinal` | `SequencePlacement.cs:180–181` | In name only | Comic Vine writes the same number to both; no `ordinal` item value was found | Keep |
| `artist, album_artist` (± `performer`) | `DetailCompositionOrchestrator.Readers.cs:108–110`, `DisplayJourneyProjectionReader.cs:71–73`, `MusicBrainzEnrichmentHostedService.cs:189–190`, `IngestionPresentationReadService.cs:559` | In name only | Track and album artist are filed on different records, so one record never holds both | Keep |
| `author, album_artist, artist, narrator` | `ProfileOverviewReadService.cs:103`, `:182` | Only between `album_artist` and `author` | Music and audiobook credits are filed on different records; no visible case found | Keep |
| `content_rating, certification` | `DisplayJourneyProjectionReader.cs` (Continue rails) | In name only | `certification` never written | Keep |
| `network, studio, broadcaster`; `network_logo_url, network_logo, studio_logo_url, broadcaster_logo_url` | `Collections.cs:93`, `:1080` (hero brand) | In name only | Unwritten names first; written ones come out in the written order, and logo pairs hold the same value | Keep |
| Background, banner, logo, hero, square, poster, still pairs | Most detail, browse and Continue reads | In name only | Identical values or never written | Keep |
| `quality, audio_quality`; `source_service, source_platform`; `edition_title, edition_label, release_title` | `Readers.cs:115–117`; Continue rails; `MediaEditorOwnedChildReadService.cs:411` | In name only | No writer found for any of these names | Keep |
| `artwork_accent_hex, cover_accent_hex, accent_color, dominant_color` | `CollectionBrowseReadService.cs` (one `LIMIT 1` read) | In name only | Among written names alphabetical equals written order | Keep |

## Related patterns (same family, different rule)

These do not pick by name, so they are outside the question asked, but they make the same kind of silent choice and would be fixed the same way:

- **"Largest value wins"** (`MAX(CASE WHEN key IN (…) THEN value END)`): `CollectionBrowseReadService.cs:248–436`, `PlayerService.cs:751–773`, `MediaEditorNavigationReadService.cs:365–368`, `DetailCompositionOrchestrator.MediaGroups.cs:68–69`. For the cover at `CollectionBrowseReadService.cs:401` a web link (`https…`) sorts above an Engine address (`/stream…`), so the provider link wins here too. For the colours (`:426–436`), `artwork_accent_hex` belongs to the most recently chosen artwork of any kind while `cover_accent_hex` belongs to the cover, so when they differ the larger hex code wins, which is arbitrary.
- **"Whichever row the grouping keeps"**: the Continue rails join every matching name and then group by file (`DisplayJourneyProjectionReader.cs:254–293`), so which name's value is used is not fixed.

## Outcome

Shy approved recommendations 1–3 on October 20, 2026. Built in the follow-up proposed change (branch `claude/stored-name-order-fix`):

- Every cover read listed above now takes `cover_url` before `cover` (`ORDER BY CASE key …` in the SQL reads; `ValueInOrder` for `CoverUrl`, `RootCoverUrl` and `CollectionCoverUrl` in the one-pass reader; one chosen row per entity in the Continue rails; one `MAX` per name, in order, in the browse grid read).
- Where a provider link still wins (no Engine copy yet), the TV show page, profile overview, Needs Review and browse grid swap it for an Engine route before it leaves the Engine. The profile overview and browse grid use the file's cover route (`/stream/{file}/cover`, which shows the saved copy or a placeholder, as Home does). Needs Review uses the work's cover route (`/stream/entity/work/{id}/cover`), which fetches the image through the Engine when no copy is saved yet, so candidates in review keep their artwork. The TV show page uses the show's managed copy or its cover route, and does this before an in-progress episode's still replaces the show cover.
- The profile overview subtitle takes `show_name` before `series`; the sequence rail takes `runtime` before `duration`.
- Tests cover the one-pass reader, the Continue rails, the profile overview and Needs Review. The browse grid, the TV show page swap and the sequence rail's time were checked by review only: each needs a whole browse or detail composition to test.
- Not changed: the colour "largest value wins" reads; an in-progress episode's still on the TV show page when that still is itself a provider link (no provider writing an episode-still link was found; they are written as Engine copies); and every field marked Keep.

## Recommendations

| # | Field | Recommendation | Product reason |
|---|---|---|---|
| 1 | Cover (`cover` vs `cover_url`) in every Engine read | **Switch to the written order**: the Engine's own copy first, the provider link last. Also make sure no provider link leaves the Engine for a browser (rewrite it to the Engine's route, as the display readers already do) | **Privacy** (no browser requests to TMDB, Apple or Comic Vine), **Performance** (small and medium sizes become usable; local images), **Reliability** (a changed cover gets a new address, and provider links can break), **Presentation** (cards, rails and detail pages agree) |
| 2 | Profile overview subtitle (`show_name` vs `series`) | **Switch** to the written order | **Presentation**: the provider's show title, not the folder name |
| 3 | Sequence rail time (`runtime` vs `duration`) | **Switch** to the written order | **Reliability**: the same movie shows the same time in the rail and on its own page |
| 4 | Every other field above | **Keep today's behaviour**: no visible effect | — |
| 5 | Related patterns ("largest value wins", grouping) | Fold the cover and colour cases into the same clean-up as recommendation 1; leave the rest | **Reliability**: one rule for choosing among names |

Recommendation 1 has one product question in it: the one-pass display reader was written to keep today's results, and its tests compare against the old read. Switching the cover there changes Home and library addresses (not pictures), so the comparison would need its expected results refreshed for that column.

Not recommended now: deleting the names nothing writes (`poster`, `hero`, `certification` and so on). They do no harm, a future provider might use them, and removing them is a separate tidy-up.

## What was not checked

- Nothing was run against a real library; which names coexist comes from reading the writers and the scope rules. A one-off look at a fully ingested library (counting records that hold both `cover` and `cover_url`, both `show_name` and `series`, both `runtime` and `duration`) would confirm the numbers.
- Whether a browser actually shows a stale cover after a cover change at the stable `/stream/{file}/cover` address (the Engine sends a one-day cache time) was not tested in the app.
- The format of Wikidata's `duration` value was not checked.
- Early in intake, before an item's place in its show or album is known, all of a file's values are stored on the file itself (`ScoringHelper`, no lineage). During that window `artist` and `album_artist` could sit on one record; this was not seen in practice.

## Addendum: the "largest value wins" reads

October 20, 2026. Investigation only, read on `main` at `02a8ef5fb` (after the cover fix above). No Engine was run. Covers the reads listed under "Related patterns" that pick among several stored names with `MAX(CASE WHEN key IN (…) THEN value END)`, which keeps the value that sorts last, whatever its name. The browse grid's cover was already fixed.

Three facts from the writers decide most of the answers:

- **People and publishers are lists.** `author`, `artist`, `album_artist`, `publisher` and the other credit names are multi-valued (`MetadataFieldConstants.MultiValuedKeys`). The Engine keeps them only in the list table (`canonical_value_arrays`); `CanonicalValueRepository.UpsertBatchAsync` drops them from the single-value table (`canonical_values`). A read that looks for them in the single-value table finds nothing, except on albums and series the editor itself created (see site 4).
- **The colour names come in three sets** (`ArtworkCanonicalHelper.CreatePreferredAssetCanonicals`). Saving any preferred picture (cover, background, logo, episode still and so on) writes that picture's own set (`cover_*_hex`, `background_*_hex`, …) **and** overwrites the shared set `artwork_*_hex` plus `dominant_color` (the same value as `artwork_accent_hex`) on the same record. `primary_color`, `secondary_color` and `accent_color` are never written as stored values.
- **Pictures and their colours sit on the picture's owner** (the show, movie, album, book or issue), not on the file. Only a cover saved before an item's place is known lands on the file, and then the shared and cover sets hold the same colours.

### Each site

| # | Site | Names | Can they sit on one record with different values? | What a person sees today | Recommendation |
|---|---|---|---|---|---|
| 1 | Browse colours, `CollectionBrowseReadService.cs:428–442` (the group pages reached from browse: a show, album or series by name) | `artwork_*_hex, cover_*_hex, primary_color` (+ `secondary`, `accent`, `dominant_color`) | **Yes** on the owner: the shared set and the cover set differ whenever the last picture saved was not the cover | The first part of each read takes the owner's values by name, alphabetically, so the shared set wins. The "largest value" part only reads the file, which normally holds none of these, or the same value under every name. So the larger-hex choice is not seen in practice; what is seen is whatever the shared set holds (finding A) | **Switch** the file part to a fixed order (shared, then cover, then the old names), the order `CollectionResponseFormatting.ResolvePalette` already uses. No visible change on its own. See recommendation 7 for what fixes the colours a person sees |
| 2 | Artist panel, `CollectionBrowseReadService.cs:248` (Listen → Artists → an artist; built at `CollectionEndpoints.cs:886–1010`) | List names `album_artist, artist, performer` from the track, its file and its album | **Yes**: the album artist is on the album, the track artists on the track and Wikidata performers alongside, and a track can have several artists | The panel's photo and its "View artist page" button come from looking up a person by the first track's largest name. On a track with a guest whose name sorts later ("Daft Punk" with "Pharrell Williams"), or on a compilation filed under "Various Artists", the panel can show someone else's photo and link, or no photo at all. The panel's title is right (it is the name that was clicked) | **Switch**: look the person up by the artist the panel is for; keep the picked name only as a fallback, chosen in a fixed order (album artist, then artist, then performer, in list order). Also replace the silent `catch { }` around that lookup (`:995`) with a logged warning, per project rules |
| 3 | Player, `PlayerService.cs:751–773` (year, content rating, quality of the item being played) | `year, release_year, premiere_year`; `content_rating, certification, rating_classification`; `quality, video_quality, resolution, video_resolution_label` | **No**: only `year` and `content_rating` are written. Wikidata's release date is stored as `original_release_year` or `edition_release_year`, not `release_year`; nothing writes the other names | Nothing | **Keep** |
| 4 | Editor's Matching suggestions, `MediaEditorNavigationReadService.cs:365–368` (in Editing Mode, the album or series you can move an item into) | `album_artist, artist`; `author, creator, publisher`; `year, series_start_year` | **No**, but for a different reason: the artist, author and publisher names are lists, so these reads almost never find them (see above). `year` (a show or album) and `series_start_year` (a comic series) are written on different kinds of record | Series suggestions show only a year, never the author, except for series the editor created. Album suggestions show no artist; and because the editor filters albums by the track's artist (`:313–316`, the Dashboard always sends it), **albums that came in through ingestion are never offered as local suggestions**, only albums an earlier editor move created. Those editor-made albums and series are the only records with a single-value `artist` or `author`, because the move writes them there directly (`HierarchyAlignmentService.cs:749`, `:775`, `:952–953`; `MusicTrackRelocationRepository.cs:101`) | **Switch**: read the artist and the author from the list table in a fixed order (album artist before artist; author before creator; publisher no longer offered as an author), as the neighbouring read at `:454` already does. Make the editor's moves store these as lists under the names ingestion uses (`album_artist` on an album, `author` on a series). Year: a fixed order, `year` then `series_start_year` |
| 5 | Audiobook chapters, `DetailCompositionOrchestrator.MediaGroups.cs:68–69` | `duration_seconds, duration_sec` | **No**: only `duration_sec` is stored (the audio and video processors). `duration_seconds` appears only inside track lists and in a lyrics lookup request, never as a stored value | Nothing | **Keep** |

The browse reads' other multi-name `MAX` lines (`background`, `banner`, `hero`, `logo`, and the `duration_seconds, duration_sec` pair at `:254` and `:394`) are the same: identical values or never written. Keep.

### Finding A: a title's colours follow whichever picture was saved last, and flip at every Engine start

`artwork_*_hex` is read as "the title's colours" by Home and the library list (`DisplayWorkProjectionReader.cs:616–656`), the detail page hero (`DetailViewModelBuilder.cs:26–40`) and the browse group pages (site 1). Because every preferred picture overwrites it, it holds the colours of whichever picture was saved last. The images pass saves backdrops, then logos, then posters (`ImageEnrichmentService.cs:542`), so straight after ingestion it is usually the poster's colours. Two things change that:

- Choosing a new background or logo in Editing Mode re-tints the whole title to that picture's colours.
- At every start, `ArtworkRenditionRepairStartupService` walks every preferred picture, newest first (`EntityAssetRepository.cs:634`), and rewrites any record whose values differ from that picture's. A title with a poster and a background therefore gets the poster's colours, then the background's, and keeps the colours of whichever picture is oldest. That is usually the background, and every start rewrites the values twice.

What a person sees: after an Engine restart, a show's or movie's tint on Home, in the library and on its detail hero comes from its backdrop or logo rather than its poster. Since the backdrop colours come from the image's left edge (`ArtworkVariantHelper.ExtractBackdropLeftEdgePalette`), this is often a dark, flat colour. Other code treats the shared set as the cover's: the cover pass clears it along with the cover (`CoverArtWorker.cs:856–864`). This was found by reading the code; it was not run.

### Recommendations

| # | Field | Recommendation | Product reason |
|---|---|---|---|
| 6 | Browse colours (site 1) | **Switch** the file part to a fixed order: shared set, cover set, old names | **Reliability**: one rule for choosing among names (recommendation 5) |
| 7 | Title colours (finding A) | **Fix the writer**: only a record's main picture writes the shared set (`artwork_*_hex`, `dominant_color`): the cover, a season's poster, an episode's still, a person's headshot, a character's portrait. Backgrounds, logos, network and studio logos and season thumbnails keep only their own set. Existing libraries correct themselves at the next Engine start (the repair rewrites the main picture's colours and no longer flips them) | **Presentation**: a title keeps its poster's colours everywhere; **Reliability**: the same colours after every restart, and no rewrite on every start |
| 8 | Artist panel (site 2) | **Switch**: find the person by the artist the panel is for; the picked name, in fixed order, is only a fallback; log instead of silently ignoring a failed lookup | **Presentation**: the right photo and the right "View artist page" link |
| 9 | Editor Matching suggestions (site 4) | **Switch** to the list table in a fixed order, and make the editor's moves store artists and authors as lists | **Reliability**: ingested albums can be chosen when moving a track; series show their author; editor-made and ingested records are stored the same way |
| 10 | Player, audiobook chapters, other browse names (sites 3 and 5) | **Keep** | No visible effect |

Recommendation 7 has one product question in it. "The title's colours" could mean the poster's colours (as above) or, as today, the colours of the picture you last chose. The first keeps cards, rails and pages steady; the second lets a newly chosen background re-tint the title. The recommendation is the poster's, since the backdrop already has its own colours for the hero (`background_*_hex`).

Not recommended now: the second silent `catch { }` in the same endpoints file (`CollectionEndpoints.cs:843`, album detail) and other reads that use the same "first person found" lookup (`:775`, `:1144`). They are outside these sites; worth a separate tidy-up.

### What was not checked

- Nothing was run. Which names coexist, and the colour flip at start-up, come from reading the writers, the scope rules and the startup repair.
- Whether any existing test library already holds single-value `artist` or `author` rows from older data was not checked; pre-release resets make this moot.
- The artist panel's behaviour depends on how multi-artist credits are split into the list (one entry per artist, or one joined entry); either way the picked name can be someone other than the artist the panel is for.

### Outcome of the addendum

Shy approved recommendations 6–9 on October 20, 2026, with titles always using their poster's colours. Built on branch `claude/competent-lichterman-531caa`:

- Title colours: only a main picture (cover, season poster, episode still, headshot, character portrait) writes `artwork_*_hex` and `dominant_color` (`ArtworkCanonicalHelper`). Backgrounds and logos keep only their own set, so neither choosing one nor the start-up repair re-tints a title. Existing libraries correct themselves at the next Engine start.
- Browse colours and the artist panel's picked name use a fixed order (`CollectionBrowseReadService`).
- The artist panel looks the person up by the artist it is for, with the picked name as a fallback, and logs a failed lookup.
- Matching suggestions read artists and authors from the list table (album artist before artist, author before creator, publisher no longer used as an author). The editor's moves and track relocation store album artists and series authors as lists. Renaming an album or series without changing its credit keeps the whole list and its Wikidata links.
- Follow-up, built afterwards on branch `claude/epic-northcutt-1bc834`: three readers took an album's or series' credit from the single-value table, where the Engine never stores it, so they found nothing for ingested or editor-made records.
  - The group detail's creator (`CollectionEndpoints.ResolveGroupCreatorAsync`) now reads an album's `album_artist`, then `artist`, and a series' `author` from the list table, first by ordinal. A series with no author falls back to its `creator`, which the data store classifies as a single value (`MetadataFieldConstants.IsMultiValued`), so it is read from `canonical_values`. Before this, the Listen album page showed no artist, and the missing-track fill searched Apple by album title alone.
  - The editor's Album section names the album's artist from the library detail (which reads the list table) instead of falling back to the album or track title.
  - The editor's artist-artwork owner lookup (`ResolveArtistArtworkOwnerAsync`) was computed and never used, so it was removed rather than fixed.
  - The combined artist view's person lookup now logs a failure instead of swallowing it.
  - Not changed: `creator` is single-valued in the data store although Wikidata can name several creators, so the editor navigator's `author`/`creator` list read can only ever find `author`. Whether `creator` should become a list is a separate decision.

### Remediation to address later: the combined artist view

Shy asked on October 20, 2026 for this to be recorded and fixed later, outside PR #356. Not started.

**Where:** `GET /collections/artist-group-detail?collection_ids=…` in `src/MediaEngine.Api/Endpoints/CollectionEndpoints.cs` (handler from about line 680). It builds one artist's albums as one view.

**What is wrong:** it reads album-level facts from the first visible track's single stored values (`GetCanonical(firstWorkDto, …)`, around lines 776–782) instead of from the album's root Work:
- Artist: `combinedCreator ??= GetCanonical(firstWorkDto, "artist") ?? GetCanonical(firstWorkDto, "author")`. Credits are list fields (`MetadataFieldConstants.IsMultiValued`) stored only in `canonical_value_arrays`, and on the album root, not the track. So this is always empty.
- Genre: `combinedGenre ??= GetCanonical(firstWorkDto, "genre")`. Genre is also a list field, so it has the same problem.
- Year (`release_year`, then `year`) and `child_entities_json` are also read from tracks. The single-album group detail reads them from the root Work (`ParentCv`), so those two reads should be checked too.

**What a person would see:** the view's title falls back to "Unknown Artist", with no artist name, photo, person link or genre. The person lookup (`personRepo.FindByNameAsync`, now logged after PR #356) never runs, because there is no name to look up.

**Who uses it today:** nothing found. No Dashboard page, native client, test or doc calls `artist-group-detail` (searched `src/`, `clients/`, `tests/`). Its only mention is the endpoint ledger (`engineering/plans/access-architecture-2026-09-08/execution/endpoint-registration-ledger.md`). The Dashboard's artist pages use `/collections/artist-detail-by-name`, which already reads credits correctly.

**Options for when it is picked up:**
1. **Fix it:** resolve each album's root Work (`ICollectionBrowseReadService.GetRootWorkIdAsync`) and take the artist from `CollectionEndpoints.ResolveGroupCreatorAsync(…, isMusic: true, …)`. Read genre from the root's `genre` list, and year and `child_entities_json` from the root's single values, as the group detail does. Add behaviour tests on a temporary data store, as in `tests/MediaEngine.Api.Tests/StoredCreditReadTests.cs`.
2. **Remove it:** if no screen is meant to use it, delete the action and its ledger row. This is less code to keep correct.

Recommendation: confirm whether any planned screen needs it, then choose. If nothing does, removing it is the long-term answer.

**Related, decided 2026-10-21 (Shy: yes):** `creator` is now a list (`MetadataFieldConstants.MultiValuedKeys`), so a series can hold several creators, as Wikidata does. The group detail header names all of them, in order, when the series has no author. Pre-release data needs a reset to pick this up (no migration).

**Outcome (2026-10-21, Shy: remove it):** the unused `GET /collections/artist-group-detail` action was removed, so its "Unknown Artist" result no longer exists. The Dashboard's artist pages (`/collections/artist-detail-by-name`) are unchanged.
