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
status: current
---

# Priority Cascade Engine

## In this page

Follow canonical metadata selection through the Priority Cascade and distinguish it from retail identity matching. This page records configuration ownership, eligibility gates, and claim routing rules.

## Where this lives in the code

- `src/MediaEngine.Intelligence/PriorityCascadeEngine.cs`
- `src/MediaEngine.Providers/Services/RetailMatchScoringService.cs`
- `src/MediaEngine.Providers/Adapters/Internals/ReconciliationAdapter.Reconciliation.cs`
- `src/MediaEngine.Ingestion/OrganizationGate.cs`
- `config/field_priorities.json`
- `config/pipelines.json`
- `config/scoring.json`
- `config/hydration.json`
- `config/providers/wikidata_reconciliation.json`

## Purpose

When multiple sources disagree about a metadata field - title, author, year, cover art - the Priority Cascade Engine resolves the dispute and produces a single canonical value for each field. Every piece of metadata is modeled as a **Claim**: a triple of (source, value, confidence). Claims accumulate from all sources. The cascade picks a winner per field.

---

## Claim Sources and Base Weights

| Source | Base confidence | Notes |
|---|---|---|
| File internal metadata (OPF, ID3) | 0.9 | High trust - embedded at creation time |
| Filename | 0.5 | Medium trust - often approximate or user-modified |
| External providers | Configurable per field | See per-field trust weights below |
| User lock | 1.0 | Honored only for lockable fields - see Tier A |

External providers declare per-field trust weights in their provider config files (`config/providers/`). These weights reflect how reliable that provider is for a specific kind of data - Wikidata carries franchise identifiers at weight 1.0, Apple API carries cover art at 0.85, and so on.

---

## Priority Cascade Tiers

Tiers are evaluated in order. The first tier that can resolve a field wins; lower tiers are not consulted for that field.

### Tier A - User Locks

`PriorityCascadeEngine` honors user locks for `rating`, `media_type`, and `custom_tags`. A locked claim wins Tier A for those fields. Structured provider-owned fields such as title, author, and year continue through the provider hierarchy. An extracted claim with confidence `1.0` is not a user lock.

### Tier B - Per-Field Provider Priority

Some fields benefit from a preferred provider. The cascade tries media-specific priorities from `config/pipelines.json` before global overrides in `config/field_priorities.json`. It resolves configured names through enabled provider definitions and selects the newest claim from the first listed provider with usable evidence. A successful priority selection ends resolution for that field; if neither priority list resolves it, the cascade continues to Tier C.

Example overrides from `config/field_priorities.json`:

| Field | Priority order | Reason |
|---|---|---|
| `description` | wikidata_reconciliation, apple_api, tmdb | The reconciliation provider supplies rich Wikipedia descriptions |
| `cover` | apple_api, tmdb, musicbrainz | Prefer edition-specific retail artwork |
| `rating` | apple_api, tmdb | Wikidata does not carry ratings |
| `biography` | wikidata_reconciliation | Rich Wikipedia bios for persons |

Unresolved fields continue to Tier C (Wikidata authority).

### Tier C - Wikidata Authority

For a field unresolved by Tier B, Wikidata claims win when present. Confidence ranks Wikidata claims, with newest claim breaking a tie; title selection also respects the configured metadata-language handling. For `author`, a stronger file-source or user-source author claim can win, preserving an explicitly credited pen name. An arbitrary retail author claim does not receive that exception. Wikidata supplies canonical cross-media identity when a QID can be resolved. An unresolved QID does not by itself prevent a ready, safely identified owned work from appearing in browse.

### Tier D - Confidence Cascade

When no earlier tier resolves a field, the highest-confidence remaining claim wins. Tied fallback claims prefer the earliest `ClaimedAt`, preserving the first source author rather than the last inserted.

---

## Field Count Scaling

The current `PriorityCascadeEngine` does **not** apply field-count scaling. Its overall confidence starts with the average confidence of winning fields, or zero when there are none. It then adds a positive folder-category prior, capped at one, and applies any eligible media-specific confidence-floor boost.

```
overallConfidence = fieldScores.Count > 0
    ? fieldScores.Average(field => field.Confidence)
    : 0.0;
```

The older `fieldCount / 3.0` penalty is a legacy design, not active cascade behavior. Readiness, identity, and organization gates independently determine whether an item can proceed.

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
| Best author match < 0.3 (clear mismatch) | 35 penalty |
| Candidate has no supported author/performer properties | 40 penalty |

Wikidata reconciliation has its own candidate scoring and acceptance gates, separate from canonical field selection.

**Wikidata score thresholds** (configured under `reconciliation` in `config/providers/wikidata_reconciliation.json`):

| Key | Value | Meaning |
|---|---|---|
| `review_threshold` | 55 | Reconciliation review threshold |
| `auto_accept_threshold` | 95 | Reconciliation automatic acceptance threshold; bridge-worker identity gates also apply |

---

## Conflicted Fields

Every current cascade field result sets `IsConflicted = false`. Close confidence scores do not trigger legacy conflict marking; the tier and tie-breaking rules choose a result. Identity matching and Review Queue can still identify actionable ambiguity through their own checks.

`conflict_epsilon` remains in scoring configuration types but is inactive in this cascade. `conflict_threshold` still serves identity/collection-link disposition thresholds; it does not activate close-score field conflicts.

---

## Claim History

All claims are stored append-only. No claim is ever deleted or overwritten - only superseded by higher-priority claims. The full provenance trail for every field is always available.

---

## Auto-Link and Promotion Gates

The auto-link threshold (`auto_link_threshold`) in `config/scoring.json` governs when a scored file is automatically promoted from staging to the organised library:

- `OrganizationGate` considers `overallConfidence >= 0.85` or its explicit `hasUserLock` input sufficient for the confidence gate.
- Media-type review, a placeholder title without bridge identity, and an `Other` destination can still block organization; confidence alone is not unconditional promotion.
- Below-threshold results select `low-confidence` or, below 0.40, `unidentifiable` staging outcomes. `AssetPathService` owns physical managed paths.

---

## Configuration Reference

Configuration ownership depends on the scoring operation. Canonical selection, retail identity matching, and Wikidata reconciliation do not share one threshold file:

| Key | Default | Purpose |
|---|---|---|
| `scoring.json`: `auto_link_threshold` | 0.85 | Organization confidence gate and identity/collection auto-link disposition |
| `scoring.json`: `conflict_threshold` | 0.60 | Identity/collection-link review disposition; not cascade field conflicts |
| `scoring.json`: `conflict_epsilon` | 0.05 | Legacy close-score setting; inactive in `PriorityCascadeEngine` |
| `scoring.json`: `stale_claim_decay_days` | 90 | Legacy age-decay setting; inactive in `PriorityCascadeEngine` |
| `scoring.json`: `stale_claim_decay_factor` | 0.8 | Legacy decay multiplier; inactive in `PriorityCascadeEngine` |
| `hydration.json`: `retail_auto_accept_threshold` | 0.90 | Retail automatic acceptance default; typed pipeline overrides may apply |
| `hydration.json`: `retail_ambiguous_threshold` | 0.65 | Retail review/discard default; typed pipeline overrides may apply |
| `providers/wikidata_reconciliation.json`: `reconciliation.review_threshold` | 55 | Reconciliation candidate review gate |
| `providers/wikidata_reconciliation.json`: `reconciliation.auto_accept_threshold` | 95 | Reconciliation candidate automatic acceptance gate |

Per-media field priorities and retail matrices live in `config/pipelines.json`; global field priorities live in `config/field_priorities.json`. Keeping legacy options in a typed configuration object does not mean the active cascade reads or applies them. See the [reader explanation](../explanation/how-scoring-works.md) for the same source-selection rules in plain language.

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
