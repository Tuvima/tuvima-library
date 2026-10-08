---
title: "How the Entire Pipeline Works"
description: "Follow a file's complete journey from detection through enrichment, scoring, and organization and understand why each stage exists."
audience: "user"
category: "explanation"
product_area: "pipeline"
tags:
  - "pipeline"
  - "ingestion"
  - "hydration"
  - "scoring"
  - "organization"
---
When you drop a file into a watched folder, Tuvima Library does much more than rename it. The Engine verifies the file, reads what it can from the file itself, looks for trustworthy external matches, decides what metadata wins, and only then decides whether the item is ready for the main browse surfaces and the organised library.

This page tells the full story end to end.

---

## The big picture

```
New file appears
  -> settle and lock check
  -> fingerprint
  -> scan and media-type classification
  -> safe staging on disk
  -> Stage 1 Retail
  -> Stage 2 Wikidata
  -> Quick Hydration
  -> metadata cascade
  -> artwork settlement
  -> browse readiness gate
  -> organisation and Stage 3 enrichment
```

Two important ideas sit underneath the whole design:

- **The main browse surfaces is not the same as the organised library on disk**
- **A match must be safe before the pipeline treats it as truth**

---

## Stage 0: Ingestion

The first phase is about making the file safe to work with.

The Engine:

- waits for the copy to finish
- confirms the file is no longer locked
- computes a SHA-256 fingerprint
- reads embedded metadata through the correct processor
- classifies ambiguous formats such as MP3 and MP4 when needed

The file is then moved into the staging area so the rest of the pipeline can work without touching your final organised folders too early.

Staging is a safety mechanism, not a user-facing "ready" signal.

---

## Stage 1: Retail matching

Retail/catalog matching is the first identity stage. The Engine queries active providers in the order declared by `config/pipelines.json`: Apple for books and audiobooks, TMDB for movies, TheTVDB for TV, Comic Vine for comics, and for music MusicBrainz first for identity followed by Apple for artwork and retail enrichment. TMDB also supplies slower Stage 3 movie and TV artwork, while LRCLIB/SubDL are text-track providers rather than identity sources.

Retail does three important jobs:

- it finds likely external candidates
- it gathers cover art and descriptions
- it collects bridge IDs that make Wikidata resolution precise

### The precision-first gate

Retail matching is intentionally conservative:

- **`>= 0.90`** can be auto-accepted
- **`0.65` to `< 0.90`** is ambiguous and goes to review
- **`< 0.65`** is treated as too weak to trust

Before auto-accepting, the Engine also checks for contradictions:

- creator mismatches can force review
- grouped TV needs exact show, season, and episode agreement
- grouped music needs track number or duration corroboration
- cover similarity can help, but it cannot save a weak text match

That stricter Retail stage is what improves match quality.

---

## Stage 2: Wikidata resolution

Wikidata runs after Retail, not in parallel with it.

The reason is simple: Retail provides the bridge IDs that make Wikidata precise.

Examples:

- ISBN for books
- TMDB ID for movies and TV
- MusicBrainz IDs for music

Without a successful Retail step, the Engine does not treat Wikidata as an automatic fallback path. That prevents text-only guesses from becoming canonical identity.

### QID Not Found

Sometimes Retail succeeds but Wikidata still cannot resolve a trustworthy QID.

When that happens, the item lands in a **QID Not Found** style outcome:

- it stays marked at the Wikidata step
- the missing QID is explicit
- the item can still remain usable and visible in the main browse surfaces if its title, media type, and artwork state are settled

This is a deliberate precision-preserving choice.

---

## Quick Hydration and the metadata cascade

By this point the Engine may have metadata from:

- the file itself
- one or more retail providers
- Wikidata
- later AI or enrichment helpers

Those sources will not always agree. The Priority Cascade resolves each field using the project rules:

- user locks win first
- configured field priorities win next
- Wikidata is the default authority for canonical structured facts
- otherwise the highest-confidence remaining claim wins

This lets the system combine the strengths of different sources instead of pretending one source is always right about everything.

---

## Artwork settlement and browse readiness

The main browse surfaces has its own quality gate.

An item is not shown in the main browse surfaces just because the file exists or because Retail started. It appears only when all of these are true:

- the title is not a placeholder
- the media type is resolved
- artwork is settled as either `present` or explicitly `missing`

That last point matters. The Engine now distinguishes between:

- artwork is present
- artwork is still pending
- artwork was checked and is missing

Items that fail this gate remain visible in **Activity**, **Review**, and the **Review Queue**, but they are held back from the main browse surfaces until the story is trustworthy.

Managed artwork is stored under `.data/assets/...` and indexed through database records such as `entity_assets` and `persons.local_headshot_path`. Sidecar images beside media files are optional exports only.

---

## Stage 3: Organisation on disk and deeper enrichment

Organisation is a separate decision from browse visibility.

The Engine can show an item in the main browse surfaces once the quality gate is satisfied, while filesystem promotion still follows the wider organisation rules and confidence thresholds.

The classic promotion gate is still about overall confidence and library organisation safety. For many items that means:

- high-confidence items are promoted automatically
- weaker items stay staged until review or later enrichment makes the decision safer

So there are really two questions:

1. Is this item ready to be shown in the main browse surfaces?
2. Is this item ready to be promoted into the organised library structure?

Those questions are related, but they are no longer the same thing.

After the fast identity path, Stage 3 enrichment expands people, fictional entities, narrative roots, universe relationships, extra artwork, lyrics/subtitles, summaries, and other non-blocking details.

---

## Where review fits in

Review is not a side path. It is part of the design.

Items go to review when the Engine decides that guessing would be worse than waiting. That includes:

- ambiguous Retail candidates
- missing or conflicting identity clues
- multiple Wikidata candidates
- a useful item with no trustworthy QID yet

The Review Queue and Review surfaces exist so the system can stop at the right moment instead of silently creating bad matches.

---

## How the UI represents the pipeline

The Dashboard uses the same three stages across Home, Read, Watch, Listen, Collections, Search, detail pages, and Review Queue:

| Stage | Meaning |
|---|---|
| **Retail** | Practical provider matching and bridge ID collection |
| **Wikidata** | Canonical identity resolution |
| **Enrichment** | Follow-up metadata, people, images, and relationships |

The readiness label gives the plain-English answer:

- **Pending artwork**
- **Needs review**
- **Ready**

That shared projection is what keeps list views, detail drawers, and overview counts aligned.

---

## Why the pipeline is designed this way

The design is intentionally more careful than "find a title and move on."

It exists to protect the library from:

- weak provider matches
- wrong canonical identities
- missing or misleading artwork
- different screens telling different stories about the same item

The result is a system that surfaces items a little later, but with much higher trust.

## Related

- [How File Ingestion Works](how-ingestion-works.md)
- [How Two-Stage Enrichment Works](how-hydration-works.md)
- [How the Review Queue Works](../guides/resolving-reviews.md)
- [Ingestion, Identity, and Enrichment Pipeline](../architecture/ingestion-identity-enrichment-pipeline.md)
- [Ingestion Pipeline Architecture](../architecture/ingestion-pipeline.md)
- [Scoring and Cascade Architecture](../architecture/scoring-and-cascade.md)
