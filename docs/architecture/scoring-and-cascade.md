---
title: "Priority Cascade Engine"
description: "Deep technical documentation for metadata claims, weights, thresholds, and conflict resolution."
audience: "developer"
category: "architecture"
product_area: "scoring"
tags:
  - "scoring"
  - "metadata"
  - "conflicts"
---
## Purpose

When multiple sources disagree about a metadata field - title, author, year, cover art - the Priority Cascade Engine resolves the dispute and produces a single canonical value for each field. Every piece of metadata is modeled as a **Claim**: a triple of (source, value, confidence). Claims accumulate from all sources. The cascade picks a winner per field.

---

## Claim Sources and Base Weights

| Source | Base confidence | Notes |
|---|---|---|
| File internal metadata (OPF, ID3) | 0.9 | High trust - embedded at creation time |
| Filename | 0.5 | Medium trust - often approximate or user-modified |
| External providers | Configurable per field | See per-field trust weights below |
| User lock | 1.0 | Absolute - see Tier A |

External providers declare per-field trust weights in their provider config files (`config/providers/`). These weights reflect how reliable that provider is for a specific kind of data - Wikidata carries franchise identifiers at weight 1.0, Apple API carries cover art at 0.85, and so on.

---

## Priority Cascade Tiers

Tiers are evaluated in order. The first tier that can resolve a field wins; lower tiers are not consulted for that field.

### Tier A - User Locks

User-locked claims always win, regardless of any provider or scoring result. A user-locked claim carries confidence 1.0 and is never overridden on any future re-score. This guarantee is absolute.

### Tier B - Per-Field Provider Priority

Some fields benefit from a specific provider rather than the default Wikidata-always-wins rule. When a field has an override in `config/field_priorities.json`, the cascade walks the provider priority list and returns the first provider that has a claim for that field. Tier C is skipped entirely for this field.

Example overrides from `config/field_priorities.json`:

| Field | Priority order | Reason |
|---|---|---|
| `description` | wikipedia, apple_api, wikidata_reconciliation | Rich Wikipedia summaries preferred over Wikidata one-liners |
| `cover` | apple_api, tmdb, wikidata_reconciliation | Retail providers have high-resolution commercial art |
| `rating` | apple_api, tmdb | Wikidata does not carry ratings |
| `biography` | wikipedia, wikidata_reconciliation | Rich Wikipedia bios for persons |

Fields not listed in the config default to Tier C (Wikidata authority).

### Tier C - Wikidata Authority

For any field without a Tier B override, Wikidata claims win unconditionally when present. Wikidata is the sole identity authority - every media item is identified by its Wikidata Q-identifier.

### Tier D - Confidence Cascade

When no Tier A, B, or C claim exists for a field, the highest-confidence claim across all remaining sources wins.

---

## Field Count Scaling

Files with very few metadata fields receive a confidence penalty to prevent inflated scores from near-empty files:

```
overallConfidence *= Math.Min(1.0, fieldCount / 3.0)
```

A file with only one field scores at approximately 1/3 of its raw confidence. A file with three or more fields is unaffected (multiplier = 1.0). This ensures corrupt or near-empty files are routed to staging for review rather than being auto-promoted.

---

## Retail identity matrices (October 2026 follow-up)

`RetailMatchScoringService` reads typed `scoring.scopes` from `config/pipelines.json`. Stage 1 single-item/grouped workers and editor searches share this evaluator. `config/pipeline-priority-defaults.json` contains the same shipped matrices for settings reset. Retail confidence is separate from canonical claim trust and Wikidata reconciliation scores.

| Media / scope | Weighted evidence | Eligibility gates | Bonuses | Penalties |
|---|---|---|---|---|
| Books / default | `title` 0.50 (zero), `author` 0.35 (zero-if-file-has), `year` 0.15 (redistribute) | format, not_derivative | `cover` +0.10, `exact_id` +0.35, `publisher` +0.05, `page_count` +0.05, `series_description` +0.05 | `language` −0.10 |
| Audiobooks / default | `title` 0.45 (zero), `author` 0.30 (zero-if-file-has), `narrator` 0.15 (redistribute), `year` 0.10 (redistribute) | format, not_derivative | `cover` +0.10, `exact_id` +0.35, `duration` +0.05, `series_description` +0.05 | `language` −0.10, `duration` −0.10 |
| Music / track | `title` 0.40 (zero), `author` 0.30 (zero-if-file-has), `album` 0.15 (redistribute), `duration` 0.10 (redistribute), `year` 0.05 (redistribute) | format | `cover` +0.10, `exact_id` +0.35, `track_disc` +0.05 | `language` −0.10, `duration` −0.15 |
| Music / album | `title` 0.50 (zero), `author` 0.35 (zero-if-file-has), `track_count` 0.10 (redistribute), `year` 0.05 (redistribute) | format | `cover` +0.10, `exact_id` +0.35 | `language` −0.10 |
| Movies / default | `title` 0.65 (zero), `year` 0.35 (redistribute) | format | `cover` +0.10, `exact_id` +0.35, `director` +0.05 | `language` −0.10, `year` −0.15, `runtime` −0.10 |
| TV / series | `title` 0.75 (zero), `year` 0.25 (redistribute) | format | `cover` +0.10, `exact_id` +0.35 | `language` −0.10 |
| TV / episode | `season_episode` 0.60 (zero-if-file-has), `title` 0.30 (redistribute), `year` 0.10 (redistribute) | format, show_title | `cover` +0.10 | `language` −0.10, `episode` −0.25, `season` −0.10 |
| Comics / issue | `series` 0.40 (zero), `issue` 0.40 (zero-if-file-has), `year` 0.10 (redistribute), `title` 0.10 (redistribute) | format | `cover` +0.10, `exact_id` +0.35, `writer` +0.05 | `language` −0.10 |

Each weighted matrix sums to 1 (tolerance 0.001). `redistribute` removes an unavailable optional comparison and normalizes the remaining active weights; its review row says Not provided. `zero` retains missing required evidence at zero. `zero-if-file-has` retains a required creator at zero when the file supplies one but the candidate does not. Missing or contradictory required creator evidence blocks automatic acceptance for books, audiobooks and music. Movie directors and comic writers are corroboration bonuses rather than creator weights. Genre has no retail confidence role.

At least two independent agreeing identity fields are required without an exact supported identifier. A known wrong media kind or track/album scope, derivative book, wrong show, or explicit conflicting episode/issue structure stays ineligible after all bonuses; a failed terminal gate caps the score at 0.50. Missing required evidence and insufficient corroboration cap at the configured review threshold. Exact IDs and cover similarity cannot override those caps. Provider kind aliases are evaluated when present; unknown kind is displayed as Not provided and relies on the provider adapter's existing media-specific result filtering. Placeholder file titles retain zero-score rejection.

Bonuses and penalties apply after weighted evidence, then the result is clamped and eligibility caps are applied last. Accept/review thresholds remain 0.90/0.65 in `config/hydration.json`; the retired global fuzzy weights no longer drive retail scoring. Comic issue numbers preserve fractional ordinals. Track duration uses seconds, accepts minute/second clocks, and treats a difference of up to 3 seconds as exact and up to 10 seconds as partial agreement. Book title comparisons use the text before a colon; movie original titles can corroborate localized titles; TV series names also require word overlap.

The additive nullable Contracts `field_scores` collection carries field key/label, score, effective weight, missing status, role, contribution, missing policy, verdict and file/candidate values. Durable candidate `score_breakdown_json` retains the complete same evidence. Gate and optional-missing rows are distinct from mismatches. Settings round-trip the typed matrices. No database migration, backfill, legacy scorer fallback or Like-to-Love conversion was added.

The follow-up's offline comparison uses 64 constructed deterministic fixtures and historical scorer/decider source from `75759857`. It is not a live-provider replay or the unavailable historical 77-case corpus; independent external review and live-provider precision estimates remain separate acceptance work.

## Wikidata Author Validation

When the Stage 2 Wikidata candidate is scored, the author from the file's embedded metadata is compared against the candidate's P50 (author) property:

| Condition | Score adjustment |
|---|---|
| Author similarity < 0.3 (clear mismatch) | 25 penalty |
| Candidate has no author properties (P50 absent) | 15 penalty |

These penalties apply on top of the base reconciliation score before the `wikidata_review_threshold` and `wikidata_auto_accept` gates are evaluated.

**Wikidata score thresholds** (configured in `config/scoring.json`):

| Key | Value | Meaning |
|---|---|---|
| `wikidata_review_threshold` | 55 | Below this score: item goes to review queue |
| `wikidata_auto_accept` | 95 | At or above this score and `match: true`: QID accepted automatically |

---

## Conflicted Fields

When two claims for the same field are too close in confidence to pick a clear winner, the field is marked **Conflicted** and surfaced to the user for manual resolution. The conflict threshold and epsilon are configured in `config/scoring.json`.

---

## Claim History

All claims are stored append-only. No claim is ever deleted or overwritten - only superseded by higher-priority claims. The full provenance trail for every field is always available.

---

## Auto-Link and Promotion Gates

The auto-link threshold (`auto_link_threshold`) in `config/scoring.json` governs when a scored file is automatically promoted from staging to the organised library:

- Files with `overallConfidence >= 0.85` or any user-locked claim are promoted automatically
- Files below the gate go to `.staging/low-confidence/` or `.staging/unidentifiable/` depending on their score

---

## Configuration Reference

All scoring parameters live in `config/scoring.json`:

| Key | Default | Purpose |
|---|---|---|
| `auto_link_threshold` | 0.85 | Confidence gate for automatic staging promotion |
| `conflict_threshold` | 0.60 | Below this, a field is not auto-resolved |
| `conflict_epsilon` | 0.05 | Maximum difference for two claims to be considered tied |
| `stale_claim_decay_days` | 90 | Claims older than this begin to decay |
| `stale_claim_decay_factor` | 0.8 | Multiplier applied to confidence of stale claims |
| `retail_auto_accept_threshold` | 0.90 | Retail match score threshold for automatic acceptance |
| `retail_ambiguous_threshold` | 0.65 | Retail match score threshold below which a match is discarded |
| `wikidata_review_threshold` | 55 | Wikidata reconciliation score below which item goes to review |
| `wikidata_auto_accept` | 95 | Wikidata reconciliation score at which QID is auto-accepted |

Per-field provider priority overrides live in `config/field_priorities.json`.

---

## Shared retail decision ownership

The identity matrices above are the only retail field evaluator. Multi-creator token comparison keeps the configured proportional/best-match policy. Worker-specific outcomes and audit metadata belong to `RetailCandidateScorer`; fields and missing-value math belong to `RetailMatchScoringService`.

### Worker-Level Retail Candidate Decisions

`RetailMatchScoringService` remains the shared field scoring implementation for automated retail matching and manual search. The Stage 1 worker now delegates worker-specific outcome metadata to `RetailCandidateScorer`: accepted, ambiguous, rejected, and failed decision labels; threshold path; rejection reasons; creator-evidence caps; weak-text cover-rescue rejection; and the score breakdown JSON persisted for candidate review.

Keep new field-level matching math in `RetailMatchScoringService`. Keep worker outcome thresholds and candidate audit metadata in `RetailCandidateScorer` so the durable worker stays a coordinator rather than the owner of scoring rules.

---

## Wikidata Candidate Ranking

`ReconciliationAdapter.FilterByMediaTypeAsync` applies multi-author matching against P50/P175 properties. Penalties:

| Condition | Penalty | Rationale |
|---|---|---|
| bestAuthorMatch < 0.3 | 35 | Strong author mismatch - likely wrong work |
| No P50/P175 properties at all | 40 | Entity has no author/performer data - highly suspect |

Score blending: 85% composite (type-aware scoring) / 15% original Wikidata API score. This ensures type filtering and author matching have strong influence over raw label-match scores.

---

## Pipeline Enforcement - No Retail, No Wikidata

Stage 2 (Wikidata) requires bridge IDs from Stage 1 (retail). If Stage 1 produces no match:
- The text-only Wikidata fallback is **removed** - no automatic text reconciliation bypass
- The item routes directly to the review queue with `RetailMatchFailed`

Bridge resolution guard: non-music Stage 2 requests require real bridge IDs from Stage 1. Title-only hints are not a Wikidata bypass; when no bridge ID is available, the item stays in the precision-preserving no-match/review path.

---

## AI and the Cascade

AI features in `MediaEngine.AI` improve the quality of inputs to the cascade - they do not replace it. The cascade determines all final canonical values.

| AI feature | Role in scoring |
|---|---|
| SmartLabeler | Cleans filenames before they are parsed into claims, producing better Tier D candidates |
| MediaTypeAdvisor | Classifies ambiguous file formats, emitting a high-confidence `media_type` claim |
| QidDisambiguator | Picks the best Wikidata candidate when the Reconciliation API returns multiple matches, accelerating Tier C resolution |
| BatchManifestBuilder | Reduces retail API calls during bulk ingestion, but does not change how claims are weighted or selected |

Wikidata remains the authority for all canonical data. AI accelerates and improves the matching process that feeds the cascade; the cascade itself is unchanged.

## Lineage-Aware Claim Routing (Phase 3)

Library Works form a hierarchy: a TV episode lives under a season under a show; a music track lives under an album; a comic issue lives under a series. Until Phase 3, every metadata claim a worker produced - *including* facts about the parent (the show's name, the album's release year, the series' description) - was written against the file on disk. The result was that show, season, and episode looked like the same row in the data store, and the Engine couldn't tell them apart.

Phase 3 introduces a small routing layer that decides which Work in the hierarchy each claim belongs to.

### Components

| Component | Layer | Role |
|---|---|---|
| `WorkLineage` (record) | Domain.Contracts | Walked chain `asset -> edition -> work -> parent -> root parent` returned by `IWorkRepository.GetLineageByAssetAsync`. `TargetForParentScope = RootParentWorkId` (TV episodes resolve up to the SHOW, not the season); `TargetForSelfScope = WorkId`. |
| `ClaimScope` (enum) | Domain.Constants | `Self` or `Parent`. New claim keys default to `Self`. |
| `ClaimScopeCatalog` | Domain.Constants | Single source of truth mapping `(claim_key, media_type)` -> `ClaimScope`. Container fields (`album`, `show_name`, `series`, `franchise`) and container bridge IDs (`apple_music_collection_id`, `tvdb_id`, etc.) declare `Parent`; per-media-type overrides handle context-sensitive cases (e.g. `year` is `Parent` for music but `Self` for movies; `director` is `Self` for TV episodes but `Self` for movies). |
| `WorkClaimRouter` | Storage.Services | Stateless splitter for bridge ID dictionaries and `MetadataClaim` lists. Used by Phase 3a/3b for `works.external_identifiers` writes. |
| `ScoringHelper.PersistAndScoreWithLineageAsync` | Providers.Services | Phase 3c entry point. Runs the existing per-asset persist+score path unchanged, then mirrors `Parent`-scoped claims into the parent Work's `metadata_claims` and `canonical_values` as a second pass. |

### Three-phase rollout

| Phase | Scope | Status |
|---|---|---|
| **3a** | `RetailMatchWorker` writes provider bridge IDs to `works.external_identifiers` JSON via the router (track-level IDs on the asset's Work, container IDs on the parent). | Shipped |
| **3b** | `WikidataBridgeWorker` routes resolved QIDs and container bridge IDs the same way; child manifests trigger `CatalogUpsertService.UpsertChildrenAsync` against the parent Work. | Shipped |
| **3c** | All four call sites in `RetailMatchWorker`, `WikidataBridgeWorker`, and `DescriptionIntelligenceBatchService` switch to `PersistAndScoreWithLineageAsync`. Display claims (title, year, description, cover, genre, cast) now mirror onto the parent Work's `canonical_values` in addition to the asset's. | Shipped |

### Dual write during transition

Phase 3c is intentionally **dual-write**: the asset's `metadata_claims` and `canonical_values` rows still receive the full picture (so existing library item CTEs and Review Queue queries don't regress), and the parent Work additionally receives an authoritative copy of the `Parent`-scoped fields. Later reader updates teach library item CTEs, collection rule evaluator, and detail drawer queries to consult the parent Work directly. A follow-up migration can retire the asset-side mirror once readers are ported and run a one-shot backfill that walks every existing asset, computes its lineage, and re-routes historical claims to the right Work rows.

The parent-side mirror is best-effort: failures are logged at warning level and never break the asset-side write. Movies and single-volume books (where `TargetForParentScope == TargetForSelfScope`) skip the parent pass entirely - the dual write collapses to a single write.

### Adding new claim keys

When a provider starts emitting a new claim key, decide its scope:

1. **Self** (the default): no action needed. The key will be written against the asset.
2. **Parent**: add an entry to `ClaimScopeCatalog.DefaultMap` if the scope is the same across all media types, or to `ClaimScopeCatalog.Overrides[mediaType]` if it depends on context.

The companion `_qid` suffix is handled automatically - `genre_qid` inherits the scope of `genre`. Tests live in `tests/MediaEngine.Domain.Tests/ClaimScopeCatalogTests.cs` and `tests/MediaEngine.Providers.Tests/ScoringHelperLineageTests.cs`.

## Related

- [How the Priority Cascade Works](../explanation/how-scoring-works.md)
- [Database Schema Reference](../reference/database-schema.md)
- [How to Resolve Items That Need Review](../guides/resolving-reviews.md)
