using System.Globalization;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Backup;

/// <summary>
/// Reads the part of the data store a backup keeps: Configuration tables and PersonalState tables, as listed by
/// <see cref="BackupTableClassification"/>. Every column that points at a catalogue row is replaced by a reference to
/// a <see cref="BackupTitle"/> holding the recognition data for that title, so personal state can find its title again
/// after a rescan. The catalogue itself (titles, descriptions, artwork, file paths) is never read.
/// </summary>
public sealed class PersonalStateBackupRepository
{
    /// <summary>The column that says what kind of item an id is, for tables with polymorphic references.</summary>
    private const string EntityKindColumn = "entity_kind";

    private readonly IDatabaseConnection _db;

    public PersonalStateBackupRepository(IDatabaseConnection db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Reads the configuration and personal state in <paramref name="scope"/> in one read transaction, so the export
    /// is a consistent snapshot. A profile scope holds that profile's personal state only.
    /// </summary>
    public BackupExport Export(BackupScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        using var connection = _db.CreateConnection();
        using var transaction = connection.BeginTransaction();
        var export = Build(connection, transaction, scope, includeConfiguration: !scope.IsProfile, ct);
        transaction.Commit();
        return export;
    }

    /// <summary>
    /// How many titles that have personal state (for any profile) have no external id of their own or their parent's,
    /// so a restore can match them by exact file only. The Backup screen shows this as a warning.
    /// </summary>
    public int CountTitlesRecognisedByFileOnly(CancellationToken ct = default)
    {
        using var connection = _db.CreateConnection();
        using var transaction = connection.BeginTransaction();
        var export = Build(connection, transaction, BackupScope.WholeLibrary, includeConfiguration: false, ct);
        transaction.Commit();
        return export.Contents.TitlesRecognisedByFileOnly;
    }

    private static BackupExport Build(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BackupScope scope,
        bool includeConfiguration,
        CancellationToken ct)
    {
        var live = ReadLiveStructure(connection, transaction);
        var book = new TitleBook(new TitleRecognitionReader(connection, transaction));
        var tables = new List<BackupExportTable>();
        var dropped = 0;

        var rules = (includeConfiguration ? BackupTableClassification.OfKind(BackupDataKind.Configuration) : [])
            .Concat(BackupTableClassification.OfKind(BackupDataKind.PersonalState));
        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();
            if (scope.IsProfile && rule.OwnerColumn is null)
            {
                continue;
            }

            if (!live.TryGetValue(rule.Table, out var liveColumns))
            {
                // Registered ahead of a table that has not landed yet (see BackupTableClassification.RegisteredAhead).
                continue;
            }

            tables.Add(ExportTable(connection, transaction, rule, liveColumns, scope, book, ref dropped, ct));
        }

        var configuration = tables.Where(table => table.Kind == BackupDataKind.Configuration).ToList();
        var contents = new BackupExportContents(
            scope,
            configuration.Count,
            configuration.Sum(table => table.Rows.Count),
            tables.Where(table => table.Kind == BackupDataKind.PersonalState).Sum(table => table.Rows.Count),
            book.Titles.Count,
            book.Titles.Count(title => title.IsRecognisedByFileOnly),
            CountProfiles(connection, transaction, scope),
            dropped);
        return new BackupExport(tables, book.Titles, contents);
    }

    private static BackupExportTable ExportTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BackupTableRule rule,
        IReadOnlyList<string> liveColumns,
        BackupScope scope,
        TitleBook book,
        ref int dropped,
        CancellationToken ct)
    {
        var excluded = new HashSet<string>(rule.ExcludedColumns, StringComparer.OrdinalIgnoreCase);
        var columns = liveColumns.Where(column => !excluded.Contains(column)).ToList();
        var references = new Dictionary<int, CatalogueTarget>();
        foreach (var reference in rule.CatalogueReferences)
        {
            var index = columns.FindIndex(column => string.Equals(column, reference.Column, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"Backup rule for '{rule.Table}' names reference column '{reference.Column}', which is not a kept column of that table.");
            }

            references[index] = reference.Target;
        }

        var kindIndex = columns.FindIndex(column => string.Equals(column, EntityKindColumn, StringComparison.OrdinalIgnoreCase));
        var where = new List<string>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        if (!string.IsNullOrWhiteSpace(rule.KeepWhere))
        {
            where.Add("(" + rule.KeepWhere + ")");
        }

        if (scope.ProfileId is { } profileId)
        {
            if (rule.OwnerColumn is null || !liveColumns.Contains(rule.OwnerColumn, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Backup rule for '{rule.Table}' has no usable owner column.");
            }

            // Owner columns are BLOB in most tables and TEXT (the "D" format, lower case) in the reader tables.
            var owner = Quote(rule.OwnerColumn);
            where.Add($"((typeof({owner}) = 'blob' AND {owner} = $ownerBlob) OR (typeof({owner}) = 'text' AND lower({owner}) = $ownerText))");
            command.Parameters.AddWithValue("$ownerBlob", GuidSql.ToBlob(profileId));
            command.Parameters.AddWithValue("$ownerText", profileId.ToString("D", CultureInfo.InvariantCulture));
        }

        command.CommandText =
            $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(rule.Table)}" +
            (where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where));

        var rows = new List<IReadOnlyList<BackupValue>>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var raw = new object?[columns.Count];
                for (var i = 0; i < raw.Length; i++)
                {
                    raw[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                var row = ConvertRow(raw, references, kindIndex, book);
                if (row is null)
                {
                    dropped++;
                }
                else
                {
                    rows.Add(row);
                }
            }
        }

        return new BackupExportTable(rule.Table, rule.Kind, columns, rows);
    }

    /// <summary>Converts a raw row, swapping catalogue references for title references. Null when a reference cannot be resolved.</summary>
    private static List<BackupValue>? ConvertRow(
        object?[] raw,
        Dictionary<int, CatalogueTarget> references,
        int kindIndex,
        TitleBook book)
    {
        var row = new List<BackupValue>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (!references.TryGetValue(i, out var target) || raw[i] is null)
            {
                row.Add(ToValue(raw[i]));
                continue;
            }

            if (raw[i] is not byte[] { Length: 16 } id)
            {
                return null;
            }

            var kind = target == CatalogueTarget.Other && kindIndex >= 0 ? raw[kindIndex] as string : null;
            var key = book.Resolve(target, kind, id);
            if (key is null)
            {
                return null;
            }

            row.Add(BackupValue.FromTitleRef(key));
        }

        return row;
    }

    private static BackupValue ToValue(object? value) => value switch
    {
        null => BackupValue.Null,
        long integer => BackupValue.FromInteger(integer),
        double real => BackupValue.FromReal(real),
        string text => BackupValue.FromText(text),
        byte[] blob => BackupValue.FromBlob(blob),
        _ => throw new InvalidOperationException($"Unexpected SQLite value of type {value.GetType().Name}."),
    };

    private static int CountProfiles(SqliteConnection connection, SqliteTransaction transaction, BackupScope scope)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (scope.ProfileId is { } profileId)
        {
            command.CommandText = "SELECT COUNT(*) FROM profiles WHERE id = $id";
            command.Parameters.AddWithValue("$id", GuidSql.ToBlob(profileId));
        }
        else
        {
            command.CommandText = "SELECT COUNT(*) FROM profiles";
        }

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>The tables that exist in the live data store, with their columns in order.</summary>
    private static Dictionary<string, IReadOnlyList<string>> ReadLiveStructure(SqliteConnection connection, SqliteTransaction transaction)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT t.name, c.name
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
                columns = new List<string>();
                result[table] = columns;
            }

            ((List<string>)columns).Add(reader.GetString(1));
        }

        return result;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Hands out one title per distinct catalogue row, keyed <c>t1</c>, <c>t2</c> in the order first met.</summary>
    private sealed class TitleBook
    {
        private readonly TitleRecognitionReader _reader;
        private readonly Dictionary<(CatalogueTarget Target, string Id), string?> _keys = [];
        private readonly List<BackupTitle> _titles = [];

        public TitleBook(TitleRecognitionReader reader)
        {
            _reader = reader;
        }

        public IReadOnlyList<BackupTitle> Titles => _titles;

        /// <summary>The key of the title for a catalogue row, or null when the row no longer exists.</summary>
        public string? Resolve(CatalogueTarget target, string? kind, byte[] id)
        {
            if (target == CatalogueTarget.Other)
            {
                if (_reader.ResolveOther(kind, id) is not { } resolved)
                {
                    return null;
                }

                target = resolved;
            }

            var cacheKey = (target, Convert.ToHexString(id));
            if (_keys.TryGetValue(cacheKey, out var known))
            {
                return known;
            }

            var title = _reader.Read(target, id);
            string? key = null;
            if (title is not null)
            {
                key = "t" + (_titles.Count + 1).ToString(CultureInfo.InvariantCulture);
                _titles.Add(title with { Key = key });
            }

            _keys[cacheKey] = key;
            return key;
        }
    }
}
