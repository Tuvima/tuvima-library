---
title: "Hybrid Asset Storage ADR"
description: "Architecture decision record for owned files, generated assets, provider cache data, and local storage policy."
audience: "developer"
category: "architecture"
product_area: "storage"
---
## Status

Accepted

## Decision

Tuvima Library uses a configurable storage policy with `Hybrid` as the default mode.

### Default behavior

- Store manager-owned artwork and derived artifacts centrally under `libraryRoot/.data/assets/`
- Keep playback-critical sidecars local
- Keep preferred subtitles local by default
- Do not export downloaded/provider artwork into media folders unless explicitly enabled

## Why

- Media folders were becoming noisy with provider posters, banners, logos, and generated images.
- Plex/Jellyfin/Emby/Kodi compatibility matters, but those systems generally support local artwork rather than requiring it.
- Subtitles are different: external players and managers commonly expect them beside the media file.
- Tuvima-specific assets such as hero art, browsing thumbnails, waveforms, chapter images, transcript caches, and alternate variants should not pollute the library tree.

## External Reference Basis

- Plex local assets and subtitles are documented as local-library conventions, not mandatory canonical storage.
- Jellyfin documents local artwork and `.nfo` support, and its NFO guidance explicitly treats one image per artwork type as the local interchange shape.
- Audiobookshelf's scanner explicitly looks for `cover`, `desc.txt`, `reader.txt`, and `.opf` in the item folder.
- Emby's movie naming guidance follows the same sidecar-style local artwork conventions as Plex and Jellyfin.

## Storage Model

### Central canonical store

- `.data/assets/artwork/{ownerKind}/{ownerId}/{assetType}/{variantId}.{ext}`
- `.data/assets/derived/{ownerKind}/{ownerId}/{artifactType}/...`
- `.data/assets/metadata/{ownerKind}/{ownerId}/...`
- `.data/assets/transcripts/{assetId}/...`
- `.data/assets/subtitle-cache/{assetId}/...`
- `.data/assets/people/{personId}/...`

### Local-by-default classes

- Media files
- Preferred subtitles/captions
- Preferred synced lyrics
- User-owned metadata sidecars
- Extras and theme media
- Cue/disc/chapter files

## Export Policy

The first compatibility profile is `plex-jellyfin-common`.

Default export settings in `Hybrid` mode:

- Artwork export: off
- Subtitle export: on for preferred subtitles
- Metadata sidecar export: off

Local exports are mirrors only. The central asset store remains the source of truth.

## Ownership Rules

- Movies: work owns artwork
- TV: series owns series art, season owns season art, episode owns stills only
- Music: artist owns artist art, album owns album art, track does not own shared artwork
- Audiobooks: book owns cover/square, person imagery stays central
- Books/comics: work/volume/issue owns cover; metadata sidecars remain local
- Podcasts: show owns branding art

## Consequences

### Positive

- Media folders stay much cleaner.
- Tuvima can safely store multiple artwork variants without colliding with sidecar naming.
- Streams and editor uploads resolve through stable asset records instead of guessed file paths.

### Negative

- Startup reconciliation can migrate DB-referenced legacy image files into the central store, after which runtime reads use only `.data/assets` and database references.
- Optional compatibility exports must be maintained separately from the canonical asset store.

## Implementation Notes

- `AssetPathService` is the policy authority for managed asset paths.
- `entity_assets` records track storage location and export state.
- DB-referenced legacy image files and local managed artwork are reconciled into `.data/assets` at startup; runtime fallback reads from legacy paths are not allowed.

## Transactional Writes

Transactional writes to the SQLite store go through `IDatabaseConnection.ExecuteWriteAsync` (`src/MediaEngine.Storage/Contracts/IDatabaseConnection.cs`), which waits asynchronously for the global write-serialization lock, opens a fresh pooled connection, begins the transaction, runs the synchronous SQLite callback, commits or rolls back, and always releases the lock in a `finally` block. Calling `SqliteConnection.BeginTransaction()` directly anywhere outside `src/MediaEngine.Storage/DatabaseConnection.cs` is banned by a guardrail test (`tests/MediaEngine.Api.Tests/DatabaseConnectionGuardrailTests.cs`).
