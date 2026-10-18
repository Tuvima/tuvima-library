# Spec: Numbered data store upgrades and contributor guide

> **Status:** Draft · **Version:** 1 · **Last updated:** 2026-10-17 · **Related docs:** [Product overview](../product/overview.md) · [ADR 0001 — SQLite and Dapper](../architecture/decisions/0001-sqlite-and-dapper.md) · [EF benchmark results](../proposals/ef-vs-dapper-benchmark-results-2026-10.md) · [ADR 0003 — Storage epoch](../architecture/decisions/0003-storage-epoch-guid-blob.md) · [Database schema reference (public)](../../docs/reference/database-schema.md)

## 1. Goal

Make changing Tuvima's data store easy for contributors and safe for people's libraries. Today, every structure change is another "does this already exist? if not, add it" check in one ever-growing startup routine. We replace that with **numbered upgrade steps**: each change is the next numbered file, it runs exactly once, and the data store records which steps it has had. A short **contributor guide** explains how to make a data change from start to finish. This is how we answer the "EF would standardise contribution" concern without moving off Dapper (ADR 0001, reaffirmed 2026-10-17).

## 2. Scope

**In scope**
- **Ordered steps.** A numbered, ordered list of upgrade steps applied once each at Engine startup, recorded in the data store with when each one ran.
- **A baseline step** that creates today's complete data structure: everything the current structure file and startup routine produce together.
- **Retiring the old routine.** The "add it if missing" startup routine and the one-time data conversions it carries (they only convert pre-release data) are removed.
- **A safety copy** of the data store before any pending step runs.
- **Clear refusals.** The Engine refuses to start, with a plain message, when a step fails, when the data store is newer than this Engine, or when an already-applied step has been edited.
- **A readable "current structure" file** for contributors, kept in step with the numbered steps by an automatic check and a refresh command.
- **Guardrail tests** for numbering, one-time application and the structure snapshot.
- **The contributor guide** on the public docs site (Develop section), plus updates to the internal rules and developer docs that describe today's routine.

**Out of scope**
- Moving any code to Entity Framework, or changing how queries are written (ADR 0001).
- Reversible "down" steps. Undoing an upgrade means restoring the safety copy (D4).
- Changing what data Tuvima stores. The baseline reproduces today's structure exactly.
- The library list and Home slowness found by the benchmark. That is a separate investigation.
- Ending pre-release data store resets. The storage epoch reset (ADR 0003) stays for breaking pre-release changes; when to stop using it is a separate decision before beta.

## 3. Decisions made

- **D1.** Stay on Dapper. Data structure changes move to numbered upgrade steps, and a contributor guide is added. (Shy, 2026-10-17, after the EF benchmark.)
- **D2.** Steps only go forward and each runs exactly once. A step is never edited after it has been released. Fixing a mistake means adding a new step. *Reason: Reliability. Every installation reaches the same structure by the same path.*
- **D3.** Two kinds of step share one numbering: a plain SQL step for structure changes, and a code step for reshaping existing data that SQL can't express cleanly. *Reason: Extensibility. The current routine already needs both kinds.*
- **D4.** No undo steps. Before running any pending step, the Engine makes a safety copy of the data store; recovery means restoring that copy. *Reason: Reliability with less to maintain. Hand-written undo steps are rarely tested and often wrong.*
- **D5.** Each step runs in its own transaction, with its record written in the same transaction. A failed step leaves the data store exactly as it was before that step.
- **D6.** The Engine refuses to start, with a message naming the data store, the step and the safety copy, when:
  - a step fails;
  - the data store has a step this Engine doesn't know (it is newer than the Engine);
  - an applied step's content no longer matches what was recorded.
- **D7.** The readable current-structure file is **generated** from the steps, never hand-edited. An automatic test fails when it is out of date, and a refresh command regenerates it. This matches how wire snapshots are handled today.

## 4. UI behaviour

The upgrade itself has no Dashboard screen; the Engine applies it during startup.

| Situation | What the person sees |
|---|---|
| Normal (no pending steps) | Nothing changes. The Engine starts as it does today. |
| Steps pending | Startup takes a little longer. The Engine log and console say which steps ran and where the safety copy is. The Dashboard shows its usual "Engine starting / unreachable" state until the Engine is ready. |
| A step fails | The Engine doesn't start. The log and console give the step's number and name, what went wrong, that the data store was left unchanged by that step, and the safety copy's location. Docker, the Windows installer and `dotnet run` all show this in their normal Engine output. |
| Data store newer than this Engine (e.g. after going back to an older release) | The Engine doesn't start. The message says the data store was upgraded by a newer Tuvima, to install that version or restore an older backup, and where the backups are. |
| An applied step was edited (a contributor mistake) | The Engine doesn't start. The message names the step and says applied steps must never be edited; add a new step instead. |
| Administrator wants to see the data store version | Out of scope unless Shy decides otherwise (Q3). |

## 5. Data / model changes

- **New record of applied steps.** For each step: its number, name, a fingerprint of its content, when it ran, and which Engine version ran it.
- **The baseline step** creates exactly the structure a fresh install has today, with no new or removed tables or columns.
- **Existing libraries (pre-release).** Following the "resets are acceptable" rule, the storage epoch is bumped, so existing data stores are backed up and reingested into the new baseline (Q1). Shy's own test library would need one reingest.
- **Retired conversions.** One-time conversions of old pre-release data (packed values, local-only accounts, legacy profile lists and similar) are deleted, not carried forward.
- **Safety copies** are kept next to the existing backups. Each is about the size of the data store; how many to keep is Q2.

## 6. Work packets

### WP1 — Upgrade step runner
- **What it does:** builds the runner and the applied-steps record.
  - It finds SQL steps (embedded `NNNN_short_name.sql` resources) and code steps (classes implementing a small step interface, numbered the same way), then sorts and validates them: numbers are unique and contiguous, and names are well-formed.
  - It applies pending steps one transaction each (D5), recording number, name, SHA-256 of the content, time and Engine version.
  - It enforces the refusals in D6 with actionable messages.
  - It supports steps that must run with foreign keys off (SQLite table rebuilds), declared per step.
  - It is not wired into startup yet.
- **Files likely touched:** new `src/MediaEngine.Storage/Schema/Upgrades/` (runner, step interface, step discovery); `src/MediaEngine.Storage/MediaEngine.Storage.csproj` (embedded resources); new tests in `tests/MediaEngine.Storage.Tests/` on real temp SQLite.
- **Depends on:** none
- **Size:** Complex
- **Done when:** these tests pass:
  - pending steps apply once and in order;
  - a rerun applies nothing;
  - a failing step rolls back and leaves no record;
  - an unknown higher step refuses;
  - an edited applied step refuses;
  - gaps or duplicate numbers fail validation;
  - a foreign-keys-off step works.

### WP2 — Baseline step and retirement of the startup routine
- **What it does:**
  - **Baseline.** Writes `0001_baseline.sql` equal to today's complete structure: the current `schema.sql` plus every effect of `SchemaMigrator.RunStartupTasks`. That means all tables, columns, indexes, triggers, the 3 FTS5 tables and the seed rows, including the `storage_epoch` row.
  - **Proof.** A one-off comparison test proves a fresh data store built the old way and the new way have identical normalised `sqlite_master` and `pragma_table_info` output. Run it once, then delete it with the old path.
  - **Wiring.** Connects the runner into `DatabaseConnection.InitializeSchema` / `RunStartupChecks`, in this order: epoch guard → writable check → integrity check → pending steps (safety copy from WP4 once it lands) → `PRAGMA optimize`.
  - **Retirement.** Removes `SchemaMigrator` (its `Ensure*`, `AddColumnIfMissing` and data-conversion steps) and the whole-file `SchemaInitializer` execution.
  - **Epoch.** Bumps `StorageEpochGuard.CurrentEpoch` (Q1).
- **Files likely touched:**
  - `src/MediaEngine.Storage/SchemaMigrator.cs` (deleted)
  - `src/MediaEngine.Storage/SchemaInitializer.cs`
  - `src/MediaEngine.Storage/DatabaseConnection.cs`
  - `src/MediaEngine.Storage/StorageEpochGuard.cs`
  - `src/MediaEngine.Storage/Schema/schema.sql` (becomes the baseline source or is replaced by the WP3 snapshot)
  - tests that call the migrator directly (to be confirmed by Claude Code)
  - `tests/MediaEngine.Storage.Tests/DatabaseStartupSafetyTests.cs`
- **Depends on:** WP1
- **Size:** Complex
- **Done when:**
  - the old-vs-new structure comparison test passes, then is removed with the old path;
  - the full restore/build/test is green;
  - a fresh Engine start on an empty folder creates the data store, and a second start applies nothing;
  - an old-epoch data store is backed up and reset when `TUVIMA_STORAGE_RESET=1` is set.

### WP3 — Generated structure snapshot and guardrails
- **What it does:**
  - **Snapshot.** Generates a readable, deterministic structure file (normalised `sqlite_master` SQL, sorted) by applying every step to an empty data store, plus a refresh script.
  - **Guardrail tests:**
    - the snapshot is current;
    - step numbers are contiguous;
    - every released step's fingerprint matches a committed list, so an edit of a merged step fails CI;
    - no new code reintroduces "add column if missing" style startup patching outside the step folder.
- **Files likely touched:**
  - new `tools/Update-SchemaSnapshot.ps1`
  - the snapshot file under `src/MediaEngine.Storage/Schema/`
  - new `tests/MediaEngine.Storage.Tests/SchemaUpgradeGuardrailTests.cs`
- **Depends on:** WP2
- **Size:** Standard
- **Done when:**
  - the guardrail suite passes;
  - editing a byte of `0001_baseline.sql`, or changing the structure without refreshing the snapshot, makes it fail with a message pointing at the refresh command or at "add a new step".

### WP4 — Safety copy before upgrades
- **What it does:**
  - Before the first pending step runs (and only then), it writes a consistent copy of the data store (`VACUUM INTO`) into the existing backups location, named with the date and the from/to step numbers.
  - It keeps the newest copies (count from Q2) and deletes older safety copies only, never user-made backups.
  - It refuses to upgrade, with a clear message, if there isn't enough free disk space for the copy.
- **Files likely touched:**
  - the runner from WP1
  - wherever the backups folder is resolved (`src/MediaEngine.Api/Services/DatabaseBackupService.cs` today; the Storage layer may need the path passed in; to be confirmed by Claude Code)
  - tests
- **Depends on:** WP1 (can run alongside WP2 if the files don't overlap; otherwise after it)
- **Size:** Standard
- **Done when:** tests show:
  - a copy appears only when steps are pending;
  - it opens as a valid data store at the "from" step;
  - old safety copies are pruned to the limit;
  - user backups are untouched;
  - low disk space refuses with a message.

### WP5 — Contributor guide and doc updates
- **What it does:** writes the public Develop page "Changing the data store". It covers:
  - adding a table, column or index (the next numbered SQL step);
  - reshaping data (a code step);
  - never editing a released step;
  - refreshing the snapshot;
  - updating `docs/reference/database-schema.md`;
  - adding startup safety tests;
  - the Dapper conventions contributors must follow: `IDatabaseConnection.CreateConnection()` per unit of work, the synchronous write-transaction template, the `Repository` suffix, internal GUIDs as 16-byte BLOBs, no SQL in Razor;
  - how to test with real temp SQLite;
  - what the guardrails will catch.

  It also updates every doc that describes the old routine.
- **Files likely touched:**
  - new `docs/develop/changing-the-data-store.md` and its `website/publication.json` entry
  - `docs/reference/database-schema.md` (its "upgrade" notes and source list)
  - `docs/architecture/architecture-summary.md`
  - `docs/tutorials/dev-setup.md`
  - `.claude/rules/data-store.md`
  - `AGENTS.md` (search for SchemaMigrator)
  - `engineering/architecture/overview.md`
  - `CONTRIBUTING.md` (one link, then re-sync the mirror)
- **Depends on:** WP2, WP3, WP4
- **Size:** Standard
- **Done when:**
  - the docs site builds with the new page published;
  - no current doc mentions `SchemaMigrator` or "idempotent startup migrations" as the way to change the data store;
  - a reviewer following the guide alone can add a test column in a scratch branch and pass all guardrails.

## 7. Risks and open questions

**Risks**
- **The baseline could silently differ from today's structure,** for example a column or index added only by the old routine. *Noticed by:* the WP2 old-vs-new comparison test, which must pass before the old path is deleted.
- **A long code step on a big library could make startup slow.** *Mitigation:* the guide says code steps must work in batches and log progress; the Engine log shows step timings.
- **Safety copies use disk space,** about the data store's size each, which matters on a NAS. *Mitigation:* the free-space check and the pruning limit (Q2).
- **SQLite table rebuilds need foreign keys switched off and back on,** and a mistake there can leave orphaned rows. *Mitigation:* the per-step flag in WP1, plus a `PRAGMA foreign_key_check` after such steps that fails the step if anything is wrong.

**Open questions**
- **Q1. Existing data stores when this lands.**
  - Options:
    - (a) bump the storage epoch, so existing data stores are backed up and reingested once (Shy's test library included);
    - (b) recognise a data store whose structure exactly matches the baseline and mark it as already at step 1, so there is no reingest; anything else still resets.
  - Recommendation: **(a)**. It follows the "resets are acceptable, no compatibility shims" rule and keeps no special-case code. (b) avoids one reingest but adds code that exists only for that moment.
- **Q2. How many safety copies to keep.**
  - Options: 1, 2 or 3; or a size cap.
  - Recommendation: **2**. That covers "the last upgrade went wrong and I only noticed after the next one" without doubling disk use further.
- **Q3. Show the data store version to administrators?**
  - Options:
    - (a) no UI; the log only;
    - (b) a read-only "Data store version: step N, upgraded on date" line in the existing Settings system/about information.
  - Recommendation: **(b)**, as a small extra packet after WP2. It helps with support questions ("which version are you on?") and is operational state, which belongs in Settings.
- **Q4. Where the readable structure file lives.**
  - Options:
    - (a) keep the name `schema.sql`, now generated, so existing links and habits keep working;
    - (b) a new name such as `schema.snapshot.sql`, so nobody mistakes it for an editable source.
  - Recommendation: **(b)**, with a header comment saying it is generated and how to refresh it.
