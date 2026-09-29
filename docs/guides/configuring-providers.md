---
title: "How to Configure Metadata Providers"
summary: "Set up provider keys and defaults, then review the configured ingestion flow."
audience: "user"
category: "guide"
product_area: "providers"
tags:
  - "providers"
  - "api-keys"
  - "configuration"
---

# How to Configure Metadata Providers

This guide explains what metadata providers are, which ones work out of the box, which ones need a key, and how to control how they're used.

---

## What providers do

When the Engine identifies a file in your library, it reaches out to external sources to gather extra information: cover art, descriptions, ratings, cast and crew, identifiers like ISBNs or TMDB IDs, and more. These external sources are called **metadata providers**.

Metadata has two pages. **Providers** lists every user-visible provider once, regardless of media type. **Ingestion Flow** is a read-only explanation of the same numbered stages used by the Engine:

1. **Retail providers** (Stage 3: Retail Match) - run after file details are read. These gather practical information: cover art, descriptions, ratings, and identifiers. The Engine uses this data both to enrich your library and to improve its confidence in identifying what the file is.

2. **Wikidata** (Stage 4: Wikidata) - runs after retail lookup, using identifiers gathered in Stage 3. Wikidata is the authority for canonical structured data: the author's full name, the official series name, genre classifications, director credits, and so on. Wikidata is always free to use and requires no key.

3. **Enrichment and artwork providers** (Stages 6-8) - run after identity is known. These providers add people, relationships, TMDB movie and TV artwork variants, synced lyrics, subtitles, and periodic refresh data. Artwork refreshes preserve any asset you selected yourself.

---

## Providers that work out of the box

These providers require no account, no sign-up, and no configuration. They are active as soon as you install Tuvima Library.

| Provider | What it supplies |
|---|---|
| **Wikidata** | Canonical identity, structured metadata, people, series, genre |
| **Wikipedia** | Plain-language descriptions |
| **Apple API** | Cover art, descriptions, ratings (books, audiobooks, music) |
| **MusicBrainz** | Music recording, release, release-group, and artist identity |
| **LRCLIB** | Lyrics and timed lyrics for music |

These built-in providers are enabled by default where their config marks them active and do not require credential setup.

---

## Providers that require an API key

Some providers require you to create a free account and obtain an API key before they can be used. The key lets the provider's service know the request is coming from your installation.

### TMDB (The Movie Database)

TMDB supplies cover art, descriptions, cast and crew, ratings, and managed poster, background, title-logo, and season-art variants for movies and TV.

1. Go to `https://www.themoviedb.org/settings/api` and create a free account.
2. Request an API key (choose "Developer" use type).
3. Copy the key.
4. In the Dashboard, go to **Settings -> Metadata -> Providers**.
5. Find TMDB, choose **Configure** and **Connect provider**, then enter the key.
6. Choose **Verify & connect**.

### Comic Vine

Comic Vine supplies metadata for comics - issue numbers, story arcs, publishers, and character information.

1. Go to `https://comicvine.gamespot.com/api/` and create a free account.
2. Click **Get API Key**.
3. Copy the key.
4. In the Dashboard, go to **Settings -> Metadata -> Providers**.
5. Find Comic Vine, choose **Configure** and **Connect provider**, then enter the key.
6. Choose **Verify & connect**.

### SubDL subtitles

SubDL is optional. Embedded subtitles and local sidecar files continue to work without an account.

1. Create or sign in to your personal account at [SubDL](https://subdl.com/).
2. Open the account's API section from the [SubDL developer page](https://subdl.com/developers) and generate an API key.
3. In the Dashboard, open **Settings -> Metadata -> Providers**, find **SubDL**, and enter that key.
4. Choose **Verify & connect**. Tuvima checks the key with SubDL and stores it in the Engine's credential store; it does not need your SubDL password.

SubDL searches a matched movie by TMDB movie ID. For an owned TV episode, Tuvima verifies the TheTVDB show and episode IDs against TMDB and searches with TMDB's episode position. If the episode crosswalk is missing or conflicting, Tuvima leaves automatic subtitle download pending rather than guessing from the title or season number. An invalid key, exhausted quota, or provider outage does not remove subtitles already stored locally.

If you previously connected OpenSubtitles, Tuvima no longer uses that provider. Its downloaded tracks stay in your library. After SubDL is working, revoke the old OpenSubtitles key in your OpenSubtitles account and remove the old ignored `config/secrets/opensubtitles.json` file from this installation. Likewise, an old `config/secrets/fanart_tv.json` file is unused; Fanart.tv is not an active artwork provider. Keep any existing artwork and generic `fanart.jpg` sidecars.

---

## Where provider keys are stored

Provider configuration is file based. The Engine reads provider definitions from `config/providers/*.json` and also applies secret overlays from `config/secrets/{provider}.json` when those files exist.

Setup, Settings, and the Engine use the same read-only connection probe declared in each provider file. **Test connection** checks the saved configuration; a sample metadata lookup is a separate operation, since a title returning no match does not mean the provider is offline. The latest configured connection result and check time are cached in SQLite's `provider_connection_checks` table for the Dashboard. This cache contains status and safe explanatory text, never the key. The provider JSON and ignored secrets JSON remain the configuration source of truth; SQLite also retains the existing provider health and permitted response caches.

That means a provider file such as `config/providers/tmdb.json` may show an empty `http_client.api_key` while the effective runtime key is still present in `config/secrets/tmdb.json`. Do not treat a blank base provider file as proof that a key was deleted. Check the matching file under `config/secrets/` as well.

When you save provider settings from the Dashboard, mutable provider settings are written back to the provider config file. For manual edits, keep long-lived credentials in `config/secrets/{provider}.json` so the base provider definition can stay shareable and the key can be rotated independently.

---

## Retail lookup inputs by media type

Retail lookup is Stage 1. It searches the configured provider chain, then scores returned candidates against local file evidence. Books use Apple as their retail catalogue source. Music uses a bounded, configuration-driven chain: MusicBrainz tries identifiers and staged text searches first; Apple can supply fallback identity; and an accepted Apple fallback schedules one configured MusicBrainz reconciliation attempt using Apple-normalized hints.

| Media type | Active retail provider | Lookup inputs sent to provider | Candidate scoring metrics | Bridge IDs produced for Wikidata |
|---|---|---|---|---|
| Books | Apple API | ISBN exact lookup when `isbn` exists; Apple Books ID lookup when `apple_books_id` exists; otherwise ebook search using `title` plus `author` when available. | Title, author, year/date, media format, description/publisher/page-count cross-checks, and cover similarity when available. | `apple_books_id`; existing file ISBN/ASIN evidence can also be carried as bridge evidence. |
| Audiobooks | Apple API | Apple Books ID lookup when available; otherwise audiobook search using `title` plus `author` when available. | Title, author, year/date, narrator-in-description, duration when available, media format, and cover similarity. | `apple_books_id`; existing `isbn` or `asin` evidence can also be carried as bridge evidence. |
| Music | MusicBrainz, then Apple API, with configured MusicBrainz reconciliation | MusicBrainz uses an embedded recording ID first, then ISRC, title/artist/album, title/album, and high-confidence recording-only searches. The recording-only stage can retain recording identity when no suitable nested release exists. Apple supplies fallback identity when those attempts remain unresolved. An accepted Apple fallback passes configured normalized fields into one bounded MusicBrainz retry. | Track title, configured creator-list policy, album, year/date, track number, duration, media format, exact bridge identifiers, and cover similarity. | MusicBrainz recording/release/release-group IDs first; Apple Music track, collection, and artist IDs remain valid when MusicBrainz cannot corroborate the retail identity. |
| Movies | TMDB | Movie search using `title`; `year` is included when known; requests include the configured TMDB API key. | Title, year, director/writer/author evidence when present locally, media format, genre/description cross-checks, and poster similarity. | `tmdb_id` mapped as a movie identifier. |
| TV | TheTVDB, then TMDB | TheTVDB supplies the primary show and episode identity when connected; TMDB remains available for fallback and artwork. The subtitle path uses a verified direct TheTVDB-to-TMDB episode link. | Episode title, show/series, provider episode ID, season/order context, year, media format, and poster/still similarity. | Distinct `tvdb_id`, `tvdb_episode_id`, `tmdb_id`, and `tmdb_episode_id` bridges where verified. |
| Comics | Comic Vine | Issue search using `title`; volume search using `series`; requests include the configured Comic Vine API key. | Title, series, issue number or series position, writer/author/illustrator evidence, year, media format, and cover similarity. | `comic_vine_id`; existing ISBN/GCD evidence can also be carried as bridge evidence. |

Retail confidence uses the configured weights in `config/hydration.json`: title `0.45`, creator `0.35`, year `0.10`, and format `0.10`. A score of `0.90` or higher can auto-accept, `0.65` to below `0.90` goes to review, and lower scores are treated as no safe retail match.

---

## Wikidata inputs by media type

Wikidata is Stage 2. It is intentionally gated behind Stage 1: the Wikidata bridge worker only processes items that reached `RetailMatched` or `RetailMatchedNeedsReview`. Items with no safe retail match are not sent to Wikidata as a broad title-only fallback.

Stage 2 requires at least one real bridge ID. Title, creator, year, series, album, artist, and language hints help the resolver rank or roll up results, but they do not bypass the bridge-ID requirement.

Wikidata relationship targets are classified before they become shelves. Ordered series, album releases, TV shows/seasons, comic series, and manga series can become immediate lane shelves. Franchises and universes are broader relationship context, and Wikimedia list articles or publisher/production lists are diagnostics only. A fresh ingestion uses this classification immediately; existing persisted rows are not backfilled or repaired in place.

| Media type | Wikidata bridge IDs used | Hints sent with the bridge request | Wikidata media kind and filtering | Edition/rollup behavior |
|---|---|---|---|---|
| Books | `isbn`, `isbn_13`, `isbn_10`, `asin`, `apple_books_id`, `goodreads_id` when present. | Title, author, year, language. | Book/literary work classes; excludes people, films, TV, and music classes. | Edition-aware; returns the work and edition when available. |
| Audiobooks | `apple_books_id`, `isbn`, `asin`, MusicBrainz IDs when present. | Title, author, year, language. | Audiobook and written-work classes. | Edition-aware and prefers audiobook edition identity when available. |
| Music | MusicBrainz recording, release, and release-group IDs first; Apple Music track, collection, and artist IDs as secondary hints. | Album title, artist, composer/author fallback, track title, year, language. | Track/recording QIDs when safely bridgeable; album/release-group QIDs stay on the album parent. | Edition-aware; album IDs roll up tracks to the album/work identity without forcing album QIDs onto tracks. |
| Movies | `tmdb_id`, `imdb_id`, Apple TV movie IDs when present. | Title, author/creator if canonicalized, year, language. | Movie/film classes; TMDB maps to the movie property. | Not edition-aware in the bridge worker; returns work identity. |
| TV | `tmdb_id`, `imdb_id`, `tvdb_id`, Apple TV show/episode IDs when present. | Show name or series as title, author/creator if canonicalized, year, language. | TV-series classes; TMDB maps to the TV-series property. | Not edition-aware in the bridge worker; resolves series/show identity. |
| Comics | `comic_vine_id`, `gcd_id`, `isbn` when present. | Series plus title, series title, writer/author/illustrator fallback, year, language. | Comic issue when a series title is present; otherwise comic series. | Not edition-aware in the bridge worker; resolves issue or series identity depending on hints. |

When a bridge ID resolves, Wikidata supplies canonical identity, relationship facts, people, series/franchise data, and additional bridge identifiers. If retail succeeded but Wikidata cannot resolve a QID, the item keeps its retail metadata and is marked as a missing-QID outcome rather than being silently changed. It can still receive a Read, Watch, or Listen shelf from provider/local grouping metadata; it only becomes a top-level Collections rollup when trusted shared relationships connect multiple shelves.

---

## Reviewing provider order and roles

Open **Settings -> Metadata -> Ingestion Flow** to see the active order for each media type. The page labels providers as Primary, Secondary, Fallback, Required, or Optional and shows the outputs contributed at each stage. It is intentionally read-only so inspecting the flow cannot accidentally change ingestion behavior.

Provider execution order remains media-scoped in `config/pipelines.json`. Sequential chains run in listed order, passing bridge IDs forward. For music, the default configuration assigns MusicBrainz the `identity` role and Apple the `enrichment` role with `requires_identity: true` plus `use_as_identity_fallback: true`. Apple's `accepted_transition` points back to MusicBrainz for one reconciliation attempt only when Apple supplied the fallback identity. `max_provider_attempts` is an absolute safety budget. Query clauses, candidate paths, nested release constraints, creator-list behavior, transition hint fields, and retry counts all live in validated JSON configuration rather than provider-name branches in the worker.

Wikidata appears in the same provider inventory as every other provider. Ingestion Flow shows its required canonical-identity role separately from optional post-match providers such as LRCLIB and SubDL.

---

## Language strategy per provider

Providers differ in what languages they support. Each provider has a **language strategy** that controls which language the Engine queries it in.

| Strategy | What it means |
|---|---|
| **Source** | Always query this provider in English, regardless of your language settings. Use this for providers whose data is English-only or not localized. |
| **Localized** | Query this provider in your metadata language setting. Use this for providers with strong international content (TMDB, Apple API). |
| **Both** | Query in your metadata language first; if the result is empty, retry in English and merge the results. Wikidata uses this by default. |

To change the language strategy for a provider:

1. Go to **Settings -> Metadata -> Providers**.
2. Choose **Configure** for the provider you want to update.
3. Find the **Language Strategy** dropdown in the provider's settings panel.
4. Select the strategy you want and click **Save**.

---

## How cover art is handled

Each provider that supports cover art downloads images into the managed asset store under `.data/assets/...` and records them in the database, usually through `entity_assets`. You never need to re-download them. Images beside media files are optional export mirrors only when storage policy enables them.

When multiple providers supply cover art for the same title, the Engine also checks the artwork visually against the cover already embedded in the file. This comparison helps identify the best match and can improve the Engine's confidence in its identification - not just the quality of the image.

In the Review Queue, you can see all the cover art options gathered for any item. Open the item's detail drawer and look at the **Assets** section. You can set a preferred image, upload your own, or keep the one the Engine selected automatically. Any image you upload is protected and will never be overwritten by an automatic refresh.

---

## The 30-day refresh cycle

Providers update their data over time. New editions are added, descriptions are improved, cover art is refreshed. The Engine automatically re-queries providers for all items in your library every 30 days to pick up these improvements.

You can also trigger a manual refresh at any time. In the Review Queue, select one or more items and click **Sync Now** in the floating action bar. This immediately re-runs both enrichment stages for the selected items.

## Related

- [Providers Reference](../reference/providers.md)
- [Configuration Reference](../reference/configuration.md)
- [How Two-Stage Enrichment Works](../explanation/how-hydration-works.md)
