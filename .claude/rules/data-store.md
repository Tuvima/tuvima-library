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
- GUID storage (epoch family `guid-blob-*`, current value in `StorageEpochGuard.CurrentEpoch`): internal SQLite GUIDs are 16-byte BLOBs, external IDs stay TEXT, API JSON returns GUID strings; legacy TEXT-GUID databases are reset and reingested, not migrated. Dapper expands a list parameter (`IN @ids`) without the Guid handler, so convert Guid lists with `.Select(GuidSql.ToBlob).ToArray()` (enforced by `DapperGuidListParameterGuardrailTests`).
- SQLite: WAL, `synchronous=NORMAL`, `busy_timeout=5000`, memory temp store, 16 MiB page cache, 256 MiB mmap cap. Accepted tradeoff: an OS/power crash may require reingesting the latest changes.
- `canonical_values` is scalar-only; multi-valued metadata goes in `canonical_value_arrays`. No packed delimiters or compatibility readers.
- The data structure is built and changed only by numbered upgrade steps in `src/MediaEngine.Storage/Schema/Upgrades/`. A step is either `NNNN_short_name.sql` or a `DataStoreCodeUpgradeStep` subclass. `DataStoreUpgradeRunner` applies each step once at startup (`DatabaseConnection.InitializeSchema()`) and records it in `data_store_upgrades`.
  - Never edit a released step; add the next number instead.
  - Steps contain no PRAGMAs and no transaction control.
  - Built-in lists defined in code (providers, onboarding steps, default profile) are topped up by `ReferenceDataTopUp`: add-if-missing rows only, no structure changes.
  - Pre-release, breaking changes are an epoch bump plus a reset (`StorageEpochGuard.CurrentEpoch`, with an upgrade step that updates the stored epoch). No backwards-compatibility shims.
- Repositories carry the `Repository` suffix and live in `src/MediaEngine.Storage/`. Razor components contain no SQL; endpoints move SQL-heavy behaviour into repositories/read services when touched.
- Claims are append-only. Cascade tiers: user locks → per-field provider priority → Wikidata authority → highest confidence.
- New tables/columns update `docs/reference/database-schema.md` in the same change.
