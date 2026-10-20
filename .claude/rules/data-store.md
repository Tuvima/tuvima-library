---
paths:
  - "src/MediaEngine.Storage/**"
  - "src/MediaEngine.Domain/**"
  - "src/MediaEngine.Application/**"
  - "src/MediaEngine.Api/**"
  - "src/MediaEngine.Ingestion/**"
  - "src/MediaEngine.Providers/**"
  - "src/MediaEngine.Intelligence/**"
  - "tests/**"
---

# Data store rules (SQLite + Dapper)

- Use `IDatabaseConnection.CreateConnection()` for repository, read-service, endpoint, background-job, and request-path work; dispose each short-lived connection with `using`. `IDatabaseConnection.Open()` is startup/schema/integrity-only — new uses elsewhere must fail guardrail tests.
- GUID storage (epoch family `guid-blob-*`, current value in `StorageEpochGuard.CurrentEpoch`): internal SQLite GUIDs are 16-byte BLOBs, external IDs stay TEXT, API JSON returns GUID strings; legacy TEXT-GUID databases are reset and reingested, not migrated.
- SQLite: WAL, `synchronous=NORMAL`, `busy_timeout=5000`, memory temp store, 16 MiB page cache, 256 MiB mmap cap. Accepted tradeoff: an OS/power crash may require reingesting the latest changes.
- `canonical_values` is scalar-only; multi-valued metadata goes in `canonical_value_arrays`. No packed delimiters or compatibility readers.
- The data structure is built and changed only by numbered upgrade steps in `src/MediaEngine.Storage/Schema/Upgrades/`. A step is either `NNNN_short_name.sql` or a `DataStoreCodeUpgradeStep` subclass. `DataStoreUpgradeRunner` applies each step once at startup (`DatabaseConnection.InitializeSchema()`) and records it in `data_store_upgrades`.
  - Never edit a released step; add the next number instead.
  - Steps contain no PRAGMAs and no transaction control.
  - Built-in lists defined in code (providers, onboarding steps, default profile) are topped up by `ReferenceDataTopUp`: add-if-missing rows only, no structure changes.
  - Pre-release, breaking changes are an epoch bump plus a reset (`StorageEpochGuard.CurrentEpoch`, with an upgrade step that updates the stored epoch). No backwards-compatibility shims.
- Repositories carry the `Repository` suffix and live in `src/MediaEngine.Storage/`. Razor components contain no SQL; endpoints move SQL-heavy behaviour into repositories/read services when touched.
- Claims are append-only. Cascade tiers: user locks → per-field provider priority → Wikidata authority → highest confidence.
- Which source wins where they disagree (Universes spec U-WP4). A user lock beats all of these.
  - **Display text and cover art:** retail providers (title, description and cover as shown).
  - **Identity, Series membership and Series order:** Wikidata. Placement reads the hydrated Wikidata Series manifest first (`SeriesPlacementRule`); order reads the manifest ordinal scoped to that Series, then the Work's own Wikidata (Series QID, position) pair from `canonical_value_arrays` key `series_placement`, and only then the retail `series_position` (`SeriesOrdering`). Every pair is stored, never one number per Work.
  - **Universe:** one record per Wikidata ID (`narrative_roots`). `UniverseIdentityRule` alone picks a Work's or Series' Universe; the Universe shelf (`collections.universe_qid`) and the graph root both come from it, never from a second rule.
  - **Lore plugin (Fandom) and AI output:** supplementary facts only. They never move a Work between Series or Universes, never change Series order, and never create a Universe.
  - Per-field priorities in `config/pipeline-priority-defaults.json` that still give a retail provider `series` or `series_position` are changed through the Library-set weights (Developer tools WP20), not by editing that file.
- New tables/columns update `docs/reference/database-schema.md` in the same change.
