---
title: "Storage Policy Review"
summary: "Review notes for storage ownership, media sidecars, generated assets, and provider cache boundaries."
audience: "developer"
category: "architecture"
product_area: "storage"
status: "internal"
---

# Storage Policy Review

## SQLite Execution Model

Tuvima uses asynchronous APIs for scheduling, cancellation, HTTP, and worker
coordination, but SQLite calls themselves are synchronous. Microsoft.Data.Sqlite
does not support asynchronous I/O; its ADO.NET async methods execute
synchronously. Repository APIs may remain `Task`-shaped so callers keep a stable
application contract, but implementations must:

- check the supplied cancellation token before each bounded SQLite operation;
- use synchronous Dapper and Microsoft.Data.Sqlite command methods;
- avoid `Task.Run` around database work;
- use `IDatabaseConnection.ExecuteWriteAsync` for writes that need a
  transaction. The method waits asynchronously for the global writer semaphore,
  then runs its SQLite callback synchronously and commits or rolls back before
  releasing the lock;
- split large `IN` queries with `SqliteBatching.MaxParametersPerQuery`; and
- batch related reads instead of issuing one query per item.

WAL mode remains the concurrency mechanism for simultaneous readers and the
single serialized writer. See Microsoft's
[SQLite async limitations](https://learn.microsoft.com/dotnet/standard/data/sqlite/async)
guidance.

## Goal

Unify Tuvima Library's managed asset storage so downloaded/provider artwork and derived artifacts stop cluttering media folders, while playback-critical sidecars remain where external players and managers expect them.

## Official Sources Reviewed

- Plex local TV assets: [support.plex.tv/articles/200220717-local-media-assets-tv-shows](https://support.plex.tv/articles/200220717-local-media-assets-tv-shows/)
- Plex movie organization: [support.plex.tv/articles/naming-and-organizing-your-movie-media-files](https://support.plex.tv/articles/naming-and-organizing-your-movie-media-files/)
- Plex local subtitles: [support.plex.tv/articles/200471133-adding-local-subtitles-to-your-media](https://support.plex.tv/articles/200471133-adding-local-subtitles-to-your-media/)
- Jellyfin movies: [jellyfin.org/docs/general/server/media/movies](https://jellyfin.org/docs/general/server/media/movies/)
- Jellyfin NFO metadata: [jellyfin.org/docs/general/server/metadata/nfo](https://jellyfin.org/docs/general/server/metadata/nfo/)
- Jellyfin books: [jellyfin.org/docs/general/server/media/books](https://jellyfin.org/docs/general/server/media/books/)
- Audiobookshelf docs: [audiobookshelf.org/docs](https://www.audiobookshelf.org/docs/)
- Audiobookshelf book scanner: [audiobookshelf.org/guides/book-scanner](https://www.audiobookshelf.org/guides/book-scanner/)
- Emby movie naming: [support.emby.media/support/articles/Movie-Naming.html](https://support.emby.media/support/articles/Movie-Naming.html)

## Review Matrix

| Manager | Media types | Requires local | Supports local | Cross-manager naming that matters | Tuvima-only assets that should stay central | Official basis |
| --- | --- | --- | --- | --- | --- |
| Plex | Movies, TV, music, photos, subtitles | Media files, external subtitles, optional local posters/fanart if the local-assets agents are enabled | Posters, fanart, banners, some extras, local subtitles | `poster.jpg`, `fanart.jpg`, `banner.jpg`, sidecar subtitles matching the media basename | Alternate artwork variants, generated hero art, thumbnails, transcripts, OCR, caches | Plex support articles for TV assets, movie naming, and local subtitles |
| Jellyfin | Movies, TV, books, music | Media files, subtitles, optional artwork and NFO sidecars | Posters, backdrops/fanart, logos, banners, NFO, subtitles | `poster.jpg`, `fanart.jpg`, `logo.png`, `banner.jpg`, `.nfo` | Alternate variants, hero renders, waveforms, chapter thumbs, subtitle caches | Jellyfin movie, NFO, and book docs |
| Emby | Movies, TV, music | Media files, subtitles, optional artwork | Posters, fanart, banners, logos, NFO | Same practical overlap as Plex/Jellyfin | Generated hero art, alternate variants, caches | Emby movie naming/local image guidance |
| Kodi | Video, music | Media files, subtitles, NFO, artwork | Wide support for local sidecars | `poster.jpg`, `fanart.jpg`, `.nfo` | Generated assets, caches, non-preferred variants | Practical overlap with Jellyfin/Emby-style local sidecars |
| Audiobookshelf | Audiobooks, podcasts, ebooks | Media files, preferred book cover if user manages it, metadata files such as `desc.txt`, `reader.txt`, `.opf` when user owns them | Covers, metadata files, some subtitles/transcripts depending on content type | `cover.jpg`, `desc.txt`, `reader.txt`, `.opf` | Provider-downloaded alternates, generated thumbs, transcripts cache | Audiobookshelf docs and book scanner guide |
| Komga | Comics, manga | Comic archives, `ComicInfo.xml` | Local cover art and metadata embedded in archives | `ComicInfo.xml` | Downloaded alternate covers, browsing thumbs | Ecosystem convention centered on comic-archive sidecars |
| Kavita | Books, comics | Source files and embedded/local metadata where present | Covers and metadata sidecars when present | `cover.jpg` is understood by users, but embedded metadata is more common | Generated thumbnails, OCR/transcript outputs | Ecosystem convention centered on embedded metadata first |
| Calibre | Books | Book files, `.opf`, user metadata sidecars | Cover images and metadata | `.opf`, `cover.jpg` | Downloaded alternates, browsing thumbs | Calibre-side metadata/cover convention |

## Manager Findings

### What managers actually require locally

- Media files themselves must remain in the library tree.
- Preferred external subtitles should remain beside the media file for playback interoperability.
- User-owned metadata sidecars such as `.nfo`, `.opf`, `ComicInfo.xml`, `desc.txt`, and `reader.txt` should stay local.
- Extras such as trailers, interviews, and theme media should stay local because those are part of the library organization, not Tuvima-managed caches.

### What managers merely support locally

- Posters, fanart/backgrounds, banners, and logos are generally optional local enhancements, not strict requirements.
- Local artwork names overlap heavily across Plex, Jellyfin, Emby, and Kodi, which makes them good candidates for an optional export profile rather than a mandatory storage location.
- Jellyfin explicitly supports only one image per artwork type in local metadata flows, which reinforces keeping alternate variants in Tuvima's central asset store instead of trying to mirror every variant locally.

### Cross-manager compatible naming

- Movies/series root: `poster.jpg`, `fanart.jpg`, `banner.jpg`, `logo.png`
- Season root: `poster.jpg`, `thumb.jpg`
- Media file subtitles: media basename plus subtitle extension, such as `Movie.en.srt`
- Book/comic metadata: `.opf`, `ComicInfo.xml`

## Tuvima Decision Inputs

### Store centrally by default

- Provider-downloaded artwork
- User-uploaded artwork from the editor
- Alternate artwork variants
- Hero banners
- Browsing thumbnails
- Chapter images
- Waveforms
- OCR/transcript outputs
- Non-preferred subtitle variants
- Any transformed cache copy

### Keep local by default

- Media files
- Preferred external subtitles/captions
- User-owned synced or plain lyrics already stored beside the media (`.lrc` or `.txt`)
- User-owned metadata sidecars
- Extras and theme media
- Cue/disc/chapter files the user manages

## Recommended Default

Use `Hybrid` storage mode:

- Central source of truth: `libraryRoot/.data/assets/`
- Local-by-default only for playback-facing or user-owned sidecars
- Artwork export off by default
- Preferred subtitle export on by default
- Metadata sidecar export off by default

## Media-Type Ownership Summary

### Movies

- Central owner: movie work
- Local: preferred subtitles, extras, theme media

### TV

- Series owns poster/background/banner/logo/square/hero
- Season owns season poster/thumb
- Episode owns stills only
- Episode subtitles remain local beside episodes

### Music

- Artist owns artist image/background/banner/logo
- Album owns cover/square
- Tracks do not duplicate album art
- User-owned lyrics/subtitles remain local and are indexed into managed storage; provider-downloaded lyrics stay central

### Audiobooks

- Book folder owns cover/square
- Author/person images stay central
- User metadata sidecars stay local

### Books / Comics

- Work/volume/issue owns cover
- `ComicInfo.xml` and `.opf` remain local
- Downloaded covers and generated thumbs stay central

### Podcasts

- Show owns cover and branding art
- Preferred captions/transcripts stay local when supported

## Result

The clean library tree wins by default, but not at the expense of Plex/Jellyfin/Audiobookshelf compatibility. Tuvima-managed artwork and derived artifacts live centrally, while subtitles and user-owned sidecars remain where playback tools actually expect them.
