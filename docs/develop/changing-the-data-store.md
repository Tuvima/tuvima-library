---
title: "Changing the data store"
description: "Add a table, column or index, reshape existing data, and test and document the change using numbered data store upgrade steps."
audience: developer
category: guide
product_area: storage
status: current
---

# Changing the data store

Use this guide when a change needs a new table, column or index, or has to reshape data that is already stored. A simple structure change takes about ten minutes: write one file, refresh two generated files and run the guardrail tests.

Tuvima keeps its library in one SQLite file, the data store, and reads it with hand-written SQL through Dapper. There is no Entity Framework and no migration tool to learn. The structure changes through numbered upgrade steps.

## How the data store is built

- Every structure change is a numbered **upgrade step** in `src/MediaEngine.Storage/Schema/Upgrades/`.
- At startup the Engine (`DatabaseConnection.InitializeSchema()`) looks at the `data_store_upgrades` table, finds the steps it has not run, and runs each one once, in order, in its own transaction. The step and its record in `data_store_upgrades` commit together, so a failed step leaves the data store exactly as it was.
- `0001_baseline.sql` creates today's whole structure, including the storage epoch row. A fresh data store runs every step from the baseline up. An existing one runs only the steps it is missing.
- `src/MediaEngine.Storage/Schema/schema.snapshot.sql` is a **generated** file: the structure after every step, readable in one place. Never edit it by hand; you refresh it with a command.
- `src/MediaEngine.Storage/Schema/Upgrades/released-steps.txt` lists a fingerprint for every step. It is also generated, and it is how CI notices an edited step.

There are no "down" steps. Before the first pending step runs on an existing data store, the Engine makes a safety copy, and going back means restoring that copy.

## Add a table, column or index

1. Find the highest number in `src/MediaEngine.Storage/Schema/Upgrades/` and create the next file, named `NNNN_short_name.sql`: four digits, then lowercase letters and digits joined by single underscores. For example, `0002_add_work_notes.sql`. Numbers start at 1 and have no gaps. Every `*.sql` file in the folder is embedded in the assembly automatically.

   ```sql
   -- A short note a person can attach to a work.
   CREATE TABLE work_notes (
       work_id    BLOB NOT NULL PRIMARY KEY REFERENCES works(id) ON DELETE CASCADE,
       note       TEXT NOT NULL,
       created_at TEXT NOT NULL DEFAULT (datetime('now'))
   );
   CREATE INDEX idx_work_notes_created_at ON work_notes(created_at);
   ```

2. Keep to what a step may contain:
   - Plain DDL and data statements. You do not need `IF NOT EXISTS`, because a step runs exactly once.
   - No `PRAGMA journal_mode` or `PRAGMA foreign_keys`. The runner owns both.
   - No `BEGIN`, `COMMIT`, `ROLLBACK`, `SAVEPOINT`, `RELEASE`, `VACUUM`, `ATTACH` or `DETACH`. The runner owns the transaction. The `BEGIN ... END` inside `CREATE TRIGGER` is fine.
   - A column added with `ALTER TABLE ... ADD COLUMN` that is `NOT NULL` needs a `DEFAULT`; that is SQLite's rule. Dropping or retyping a column means rebuilding the table, which needs foreign keys off (see the next section).
   - Internal IDs are 16-byte `BLOB`s, as in the example above.

3. Refresh the generated files:

   ```sh
   pwsh -File tools/Update-SchemaSnapshot.ps1
   ```

   The script runs the schema guardrail tests, copies the new snapshot and the new fingerprint line into place, and runs the tests again to confirm they pass.

4. Review the diff and commit three files together: your step, `schema.snapshot.sql` and `released-steps.txt`.

You can keep editing a step you have not merged yet. Run the refresh script again and it replaces your step's fingerprint. Once the step is on `main`, it is released and no longer yours to edit.

## Reshape existing data

When SQL cannot do the job cleanly, such as splitting a value across rows or recomputing a column, write a code step. It shares the numbering with SQL steps.

1. Create `src/MediaEngine.Storage/Schema/Upgrades/AddWorkNotesFromSummary.cs` (or in a subfolder). The file must be named after the type, and the step, its helpers and its SQL must all live in that one file. The guardrail fingerprints the file, so everything the step does is covered.
2. Make the class `internal sealed`, derive from `DataStoreCodeUpgradeStep`, give it a parameterless constructor, and override `Number`, `Name` and `Apply`:

   ```csharp
   internal sealed class AddWorkNotesFromSummary : DataStoreCodeUpgradeStep
   {
       public override int Number => 3;
       public override string Name => "add_work_notes_from_summary";

       public override void Apply(SqliteConnection connection, SqliteTransaction transaction)
       {
           // Use the connection and transaction you are given. Never begin, commit or roll back.
       }
   }
   ```

3. Work in batches and log progress. A library can be large, and a long silent step looks like a hung Engine.
4. If the step rebuilds a table (SQLite's way to drop or retype a column), it needs foreign keys off. In a SQL step, make the exact first line `-- tuvima:foreign-keys-off`. In a code step, override `RequiresForeignKeysOff` to return `true`. The runner switches foreign keys off before the step's transaction, runs `PRAGMA foreign_key_check` before committing, fails the step if any row points at a missing row, and always switches foreign keys back on.
5. Refresh and commit as above.

## Never edit a released step

A released step is one that is on `main`. Every installation must reach the same structure by the same path, so a released step never changes, even to fix a typo in a comment.

If you do edit one, CI fails with a message like this:

> Upgrade step 0001 (baseline) changed after release. Never edit a released step - revert it and add a new step.

Revert your edit, then add the next numbered step that makes the change you wanted. Fixing a mistake in a released step is a new step. An installation that already ran the old step refuses to start if the step's content changes, so the guardrail protects real data stores.

## Built-in lists defined in code

Some built-in rows have their master copy in C#: the known metadata providers, the setup wizard's steps and the default Owner profile. To change one, change the C# list. `ReferenceDataTopUp` runs at every startup after the upgrade steps and adds any missing rows with `INSERT OR IGNORE`. A new provider reaches existing libraries on their next start with no step.

The top-up only adds. It never updates or deletes rows, never changes structure, and touches only `metadata_providers`, `onboarding_workflows`, `onboarding_steps` and `profiles`. A guardrail enforces that. Built-in data written in SQL, such as the registration of the native apps, goes in an upgrade step like any other change.

## Breaking changes before release

Until beta, a change that old data stores cannot survive is allowed, with a reset. Do not write a compatibility reader or an in-place conversion.

1. Change `StorageEpochGuard.CurrentEpoch` in `src/MediaEngine.Storage/StorageEpochGuard.cs` to a new value.
2. Add an upgrade step that updates the stored value to the same text: `UPDATE storage_metadata SET value = '...' WHERE key = 'storage_epoch';`.

At startup, a data store whose epoch is not the current one is refused. Setting `TUVIMA_STORAGE_RESET=1` backs it up and starts a fresh data store to reingest into. A fresh data store runs every step, including yours, so it ends at the new epoch.

## Write code that reads and writes the data store

- Open a short-lived connection with `IDatabaseConnection.CreateConnection()` for each unit of work and dispose it with `using`. `Open()` is for startup, schema and integrity code only; a guardrail fails new uses.
- Do writes through the write-transaction callbacks, which take the write lock and commit for you. The callback is synchronous, with no `await` inside:

  ```csharp
  public Task InsertAsync(Account account, CancellationToken ct = default) =>
      db.ExecuteWriteAsync((conn, tx, token) =>
      {
          token.ThrowIfCancellationRequested();
          InsertAccount(conn, tx, account);
      }, ct);
  ```

- Put SQL in classes with the `Repository` suffix (or read services) in `MediaEngine.Storage`. Razor components never contain SQL, and the Dashboard never uses storage models.
- Store internal IDs as 16-byte `BLOB`s and convert with `GuidSql.ToBlob(id)`. External IDs, such as a Wikidata QID, stay `TEXT`. API JSON still shows GUIDs as strings.
- `canonical_values` holds one scalar per field. Multi-valued metadata goes in `canonical_value_arrays`, never in a packed delimited string.

## Test the change

Use a real temporary SQLite file, never a mock. Build it the way the Engine does, and clean it up with `TestTemp.DeleteDatabase`:

```csharp
var path = Path.Combine(Path.GetTempPath(), $"tuvima_work_notes_{Guid.NewGuid():N}.db");
var database = new DatabaseConnection(path);
try
{
    database.InitializeSchema();
    database.RunStartupChecks();

    using var conn = database.CreateConnection();
    // Exercise your repository or table here.
}
finally
{
    database.Dispose();
    TestTemp.DeleteDatabase(path);
}
```

- Name tests as behaviour sentences, such as `WorkNotes_RemovingTheWork_RemovesItsNote`.
- For a new table, add a startup-safety assertion to `tests/MediaEngine.Storage.Tests/DatabaseStartupSafetyTests.cs`, such as a foreign key cascade or a constraint you rely on.
- Run the focused tests and the guardrails before you push:

  ```sh
  dotnet test tests/MediaEngine.Storage.Tests/MediaEngine.Storage.Tests.csproj --filter "FullyQualifiedName~Guardrail"
  ```

## Document the change

Update [Database schema](../reference/database-schema.md) in the same change. Add the table or column with its type and a plain note on what it holds.

## What the guardrails catch

| Mistake | What fails |
|---|---|
| The structure changed but the snapshot was not refreshed | `SchemaSnapshot_MatchesTheUpgradeSteps`, with the refresh command in the message |
| A released step was edited or removed | `ReleasedUpgradeSteps_AreNeverEdited` and `ReleasedStepsOnMain_AreUnchangedInThisChange` |
| Step numbers skip, repeat or are badly named | `UpgradeStepNumbers_AreContiguousFromOne`, and the Engine refuses to start |
| Structure is changed outside a step (`ALTER TABLE`, `CREATE TABLE`, `table_info` and similar) | `StartupCode_DoesNotPatchTheStructureOutsideUpgradeSteps` |
| The top-up does more than add missing rows to its four tables | `ReferenceDataTopUp_OnlyAddsMissingRowsToItsAllowedTables` |

`ReleasedStepsOnMain_AreUnchangedInThisChange` reads `released-steps.txt` from `origin/main`. Make sure CI fetches it (`fetch-depth: 0`, or `git fetch origin main`).

## What people see when an upgrade runs

On a start with pending steps, the Engine log lists each step with a line starting "Data store upgrade:" and names the safety copy. If a step fails, the data store is newer than the Engine, or an applied step was edited, the Engine writes a plain message that names the step or the data store and says where to find the safety copy or a backup, then exits with code 75. A data store that cannot be written exits with code 74. If there is not enough free disk space for the safety copy, the Engine refuses before changing anything. Administrators also see "Data store version: step N" in Settings. The messages, restore steps and folder locations are in [Operations and recovery](../guides/operations-and-recovery.md#when-tuvima-upgrades-its-data-store).

## Next steps

- [Database schema](../reference/database-schema.md)
- [How to build, test and verify changes](../guides/running-tests.md)
- [Contribute to Tuvima Library](contributing.md)
