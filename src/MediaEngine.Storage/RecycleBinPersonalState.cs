using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// Keeps people's progress while a deleted file waits in the Recycle bin (delete groups D21). Deleting a title removes its records,
/// including every profile's progress, bookmarks and history, so before that happens the rows tied to each file are copied into
/// <c>recycle_bin_personal_state</c> under the file's fingerprint. When a file with the same fingerprint is added again, the rows are
/// put back on the new copy (and on the title it now belongs to) and the kept copies are dropped.
/// </summary>
internal static class RecycleBinPersonalState
{
    /// <summary>A table whose rows hang off one file. <see cref="HasWork"/> tables also carry the title; <see cref="HasProfile"/> tables need the profile to still exist.</summary>
    private sealed record Kept(string Table, bool HasWork = false, bool HasProfile = false);

    private static readonly Kept[] Tables =
    [
        new("user_states"),
        new("consumption_history"),
        new("reader_bookmarks"),
        new("reader_highlights"),
        new("reader_statistics"),
        new("audiobook_bookmarks", HasWork: true, HasProfile: true),
        new("audiobook_listen_history", HasWork: true, HasProfile: true),
        new("bookmark_packs", HasWork: true, HasProfile: true),
    ];

    private sealed record Cell(string? T, string? V);

    /// <summary>
    /// Copies the rows of every file in <c>deletion_assets</c> into the kept table. Must run before the files' records are removed.
    /// A file with no fingerprint cannot be recognised later, so it is skipped. Returns how many rows were kept.
    /// </summary>
    public static int Snapshot(SqliteConnection connection, SqliteTransaction transaction)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var kept = 0;
        connection.Execute("""
            DELETE FROM recycle_bin_personal_state
            WHERE content_hash IN (SELECT content_hash FROM media_assets WHERE id IN (SELECT id FROM deletion_assets) AND content_hash IS NOT NULL);
            """, transaction: transaction);

        foreach (var kind in Tables)
        {
            var rows = new List<(string Hash, string Json)>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    -- readiness: copies personal rows of files that are about to be deleted; the title need not be ready
                    SELECT t.*, ma.content_hash AS __hash
                    FROM {kind.Table} t
                    INNER JOIN media_assets ma ON ma.id = t.asset_id
                    WHERE t.asset_id IN (SELECT id FROM deletion_assets) AND ma.content_hash IS NOT NULL;
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var cells = new Dictionary<string, Cell>(StringComparer.Ordinal);
                    string? hash = null;
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var name = reader.GetName(i);
                        if (name == "__hash")
                        {
                            hash = reader.GetString(i);
                            continue;
                        }

                        cells[name] = reader.IsDBNull(i) ? new Cell(null, null) : reader.GetValue(i) switch
                        {
                            byte[] bytes => new Cell("b", Convert.ToBase64String(bytes)),
                            long number => new Cell("i", number.ToString(CultureInfo.InvariantCulture)),
                            double number => new Cell("r", number.ToString("R", CultureInfo.InvariantCulture)),
                            var other => new Cell("s", Convert.ToString(other, CultureInfo.InvariantCulture)),
                        };
                    }

                    if (hash is not null)
                    {
                        rows.Add((hash, JsonSerializer.Serialize(cells)));
                    }
                }
            }

            foreach (var (hash, json) in rows)
            {
                connection.Execute(
                    "INSERT INTO recycle_bin_personal_state (content_hash, table_name, row_json, kept_at) VALUES (@hash, @table, @json, @now);",
                    new { hash, table = kind.Table, json, now },
                    transaction);
                kept++;
            }
        }

        return kept;
    }

    /// <summary>
    /// Puts kept rows back on a file that has just been added, then drops the kept copies. <paramref name="assetId"/> is the new file's
    /// row; its title is read from the data store. Rows already present are left alone. Returns how many rows were put back.
    /// </summary>
    public static int Reattach(IDbConnection connection, Guid assetId, string? contentHash)
    {
        if (string.IsNullOrEmpty(contentHash))
        {
            return 0;
        }

        var kept = connection.Query<(long Id, string Table, string Json)>(
            "SELECT id AS Id, table_name AS \"Table\", row_json AS Json FROM recycle_bin_personal_state WHERE content_hash = @contentHash;",
            new { contentHash }).ToList();
        if (kept.Count == 0)
        {
            return 0;
        }

        var workId = connection.QuerySingleOrDefault<Guid?>(
            "SELECT e.work_id FROM media_assets ma INNER JOIN editions e ON e.id = ma.edition_id WHERE ma.id = @assetId;",
            new { assetId });
        var restored = 0;
        foreach (var (id, table, json) in kept)
        {
            var kind = Tables.FirstOrDefault(candidate => candidate.Table == table);
            var cells = JsonSerializer.Deserialize<Dictionary<string, Cell>>(json);
            if (kind is null || cells is null || (kind.HasWork && workId is null))
            {
                continue;
            }

            if (kind.HasProfile
                && cells.TryGetValue("profile_id", out var profile)
                && connection.ExecuteScalar<long>("SELECT COUNT(*) FROM profiles WHERE id = @id;", new { id = Convert.FromBase64String(profile.V ?? string.Empty) }) == 0)
            {
                continue;
            }

            var parameters = new DynamicParameters();
            foreach (var (name, cell) in cells)
            {
                object? value = cell.T switch
                {
                    "b" => Convert.FromBase64String(cell.V ?? string.Empty),
                    "i" => long.Parse(cell.V!, CultureInfo.InvariantCulture),
                    "r" => double.Parse(cell.V!, CultureInfo.InvariantCulture),
                    "s" => cell.V,
                    _ => null,
                };
                parameters.Add(name, name == "asset_id" ? assetId : name == "work_id" && kind.HasWork ? workId : value);
            }

            var columns = cells.Keys.ToList();
            var inserted = connection.Execute(
                $"INSERT OR IGNORE INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(c => "@" + c))});",
                parameters);
            restored += inserted > 0 ? 1 : 0;
        }

        connection.Execute("DELETE FROM recycle_bin_personal_state WHERE content_hash = @contentHash;", new { contentHash });
        return restored;
    }

    /// <summary>Drops kept rows older than <paramref name="olderThan"/> (null drops every one). Returns how many rows were dropped.</summary>
    public static int Discard(IDbConnection connection, DateTimeOffset? olderThan) =>
        olderThan is null
            ? connection.Execute("DELETE FROM recycle_bin_personal_state;")
            : connection.Execute(
                "DELETE FROM recycle_bin_personal_state WHERE kept_at < @cutoff;",
                new { cutoff = olderThan.Value.ToString("O", CultureInfo.InvariantCulture) });
}
