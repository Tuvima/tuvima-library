---
title: "How the Priority Cascade Works"
description: "Understand how Tuvima chooses canonical metadata when sources disagree."
audience: "user"
category: "explanation"
product_area: "scoring"
tags:
  - "metadata"
  - "scoring"
  - "cascade"
---
A single audiobook might have metadata coming from several places: the M4B file itself, Apple API, and Wikidata. They often disagree. Apple might spell the author's name differently than Wikidata. The EPUB or audio file might have a series name the retailer doesn't know about.

Which one is right? The Priority Cascade answers that question - consistently, transparently, and in a way you can override when you need to.

---

## Everything is a Claim

The foundation of the system is the concept of a **Claim**. Every piece of metadata is a Claim: a triple of (source, value, confidence).

When the Engine reads an EPUB file and finds the author name "Frank Herbert", that's a Claim:
- Source: `epub_processor`
- Value: `Frank Herbert`
- Confidence: `0.85`

When Apple API returns the same book and lists the author as "Frank Herbert" with an Apple Books author ID, that's another Claim:
- Source: `apple_api`
- Value: `Frank Herbert`
- Confidence: `0.90`

When Wikidata returns the Wikidata item for Frank Herbert (Q159378), that becomes yet another Claim:
- Source: `wikidata_reconciliation`
- Value: `Frank Herbert` (with QID Q159378)
- Confidence: `0.95`

Claims accumulate from all sources and are **never deleted** - only superseded. This means the Engine always has a complete history of where every piece of data came from and how confident each source was. If you ever want to understand why the Engine chose a particular value, the full claim history is there.

---

## The Four Tiers

The cascade evaluates four tiers in strict order. The first tier that produces a winning claim for a given field is used. Lower tiers are only reached if higher tiers don't apply.

### Tier A - User Locks (always wins)

If you manually lock a field from a detail page, media row, or Review Queue item, your value wins at confidence 1.0. Full stop. No source - not even Wikidata - overrides a user lock.

User locks are the override of last resort. The Engine is designed to be right on its own, so you should rarely need them. But when you do - when you know something the Engine doesn't, or when a source has genuinely wrong data - a lock is absolute.

### Tier B - Per-Field Provider Priority

You can configure specific providers to be the preferred authority for specific fields. This is done in Settings under Providers.

Examples of sensible Tier B configurations:
- Always use Apple API for cover art (their images are consistently high quality)
- Always use TMDB for movie poster images
- Always use MusicBrainz for track duration

Tier B lets you express domain knowledge about which providers are trustworthy for which data types, without having to intervene on individual items.

Tier B priorities can be configured at two levels: per-media-type overrides in `config/pipelines.json` (checked first), and global overrides in `config/field_priorities.json` (checked second).

### Tier C - Wikidata Authority

For fields without a Tier B override, Wikidata claims win when present. This is the heart of the cascade's design philosophy.

Wikidata is the authority for **factual data**: canonical title, author, year of first publication, genre, series membership, franchise relationships, cast, crew. These are objective facts that Wikidata maintains with community oversight and citation requirements.

Retail providers like Apple API have richer images and descriptions, but their structured data can be inconsistent - reprint dates rather than original publication dates, house style author name formatting, regional variations. Wikidata's data is more carefully curated and more stable.

By default, if Wikidata has an opinion about a field, Wikidata wins.

**The author exception:** For the `author` field only, there is one exception to Wikidata's authority. When a non-Wikidata claim (typically from the file's embedded metadata) has strictly higher confidence than the best Wikidata P50 author claim, the higher-confidence claim wins. This handles pen names - a file might carry "Richard Bachman" as the author at confidence 0.95, while Wikidata returns "Stephen King" via P50 at the deliberately reduced confidence of 0.75. The pen name embedded in the file should win, because it reflects the author's creative intent for that specific edition. For all other fields, Wikidata wins unconditionally in Tier C.

### Tier D - Confidence Cascade

When no higher tier applies (no user lock, no Tier B configuration, no Wikidata claim), the claim with the highest confidence score wins.

Confidence scores are set by each provider and processor based on how reliable their data typically is for that field type. The file's own embedded metadata often scores lower than a confirmed retail match, which scores lower than a Wikidata-verified property.

---

## Special Rules

Beyond the four tiers, a few additional mechanisms affect how scores are calculated.

### Field Count Scaling

A file with five rich metadata fields (title, author, year, series, ISBN) is a much stronger match candidate than a file with only a title. To reflect this, files with fewer fields get a penalty applied to their raw confidence score.

The scale is roughly:
- 1 field: ~33% of raw confidence
- 3 fields: ~66%
- 5+ fields: full confidence

This prevents near-empty files from auto-promoting to high-confidence status on a title match alone.

### Conflicted Fields

When two claims for the same field are within 0.05 confidence of each other - close enough that neither is clearly better - the field is marked **Conflicted**. Detail pages and the shared media editor surface conflicted fields for your review rather than silently picking one.

You'll see conflicted fields highlighted in the Claims section of the detail drawer. You can pick the value you prefer, which applies a Tier A user lock for that field.

### Stale Claim Decay

Claims lose 20% of their confidence after 90 days. This is designed to work in tandem with the 30-day enrichment refresh cycle.

When fresh data arrives from a provider during a refresh, it competes against the now-decayed old claims. The fresh data wins more easily, so the library stays current without requiring manual intervention. Old data is deprioritized gracefully rather than abruptly deleted.

---

## Why This Design?

It would be simpler to just pick one authoritative source and use it for everything. But no single source is best at everything:

- Retail providers have excellent cover art and descriptions, but inconsistent structured data
- Wikidata has authoritative structured data, but no cover art
- The embedded file metadata is the only source for data unique to your copy (your personal notes, your narrator preference, your file organization)
- You might have domain knowledge that no automated source could have

The cascade respects all of these without asking you to manage them manually. Wikidata handles facts. Retail handles presentation. Your files contribute what only they know. And you hold the override whenever you need it.

The append-only claim history means nothing is ever lost. If a future provider produces better data, it can win on confidence - but the old claims are still there if you want to audit what happened.

---

For the technical details of the cascade implementation - scoring weights, configuration format, provider priority configuration, and the full field resolution algorithm - see the [architecture deep-dive](../architecture/scoring-and-cascade.md).

## Related

- [How the Entire Pipeline Works](how-the-pipeline-works.md) - end-to-end pipeline overview
- [Priority Cascade Engine](../architecture/scoring-and-cascade.md)
- [How to Resolve Items That Need Review](../guides/resolving-reviews.md)
- [Database Schema Reference](../reference/database-schema.md)
