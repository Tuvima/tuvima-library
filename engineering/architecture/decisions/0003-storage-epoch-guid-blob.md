# 0003 — Storage epoch `guid-blob-v1`; reset instead of migrate

**Date:** 2026-05-30 (`bfe9f908`) · **Status:** Accepted

## What
Internal IDs are stored as 16-byte binary values; external IDs (Wikidata, provider IDs) stay as text; the Engine's JSON returns IDs as strings. `canonical_values` holds single values only; lists go in `canonical_value_arrays`. Libraries created before this epoch are reset and re-ingested, not converted.

## Why
- **Performance:** smaller indexes and faster joins on large libraries.
- **Maintenance:** one storage shape, no compatibility readers for old text IDs or packed lists.

## Trade-offs accepted
Pre-release users with old data re-ingest once. After a power or OS crash, the latest changes may need re-ingesting (WAL with `synchronous=NORMAL`).

## Revisit if
Tuvima leaves pre-release and resets stop being acceptable — then a real migration path is needed.
