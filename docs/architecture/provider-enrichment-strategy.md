---
title: "Provider enrichment strategy"
description: "Understand provider responsibilities and the identity evidence used to enrich owned media."
audience: developer
category: architecture
product_area: providers
status: current
---

# Provider enrichment strategy

## In this page

Understand provider responsibilities and the identity evidence used to enrich owned media.

## Where this lives in the code

- `src/MediaEngine.Providers`
- `config/providers`
- `config/field_priorities.json`

## Purpose

Tuvima Library uses the provider best suited to each media type, then connects
the resulting records through Wikidata QIDs. Provider identifiers are evidence
for matching; a QID is the preferred cross-media identity for people, works,
series, franchises, and fictional universes. Wikipedia remains the preferred
source for long biographies and encyclopedic descriptions when a QID resolves
to a suitable article.

The Engine calls providers directly and stores downloaded artwork as managed
assets. It never exposes provider credentials or remote image URLs to the
Dashboard. User-selected artwork always wins over a later provider refresh.

## Five implementation decisions

### 1. Use one primary catalog per media type

**Before:** overlapping catalog providers could return different editions,
titles, dates, and artwork, making it difficult to explain why one value won.

**After:** Apple is the retail catalog for books and audiobooks, MusicBrainz is
the identity authority for music with Apple enrichment, TMDB owns movie and TV
catalog data, and Comic Vine owns comic issue/run data. Local embedded metadata
always participates in matching.

**Benefit:** fewer ambiguous candidates, clearer failure states, and provider
behavior that can be explained by media type.

Example: an M4B with an ISBN, author, narrator, and duration is matched to the
Apple audiobook result with the same edition evidence rather than mixed with
ebook or Audible-specific results.

### 2. Keep Wikidata as the cross-media identity spine

**Before:** provider IDs could identify an item inside one catalog without
connecting its author, adaptation, series, franchise, or universe elsewhere.

**After:** provider IDs and local identifiers are submitted as bridge evidence
to Wikidata reconciliation. Confirmed QIDs connect people and works across Read,
Watch, and Listen while retaining the provider ID that produced the match.

**Benefit:** provider-specific metadata stays useful without fragmenting one
person or creative universe into unrelated records.

Example: a novel, audiobook edition, film adaptation, and credited creator can
retain their Apple and TMDB identifiers while QIDs connect them on shelves and
relationship views.

### 3. Store provider artwork once in the managed asset pipeline

**Before:** Fanart.tv supplied additional video artwork, while some TMDB brand
images could remain remote URLs. This required another credential and another
ranking path.

**After:** TMDB supplies movie and TV posters, backdrops, logos, season art, and
episode stills. The Engine downloads selected variants, deduplicates them by
source URL, creates renditions and palettes, and serves them through managed
artwork endpoints. The generic `fanart.jpg` sidecar name remains supported as a
local background convention.

**Benefit:** one video provider, fewer credentials, consistent caching, and no
Dashboard dependency on a provider image host.

Example: refreshing a TV show can update its backdrop and logo from TMDB while
leaving an administrator-selected poster unchanged.

### 4. Treat embedded metadata as edition and issue evidence

**Before:** comics used only a small part of ComicInfo.xml, and book/audio
matching could lean too heavily on title text.

**After:** embedded ISBN, ASIN, Apple IDs, duration, language, narrator, comic
volume/run details, issue number, provider URLs, publisher, and creator roles
are retained as matching evidence. Historical external identifiers may remain
as bridge evidence even when Tuvima has no network adapter for that provider.

**Benefit:** better edition and issue selection without adding unreliable or
duplicative network catalogs.

Example: two comic runs with the same title can be separated by volume, year,
publisher, and issue number before Comic Vine enrichment begins.

### 5. Make credentials explicit and server-side

**Before:** provider setup could imply that every enabled integration required
the user to create a key, and local software could be mistaken for a place where
a bundled key can be kept secret.

**After:** no-key providers work immediately. Required credentials are loaded by
the Engine from its secret configuration and are redacted from API responses.
TMDB can use the Tuvima application credential provisioned for the installation;
an administrator may supply an override where supported. Comic Vine and
SubDL remain optional administrator-configured integrations.

**Benefit:** setup shows the real requirement for each provider and secrets
never travel to the browser. A key shipped inside a local binary is not treated
as secret; release provisioning must inject it into server-side configuration
or use a separately operated credential service.

Example: the current installation continues to use its existing TMDB secret,
while SubDL remains disconnected until an administrator supplies a personal
API key.

## Media-type behavior

| Media type | Primary sources | QID and Wikipedia role | Improvement |
| --- | --- | --- | --- |
| Books | Embedded EPUB/ISBN metadata, Apple Books | Resolve edition/work and author QIDs; prefer Wikipedia descriptions when available | One retail catalog, stronger edition evidence, less conflicting metadata |
| Audiobooks | Embedded tags/ISBN/ASIN, Apple audiobooks | Separate audiobook edition from work; reconcile author and narrator QIDs | Avoids Audible/Audnexus dependency while preserving useful embedded identifiers |
| Comics | ComicInfo.xml, Comic Vine | Issue QID when available; otherwise scoped series/run QID; reconcile creator QIDs | Better run and issue selection plus richer local creator evidence |
| Music | Embedded tags, MusicBrainz, Apple artwork/enrichment | Reconcile artist/composer QIDs and related works | Stable open identity plus commercial album art; no unused music-logo pipeline |
| Movies | Embedded technical metadata, TMDB | Bridge TMDB/IMDb IDs to film and person QIDs; use Wikipedia descriptions | TMDB handles identity, metadata, credits, and managed artwork in one path |
| TV | Embedded episode metadata, TheTVDB show/episode identity, TMDB fallback and artwork | Bridge distinct TVDB/TMDB/IMDb IDs to show, episode, and person QIDs | Owned episode accuracy without forcing TMDB's season numbering onto TheTVDB matches |
| People | Provider credits plus Wikidata/Wikipedia/Commons | QID is the canonical cross-media identity; Wikipedia supplies biography | One person can connect credited work across every catalogued lane |
| Subtitles | Local tracks; optional SubDL | No QID role | Local-first subtitle handling with optional authenticated lookup by verified TMDB identity |
| Lyrics | Embedded/local lyrics and LRCLIB | Artist/work QIDs remain relationship evidence | No credential requirement and no change to music artwork behavior |
| Personal photos/video | Local metadata only | Excluded from catalog identity and provider workflows | Provider enrichment cannot leak or misclassify personal content |

## Credential matrix

| Provider | Credential | Who supplies it | Distribution rule |
| --- | --- | --- | --- |
| Apple Search API | None | Nobody | Direct server-side requests |
| MusicBrainz / Cover Art Archive | None | Nobody | Identify the application with the configured user agent and respect throttling |
| Wikidata / Wikipedia / Commons | None | Nobody | Direct server-side requests with caching and rate limits |
| LRCLIB | None | Nobody | Direct server-side requests |
| TMDB | API credential | Tuvima installation credential; optional administrator override where enabled | Provision through Engine secret configuration or environment; never expose it to the Dashboard or commit it |
| Comic Vine | API key | Administrator | Optional; store in Engine secret configuration |
| SubDL | Personal API key | Administrator | Optional; no shared embedded Tuvima key |

Google Books, Open Library, Audible, Audnexus, and Fanart.tv are not active
network providers. Historical IDs may still be retained solely as local or
Wikidata bridge evidence.
