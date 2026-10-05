# Feature: Metadata Source Management

> Access update, 2026-09-09: the role descriptions below are historical. Metadata configuration and editing require live effective administration with any configured surface unlock, or the precise registered Application operation where supported. Household users have feature/library grants, not a Curator role. Dashboard navigation/actions consume Engine authority; never implement the old role matrix. See `docs/architecture/security.md`.

> **Mirrors:** `CLAUDE.md` §3.2 (Priority Cascade Engine), §3.7 (Hydration Pipeline) — keep both in sync per `.agent/SYNC-MAP.md`
> Last audited: 2026-04-02 | Auditor: Claude
> **Update 2026-04-08 — Adapter slimdown remediation (Tuvima.Wikidata v2.4.1):** The hand-rolled `ResolveBridgeAsync` / `ResolveMusicAlbumAsync` / `ResolveByTextAsync` / `AutoDetectStrategy` block (~540 lines) and the `BridgeResolutionResult` DTO have been deleted from `ReconciliationAdapter`. The public `ResolveAsync` / `ResolveBatchAsync` are now thin pass-throughs to `_reconciler.Stage2.ResolveBatchAsync` (the library natively groups by natural key, handles edition pivoting via `EditionPivotRule`, and supports a discriminated `IStage2Request` hierarchy: `BridgeStage2Request`, `MusicStage2Request`, `TextStage2Request`). The adapter follows up with a Data Extension call to populate `WikidataResolveResult.Claims`. Child entity discovery moved to `_reconciler.Children.GetChildEntitiesAsync(ChildEntityRequest)` (1 library call instead of N+1 for TV). `PersonReconciliationService` collapsed from ~445 lines to ~175 lines as a thin wrapper over `_reconciler.Persons.SearchAsync`. New pseudonym detection: Pattern 1 reverse P742 ("Richard Bachman" → Stephen King) and Pattern 2 P742 enumeration via `_reconciler.Authors.ResolveAsync`, emitted as `BridgeIdKeys.AuthorRealNameQid` and `BridgeIdKeys.AuthorPseudonym`. Pattern 3 collective pseudonyms (legacy block) still owns `author_is_collective_pseudonym` / `collective_members_qid`. Parity baseline at `tests/fixtures/stage2-baseline-v2.json`. See `.claude/plans/adapter-slimdown-remediation.md`.
> **Update 2026-04-02:** Unified scoring via `RetailMatchScoringService` — single scorer for both pipeline and manual search. Multi-author splitting, stronger Wikidata penalties (-35/-40), 85/15 score blend. Text-only Wikidata fallback removed; sentinel-only Stage 2 calls blocked. Network metadata (P449) added. See `CLAUDE.md` §3.2 and §3.7 for current details.
> **Update 2026-03-30:** Provider management now uses ranked pipelines (`config/pipelines.json`) with three strategies (Waterfall, Cascade, Sequential) per media type. The fixed 3-slot system (`slots.json`) is auto-converted. Settings UI has a per-media-type strategy picker. See `CLAUDE.md` §3.7 for current details.

---

## What the User Sees

The **Metadata** tab in Server Settings presents all registered metadata sources — the services that enrich your library with cover art, descriptions, narrator credits, and more.

Sources are grouped by media type into collapsible categories:

| Category | Icon | What it covers |
|----------|------|----------------|
| **Ebooks** | Book icon | Sources that specialise in book metadata (e.g. Apple Books Ebook, Open Library) |
| **Audiobooks** | Headphones icon | Sources that specialise in audiobook metadata (e.g. Apple Books Audiobook) |
| **Movies** | Film icon | Sources that specialise in video metadata |
| **Universal** | Globe icon | Sources that work across all media types (e.g. Wikidata, Local Filesystem) |

Each source appears as a floating card showing:

1. **Status badge** — "Reachable" (green), "Unreachable" (red), or "Disabled" (grey)
2. **Source name** — bold display name
3. **Domain chip** — which media type this source covers
4. **Zero-key badge** — shown when the source requires no API key
5. **Capability icons** — visual indicators of what data this source provides (cover art, narrator, series, description, etc.)
6. **Trust Score** — a progress bar showing how much influence this source has when different sources disagree about the same detail
7. **Field-specific trust** — an expandable section showing per-field trust weights (e.g. "narrator: 90%, cover: 85%")
8. **Enable/disable toggle** — turn a source on or off instantly
9. **Drag handle** — visual indicator for future reordering (not yet functional)

### Add Source Wizard

Administrators can click the "Add Source" button to open a three-step wizard:

1. **Basic Details** — enter a source name, endpoint URL, and optional API key
2. **Advanced Mapping** — configure how the source's response fields map to Tuvima Library metadata keys (placeholder)
3. **Verify Connection** — test connectivity to the source endpoint (placeholder)

The wizard collects information in preparation for Engine-side custom source registration, which is planned for a future update.

### Edit and Delete

- **Edit** (pencil icon) — opens the wizard with the source's details pre-filled
- **Delete** (bin icon) — shows a confirmation dialog explaining that source removal requires Engine-side support

---

## Who Can See This

| Role | Can see tab? | Can toggle sources? | Can add/edit/delete? |
|------|-------------|--------------------|--------------------|
| **Administrator** | Yes | Yes | Yes |
| **Curator** | Yes | No (read-only) | No |
| **Consumer** | No (tab hidden) | — | — |

---

## Staged Enrichment Pipeline

Metadata enrichment is a strict staged sequence. The identity stages are never reversed or bypassed.

**Stage 1 — Provider Identification:** Active configured providers are searched for identity, cover art, descriptions, ratings, people, and bridge identifiers. Music uses MusicBrainz first for recording/release identity and Apple second for artwork and retail metadata; other lanes use their configured retail/catalogue providers. Each candidate is scored by `RetailMatchScoringService`. If no provider returns a confident match, the item is routed to the review queue and Stage 2 is never attempted.

**Stage 2 — Wikidata Bridge Resolution:** Bridge IDs from Stage 1 are used to resolve a canonical Wikidata entity (QID) via the `Tuvima.Wikidata` bridge resolution service. This provides universe linkage, person relationships, and canonical metadata authority. If Stage 2 finds no QID, the item keeps its retail data and is flagged for periodic re-checking. The text-only Wikidata fallback (sentinel-only Stage 2 calls with no real bridge IDs) is disabled — Stage 1 must supply real bridge IDs.

**Quick Hydration:** Core identity, canonical values, and managed artwork are written for fast browse readiness. Artwork is stored through `entity_assets` and `.data/assets`, not legacy QID image folders.

**Stage 3 — Universe Enrichment:** Fanart.tv artwork, LRCLIB/OpenSubtitles text tracks, people, fictional entities, narrative roots, and relationships run after identity is safe enough.

**Post-resolution P31 validation:** After bridge resolution returns a QID and Data Extension fetches its properties, the adapter validates the entity's P31 (instance_of) against `exclude_classes` and `instance_of_classes` from `wikidata_reconciliation.json` for the requesting media type. If the entity type doesn't match — for example, a book's ISBN resolving to a film entity (P31 = Q11424) because both share the same ISBN — the result is rejected and the text fallback retries with CirrusSearch type filtering. This prevents cross-type contamination where a single shared identifier could pull in entirely wrong metadata.

**CirrusSearch type narrowing:** When text fallback searches Wikidata via CirrusSearch, `GetCirrusTypesForMediaType` now prefers narrow `work_classes` from the edition pivot config (e.g. Q571 literary work, Q7725634 written work for Books) over the broader `instance_of_classes`. This reduces false matches where a search term matches entities of the wrong media type.

**Two-pass enrichment:** The Quick pass gets the item visible on the Dashboard fast (core identity + managed artwork). The Universe pass runs later in the background for deep enrichment (relationships, people, fictional entities, additional images).

---

## Business Rules

| # | Rule | Where enforced |
|---|------|----------------|
| BR-01 | The Metadata tab is visible only to Administrators and Curators (AdminOnly = true in tab definitions). | Dashboard (SettingsTabBar) |
| BR-02 | Within the Metadata tab, write actions (toggle, edit, delete, add) require Administrator role. Curators see a read-only view. | Dashboard (MetadataTab + MetadataProviderCard) |
| BR-03 | Sources are grouped by Domain (Ebook, Audiobook, Video, Universal). Empty categories are hidden. | Dashboard (MetadataTab.GroupProviders) |
| BR-04 | Trust Score is displayed as a percentage derived from the provider's DefaultWeight. | Dashboard (MetadataProviderCard) |
| BR-05 | The Engine must be reachable for source data to load. If unreachable, a clear error message is shown. | Dashboard (MetadataTab.LoadProvidersAsync) |
| BR-06 | Source deletion is gated behind a confirmation dialog. Actual removal requires Engine-side support. | Dashboard (MetadataTab delete confirmation) |
| BR-07 | The Add Source wizard collects registration data but does not persist — custom source registration requires Engine-side support. | Dashboard (ProviderWizard) |
| BR-08 | Drag-and-drop reordering is visual only — no priority ordering until the Engine supports it. | Dashboard (MetadataProviderCard drag handle) |

---

## System Readiness

| Capability | Status | Notes |
|-----------|--------|-------|
| View grouped sources | **Live** | Sources load from Engine and display in category panels |
| Toggle source on/off | **Live** | Calls Engine PUT /settings/providers; refreshes list on success |
| Trust score display | **Live** | DefaultWeight and FieldWeights rendered from Engine data |
| Capability icons | **Live** | Mapped from CapabilityTags to Material Design icons |
| Status badges | **Live** | Derived from Enabled + IsReachable flags |
| Add Source wizard | **Stub** | Collects input; cannot register until Engine supports custom providers |
| Edit source | **Stub** | Opens wizard with pre-filled name; cannot save changes yet |
| Delete source | **Stub** | Confirmation dialog shown; actual deletion requires Engine support |
| Drag-and-drop reorder | **Stub** | Handle icon visible; no reordering logic until Engine supports priority |
| Connection test | **Stub** | Button exists; shows informational message about future availability |
| Schema mapping | **Stub** | Placeholder step in wizard; requires Engine support |

---

## Component Architecture

| Component | Responsibility |
|-----------|---------------|
| `MetadataTab.razor` | Page-level orchestrator: loads providers, groups by category, hosts wizard and delete dialog |
| `MetadataProviderCard.razor` | Single provider card: status, capabilities, trust score, toggle, edit/delete actions |
| `ProviderWizard.razor` | Three-step drawer wizard for add/edit flow |
| `SettingsTabBar.razor` | Tab navigation with role-based visibility |

---

## PO Summary

The Metadata Sources screen now presents your metadata providers in a clear, categorised layout — grouped by media type, with rich cards showing each source's capabilities, trust level, and status at a glance. Administrators can toggle sources on and off, and an "Add Source" wizard is ready to collect registration details for when the Engine supports custom provider connections. The screen is properly restricted: only Administrators and Curators can see it, and only Administrators can make changes.


### Retail matrix follow-up

Retail identity confidence uses typed per-media `scoring.scopes` in `config/pipelines.json` and synchronized settings defaults. Eight scopes replace the global retail weights: books, audiobooks, movies, TV series, TV episodes, music tracks, music albums and comic issues. Missing optional values redistribute their weight and show Not provided; required creator evidence fails closed. Genre contributes nothing. Wrong-kind and structural gates apply after bonuses. Additive nullable `field_scores` exposes the complete durable breakdown. Accept/review thresholds remain 0.90/0.65. See `docs/architecture/scoring-and-cascade.md` for the normative matrices and the distinction from canonical trust scoring. No legacy fallback or data conversion is supported.
