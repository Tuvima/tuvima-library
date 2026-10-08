---
title: "Configure metadata providers"
description: "Connect metadata providers, verify saved credentials, and understand how matching and enrichment use them."
audience: "administrator"
category: "guide"
product_area: "providers"
status: current
---

# Configure metadata providers

Connect the services Tuvima Library uses for titles, artwork, people, and other catalogue information. Allow a few minutes per provider, plus any account registration.

Providers can receive identifiers and search hints from catalogue media. View personal media follows a separate local path and does not use these providers.

## Check what needs credentials

| Provider | Use | Credentials |
| --- | --- | --- |
| Apple API | Books, audiobooks, and music metadata/artwork | No key |
| MusicBrainz | Music recording, release, and artist identity | No key |
| Wikidata and Wikipedia | Structured relationships and descriptions | No key |
| LRCLIB | Music lyrics and timed lyrics | No key |
| TMDB | Movies, TV fallback, and artwork | Your own TMDB API key |
| TheTVDB | Primary TV show and episode identity when connected | Approved project key; subscriber PIN if required |
| Comic Vine | Comic issue and series metadata | Your own API key |
| SubDL | Optional subtitle downloads | Your own API key |

Enabled configuration and provider availability still determine what runs. Tuvima does not ship a TMDB or TheTVDB key or proxy.

## Connect a provider

1. Open **Settings → Metadata Providers → Providers** (`/settings/metadata/providers`).
2. Find the provider, open its configuration, and choose **Connect provider**.
3. Follow its account and credential instructions.
4. Enter the required values.
5. Choose **Verify & connect**.
6. Confirm the saved connection result before importing a large batch.

Use **Test connection** to check saved configuration later. A title lookup returning no match does not by itself mean the provider is offline.

Provider-specific help is available from [TMDB](https://www.themoviedb.org/settings/api), [TheTVDB](https://thetvdb.com/api-information), [Comic Vine](https://comicvine.gamespot.com/api/), and [SubDL](https://subdl.com/developers).

## Add optional subtitles

Embedded and local subtitle files work without SubDL.

1. Generate an API key in your SubDL account.
2. Connect SubDL from the Providers page.
3. Verify the key and check a matched movie or owned TV episode.

Tuvima uses TMDB identity for movie searches. TV searches require a verified TheTVDB-to-TMDB episode crosswalk. Missing or conflicting links leave automatic downloads pending rather than guessing.

An invalid key, quota limit, or outage does not remove stored subtitles.

## Understand the matching flow

Open **Settings → Providers → Ingestion Flow** for the active, read-only media-specific order.

Retail lookup establishes a likely work and gathers artwork and bridge identifiers. Wikidata uses those identifiers for canonical identity and relationships. Later enrichment can add people, universe links, lyrics, subtitles, and artwork variants.

A successful retail match can remain useful without a Wikidata result. Provider or local grouping can still create a lane shelf. Broader Automatic Collections require trusted shared relationships.

## Choose languages and artwork

Each provider offers **Language strategy**. Keep its default unless results use the wrong language. See [language preferences](language-setup.md) for the available strategies.

Open an item's shared editor to choose or upload artwork. Managed images live in the application asset store. Optional exports beside media files depend on storage policy. User-selected artwork is protected from automatic replacement.

## Keep credentials private

Provider definitions live in `config/providers/*.json`. Long-lived keys belong in ignored overlays at `config/secrets/{provider}.json`.

A blank key in a base provider file can still have an effective secret overlay. Check both places before assuming a key was removed. Saved connection checks contain safe status text, not the key.

Store and back up secrets separately: normal recovery archives exclude them. Do not paste keys into issue reports or commit them to code history.

<details>
<summary>Technical details: lookup inputs, precedence, and refresh</summary>

## Retail lookup inputs by media type

Retail lookup establishes identity before Wikidata alignment. It searches the configured provider chain, then scores returned candidates against local file evidence. Books use Apple as their retail catalogue source. Music uses a bounded, configuration-driven chain: MusicBrainz tries identifiers and staged text searches first; Apple can supply fallback identity; and an accepted Apple fallback schedules one configured MusicBrainz reconciliation attempt using Apple-normalized hints.

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

Wikidata alignment follows retail lookup. It is gated behind retail lookup: the Wikidata bridge worker only processes items that reached `RetailMatched` or `RetailMatchedNeedsReview`. Items with no safe retail match are not sent to Wikidata as a broad title-only fallback.

Wikidata alignment requires at least one real bridge ID. Title, creator, year, series, album, artist, and language hints help the resolver rank or roll up results, but they do not bypass the bridge-ID requirement.

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

Open **Settings → Metadata Providers → Ingestion Flow** to see the active order for each media type. The page labels providers as Primary, Secondary, Fallback, Required, or Optional and shows the outputs contributed at each stage. It is intentionally read-only so inspecting the flow cannot accidentally change ingestion behavior.

Provider execution order remains media-scoped in `config/pipelines.json`. Sequential chains run in listed order, passing bridge IDs forward. For music, the default configuration assigns MusicBrainz the `identity` role and Apple the `enrichment` role with `requires_identity: true` plus `use_as_identity_fallback: true`. Apple's `accepted_transition` points back to MusicBrainz for one reconciliation attempt only when Apple supplied the fallback identity. `max_provider_attempts` is an absolute safety budget. Query clauses, candidate paths, nested release constraints, creator-list behavior, transition hint fields, and retry counts all live in validated JSON configuration rather than provider-name branches in the worker.

Wikidata appears in the same provider inventory as every other provider. Ingestion Flow shows its required canonical-identity role separately from optional post-match providers such as LRCLIB and SubDL.

---


The default refresh interval is 30 days in `config/hydration.json`; it is configurable. A refresh depends on the relevant provider and job being available.

OpenSubtitles and Fanart.tv are no longer active providers. Keep previously downloaded subtitles and artwork. After replacing an old connection, revoke its credential with that provider and remove unused ignored `config/secrets/opensubtitles.json` or `config/secrets/fanart_tv.json` files. Generic `fanart.jpg` sidecars are unrelated and can remain.

</details>

## Next steps

- [Add media](adding-media.md).
- [Read the provider reference](../reference/providers.md).
- [Check configuration keys](../reference/configuration.md).
- [Troubleshoot failed lookups](troubleshooting.md).
