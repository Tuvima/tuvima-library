---
title: "Providers Reference"
description: "Review the capabilities, rate limits, bridge IDs, and behaviors of the retail providers in the hydration pipeline."
audience: administrator
category: "reference"
product_area: "providers"
tags:
  - "providers"
  - "retail-identification"
  - "enrichment"
status: current
---

# Retail Provider Reference

## In this page

Review the capabilities, rate limits, bridge IDs, and behaviors of the retail providers in the hydration pipeline.

## Where this lives in the code

- `config/providers`
- `src/MediaEngine.Providers/Adapters`
- `src/MediaEngine.Providers/Workers`
- `config/pipelines.json`

> **What is this document?**
> A complete reference of every retail provider in the Tuvima Library pipeline - what they accept as search parameters, what they return, and how their output feeds into Stage 4 (Wikidata) resolution. This is the authoritative source for provider capabilities.

---

## How Providers Fit Into the Pipeline

The provider portion of ingestion maps to the numbered stages shown on the Ingestion page:

1. **Stage 3 (Retail Match):** Retail providers search for the media item using file metadata. They return primary cover/poster evidence, descriptions, ratings, and - critically - **bridge IDs** (ISBN, TMDB ID, etc.) that uniquely identify the item on external platforms.

2. **Stage 4 (Wikidata):** The Wikidata Reconciliation adapter uses those bridge IDs to resolve the item's Wikidata QID. Each bridge ID maps to a Wikidata property code (for example, ISBN-13 maps to P212). Stage 4 is strict-gated behind Stage 3: no safe retail match means no automatic Wikidata attempt. A Stage 4 request also needs at least one real bridge ID; title and creator hints are sent as ranking context, not as a broad title-only fallback.

3. **Stage 8 (Artwork):** TMDB refreshes ranked movie and TV backgrounds, title logos, poster variants, and season artwork after a TMDB bridge ID is available. It does not replace a user-selected asset.

Retail providers are a **rich data source for matching** - descriptions, narrator data, ratings, and cover art similarity are all used to rank candidates against file metadata. **Wikidata is the authority** for final canonical values (title, author, year, genre, series).

---

## Provider Summary

| Provider | Media Types | Auth Required | Rate Limit | Language Strategy | Status |
|---|---|---|---|---|---|
| Apple API | Books, Audiobooks, Music | None | 500ms throttle | Localized (user language) | Active |
| TMDB | Movies, TV | API key query parameter | 500ms throttle, max 1 concurrent | Localized (user language) | Active (requires key) |
| TheTVDB | TV | Project or administrator key | Configured provider throttle | Localized (user language) | First TV identity source when connected |
| MusicBrainz | Music | None | 1 request/sec, max 1 concurrent | Source (English only) | Active music identity |
| Comic Vine | Comics | API key | 500ms, max 1 concurrent | Source (English only) | Active (requires key) |
| LRCLIB | Music | None | Configured provider throttle | Source | Text-track provider |
| SubDL | Movies, TV | Personal API key | Configured provider throttle | Source | Optional text-track provider |

---

## Query Parameters - What We Send

Provider search is defined in `config/providers/*.json`, with grouped worker paths for TV and for Apple-led music chains when Apple is configured first. Most providers accept one primary search term; Apple title searches include creator text through `query_template` when available. The returned candidates are still validated afterward by `RetailMatchScoringService`.

API credentials are config-file data. Base provider definitions live in `config/providers/*.json`; optional secret overlays live in `config/secrets/{provider}.json` and are applied by `ConfigurationDirectoryLoader` on `LoadProvider` and `LoadAllProviders`. A blank `http_client.api_key` in the base provider file does not mean the effective runtime key is missing if a matching secrets file exists.

### Active Retail Lookup Matrix

| Media type | Provider | Search or lookup strategy | Lookup inputs | Scoring inputs |
|---|---|---|---|---|
| Books | Apple API | ISBN lookup, Apple Books ID lookup, ebook search | `isbn`, `apple_books_id`, then `title` plus `author` in the Apple search term. | Title, author, year/date, format, description/publisher/page-count cross-checks, cover similarity. |
| Audiobooks | Apple API | Apple Books ID lookup, audiobook search | `apple_books_id`, then `title` plus `author` in the Apple search term. | Title, author, narrator-in-description, year/date, duration, format, cover similarity. |
| Music | MusicBrainz, then Apple API | MusicBrainz recording/release search, then Apple track/album enrichment | `title` plus artist/composer/author and album hints for MusicBrainz; Apple uses title/artist/album and Apple collection IDs after identity. | Track title, artist/composer/author, album, year/date, track number, duration, format, cover similarity. |
| Movies | TMDB | Movie search | `title`; `year` when available; `api_key`; locale. | Title, year, local director/writer/author evidence, format, genre/description cross-checks, poster similarity. |
| TV | TheTVDB, then TMDB | TheTVDB show/episode lookup with TMDB fallback and artwork | `show_name`, selected episode order, TheTVDB show/episode IDs where known; TMDB show ID for supported fallback/artwork. | Episode title, show/series, provider episode ID, year, format, poster/still similarity. |
| Comics | Comic Vine | Issue search, volume search | `title` for issue search; `series` for volume search; `api_key`. | Title, series, issue number or series position, writer/author/illustrator, year, format, cover similarity. |

### Per-Provider Search Strategies

#### Apple API

| Strategy | Priority | Required Fields | URL Pattern | Media Types |
|---|---|---|---|---|
| ISBN Lookup | 0 (highest) | `isbn` | `/lookup?isbn={isbn}` | Books |
| Apple ID Lookup | 1 | `apple_books_id` | `/lookup?id={apple_books_id}&country={country}&lang={lang}_{country}` | Books, Audiobooks |
| Ebook Search | 2 | `title` | `/search?term={title} {author}&entity=ebook&limit={limit}&country={country}&lang={lang}_{country}` | Books |
| Audiobook Search | 2 | `title` | `/search?term={title} {author}&entity=audiobook&limit={limit}&country={country}&lang={lang}_{country}` | Audiobooks |
| Music Track Search | 2 | `title` | `/search?term={title} {artist}&entity=musicTrack&limit={limit}&country={country}&lang={lang}_{country}` | Music |
| Music Album Search | 3 | `title` | `/search?term={artist} {title}&entity=album&limit={limit}&country={country}&lang={lang}_{country}` | Music |
| Music Album Lookup | 4 | `apple_music_collection_id` | `/lookup?id={apple_music_collection_id}&entity=song&country={country}&lang={lang}_{country}` | Music |

**Notes:** ISBN lookup is exact match (1 result). Book and audiobook search returns up to 25 results and fetches the top 5. For music, Apple runs after the configured MusicBrainz identity step and supplies cover art, retail album metadata, genre/year, and Apple source links. Cover art URL transforms remove Apple's size constraints for higher resolution.

File languages written as names (`English`), ISO 639-2 codes (`eng`) or culture codes (`en-US`) are normalised to two-letter codes. Apple's `lang` must be a real language-country pair; otherwise English is used. Apple results from an ISBN lookup are trusted as the exact edition, while search results are accepted only when the file language matches the storefront language.

#### TMDB

| Strategy | Priority | Required Fields | URL Pattern | Media Types |
|---|---|---|---|---|
| Movie Search | 1 | `title` | `/search/movie?query={title}&year={year}&include_adult=false&language={lang}-{country}&page=1` | Movies |
| TV Search | 1 | `title` | `/search/tv?query={title}&first_air_date_year={year}&include_adult=false&language={lang}-{country}&page=1` | TV |
| TV Season Episodes | 2 | `tmdb_id`, `season_number` | `/tv/{tmdb_id}/season/{season_number}?language={lang}-{country}` | TV |

**Notes:** Year is optional but included when available from file metadata. TV jobs are grouped by show and season: the worker searches the show, fetches show details, then fetches the season episode list and distributes episode-level claims to queued files. Returns poster paths that are expanded to `https://image.tmdb.org/t/p/w500{path}`.

A Movies-library item with no TMDB movie match is checked against TMDB's TV catalogue. A strong miniseries or short-season match is never applied automatically; it becomes a Review Queue suggestion, "Found as a TV title", with Move to TV, Search again, or Keep as unmatched film. Move to TV re-files the item under TV as a Season 0 special without moving the file.

#### MusicBrainz

| Strategy | Priority | Required Fields | URL Pattern | Media Types |
|---|---|---|---|---|
| Recording Album Search | 1 | `title` | `/recording?query={query}&fmt=json&limit={limit}` | Music |
| Release Search | 2 | `title` | `/release?query={query}&fmt=json&limit={limit}` | Music |

**Notes:** MusicBrainz is the default first Stage 1 provider for music. It identifies recordings and album/release containers before Apple enrichment runs. Its configured searches combine title with artist/composer/author where available, while album and year remain ranking and scoping signals. Recording IDs stay track-scoped; release and release-group IDs stay album-scoped.

For MusicBrainz and Apple Music matches, a track keeps its tagged album. A release is only applied to the album when its title matches the album tag.

#### Comic Vine

| Strategy | Priority | Required Fields | URL Pattern | Media Types |
|---|---|---|---|---|
| Issue Search | 1 | `title` | `/search/?query={title}&resources=issue&limit={limit}` | Comics |
| Volume Search | 2 | `series` | `/search/?query={series}&resources=volume&limit={limit}` | Comics |

**Notes:** Comic Vine supplies comic issue and volume metadata, including cover art and Comic Vine bridge identifiers. Series and issue hints are used for search and post-search ranking. Exact series + issue number + volume context is authoritative for comics; issue title wording is not allowed to reject an otherwise exact run/issue match. Accepted issue matches write issue-scoped fields (`issue_title`, `issue_description`, and `issue_source_url`) so issue detail pages do not reuse the parent series description as an issue synopsis.

When an accepted issue match includes a volume ID, the Engine performs a volume
lookup and records volume-scoped sequence facts such as issue count, start year,
publisher, and `sequence_total_scope = MainSequence`. Comic shelves should use
the volume/run identity, not an individual issue ID or a same-name title match.
When issue lookup fails but a Comic Vine volume strongly matches series and
structural context, the Engine may accept the volume/run as the retail identity
without inventing an issue ID. Stage 4 can then roll up to a clearly scoped
series/run Wikidata QID using `wikidata_qid_scope = series` and
`qid_resolution_method = comic_series_rollup`.

#### LRCLIB and SubDL

LRCLIB and SubDL provide lyrics and subtitle/text-track data. They do not decide identity, do not unlock Wikidata resolution, and do not participate in retail candidate scoring. SubDL searches matched movies by TMDB movie ID and owned TV episodes by a verified TheTVDB-to-TMDB episode link; the TMDB season and episode numbers are used when catalog numbering differs. Existing OpenSubtitles tracks remain local and playable after that provider is retired.

### Sequence And Artwork Responsibilities

| Media type | Preferred sequence source | Artwork display source |
|---|---|---|
| Books/Audiobooks | Apple retail sequence when available; otherwise Wikidata manifest if the container is sequence-compatible. | Managed asset from accepted Apple/provider cover, then placeholder after artwork settles. |
| Music | MusicBrainz recording/release identity first, then Apple album/artwork enrichment. | Managed asset from the accepted Apple source; no direct provider URL after settlement. |
| Movies | TMDB movie collection for ordered film collections; Wikidata franchise context stays broader discovery context. | Managed TMDB posters, backdrops, and title logos. |
| TV | TMDB show/season/episode details. | Managed TMDB show and season art; episode stills continue to use the existing TMDB episode retrieval. |
| Comics | Comic Vine volume/run and issue metadata. | Managed Comic Vine cover. |

Provider descriptions and long-form metadata should carry source attribution
when surfaced: provider name, source title, source URL, license name, license
URL, retrieval timestamp, and whether the display value was modified or
summarized. Wikipedia text is attributed as Wikipedia/CC BY-SA. Comic Vine
issue synopses are attributed as Comic Vine issue synopses governed by the
Comic Vine API Terms, not as Creative Commons text.

---

## Response Fields - What We Extract

Each provider's config defines a `field_mappings` array that maps JSON response paths to claim keys. These become `ProviderClaim` objects (key, value, confidence) stored in `metadata_claims`.

### Extraction Summary

| Provider | Active media | Main claims extracted | Bridge claims extracted |
|---|---|---|---|
| **Apple API** | Books, Audiobooks, Music | Title, author/artist, year, cover, description, genre, rating for books, album, track/disc counts, track number, duration. Claim confidence is usually 0.70 to 0.90 depending on field and media type. | `apple_books_id`, `apple_music_id`, `apple_music_collection_id`, `apple_artist_id`. |
| **TMDB** | Movies, TV | Title, year, cover, description, short description, rating, original language, genre, network. Claim confidence is usually 0.80 to 0.90. | `tmdb_id`. |
| **Comic Vine** | Comics | Candidate title for matching, `issue_title`, `issue_description`, `issue_source_url`, series, issue number, creator credits, cover, year, series position, volume sequence facts. Claim confidence is usually 0.70 to 1.00. | `comic_vine_id`, `comic_vine_volume_id`. |
| **MusicBrainz** | Music | Title, artist/author, album, year, track count, MusicBrainz IDs, ISRC. | MusicBrainz artist/work/release/recording/release-group IDs when present. |

Numbers in provider JSON represent confidence values assigned to extracted claims when the provider is enabled.

### Value Transforms

The `ValueTransformCatalog` applies transformations during extraction:

| Transform | Purpose | Used By |
|---|---|---|
| `regex_replace` | Strip image size suffixes from cover URLs | Apple API |
| `strip_html` | Clean HTML tags from descriptions | Apple API, Comic Vine |
| `to_string` | Convert numeric values such as IDs and ratings to strings | Apple API, TMDB, Comic Vine |
| `url_template` | Construct full image URLs from partial paths | TMDB, MusicBrainz |
| `first_n_chars(4)` | Extract 4-digit year from date strings | Apple API, TMDB, MusicBrainz, Comic Vine |
| `array_join` | Join array elements into comma-separated strings | Apple API, TMDB, MusicBrainz |

---

## Bridge IDs - What Feeds Stage 4

Bridge IDs are external platform identifiers that the Wikidata Reconciliation adapter uses to resolve the item's Wikidata QID after Stage 3 has produced a safe retail match. Each bridge ID maps to a Wikidata property code.

### Bridge IDs by Provider

| Provider | Bridge ID | Claim Key | Confidence | Wikidata Property |
|---|---|---|---|---|
| **Apple API** | Apple Books ID | `apple_books_id` | 0.95 | P6395 |
| **Apple API** | Apple Music Track ID | `apple_music_id` | 0.95 | P10110 |
| **Apple API** | Apple Music Collection ID | `apple_music_collection_id` | 0.95 | P2281 |
| **Apple API** | Apple Artist ID | `apple_artist_id` | 0.90 | P2850 |
| **TMDB** | TMDB ID | `tmdb_id` | 1.0 | P4947 (movies) / P4983 (TV) |
| **Comic Vine** | Comic Vine ID | `comic_vine_id` | 0.95 | P5905 |
| **Comic Vine** | Comic Vine Volume ID | `comic_vine_volume_id` | 0.95 | Provider/run evidence; used for scoped comic series rollup when issue QID is absent |
| **MusicBrainz** | MusicBrainz IDs | `musicbrainz_id`, `musicbrainz_recording_id`, `musicbrainz_release_group_id` | provider-dependent | P434/P435/P436/P5813/P4404 depending on ID type |
| **Embedded book/comic evidence** | ISBN and retained historical identifiers | `isbn`, `isbn_13`, `isbn_10` | provider-dependent | P212/P957; historical bridge IDs remain evidence only |

### Stage 4 Resolution Flow Per Bridge ID

The Reconciliation adapter is now a thin orchestrator over `Tuvima.Wikidata` v3.0's `BridgeResolutionService`.

1. **Bridge request build:** The adapter converts each `WikidataResolveRequest` into a `BridgeResolutionRequest` with bridge IDs, media kind, title/creator/year/series hints, language, custom P-code mappings, and rollup preference.
2. **Direct lookup:** The package groups `(propertyId, normalizedValue)` lookups so duplicate ISBN/TMDB/Apple/MusicBrainz/ComicVine IDs share one Wikidata query.
3. **Edition awareness:** The package walks P629 for edition/release-to-work rollups and can return both the resolved entity QID and canonical work QID plus the relationship path.
4. **Media-specific bridge mapping:** TMDB, Apple, TVDB, MusicBrainz, and ComicVine keys are mapped to official Wikidata properties inside the package; retained historical keys are evidence only and do not activate a provider.
5. **Strict bridge gate:** If a request has no real bridge IDs, the adapter does not build a Stage 4 bridge request. Title-only automatic requests are skipped.
6. **Claim and diagnostics follow-up:** After every successful resolution, the adapter calls `ExtendAsync` over the known bridge P-codes to populate `WikidataResolveResult.Claims` and `CollectedBridgeIds`, and it also carries `BridgeDiagnostics`, ranked candidates, and rollup details from the package result.

Manual Wikidata searches from the editor and automated bridge resolution both flow through `ReconciliationAdapter` before calling `Tuvima.Wikidata`, but manual user searches are not the same as automatic Stage 4 fallback. Exact QID searches such as `Q155653` are passed through as identity lookups, so the package fetches the entity directly and still applies configured media type constraints.

### Wikidata Lookup Matrix

The bridge worker sends these fields to Wikidata after Stage 3 has produced a retail match and at least one bridge ID.

| Media type | Bridge IDs | Hints | Media kind / filter |
|---|---|---|---|
| Books | `isbn`, `isbn_13`, `isbn_10`, `asin`, `apple_books_id`, `goodreads_id` | Title, author, year, language | Book/literary work, edition-aware |
| Audiobooks | `apple_books_id`, `isbn`, `asin`, MusicBrainz IDs | Title, author, year, language | Audiobook, edition-aware, prefers edition |
| Music | MusicBrainz recording/release/release-group IDs first; Apple Music IDs as secondary hints | Album, artist, composer/author fallback, track title, year, language | Recording/track when safely bridgeable; album when only release-group identity is known |
| Movies | `tmdb_id`, `imdb_id`, Apple TV movie IDs | Title, creator if canonicalized, year, language | Movie/film |
| TV | `tmdb_id`, `imdb_id`, `tvdb_id`, Apple TV show/episode IDs | Show name or series, creator if canonicalized, year, language | TV series |
| Comics | `comic_vine_id`, `comic_vine_volume_id`, `gcd_id`, `isbn` | Series plus title, series title, writer/author/illustrator, year, language | Comic issue when available; scoped comic series/run when issue QID is absent |

---

## Retail Match Scoring

After a provider returns results, the `RetailMatchScoringService` scores each candidate against file metadata:

### Scoring Weights

| Field | Weight | How Scored |
|---|---|---|
| Title | 0.45 | Fuzzy string similarity (Levenshtein distance) |
| Author/Creator | 0.35 | Fuzzy string similarity |
| Year | 0.10 | Exact = 1.0, off-by-1 = 0.8, off-by-2+ = 0.3 |
| Format | 0.10 | Exact media type match |

### Boosters

| Booster | Condition | Boost |
|---|---|---|
| Cross-field | Narrator found in description, genre overlap | Variable |
| Cover art (strong) | pHash similarity > 0.8 | +0.10 |
| Cover art (moderate) | pHash similarity > 0.6 | +0.05 |

Cover similarity can boost an already plausible candidate, but it cannot rescue weak title or creator evidence on its own.

### Thresholds

| Threshold | Value | Result |
|---|---|---|
| Auto-accept | >= 0.90 | Match accepted automatically and allowed to proceed to Wikidata |
| Ambiguous | 0.65 - <0.90 | Review queue (`RetailMatchAmbiguous`) |
| Failed | < 0.65 or no results | Review queue (`RetailMatchFailed`) |

Additional contradiction gates apply before auto-accept. Weak creator agreement can cap a candidate to review, grouped TV auto-accept requires exact show/season/episode agreement, and grouped music auto-accept requires track-number or duration corroboration.

### Cover Art Matching

Embedded cover art from the file is perceptually hashed (aHash - 64-bit fingerprint via 8x8 grayscale resize). When retail provider cover art is downloaded, it's hashed and compared. The Hamming distance produces a similarity score (0.0-1.0) that feeds into the scoring composite as a confidence boost.

---

## Provider Configuration

All provider configs live in `config/providers/` as individual JSON files. Each is self-contained with connection details, search strategies, response mappings, and bridge ID preferences. Adding a new REST+JSON provider is a zero-code operation: create a config file, restart the Engine.

### Config File Structure

```
{
  "id": "UUID",
  "name": "provider_name",
  "display_name": "Human Name",
  "enabled": true,
  "version": "1.0",
  "domain": "media_domain",
  "media_types": ["Type1", "Type2"],
  "entity_types": ["Work", "MediaAsset"],
  "base_url": "https://api.example.com",
  "weight": 0.8,
  "language_strategy": "localized|source|both",
  "auth": { ... },
  "rate_limit": { "throttle_ms": 500, "max_concurrent": 2 },
  "search_strategies": [ ... ],
  "field_mappings": [ ... ],
  "preferred_bridge_ids": { "MediaType": ["id1", "id2"] }
}
```

See `config/providers/` for complete examples of each provider.

## Related

- [How to Configure Metadata Providers](../guides/configuring-providers.md)
- [How to Add a New Metadata Provider](../guides/adding-a-provider.md)
- [How Two-Stage Enrichment Works](../explanation/how-hydration-works.md)
