---
title: "Hydration Pipeline, Provider Architecture and Enrichment Strategy"
description: "Deep technical documentation for provider adapters, waterfalling, and Wikidata bridge resolution."
audience: "developer"
category: "architecture"
product_area: "providers"
tags:
  - "providers"
  - "hydration"
  - "wikidata"
---
This document describes how Tuvima Library discovers metadata for ingested media files: the provider architecture, retail gate, Wikidata bridge resolution, file readiness, enrichment, provider response caching, and the review queue data model.

Terminology note: implementation code and provider config still use the older provider-phase names `Stage 1` for retail, `Stage 2` for Wikidata, and `Stage 3` for post-identity enrichment because those values appear in `hydration_stages`. The user-facing Ingestion page maps those phases to numbered operational stages: retail is Stage 3, Wikidata is Stage 4, file readiness is Stage 5, and post-identity enrichment spans Stages 6-8.

---

## 1. Provider Authority Model

Wikidata is the sole identity authority. Every media item is identified by its Wikidata Q-identifier (QID) when one can be resolved. The main browse surfaces, however, is no longer gated on QID alone. An item can be shown in the main browse surfaces once it has a non-placeholder title, a resolved media type, and a settled artwork outcome (`present`, or `missing` after the artwork pass has explicitly settled). Items that are still uncertain stay in Activity, Review, and the Review Queue instead of appearing early in the main browse surfaces.

All providers divide cleanly into two categories:

**Wikidata + Wikipedia** - the sole sources for canonical structured data: titles, authors, series relationships, franchise links, fictional entities, person biographies, and all bridge identifiers. The Wikidata Reconciliation client (`Tuvima.Wikidata`) handles QID resolution via the OpenRefine Reconciliation API, property fetching via the Data Extension API, and Wikipedia summaries via `GetWikipediaSummariesAsync`.

**Retail providers** - exist solely to supply matching data that aids identity resolution, plus media assets that Wikidata cannot host. Their output is never treated as canonical structured data. Retail providers contribute:
- Cover art and promotional imagery (copyright-safe sources that Wikimedia cannot host)
- Descriptions and ratings (for display and candidate ranking)
- Bridge identifiers: ISBN, ASIN, TMDB ID, Apple Books ID, Comic Vine ID, and any enabled provider-specific IDs - these are used by the Wikidata lookup stage to resolve the QID precisely

The distinction matters for trust: a title or author name from Apple API is a hint used to rank candidates, not a fact stored as canonical data. Only Wikidata-sourced claims become canonical values.

### Provider Inventory

**Zero-key providers (no API key required):**

| Provider | Media Types | What it contributes |
|---|---|---|
| Apple API | Books, Audiobooks, Music | Cover art (up to 3000x3000 via the 9999 trick), description, rating, Apple Books/Apple Music IDs; for music it enriches after MusicBrainz identity |
| MusicBrainz | Music | Primary music identity lookup for recording, release, release-group, artist, and ISRC evidence |
| LRCLIB | Music | Lyrics and timed lyrics; text-track enrichment, not identity |
| Wikidata / Wikidata Reconciliation | All | QID, all structured properties, Wikipedia descriptions, person headshots (P18, persons only) |

**Key-required providers (free API key):**

| Provider | Media Types | What it contributes |
|---|---|---|
| TMDB | Movies, TV | Identity, metadata, poster/backdrop/logo/season/episode artwork, people seeds, TMDB/IMDb/TVDB/Wikidata bridge IDs, and network/studio identity |
| TheTVDB | TV | Primary show and episode identity, selectable episode-order data, and TV artwork when connected |
| Comic Vine | Comics | Cover art (super_url, ~900px), issue title/synopsis/source URL, volume/run facts, Comic Vine issue and volume IDs |
| SubDL | Movies, TV | Subtitle candidates and normalized text tracks |

**Copyright constraint - P18 (Image):** Wikidata P18 is exclusively for Person entities (author/director headshots from Wikimedia Commons). P18 is never fetched for media items. Media cover art comes exclusively from retail providers.

---

## 2. Provider Configuration Architecture

All provider behaviour is declared in JSON config files under `config/providers/`. There are no individual adapter classes for REST+JSON providers - they all run through a single `ConfigDrivenAdapter`. Adding a new REST+JSON provider is a zero-code operation: drop a config file and restart.

Each provider config file declares:

```
adapter_type          "config_driven" - routes to ConfigDrivenAdapter; "reconciliation" - ReconciliationAdapter
provider_id           Stable GUID used as the FK value in metadata_claims rows
hydration_stages      Array: [1] = RetailIdentification, [2] = WikidataBridge
cache_ttl_hours       How long raw API responses are cached in provider_response_cache
throttle_ms           Minimum delay between calls to this provider
max_concurrency       Maximum concurrent calls
can_handle            media_types[] scoping - which media types this provider serves
search_strategies     Ordered list of URL templates with required_fields and media_type scoping
field_mappings        JSON path extraction rules with named transforms, confidence values, media_type scoping
```

**Media-type scoping on strategies and field mappings:** A single provider config can serve multiple media types. Individual `search_strategies` and `field_mappings` entries carry an optional `media_types` array. When a request includes a media type, only matching entries are used. Entries with no `media_types` array are universal. `MediaType.Unknown` acts as a wildcard.

**ReconciliationAdapter** uses the `Tuvima.Wikidata` NuGet package. Its configuration lives in `config/providers/wikidata_reconciliation.json` (all Wikidata settings: provider scoring config, property map, bridge lookup order, value transforms, instance_of class mappings, edition pivot rules, scope exclusions).

**ValueTransformCatalog** provides named transform functions applied to raw API values: `to_string`, `strip_html`, `url_template`, `regex_replace`, `prefer_isbn13`, `array_join`, `array_nested_join`, `first_n_chars`, `fallback_key`, `title_case`. Transform assignment lives in config; transform implementations live in code.

**Required-field short-circuits:** Each search strategy declares `required_fields`. If a required field is missing from the request, the strategy is skipped with no HTTP call made.

**Comic title path validation:** `ConfigDrivenAdapter` validates a response before accepting it by checking that at least one recognised title field is non-empty. Recognised comic title fields include `name`, `title`, `issue`, `series.name`, `series`, and `volumeName`.

### Sequence, Attribution, And Artwork Responsibilities

Providers may supply sequence facts, but they do not get to create title-specific
corrections. The Engine interprets those facts through shared media-type
placement rules:

| Provider | Sequence responsibility |
| --- | --- |
| Comic Vine | Issue matches must carry the volume ID when available; volume lookup supplies `sequence_total`, `sequence_total_scope = MainSequence`, start year, and publisher. |
| Apple API | Accepted album identity supplies the album/track manifest and track count. Low-confidence album search results must not synthesize missing tracks. |
| TMDB | TV show/season lookups supply episode totals and specials; movie collection data supplies ordered film collection context separate from franchise context. |
| Wikidata | Supplies canonical identity, relationships, and manifests only when the container classification is compatible with the media lane. It must not provide runtime title-specific count overrides. |
| TheTVDB and TMDB | TheTVDB supplies primary TV episode identity when connected; TMDB supplies movie identity and rich video artwork. Subtitle lookup uses a verified episode ID crosswalk rather than copying one catalog's numbers into the other. |

Provider text claims should retain attribution fields when they are surfaced as
descriptions or long-form metadata: provider name, source title, source URL,
license name and URL when known, retrieved timestamp, and whether the displayed
text is summarized or otherwise modified. Comic Vine issue synopses are
issue-scoped display text and are attributed to Comic Vine with the Comic Vine
API Terms; they are not Wikipedia or Creative Commons text. Generic comic
series descriptions remain parent-scoped.

### Provider Decomposition

Provider-side decomposition keeps public adapter and worker contracts stable while
placing implementation details in focused collaborators or partial-class internals.
New provider logic should be added to the matching owner below instead of growing
the public facades.

Current ownership:

| Component | Responsibility |
|---|---|
| `ReconciliationAdapter` | Construction, capabilities, and the public `IExternalMetadataProvider` fetch/search facade. |
| `Adapters/Internals/ReconciliationAdapter.Reconciliation` | Manual reconciliation, batch reconciliation, extension, and media-type candidate filtering. |
| `Adapters/Internals/ReconciliationAdapter.FictionalAndEditions` | Fictional-entity projections, TV manifests, author pseudonyms, audiobook editions, and bridge-request construction. |
| `Adapters/Internals/ReconciliationAdapter.BridgeResolution` | Library-backed bridge resolution, candidate acceptance, rollup selection, and the public Stage 2 resolution methods. |
| `Adapters/Internals/ReconciliationAdapter.WorkClaims` | Resolved work claim composition and sequence/child-discovery setup. |
| `Adapters/Internals/ReconciliationAdapter.EntityEnrichment` | Child entities, people, Wikipedia descriptions, labels, and language normalization. |
| `Adapters/Internals/ReconciliationAdapter.ClaimMapping` | Data-extension filtering/mapping, value extraction, cache keys, and staleness checks. |
| `ConfigDrivenAdapter` | Construction, capabilities, and the public config-driven fetch/search facade. |
| `Adapters/Internals/ConfigDrivenAdapter.SearchExecution` | Strategy execution, response caching, multi-result projection, and search scoring. |
| `Adapters/Internals/ConfigDrivenAdapter.ComicValidation` | Claim validation plus Comic Vine volume, manifest, creator, and request-alignment rules. |
| `Adapters/Internals/ConfigDrivenAdapter.TmdbEnrichment` | TMDB detail, collection, cast/crew, content-rating, URL, and response-cache enrichment. |
| `Adapters/Internals/ConfigDrivenAdapter.ResultSelection` | JSON result navigation and comic, album, year, publisher, and text candidate selection. |
| `Adapters/Internals/ConfigDrivenAdapter.ClaimExtraction` | Release selection, field mapping/transforms, media scoping, language cloning, and private projection records. |
| `CommonsImageResolver` | Wikimedia Commons person image URL/download handling through the `headshot_download` named client. |
| `RetailMatchWorker` | Stage 1 dependencies, durable leasing, and the public polling facade. |
| `Workers/Internals/RetailMatchWorker.MusicBatch` | Album-grouped Apple Music matching and track claim application. |
| `Workers/Internals/RetailMatchWorker.TvBatch` | Show/season-grouped TMDB matching, episode/show claims, and managed still persistence. |
| `Workers/Internals/RetailMatchWorker.CandidateSelection` | Locale/grouping helpers, structural scoring, candidate ranking, and identity/enrichment selection. |
| `Workers/Internals/RetailMatchWorker.JobProcessing` | File-hint loading, local music fallback, single-job provider execution, outcome persistence, and provenance. |
| `WikidataBridgeWorker` | Stage 2 dependencies, durable leasing, and the public polling facade. |
| `Workers/Internals/WikidataBridgeWorker.BatchResolution` | Batch gates, context loading, grouped QID resolution, and result distribution. |
| `Workers/Internals/WikidataBridgeWorker.Finalization` | Candidate persistence, sibling/comic rollups, identity merging, and series-manifest hydration. |
| `Workers/Internals/WikidataBridgeWorker.JobResolution` | Synchronous single-job resolution plus bridge-ID scope, priority, and fallback policies. |
| `Workers/Internals/WikidataBridgeWorker.Persistence` | Retained-retail organization, property persistence, operation progress, Work routing, and person enrichment. |
| `RetailRequestBuilder` | Apple iTunes and TMDB request URL construction plus image URL helpers for touched retail paths. |
| `RetailHttpThrottle` | Cancellation-aware Apple and TMDB pacing. |
| `AppleRetailClient` | Apple search/lookup HTTP, JSON parsing, matching thresholds, and safe provider fallback. |
| `TmdbRetailClient` | TMDB TV search/detail/season HTTP, retry/fallback behavior, and safe provider fallback. |
| `RetailCandidateScorer` | Worker-level retail candidate outcome decisions and score breakdown JSON. |

The provider internals are source-level partial-class boundaries: they do not add
runtime layers, change dependency-injection registrations, or alter serialized
provider contracts. Guardrail tests cap each public adapter/worker facade at 500 lines
and every extracted implementation file at 1,500 lines.

Provider HTTP calls still go through `IHttpClientFactory` and named clients (`apple_api`, `tmdb`, `wikidata_reconciliation`, `headshot_download`). Request construction, throttling, and provider-specific scoring should remain independently testable. Avoid adding new hardcoded provider URLs or threshold logic inside `RetailMatchWorker` or `ReconciliationAdapter`.

---

## 3. Staged Hydration Pipeline

### Overview

When a media file is ingested, the hydration pipeline runs through strict identity stages before deeper enrichment. Stage 1 gathers matching assets from retail providers. Stage 2 uses the bridge IDs deposited by Stage 1 for precise Wikidata identity resolution. Quick Hydration writes the fast browse data, and Stage 3 fills in universe and rich enrichment details.

```
File ingested
     |
     v
Stage 1: RetailIdentification
  |-- Retail providers run in ranked pipeline order (config/pipelines.json)
  |-- Deposit: cover art, descriptions, ratings, bridge IDs
  `- Result: managed cover asset candidates, bridge IDs in metadata_claims
     |
     v
Stage 2: WikidataBridge
  |-- ReconciliationAdapter uses verified bridge IDs for edition-first QID resolution
  |-- No automatic title-only fallback is accepted
  |-- Data Extension API fetches configured properties
  |-- Wikipedia descriptions via GetWikipediaSummariesAsync
  `- On failure: retail metadata is preserved; item remains eligible for recheck/review
     |
     v
Quick Hydration
  |-- Reload canonical values, compute overall confidence
  |-- Persist accepted managed artwork through entity_assets + .data/assets
  `- If below auto_review_confidence_threshold (0.60): LowConfidence review item created
     |
     v
Stage 3: Universe + rich enrichment
  |-- People, fictional entities, narrative roots, relationships
  `-- TMDB movie/TV artwork, LRCLIB lyrics, SubDL subtitles
```

### Stage 1 - RetailIdentification

Retail providers run in ranked pipeline order defined in `config/pipelines.json`. Each media type has an ordered list of providers with a configurable execution strategy (Waterfall, Cascade, or Sequential). Pipeline entries can include an optional `purpose` value such as `identity` or `enrichment` to document why that provider is in the chain; execution still follows rank and strategy. In Waterfall mode, the first provider that returns a result wins; later providers are not called for that field.

Music uses a Sequential Stage 1 chain by default: `musicbrainz` with purpose `identity`, followed by `apple_api` with purpose `enrichment`. MusicBrainz supplies recording, release, release-group, artist, and ISRC bridge evidence. Apple then fills managed artwork, commercial album metadata, genre/year, and Apple source links. Apple is not delayed to Stage 3 for the normal first artwork pass, and album-level IDs/QIDs remain scoped to the album parent rather than individual tracks.

Providers participate in Stage 1 by declaring `"hydration_stages": [1]` in their config.

**Retail match confidence gate:** After each provider returns claims, `RetailMatchScoringService` scores the candidate against file metadata (title 45%, author 35%, year 10%, format 10% + cross-field boosts). Scores below 0.65 are discarded; scores between 0.65-0.90 are accepted with a review flag; scores >= 0.90 are auto-accepted. Auto-accept is further capped to review when creator evidence contradicts the file, when grouped TV matching lacks exact show+season+episode agreement, when grouped music matching lacks track-number or duration corroboration, or when cover similarity would be the only reason a weak-text candidate crossed the gate. This uses the same unified scoring as manual search from the media detail editor.

Stage 1 never waits on Stage 2. Cover art from retail providers is persisted through `entity_assets` and `.data/assets` when accepted. If Stage 1 fails to find any matching provider, the item routes directly to the review queue - **no text-only Wikidata fallback is attempted**. The principle: no retail match = no Wikidata.

### Manual Identity Corrections

Manual editor searches are retail-first by default. The normal **Find Retail Match** flow searches provider catalogues only, applies the selected provider ID and bridge IDs, and then lets Stage 2 align Wikidata from those bridge IDs.

Direct Wikidata search is an explicit exception exposed as **Fix Wikidata Match**. It is used when the provider match is acceptable but the canonical Wikidata identity is wrong, missing, or ambiguous.

When a curator confirms or replaces a Wikidata QID, the item records the match as user-owned and locks canonical identity fields against automatic overwrite. Later sweeps may retry provider-only or missing items, but they must not replace user-confirmed or user-replaced QIDs without a new explicit curator action.
### Stage 2 - WikidataBridge

The `ReconciliationAdapter` runs second, using bridge IDs deposited by Stage 1 for precise QID resolution. The adapter is now a thin orchestrator over `Tuvima.Wikidata` v3.0's `BridgeResolutionService`:

1. **Stage 2 dispatch:** The public `ReconciliationAdapter.ResolveAsync` / `ResolveBatchAsync` build `BridgeResolutionRequest` objects and call `_reconciler.Bridge.ResolveBatchAsync`. The package groups duplicate bridge lookups, ranks candidates with media/title/creator/year/series hints, and returns typed failures and diagnostics.
2. **Bridge ID lookup and rollup:** Requests carry deposited bridge IDs (ISBN, ASIN, TMDB ID, Apple IDs, MusicBrainz IDs, ComicVine IDs, etc.) plus a media kind and rollup target. The package walks P629 to surface canonical work QIDs for editions/releases and can expose P747 work-to-edition paths.
3. **Text fallback (gated):** When bridge lookup fails but the input still has title metadata and a known media type, the package performs typed text fallback internally. The adapter retains a small second pass for old parity behavior, but no text-only Wikidata fallback runs when Stage 1 found no retail/provider match.
4. **Auto-accept:** Score >= ReviewThreshold (0.70 by default for text, the library's own bridge confidence for ISBN matches) -> QID accepted automatically.
5. **Multiple candidates:** Multiple candidates without auto-accept -> `MultipleQidMatches` review item. Conservative matching - no auto-accept when ambiguous.
6. **Data Extension fetch:** After every successful Stage 2 resolution, the adapter calls `BuildClaimsForResolvedQidAsync` which runs a single Data Extension POST (`ExtendAsync`) over known bridge P-codes and converts the response to `ProviderClaim` entries via `ExtensionToClaims`. `WikidataResolveResult` now also carries the bridge diagnostics, ranked candidates, and rollup path emitted by the package.
7. **Post-resolution P31 validation:** After Data Extension claims are fetched, the adapter validates that the resolved entity's P31 (instance_of) is compatible with the requesting media type. `ValidateP31ForMediaType` checks two conditions: (a) the entity's P31 must not appear in `exclude_classes` for the media type, and (b) at least one P31 must appear in `instance_of_classes` for the media type. If validation fails, the result is discarded and fallback can retry. This prevents cross-type contamination where bridge IDs resolve to the wrong entity type. When no P31 data is returned, validation is permissive.
8. **Wikipedia descriptions:** Fetched through the package's Wikipedia summary/description APIs as part of Stage 2.
9. **Pseudonym detection:** After Data Extension, `FetchWorkAsync` calls `_reconciler.Authors.ResolveAsync(request.Author)` to detect Pattern 1 (reverse P742 - "Richard Bachman" -> Stephen King's QID), Pattern 2 (P742 enumeration - Stephen King -> ["Richard Bachman", ...]), and Pattern 3 (collective pseudonyms - "James S.A. Corey" -> Daniel Abraham + Ty Franck). The adapter converts those resolver results into author, pseudonym, and collective-member claims.

Providers participate in Stage 2 by declaring `"hydration_stages": [2]`. Currently only `ReconciliationAdapter` participates in Stage 2.

**Parity baseline:** `tests/fixtures/stage2-baseline-v2.json` is the authoritative snapshot of the library-backed Stage 2 path against a 12-request fixture (books, movies, TV, music, audiobooks, plus Pattern 1/3 edge cases). The `Stage2BaselineCapture.CaptureStage2BaselineViaLibraryPath` xUnit fixture (Skip'd by default) re-captures the baseline on demand against live Wikidata.

**Pipeline continuation on failure:** If Stage 2 fails to resolve a QID and `continue_pipeline_on_authority_failure` is `true`, the pipeline continues (the file retains its Stage 1 metadata). A `QidNoMatch` item is treated as a terminal precision-preserving outcome: the item may still remain visible in the main browse surfaces if it passes the browse readiness gate, but its pipeline step stays at Wikidata and its status makes the missing QID explicit.

**Comic issue/run rollup:** Comics are allowed to resolve to a scoped series or
run QID when Stage 1 has a trusted Comic Vine issue or volume match but
Wikidata has no item for the individual issue. The issue stores
`wikidata_qid`, `wikidata_qid_scope = series`, and
`qid_resolution_method = comic_series_rollup`. Detail pages must label that
link as "Series on Wikidata" so users can see that the QID identifies the
series/run source, not the issue itself. This is structural behavior for comic
metadata gaps, not a title-specific exception.

**Comparable-text normalization:** Title/series equivalence checks across the identity
pipeline are unified on `RetailTextSimilarity.NormalizeComparableText` — the single
canonical implementation (diacritic folding, `&` → " and ", lowercasing, and
punctuation/whitespace collapse) shared by Stage 1 retail matching
(`RetailMatchScoringService`, `RetailMatchWorker`) and Stage 2 Wikidata bridging
(`WikidataBridgeWorker`), as well as manual search (`SearchService`). Previously,
Stage 2 used a divergent private variant that neither stripped diacritics nor mapped
`&` to "and", so a title such as "Für Elise & Co" compared equal in Stage 1 but not in
Stage 2. Bridge matching for titles containing diacritics or ampersands may now
succeed where it previously failed — this is an intentional correctness fix, not a
config change.

### Provider Pipeline Assignments (config/pipelines.json)

| Media Type | Primary | Secondary | Tertiary | Bridge to Wikidata |
|---|---|---|---|---|
| Books | Apple API | - | - | ISBN (P212), Apple Books ID (P6395) |
| Audiobooks | Apple API | - | - | ASIN, Apple Books ID (P6395) |
| Movies | TMDB | - | - | TMDB ID (P4947), IMDb ID (P345) |
| TV | TheTVDB, then TMDB | - | - | Distinct TheTVDB and TMDB show/episode IDs, IMDb ID (P345) |
| Comics | Comic Vine | - | - | Comic Vine ID (P5905) |
| Music | MusicBrainz | Apple API | - | MusicBrainz recording/release IDs first; Apple Music IDs second |

For comics, ComicInfo.xml creator fields are the highest-priority creator
evidence for display. Comic Vine structured creator credits can fill missing
writer/artist values, and Wikidata creator claims are used when neither local
nor Comic Vine creator evidence is available. Parent-scoped series/run creator
values remain visible on issue rows; issue-scoped local creator values may be
used as a display fallback when the parent/run has no creator yet.

### Pipeline Configuration (config/hydration.json)

```json
{
  "stage_concurrency": 3,
  "stage1_timeout_seconds": 45,
  "stage2_timeout_seconds": 30,
  "disambiguation_threshold": 0.7,
  "auto_review_confidence_threshold": 0.60,
  "max_qid_candidates": 5,
  "continue_pipeline_on_authority_failure": true,
  "universe_title_search_auto_accept": 0.80,
  "stage2_waterfall_confidence_threshold": 0.65
}
```

### Dual-Path Architecture

The pipeline maintains two separate processing paths that are safe to run concurrently:

- **`HydrationPipelineService`** handles `MediaAsset`-type requests using the staged identity pipeline (Stage 1 retail -> Stage 2 Wikidata -> Quick Hydration, with Stage 3 follow-up enrichment).
- **`MetadataHarvestingService`** handles `Person`-type requests from `RecursiveIdentityService`, running Wikidata enrichment directly without the retail Stage 1.

Person creation is idempotent - both paths can run simultaneously without conflict.

---

## 4. Two-Pass Enrichment Architecture

> Current default: `two_pass_enabled` is `false` in `config/hydration.json`. The material below describes the optional two-pass mode, not the default runtime path.

The two-stage pipeline runs twice, at different times, to different depths. This separation ensures files appear on the Dashboard within seconds while the deeper universe intelligence work runs in the background when the system is idle.

### Pass 1 - Quick Match (immediate, during ingestion)

Pass 1 runs as part of normal ingestion and executes a shallow version of both stages:

- **Stage 1 (core subset):** Retail providers gather cover art, descriptions, and bridge IDs.
- **Stage 2 (core subset):** Wikidata QID resolved from bridge IDs. Core properties only fetched: title, author/artist, year, genre, series, series_position. Wikipedia descriptions fetched. The full 50+ property Data Extension deep hydration is skipped.
- **Basic person creation:** Author, narrator, and director Person records are created with name, headshot, and occupation. Social links and biographical details are deferred to Pass 2.

Result: the file appears on the Dashboard within seconds with title, author, cover art, and author photo.

### Pass 2 - Universe Lookup (deferred, background)

Pass 2 runs in the background and handles everything that makes the library intelligent:

- Full Data Extension deep hydration - all 50+ properties from `config/providers/wikidata_reconciliation.json`
- Collection Intelligence - franchise resolution, narrative root assignment (P1434, P8345, P179)
- Fictional entity discovery - characters, locations, organisations, events, and objects
- Relationship population - father, spouse, member_of, performer links (depth limit configurable via `lineage_depth`, default 2)
- Deep person enrichment - social links (Instagram, TikTok, Mastodon, website), biographical details (birth/death dates, nationality), pseudonym resolution (P1773/P742)
- Character-performer links - which actor played which character in each adaptation
- Universe graph population - fictional entities, relationships, and narrative roots written to SQLite

**Recursive enrichment in Pass 2:** When Pass 2 discovers a new connection - a pen name, an actor who played a character from a book, a director's other works - it enriches those people too. This recursive chain only runs in Pass 2 to avoid load during initial ingestion. Pass 1 creates Person records; Pass 2 follows the web.

### Scheduling

Three mechanisms ensure all files eventually receive Pass 2 enrichment:

1. **Priority queue (primary):** Pass 2 requests go onto a low-priority background channel. When the ingestion pipeline is idle (no Pass 1 work pending), the service picks up Pass 2 requests with a configurable rate limit (default 2-second gap between Reconciliation calls). New file arrivals preempt Pass 2 work.

2. **Nightly sweep (safety net):** A configurable cron job scans for Pass 2 requests older than `pass2_stale_threshold_hours` that the queue has not yet processed. Runs in batches with inter-batch delay.

3. **User-triggered override:** The Hydrate button in the Dashboard runs both passes synchronously via `RunSynchronousAsync`, bypassing the queue entirely for immediate results.

4. **On-demand deep enrichment:** `POST /universe/entity/{qid}/deep-enrich` - triggered when a user navigates to an un-enriched entity in the Chronicle Explorer. Enqueues via `IMetadataHarvestingService`. Depth capped at 3. Returns within 2-3 seconds.

### Two-Pass Configuration (config/hydration.json additions)

```json
{
  "two_pass_enabled": true,
  "pass1_core_properties_only": true,
  "pass2_idle_delay_seconds": 10,
  "pass2_rate_limit_ms": 2000,
  "pass2_nightly_cron": "0 2 * * *",
  "pass2_stale_threshold_hours": 24,
  "pass2_batch_size": 50
}
```

---

## 5. Provider Response Caching

The `provider_response_cache` table stores raw JSON responses from metadata provider API calls. This eliminates redundant requests when multiple files share the same entity - TV episodes from one series, album tracks, comic issues from one volume.

### How It Works

Before making an HTTP call, `ConfigDrivenAdapter` computes a SHA-256 hash of the full request URL. It checks `provider_response_cache` for a non-expired entry:

- **Cache hit (not expired):** Returns cached response. No HTTP call made.
- **Cache hit (expired, has ETag):** Sends `If-None-Match` header. HTTP 304 Not Modified -> reuses cached response, refreshes expiry.
- **Cache miss:** Makes HTTP call, writes response to cache with per-provider TTL.

### Per-Provider TTL Defaults

| Provider | TTL | Rationale |
|---|---|---|
| Apple API | 168 hours (7 days) | Retail data changes infrequently |
| TMDB | 168 hours (7 days) | Retail data changes infrequently |
| MusicBrainz | 336 hours (14 days) | Discography data is stable |
| Comic Vine | 720 hours (30 days) | Strict rate limits - aggressive caching |

TTL is configured per-provider via `cache_ttl_hours` in each provider config file.

### Scope

The response cache is a performance optimisation only. It is not part of the data model. On a fresh install or database rebuild, the cache starts empty and repopulates naturally during re-hydration. Canonical values are always rebuilt from file re-ingestion and batch Reconciliation API calls - never from the response cache.

### Rate Limit Context

| Provider | Rate Limit | 10,000 files (uncached) | With Cache |
|---|---|---|---|
| Apple API | ~20 req/sec | ~33 min | ~5 min |
| TMDB | 50 req/sec | ~42 min | ~5 min |
| MusicBrainz | 1 req/sec | ~3 hours | ~15 min |
| Comic Vine | 200 req/hour | ~14 hours | ~2 hours |
| Wikidata Reconciliation | ~5 req/sec | ~83 min | ~20 min |

---

## 6. Description Signal Extraction

When a file's metadata is missing the narrator, translator, or illustrator - or when Wikidata resolves to the work level instead of a specific edition - the Engine mines retail provider descriptions for person names. "Read by Scott Brick" in an Apple API description becomes a narrator claim after Wikidata verification.

### Two Purposes

**Candidate ranking improvement (inline, during Stage 1):** Person names are extracted from each candidate's description and compared name-to-name against hints in the file's embedded metadata. A matching name boosts the candidate's score; a mismatch penalises it. This is more precise than fuzzy-matching the name against the full description paragraph.

**Person record creation (background, after Stage 1):** After Stage 1 selects a winning candidate, all person names are extracted from the description, validated (minimum 2 words, uppercase start, not in stop list), and queued as pending signals. A background worker batch-verifies them against Wikidata: searching for each unique name, fetching P31 (is human?) and P106 (occupation), and confirming the person works in the right field for the extracted role.

### Extraction Rules (config/signal_extraction.json)

Extraction rules are configured per media type with regex patterns, role assignments, and Wikidata occupation classes for verification:

| Media Type | Extracted Roles | Example Patterns |
|---|---|---|
| Audiobooks | Narrator | "Read by", "Narrated by", "Performed by" |
| Books | Translator, Editor, Illustrator, Author (foreword) | "Translated by", "Edited by", "Illustrated by", "Foreword by" |
| Movies | Director, Cast Member, Producer | "Directed by", "Starring", "Produced by" |
| TV | Director, Cast Member | "Directed by", "Starring" |
| Comics | Author, Illustrator | "Written by", "Art by", "Pencils by" |
| Music | Producer, Featured Artist | "Produced by", "feat." |

Each extraction rule carries Wikidata occupation class Q-identifiers used to confirm the person works in the right role. For example, the Narrator role verifies against Q1622272 (narrator), Q33999 (actor), and Q2405480 (voice actor).

### Confidence Tiers

| Verification result | Confidence |
|---|---|
| Extracted from description, unverified | 0.60 |
| Extracted from file metadata, unverified | 0.75 |
| QID found + occupation matches role | 0.85 |
| QID found + human but no matching occupation | 0.65 |
| QID found but not human, or no match | Discarded |

### Batch Processing Architecture

Inline extraction runs during hydration with zero API calls - pure regex plus name validation. All Wikidata verification is deferred to a background worker (`PersonSignalVerificationWorker`) that polls every 5 minutes, deduplicates names across entities, and batch-verifies in a single `wbgetentities` call. For 500 audiobooks sharing 30 unique narrators: 30 search calls plus 1 batch properties call.

---

## 7. Recursive Person Enrichment

### 7.1 Person Role Extraction

The Engine extracts person roles from both structured Wikidata properties and file metadata across all media types:

| Wikidata Property | Role | Media Types |
|---|---|---|
| P50 | Author | Books, Audiobooks, Comics |
| P57 | Director | Movies, TV |
| P58 | Screenwriter | Movies, TV |
| P86 | Composer | Movies, TV, Music |
| P110 | Illustrator | Books, Comics |
| P161 | Cast Member | Movies, TV (capped at 20 per work) |
| P175 | Narrator | Audiobooks (via edition resolution) |

**Media-type-aware Performer mapping:** The generic `Performer` role from file tags is mapped to a more specific role based on media type before person records are created:

| Media Type | Performer maps to |
|---|---|
| Music | Performer |
| Audiobooks | Narrator |
| TV, Movies | Actor |

This prevents audiobook narrator names from being stored with the generic Performer role, which would cause them to appear under the Musicians filter in the People tab rather than under Authors.

These are fetched during Stage 2 (WikidataBridge) via the `work_properties.core` config. Each property emits both a name claim (e.g. `director`) and a companion QID claim (e.g. `director_qid`) at confidence 0.90.

### 7.2 QID-First Person Creation

Person records are only created when a Wikidata QID is confirmed. The pipeline:

1. Extract person references from raw claims - pairing name claims with companion QID claims by index.
2. Apply the QID-first gate: only references with a confirmed QID proceed.
3. Look up or create a Person record (QID-first via `FindByQidAsync`).
4. Link the Person to the media asset (INSERT OR IGNORE - idempotent).
5. Add the role to the `person_roles` junction table (idempotent - one person can be Director on Film A, Cast Member on Film B).
6. If the Person has not been enriched (or enrichment is stale >30 days), return a harvest request.

### 7.3 Standalone Person Reconciliation

After Stage 2, some person names from file metadata remain unlinked - e.g. a narrator from an M4B file when Wikidata has no audiobook edition, or a director from video tags when the work QID has no P57 data.

**`PersonReconciliationService`** resolves these via standalone Wikidata search:

1. Search `wbsearchentities` for the person name, limit 10 candidates.
2. Fetch P31 (instance_of), P106 (occupation), P800 (notable_work) for each candidate.
3. Filter: must be Q5 (human).
4. Score: name similarity (0.50 weight) + occupation match (+0.20 if P106 matches expected role) + notable work match (+0.10 if P800 fuzzy-matches the work title).
5. Auto-accept at score >= 0.80. Deposit companion QID claim at confidence 0.80.
6. Auto-skip below threshold. Retry at next 30-day refresh cycle.

**Three-tier confidence model:**
- Tier 1 (0.90): Structured Wikidata properties (P50, P57, P161, P175)
- Tier 2 (0.80): Standalone person search with occupation match
- Tier 3 (0.75): AI description extraction fallback

### 7.4 AI Person Signal Fallback

The Description Intelligence batch service (LLM-powered) extracts people and roles from text descriptions. When a person is mentioned in a description but no QID exists from higher-tier sources, the batch service feeds the name into `PersonReconciliationService` at confidence 0.75. This only fires when:
- The AI extraction confidence is >= 0.50
- No QID claim already exists for that role from Tier 1 or Tier 2

### 7.4a Person Headshot Download Logging

When the Engine downloads a headshot for a Person record during Stage 2 enrichment, it logs the outcome at `Information` level. Both successful downloads and skip conditions (file already present, no P18 value on the Wikidata entity) are logged so headshot coverage can be audited in the activity log.

### 7.5 Person Data Freshness

To avoid redundant Wikidata API calls when the same person appears across multiple works (e.g. Tom Hanks in 15 movies):

- **Fresh (<=30 days):** Person already enriched -> just link to new media asset, skip re-fetch. Zero API calls.
- **Stale (>30 days):** Check `last_revision_id` against Wikidata entity revision. If unchanged, skip full fetch. If changed, re-fetch all properties.
- **New:** Full property fetch and enrichment.

`last_revision_id` is stored on the Person record (migration M-065) and passed as a hint in harvest requests.

### 7.6 Pseudonym Resolution

After Wikidata enrichment, P1773 (attributed_to) links pen names to real people; P742 (pseudonym) links real people to their pen names. Both directions are stored in the `person_aliases` table.

### 7.7 Actor-Character Mapping

For works with cast members, the pipeline fetches P161 (cast member) statements with P453 (character role) qualifiers from the work's QID. For each actor-character pair, a Person record is created for the actor and linked to the FictionalEntity for the character.

---

## 8. Bridge ID Normalization

Identifiers flow between Wikidata (dashed ISBNs, mixed-case ASINs, full IMDb URLs) and retail providers (bare digit strings, uppercase codes). `IdentifierNormalizationService` normalizes 12 identifier types across three directions:

| Direction | Method | Purpose |
|---|---|---|
| `NormalizeRaw` | Cleans up from any source | Input normalization; includes ISBN-13 Mod10 checksum validation |
| `ToWikidataFormat` | Converts to Wikidata's expected format | Used when writing claims or comparing against Wikidata values |
| `ToRetailFormat` | Strips to bare form | Used when calling retail provider APIs |

**Supported identifier types:** ISBN-13, ISBN-10, ASIN, IMDb, Apple Books ID, TMDB, MusicBrainz, Goodreads, ComicVine, ISRC, LCCN.

**Key aliases:** `isbn_13` -> `isbn`, `isbn_10` -> `isbn` (provided by `GetClaimKeyAlias`).

**Edition bridge ID filtering:** When `ReconciliationAdapter` resolves editions, it filters by P31 (instance_of) to ensure the correct edition type is matched - audiobooks get audiobook-edition ISBNs, books get print ISBNs.

---

## 9. Review Queue Data Model

The review queue surfaces items that need human attention. Dashboard review actions open the shared media editor in Review mode, while normal fixes stay inline on the media surface where the issue appears. The Dashboard interaction layer is described in the UI architecture document. This section covers the data model and API.

### Review Item Types

| Trigger | Cause |
|---|---|
| `WikidataBridgeFailed` | Stage 2 (Wikidata) failed to resolve a QID from bridge IDs |
| `LowConfidence` | Pipeline completed but overall confidence fell below `auto_review_confidence_threshold` (0.60) |
| `MultipleQidMatches` | Stage 2 found multiple Wikidata candidates; user must pick one |
| `UserFixMatch` | User manually flagged an item for re-review |
| `ArbiterNeedsReview` | Collection Arbiter flagged an uncertain Collection assignment |
| `AmbiguousMediaType` | Media type disambiguation could not determine the content type with sufficient confidence |

Each review item carries: entity reference, trigger reason, confidence score, optional disambiguation candidates (JSON array of `{ qid, label, description }`), and a human-readable detail string.

### Resolution Flow

1. User opens Review Queue in Settings/Admin.
2. Selects a review item -> sees current metadata versus proposed match.
3. For `MultipleQidMatches`: picks a QID candidate from a card grid.
4. Clicks Resolve -> `POST /review/{id}/resolve` fires.
5. Engine creates user-locked claims for any field overrides.
6. If a QID was selected -> Stage 2 (WikidataBridge) re-runs synchronously with the pre-resolved QID.
7. Activity ledger records `ReviewItemResolved`.
8. SignalR broadcasts `ReviewItemResolved` -> review badge count decrements.

### API Endpoints

| Method | Route | Auth |
|---|---|---|
| `GET` | `/review/pending?limit=50` | Admin, Curator |
| `GET` | `/review/{id}` | Admin, Curator |
| `GET` | `/review/count` | Admin, Curator |
| `POST` | `/review/{id}/resolve` | Admin, Curator |
| `POST` | `/review/{id}/dismiss` | Admin, Curator |

The review count is used in two places in the Dashboard: the notification bell badge in the TopBar and the profile avatar badge in the AppBar. Both are kept current via SignalR `ReviewItemCreated` and `ReviewItemResolved` events.

---

## 10. Artwork Quality Strategy

Managed artwork is tracked in the database through `entity_assets` and stored under `.data/assets`. Sidecar art beside media files is an optional export only. Art is sourced from retail and artwork providers; Wikidata P18 remains Person-only and is not used as media cover art.

| Media Type | Primary Art Source | Max Resolution | Notes |
|---|---|---|---|
| Books & Audiobooks | Apple API | Up to 3000x3000 | 9999 trick in URL template |
| Movies & TV | TMDB | Up to 2000x3000 | Backdrop available at w1280 |
| Comics | Comic Vine | ~900px | `super_url` field |
| Music | Apple API | Up to 3000x3000 | MusicBrainz supplies identity first; Apple supplies managed cover art |

**Cover art timing:** Stage 1 records provider art and bridge evidence. The artwork pipeline persists accepted files under `.data/assets` and records canonical artwork flags (`cover_state`, `cover_source`, `hero_state`, `artwork_settled_at`) whether art is present, still pending, or explicitly missing. Hero banner generation (SkiaSharp blur + vignette + grain) happens later when the downstream image and organisation flow settles.

**Managed artwork owner:** Books, audiobooks, and comics store accepted cover
art on the owned work itself. Music albums, TV shows, and movie works store the
main artwork on the parent/root owner selected by the media hierarchy. The cover
worker searches asset, self-work, parent-work, and root-work canonical scopes
for an accepted provider cover URL before marking artwork missing, and Stage 2
reruns cover persistence as an idempotent backstop before marking a resolved
item complete.

**Image hash validation:** Cover art and provider thumbnails are tracked by content hash (SHA-256) in the `image_cache` table to prevent redundant re-downloads. When the same image URL appears across multiple entities, the hash is checked first; if found, the cached file path is reused.

**UI URL rule:** Provider image URLs are source inputs, not stable UI outputs.
After artwork settles, browse cards, albums, shelves, search results, review
cards, and detail pages should render a managed `/stream/...` URL or a settled
placeholder. Direct external provider URLs should not be exposed as display
artwork because they can expire, rate-limit, or break independently of the local
library.

## 11. Ranked Pipeline System

Stage 1 retail identification now supports unlimited ranked providers per media type, replacing the fixed 3-slot waterfall system.

### Execution Strategies

| Strategy | Behaviour | Default for |
|---|---|---|
| **Waterfall** | First provider to return a match wins; remaining providers are skipped | Movies, TV, Comics |
| **Cascade** | All providers run independently; their claims are merged | Books |
| **Sequential** | Providers run in order; each passes its bridge IDs to the next | Audiobooks, Music |

### Configuration

Pipeline configuration lives in `config/pipelines.json`:

```json
{
  "pipelines": {
    "Audiobooks": {
      "strategy": "Sequential",
      "providers": [
        { "rank": 1, "name": "musicbrainz" },
        { "rank": 2, "name": "apple_api" }
      ]
    }
  }
}
```

Provider pipeline configuration is read from `config/pipelines.json` via `PipelineConfiguration`.

### Sequential Bridge ID Passing

In Sequential mode, `PriorProviderBridgeIds` on `ProviderLookupRequest` carries bridge IDs from Provider A to Provider B. `ConfigDrivenAdapter.ResolveRequestField` checks these before falling back to the original request properties.

### Key Files

- `src/MediaEngine.Domain/Enums/ProviderStrategy.cs` - Waterfall, Cascade, Sequential enum
- `src/MediaEngine.Storage/Models/PipelineConfiguration.cs` - Pipeline config model + legacy converter
- `src/MediaEngine.Domain/Constants/MediaTypeFieldCatalog.cs` - Fields, search display, searchable fields per media type
- `src/MediaEngine.Providers/Services/HydrationPipelineService.cs` - Strategy execution loop
- `src/MediaEngine.Intelligence/PriorityCascadeEngine.cs` - Tier B reads per-media-type field priorities
- `config/pipelines.json` - Pipeline configuration

---

## Related

- [How Two-Stage Enrichment Works](../explanation/how-hydration-works.md)
- [Providers Reference](../reference/providers.md)
- [How to Add a New Metadata Provider](../guides/adding-a-provider.md)

---

## Durable Identity Pipeline (v2)

> **Active:** The durable identity pipeline is the sole implementation. The legacy in-memory `BoundedChannel` monolith (`HydrationPipelineService`) has been removed. `SynchronousIdentityPipelineService` implements `IHydrationPipelineService` by delegating to the three pipeline workers inline.

### Architecture

The v2 pipeline replaces the in-memory queue with durable SQLite-backed jobs (migration M-080). Three tables: `identity_jobs` (one row per staged asset, tracks state machine), `retail_match_candidates` (every candidate from Stage 1 with full score breakdowns), `wikidata_bridge_candidates` (every Wikidata entity evaluated in Stage 2).

### State Machine

```
Queued -> RetailSearching -> RetailMatched / RetailMatchedNeedsReview / RetailNoMatch
RetailMatched -> BridgeSearching -> QidResolved / QidNeedsReview / QidNoMatch
QidResolved -> Hydrating -> Completed
Failed (terminal - from any state after max retries)
```

`RetailNoMatch` is terminal for the automatic pipeline. Only user Fix Match can advance it.

### Pipeline Workers

Workers are plain service classes in `MediaEngine.Providers/Workers/`. The Api layer wraps them in `BackgroundService` for polling lifecycle.

| Worker | Leases | Output States |
|--------|--------|--------------|
| `RetailMatchWorker` | `Queued` | `RetailMatched`, `RetailMatchedNeedsReview`, `RetailNoMatch` |
| `WikidataBridgeWorker` | `RetailMatched`, `RetailMatchedNeedsReview` | `QidResolved`, `QidNeedsReview`, `QidNoMatch` |
| `QuickHydrationWorker` | `QidResolved` | `Completed` |

### EnrichmentService

Thin dispatcher (`IEnrichmentService`) routing to modular enrichment workers:

- **Quick Pass:** CoverArt -> Persons -> WriteBack
- **Universe Pass:** Children -> Fictional -> Persons (actor mapping) -> Images -> Descriptions -> WriteBack
- **Single Enrichment:** targeted re-run for one enrichment type

### Extracted Helpers

| Helper | Responsibility |
|--------|---------------|
| `BridgeIdHelper` | Bridge ID <-> Wikidata P-code mapping |
| `StageOutcomeFactory` | Review item creation with correct triggers |
| `TimelineRecorder` | Entity timeline event recording |
| `BatchProgressService` | Batch counter adjustment + SignalR progress |
| `PersonReferenceExtractor` | Person reference extraction from claims |

## Wikidata Series Manifest Hydration

After full Stage 2 claims are persisted and routed to Works, Tuvima Library checks for a canonical series QID from bridge/claim data. For books, audiobooks, comics, and TV, it calls `Tuvima.Wikidata.Series.GetManifestAsync` and stores a named ordered manifest locally. Wikidata supplies factual series data; Tuvima stores local ownership state (`Owned`, `Missing`, `Provisional`, `Ambiguous`), warnings, and UI-ready counts.

`series_manifest_refresh_days` in `config/hydration.json` controls how long a fetched manifest is considered fresh. While fresh, later sibling imports relink against the cached named manifest before any refetch. If a newly resolved QID is not present in the cached manifest and no package-level delta API is available, the service falls back to an idempotent full manifest refresh.
