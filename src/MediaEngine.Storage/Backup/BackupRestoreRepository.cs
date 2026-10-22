using System.Globalization;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Backup;

/// <summary>What a restore did, in counts. Never names a title or a profile.</summary>
/// <param name="RestoreId">The id of the restore record, which the waiting rows belong to.</param>
/// <param name="AppliedRows">Rows written straight into place (settings, and personal state that points at no title).</param>
/// <param name="WaitingRows">Rows that started waiting for their title (including rows a backup carried over from an earlier restore).</param>
/// <param name="ConfigurationTables">Configuration tables that were written.</param>
/// <param name="PortableValuesRestored">Machine-protected values that were protected again with this computer's key.</param>
/// <param name="PortableValuesSkipped">Machine-protected values the backup could not make readable; they were written as they were and work only on the computer that made the backup.</param>
/// <param name="RowsDropped">Rows left out because they point at something this data store does not have, or had nothing to attach to.</param>
public sealed record BackupRestoreResult(
    Guid RestoreId,
    int AppliedRows,
    int WaitingRows,
    int ConfigurationTables,
    int PortableValuesRestored,
    int PortableValuesSkipped,
    int RowsDropped);

/// <summary>
/// Puts an export (<see cref="BackupExport"/>) into the current data store: settings straight back into their tables, and every row
/// that points at a title into the waiting area (<c>waiting_personal_state</c>), where <see cref="WaitingPersonalStateRepository"/>
/// attaches it when the title is in the Library. Everything happens in one write transaction, so a restore either completes or
/// leaves the data store as it was (backup and restore spec D3 to D5).
/// </summary>
public sealed class BackupRestoreRepository
{
    private const string WaitingTable = "waiting_personal_state";
    private const string RestoresTable = "backup_restores";

    private readonly IDatabaseConnection _db;

    /// <param name="db">The data store.</param>
    public BackupRestoreRepository(IDatabaseConnection db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Imports <paramref name="export"/>. A Configuration row with no title is written with INSERT OR REPLACE (the backup's row wins
    /// over a built-in one a fresh data store already holds); a personal row with no title with INSERT OR IGNORE (a row that already
    /// exists is newer); every other row waits, starting its waiting period at <paramref name="now"/> (the period itself is applied when
    /// waiting rows are cleared, see <see cref="WaitingPersonalStateRepository.RunPass"/>). A machine-protected
    /// value is protected again through <paramref name="codec"/>; with no codec the import refuses rather than write it unprotected.
    /// </summary>
    /// <param name="export">What the backup holds.</param>
    /// <param name="backupFileName">The backup's file name, recorded with the restore (it names no title).</param>
    /// <param name="backupCreatedAt">When the backup was made, when known.</param>
    /// <param name="codec">Protects portable values with this computer's key; null when none is available.</param>
    /// <param name="now">The time of the restore, which is when each waiting row starts waiting.</param>
    /// <param name="ct">Cancels the import; nothing is written.</param>
    /// <param name="restoreId">The id to record the restore under (a restore that is staged first carries its own id, so a crash after the import committed is recognised); a new id when null.</param>
    /// <exception cref="InvalidOperationException">The backup holds a portable value and no codec was given, or a table the backup holds is not classified.</exception>
    public BackupRestoreResult Import(
        BackupExport export,
        string backupFileName,
        DateTimeOffset? backupCreatedAt,
        IBackupPortableValueCodec? codec,
        DateTimeOffset now,
        CancellationToken ct = default,
        Guid? restoreId = null)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFileName);

        // The write lock is taken by the data store's own helper, like every other write; the work inside is synchronous.
        return _db.ExecuteWriteAsync(
            (connection, transaction, token) => ImportCore(connection, transaction, export, backupFileName, backupCreatedAt, codec, now, restoreId ?? Guid.NewGuid(), token),
            ct).GetAwaiter().GetResult();
    }

    /// <summary>True when a restore with this id has been recorded, which means its import committed.</summary>
    public bool HasRestore(Guid restoreId)
    {
        using var connection = _db.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {RestoresTable} WHERE id = $id";
        command.Parameters.AddWithValue("$id", GuidSql.ToBlob(restoreId));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static BackupRestoreResult ImportCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BackupExport export,
        string backupFileName,
        DateTimeOffset? backupCreatedAt,
        IBackupPortableValueCodec? codec,
        DateTimeOffset now,
        Guid restoreIdValue,
        CancellationToken ct)
    {
        // Parents are written first, but the check waits for the end, so the rows of a table cycle can go in any order.
        Execute(connection, transaction, "PRAGMA defer_foreign_keys = ON");

        var structure = RestoredRowSql.ReadStructure(connection, transaction);
        var foreignKeys = RestoredRowSql.ReadForeignKeys(connection, transaction, structure);
        var restoreId = GuidSql.ToBlob(restoreIdValue);
        var createdAt = RestoredRowSql.Timestamp(now);

        Execute(
            connection,
            transaction,
            $"INSERT INTO {RestoresTable} (id, restored_at, backup_file_name, backup_created_at, applied_rows, waiting_rows) VALUES ($id, $at, $file, $created, 0, 0)",
            ("$id", restoreId),
            ("$at", createdAt),
            ("$file", backupFileName),
            ("$created", backupCreatedAt is { } made ? RestoredRowSql.Timestamp(made) : (object)DBNull.Value));

        var titles = export.Titles.ToDictionary(title => title.Key, StringComparer.Ordinal);
        var waiting = new List<WaitingEntry>();
        var referenced = foreignKeys.Select(key => (key.ParentTable, key.ParentColumn)).ToHashSet();
        var byValue = new Dictionary<(string Table, string Column, string Key), WaitingEntry>();
        var applied = 0;
        var dropped = 0;
        var configurationTables = 0;
        var portableRestored = 0;
        var portableSkipped = 0;
        var carriedOver = 0;

        foreach (var table in OrderByForeignKeys(export.Tables, foreignKeys))
        {
            ct.ThrowIfCancellationRequested();
            var rule = BackupTableClassification.Find(table.Table)
                ?? throw new InvalidOperationException($"The backup holds a table this version does not classify: {table.Table}.");
            if (!structure.TryGetValue(table.Table, out var liveColumns))
            {
                dropped += table.Rows.Count;
                continue;
            }

            if (string.Equals(table.Table, WaitingTable, StringComparison.OrdinalIgnoreCase))
            {
                carriedOver += CarryOverWaitingRows(connection, transaction, table, liveColumns, restoreId, ref dropped);
                continue;
            }

            var group = rule.Kind == BackupDataKind.Configuration ? WaitingGroup.Configuration : WaitingGroup.Personal;
            var written = 0;
            var ownerIndex = rule.OwnerColumn is null ? -1 : IndexOf(table.Columns, rule.OwnerColumn);
            foreach (var row in table.Rows)
            {
                if (row.Any(value => value is BackupValue.TitleRef))
                {
                    var entry = new WaitingEntry(group, table.Table, OwnerOf(ownerIndex < 0 ? null : row[ownerIndex]), new WaitingRowData(table.Columns, row, []));
                    waiting.Add(entry);
                    Register(byValue, referenced, entry, table.Table, table.Columns, row);
                    continue;
                }

                // A row that hangs off a row which is waiting (the items of a bookmark pack) waits with it.
                var parent = FindWaitingParent(byValue, foreignKeys, table.Table, table.Columns, row);
                if (parent is not null)
                {
                    parent.Children.Add(new WaitingRowChild(table.Table, table.Columns, row));
                    Register(byValue, referenced, parent, table.Table, table.Columns, row);
                    continue;
                }

                var values = new List<object?>(row.Count);
                for (var i = 0; i < row.Count; i++)
                {
                    values.Add(StoredValue(rule, table.Table, table.Columns, row, i, codec, ref portableRestored, ref portableSkipped));
                }

                var verb = rule.Kind == BackupDataKind.Configuration ? "INSERT OR REPLACE" : "INSERT OR IGNORE";
                written += RestoredRowSql.Insert(connection, transaction, verb, table.Table, liveColumns, table.Columns, values);
            }

            // The record of earlier restores is history, not something put back.
            if (string.Equals(table.Table, RestoresTable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            applied += written;
            if (rule.Kind == BackupDataKind.Configuration && written > 0)
            {
                configurationTables++;
            }
        }

        // A person's choices and corrections on catalogue rows always belong to a title: they wait for it.
        foreach (var table in export.Choices)
        {
            AddRowsThatNameATitle(table, WaitingGroup.Choice, waiting, ref dropped);
        }

        foreach (var table in export.Corrections)
        {
            AddRowsThatNameATitle(table, WaitingGroup.Correction, waiting, ref dropped);
        }

        foreach (var entry in waiting)
        {
            ct.ThrowIfCancellationRequested();
            InsertWaitingRow(connection, transaction, entry, titles, restoreId, createdAt);
        }

        dropped += RemoveRowsWithMissingParents(connection, transaction);

        var waitingRows = waiting.Count + carriedOver;
        Execute(
            connection,
            transaction,
            $"UPDATE {RestoresTable} SET applied_rows = $applied, waiting_rows = $waiting WHERE id = $id",
            ("$applied", (long)applied),
            ("$waiting", (long)waitingRows),
            ("$id", restoreId));

        return new BackupRestoreResult(
            new Guid(restoreId, bigEndian: true),
            applied,
            waitingRows,
            configurationTables,
            portableRestored,
            portableSkipped,
            dropped);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Rows that start waiting
    // ---------------------------------------------------------------------------------------------------------

    private enum WaitingGroup
    {
        Configuration,
        Personal,
        Correction,
        Choice,
    }

    private sealed class WaitingEntry(WaitingGroup group, string table, byte[]? profileId, WaitingRowData row)
    {
        public WaitingGroup Group { get; } = group;

        public string Table { get; } = table;

        public byte[]? ProfileId { get; } = profileId;

        public WaitingRowData Row { get; } = row;

        public List<WaitingRowChild> Children { get; } = [];
    }

    private static string GroupName(WaitingGroup group) => group switch
    {
        WaitingGroup.Configuration => "configuration",
        WaitingGroup.Personal => "personal",
        WaitingGroup.Correction => "correction",
        _ => "choice",
    };

    private static void AddRowsThatNameATitle(BackupExportTable table, WaitingGroup group, List<WaitingEntry> waiting, ref int dropped)
    {
        if (BackupTableClassification.Find(table.Table) is null)
        {
            throw new InvalidOperationException($"The backup holds a table this version does not classify: {table.Table}.");
        }

        foreach (var row in table.Rows)
        {
            if (row.Any(value => value is BackupValue.TitleRef))
            {
                waiting.Add(new WaitingEntry(group, table.Table, null, new WaitingRowData(table.Columns, row, [])));
            }
            else
            {
                // Nothing says which title it belongs to, so there is nothing to attach it to.
                dropped++;
            }
        }
    }

    /// <summary>Makes a waiting row findable by the values other tables point at it with (its key columns).</summary>
    private static void Register(
        Dictionary<(string Table, string Column, string Key), WaitingEntry> byValue,
        HashSet<(string ParentTable, string ParentColumn)> referenced,
        WaitingEntry entry,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<BackupValue> row)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (referenced.Any(key => string.Equals(key.ParentTable, table, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(key.ParentColumn, columns[i], StringComparison.OrdinalIgnoreCase))
                && row[i] is not BackupValue.NullValue)
            {
                byValue[(table.ToLowerInvariant(), columns[i].ToLowerInvariant(), RestoredRowSql.ValueKey(row[i]))] = entry;
            }
        }
    }

    private static WaitingEntry? FindWaitingParent(
        Dictionary<(string Table, string Column, string Key), WaitingEntry> byValue,
        IReadOnlyList<RestoredForeignKey> foreignKeys,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<BackupValue> row)
    {
        foreach (var key in foreignKeys.Where(key => string.Equals(key.Table, table, StringComparison.OrdinalIgnoreCase)))
        {
            var index = IndexOf(columns, key.Column);
            if (index >= 0
                && row[index] is not BackupValue.NullValue
                && byValue.TryGetValue((key.ParentTable.ToLowerInvariant(), key.ParentColumn.ToLowerInvariant(), RestoredRowSql.ValueKey(row[index])), out var parent))
            {
                return parent;
            }
        }

        return null;
    }

    /// <summary>
    /// Writes one waiting row. Its titles are copied into the row, each re-keyed by its position in the row's own list, so the row
    /// is complete on its own and is removed with nothing else to clean up.
    /// </summary>
    private static void InsertWaitingRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WaitingEntry entry,
        Dictionary<string, BackupTitle> titles,
        byte[] restoreId,
        string createdAt)
    {
        var order = new List<string>();
        BackupValue Rekey(BackupValue value)
        {
            if (value is not BackupValue.TitleRef reference)
            {
                return value;
            }

            var index = order.IndexOf(reference.Key);
            if (index < 0)
            {
                order.Add(reference.Key);
                index = order.Count - 1;
            }

            return BackupValue.FromTitleRef(index.ToString(CultureInfo.InvariantCulture), reference.WasText);
        }

        var row = new WaitingRowData(
            entry.Row.Columns,
            entry.Row.Values.Select(Rekey).ToList(),
            entry.Children.Select(child => child with { Values = child.Values.Select(Rekey).ToList() }).ToList());
        var rowTitles = order
            .Select((key, index) => (titles.TryGetValue(key, out var title) ? title : throw new InvalidDataException("The backup refers to a title it does not list.")) with
            {
                Key = index.ToString(CultureInfo.InvariantCulture),
            })
            .ToList();

        Execute(
            connection,
            transaction,
            $"""
            INSERT INTO {WaitingTable}
                (id, restore_id, group_kind, table_name, profile_id, is_withheld, row_json, titles_json, created_at, last_checked_at)
            VALUES ($id, $restore, $group, $table, $profile, $withheld, $row, $titles, $created, NULL)
            """,
            ("$id", GuidSql.ToBlob(Guid.NewGuid())),
            ("$restore", restoreId),
            ("$group", GroupName(entry.Group)),
            ("$table", entry.Table),
            ("$profile", (object?)entry.ProfileId ?? DBNull.Value),
            ("$withheld", rowTitles.Any(title => title.Withheld) ? 1L : 0L),
            ("$row", WaitingRowJson.WriteRow(row)),
            ("$titles", WaitingRowJson.WriteTitles(rowTitles)),
            ("$created", createdAt));
    }

    /// <summary>
    /// Waiting rows an earlier restore left, carried in a backup taken while they still waited. They keep the time they started
    /// waiting (so the waiting period counts from then); only the restore they belong to changes.
    /// </summary>
    private static int CarryOverWaitingRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BackupExportTable table,
        IReadOnlyList<RestoredColumn> liveColumns,
        byte[] restoreId,
        ref int dropped)
    {
        var restoreIndex = IndexOf(table.Columns, "restore_id");
        var carried = 0;
        foreach (var row in table.Rows)
        {
            var values = row.Select(RestoredRowSql.ToSql).ToList();
            if (restoreIndex >= 0)
            {
                values[restoreIndex] = restoreId;
            }

            try
            {
                carried += RestoredRowSql.Insert(connection, transaction, "INSERT OR REPLACE", table.Table, liveColumns, table.Columns, values);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // A row the data store's own checks refuse (for example a row_json that is not valid JSON) is not a row to keep.
                dropped++;
            }
        }

        return carried;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Values
    // ---------------------------------------------------------------------------------------------------------

    private static object? StoredValue(
        BackupTableRule rule,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<BackupValue> row,
        int index,
        IBackupPortableValueCodec? codec,
        ref int portableRestored,
        ref int portableSkipped)
    {
        var column = columns[index];
        var value = row[index];
        switch (value)
        {
            case BackupValue.Portable portable:
                if (codec is null)
                {
                    throw new InvalidOperationException(
                        $"The backup holds a protected value for {table}.{column}, and there is no key to protect it with on this computer.");
                }

                portableRestored++;
                return codec.Import(table, column, portable.Value, RowByColumn(columns, row));
            case BackupValue.Text when rule.PortableColumns.Contains(column, StringComparer.OrdinalIgnoreCase):
                // The backup could not make this readable, so it only works on the computer that made it. It is kept as it was.
                portableSkipped++;
                return RestoredRowSql.ToSql(value);
            default:
                return RestoredRowSql.ToSql(value);
        }
    }

    private static Dictionary<string, BackupValue> RowByColumn(IReadOnlyList<string> columns, IReadOnlyList<BackupValue> row)
    {
        var byColumn = new Dictionary<string, BackupValue>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            byColumn[columns[i]] = row[i];
        }

        return byColumn;
    }

    private static byte[]? OwnerOf(BackupValue? value) => value switch
    {
        BackupValue.Blob { Value.Length: 16 } blob => blob.Value,
        BackupValue.Text text when Guid.TryParse(text.Value, out var guid) => GuidSql.ToBlob(guid),
        _ => null,
    };

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Order and clean-up
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The tables in an order where a table comes after the tables it has a foreign key to (those that are in the export too), so
    /// parents are written before the rows that point at them. Tables in a cycle, or pointing only at themselves, keep their order.
    /// </summary>
    private static List<BackupExportTable> OrderByForeignKeys(IReadOnlyList<BackupExportTable> tables, IReadOnlyList<RestoredForeignKey> foreignKeys)
    {
        var byName = tables.ToDictionary(table => table.Table, StringComparer.OrdinalIgnoreCase);
        var dependsOn = tables.ToDictionary(
            table => table.Table,
            table => foreignKeys
                .Where(key => string.Equals(key.Table, table.Table, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(key.ParentTable, table.Table, StringComparison.OrdinalIgnoreCase)
                    && byName.ContainsKey(key.ParentTable))
                .Select(key => byName[key.ParentTable].Table)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        // Depth first: a table's parents are visited before it. In a cycle the edge back to a table being visited is left alone
        // (the check is deferred to the end, so those few rows can go in either order).
        var ordered = new List<BackupExportTable>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(BackupExportTable table)
        {
            if (!visited.Add(table.Table))
            {
                return;
            }

            foreach (var parent in dependsOn[table.Table].OrderBy(name => name, StringComparer.Ordinal))
            {
                Visit(byName[parent]);
            }

            ordered.Add(table);
        }

        foreach (var table in tables.OrderBy(table => table.Table, StringComparer.Ordinal))
        {
            Visit(table);
        }

        return ordered;
    }

    /// <summary>
    /// A row put into place can still point at a row this data store does not have (a profile that is not in a profile backup, a
    /// collection that had a missing parent). Such rows are removed, so the restore never leaves a broken link. Returns how many.
    /// </summary>
    private static int RemoveRowsWithMissingParents(SqliteConnection connection, SqliteTransaction transaction)
    {
        var removed = 0;
        for (var pass = 0; pass < 5; pass++)
        {
            var broken = new HashSet<(string Table, long RowId)>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "PRAGMA foreign_key_check";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(1))
                    {
                        throw new InvalidOperationException($"A restored row in {reader.GetString(0)} points at a row that does not exist, and it cannot be removed.");
                    }

                    broken.Add((reader.GetString(0), reader.GetInt64(1)));
                }
            }

            if (broken.Count == 0)
            {
                break;
            }

            foreach (var (table, rowId) in broken)
            {
                removed += Execute(connection, transaction, $"DELETE FROM {RestoredRowSql.Quote(table)} WHERE rowid = $row", ("$row", rowId));
            }
        }

        return removed;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }
}
