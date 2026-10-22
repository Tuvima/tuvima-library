using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Backup;

/// <summary>A column of the live data store: its name, whether it forbids NULL, its default as SQL text and whether it is part of the key.</summary>
internal sealed record RestoredColumn(string Name, bool NotNull, string? Default, bool IsKey);

/// <summary>A foreign key of the live data store: a column of a table and the parent table and column it points at.</summary>
internal sealed record RestoredForeignKey(string Table, string Column, string ParentTable, string ParentColumn);

/// <summary>
/// The statements a restore writes with, shared by the importer and the re-attach pass. Table and column names come from the live
/// data store (a name from a backup is only ever used after it has been matched to a live column), values are always parameters.
/// </summary>
internal static class RestoredRowSql
{
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>The tables that exist in the live data store, with their columns in order.</summary>
    public static Dictionary<string, List<RestoredColumn>> ReadStructure(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var result = new Dictionary<string, List<RestoredColumn>>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT t.name, c.name, c."notnull", c.dflt_value, c.pk
            FROM sqlite_master t
            JOIN pragma_table_info(t.name) c
            WHERE t.type = 'table' AND t.name NOT LIKE 'sqlite\_%' ESCAPE '\'
            ORDER BY t.name, c.cid
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var table = reader.GetString(0);
            if (!result.TryGetValue(table, out var columns))
            {
                columns = [];
                result[table] = columns;
            }

            columns.Add(new RestoredColumn(
                reader.GetString(1),
                reader.GetInt64(2) != 0,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) != 0));
        }

        return result;
    }

    /// <summary>
    /// Every single-column foreign key of the live data store, so a table can be written after the tables it points at. A key with
    /// no named parent column points at the parent's only key column.
    /// </summary>
    public static List<RestoredForeignKey> ReadForeignKeys(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyDictionary<string, List<RestoredColumn>> structure)
    {
        var keys = new List<RestoredForeignKey>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // The foreign keys are read from the data store itself, so the order a restore writes tables in cannot drift from the schema.
        command.CommandText =
            """
            SELECT m.name, f."from", f."table", f."to", f.id
            FROM sqlite_master m
            JOIN pragma_foreign_key_list(m.name) f
            WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite\_%' ESCAPE '\'
            ORDER BY m.name, f.id, f.seq
            """;
        var raw = new List<(string Table, string From, string ParentTable, string? To, long Id)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                raw.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4)));
            }
        }

        // Only single-column keys can be followed one value at a time.
        foreach (var group in raw.GroupBy(entry => (entry.Table, entry.Id)).Where(group => group.Count() == 1))
        {
            var entry = group.Single();
            var parentColumn = entry.To;
            if (parentColumn is null)
            {
                var parentKeys = structure.TryGetValue(entry.ParentTable, out var parentColumns)
                    ? parentColumns.Where(column => column.IsKey).ToList()
                    : [];
                if (parentKeys.Count != 1)
                {
                    continue;
                }

                parentColumn = parentKeys[0].Name;
            }

            keys.Add(new RestoredForeignKey(entry.Table, entry.From, entry.ParentTable, parentColumn));
        }

        return keys;
    }

    /// <summary>
    /// Inserts one row, using the columns the live table has (a column the backup holds that the live table no longer has is left
    /// out). <paramref name="verb"/> is <c>INSERT OR REPLACE</c>, <c>INSERT OR IGNORE</c> or <c>INSERT</c>. Returns the rows written.
    /// </summary>
    public static int Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string verb,
        string table,
        IReadOnlyList<RestoredColumn> liveColumns,
        IReadOnlyList<string> columns,
        IReadOnlyList<object?> values)
    {
        var names = new List<string>();
        var parameters = new List<object?>();
        for (var i = 0; i < columns.Count; i++)
        {
            var live = liveColumns.FirstOrDefault(column => string.Equals(column.Name, columns[i], StringComparison.OrdinalIgnoreCase));
            if (live is null)
            {
                continue;
            }

            names.Add(live.Name);
            parameters.Add(values[i]);
        }

        if (names.Count == 0)
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"{verb} INTO {Quote(table)} ({string.Join(", ", names.Select(Quote))}) VALUES ({string.Join(", ", names.Select((_, index) => "$p" + index.ToString(CultureInfo.InvariantCulture)))})";
        for (var i = 0; i < parameters.Count; i++)
        {
            command.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), parameters[i] ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }

    /// <summary>A stored value as the object a command parameter takes.</summary>
    public static object? ToSql(BackupValue value) => value switch
    {
        BackupValue.NullValue => null,
        BackupValue.Integer integer => integer.Value,
        BackupValue.Real real => real.Value,
        BackupValue.Text text => text.Value,
        BackupValue.Blob blob => blob.Value,
        _ => throw new InvalidOperationException($"A {value.GetType().Name} value has to be turned into a stored value before it is written."),
    };

    /// <summary>A key that tells two stored values apart (a BLOB by its bytes, a number or text by its value and kind).</summary>
    public static string ValueKey(object? value) => value switch
    {
        null => "n",
        byte[] bytes => "b" + Convert.ToHexString(bytes),
        long integer => "i" + integer.ToString(CultureInfo.InvariantCulture),
        double real => "r" + real.ToString("R", CultureInfo.InvariantCulture),
        string text => "t" + text,
        _ => "?" + value,
    };

    public static string ValueKey(BackupValue value) => ValueKey(value is BackupValue.TitleRef or BackupValue.Portable ? null : ToSql(value));

    /// <summary>The current time as the text the data store keeps (UTC, sorts as time).</summary>
    public static string Timestamp(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
