# Sequence Dataset Gap Report (2026-07-15)

## Scope

This is a read-only audit of the existing local database at `C:\temp\tuvima-library\.data\database\library.db`. The Engine and Dashboard were stopped before inspection. No watched folders, library data, database rows, or media files were deleted or re-ingested.

The audit focused on whether Tuvima Library can show named missing members for TV seasons and comic series, plus the TV fields needed by the updated episode cards: episode number, title, original air date, runtime, and description.

## Executive finding

The system has enough episode identity data to improve the owned TV card labels immediately, but it does not yet have consistently trustworthy ownership and completeness data for showing missing episodes across all current shows. Comics currently have series totals but almost no full issue-member datasets, so they cannot yet show named missing issues safely.

The Dashboard gates **Show missing** on an authoritative total. Its initial state comes from the media policy in `config/ui/library-preferences.json`, then an explicit profile-and-series database override takes precedence. The control does not appear when the backend only has a partial list.

## Current TV coverage

The database contains 21 owned TV child works and 306 catalog-only TV works. Across metadata claims, 338 entities have an episode number.

| Field | Present | Coverage |
|---|---:|---:|
| Episode title | 338 / 338 | 100% |
| Year | 316 / 338 | 93.5% |
| Runtime | 181 / 338 | 53.6% |
| Description | 157 / 338 | 46.4% |
| Exact original air date as a normal claim | 0 / 338 | 0% |

Deep child discovery contains 317 distinct episodes. Inside the stored `child_entities_json` payloads, 316 have exact air dates, 181 have runtimes, and 136 have descriptions. The exact air dates are therefore being fetched but are not projected into normal episode claims/canonical values or the detail read model. Existing episode cards generally receive only a year.

Seven of the 18 owned TV parent rows have deep child-manifest claims. The persisted series-manifest layer currently covers three shows and their first-season manifests:

| Dataset | Completeness | Rows | Key concern |
|---|---|---:|---|
| Breaking Bad show | Partial | 67 | 58 catalog-only links are marked manifest-owned |
| Breaking Bad season 1 | Partial | 7 | 3 catalog-only links are marked manifest-owned |
| Game of Thrones show | Partial | 81 | 71 catalog-only links are marked manifest-owned |
| Game of Thrones season 1 | Partial | 10 | 8 catalog-only links are marked manifest-owned |
| The Last of Us show | Partial | 20 | Ownership link is correct for the one owned row |
| The Last of Us season 1 | Complete | 9 | Only current authoritative season dataset |

The parent show manifests also report `expectedTotalKind = seasons` while the totals (62 for Breaking Bad and 73 for Game of Thrones) are episode counts. This makes the current parent-level completeness metadata semantically inconsistent.

### TV work required before a larger refresh

1. Project `air_date` from child discovery into episode claims/canonical values and preserve the full date instead of reducing it to `year`.
2. Treat a linked catalog-only work as missing, not owned, when `works.ownership = 'Unowned'` or `is_catalog_only = 1`.
3. Correct TV show manifest count kinds so episode totals are labeled as episodes; retain season totals separately.
4. Add or verify an authoritative provider season manifest path. The TMDB configuration has a season endpoint, but it currently selects one result for ordinary field mapping and does not build a complete episode-member `sequence_manifest_json`.
5. Persist per-season completeness so the Dashboard can enable **Show Missing** for a complete season even when the broader show dataset is partial.
6. Backfill exact dates, runtimes, descriptions, and corrected ownership through a controlled fresh ingest after the fixes are tested.

## Current comic coverage

The database contains 12 owned comic child works and five owned comic parent works. All five parents have a Comic Vine volume ID, `sequence_total`, and `sequence_total_scope`. None has `sequence_manifest_json`.

Four Wikidata comic/manga manifests exist:

| Dataset | Completeness | Member rows |
|---|---|---:|
| Akira | Partial | 1 |
| Batman | Partial | 0 |
| Saga | Partial | 1 |
| Watchmen | Partial | 0 |

The current Comic Vine configuration fetches an issue or volume search result and maps `count_of_issues`, but it does not call the volume issue-list endpoint to retrieve every issue ID, number, title, cover date, and description. A numeric total is useful for an owned-count badge, but it is not enough to render named missing issues.

### Comic work required before a larger refresh

1. Add a Comic Vine volume-member request that pages through the complete issue list for the resolved volume ID.
2. Store the result as a provider sequence manifest with stable issue IDs, source ordinals, titles, publication dates, and an authoritative-completeness flag.
3. Link owned issues by Comic Vine issue ID; do not merge members by normalized title alone.
4. Keep comic completion UI as an owned-count badge until the member list is authoritative, consistent with the product rule that comics do not show an `of N` completion target from a total alone.
5. Fresh-ingest the comic test set only after manifest paging, identity linking, and partial-response tests pass.

The Comic Vine member request must use the provider's configured field list and exclude image fields. The member enumeration stores issue identity, number, title, and date only; it does not schedule artwork downloads for missing issues. Per-media missing-item visibility, paging, page size, and detail-hydration behavior are tracked in `config/ui/library-preferences.json` rather than hardcoded in the Dashboard.

## Other data-quality finding

Four TV entities currently carry a `comic_vine_id` claim. This is cross-media contamination and should be investigated in provider eligibility or hierarchy claim propagation before re-ingestion.

## Recommended validation plan before re-ingestion

1. Add unit tests for exact TV air-date projection, runtime projection, correct episode/season count kinds, and catalog-only ownership.
2. Add provider tests proving full paging and completeness behavior for TMDB seasons and Comic Vine volumes, including partial responses and API limits.
3. Add detail-composer tests proving missing rows appear only for authoritative manifests and that unchecking **Show Missing** retains owned rows and stable counts.
4. Run the standard restore, build, and test gates.
5. Then perform the repo-prescribed targeted fresh ingest and compare database manifest counts, API output, and Dashboard rendering against provider totals.

## Plain-English summary

Tuvima already knows the names and numbers of nearly all discovered TV episodes, and it has most original air dates in a raw payload. The main issue is that those dates are not yet reaching the screen, and some episodes that are only in the provider catalog are incorrectly counted as owned. For comics, Tuvima knows how many issues some series contain but usually does not yet know the full named issue list. Fixing those data paths before re-ingesting will let the new **Show Missing** option be accurate instead of merely looking complete.
