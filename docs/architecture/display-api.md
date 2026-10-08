---
title: "Cross-Platform Display API"
description: "Target-state display API notes for future clients that need common browse and card composition."
audience: "developer"
category: "architecture"
product_area: "api"
status: "target-state"
---

# Cross-Platform Display API

## In this page

Target-state display API notes for future clients that need common browse and card composition.

## Where this lives in the code

- `src/MediaEngine.Api/Endpoints/DisplayEndpoints.cs`
- `src/MediaEngine.Api/Services/Display`
- `src/MediaEngine.Contracts/Display`

The Engine owns consumer display composition for browse surfaces. Web, mobile, TV, and future native clients should call the display API for shelves, cards, artwork choices, compact facts, progress, and semantic actions.

## Current Contract

The stable consumer routes are versioned under `/api/v1/display`:

- `/home`
- `/home?profileId=...`
- `/browse`
- `/continue`
- `/search`
- `/groups/{groupId}`

Display responses use platform-neutral DTOs from `MediaEngine.Contracts.Display`. They intentionally avoid web-only concepts such as CSS classes. Clients render their own layout while using the same card facts, typed badges, artwork variants, progress state, and action targets.

`MediaEngine.Contracts` is the only non-frozen wire owner. API projection rows,
Domain models, and Dashboard presentation models must not become alternate
serialized definitions of display cards, shelves, previews, groups, people, or
collections. The API maps internal projection results explicitly into Contracts
types. The Dashboard may map a Contracts DTO into a presentation wrapper for
selection, formatting, or component state, but that wrapper is not a JSON DTO.

## Composition Boundaries

`DisplayComposerService` is the page and shelf orchestration layer. It should decide which shelves exist and how they are ordered.

Home composition is placement-aware: the Collections & Lists shelf comes from accessible collection placements at `location=home`, filtered by the active profile when `profileId` is supplied. The composer must not synthesize broad series/franchise cards for Home.

Lane grouping is item-count aware. TV show cards can represent a show with one or more owned episodes, but non-TV/non-music series cards require at least two distinct owned works by default. Operators can tune this with `config/ui/library-preferences.json` under `lane_group_display.*.minimum_series_items`. Grouped TV show cards and Watch landing heroes use root show artwork only; when it is unavailable they render the settled placeholder instead of borrowing an episode still.

TV Continue cards are episode cards, not substituted show cards. They retain the episode work/asset and managed still, send `Watch Sx Ey` or `Resume Sx Ey` to the player resolver, and expose the show-scoped episode detail route as a separate Details action. Episode detail composition keeps the episode still, short synopsis, genre, and season-list artwork; root show composition reports owned episode count and targets the in-progress or earliest owned episode.

Ordered series cards use `DisplayCardDto.PreviewItems` and `PreviewTotalCount` to expose a bounded representative set plus the full owned count. For sequences larger than four entries, the display projection selects the first, second, penultimate, and final available artworks while preserving their source positions. Each preview retains its media type, semantic web route, description, and media-specific facts for detail and hero contexts, but card hover does not navigate to individual children. Broader collections select up to four unique representatives with media-type breadth before filling from stable collection order. `DisplayGroupSummaryDto` carries container-level owned/known totals, media counts, progress counts, sequence range, and relationship rationale so the Dashboard can show useful context without inferring it from or repeating preview titles. TV show cards may still carry owned episode previews for show context, while Dashboard card hover preserves the show identity and uses show-level cinematic artwork. Group-card previews are static side-by-side strips: no artwork rotation, carousel controls, or child-level card actions are supported.

Person-backed groups preserve the complete canonical role list through
`person_roles`/`roles`; clients must not reconstruct roles from subtitles or a
single primary role. Managed collection contracts likewise retain the complete
management, placement, rule, permission, artwork, status, and person identity
fields that were previously split across API and Dashboard mirrors.

`DisplayProjectionRepository` owns database reads for display projections. It should keep SQL and visibility filtering out of page composition.

`DisplayCardBuilder` owns card, fact, badge, artwork, progress, and action construction. Hero facts should flow from the source card so spotlight and tile metadata stay consistent.

### Detail composition

`DetailComposerService` is the stable public facade for unified detail responses.
It dispatches to the internal detail composition pipeline without owning SQL,
entity-specific branching, or presentation rules.

The internal structure follows the same separation used by display composition:

- `DetailProjectionReader` owns focused database and repository projection reads.
- `DetailViewModelBuilder` builds reusable detail response fragments from loaded data.
- `DetailPresentationPolicy` owns route parsing and artwork-presentation decisions.
- `DetailCompositionOrchestrator.*` components coordinate work, collection, person,
  character, sequence, and Universe detail behavior in bounded feature files.

Universe and sequence composition remain isolated components of the same pipeline;
they are not alternate wire contracts or Dashboard-side inference paths. New detail
SQL belongs in the projection reader/component reader layer, reusable response
construction belongs in a builder, and branching presentation rules belong in a
policy rather than the public facade.

## Payload Size

Some page responses include the same cards in shelves and in `catalog`. Web uses `catalog` for browse grids, so it remains included by default for compatibility.

Clients that only need shelves can request smaller payloads with:

```text
includeCatalog=false
```

Example:

```text
GET /api/v1/display/browse?lane=watch&includeCatalog=false
```

This keeps the default web behavior stable while giving TV/native clients a way to avoid duplicated card payloads.

## Pagination

Browse endpoints already support `offset` and `limit` for catalog-style result pages. Lane pages currently return full shelf groups because that is simple for the web dashboard and current local-library sizes.

If TV or native clients need independent shelf paging, add shelf-level cursor routes instead of overloading page responses:

```text
GET /api/v1/display/shelves/{shelfKey}?cursor=...
```

The shelf route should reuse `DisplayCardDto` and the same `DisplayCardBuilder` rules so pagination does not fork display behavior.

## Active Non-Display Card Surfaces

The Universe card components, including `PosterSwimlane`, `PosterItemViewModel`, `LibraryCard`, `SquareCard`, and `LandscapeCard`, are still active in detail and related-content areas. They are not stale code.

Future cleanup can migrate related-content shelves to `MediaTile` or a display-card adapter, but detail and operational APIs should stay separate from display shelf APIs.

Universe and Chronicle HTTP responses use Contracts like every other API area.
The only frozen boundary exception is the exact Dashboard-local SignalR pair
`LoreDeltaDiscoveredEvent` and `UniverseEnrichmentProgressEvent`; it does not
apply to display or detail HTTP DTOs and must not be expanded.

