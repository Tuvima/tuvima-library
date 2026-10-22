using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Storage.Backup;

/// <summary>What one re-attach pass did, in counts. Never names a title.</summary>
/// <param name="Attached">Rows written back into place.</param>
/// <param name="StillWaiting">Rows still waiting after the pass.</param>
/// <param name="Purged">Rows cleared because their waiting period ended.</param>
/// <param name="Checked">Rows the pass looked at (a full batch means there may be more to look at).</param>
/// <param name="Yielded">Rows removed unwritten because the person made the same row in the Library since the restore; theirs stays. Not counted as attached.</param>
/// <param name="FailedRows">Rows that could not be written this time and keep waiting (a data store error, not "title not found yet").</param>
/// <param name="FailedTables">The tables of the failed rows, each once. Never a title.</param>
public sealed record ReattachResult(
    int Attached,
    int StillWaiting,
    int Purged,
    int Checked = 0,
    int Yielded = 0,
    int FailedRows = 0,
    IReadOnlyList<string>? FailedTables = null)
{
    /// <summary>The tables of the failed rows, each once; empty when none failed.</summary>
    public IReadOnlyList<string> FailedTables { get; init; } = FailedTables ?? [];
}

/// <summary>One title a profile's restored state is still waiting for, as global search lists it ("File missing").</summary>
/// <param name="Name">The title's name.</param>
/// <param name="ParentName">The show, album or series it belongs to, when there is one.</param>
/// <param name="Year">The release year, when known.</param>
/// <param name="MediaType">The media type of the title as the Library stored it, when known.</param>
public sealed record WaitingTitle(string Name, string? ParentName, int? Year, string? MediaType);

/// <summary>The latest restore, in counts.</summary>
public sealed record RestoreSummary(
    Guid Id,
    DateTimeOffset RestoredAt,
    string BackupFileName,
    DateTimeOffset? BackupCreatedAt,
    int AppliedRows,
    int WaitingRows,
    int AttachedRows,
    int PurgedRows,
    int YieldedRows = 0);

/// <summary>
/// The waiting area of a restored backup (<c>waiting_personal_state</c>): state that is waiting for its title. A pass looks every
/// waiting row's titles up in the Library, writes back the rows whose titles are all found exactly once and removes them, and clears
/// the rows that have waited longer than the waiting period (backup and restore spec D3 to D6, D13). Nothing is guessed: a title that matches more than
/// one candidate, or none, keeps its state waiting. A row the person has since made again in the Library is never overwritten.
/// </summary>
public sealed class WaitingPersonalStateRepository
{
    private const string WaitingTable = "waiting_personal_state";

    private readonly IDatabaseConnection _db;
    private readonly ILogger _logger;

    /// <param name="db">The data store.</param>
    /// <param name="logger">Receives a debug line per row. It never receives a title.</param>
    public WaitingPersonalStateRepository(IDatabaseConnection db, ILogger<WaitingPersonalStateRepository>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? NullLogger<WaitingPersonalStateRepository>.Instance;
    }

    /// <summary>How many rows are waiting. Rows on a title a backup may not name (the Vault) are counted only when asked.</summary>
    public int CountWaiting(bool includeWithheld)
    {
        using var connection = _db.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {WaitingTable} WHERE $includeWithheld = 1 OR is_withheld = 0";
        command.Parameters.AddWithValue("$includeWithheld", includeWithheld ? 1L : 0L);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>How many rows are waiting on a title a backup may not name (a Vault video): the Vault's count of restored items still to come back. A count only.</summary>
    public int CountWithheldWaiting()
    {
        using var connection = _db.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {WaitingTable} WHERE is_withheld = 1";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The titles one profile's restored personal state is still waiting for whose name or parent name contains
    /// <paramref name="query"/> (case-insensitive), at most <paramref name="take"/>, each once. Only rows of that profile, only
    /// personal state, never a row on a title a backup may not name (a Vault video), and only titles that are a Work, Edition or
    /// Media Asset (never a View item). Once a title's rows are written back they are gone from the waiting area, so it stops appearing.
    /// </summary>
    public async Task<IReadOnlyList<WaitingTitle>> SearchWaitingTitlesAsync(Guid profileId, string query, int take, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (take < 1 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        using var connection = _db.CreateConnection();
        var rows = await connection.QueryAsync<WaitingTitleRow>(new CommandDefinition(
            $"""
            SELECT DISTINCT
                   json_extract(title.value, '$.name') AS Name,
                   json_extract(title.value, '$.parent_name') AS ParentName,
                   json_extract(title.value, '$.year') AS Year,
                   json_extract(title.value, '$.media_type') AS MediaType
            FROM {WaitingTable} waiting, json_each(waiting.titles_json, '$.titles') title
            WHERE waiting.profile_id = @Profile
              AND waiting.group_kind = 'personal'
              AND waiting.is_withheld = 0
              AND json_extract(title.value, '$.target') IN ('Work', 'Edition', 'MediaAsset')
              AND COALESCE(json_extract(title.value, '$.withheld'), 0) = 0
              AND json_extract(title.value, '$.name') IS NOT NULL
              AND (json_extract(title.value, '$.name') LIKE @Like ESCAPE '\'
                   OR COALESCE(json_extract(title.value, '$.parent_name'), '') LIKE @Like ESCAPE '\')
            ORDER BY json_extract(title.value, '$.name') COLLATE NOCASE, json_extract(title.value, '$.parent_name') COLLATE NOCASE
            LIMIT @Take
            """,
            new
            {
                Profile = GuidSql.ToBlob(profileId),
                Like = "%" + query.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%",
                Take = take,
            },
            cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(row => new WaitingTitle(row.Name, row.ParentName, row.Year, row.MediaType)).ToList();
    }

    private sealed class WaitingTitleRow
    {
        public string Name { get; init; } = string.Empty;
        public string? ParentName { get; init; }
        public int? Year { get; init; }
        public string? MediaType { get; init; }
    }

    /// <summary>The most recent restore with what it put back, what is waiting and what has attached or been cleared since; null if nothing was ever restored.</summary>
    public RestoreSummary? GetLatestRestore()
    {
        using var connection = _db.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, restored_at, backup_file_name, backup_created_at, applied_rows, waiting_rows, attached_rows, purged_rows, yielded_rows
            FROM backup_restores ORDER BY restored_at DESC, id LIMIT 1
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new RestoreSummary(
            GuidSql.FromDb(reader.GetValue(0)),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8));
    }

    /// <summary>
    /// Looks at up to <paramref name="batchSize"/> waiting rows (those never looked at first, then the longest since looked at,
    /// oldest first), writes back every row whose titles are all in the Library, then clears rows that started waiting more than
    /// <paramref name="retentionDays"/> ago. The waiting period is passed in by the caller each time (it reads the live setting), so
    /// changing the setting affects rows already waiting. A row already looked at on or after <paramref name="now"/> is skipped, so
    /// running passes with the same time until <see cref="ReattachResult.Checked"/> falls below the batch size looks at each row once.
    /// </summary>
    public ReattachResult RunPass(DateTimeOffset now, int batchSize, int retentionDays, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);
        ct.ThrowIfCancellationRequested();
        var stamp = RestoredRowSql.Timestamp(now);

        List<WaitingRow> batch;
        int attached = 0, yielded = 0, failed = 0;
        var failedTables = new SortedSet<string>(StringComparer.Ordinal);
        var stayed = new List<Guid>();
        using (var read = _db.CreateConnection())
        {
            batch = ReadBatch(read, stamp, batchSize);
            var resolver = new LiveTitleResolver(read, null);
            var structure = RestoredRowSql.ReadStructure(read, null);
            foreach (var row in batch)
            {
                ct.ThrowIfCancellationRequested();
                switch (TryAttach(row, resolver, structure, now, ct))
                {
                    case AttachOutcome.Attached:
                        attached++;
                        break;
                    case AttachOutcome.Yielded:
                        yielded++;
                        break;
                    case AttachOutcome.Failed:
                        failed++;
                        failedTables.Add(row.Table);
                        stayed.Add(row.Id);
                        break;
                    default:
                        stayed.Add(row.Id);
                        break;
                }
            }
        }

        MarkChecked(stayed, stamp, ct);
        var purged = Purge(RestoredRowSql.Timestamp(now.AddDays(-retentionDays)), ct);
        var result = new ReattachResult(attached, CountWaiting(includeWithheld: true), purged, batch.Count, yielded, failed, failedTables.ToList());
        if (attached > 0 || yielded > 0 || purged > 0)
        {
            _logger.LogDebug(
                "Restored state pass: {Attached} attached, {Yielded} yielded, {Waiting} still waiting, {Purged} cleared.",
                attached,
                yielded,
                result.StillWaiting,
                purged);
        }

        return result;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reading the waiting area
    // ---------------------------------------------------------------------------------------------------------

    private sealed record WaitingRow(Guid Id, byte[] RestoreId, string Group, string Table, string RowJson, string TitlesJson);

    private static List<WaitingRow> ReadBatch(SqliteConnection connection, string stamp, int batchSize)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT id, restore_id, group_kind, table_name, row_json, titles_json
            FROM {WaitingTable}
            WHERE last_checked_at IS NULL OR last_checked_at < $now
            ORDER BY last_checked_at IS NOT NULL, last_checked_at, created_at, id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$now", stamp);
        command.Parameters.AddWithValue("$limit", (long)batchSize);
        using var reader = command.ExecuteReader();
        var rows = new List<WaitingRow>();
        while (reader.Read())
        {
            rows.Add(new WaitingRow(
                GuidSql.FromDb(reader.GetValue(0)),
                (byte[])reader.GetValue(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return rows;
    }

    // ---------------------------------------------------------------------------------------------------------
    // One row
    // ---------------------------------------------------------------------------------------------------------

    private enum AttachOutcome
    {
        /// <summary>Not ready (a title not found, or found more than once): it keeps waiting.</summary>
        Waiting,

        /// <summary>It could not be written (a data store error): it keeps waiting and is reported.</summary>
        Failed,

        /// <summary>Written back and removed.</summary>
        Attached,

        /// <summary>The person made this row since the restore: theirs stays and the waiting row is removed.</summary>
        Yielded,
    }

    /// <summary>What a write did to the live data store.</summary>
    private enum WriteOutcome
    {
        Written,
        Yielded,
    }

    /// <summary>Tries to put one row back; the outcome says whether it attached, yielded, keeps waiting, or could not be written.</summary>
    private AttachOutcome TryAttach(
        WaitingRow row,
        LiveTitleResolver resolver,
        IReadOnlyDictionary<string, List<RestoredColumn>> structure,
        DateTimeOffset now,
        CancellationToken ct)
    {
        try
        {
            if (!CanBeWritten(row, structure, out var rule, out var liveColumns))
            {
                return AttachOutcome.Waiting;
            }

            var data = WaitingRowJson.ReadRow(row.RowJson);
            var titles = WaitingRowJson.ReadTitles(row.TitlesJson);
            var matches = new LiveTitleMatch[titles.Count];
            for (var i = 0; i < titles.Count; i++)
            {
                matches[i] = resolver.Resolve(titles[i]);
                if (matches[i].Kind == LiveMatchKind.None)
                {
                    return AttachOutcome.Waiting;
                }
            }

            var values = ValuesToWrite(row.Table, data.Columns, data.Values, titles, matches, resolver);
            if (values is null)
            {
                return AttachOutcome.Waiting;
            }

            UseLiveFileHash(row.Table, data.Columns, values, resolver);

            var childValues = data.Children
                .Select(child => (Child: child, Values: child.Values.Select(RestoredRowSql.ToSql).ToList()))
                .ToList();
            foreach (var (child, _) in childValues)
            {
                if (!structure.ContainsKey(child.Table) || BackupTableClassification.Find(child.Table) is not { Kind: BackupDataKind.Configuration or BackupDataKind.PersonalState })
                {
                    return AttachOutcome.Waiting;
                }
            }

            var outcome = _db.ExecuteWriteAsync(
                (connection, transaction, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    WriteOutcome? done = row.Group switch
                    {
                        "configuration" or "personal" => WriteInPlace(connection, transaction, row.Table, liveColumns, data.Columns, values, childValues, structure),
                        "correction" => WriteCorrection(connection, transaction, row.Table, rule, liveColumns, data.Columns, values, now) ? WriteOutcome.Written : null,
                        _ => WriteChoice(connection, transaction, rule, liveColumns, data.Columns, values) ? WriteOutcome.Written : null,
                    };
                    if (done is { } finished)
                    {
                        RemoveRow(connection, transaction, row, finished == WriteOutcome.Yielded ? "yielded_rows" : "attached_rows");
                    }

                    return done;
                },
                ct).GetAwaiter().GetResult();
            switch (outcome)
            {
                case WriteOutcome.Written:
                    _logger.LogDebug("Restored state re-attached for {Table}.", row.Table);
                    return AttachOutcome.Attached;
                case WriteOutcome.Yielded:
                    _logger.LogDebug("Restored state for {Table} yielded to a row made since the restore.", row.Table);
                    return AttachOutcome.Yielded;
                default:
                    return AttachOutcome.Waiting;
            }
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or InvalidOperationException or JsonException)
        {
            // The kind of error and the table only: a title is exactly what must not be written down. The row stays and is tried again.
            _logger.LogDebug("Restored state for {Table} could not be put back ({Type}); it keeps waiting.", row.Table, ex.GetType().Name);
            return AttachOutcome.Failed;
        }
    }

    /// <summary>The table must be one a backup holds in that group and still exist in this data store.</summary>
    private static bool CanBeWritten(
        WaitingRow row,
        IReadOnlyDictionary<string, List<RestoredColumn>> structure,
        out BackupTableRule rule,
        out List<RestoredColumn> liveColumns)
    {
        liveColumns = [];
        rule = null!;
        if (BackupTableClassification.Find(row.Table) is not { } found || !structure.TryGetValue(row.Table, out var columns))
        {
            return false;
        }

        var allowed = row.Group switch
        {
            "configuration" => found.Kind == BackupDataKind.Configuration,
            "personal" => found.Kind == BackupDataKind.PersonalState,
            "correction" => found.Kind == BackupDataKind.Catalogue && found.CorrectionWhere is not null,
            "choice" => found.ChoiceColumns.Count > 0,
            _ => false,
        };
        if (!allowed)
        {
            return false;
        }

        rule = found;
        liveColumns = columns;
        return true;
    }

    /// <summary>
    /// The values to write: each title reference replaced by the live row's id, and (on a different cut) the positions moved to the
    /// same chapter. Null when the row has to keep waiting, for example a position that cannot be moved.
    /// </summary>
    private List<object?>? ValuesToWrite(
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<BackupValue> values,
        IReadOnlyList<BackupTitle> titles,
        LiveTitleMatch[] matches,
        LiveTitleResolver resolver)
    {
        var result = new List<object?>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            result.Add(values[i] is BackupValue.TitleRef reference
                ? LiveId(table, columns[i], reference, titles, matches)
                : RestoredRowSql.ToSql(values[i]));
        }

        // A different cut: only a position that can be moved by chapter name, or a row that holds no position, may be written.
        var otherCut = values
            .OfType<BackupValue.TitleRef>()
            .Select(reference => int.Parse(reference.Key, CultureInfo.InvariantCulture))
            .Distinct()
            .Where(index => matches[index].Kind == LiveMatchKind.DifferentCut)
            .ToList();
        if (otherCut.Count == 0 || !WaitingStateTimePositions.Tables.TryGetValue(table, out var time))
        {
            return result;
        }

        if (time.ExactCutOnly)
        {
            return null;
        }

        // Seconds kept inside a JSON column (the saved timing of a file) mean nothing on another cut: they are left out, and the rest
        // of the row, its percent included, attaches.
        if (time.JsonColumn is not null && time.JsonSecondsKeys is { Count: > 0 })
        {
            DropSecondsFromJson(columns, result, time.JsonColumn, time.JsonSecondsKeys);
        }

        if (time.Positions.Count == 0)
        {
            return result;
        }

        if (otherCut.Count > 1 || time.ChapterColumn is null)
        {
            return null;
        }

        var chapterIndex = IndexOf(columns, time.ChapterColumn);
        var positionIndexes = time.Positions.Select(position => IndexOf(columns, position)).ToList();
        if (chapterIndex < 0 || positionIndexes.Any(index => index < 0) || result[chapterIndex] is not string { Length: > 0 } chapter || string.IsNullOrWhiteSpace(chapter))
        {
            return null;
        }

        var point = Seconds(result[positionIndexes[0]]);
        if (point is null)
        {
            return null;
        }

        var assetId = matches[otherCut[0]].Id!;
        var bookmark = new BookmarkPackBookmark(
            point.Value,
            positionIndexes.Count > 1 ? Seconds(result[positionIndexes[1]]) : null,
            chapter,
            null,
            null,
            null);
        var aligned = BookmarkPackMatcher.AlignByChapter([bookmark], resolver.ReadChapters(assetId), resolver.ReadRunningTime(assetId));
        if (aligned.Aligned.Count != 1)
        {
            return null;
        }

        result[positionIndexes[0]] = aligned.Aligned[0].Time;
        if (positionIndexes.Count > 1)
        {
            result[positionIndexes[1]] = aligned.Aligned[0].EndTime is { } end ? end : null;
        }

        return result;
    }

    /// <summary>
    /// The kept tables whose <c>content_hash</c> is the fingerprint of the file the row's asset column points at (the other kept table
    /// with a content_hash, <c>bookmark_packs</c>, fingerprints the pack itself and is not listed). Nothing in the Engine resets
    /// progress when it differs, but the hash is sent to clients and must describe the file the row now sits on.
    /// </summary>
    private static readonly Dictionary<string, (string AssetColumn, string HashColumn)> AssetHashColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user_states"] = ("asset_id", "content_hash"),
    };

    /// <summary>A row written onto a live file carries that file's fingerprint, not the one in the backup (the file may be a different one).</summary>
    private static void UseLiveFileHash(string table, IReadOnlyList<string> columns, List<object?> values, LiveTitleResolver resolver)
    {
        if (!AssetHashColumns.TryGetValue(table, out var hash))
        {
            return;
        }

        var assetIndex = IndexOf(columns, hash.AssetColumn);
        var hashIndex = IndexOf(columns, hash.HashColumn);
        if (assetIndex >= 0 && hashIndex >= 0 && values[assetIndex] is byte[] asset && resolver.ReadContentHash(asset) is { } live)
        {
            values[hashIndex] = live;
        }
    }

    /// <summary>Removes the given keys (any case) from the JSON object in <paramref name="column"/>; a column that is null or not an object is left as it is.</summary>
    private static void DropSecondsFromJson(IReadOnlyList<string> columns, List<object?> row, string column, IReadOnlyList<string> keys)
    {
        var index = IndexOf(columns, column);
        if (index < 0 || row[index] is not string { Length: > 0 } json)
        {
            return;
        }

        if (JsonNode.Parse(json) is not JsonObject properties)
        {
            return;
        }

        foreach (var key in properties.Select(pair => pair.Key).Where(name => keys.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            properties.Remove(key);
        }

        row[index] = properties.ToJsonString();
    }

    private static double? Seconds(object? value) => value switch
    {
        double real when double.IsFinite(real) => real,
        long integer => integer,
        _ => null,
    };

    /// <summary>
    /// How a table writes a TEXT id, where the backup noted the original was text rather than a 16-byte id. The live table's own form is
    /// used; the default is the dashed form.
    /// </summary>
    private static readonly Dictionary<string, string> TextIdFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        // The key of a series or sequence is the id as 32 lower-case hex digits (lower(hex(id)) in the hierarchy clean-up).
        ["profile_sequence_preferences.container_key"] = "N",
    };

    private static object LiveId(string table, string column, BackupValue.TitleRef reference, IReadOnlyList<BackupTitle> titles, LiveTitleMatch[] matches)
    {
        var index = int.Parse(reference.Key, NumberStyles.None, CultureInfo.InvariantCulture);
        if (index < 0 || index >= titles.Count)
        {
            throw new InvalidDataException("A waiting row refers to a title it does not hold.");
        }

        var id = matches[index].Id ?? throw new InvalidOperationException("A title was matched without an id.");
        if (!reference.WasText)
        {
            return id;
        }

        var format = TextIdFormats.GetValueOrDefault(table + "." + column, "D");
        return GuidSql.FromDb(id).ToString(format, CultureInfo.InvariantCulture).ToLowerInvariant();
    }

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
    // Writing
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Settings and personal state: a row the person has since made in the Library wins, so the restored row only fills a gap. When the
    /// row itself is not written, the rows that hang off it are not either (they would point at a row that was never made).
    /// </summary>
    private static WriteOutcome WriteInPlace(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IReadOnlyList<RestoredColumn> liveColumns,
        IReadOnlyList<string> columns,
        IReadOnlyList<object?> values,
        IReadOnlyList<(WaitingRowChild Child, List<object?> Values)> children,
        IReadOnlyDictionary<string, List<RestoredColumn>> structure)
    {
        if (RestoredRowSql.Insert(connection, transaction, "INSERT OR IGNORE", table, liveColumns, columns, values) == 0)
        {
            return WriteOutcome.Yielded;
        }

        foreach (var (child, childValues) in children)
        {
            RestoredRowSql.Insert(connection, transaction, "INSERT OR IGNORE", child.Table, structure[child.Table], child.Columns, childValues);
        }

        return WriteOutcome.Written;
    }

    /// <summary>
    /// A row a person made themselves on a catalogue table (a locked title field, a renamed chapter, a cover they picked). It is
    /// written whole, with a new id if its own is taken. It replaces what a provider put in the same place; a correction the person
    /// made again since the restore stays. A typed claim on a title also brings the title's winning value in line, exactly as saving
    /// it in Editing Mode does (the claim is stored, then the canonical value is set to it), so it wins over the provider.
    /// </summary>
    private static bool WriteCorrection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        BackupTableRule rule,
        IReadOnlyList<RestoredColumn> liveColumns,
        IReadOnlyList<string> columns,
        List<object?> values,
        DateTimeOffset now)
    {
        var names = columns.ToList();
        var row = values.ToList();
        var idIndex = IndexOf(names, "id");
        var hasId = liveColumns.Any(column => string.Equals(column.Name, "id", StringComparison.OrdinalIgnoreCase));
        if (hasId && (idIndex < 0 || RowExists(connection, transaction, table, "id", row[idIndex])))
        {
            // The row's own id is taken (or was left out of the backup): it gets a new one.
            if (idIndex < 0)
            {
                names.Add("id");
                row.Add(GuidSql.ToBlob(Guid.NewGuid()));
            }
            else
            {
                row[idIndex] = GuidSql.ToBlob(Guid.NewGuid());
            }
        }

        var isClaim = string.Equals(table, "metadata_claims", StringComparison.OrdinalIgnoreCase);
        if (isClaim)
        {
            // A typed value the person has made since the restore is newer than this one.
            var entity = row[IndexOf(names, "entity_id")];
            var key = row[IndexOf(names, "claim_key")];
            if (ExistsWhere(connection, transaction, table, "entity_id = $entity AND claim_key = $key AND (" + rule.CorrectionWhere + ")", ("$entity", entity), ("$key", key)))
            {
                return true;
            }
        }
        else if (!ClearReplaceableConflicts(connection, transaction, table, rule, liveColumns, names, row))
        {
            // The person made this correction again since the restore: theirs stays.
            return true;
        }

        RestoredRowSql.Insert(connection, transaction, "INSERT", table, liveColumns, names, row);
        if (isClaim)
        {
            UseClaimAsCanonicalValue(connection, transaction, names, row, now);
        }

        return true;
    }

    /// <summary>
    /// Finds the live rows that hold the same place as the restored row (same value in a unique index). A row a person made is kept,
    /// and the restored row is not written; a row a provider put there is removed to make room. Returns false to not write.
    /// </summary>
    private static bool ClearReplaceableConflicts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        BackupTableRule rule,
        IReadOnlyList<RestoredColumn> liveColumns,
        List<string> names,
        List<object?> row)
    {
        var replace = new List<long>();
        foreach (var index in ReadUniqueIndexes(connection, transaction, table))
        {
            var positions = index.Select(column => names.FindIndex(name => string.Equals(name, column, StringComparison.OrdinalIgnoreCase))).ToList();

            // A unique index only holds a row apart when none of its values is NULL, and the id was handled above.
            if (positions.Any(position => position < 0 || row[position] is null)
                || (index.Count == 1 && string.Equals(index[0], "id", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var where = string.Join(" AND ", index.Select((column, i) => $"{RestoredRowSql.Quote(column)} = $v{i.ToString(CultureInfo.InvariantCulture)}"));
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT rowid, COALESCE(({rule.CorrectionWhere}), 0) FROM {RestoredRowSql.Quote(table)} WHERE {where}";
            for (var i = 0; i < positions.Count; i++)
            {
                command.Parameters.AddWithValue("$v" + i.ToString(CultureInfo.InvariantCulture), row[positions[i]]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetInt64(1) != 0)
                {
                    return false;
                }

                replace.Add(reader.GetInt64(0));
            }
        }

        foreach (var rowId in replace.Distinct())
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {RestoredRowSql.Quote(table)} WHERE rowid = $row";
            delete.Parameters.AddWithValue("$row", rowId);
            delete.ExecuteNonQuery();
        }

        return true;
    }

    private static List<List<string>> ReadUniqueIndexes(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        var names = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM pragma_index_list($table) WHERE \"unique\" = 1 AND partial = 0";
            command.Parameters.AddWithValue("$table", table);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
        }

        var indexes = new List<List<string>>();
        foreach (var name in names)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM pragma_index_info($index) ORDER BY seqno";
            command.Parameters.AddWithValue("$index", name);
            using var reader = command.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    columns.Add(reader.GetString(0));
                }
            }

            if (columns.Count > 0)
            {
                indexes.Add(columns);
            }
        }

        return indexes;
    }

    /// <summary>
    /// The title's winning value is the person's typed one, written the way Editing Mode writes it after storing a claim: the
    /// canonical value is set to the claim's value, won by the claim's provider, not conflicted and not needing review. A list-type
    /// key is not a canonical scalar, so it is left to the claim alone, as Editing Mode does.
    /// </summary>
    private static void UseClaimAsCanonicalValue(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> names,
        IReadOnlyList<object?> row,
        DateTimeOffset now)
    {
        var current = IndexOf(names, "is_current");
        if (current >= 0 && row[current] is long and 0)
        {
            return;
        }

        if (row[IndexOf(names, "entity_id")] is not byte[] { Length: 16 } entity
            || row[IndexOf(names, "claim_key")] is not string key
            || row[IndexOf(names, "claim_value")] is not string value)
        {
            return;
        }

        var provider = IndexOf(names, "provider_id") is var providerIndex and >= 0 && row[providerIndex] is byte[] { Length: 16 } providerId
            ? GuidSql.FromDb(providerId)
            : (Guid?)null;
        CanonicalValueRepository.UpsertBatchInTransaction(
            connection,
            transaction,
            [
                new CanonicalValue
                {
                    EntityId = GuidSql.FromDb(entity),
                    Key = key,
                    Value = value,
                    LastScoredAt = now,
                    IsConflicted = false,
                    NeedsReview = false,
                    WinningProviderId = provider,
                },
            ]);
    }

    /// <summary>
    /// A person's choice on a catalogue row (a favourite flag, a display change, a switch on a collection): written only for the
    /// columns still at their default, so a choice made since the restore stays. A row the rescan has not made yet keeps waiting.
    /// </summary>
    private static bool WriteChoice(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BackupTableRule rule,
        IReadOnlyList<RestoredColumn> liveColumns,
        IReadOnlyList<string> columns,
        IReadOnlyList<object?> values)
    {
        if (columns.Count < 2 || !string.Equals(columns[0], "ref", StringComparison.Ordinal))
        {
            throw new InvalidDataException("A waiting choice does not name the row it belongs to.");
        }

        var rowColumn = rule.ChoiceRowReference?.Column ?? "id";
        var choices = columns.Skip(1).ToList();
        var live = choices.Select(choice => liveColumns.FirstOrDefault(column => string.Equals(column.Name, choice, StringComparison.OrdinalIgnoreCase))).ToList();
        if (live.Any(column => column is null))
        {
            return false;
        }

        object?[] current;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT {string.Join(", ", live.Select(column => RestoredRowSql.Quote(column!.Name)))} FROM {RestoredRowSql.Quote(rule.Table)} WHERE {RestoredRowSql.Quote(rowColumn)} = $id LIMIT 1";
            command.Parameters.AddWithValue("$id", values[0]);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            current = new object?[choices.Count];
            for (var i = 0; i < current.Length; i++)
            {
                current[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
        }

        var assignments = new List<(string Column, object? Value)>();
        for (var i = 0; i < choices.Count; i++)
        {
            var restored = values[i + 1];
            if (!PersonalStateBackupRepository.MatchesDefault(current[i], live[i]!.Default) || Equals(current[i], restored))
            {
                continue;
            }

            // A value counts only while its flag is set, in the backup.
            var flag = rule.ChoiceGatedBy.Where(entry => string.Equals(entry.Column, choices[i], StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Flag).FirstOrDefault();
            if (flag is not null)
            {
                var flagIndex = choices.FindIndex(choice => string.Equals(choice, flag, StringComparison.OrdinalIgnoreCase));
                if (flagIndex < 0 || !PersonalStateBackupRepository.IsSet(values[flagIndex + 1]))
                {
                    continue;
                }
            }

            assignments.Add((live[i]!.Name, restored));
        }

        if (assignments.Count > 0)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                $"UPDATE {RestoredRowSql.Quote(rule.Table)} SET {string.Join(", ", assignments.Select((entry, i) => RestoredRowSql.Quote(entry.Column) + " = $v" + i.ToString(CultureInfo.InvariantCulture)))} WHERE {RestoredRowSql.Quote(rowColumn)} = $id";
            for (var i = 0; i < assignments.Count; i++)
            {
                update.Parameters.AddWithValue("$v" + i.ToString(CultureInfo.InvariantCulture), assignments[i].Value ?? DBNull.Value);
            }

            update.Parameters.AddWithValue("$id", values[0]);
            update.ExecuteNonQuery();
        }

        return true;
    }

    private static bool RowExists(SqliteConnection connection, SqliteTransaction transaction, string table, string column, object? value) =>
        value is not null && ExistsWhere(connection, transaction, table, $"{RestoredRowSql.Quote(column)} = $value", ("$value", value));

    private static bool ExistsWhere(SqliteConnection connection, SqliteTransaction transaction, string table, string where, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT 1 FROM {RestoredRowSql.Quote(table)} WHERE {where} LIMIT 1";
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteScalar() is not null;
    }

    /// <param name="counter">The <c>backup_restores</c> column that counts this row: <c>attached_rows</c> or <c>yielded_rows</c>.</param>
    private static void RemoveRow(SqliteConnection connection, SqliteTransaction transaction, WaitingRow row, string counter)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {WaitingTable} WHERE id = $id";
            delete.Parameters.AddWithValue("$id", GuidSql.ToBlob(row.Id));
            delete.ExecuteNonQuery();
        }

        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = $"UPDATE backup_restores SET {counter} = {counter} + 1 WHERE id = $restore";
        count.Parameters.AddWithValue("$restore", row.RestoreId);
        count.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Bookkeeping
    // ---------------------------------------------------------------------------------------------------------

    private void MarkChecked(IReadOnlyList<Guid> ids, string stamp, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return;
        }

        _db.ExecuteWriteAsync(
            (connection, transaction, token) =>
            {
                token.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"UPDATE {WaitingTable} SET last_checked_at = $now WHERE id IN (SELECT unhex(value) FROM json_each($ids))";
                command.Parameters.AddWithValue("$now", stamp);
                command.Parameters.AddWithValue("$ids", GuidSql.ToJsonHexArray(ids));
                command.ExecuteNonQuery();
            },
            ct).GetAwaiter().GetResult();
    }

    /// <summary>Clears the rows that started waiting before <paramref name="cutoff"/>, and counts them against the restore they came from.</summary>
    private int Purge(string cutoff, CancellationToken ct) =>
        _db.ExecuteWriteAsync(
            (connection, transaction, token) =>
            {
                token.ThrowIfCancellationRequested();
                var perRestore = new List<(byte[] Restore, long Count)>();
                using (var count = connection.CreateCommand())
                {
                    count.Transaction = transaction;
                    count.CommandText = $"SELECT restore_id, COUNT(*) FROM {WaitingTable} WHERE created_at < $cutoff GROUP BY restore_id";
                    count.Parameters.AddWithValue("$cutoff", cutoff);
                    using var reader = count.ExecuteReader();
                    while (reader.Read())
                    {
                        perRestore.Add(((byte[])reader.GetValue(0), reader.GetInt64(1)));
                    }
                }

                foreach (var (restore, total) in perRestore)
                {
                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE backup_restores SET purged_rows = purged_rows + $count WHERE id = $restore";
                    update.Parameters.AddWithValue("$count", total);
                    update.Parameters.AddWithValue("$restore", restore);
                    update.ExecuteNonQuery();
                }

                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {WaitingTable} WHERE created_at < $cutoff";
                delete.Parameters.AddWithValue("$cutoff", cutoff);
                return delete.ExecuteNonQuery();
            },
            ct).GetAwaiter().GetResult();
}
