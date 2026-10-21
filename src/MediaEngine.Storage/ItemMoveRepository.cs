using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>How a title joins its destination (details overlay spec D12 to D16).</summary>
public enum ItemMoveMode
{
    /// <summary>The title takes the number and identity of an empty entry in the destination's list.</summary>
    MatchEntry,

    /// <summary>The chosen entry already has a file: the file becomes another version (Edition) of that entry.</summary>
    AnotherVersion,

    /// <summary>Nothing in the list fits: the title joins with no number and a "Not matched" tag, borrowing nothing.</summary>
    AddAnyway,
}

/// <summary>The title being moved, as the move rules need to see it.</summary>
public sealed record ItemMoveSource(
    Guid WorkId,
    string MediaType,
    string Title,
    Guid? ParentId,
    string? ParentTitle,
    int? Ordinal,
    IReadOnlyList<Guid> EditionIds,
    IReadOnlyList<Guid> AssetIds,
    int OtherOwnedSiblings);

/// <summary>A place the title can move to: an album, a season, an audiobook.</summary>
public sealed record ItemMoveDestination(Guid Id, string Title, string? Subtitle, int EntryCount);

/// <summary>One line of a destination's own list. <see cref="Taken"/> is true when the entry already has a file.</summary>
public sealed record ItemMoveEntry(Guid Id, string Title, int? Ordinal, bool Taken, int EditionCount);

/// <summary>What the person confirmed. Everything is re-checked inside the write.</summary>
public sealed record ItemMoveCommand(
    Guid WorkId,
    Guid DestinationId,
    Guid? EntryId,
    ItemMoveMode Mode,
    int ExpectedSourceEditions,
    bool ExpectedEntryTaken);

public enum ItemMoveOutcome
{
    Moved,
    SourceGone,
    DestinationGone,
    Changed,
    WrongKind,
}

/// <summary>
/// Moves one title to another place in the same kind of list (a song to another album, an episode to another season, an audiobook part
/// to another audiobook). The Engine's rules live here so every caller moves the same way: a song is never dropped into an album that
/// lacks it without matching an entry, or being added anyway and tagged "Not matched" (spec D12, D13, D15, D16). Moving a file asks the
/// organiser to rename it to fit, through the same write intent the editor uses.
/// </summary>
public sealed class ItemMoveRepository(IDatabaseConnection database)
{
    /// <summary>The key a title added anyway carries so it can show "Not matched" and be offered a later Match.</summary>
    public const string MatchStateKey = "match_state";

    public const string UnmatchedValue = "unmatched";

    private const string TitleSql = """
        COALESCE((SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'title'),
                 (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'album'),
                 (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'show_name'),
                 '')
        """;

    // Only a child that was a title with files can move: groups (a whole album or show) are not moved (spec: not moved).
    public Task<ItemMoveSource?> GetSourceAsync(Guid workId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = database.CreateConnection();
        return Task.FromResult(ReadSource(conn, null, workId));
    }

    public Task<ItemMoveDestination?> GetDestinationAsync(Guid destinationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = database.CreateConnection();
        var row = conn.QuerySingleOrDefault<DestinationRow>($"""
            SELECT w.id AS Id, {TitleSql} AS Title, w.ordinal AS Ordinal, w.parent_work_id AS ParentId,
                   (SELECT COUNT(*) FROM works c WHERE c.parent_work_id = w.id AND c.work_kind IN ('child','catalog')) AS EntryCount
            FROM works w WHERE w.id = @destinationId AND w.work_kind = 'parent';
            """, new { destinationId });
        return Task.FromResult(row is null ? null : ToDestination(conn, null, row));
    }

    public Task<IReadOnlyList<ItemMoveDestination>> FindDestinationsAsync(
        ItemMoveSource source, string? query, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = database.CreateConnection();
        // Same kind only (spec D15): the destination is a parent of the same media type, and a TV episode only goes into a season
        // (a parent that itself has a parent), never straight onto a show.
        var rows = conn.Query<DestinationRow>($"""
            SELECT w.id AS Id, {TitleSql} AS Title, w.ordinal AS Ordinal, w.parent_work_id AS ParentId,
                   (SELECT COUNT(*) FROM works c WHERE c.parent_work_id = w.id AND c.work_kind IN ('child','catalog')) AS EntryCount
            FROM works w
            WHERE w.work_kind = 'parent' AND w.media_type = @mediaType
              AND (@parentId IS NULL OR w.id <> @parentId)
              AND (@mediaType <> 'TV' OR w.parent_work_id IS NOT NULL)
              AND (@mediaType = 'TV' OR w.parent_work_id IS NULL OR @mediaType = 'Audiobooks')
            ORDER BY Title COLLATE NOCASE
            LIMIT 400;
            """, new { mediaType = source.MediaType, parentId = source.ParentId });
        var wanted = (query ?? string.Empty).Trim();
        var result = rows
            .Select(row => ToDestination(conn, null, row))
            .Where(d => wanted.Length == 0
                || d.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || (d.Subtitle?.Contains(wanted, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(Math.Clamp(limit, 1, 50))
            .ToList();
        return Task.FromResult<IReadOnlyList<ItemMoveDestination>>(result);
    }

    public Task<IReadOnlyList<ItemMoveEntry>> GetEntriesAsync(Guid destinationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = database.CreateConnection();
        return Task.FromResult(ReadEntries(conn, null, destinationId));
    }

    public Task<ItemMoveOutcome> CommitAsync(ItemMoveCommand command, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var source = ReadSource(conn, tx, command.WorkId);
            if (source is null)
            {
                return ItemMoveOutcome.SourceGone;
            }

            var destination = conn.QuerySingleOrDefault<DestinationRow>("""
                SELECT w.id AS Id, '' AS Title, w.ordinal AS Ordinal, w.parent_work_id AS ParentId, 0 AS EntryCount, w.media_type AS MediaType
                FROM works w WHERE w.id = @id AND w.work_kind = 'parent';
                """, new { id = command.DestinationId }, tx);
            if (destination is null)
            {
                return ItemMoveOutcome.DestinationGone;
            }

            if (!string.Equals(destination.MediaType, source.MediaType, StringComparison.OrdinalIgnoreCase)
                || command.DestinationId == source.ParentId
                || (source.MediaType == "TV" && destination.ParentId is null)
                || source.EditionIds.Count != command.ExpectedSourceEditions)
            {
                return source.EditionIds.Count != command.ExpectedSourceEditions ? ItemMoveOutcome.Changed : ItemMoveOutcome.WrongKind;
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            if (command.Mode == ItemMoveMode.AddAnyway)
            {
                // The title stays the same Work: only its place changes. No number, no borrowed provider details, and a tag so the
                // page can say "Not matched" and offer Match later.
                conn.Execute("""
                    UPDATE works SET parent_work_id = @destination, ordinal = NULL, ordinal_sort = NULL WHERE id = @workId;
                    INSERT INTO canonical_values(entity_id, key, value, last_scored_at, winning_provider_id)
                    VALUES(@workId, @key, @value, @now, NULL)
                    ON CONFLICT(entity_id, key) DO UPDATE SET value = excluded.value, last_scored_at = excluded.last_scored_at;
                    """, new { destination = command.DestinationId, workId = command.WorkId, key = MatchStateKey, value = UnmatchedValue, now }, tx);
            }
            else
            {
                if (command.EntryId is not { } entryId)
                {
                    return ItemMoveOutcome.Changed;
                }

                var entry = ReadEntries(conn, tx, command.DestinationId).FirstOrDefault(e => e.Id == entryId);
                if (entry is null || entry.Taken != command.ExpectedEntryTaken
                    || (command.Mode == ItemMoveMode.MatchEntry && entry.Taken)
                    || (command.Mode == ItemMoveMode.AnotherVersion && !entry.Taken))
                {
                    return ItemMoveOutcome.Changed;
                }

                foreach (var editionId in source.EditionIds)
                {
                    if (conn.Execute("UPDATE editions SET work_id = @target WHERE id = @editionId AND work_id = @source;",
                            new { target = entryId, editionId, source = source.WorkId }, tx) != 1)
                    {
                        return ItemMoveOutcome.Changed;
                    }
                }

                // A matched title is no longer "Not matched"; the old place keeps an empty entry if it had one.
                conn.Execute("DELETE FROM canonical_values WHERE entity_id = @workId AND key = @key;",
                    new { workId = source.WorkId, key = MatchStateKey }, tx);
                WorkOwnershipSync.Recompute(conn, tx, [source.WorkId, entryId]);
            }

            // The organiser renames and files the moved file to fit its new place (managed folders only; as-is and protected files are
            // never touched because the organiser's own gate decides).
            foreach (var assetId in source.AssetIds)
            {
                conn.Execute("""
                    INSERT INTO media_file_write_intents(asset_id, generation, operation_token, trigger, status, attempts, created_at, updated_at)
                    VALUES(@assetId, 1, @token, 'editor_commit', 'pending', 0, @now, @now)
                    ON CONFLICT(asset_id) DO UPDATE SET generation = generation + 1, operation_token = excluded.operation_token,
                        trigger = 'editor_commit', status = 'pending', attempts = 0, lease_expires_at = NULL, last_error = NULL,
                        updated_at = excluded.updated_at;
                    """, new { assetId, token = "move:" + Guid.NewGuid().ToString("N"), now }, tx);
            }

            conn.Execute("""
                INSERT INTO system_activity(action_type, entity_id, entity_type, detail, changes_json)
                VALUES('MetadataUpdated', @workId, 'Work', @detail, @changes);
                """, new
            {
                workId = source.WorkId,
                detail = command.Mode switch
                {
                    ItemMoveMode.AddAnyway => "Moved without a number (not matched)",
                    ItemMoveMode.AnotherVersion => "Moved as another version of an entry",
                    _ => "Moved to a matched entry",
                },
                changes = System.Text.Json.JsonSerializer.Serialize(new
                {
                    from_parent = source.ParentId,
                    destination = command.DestinationId,
                    entry = command.EntryId,
                    mode = command.Mode.ToString(),
                }),
            }, tx);
            return ItemMoveOutcome.Moved;
        }, ct);

    private static ItemMoveSource? ReadSource(Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction? tx, Guid workId)
    {
        var row = conn.QuerySingleOrDefault<SourceRow>($"""
            SELECT w.id AS WorkId, w.media_type AS MediaType, {TitleSql} AS Title, w.parent_work_id AS ParentId, w.ordinal AS Ordinal,
                   (SELECT {TitleSql.Replace("w.id", "p.id")} FROM works p WHERE p.id = w.parent_work_id) AS ParentTitle
            FROM works w
            WHERE w.id = @workId AND w.work_kind = 'child' AND w.parent_work_id IS NOT NULL
              AND w.media_type IN ('Music', 'TV', 'Audiobooks');
            """, new { workId }, tx);
        if (row is null)
        {
            return null;
        }

        var editions = conn.Query<Guid>("SELECT id FROM editions WHERE work_id = @workId ORDER BY id;", new { workId }, tx).ToArray();
        if (editions.Length == 0)
        {
            return null;
        }

        var assets = conn.Query<Guid>("""
            SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @workId ORDER BY ma.id;
            """, new { workId }, tx).ToArray();
        var siblings = conn.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM works s
            WHERE s.parent_work_id = @parentId AND s.id <> @workId AND s.work_kind = 'child'
              AND EXISTS (SELECT 1 FROM work_owned_assets woa WHERE woa.work_id = s.id);
            """, new { parentId = row.ParentId, workId }, tx);
        return new ItemMoveSource(row.WorkId, row.MediaType, row.Title, row.ParentId, row.ParentTitle, row.Ordinal, editions, assets, siblings);
    }

    private static IReadOnlyList<ItemMoveEntry> ReadEntries(
        Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction? tx, Guid destinationId) =>
        conn.Query<EntryRow>($"""
            SELECT w.id AS Id, {TitleSql} AS Title, w.ordinal AS Ordinal,
                   (SELECT COUNT(*) FROM editions e WHERE e.work_id = w.id) AS EditionCount,
                   EXISTS (SELECT 1 FROM work_owned_assets woa WHERE woa.work_id = w.id) AS Taken
            FROM works w
            WHERE w.parent_work_id = @destinationId AND w.work_kind IN ('child', 'catalog')
            ORDER BY w.ordinal_sort IS NULL, w.ordinal_sort, w.ordinal;
            """, new { destinationId }, tx)
            .Select(row => new ItemMoveEntry(row.Id, row.Title, row.Ordinal, row.Taken, row.EditionCount))
            .ToList();

    private static ItemMoveDestination ToDestination(
        Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction? tx, DestinationRow row)
    {
        // A season reads as "Season 2" under its show; anything else is just its title.
        if (row.ParentId is { } showId && row.Ordinal is { } season)
        {
            var show = conn.QuerySingleOrDefault<string>($"""
                SELECT {TitleSql} FROM works w WHERE w.id = @showId;
                """, new { showId }, tx);
            var label = season == 0 ? "Specials" : $"Season {season}";
            return new ItemMoveDestination(row.Id, string.IsNullOrWhiteSpace(show) ? label : $"{show} · {label}", null, row.EntryCount);
        }

        return new ItemMoveDestination(row.Id, row.Title, null, row.EntryCount);
    }

    private sealed class SourceRow
    {
        public Guid WorkId { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public Guid? ParentId { get; set; }
        public int? Ordinal { get; set; }
        public string? ParentTitle { get; set; }
    }

    private sealed class DestinationRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public int? Ordinal { get; set; }
        public Guid? ParentId { get; set; }
        public int EntryCount { get; set; }
        public string MediaType { get; set; } = string.Empty;
    }

    private sealed class EntryRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public int? Ordinal { get; set; }
        public int EditionCount { get; set; }
        public bool Taken { get; set; }
    }
}
