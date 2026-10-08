---
title: "Canonical artwork architecture"
description: "How Tuvima Library stores artwork as one managed image with many contextual uses."
audience: "developer"
category: "architecture"
product_area: "artwork"
---
Tuvima stores artwork as one managed image with many contextual uses.

## Durable model

- `image_cache` remains the low-level download/content cache. Its SHA-256 identity is reused by the artwork layer.
- `artwork_assets` is the canonical image identity. It owns the original managed path, S/M/L renditions, measured dimensions, aspect class, palette, perceptual hash, and source provenance. `content_hash` is unique.
- `entity_artwork_links` assigns an existing image to an entity and semantic role (`Primary`, `Background`, `Portrait`, or `Logo`). Preferred and user-override state belongs to this link, not to the image.
- `artwork_asset_context` is the maintained searchable projection for every import and link path. It records entity label/type, media type, year, role, provider, aliases, and canonical identifiers without treating a current use as the image's only meaning.
- `artwork_asset_search` is the FTS5 trigram index over that projection. Insert, update, and delete triggers keep it synchronized, while short searches use a bounded case-insensitive fallback.

Image shape is deliberately independent of semantic role. `aspect_class` remains `Portrait`, `Square`, `LandscapeWide`, `BannerStrip`, or `UnsupportedRect`.

Automatic structural artwork is a derived presentation. It remains a bounded ordered list of child artwork IDs and is never written to `artwork_assets`.

## Compatibility and migration

Startup migration `007_canonical_artwork_assets` creates the normalized tables, the facet indexes and FTS projection, then projects every legacy `entity_assets` row into them. Rows backed by `image_cache` converge on the cache SHA-256; legacy rows without a known content hash receive a stable migration identity. Work, person, collection/universe, and fictional-entity search contexts are generated from canonical metadata and refreshed by the compatibility repository when their links change.

`EntityAssetRepository` is the temporary compatibility bridge for existing ingestion/provider workers: every artwork upsert now resolves a SHA-256 canonical asset and writes an entity link. Legacy rows continue to be projected so current Home, Read, Watch, Listen, Collections, details, and editor consumers remain shape-aware while those readers migrate to the link resolver. Linking an existing asset writes only a compatibility reference to the same managed paths; it never copies image bytes or regenerates renditions.

Collection path columns are compatibility projections of the preferred collection link. Removing a collection link clears the projection but keeps the canonical image and other entity links.

Physical cleanup treats every canonical original/rendition as referenced. Removing one link never deletes shared files; orphan retention remains the responsibility of the normal managed-asset cleanup policy.

## API and UI

- The user-facing library has one destination, **View > Artwork Library** at `/view/artwork`. Older `/view/artwork/media`, `/people`, `/universes`, and `/assets` bookmarks resolve to the same browser; they are not separate browse modes.
- Bounded asset search/picker: `GET /api/v1/display/artwork/assets`. `ArtworkAssetQuery` supplies search, repeated role/aspect/media/source/year facets, related and target entity context, role/slot, usage state, picker scope, dimensions, stable sort, and paging.
- Lazy entity variants and its ordered capability contract: `GET /api/v1/display/artwork/entities/{type}/{id}`.
- Link existing, upload, URL import, remove link, and rendition streaming use the sibling artwork endpoints.
- `ArtworkWorkspace` is the one entity editor. It renders capability-driven role cards, keeps role switching local, shows only variants for the focused canonical role/source slot, and owns metadata, zoom, upload, URL import, unlink, preferred state, refresh, and automatic fallback.
- `ArtworkAssetBrowser` is shared by the library and the near-viewport `ArtworkAssetPickerDialog`. Selection is by exact artwork asset ID. Recommended, Related, and All Artwork are explicit server query scopes; inspection alone never creates a link, and confirmation reuses the existing managed asset by reference.
- Artwork Library, picker, Workspace, and media-editor inspection adapt canonical artwork into the shared `MediaViewerShell`. The shell owns focus containment/return, fit/zoom/pan/fullscreen, navigation, download, and information presentation; artwork adapters expose entity/role/source context and never expose personal-photo lifecycle actions. Inspection remains non-mutating and does not copy files or create links.
- Canonical role names remain `Primary`, `Background`, `Portrait`, and `Logo`. `ArtworkRoleCatalog` defines supported entity/slot combinations and presentation keys; the Dashboard resolver supplies labels such as Poster / Cover, Still, Portrait, and Primary artwork without renaming persisted data.

VIEW keeps Personal Space and Shared Library originals outside artwork enumeration. The artwork API reads only canonical managed artwork paths and applies normal display authorization before use. Grid requests are paged, use small renditions, preserve natural image geometry, and request larger bounded renditions only for the inspector/editor preview. Originals remain reserved for explicit full-size delivery.
