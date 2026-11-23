# Native phone Listen: what the Engine already returns (WP8 check), 2026-11-23

Part of `engineering/specs/native-apps.md` WP8. Read from the code only; no Engine was run. The question left open in `native-android-phone-audit-2026-11-22.md` ("Connection points", item 5): can the Android phone build its Listen pages (songs, albums, playlists, artists, Favorites) from the Engine's ready-made lanes and shelves, or does it need new routes?

## Plain English answer for Shy

Mostly yes. Four of the five Listen pages need no new Engine work:

- **Albums and Artists**: the Engine already builds them as shelves of the Music page. The phone can page through them.
- **Songs**: a plain list of every song, page by page, is available.
- **Favorites**: the list of hearted songs, in the order the person set by hand, already exists. The phone has to match those song ids to the songs list itself, the same way the Dashboard does. The Engine's own "Favorite Songs" shelf is also there, but it is newest first and ignores the hand order, so it is only good for a preview row.
- **Playlists are the gap.** The phone door does not carry them at all: the Collections catalogue the phone can read leaves playlists out on purpose, and the phone cannot open a playlist's songs. The Dashboard reads playlists through routes the phone is never given. So a Playlists page on the phone needs a new, read-only connection point (a playlist list and one playlist's songs, in the person's saved order). This is a new Engine action, so it needs your yes (WP8 is scoped to fixtures and this check only).

One more catch for building: the phone door passes a short list of routes. The "View all" grid route and the "items by id" route are not on it, so the phone must use the shelves and browse routes below, not those two.

Decision for Shy:

1. Playlists on the phone in version 1: A. Leave Playlists out of the phone's Listen for now (recommended: Albums, Songs, Artists and Favorites are enough for a first release). B. Add a small read-only playlist connection point as its own packet.

## How the phone reaches these (what is on the door)

The phone talks to the Dashboard's app door, which forwards only an allow-list of routes (`src/MediaEngine.Web/Services/Integration/NativeApiForwardPolicy.cs`):

- `display/(home|browse|continue|recent|search|contributor-shelves)`, GET (`:70`)
- `display/shelves/{key}`, GET (`:71`)
- `display/groups/{id}` (`:72`), `details/.*` (`:76`), `collections` (catalogue, one, items) (`:79`)
- `profile-state/.*` read and write (`:117`), `player/...` (`:114`)
- Not on it: `display/grids/...`, `display/items` (a POST), `display/browse/libraries`, `/collections/managed`, `/collections/artist-detail-by-name`, `/collections/system-view-detail`.

## What the Engine returns, by Listen page

Request keys are query-string values on `GET /api/v1/display/...`. Lane, media type and grouping are matched without caring about letter case. A token needs `library.read` for all of these (`DisplayEndpoints.cs:61`, `:110`, `:498`).

### Songs (every song, paged)

`GET /api/v1/display/browse?lane=listen&mediaType=Music&offset=0&limit=48&includeCatalog=true`

- Code: `DisplayEndpoints.cs:63-110` calls `DisplayComposerService.BuildBrowseAsync` (`DisplayComposerService.cs:227`). With a media type given and no `grouping=home`, none of the prebuilt-lane shortcuts apply (`:251-269`), so it takes the generic path: filters to Music works (`:298-301`), sorts (`sort=title|oldest|creator|year`, default newest added, `SortBrowseWorks`), pages (`:458-461`) and builds one work card per song (`:467-473`).
- Result: songs in `catalog` (when `includeCatalog=true`, the default) or in `shelves[0]` with key `results` (when `includeCatalog=false`) (`:475-481`). Total in `totalCount`; `limit` is clamped to 1..200 (default 48). Page by `offset` (the response has no cursor on this route).
- Optional: `search=<text>` (title, artist, album, genre), `genres=`, `creator=` (an artist), `year=`, `status=favorite` (only hearted songs, `:370-373`).
- Each card carries the song's `workId` and `assetId`, and `flags.isFavorite` is set from the profile's hearts (`MarkFavorite`, `:468-470`).

### Albums

- First 20, with the Music home: `GET /api/v1/display/browse?lane=listen&mediaType=Music&grouping=home&includeCatalog=false`. Handled by `BuildMusicHomeAsync` (`DisplayComposerService.cs:818-866`, entry at `:261-269`). Shelf key `albums` (`:850`).
- All albums, paged: `GET /api/v1/display/shelves/albums?lane=listen&mediaType=Music&grouping=home&offset=0&limit=24` (shelves route `DisplayEndpoints.cs:469-499`, limit clamped to 100). For `albums` the shelf is read from the full album grid (`DisplayComposerService.Grids.cs:118-150`, grid defined at `DisplayGridCatalog.cs:90`), so it is not cut at 20. The response is a `DisplayShelfPageDto` with `nextCursor`, `offset`, `limit`, `hasMore`; send `cursor=<nextCursor>` (or `offset`) for the next page.
- An album card (`ToMusicAlbumCard`, `DisplayComposerService.cs:1097`): `groupingType: "album"`, `presentation: "album"`, `subject: "Album"`, square art, `id` = the album's root work id, `previewItems` = the album's tracks in track order (each with `workId`, `assetId`, `title`, `position` = track number). The "Play Album" action's `webUrl` is `/details/musicalbum/{id}?context=listen`; the app should open the album with `GET /api/v1/details/...` (on the door, `:76`).
- Related album shelves, same call with another key: `recently-played` (grid, `DisplayGridCatalog.cs:87`), `new-tracks-added` (`:89`).

### Artists

`GET /api/v1/display/shelves/artists?lane=listen&mediaType=Music&grouping=home&offset=0&limit=24`

- Artists are only a shelf of the Music home (`DisplayComposerService.cs:852-853`), built by `BuildMusicArtistCards` (`:1057`), not a grid. The shelf route re-builds the home with enough rows for the page and slices it (`:601-626`), so it works but gets slower for deep pages. No `mediaType`/`grouping` means no artists: the plain Listen lane (`BuildAlbumFirstListenShelves`, `:1002-1055`) has only continue-listening, recently-played, recently-updated-albums, recently-added-audiobooks and audiobook-series.
- Artist card (`ToMusicArtistCard`, `:1135`): `groupingType: "artist"`, `title` = the artist name, `subtitle` = "N tracks", action `openPerson` with `webUrl` `/details/person/{personId}?context=listen`, or `/search?q=<name>&type=person` when the artist has no person record yet. Open an artist with `GET /api/v1/details/...` for the person, or list the artist's songs with the Songs call plus `creator=<artist name>`.

### Favorites (hearted songs)

- The list and its order: `GET /api/v1/profile-state/saved`, keep the entries whose `entityKind` is Song (number 6; see the fixture notes in `tests/fixtures/native-client-v1/my-list-saved.json`). Source: `ProfileStateEndpoints.cs:23-36`. The order is the person's own (newest hearted first unless moved by hand); `position` carries it. This is exactly what the Dashboard does (`ListenPage.Loading.cs:333-345`).
- Showing them: join those ids with the Songs call (`workId`). A convenience for a preview row only: `GET /api/v1/display/shelves/favorite-songs?lane=listen&mediaType=Music&grouping=home` (shelf `DisplayComposerService.cs:846-847`); it is in library order, not the hand order.
- Heart or un-heart a song: `PUT` / `DELETE /api/v1/profile-state/saved/Song/{workId}` (`ProfileStateEndpoints.cs:140-194`; the route takes the kind name). Reorder: `POST /api/v1/profile-state/favorites/songs/{id}/move` (`:199-258`).
- My List never contains songs (`ProfileStateEndpoints.cs:71`, `:102`), so the My List page and Favorites never mix.

### Playlists (not available to the phone)

- `/api/v1/collections` leaves playlists out: `ShouldIncludeInManagementCatalog` returns false for a playlist, a playlist folder or a smart list (`CollectionCatalogReadService.cs:809-812`, `:846-849`), and `/api/v1/collections/{id}/items` answers 404 for them (`:347-349`, doc at `:325-330`).
- The Dashboard reads playlists through `/collections/managed` and friends (`CollectionEndpoints.cs:1376`), which are not on the phone door.
- So a phone Playlists page needs one new read-only connection point. Nothing is built for it in WP8 (scope decided: only fixtures and this check).

## Exact request keys for the Android phone

| Listen page | Request | Notes |
|---|---|---|
| Music home (first rows of Albums and Artists, preview Favorites) | `GET /api/v1/display/browse?lane=listen&mediaType=Music&grouping=home&includeCatalog=false` | Shelf keys: `recently-played`, `favorite-songs`, `new-tracks-added`, `albums`, `artists`. About 20 cards per shelf. |
| Albums, all | `GET /api/v1/display/shelves/albums?lane=listen&mediaType=Music&grouping=home&limit=24&cursor=` | Follow `nextCursor` while `hasMore`. |
| Artists, all | `GET /api/v1/display/shelves/artists?lane=listen&mediaType=Music&grouping=home&limit=24&cursor=` | Same paging. |
| Songs, all | `GET /api/v1/display/browse?lane=listen&mediaType=Music&limit=48&offset=0&sort=title` | `totalCount` for the end; `search=` narrows. |
| Favorites | `GET /api/v1/profile-state/saved` (Song entries) joined with the Songs list; hearts through `PUT`/`DELETE /api/v1/profile-state/saved/Song/{id}` | Order comes from the saved list, not the shelf. |
| Audiobooks | `GET /api/v1/display/shelves/recently-added-audiobooks?lane=listen&limit=24`, `continue-listening`, `audiobook-series` | Plain Listen lane shelves (`DisplayComposerService.cs:1043-1053`); or `browse?lane=listen&mediaType=Audiobook`. |
| Playlists | none today | See above. |

Do not use on the phone: `display/grids/...`, `display/items`, `/collections/managed` (all closed at the door).

## What this means for WP9

Listen pages 9b can be built from the table above with no Engine change, except Playlists. The wire shapes involved are `DisplayPageDto`, `DisplayShelfPageDto` and `DisplayCardDto` (already covered by the `universe.json` fixture), plus the My List and recent fixtures added in WP8. No fixture exists yet for a music home page or a shelf page; if the phone reads them, WP9 should add `display-music-home.json` and `display-shelf-page.json` in the same way.
