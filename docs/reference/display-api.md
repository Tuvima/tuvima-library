---
title: "Consumer Display API"
description: "Target-state reference for display composition endpoints intended for future consumer clients."
audience: "developer"
category: "reference"
product_area: "api"
status: "target-state"
---

# Consumer Display API

## In this page

Target-state reference for display composition endpoints intended for future consumer clients.

## Where this lives in the code

- `src/MediaEngine.Api/Endpoints/DisplayEndpoints.cs`
- `src/MediaEngine.Api/Services/Display`
- `src/MediaEngine.Contracts/Display`

The Engine owns consumer display composition for Home, Watch, Read, Listen, music landing pages, browse results, continue surfaces, search results, and grouped shelves. Web, iOS, Android, Roku, and TV clients should consume this API for browsing cards instead of reimplementing media-specific display rules.

## Endpoints

- `GET /api/v1/display/home?profileId=...`
- `GET /api/v1/display/browse?lane=watch&mediaType=Movie&grouping=all`
- `GET /api/v1/display/continue?lane=read`
- `GET /api/v1/display/search?q=dune`
- `GET /api/v1/display/shelves/{shelfKey}?lane=listen&mediaType=Music&grouping=home&cursor=...`
- `GET /api/v1/display/groups/{groupId}`

## Payload Rules

- Page-style endpoints return `DisplayPageDto`.
- Shelf pagination returns `DisplayShelfPageDto` with `nextCursor` and `hasMore`.
- Browse/search endpoints return cards in `catalog` by default to avoid duplicating the same cards in a `results` shelf.
- Clients that need a shelf-shaped browse response can pass `includeCatalog=false`.
- Home composition is shelf-driven: Jump Back In, Watch, Read, Listen, Collections & Lists, then New in your library when each shelf has real data. The final New shelf is filled only with structural work/group identities not already placed in the preceding Home shelves and is omitted when it would be entirely repetitive.
- Home collections come from accessible collection placements at `location=home`; clients should not synthesize top-level series/franchise cards.
- Detail/edit/playback APIs remain separate; display cards carry compact facts and semantic action targets, not full detail payloads.

## DTO Semantics

- `DisplayPageDto`: a client-neutral page with optional hero, shelves, and/or catalog.
- `DisplayShelfDto`: a named row of `DisplayCardDto` cards plus an optional see-all route.
- `DisplayHeroDto`: spotlight identity, artwork, actions, progress, display facts, genres, presentation, and optional ordered preview items copied from the source card. Album previews let compact Listen heroes show track context without turning tracks into landing cards.
- Listen landing composition is album-first. `continue-listening`, `new-tracks-added`, and `albums` return album cards for Music; Audiobooks remain individual or series cards. `new-tracks-added` describes newly arrived tracks on each album card rather than returning one card per track.
- `DisplayCardDto`: compact card identity, title, facts, typed badges, optional ordered preview items, artwork, progress, flags, and semantic actions.
- `DisplayCardBadgeDto`: typed badge metadata such as `quality` and `source`; badges are omitted unless source data exists.
- `DisplayCardPreviewItemDto`: ordered owned-child preview metadata for series and collection cards, including work/asset identity, title, managed image, shape, optional position, media type, and child web route. Clients should preserve sequence order, show positions when present, and use `previewTotalCount` for owned counts or overflow.
- `DisplayArtworkDto`: platform-neutral artwork variants and dimensions.
- `DisplayProgressDto`: progress percent, display label, last access timestamp, and resume action.
- `DisplayActionDto`: semantic action type such as `openWork`, `openCollection`, `playAsset`, or `readAsset`, plus IDs and optional web fallback URL.

## Client Responsibilities

Clients own layout and interaction style. Web can render hover cards, mobile can render long-press previews, and TV clients can render focus cards. All clients should use the same facts, artwork choices, progress, and action targets from the Engine.

