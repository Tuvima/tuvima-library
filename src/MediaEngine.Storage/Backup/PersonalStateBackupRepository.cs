using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Backup;

/// <summary>
/// Reads the part of the data store a backup keeps: Configuration tables and PersonalState tables, as listed by
/// <see cref="BackupTableClassification"/>. Every column that points at a catalogue row is replaced by a reference to
/// a <see cref="BackupTitle"/> holding the recognition data for that title, so personal state can find its title again
/// after a rescan. The catalogue itself (titles, descriptions, artwork, file paths) is never read, except a person's own
/// choices and corrections on catalogue rows (a locked title field, a renamed chapter), which are kept the same way.
/// </summary>
public sealed class PersonalStateBackupRepository
{
    /// <summary>The column that says what kind of item an id is, for tables with polymorphic references.</summary>
    private const string EntityKindColumn = "entity_kind";

    private readonly IDatabaseConnection _db;
    private readonly IBackupPortableValueCodec? _portableValues;
    private readonly Func<CatalogueTarget, byte[], bool>? _isWithheld;

    /// <param name="db">The data store.</param>
    /// <param name="portableValues">
    /// Makes machine-protected values readable for the backup. With none, portable columns are exported unchanged.
    /// </param>
    public PersonalStateBackupRepository(IDatabaseConnection db, IBackupPortableValueCodec? portableValues = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _portableValues = portableValues;
    }

    /// <summary>A test seam: <paramref name="isWithheld"/> marks titles as withheld (see <see cref="TitleRecognitionReader"/>).</summary>
    internal PersonalStateBackupRepository(IDatabaseConnection db, IBackupPortableValueCodec? portableValues, Func<CatalogueTarget, byte[], bool>? isWithheld)
        : this(db, portableValues)
    {
        _isWithheld = isWithheld;
    }

    /// <summary>
    /// Reads the configuration and personal state in <paramref name="scope"/> in one read transaction, so the export
    /// is a consistent snapshot. A profile scope holds that profile's personal state only. The transaction is deferred
    /// (it takes no write lock; WAL gives the snapshot), so the Engine keeps writing while a backup runs.
    /// </summary>
    public BackupExport Export(BackupScope scope, CancellationToken ct = default) => Export(scope, null, ct);

    /// <summary>
    /// As <see cref="Export(BackupScope, CancellationToken)"/>, calling <paramref name="afterSnapshotStarted"/> once the
    /// read snapshot is open and before any row is exported, so a test can write from another connection at that point.
    /// </summary>
    internal BackupExport Export(BackupScope scope, Action? afterSnapshotStarted, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        using var connection = _db.CreateConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        var export = Build(connection, transaction, scope, includeConfiguration: !scope.IsProfile, _portableValues, _isWithheld, afterSnapshotStarted, ct);
        transaction.Commit();
        return export;
    }

    /// <summary>
    /// How many titles that have personal state (for any profile) have no external id of their own or their parent's,
    /// so a restore can match them by exact file only. The Backup screen shows this as a warning. It reads only the
    /// distinct catalogue references of the personal state tables, never the rows themselves.
    /// </summary>
    public int CountTitlesRecognisedByFileOnly(CancellationToken ct = default)
    {
        using var connection = _db.CreateConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        var live = ReadLiveStructure(connection, transaction);
        var book = new TitleBook(new TitleRecognitionReader(connection, transaction));
        foreach (var rule in BackupTableClassification.OfKind(BackupDataKind.PersonalState))
        {
            ct.ThrowIfCancellationRequested();
            if (!live.TryGetValue(rule.Table, out var liveColumns))
            {
                continue;
            }

            var hasKind = liveColumns.Any(column => string.Equals(column.Name, EntityKindColumn, StringComparison.OrdinalIgnoreCase));
            foreach (var reference in rule.CatalogueReferences)
            {
                var column = liveColumns.FirstOrDefault(candidate => string.Equals(candidate.Name, reference.Column, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"Backup rule for '{rule.Table}' names reference column '{reference.Column}', which is not a column of that table.");
                var withKind = reference.Target == CatalogueTarget.Other && hasKind;
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    $"SELECT DISTINCT {Quote(column.Name)}{(withKind ? ", " + Quote(EntityKindColumn) : string.Empty)} " +
                    $"FROM {Quote(rule.Table)} WHERE {Quote(column.Name)} IS NOT NULL";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (ClassifyReference(reader.GetValue(0), out var id) is not (ReferenceKind.Id or ReferenceKind.TextId))
                    {
                        continue;
                    }

                    var kind = withKind && !reader.IsDBNull(1) ? reader.GetValue(1) as string : null;
                    book.Resolve(reference.Target, kind, id);
                }
            }
        }

        transaction.Commit();
        return book.Titles.Count(title => title.IsRecognisedByFileOnly);
    }

    private static BackupExport Build(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BackupScope scope,
        bool includeConfiguration,
        IBackupPortableValueCodec? portableValues,
        Func<CatalogueTarget, byte[], bool>? isWithheld,
        Action? afterSnapshotStarted,
        CancellationToken ct)
    {
        var live = ReadLiveStructure(connection, transaction);
        afterSnapshotStarted?.Invoke();
        var book = new TitleBook(new TitleRecognitionReader(connection, transaction, isWithheld));
        var tables = new List<BackupExportTable>();
        var dropped = 0;
        var withheld = 0;
        var keptMachineBound = 0;

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

            tables.Add(ExportTable(connection, transaction, rule, liveColumns, scope, book, rule.KeepWhere, portableValues, ref dropped, ref withheld, ref keptMachineBound, ct));
        }

        // A person's own choices: on catalogue rows, and on the rows of a configuration table that a keep condition leaves
        // out (collections the Library built).
        var choices = new List<BackupExportTable>();
        var choiceRules = BackupTableClassification.OfKind(BackupDataKind.Catalogue)
            .Concat(BackupTableClassification.OfKind(BackupDataKind.Configuration).Where(rule => rule.KeepWhere is not null))
            .Where(rule => rule.ChoiceColumns.Count > 0);
        foreach (var rule in choiceRules)
        {
            ct.ThrowIfCancellationRequested();
            if (live.TryGetValue(rule.Table, out var liveColumns)
                && ExportChoices(connection, transaction, rule, liveColumns, scope, book, ref dropped, ref withheld, ct) is { } choiceTable)
            {
                choices.Add(choiceTable);
            }
        }

        // A person's own rows on catalogue tables belong to the Library, so a profile export holds none.
        var corrections = new List<BackupExportTable>();
        if (!scope.IsProfile)
        {
            foreach (var rule in BackupTableClassification.OfKind(BackupDataKind.Catalogue).Where(rule => rule.CorrectionWhere is not null))
            {
                ct.ThrowIfCancellationRequested();
                if (!live.TryGetValue(rule.Table, out var liveColumns))
                {
                    continue;
                }

                var table = ExportTable(connection, transaction, rule, liveColumns, scope, book, rule.CorrectionWhere, null, ref dropped, ref withheld, ref keptMachineBound, ct);
                if (table.Rows.Count > 0)
                {
                    corrections.Add(table);
                }
            }
        }

        var configuration = tables.Where(table => table.Kind == BackupDataKind.Configuration).ToList();
        var contents = new BackupExportContents(
            scope,
            configuration.Count,
            configuration.Sum(table => table.Rows.Count),
            tables.Where(table => table.Kind == BackupDataKind.PersonalState).Sum(table => table.Rows.Count),
            choices.Sum(table => table.Rows.Count),
            book.Titles.Count,
            book.Titles.Count(title => title.IsRecognisedByFileOnly),
            CountProfiles(connection, transaction, scope),
            dropped,
            corrections.Sum(table => table.Rows.Count),
            keptMachineBound,
            withheld);
        return new BackupExport(tables, choices, book.Titles, contents, corrections);
    }

    private static BackupExportTable ExportTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BackupTableRule rule,
        IReadOnlyList<LiveColumn> liveColumns,
        BackupScope scope,
        TitleBook book,
        string? predicate,
        IBackupPortableValueCodec? portableValues,
        ref int dropped,
        ref int withheld,
        ref int keptMachineBound,
        CancellationToken ct)
    {
        var excluded = new HashSet<string>(rule.ExcludedColumns, StringComparer.OrdinalIgnoreCase);

        // An excluded column is never read. One that may be NULL (and has no default) stays in the column list as NULL;
        // any other is left out of the list, so a restore uses the table's own default.
        var columns = liveColumns
            .Where(column => !excluded.Contains(column.Name) || (!column.NotNull && column.Default is null))
            .Select(column => column.Name)
            .ToList();
        var stripped = rule.ExcludedJsonProperties
            .Select(property => (Index: columns.FindIndex(column => string.Equals(column, property.Column, StringComparison.OrdinalIgnoreCase)), property.Property))
            .ToList();
        if (stripped.Any(entry => entry.Index < 0))
        {
            throw new InvalidOperationException($"Backup rule for '{rule.Table}' names a JSON column that is not a kept column of that table.");
        }
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

        var portableIndexes = rule.PortableColumns
            .Select(portable => columns.FindIndex(column => string.Equals(column, portable, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (portableIndexes.Any(index => index < 0))
        {
            throw new InvalidOperationException($"Backup rule for '{rule.Table}' names a portable column that is not a kept column of that table.");
        }

        // A reference that may be NULL is written as NULL when its catalogue row is gone (an activity entry outlives the
        // title it names); a row whose NOT NULL reference is gone is dropped.
        var nullableReferences = references.Keys
            .Where(index => liveColumns.First(column => string.Equals(column.Name, columns[index], StringComparison.OrdinalIgnoreCase)) is { NotNull: false })
            .ToHashSet();

        var kindIndex = columns.FindIndex(column => string.Equals(column, EntityKindColumn, StringComparison.OrdinalIgnoreCase));
        var where = new List<string>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        if (!string.IsNullOrWhiteSpace(predicate))
        {
            where.Add("(" + predicate + ")");
        }

        if (scope.ProfileId is { } profileId)
        {
            if (rule.OwnerColumn is null || !liveColumns.Any(column => string.Equals(column.Name, rule.OwnerColumn, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Backup rule for '{rule.Table}' has no usable owner column.");
            }

            // Owner columns are BLOB in most tables and TEXT (the "D" format, lower case) in the reader tables.
            var owner = Quote(rule.OwnerColumn);
            where.Add($"((typeof({owner}) = 'blob' AND {owner} = $ownerBlob) OR (typeof({owner}) = 'text' AND lower({owner}) = $ownerText))");
            var textForm = profileId.ToString("D", CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue("$ownerBlob", GuidSql.ToBlob(profileId));
            command.Parameters.AddWithValue("$ownerText", textForm);
        }

        command.CommandText =
            $"SELECT {string.Join(", ", columns.Select(column => excluded.Contains(column) ? "NULL AS " + Quote(column) : Quote(column)))} FROM {Quote(rule.Table)}" +
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

                var mark = book.Mark();
                var row = ConvertRow(rule.Table, raw, references, nullableReferences, kindIndex, stripped, book);
                if (row is null)
                {
                    book.Rollback(mark);
                    dropped++;
                    continue;
                }

                // A person's correction on a withheld title is not kept (and the title is not written for it).
                if (rule.Kind == BackupDataKind.Catalogue && book.TouchesWithheld(row))
                {
                    book.Rollback(mark);
                    withheld++;
                    continue;
                }

                if (portableValues is not null)
                {
                    foreach (var index in portableIndexes)
                    {
                        if (row[index] is not BackupValue.Text protectedValue)
                        {
                            continue;
                        }

                        // Null means this computer's key cannot read it: keep it as it is, usable on this computer only.
                        var readable = portableValues.Export(rule.Table, columns[index], protectedValue.Value);
                        if (readable is null)
                        {
                            keptMachineBound++;
                        }
                        else
                        {
                            row[index] = BackupValue.FromPortable(readable);
                        }
                    }
                }

                rows.Add(row);
            }
        }

        return new BackupExportTable(rule.Table, rule.Kind, columns, rows);
    }

    /// <summary>
    /// Converts a raw row, swapping catalogue references for title references. An unresolved reference becomes NULL when
    /// its column may be NULL; otherwise the whole row is dropped (null is returned).
    /// </summary>
    private static List<BackupValue>? ConvertRow(
        string table,
        object?[] raw,
        Dictionary<int, CatalogueTarget> references,
        HashSet<int> nullableReferences,
        int kindIndex,
        List<(int Index, string Property)> stripped,
        TitleBook book)
    {
        var row = new List<BackupValue>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (!references.TryGetValue(i, out var target) || raw[i] is null)
            {
                row.Add(ToValue(StripJsonProperties(table, raw[i], i, stripped)));
                continue;
            }

            // A reference is normally a 16-byte BLOB id. A TEXT reference that parses as a GUID is the same id (and is
            // marked as TEXT so a restore writes it back as TEXT); any other text (a Wikidata id, a provider key) is an
            // external key, kept as written. Any other value cannot be resolved: it becomes NULL where the column may be
            // NULL, and the row is dropped where it may not.
            var referenceKind = ClassifyReference(raw[i]!, out var id);
            if (referenceKind == ReferenceKind.External)
            {
                row.Add(ToValue(raw[i]));
                continue;
            }

            if (referenceKind == ReferenceKind.Unusable)
            {
                if (!nullableReferences.Contains(i))
                {
                    return null;
                }

                row.Add(ToValue(null));
                continue;
            }

            var kind = target == CatalogueTarget.Other && kindIndex >= 0 ? raw[kindIndex] as string : null;
            var key = book.Resolve(target, kind, id);
            if (key is null)
            {
                if (!nullableReferences.Contains(i))
                {
                    return null;
                }

                row.Add(ToValue(null));
                continue;
            }

            row.Add(BackupValue.FromTitleRef(key, referenceKind == ReferenceKind.TextId));
        }

        return row;
    }

    /// <summary>What a value in a catalogue reference column is.</summary>
    private enum ReferenceKind
    {
        /// <summary>A 16-byte BLOB id.</summary>
        Id,

        /// <summary>A TEXT id in GUID form.</summary>
        TextId,

        /// <summary>Any other text: an external key kept as written.</summary>
        External,

        /// <summary>Neither an id nor text.</summary>
        Unusable,
    }

    private static ReferenceKind ClassifyReference(object value, out byte[] id)
    {
        switch (value)
        {
            case byte[] { Length: 16 } blob:
                id = blob;
                return ReferenceKind.Id;
            case string text when Guid.TryParse(text, out var guid):
                id = GuidSql.ToBlob(guid);
                return ReferenceKind.TextId;
            case string:
                id = [];
                return ReferenceKind.External;
            default:
                id = [];
                return ReferenceKind.Unusable;
        }
    }

    /// <summary>Removes the named JSON properties, at any depth, from a JSON text value. Fails rather than keep unreadable JSON.</summary>
    private static object? StripJsonProperties(string table, object? value, int index, List<(int Index, string Property)> stripped)
    {
        if (value is not string json || stripped.Count == 0)
        {
            return value;
        }

        var names = stripped.Where(entry => entry.Index == index).Select(entry => entry.Property).ToList();
        if (names.Count == 0)
        {
            return value;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidOperationException($"A JSON value in '{table}' could not be read, so it cannot be checked for secrets.", ex);
        }

        RemoveProperties(node, names);
        return node?.ToJsonString() ?? value;
    }

    private static void RemoveProperties(JsonNode? node, List<string> names)
    {
        switch (node)
        {
            case JsonObject obj:
                // Names match ignoring case: settings are stored camelCase, but a rule names the property as the type does.
                foreach (var key in obj.Select(pair => pair.Key).Where(key => names.Contains(key, StringComparer.OrdinalIgnoreCase)).ToList())
                {
                    obj.Remove(key);
                }

                foreach (var child in obj.Select(pair => pair.Value).ToList())
                {
                    RemoveProperties(child, names);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    RemoveProperties(child, names);
                }

                break;
        }
    }

    /// <summary>
    /// Reads the person's own choices that sit on a catalogue row (a favourite flag on a View item, a locked match on a
    /// title) or, for a configuration table with a keep condition, on the rows that condition does not keep (a switch on a
    /// collection the Library built). Only rows where a choice differs from the column's default are written, each as a
    /// reference to the row plus the choice values. A column listed in the rule's <c>ChoiceGatedBy</c> counts, and is
    /// kept, only while its flag column is set (a capture date read from the file never enters the backup). A choice
    /// column that points at a catalogue row is written as a title reference. Returns null when there is nothing to write.
    /// </summary>
    private static BackupExportTable? ExportChoices(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BackupTableRule rule,
        IReadOnlyList<LiveColumn> liveColumns,
        BackupScope scope,
        TitleBook book,
        ref int dropped,
        ref int withheld,
        CancellationToken ct)
    {
        // The choice belongs to the row itself, or to the catalogue row a column names (a View item's metadata row).
        var target = rule.ChoiceRowReference?.Target ?? ChoiceTarget(rule.Table);
        var rowColumn = rule.ChoiceRowReference?.Column ?? "id";
        var byName = liveColumns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        if (!byName.ContainsKey(rowColumn))
        {
            throw new InvalidOperationException($"Backup rule for '{rule.Table}' has choices but the table has no {rowColumn} column.");
        }

        foreach (var column in rule.ChoiceColumns.Where(column => !byName.ContainsKey(column)))
        {
            throw new InvalidOperationException($"Backup rule for '{rule.Table}' names choice column '{column}', which is not a column of that table.");
        }

        var choiceColumns = rule.ChoiceColumns.ToList();

        // The flag that says a value was set by the person, when the column has one.
        var gateIndex = choiceColumns
            .Select(column =>
            {
                var gate = rule.ChoiceGatedBy.Where(entry => string.Equals(entry.Column, column, StringComparison.OrdinalIgnoreCase)).ToList();
                if (gate.Count == 0)
                {
                    return -1;
                }

                var flag = choiceColumns.FindIndex(other => string.Equals(other, gate[0].Flag, StringComparison.OrdinalIgnoreCase));
                return flag >= 0
                    ? flag
                    : throw new InvalidOperationException($"Backup rule for '{rule.Table}' gates '{column}' by '{gate[0].Flag}', which is not one of its choice columns.");
            })
            .ToList();

        // Choice columns that point at a catalogue row become title references, like any other reference.
        var references = new Dictionary<int, CatalogueTarget>();
        foreach (var reference in rule.CatalogueReferences)
        {
            var index = choiceColumns.FindIndex(column => string.Equals(column, reference.Column, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                references[index] = reference.Target;
            }
        }

        var nullableReferences = references.Keys.Where(index => !byName[choiceColumns[index]].NotNull).ToHashSet();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var where = new List<string>();
        if (rule.Kind == BackupDataKind.Configuration)
        {
            // The rows the keep condition does not keep. A NULL answer counts as not kept.
            where.Add("NOT COALESCE((" + rule.KeepWhere + "), 0)");
        }

        if (scope.ProfileId is { } profileId)
        {
            // A profile export holds that profile's own items only; a table with no owner has none to give.
            if (!byName.ContainsKey("owner_profile_id"))
            {
                return null;
            }

            where.Add("owner_profile_id = $owner");
            command.Parameters.AddWithValue("$owner", GuidSql.ToBlob(profileId));
        }

        command.CommandText =
            $"SELECT {string.Join(", ", new[] { rowColumn }.Concat(choiceColumns).Select(Quote))} FROM {Quote(rule.Table)}" +
            (where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where));

        var rows = new List<IReadOnlyList<BackupValue>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.GetValue(0) is not byte[] { Length: 16 } id)
            {
                continue;
            }

            var values = new object?[choiceColumns.Count];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = reader.IsDBNull(i + 1) ? null : reader.GetValue(i + 1);
            }

            var differs = false;
            for (var i = 0; i < values.Length; i++)
            {
                if (gateIndex[i] >= 0 && !IsSet(values[gateIndex[i]]))
                {
                    values[i] = null;
                    continue;
                }

                differs |= !MatchesDefault(values[i], byName[choiceColumns[i]].Default);
            }

            if (!differs)
            {
                continue;
            }

            var mark = book.Mark();
            var key = book.Resolve(target, null, id);
            if (key is null)
            {
                continue;
            }

            var converted = ConvertRow(rule.Table, values, references, nullableReferences, -1, [], book);
            if (converted is null)
            {
                book.Rollback(mark);
                dropped++;
                continue;
            }

            var row = new List<BackupValue>(values.Length + 1) { BackupValue.FromTitleRef(key) };
            row.AddRange(converted);
            if (book.TouchesWithheld(row))
            {
                book.Rollback(mark);
                withheld++;
                continue;
            }

            rows.Add(row);
        }

        return rows.Count == 0 ? null : new BackupExportTable(rule.Table, BackupDataKind.Catalogue, ["ref", .. choiceColumns], rows);
    }

    private static CatalogueTarget ChoiceTarget(string table) => table.ToLowerInvariant() switch
    {
        "local_items" => CatalogueTarget.LocalItem,
        "works" => CatalogueTarget.Work,
        "persons" => CatalogueTarget.Person,
        "collections" => CatalogueTarget.Collection,
        _ => throw new InvalidOperationException($"Backup rule for '{table}' has choices but no catalogue target is known for that table."),
    };

    private static bool IsSet(object? value) => value switch
    {
        long integer => integer != 0,
        double real => real != 0,
        string text => !string.IsNullOrEmpty(text) && text != "0",
        _ => false,
    };

    /// <summary>Whether a stored value equals the column default (<c>dflt_value</c> as SQLite reports it; null means no default, so NULL).</summary>
    private static bool MatchesDefault(object? value, string? defaultSql)
    {
        if (defaultSql is null || string.Equals(defaultSql, "NULL", StringComparison.OrdinalIgnoreCase))
        {
            return value is null;
        }

        if (value is null)
        {
            return false;
        }

        var literal = defaultSql.Length >= 2 && defaultSql[0] == '\'' && defaultSql[^1] == '\''
            ? defaultSql[1..^1].Replace("''", "'", StringComparison.Ordinal)
            : defaultSql;
        return value switch
        {
            long integer => long.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed == integer,
            double real => double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed == real,
            string text => string.Equals(text, literal, StringComparison.Ordinal),
            _ => false,
        };
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
    private static Dictionary<string, IReadOnlyList<LiveColumn>> ReadLiveStructure(SqliteConnection connection, SqliteTransaction transaction)
    {
        var result = new Dictionary<string, IReadOnlyList<LiveColumn>>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT t.name, c.name, c."notnull", c.dflt_value
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
                columns = new List<LiveColumn>();
                result[table] = columns;
            }

            ((List<LiveColumn>)columns).Add(new LiveColumn(
                reader.GetString(1),
                reader.GetInt64(2) != 0,
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return result;
    }

    /// <summary>A column of the live data store: its name, whether it forbids NULL and its default as SQL text.</summary>
    private sealed record LiveColumn(string Name, bool NotNull, string? Default);

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Hands out one title per distinct catalogue row, keyed <c>t1</c>, <c>t2</c> in the order first met.</summary>
    private sealed class TitleBook
    {
        private readonly TitleRecognitionReader _reader;
        private readonly Dictionary<(CatalogueTarget Target, string Id), string?> _keys = [];
        private readonly List<BackupTitle> _titles = [];
        private readonly List<(CatalogueTarget Target, string Id)> _titleCacheKeys = [];
        private readonly HashSet<string> _withheldKeys = new(StringComparer.Ordinal);

        public TitleBook(TitleRecognitionReader reader)
        {
            _reader = reader;
        }

        public IReadOnlyList<BackupTitle> Titles => _titles;

        /// <summary>The number of titles written so far, to roll back to if a row turns out not to be kept.</summary>
        public int Mark() => _titles.Count;

        /// <summary>Forgets the titles added since <paramref name="mark"/>, so a row that is left out does not leave its titles behind.</summary>
        public void Rollback(int mark)
        {
            for (var i = _titles.Count - 1; i >= mark; i--)
            {
                _keys.Remove(_titleCacheKeys[i]);
                _withheldKeys.Remove(_titles[i].Key);
                _titles.RemoveAt(i);
                _titleCacheKeys.RemoveAt(i);
            }
        }

        /// <summary>Whether a row refers to a withheld title.</summary>
        public bool TouchesWithheld(IEnumerable<BackupValue> row) =>
            _withheldKeys.Count > 0 && row.OfType<BackupValue.TitleRef>().Any(reference => _withheldKeys.Contains(reference.Key));

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
                _titleCacheKeys.Add(cacheKey);
                if (_reader.IsWithheld(target, id))
                {
                    _withheldKeys.Add(key);
                }
            }

            _keys[cacheKey] = key;
            return key;
        }
    }
}
