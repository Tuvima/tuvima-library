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
- Storage epoch `guid-blob-v1`: internal SQLite GUIDs are 16-byte BLOBs, external IDs stay TEXT, API JSON returns GUID strings; legacy TEXT-GUID databases are reset and reingested, not migrated.
- SQLite: WAL, `synchronous=NORMAL`, `busy_timeout=5000`, memory temp store, 16 MiB page cache, 256 MiB mmap cap. Accepted tradeoff: an OS/power crash may require reingesting the latest changes.
- `canonical_values` is scalar-only; multi-valued metadata goes in `canonical_value_arrays`. No packed delimiters or compatibility readers.
- Schema lives in `src/MediaEngine.Storage/Schema/schema.sql`. Schema changes are idempotent startup migrations owned by `SchemaMigrator` (via `DatabaseConnection.RunStartupChecks()`). Pre-release: breaking changes and data-store resets are acceptable; no backwards-compatibility shims.
- Repositories carry the `Repository` suffix and live in `src/MediaEngine.Storage/`. Razor components contain no SQL; endpoints move SQL-heavy behaviour into repositories/read services when touched.
- Claims are append-only. Cascade tiers: user locks → per-field provider priority → Wikidata authority → highest confidence.
- New tables/columns update `docs/reference/database-schema.md` in the same change.
