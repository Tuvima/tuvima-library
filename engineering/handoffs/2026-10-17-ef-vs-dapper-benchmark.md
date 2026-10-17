# Handoff: Dapper vs Entity Framework benchmark (revisits ADR 0001)

> Written 2026-10-17 by the planning side for Shy (product owner, not a developer). Status: **decision pending**. ADR 0001 stays Accepted until Shy decides after reading the results. This is a measurement task only; nothing in the product changes.

## Decision so far
Shy chose "benchmark first" before deciding whether to move from Dapper to Entity Framework (EF) Core.

Planning-side recommendation, for context: **stay on Dapper** and add a small numbered-upgrade-script migrator plus a contributor guide for data changes. Reasons: none of ADR 0001's revisit triggers has fired (real users needing reversible migrations, query-writing being the main bug source, a second database engine); a move touches ~200 source files, 97 `*Repository` classes and ~1,500 query call sites, plus `schema.sql` (~3,500 lines), `SchemaMigrator` (~2,400 lines) and ~50 guardrail suites; users would see no benefit. Shy's counterpoints: the app is open source (AGPLv3) so EF might standardise contribution; and it runs locally, so raw speed may matter less. The benchmark should say how much speed and effort actually differ.

## Goal
Give Shy numbers and a short plain-language report so the EF decision rests on evidence: how much slower or faster EF Core is on Tuvima's heaviest queries, and how much of the code would have to change.

## Scope
In:
1. Find the heaviest read paths (scout first; likely candidates to check, not confirmed: library item lists in `LibraryItemRepository`, search in `SearchIndexRepository`/`SearchResultsCacheRepository`, home visibility in `HomeVisibilitySql`, the enrichment/claims reads). Pick about 5 and name why.
2. Count queries by kind: simple single-table CRUD vs multi-join/aggregate/FTS/raw-SQL-only. Report the split.
3. Build a throwaway benchmark (separate project, not part of `MediaEngine.slnx` shipping outputs, like `benchmarks/` in other Tuvima repos) that runs the same 5 queries through current Dapper code and through an EF Core equivalent, on the same real temp SQLite file, with a synthetic large library (suggest 100k works / 500k claims / 50k artwork rows; state the size used) and the production SQLite settings (WAL, `synchronous=NORMAL`, busy timeout, page cache, mmap cap from `.claude/rules/data-store.md`).
4. Measure: median and p95 time, allocations, and memory for each query on both; also one concurrency run (a background write loop plus reads) to compare lock behaviour.
5. List which guardrail suites assume Dapper or short-lived `IDatabaseConnection.CreateConnection()` and would need rewriting.
6. Note whether the current idempotent `SchemaMigrator` could be replaced by EF migrations, or by a small numbered-script migrator, and what each would take.

Out: any change to shipped code, the schema, or ADR 0001; committing `*.db`, `bin/`, `obj/`; a full EF port. EF Core is MIT-licensed (AGPLv3-compatible); still record the license check for any new benchmark-only package.

## How to work
- Plan first, per `CLAUDE.md` and `.claude/rules/product-owner.md`; use the scout for the finding steps. Benchmark code lives on a feature branch and is not merged without Shy.
- Read narrowly (`AGENTS.md` and `schema.sql` are large).
- Stop and ask Shy if a result is ambiguous or the work would exceed this scope.

## Done when
- A report at `engineering/proposals/ef-vs-dapper-benchmark-results-2026-10.md` containing: queries chosen and why; query-kind split; a table of Dapper vs EF Core median, p95, allocations, memory; the concurrency result; guardrail suites affected; migration options; known limits of the test (hardware, data size, synthetic data).
- A one-paragraph Plain English summary ending with a recommendation (stay, mix, or move) and its reason in terms of Maintenance, Extensibility, Privacy, Reliability, Performance.
- A PR with the report (and the benchmark project if Shy wants it kept), assigned to Shy for review. Do not change ADR 0001's status; Shy decides.

## Plain English summary
Before deciding whether to swap the way Tuvima talks to its data store, we measure both on a big pretend library: how fast they are, how much memory they use, and how much code would have to change. Nothing shipped changes. The result is a short report that lets Shy decide with numbers.
