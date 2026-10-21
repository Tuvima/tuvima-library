---
title: "Canonical artwork architecture"
description: "How managed artwork identities, renditions and presentation purposes fit together in Tuvima Library."
audience: developer
category: architecture
product_area: artwork
status: current
---

# Canonical artwork architecture

## In this page

How managed artwork identities, renditions and presentation purposes fit together in Tuvima Library.

## Where this lives in the code

- `src/MediaEngine.Api/Services/ArtworkAssetService.cs`
- `src/MediaEngine.Api/Services/Display/DisplayArtworkUrlResolver.cs`
- `src/MediaEngine.Web/Components/Shared/AppMediaArtwork.razor`

Tuvima stores artwork as one managed image with many contextual uses.

## Durable model

- `image_cache` remains the low-level download/content cache. Its SHA-256 identity is reused by the artwork layer.
- `artwork_assets` is the canonical image identity. It owns the original managed path, S/M/L renditions, measured dimensions, aspect class, palette, perceptual hash, and source provenance. `content_hash` is unique.
- `entity_artwork_links` assigns an existing image to an entity and semantic role (`Primary`, `Background`, `Portrait`, or `Logo`). Preferred and user-override state belongs to this link, not to the image.
- `artwork_asset_context` is the maintained searchable projection for every import and link path. It records entity label/type, media type, year, role, provider, aliases, and canonical identifiers without treating a current use as the image's only meaning.
- `artwork_asset_search` is the FTS5 trigram index over that projection. Insert, update, and delete triggers keep it synchronized, while short searches use a bounded case-insensitive fallback.

Image shape is deliberately independent of semantic role. `aspect_class` remains `Portrait`, `Square`, `LandscapeWide`, `BannerStrip`, or `UnsupportedRect`.

Automatic structural artwork is a derived presentation. It remains a bounded ordered list of child artwork IDs and is never written to `artwork_assets`.

## Compatibility

The baseline upgrade step (`Schema/Upgrades/0001_baseline.sql`) creates the normalized artwork tables, the facet indexes and the FTS projection. Work, person, collection/universe, and fictional-entity search contexts are generated from canonical metadata and refreshed by the compatibility repository when their links change.

`EntityAssetRepository` is the temporary compatibility bridge for existing ingestion/provider workers: every artwork upsert now resolves a SHA-256 canonical asset and writes an entity link. The bridge keeps current Home, Read, Watch, Listen, Collections, details, and editor consumers remain shape-aware while those readers migrate to the link resolver. Linking an existing asset writes only a compatibility reference to the same managed paths; it never copies image bytes or regenerates renditions.

Collection path columns are compatibility projections of the preferred collection link. Removing a collection link clears the projection but keeps the canonical image and other entity links.

Physical cleanup treats every canonical original/rendition as referenced. Removing one link never deletes shared files; orphan retention remains the responsibility of the normal managed-asset cleanup policy.

## Provider artwork passes

TMDB and TheTVDB artwork passes record their outcome on the item, its work and its root as `{provider}_artwork_status`, `{provider}_artwork_held_count` and related diagnostics.

- **Held count:** the number of provider images the pass's scopes hold once it finishes, whether this pass downloaded them or found them already stored. Sibling episodes of one show therefore record the same values whichever finishes first. A repeat pass that skips the provider call reports what the earlier pass stored instead of `NoImages`.
- **Show-level root:** when the root is a separate show above the episode, it records only its own show-level count.
- **"Added" count:** the result's `DownloadedCount` still means "newly added by this pass". The Dashboard uses it to say how many images a refresh added.
- **Brand logos:** once stored, `network_logo_url` / `studio_logo_url` hold the stored copy's `/stream/artwork/{id}` address. A repeat pass maps that back to the copy's provider URL, so the logo still counts as held.
- **Repeat passes:** an ordinary TMDB pass skips the provider call only when the last pass got TMDB's artwork (`Completed` or `NoImages`) for the item's current `tmdb_id`. After re-identification, or after a pass that was skipped, failed or found no TMDB record (`NoResult`), it asks TMDB again. TheTVDB passes always ask again; a season id stored under a show's earlier identity is looked up again from the current show.
- **Poster choice:** when a pass actually chooses the owner's poster it records the TMDB or TheTVDB id it chose it for as `{provider}_artwork_poster_bridge_id` (a pass that skipped the provider call or failed records nothing). The Quick Hydration cover step keeps that poster, or a user's choice, as preferred while the item's current `tmdb_id` / `tvdb_id` still matches, and stores the retail cover as an alternative; so the result is the same whichever runs first. After a rematch or re-identification the ids differ and the retail cover takes over again, until the next images pass ranks the new identity's posters. An explicit retail rematch always replaces the poster.
- **Re-identification:** `{provider}_artwork_bridge_id` records the id the held artwork was found for (a skipped or failed pass leaves it unchanged). When it, or the poster choice, differs from the current id, the images pass removes the earlier identity's provider artwork once the new identity's artwork is stored: posters, backdrops, logos and season art the current identity did not offer, and the earlier retail cover. Uploads, user picks, artwork-library choices, local covers (embedded or folder art), brand logos and the current retail cover (read again just before removing) stay.
- **Removing a variant:** rematch and re-identification remove variants through `IEntityAssetRepository.DeleteVariantAsync`. The picture's artwork-library row goes with its last link, and only files no remaining row references are deleted, because artwork is content-addressed and another item can be served by the same file.
- **User choices:** choosing a variant in the artwork editor (`PUT /metadata/artwork/{variantId}/preferred`, `IEntityAssetRepository.SetUserChoiceAsync`) marks it `IsUserOverride`, like an upload. Later cover and images passes keep it preferred, including after re-identification. Choosing another variant releases an earlier pick of a provider image (never an artwork-library choice); an explicit retail rematch replaces picked provider variants (they belong to the earlier identity) but keeps uploads and artwork-library choices.

Before downloading, a pass takes the per-URL download lease, then re-reads the owner's stored artwork. A concurrent pass that already saved the same image is reused rather than stored twice.

## API and UI

- The user-facing library has one destination, **View > Artwork Library** at `/view/artwork`. Older `/view/artwork/media`, `/people`, `/universes`, and `/assets` bookmarks resolve to the same browser; they are not separate browse modes.
- Bounded asset search/picker: `GET /api/v1/display/artwork/assets`. `ArtworkAssetQuery` supplies search, repeated role/aspect/media/source/year facets, related and target entity context, role/slot, usage state, picker scope, dimensions, stable sort, and paging.
- Lazy entity variants and its ordered capability contract: `GET /api/v1/display/artwork/entities/{type}/{id}`.
- Link existing, upload, URL import, remove link, and rendition streaming use the sibling artwork endpoints.
- `ArtworkWorkspace` is the one entity editor. It renders capability-driven role cards, keeps role switching local, shows only variants for the focused canonical role/source slot, and owns metadata, zoom, upload, URL import, unlink, preferred state, refresh, and automatic fallback.
- `ArtworkAssetBrowser` is shared by the library and the near-viewport `ArtworkAssetPickerDialog`. Selection is by exact artwork asset ID. Recommended, Related, and All Artwork are explicit server query scopes; inspection alone never creates a link, and confirmation reuses the existing managed asset by reference.
- Artwork Library, picker, Workspace, and media-editor inspection adapt canonical artwork into the shared `MediaViewerShell`. The shell owns focus containment/return, fit/zoom/pan/fullscreen, navigation, download, and information presentation; artwork adapters expose entity/role/source context and never expose personal-photo lifecycle actions. Inspection remains non-mutating and does not copy files or create links.
- Canonical role names remain `Primary`, `Background`, `Portrait`, and `Logo`. `ArtworkRoleCatalog` defines supported entity/slot combinations and presentation keys; the Dashboard resolver supplies labels such as Poster / Cover, Still, Portrait, and Primary artwork without renaming persisted data.

## Picture sizes and browser keep

Every managed picture except logos has four copies beside its original, by long side: tiny (`t`, 96 px: faces, list thumbnails), small (`s`, 320: tiles, cards), medium (`m`, 960: details posters, phone heroes) and large (`l`, 2160: full-screen backgrounds, 4K TV). `ArtworkVariantHelper` writes them when a picture is saved; `entity_assets` stores their paths (`local_image_path_t/_s/_m/_l`). Faces are ordinary managed `Headshot` pictures (`PersonHeadshotAssets`), including faces from the older direct download, which are adopted on first use and by the start-up catch-up (`ArtworkRenditionRepairStartupService`). Character portraits get their copies on first request (`CharacterPortraitRenditions`).

`ArtworkRenditionService` is the one size resolver for the picture routes: exact copy if present; otherwise the next bigger copy now plus a background job (`ArtworkRenditionQueue`) to make the missing one; otherwise, when only the original exists, it makes the copies first (a few pictures at a time, one job per picture). `ArtworkFileResults` turns every picture file into a response: `private` always; `max-age=31536000, immutable` only when the address's `v=` matches `ArtworkVersionMarker.ForFile(original)` and the exact copy was sent; otherwise `no-cache` with `ETag` and `Last-Modified`; the placeholder is `no-store`. The marker comes from the original file's length and write time, so an edit or an in-place refresh gives the picture a new address. `ArtworkCanonicalHelper.StoredArtworkUrl` and `ApiImageUrls` put it on the addresses the Engine hands out; the plain unsized identity address stays marker-free.

In the Dashboard, `ArtworkSize` and `MediaTileArtworkUrl.ForDrawnSize` / `SrcSetForDrawnSize` / `SrcSetByLongSide` pick the copy for the size a picture is drawn (see the size table in `docs/product/presentation-rules.md`), and `PictureSizeGuardrailTests` fails the build when a screen draws an Engine picture without a size.

VIEW keeps Personal Space and Shared Library originals outside artwork enumeration. The artwork API reads only canonical managed artwork paths and applies normal display authorization before use. Grid requests are paged, use small renditions, preserve natural image geometry, and request larger bounded renditions only for the inspector/editor preview. Originals remain reserved for explicit full-size delivery.
