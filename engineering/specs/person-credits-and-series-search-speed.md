# Spec: Person credits and series search without the whole-library credit list

> **Status:** Draft · **Version:** 1 · **Last updated:** 2026-10-20 · **Related docs:** [Product overview](../product/overview.md) · [Spec: Audiobook year on the remaining screens](archive/audiobook-year-other-reads.md) (built; its section 8 lists these follow-ups, and its D12 accepted the person page's slower preparation on condition that this spec removes it) · [Spec: Home and item detail speed](archive/home-and-detail-speed.md) (built; the person page's two-step look-up this spec copies) · [Benchmarks README](../../benchmarks/README.md)

## 1. Goal

Two screens build the whole library's credit list (who wrote, narrated, directed or performed every file) on every visit, only to keep a handful of rows. A person's page does it twice (three times for someone in a group), about 1–3.5 s at 4,000 works, and the cost doubles with the library. Series search does it once for every file the search index matches, so a common word takes 15 seconds to 5 minutes at 4,000 works. After this spec, a person's page looks only at the files that could credit that person, and series search works out credits only for the 20 results it shows. Measured on a prototype: about 100 times less work on a person's page and about 10,000 times less on series search, with the same rows. Nothing on screen changes except speed.

## 2. Scope

**In scope**
1. **A person's credits** (the list of a person's works on their page, newest year first, including "With <group>" rows for a group the person belongs to and the pen-name rule). It finds the files that could credit the person or their groups first, then works out credits for just those files, the way the person page's own item list already does.
2. **Series search** (the search box on the series screens, 20 results). It chooses its 20 results first, then works out the author shown on each result for those 20 files only.
3. **The name search series search uses when there is no search index** (Q1 decides whether it is made fast or removed).
4. **One shared "files that may credit these persons" step**, used by the person page's item list, a person's credits and (if kept, Q1) the series search name search, so the three cannot drift apart.
5. **The collection editor's "add media" search**, if Q2 says so: it sends the whole visible library as a list of separate values and works out a representative file for every work in the library on every page.
6. One before-and-after comparison of every value these reads return, on the benchmark library, and guardrails that fail if either read goes back to the whole-library credit list (Q3).

**Out of scope**
- Any change to what these screens show, their order (except tied rows, D6), filters, profile visibility rules or wire shapes.
- New indexes or any other data store change. The person page's candidate-file search stays as it is (earlier spec's D10); a person's credits reuses it.
- What the search index covers (titles and the work's author only). Series search keeps asking it as today.
- The other screens that read the whole-library credit list (section 8): about 14 more reads, including universal search's people results and the series browse page. They stay as they are; section 8 records them as follow-up remediation R2.

## 3. Decisions made

- **D1. Same values, same order.** Every field of every row stays identical, and so does row order. The benchmark comparison (D7) is the proof; reviewers reject any difference it does not explain.
- **D2. Candidates may be more than the credited files, never fewer.** The shared step (D4) finds every file whose show or album, work or own id carries a stored credit, provider claim or single stored value that names one of the persons (same name ignoring case and surrounding spaces, or the same Wikidata id). That is a superset of the files the whole-library credit list would credit to them. Credits are then worked out exactly for those files by the existing per-file block, which already gives exactly the whole-library list's rows for any set of files (its own tests prove it). So the rows a read keeps are the same.
- **D3. Both reads stay one statement.** SQLite copies a step's text at every place it is named, which is why the person page split its look-up into two statements. Here every new step is marked saved ("materialised") and each large one is named once, so there is no copy blow-up. Measured on the prototype on a quiet machine: a person's credits prepares in 0.11–0.14 s instead of 0.23–0.28 s today, back near the 0.1 s it took before the audiobook-year spec (that spec's D12 asked this follow-up to remove the extra time), and its compiled size falls from 58,653 to 48,826 SQLite operations. Series search prepares in about the same time as today. A two-statement version of a person's credits was also measured: the same work and no faster to prepare, and two statements are not one snapshot, so one statement is kept. (A first measurement of the one-statement version showed 2–5 s; repeated on a quieter machine it was 0.11–0.14 s, so that was machine load.)
- **D4. One shared candidate step.** The person page's item list, a person's credits and (if kept) the series search name search use the same building block, given the persons to look for: the page's person and everyone with the same name or Wikidata id; the person and the groups they belong to; or everyone whose name contains the search text. A rule change for how a credit names a person is then made in one place, beside the per-file credit block.
- **D5. Series search works out the author after choosing its page.** The author shown on a result is not used to match, filter or sort, so the 20 rows are chosen first (as the borrowed year already is) and the author look-up runs for their 20 files with the same rules: by media type, the main role first (author, director, artist, performer), then billing order, then name; then the same stored fallbacks as today.
- **D6. Tied rows get a fixed order.** Today rows with the same year, title and role on a person's page (for example a book and its audiobook with the same title) come back in whatever order the data store's plan produces; the prototype swapped two such pairs. Those rows are ordered by work after role, so the order no longer depends on the plan. The same was done for series search in the earlier spec.
- **D7. One before-and-after comparison.** The benchmark `snapshot` already has `person-credits:*` (including a group member), `series-search:*` and `collection-lookup:*` sections, and `detail:person` for the person page that shares D4's step. Run on unchanged code first, then after each packet; `compare` must report every section identical (for person credits, apart from tied rows if D6 reorders them, which the packet records). WP1 adds a pen-name person to the benchmark seed so the pen-name rule is compared too.

## 4. UI behaviour

Nothing changes on screen except speed.

| Situation | What the person sees |
|---|---|
| Normal | A person's page lists the same works, roles and "With <group>" rows in the same order, about 1–3.5 s sooner at 4,000 works. Series search shows the same 20 results with the same authors in well under a second instead of 15 seconds to 5 minutes. "Add media" (if Q2 is (a)) shows each page in about a tenth of a second instead of about a second. |
| Empty (nothing to show yet) | Unchanged: a person with no credited files shows no works; a search with no matches shows none. |
| Loading | Unchanged, just shorter. |
| Error (Engine unreachable, not allowed, not found) | Unchanged. Profile limits hide exactly the same items as today. |
| Partly ready (enrichment still running, provider down) | Unchanged. A credit that only a provider claim carries so far still counts, as today. |

## 5. Data / model changes

None in the product. The benchmark seed gains one pen-name person (WP1); the performance-test library gains one group and one pen name (test tools only).

## 6. Work packets

**Today's cost and the prototype, measured** on 2026-10-20 through the production code on the performance-test library (with one group and one pen name added), thousands of SQLite operations, the same on every run; 8,000-work figures in brackets. Wall times are from a busy desktop and only indicative. The prototype was a scratch change to the three reads, thrown away afterwards. Its rows were compared with today's for every sample: series search and "add media" identical; a person's credits identical except two pairs of fully tied rows that swapped places (D6). The existing behaviour tests and the audiobook-year guardrails passed on it.

*A person's credits*

| Person | Rows | Today | Prototype |
|---|---:|---:|---:|
| An author of 12 works | 12 | 85,436 (170,576) | 919 (1,767) |
| An author of 24 books and an album, sharing a name with another person | 25 | 85,295 (170,270) | 807 (1,463) |
| A group (credited as author) | 32 | 85,302 (170,284) | 876 (1,565) |
| A member of that group (own credits and "With <group>" rows) | 39 | 167,048 (333,546) | 1,113 (2,014) |
| A pen name | 11 | 85,278 (170,284) | 758 (1,565) |
| A narrator of 40 audiobooks | 40 | 3,764 (7,446) | 1,128 (2,190) |

Today each visit builds the whole library's credit list once for direct credits and once for the pen-name check (a member of a group: once more), about 1–3.5 s. A narrator's page is already cheaper today because the data store's plan happens to handle that case differently; it still gets about three times cheaper. The prototype's remaining cost is the search for candidate files (it reads every stored credit, claim and value once, which the earlier spec's D10 accepted), so it still grows with the library (1.9 times at twice the size) but at about a hundredth of today's work.

*Series search* (through the search index, as the Engine always runs it)

| Search | Rows | Today | Prototype |
|---|---:|---:|---:|
| 40 index matches, audiobooks | 20 | 3,135,156 (15–43 s) | 332 |
| 40 index matches, mixed | 20 | 3,135,147 | 314 |
| 80 index matches (the index's maximum) | 20 | 6,266,690 (about 5 minutes) | 341 |
| No index, name search (Q1) | 20 | about 20.7 billion (570 s, earlier spec) | 1,551–3,721 |

Today's cost is the whole credit list once for every file the index matched, before the page of 20 is chosen. The prototype works out credits for the 20 chosen files only, so its cost no longer depends on the number of matches or the library's size.

*"Add media"* (every visible work allowed, as for an administrator)

| Page | Today | Prototype |
|---|---:|---:|
| First page, no filter | 2,978 (0.95 s) [5,969, 2.2 s] | 3,044 (0.17 s) [6,101, 0.2 s] |
| Audiobooks, first page | 1,113 (0.98 s) | 1,179 (0.09 s) |
| Books, second page | 1,395 (0.68 s) | 1,461 (0.08 s) |
| With search text | 2,487 (1.09 s) | 2,553 (0.11 s) |

The work SQLite does barely changes (a representative file is still found for every allowed work, and an administrator is allowed every work). The time goes because the 4,123 allowed works (8,243 at 8,000 works) are no longer sent as 4,123 separate values; that list, not the representative file, was most of the time. SQLite on this product accepts at most 32,766 values in one statement, so today this screen fails outright above about 32,000 visible works.

### WP1 — Comparison baseline and test library additions
- **What it does:** take the `snapshot` baseline on unchanged code (all sections). In the benchmark seeder, flag one person who shares works with another author as a pen name (`persons.is_pseudonym`), add a `person-credits:pen-name` sample for that person and for the co-author, and record it in `benchmarks/README.md` with the same "does not change earlier sections" note as the person group. In `tests/MediaEngine.Performance.Tests/LibraryListFixture.cs` add one group ("Person 70" flagged `is_group`, with "Person 71" as member) and one pen name ("Person 140"), as the planning probe did; earlier guardrail numbers must not move (re-measure and record if one does).
- **Files likely touched:** `benchmarks/MediaEngine.Storage.Benchmarks/Seeding/DataSeeder.cs`, `benchmarks/MediaEngine.Storage.Benchmarks/Snapshot/SnapshotSamples.cs`, `benchmarks/README.md`, `tests/MediaEngine.Performance.Tests/LibraryListFixture.cs`
- **Depends on:** none
- **Size:** Mechanical
- **Done when:** `snapshot` on unchanged code writes every section with rows, including the new pen-name samples; a second run gives an identical file; the existing performance guardrails pass.

### WP2 — Shared candidate step and a person's credits
- **What it does:**
  - In `src/MediaEngine.Storage/PersonCreditSql.cs` add `CandidateAssetsCte(string resultName, string personsSelect, string prefix)`: the chain now inside `DisplayWorkProjectionReader.PersonAssetsCtes` from `detail_credit_entities` to `detail_candidate_assets` (stored credit arrays matched by trimmed name ignoring case or by Wikidata id; claims and single stored values by trimmed name; then the file itself, its work's files, its children's and grandchildren's files), fed by `personsSelect` (columns `id, name, wikidata_qid`), every step `MATERIALIZED`, result column `asset_id`. Doc comment says it is a superset and why (D2).
  - `DisplayWorkProjectionReader.PersonAssetsCtes` keeps `detail_person` and `detail_match_persons` and calls the new block with `SELECT id, name, wikidata_qid FROM detail_match_persons`. Its SQL must stay equivalent; `detail:person` compares identical.
  - `PersonCreditReadService.GetLibraryCreditsAsync` (one statement, D3): at the start of its `WITH`, the new block for `SELECT id, name, wikidata_qid FROM persons WHERE id = @personId UNION SELECT group_person.… FROM person_group_members membership INNER JOIN persons group_person ON group_person.id = membership.group_id WHERE membership.member_id = @personId`, then `PersonCreditSql.ScopedCreditsCte("…_rows", "SELECT asset_id FROM <candidates>", …)`, then one saved step `… AS MATERIALIZED (SELECT * FROM …_rows)`. Both `primary_person_media_credits` reads in `effective_credit` and the one in the pen-name `NOT EXISTS` read that saved step instead. Everything else (the year, grouping, the membership-date filter in C#, characters) is unchanged. Add `w.id` after `primary_credit.role` in the `ORDER BY` (D6). The method's comment says why credits are worked out for candidate files only (D2) and why it stays one statement (D3). The planning prototype is in the shared session notes (`credit-reads-prototype.patch`) as a reference, not as code to copy.
  - Tests (behaviour sentences, in `tests/MediaEngine.Api.Tests/PersonCreditReadServiceTests.cs`): `GetLibraryCreditsAsync_FindsCreditsGivenOnlyByAProviderClaim`, `…_FindsCreditsByWikidataIdWhenTheNameDiffers`, `…_FindsGroupCreditsOnAnAlbumThroughItsTracks` (credit on the album, rows for its tracks), and `PersonCreditSqlTests.CandidateAssets_ContainEveryFileTheSharedCreditViewCreditsToThePerson` (for every person in the test library, the view's files are a subset of the block's). The existing group and pen-name tests must pass unchanged.
- **Files likely touched:** `src/MediaEngine.Storage/PersonCreditSql.cs`, `src/MediaEngine.Api/Services/Display/DisplayWorkProjectionReader.cs`, `src/MediaEngine.Api/Services/ReadServices/PersonCreditReadService.cs`, `tests/MediaEngine.Storage.Tests/PersonCreditSqlTests.cs`, `tests/MediaEngine.Api.Tests/PersonCreditReadServiceTests.cs`, `tests/MediaEngine.Performance.Tests/` (guardrails, Q3)
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** the new and existing tests pass; `compare` reports `person-credits:*` and `detail:person` identical (tied rows excepted and recorded, D6); the person-credits guardrails (Q3) pass; preparing the statement takes under 0.2 s on the benchmark library on a quiet machine (measured once, recorded in section 9).

### WP3 — Series search
- **What it does:** in `CollectionSearchReadService.SearchAsync` take the `primary_person_media_credits` subquery out of `matched` and keep its stored fallbacks there as `AuthorFallback`; carry `AuthorFallback` into `search_page`; add `PersonCreditSql.ScopedCreditsCte` over `SELECT AssetId FROM search_page` as one saved step; the outer select works out `Author` from it for `page.AssetId` with the same media-type rules and order, then `page.AuthorFallback`. For the no-index name search, do what Q1 decides: (a) delete the no-index path and the one-argument constructor, and move `UniversalSearchReadServiceTests` "Frank Herbert" to the index path (with a real `SearchIndexRepository` after `RebuildAsync`); or (b) replace the per-file `EXISTS` with `ma.id IN (...)` over WP2's block for `persons WHERE name LIKE @like` followed by `ScopedCreditsCte` and `person_name LIKE @like` (both named once inside `matched`).
  - Tests: `SeriesSearch_ShowsTheNarratorForAnAudiobookWithoutAnAuthor`, `SeriesSearch_ShowsTheFilesDirectorBeforeTheWorksStoredDirector` (the existing author rules), and for (b) `SeriesSearch_WithoutAnIndex_FindsAWorkByItsNarratorsName`.
- **Files likely touched:** `src/MediaEngine.Api/Services/ReadServices/CollectionSearchReadService.cs`, `tests/MediaEngine.Api.Tests/UniversalSearchReadServiceTests.cs` or a new `CollectionSearchReadServiceTests.cs`, `tests/MediaEngine.Performance.Tests/`
- **Depends on:** WP1; WP2 for Q1 (b) only. Runs beside WP2 for Q1 (a) (different files).
- **Size:** Standard
- **Done when:** the tests pass; `compare` reports `series-search:*` identical; the series search guardrail (Q3) passes; the `common-word` section takes seconds, not about a minute.

### WP4 — "Add media" (only if Q2 is (a))
- **What it does:** in `CollectionMediaLookupReadService.LookupAsync` pass the allowed works as one JSON array of hex ids (`w.id IN (SELECT work_id FROM allowed_works)`, where `allowed_works AS MATERIALIZED (SELECT unhex(value) AS work_id FROM json_each(@allowedWorkIds))`) instead of one value per work, and start `work_descendants` from those works only (no change for an administrator; less work for a profile that sees part of the library). Add the conversion as one shared helper beside `GuidSql.ToBlob` (for example `GuidSql.ToJsonHexArray`) so the follow-up in section 8 reuses it. Rows, order and the C# de-duplication stay.
  - Tests: `AddMedia_WithMoreThanThirtyTwoThousandVisibleWorks_ReturnsTheFirstPage` (fails today with "too many SQL variables").
- **Files likely touched:** `src/MediaEngine.Api/Services/ReadServices/CollectionMediaLookupReadService.cs`, `tests/MediaEngine.Api.Tests/`, `tests/MediaEngine.Performance.Tests/`, `benchmarks/README.md` (the note that `collection-lookup` needs under about 30,000 works)
- **Depends on:** WP1
- **Size:** Standard
- **Done when:** the test passes; `compare` reports `collection-lookup:*` identical; the `collection-lookup` sections run on the `full` (100,000-work) benchmark file.

### WP5 — Guardrails and close-out
- **What it does:** the guardrails Q3 decides, in `tests/MediaEngine.Performance.Tests/CreditReadGuardrailTests.cs`, written in the style of `DisplayReadGuardrailTests` (operations counted, budgets about 1.5 times the measured cost, the measured number written beside each budget, a failure message that names the likely cause). A final full `snapshot` compares identical with the WP1 baseline.
- **Files likely touched:** `tests/MediaEngine.Performance.Tests/CreditReadGuardrailTests.cs`, `tests/MediaEngine.Performance.Tests/AudiobookYearGuardrailTests.Collections.cs` (the person-credits year budget's comment, if its numbers move)
- **Depends on:** WP2, WP3 (and WP4 if built)
- **Size:** Standard
- **Done when:** the guardrails pass; with today's code put back locally each fails (record both numbers, do not commit); the CI-strength build and all tests pass.

## 7. Local checks

- **L1. A person's page on the real library.** On SHY-DESKTOP, after `C:\Temp\real_import` has been ingested, open three people: a prolific author, a narrator, and someone in a band or group. Each page's list of works should look exactly as before (same works, roles, "With <group>" rows) and appear within about a second. Failure: a missing or extra work, a role that changed, or the list taking several seconds.
- **L2. Series search on the real library.** In the "add to a series" search, type a common word (for example "the") and an author's surname. Results appear within about a second with the same authors as before. Failure: a result without the author it had before, or a wait of more than a few seconds.

## 8. Risks and open questions

- **Risks:**
  - *A credit drops off a person's page.* If the candidate step misses a file the whole-library list credits to the person (D2), that work disappears. The candidate-superset test in WP2 checks every person of the test library; the benchmark comparison checks the samples.
  - *Statement preparation blows up.* A step named in several places without being saved is copied at each place. D3; WP2 and WP3 measure preparation and record it; Q3 (b) would guard it.
  - *The person page changes.* WP2 moves its candidate chain into the shared block; `detail:person` must compare identical.
  - *Two statements are not one snapshot.* A credit added between them shows on the next visit, as on the person page today.
- **Follow-up remediations** (found while planning; not built by this spec, picked up once it is built, Shy 2026-10-20):
  - **R1. Reads that send the whole visible library as separate values.** Besides "add media" (Q2), universal search's people results send every visible file as one value each (`UniversalSearchReadService`, `AllowedAssetIds`, and `AllowedWorkIds` for its series counts), and the library feed sends its allowed works the same way when it is given a list (`LibraryWorkFeedReadService`, `AllowedWorkIds`). SQLite on this product accepts at most 32,766 values in one statement (checked: `MAX_VARIABLE_NUMBER=32766`), so above that those reads fail outright; below it, sending them dominates the time ("add media": about 0.8 of its 0.95 s at 4,000 works). The benchmarks README already notes that `collection-lookup` only runs below about 30,000 works for this reason. *Remediation:* search `src/` and `tools/` for every `IN @…` fed from a library-sized set (such as `LoadVisibleWorkIdsAsync`), switch each to WP4's one-list helper, add a behaviour test per read with more than 32,766 ids, and a guardrail in the style of `DapperGuidListParameterGuardrailTests` that rejects library-sized id lists bound as separate values.
  - **R2. About 14 other reads use the whole-library credit list.** Universal search's people results (an `EXISTS` once for every matching person), the series browse page (three places), the contributor shelf, people reads (four), a work's cast credits, artwork (two services), a series detail read, access checks, collection rules, the metadata editor and the person store (six). None is measured; each may be as slow as the two reads here, and one that checks it once per row is far worse (series search: 3 billion operations). *Remediation:* measure each on the performance-test library at 4,000 and 8,000 works (ids masked when comparing, see section 6), say which build the list once per visit and which once per row, and move the slow ones to `PersonCreditSql.ScopedCreditsCte` for the files they need and D4's candidate step for "files that may credit these persons", with the same same-values comparison and guardrails as this spec.
- **Open questions:**
  - **Q1. Series search when there is no search index.** The Engine always has a search index, and an empty answer from it already returns no results, so the no-index name search is used only by one test. (a) **Delete it**: series search always asks the index, and the test moves to the index. Simplest, nothing changes for anyone using Tuvima, and there is one rule for what series search finds. (b) **Keep it and make it fast**: find the persons whose name contains the text first, then their files (prototype: 1.6–3.7 million operations instead of the 20.7 billion, 570 s, the earlier spec measured at 4,000 works). It keeps a second, wider search (any stored value, any credited person) that nobody reaches. **Recommendation: (a)**, per the "best long-term fix before beta" rule: (b) maintains code no screen uses.
  - **Q2. "Add media".** The brief named its representative file as the cost; the prototype shows the representative file is cheap and the list of 4,123 separate values is what costs (section 6). (a) **Include it (WP4)**: one list instead of one value per work, with a shared helper. The screen gets about 6–10 times faster (0.95 s to 0.17 s at 4,000 works) with the same rows, and stops failing above about 32,000 visible works. (b) **Leave it for a separate spec** that fixes every read sending whole-library lists (section 8) at once. (c) Leave it. **Recommendation: (a).** It is small, measured and proven identical, it removes a hard failure on large libraries, and its helper is what the follow-up reuses for the other reads.
  - **Q3. Guardrails.** (a) **Work budgets and a text check**: a fixed budget at 4,000 works for a person's credits (an author, about 1,400 thousand operations against 919 measured; a group member, about 1,700 against 1,113) and for series search (40 index matches, about 500 against 332); a "twice the library costs at most 2.5 times the work" check for a person's credits (prototype 1.9 times); a check that neither read's code names the whole-library credit list; and for "add media" (if Q2 is (a)) the behaviour test with more than 32,766 allowed works. All deterministic. A return to today's form fails a budget by about 100 times (person) or 10,000 times (search); the growth check alone would not catch it (today's form also grows 2 times). (b) As (a), plus a timing check that preparing a person's credits statement stays under 0.5 s (today 0.23–0.28 s, prototype 0.11–0.14 s). Timings vary on a busy machine (this planning saw 0.1 s and 5 s for the same statement), so it may fail spuriously. (c) Only the text check. **Recommendation: (a)**, with preparation measured once and recorded per packet, as the earlier specs did.

## 9. Gaps and drift

Not built yet.
