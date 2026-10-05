# Skill: Metadata Scoring & Provider Management

> **Mirrors:** `CLAUDE.md` §3.2 (Priority Cascade Engine) — keep both in sync per `.agent/SYNC-MAP.md`

> Last updated: 2026-04-02

---

## Purpose

This skill covers the Priority Cascade Engine — how metadata Claims are collected, scored, and resolved into Canonical Values — and how providers are managed. Also covers `RetailMatchScoringService`, the unified scorer for both pipeline and manual search.

---

## Key files

| File | Role |
|------|------|
| `src/MediaEngine.Intelligence/PriorityCascadeEngine.cs` | Four-tier cascade: User Locks → Field Priority → Wikidata → Highest Confidence |
| `src/MediaEngine.Intelligence/Models/ScoringConfiguration.cs` | Thresholds: auto-link 0.85, conflict 0.60, epsilon 0.05, stale decay 90d/0.8 |
| `src/MediaEngine.Providers/Services/RetailMatchScoringService.cs` | Unified scorer: title/author/year/format + cross-field boosts + multi-author splitting |
| `src/MediaEngine.Providers/Adapters/ReconciliationAdapter.cs` | Wikidata candidate ranking with multi-author matching and penalty system |
| `src/MediaEngine.Providers/Services/HydrationPipelineService.cs` | Stage 1 retail confidence gate using RetailMatchScoringService |
| `src/MediaEngine.Providers/Services/SearchService.cs` | Manual search scoring using RetailMatchScoringService |
| `src/MediaEngine.Domain/Entities/MetadataClaim.cs` | Append-only claim record (key, value, confidence, provider, timestamp) |
| `src/MediaEngine.Domain/Entities/CanonicalValue.cs` | Scored winner per field per entity |
| `src/MediaEngine.Domain/Constants/ClaimConfidence.cs` | 22 named confidence constants (0.70–1.00) |
| `src/MediaEngine.Api/Endpoints/SettingsEndpoints.cs` | Provider status + toggle endpoints |

---

## Priority Cascade (replaces Weighted Voter)

```
For each metadata field:
  Tier A: User lock → wins unconditionally (confidence 1.0)
  Tier B: Per-field provider priority from config/field_priorities.json
  Tier C: Wikidata authority
  Tier D: Highest confidence wins (with stale decay)
```

## Unified Retail Match Scoring

`RetailMatchScoringService` is the single scorer for both pipeline and manual search:
- Field weights: title 45%, author 35%, year 10%, format 10%
- Cross-field boosts: narrator/author in description, genre overlap, language match/mismatch
- Multi-author splitting: "A & B" split on &/and/, — proportional matching (matched/total)
- Placeholder rejection: "Unknown", "Untitled", "Track XX" → zero score
- Pipeline gate: ≥0.85 auto-accept, 0.50–0.85 accept+review, <0.50 discard

## Wikidata Candidate Ranking

- Multi-author matching against P50/P175 labels (proportional scoring)
- Author mismatch (best < 0.3): -35 penalty
- No P50/P175 properties: -40 penalty
- Score blend: 85% composite / 15% API score
- Sentinel-only ResolveBridgeAsync calls blocked (no retail = no Wikidata)

---

## How to add a new provider

1. Create a class implementing `IExternalMetadataProvider` in `src/MediaEngine.Providers/`.
2. Assign a stable hardcoded GUID (never looked up from DB at runtime).
3. Add the provider entry to `tuvima_master.json` under `providers` with:
   - `name`, `version`, `enabled`, `weight`, `domain`, `capability_tags`, `field_weights`
4. Add the base URL to `provider_endpoints` in the manifest.
5. Register the class in `Program.cs` DI container.
6. The harvest pipeline will automatically include it in background enrichment.

No changes to existing providers or scoring code are needed.

---

## How to change trust weights

Currently: edit `tuvima_master.json` directly. No API endpoint or UI editor exists.

Future: a weight editor on the Metadata tab would allow runtime adjustment with live preview of scoring impact.

---

## How user corrections work

1. User opens Curator's Drawer from Server Settings.
2. Searches for a work and selects the correct match.
3. Clicks "Apply Correction" — this calls `PATCH /metadata/lock-claim` with `entityId`, `key`, and `value`.
4. Engine creates a new MetadataClaim with `IsUserLocked = true` and `Confidence = 1.0`.
5. Re-scoring runs; the locked Claim always wins, overriding all provider claims.
6. Locked Claims survive any future re-scoring and are written into the library.xml sidecar for portability.

---

## Known gaps

1. **Conflicted fields are not surfaced in the Dashboard.** The engine detects them but there is no UI indicator.
2. **Trust weights have no API endpoint or UI editor.** Must be changed via manifest file.
3. **Two providers (Local Filesystem, Open Library) have no reachability probe** — they always show as unreachable in the Metadata tab even though Local Filesystem is inherently always available.
4. **Scoring/Maintenance settings have no API surface.** Thresholds, decay parameters, and vacuum settings are manifest-only.


### Retail matrix follow-up

Retail identity confidence uses typed per-media `scoring.scopes` in `config/pipelines.json` and synchronized settings defaults. Eight scopes replace the global retail weights: books, audiobooks, movies, TV series, TV episodes, music tracks, music albums and comic issues. Missing optional values redistribute their weight and show Not provided; required creator evidence fails closed. Genre contributes nothing. Wrong-kind and structural gates apply after bonuses. Additive nullable `field_scores` exposes the complete durable breakdown. Accept/review thresholds remain 0.90/0.65. See `docs/architecture/scoring-and-cascade.md` for the normative matrices and the distinction from canonical trust scoring. No legacy fallback or data conversion is supported.
