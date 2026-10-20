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
