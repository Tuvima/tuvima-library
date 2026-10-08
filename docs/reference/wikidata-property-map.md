---
title: "Wikidata Property Map"
description: "Reference table of 40+ Wikidata properties used for metadata enrichment, with P-codes, claim keys, and confidence levels."
audience: "developer"
category: "reference"
product_area: "providers"
tags:
  - "wikidata"
  - "properties"
  - "stage-3"
---
This table describes properties actually requested by `config/providers/wikidata_reconciliation.json`. Entity-valued properties retain both a localized display label and a QID in `canonical_value_arrays`; Smart Collection rules persist the QID as identity.

---

## Core Identity Properties

| P-code | Claim Key | Scope | Confidence | Notes |
|--------|-----------|-------|------------|-------|
| P31 | `instance_of` | Work | 0.9 | Entity-valued |
| P1476 | `title` | Work | 0.9 | Monolingual text |
| P179 | `series` | Work | 0.9 | Entity-valued |
| P1545 | `series_position` | Work | 0.9 | Numeric portion extracted |
| P8345 | `franchise` | Work | 0.9 | Entity-valued |
| P155 | `preceded_by` | Work | 0.8 | Entity-valued |
| P156 | `followed_by` | Work | 0.8 | Entity-valued |
| P577 | `year` | Work | 0.9 | Year extracted from ISO date |

---

## People Properties - Work Scope

| P-code | Claim Key | Scope | Confidence | Notes |
|--------|-----------|-------|------------|-------|
| P50 | `author` | Work | 0.9 | Entity-valued |
| P110 | `illustrator` | Work | 0.9 | Entity-valued |
| P57 | `director` | Work | 0.9 | Entity-valued |
| P161 | `cast_member` | Work | 0.9 | Entity-valued, capped at 20 |
| P987 | `narrator` | Work | 0.9 | Entity-valued |
| P725 | `voice_actor` | Work | 0.9 | Entity-valued |
| P58 | `screenwriter` | Work | 0.9 | Entity-valued |
| P86 | `composer` | Work | 0.9 | Entity-valued |

---

## People Properties - Person Scope

| P-code | Claim Key | Scope | Confidence | Notes |
|--------|-----------|-------|------------|-------|
| P800 | `notable_work` | Person | 0.85 | Entity-valued |
| P18 | `headshot_url` | Person | 0.9 | Commons URL transform |
| P106 | `occupation` | Person | 0.85 | Entity-valued |
| P742 | `pseudonym` | Person | 0.85 | Entity-valued |
| P1773 | `attributed_to` | Person | 0.85 | Entity-valued |

---

## Lore & Narrative Properties

These properties are consumed by the current reconciliation and universe pipeline.

| P-code | Claim Key | Scope | Confidence | Notes |
|--------|-----------|-------|------------|-------|
| P840 | `narrative_location` | Work | 0.8 | Entity-valued |
| P793 | `narrative_event` | Work | 0.8 | Entity-valued significant event, used for Stage 3 event discovery |
| P674 | `characters` | Work | 0.8 | Entity-valued |
| P921 | `main_subject` | Work | 0.8 | Entity-valued |
| P1434 | `fictional_universe` | Work | 0.8 | Entity-valued |
| P144 | `based_on` | Work | 0.8 | Entity-valued |
| P4584 | `first_appearance` | Work | 0.8 | Entity-valued |
| P451 | `partner` | Work | 0.9 | Entity-valued |
| P69 | `educated_at` | Work | 0.9 | Entity-valued |
| P945 | `allegiance` | Work | 0.9 | Entity-valued |
| P39 | `position_held` | Work | 0.9 | Entity-valued |
| P607 | `conflict` | Work | 0.9 | Entity-valued |

### Fictional entity graph scope

Stage 3 uses first-class `Character`, `Location`, `Organization`, `Event`, and `Object` entity types. Entity-valued statements retain their individual Wikidata qualifiers and provenance instead of being flattened into one canonical value. P1080 can replace a work-derived universe QID, but a missing resolved P1080 label never erases the existing owned-work label.

| P-code | Claim key | Entity scope | Notes |
|--------|-----------|--------------|-------|
| P1080 | `narrative_universe` | All fictional entity types | Authoritative narrative-universe identity |
| P1441 | `present_in_work` | All fictional entity types | First-class work appearance with contextual qualifiers |
| P4584 | `first_appearance` | Character, Location, Object | First appearance work link |
| P585 | `point_in_time` | Event | Time-indexed event point |
| P580 / P582 | `start_time` / `end_time` | Event and relationships | Temporal scope |
| P276 | `event_location` | Event | Event location edge |
| P710 | `participant` | Event | Event participant edge |
| P828 / P1542 | `cause` / `effect` | Event | Causal event edges |
| P5800 | `narrative_role` | Object and work links | Role within a work or narrative |

P171 is taxon parent and is deliberately not treated as a fictional-character species relationship. P169 is `chief_executive_officer`; it must not be represented as a generic `head_of` relationship. Statement qualifiers P10663 (work scope), P580/P582/P585 (time), P4895 (narrative time index), and P7528 (spoiler boundary) are normalized into queryable graph facts and appearance rows with source/provenance retained. Direct scalar Event P585/P580/P582/P4895 values are retained as canonical Stage 3 facts and projected into the in-universe Timeline; they are not discarded merely because they lack an entity target.

## Structured Discovery Properties

| P-code | Claim key | Cardinality | Typical scope |
|--------|-----------|-------------|---------------|
| P166 | `award_received` | Many | Work/root Work |
| P1411 | `award_nominated` | Many | Work/root Work |
| P495 | `country_of_origin` | Many | Work/root Work |
| P407 | `language` | Many | Work/root Work |
| P364 | `original_language` | Many | Work/root Work |
| P272 | `production_company` | Many | Movie or TV root Work |
| P449 | `network` | Many | TV root Work |
| P123 | `publisher` | Many | Literary Work; audiobook Edition |
| P264 | `record_label` | Many | Album/root Work |
| P2408 | `set_in_period` | Many | Work/root Work |
| P915 | `filming_location` | Many | Movie or TV root Work |

Award and nomination categories are also extended with P361 in one batched, cached request. A declared parent becomes `award_family` or `nomination_family`; no family is guessed when Wikidata has no reliable parent.

### Knowledge state and refresh

`enrichment.structured_discovery_metadata` version `1.0` records a state per field. A successful lookup with no statement is `no_result` (unknown), not false. Smart Collections expose `is known`, `is unknown`, and `has any value`; `is not` requires a known value. The enrichment schedule detects libraries without this capability version and queues a normal universe refresh, so existing items are backfilled without re-import. Later regular Stage 3 refreshes replace the current Wikidata observation set while retaining superseded claims as provenance history.

### Subjective AI attributes

Themes, mood, vibe, and content warnings remain local-AI attributes. They are deliberately not populated from Wikidata and can be combined with structured facts in the same collection rule set.

---

## Bridge Identifiers

Bridge identifiers are external IDs used to cross-reference Wikidata entities with retail and specialist providers. They enable precise QID resolution in Stage 2 of the hydration pipeline.

### Books

| P-code | Identifier | Notes |
|--------|-----------|-------|
| P3861 | Apple Books ID | |
| P212 | ISBN | |
| P1566 | ASIN | |
| P2969 | Goodreads ID | |
| P244 | Library of Congress ID | |

### Movies & TV

| P-code | Identifier | Notes |
|--------|-----------|-------|
| P4947 | TMDB ID | Used for TMDB identity bridging and image enrichment |
| P345 | IMDb ID | |
| P9385 | JustWatch ID | |
| P1712 | Metacritic ID | |
| P6127 | Letterboxd ID | |
| P2638 | TV.com ID | |

### Comics & Anime

| P-code | Identifier | Notes |
|--------|-----------|-------|
| P3589 | GCD Series ID | Grand Comics Database |
| P11308 | GCD Issue ID | Grand Comics Database |
| P5905 | ComicVine ID | |
| P4084 | MAL Anime ID | MyAnimeList |
| P4087 | MAL Manga ID | MyAnimeList |

### Music & Audio

| P-code | Identifier | Notes |
|--------|-----------|-------|
| P434 | MusicBrainz ID | Used for music identity bridging |
| P1902 | Spotify ID | |
| P1953 | Discogs ID | |
| P3398 | Audible ID | |

---

## Social Pivot Properties - Person Scope

Used to populate actionable social links on Person entities (see Universe Graph & Chronicle Engine, section 3.9).

| P-code | Claim Key | Platform |
|--------|-----------|----------|
| P2003 | `instagram` | Instagram |
| P2002 | `twitter` | X / Twitter |
| P7085 | `tiktok` | TikTok |
| P4033 | `mastodon` | Mastodon |
| P856 | `website` | Official website |

---

## Notes on Confidence Levels

Confidence values indicate the strength of trust assigned to a claim when it enters the Priority Cascade:

| Value | Meaning |
|-------|---------|
| 0.9 | High confidence - Wikidata is the primary authority for this field |
| 0.85 | Good confidence - used for person-scope properties and identifiers |
| 0.8 | Moderate confidence - lore/narrative properties where Wikidata coverage varies |

For the `author` field specifically, a non-Wikidata claim with strictly higher confidence than the best Wikidata P50 claim wins in Tier C of the cascade (to honour deliberately reduced P50 author confidence of 0.75 and preserve embedded pen names at 0.95). All other fields follow standard Tier C Wikidata authority.

See [`docs/architecture/scoring-and-cascade.md`](../architecture/scoring-and-cascade.md) for the full Priority Cascade rules.
