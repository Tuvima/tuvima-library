---
title: "How Two-Stage Enrichment Works"
description: "Learn how retail providers and Wikidata combine to enrich media after ingestion."
audience: "user"
category: "explanation"
product_area: "providers"
tags:
  - "hydration"
  - "providers"
  - "wikidata"
---
Once a file has been scanned and classified, the Engine starts enrichment. This is where cover art, descriptions, bridge IDs, canonical identity, people, and relationships are filled in.

Hydration is split into two identity stages plus a follow-up enrichment stage:

- **Stage 1: Retail**
- **Stage 2: Wikidata**
- **Stage 3: Enrichment and universe expansion**

The first two stages decide what the item actually is. Stage 3 makes the item richer after identity is safe enough.

---

## Why it is split in two

No single source is good at everything.

Retail providers are strong at:

- cover art
- descriptions
- ratings
- edition-specific hints
- bridge identifiers such as ISBN, TMDB ID, and MusicBrainz IDs

Wikidata is strong at:

- canonical identity
- structured facts
- series and franchise relationships
- linked people
- stable cross-language identifiers

The design is simple:

- Retail finds the best practical match and the IDs that make the next step precise
- Wikidata turns those IDs into canonical identity

---

## Stage 1: Retail

Retail providers run first. The exact strategy depends on media type, but the goal is always the same: find the best practical external match without guessing.

Retail produces:

- cover art
- descriptions
- ratings
- bridge IDs
- extra evidence used for ranking

The active Stage 1 provider matrix is:

| Provider | Use |
|---|---|
| Apple | Books, audiobooks, and music artwork/retail enrichment |
| TMDB | Movie identity; TV fallback and cross-reference; video metadata, people seeds, and Stage 3 artwork |
| TheTVDB | Primary TV show and episode identity when connected |
| Comic Vine | Comics |
| MusicBrainz | Music identity lookup before Apple enrichment |
| LRCLIB / SubDL | Lyrics/subtitles/text tracks, not identity |

For the exact lookup fields and scoring metrics used by each media type, see [How to Configure Metadata Providers](../guides/configuring-providers.md#retail-lookup-inputs-by-media-type).

### The stricter confidence gate

Retail matching is now precision-first:

- **`>= 0.90`**: candidate can be auto-accepted
- **`0.65` to `< 0.90`**: candidate is treated as ambiguous and sent to review
- **`< 0.65`**: candidate is too weak and is treated as no match

That stricter gate exists to stop weak matches from contaminating later stages.

### Extra safety rules

A high numeric score is not enough on its own. The Engine also applies contradiction checks before auto-accept:

- weak creator agreement can cap a result to review
- grouped TV matching must agree on show, season, and episode
- grouped music matching must be supported by track number or duration
- cover similarity can boost an already plausible candidate, but it cannot rescue weak text evidence

This is why Stage 1 is more conservative than older documentation may suggest.

---

## Stage 2: Wikidata

Stage 2 only runs after Stage 1 has produced enough signal to move forward.

In practice that means:

- no good Retail match means no automatic Wikidata attempt
- real bridge IDs from Retail are the preferred path into Wikidata

Typical bridge IDs include:

- ISBN for books and some comics
- ASIN or Apple Books ID for audiobooks
- TMDB ID for movies and TV
- MusicBrainz IDs for music, with Apple IDs retained as secondary provider links and bridge hints

When a bridge ID resolves successfully, the Engine fetches canonical properties such as title, creator, year, genre, series, and relationship data.

For the exact bridge IDs and hints sent to Wikidata for each media type, see [How to Configure Metadata Providers](../guides/configuring-providers.md#wikidata-inputs-by-media-type).

### What if no QID is found?

If Retail succeeded but Wikidata still cannot resolve a good QID, the outcome is **QID Not Found**.

That is a real terminal state, not a silent failure. The item may still be usable and visible in the main browse surfaces if it passes the browse readiness gate:

- good title
- resolved media type
- settled artwork outcome

The pipeline stays marked at the Wikidata step so the missing QID is visible.

---

## Quick Hydration and artwork truth

Quick Hydration is the fast path after identity: it stores the core values needed for browsing and persists accepted managed artwork. Artwork is not treated as "maybe there somewhere" anymore. The system tracks whether artwork is:

- **present**
- **pending**
- **missing**

The main browse surfaces waits for that result to settle. An item is not considered ready just because a provider search started. It becomes ready when artwork is actually present, or when the artwork pass has explicitly finished and confirmed that no cover is available.

That makes cover display and readiness much more honest.

Managed artwork and headshots live under `.data/assets/...` and are referenced from `entity_assets` or person/entity records. Sidecar artwork beside media files is an optional export mirror, not the Engine's canonical store.

---

## Stage 3: Enrichment and universe expansion

Once Retail and Wikidata have done enough to identify the item, Stage 3 enrichment continues with:

- people resolution
- relationship discovery
- extra images
- summaries and vibe tags
- universe and graph data
- lyrics and subtitles from text-track providers
- TMDB poster, backdrop, logo, season, episode, network, and studio artwork where TMDB IDs are available

These deeper steps make the item richer, but they are not the same thing as deciding whether the item is the right match.

---

## About two-pass mode

There is an optional **two-pass** mode, but it is **not the default runtime path** right now. In the current shipped config, `two_pass_enabled` is `false`.

When two-pass mode is enabled:

- **Pass 1** focuses on fast identity and core artwork
- **Pass 2** runs later for deeper follow-up enrichment

When two-pass mode is disabled, the normal identity pipeline runs inline, while scheduled follow-up enrichment still happens separately where appropriate.

---

## Refresh and retries

Hydration is not a one-time event. The Engine can revisit items when:

- provider data changes
- Wikidata gains a new or corrected entry
- stale metadata is due for refresh
- a previously uncertain item now has enough evidence to resolve

That is why an item that was review-only last month can become a clean match later without manual work.

## Related

- [How the Entire Pipeline Works](how-the-pipeline-works.md)
- [How the Review Queue Works](../guides/resolving-reviews.md)
- [Ingestion, Identity, and Enrichment Pipeline](../architecture/ingestion-identity-enrichment-pipeline.md)
- [Hydration Pipeline, Provider Architecture and Enrichment Strategy](../architecture/hydration-and-providers.md)
- [Providers Reference](../reference/providers.md)
