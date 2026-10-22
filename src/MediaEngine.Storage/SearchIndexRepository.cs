using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite FTS5 full-text search index for works. Replaces in-memory re-ranking
/// with native database-level search, prefix matching, and BM25 ranking.
/// Supports cross-language search via original_title and alternate_titles columns
/// (e.g. "Amélie" matches "Le Fabuleux Destin d'Amélie Poulain" via unicode61 tokenizer).
/// </summary>
public sealed class SearchIndexRepository : ISearchIndexRepository
{
    private readonly IDatabaseConnection _db;

    public SearchIndexRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task UpsertByEntityIdAsync(Guid entityId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();

        // Phase 4 — lineage-aware refresh. The entityId may be either a
        // media_assets id (Self-scope writes) or a works id (Parent-scope
        // writes from the parent persist pass). Both resolve to the same
        // leaf work row.
        using var lookup = conn.CreateCommand();
        lookup.CommandText = """
            SELECT w.id                                AS work_id,
                   ma.id                               AS asset_id,
                   COALESCE(gp.id, p.id, w.id)         AS root_parent_id
            FROM works w
            LEFT JOIN works p          ON p.id  = w.parent_work_id
            LEFT JOIN works gp         ON gp.id = p.parent_work_id
            LEFT JOIN editions e       ON e.work_id = w.id
            LEFT JOIN media_assets ma  ON ma.edition_id = e.id
            WHERE w.id = @entityId
               OR ma.id = @entityId
            LIMIT 1
            """;
        lookup.Parameters.Add("@entityId", SqliteType.Blob).Value = GuidSql.ToBlob(entityId);

        Guid? workId = null, assetId = null, rootParentId = null;
        using (var rdr = lookup.ExecuteReader())
        {
            if (rdr.Read())
            {
                workId = rdr.IsDBNull(0) ? null : GuidSql.FromDb(rdr.GetValue(0));
                assetId = rdr.IsDBNull(1) ? null : GuidSql.FromDb(rdr.GetValue(1));
                rootParentId = rdr.IsDBNull(2) ? null : GuidSql.FromDb(rdr.GetValue(2));
            }
        }
        if (workId is null)
        {
            return Task.CompletedTask;
        }

        // A work with a file in a Vault library is never indexed for the shared search (Videos spec D27); a row it had is dropped.
        using (var vaultCheck = conn.CreateCommand())
        {
            vaultCheck.CommandText = $"SELECT NOT ({VaultSql.NotVaultEntity("@workId")});";
            vaultCheck.Parameters.Add("@workId", SqliteType.Blob).Value = GuidSql.ToBlob(workId.Value);
            if (Convert.ToInt64(vaultCheck.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                using var drop = conn.CreateCommand();
                drop.CommandText = "DELETE FROM search_index WHERE entity_id = @workId;";
                drop.Parameters.Add("@workId", SqliteType.Blob).Value = GuidSql.ToBlob(workId.Value);
                drop.ExecuteNonQuery();
                return Task.CompletedTask;
            }
        }

        // Self-scope reads (asset row).
        string? title = null, originalTitle = null;
        if (assetId is not null)
        {
            using var selfCmd = conn.CreateCommand();
            selfCmd.CommandText = """
                SELECT key, value FROM canonical_values
                WHERE entity_id = @assetId
                  AND key IN ('title', 'original_title')
                """;
            selfCmd.Parameters.Add("@assetId", SqliteType.Blob).Value = GuidSql.ToBlob(assetId.Value);
            using var sr = selfCmd.ExecuteReader();
            while (sr.Read())
            {
                var k = sr.GetString(0);
                var v = sr.IsDBNull(1) ? null : sr.GetString(1);
                if (k == "title")
                {
                    title = v;
                }

                if (k == "original_title")
                {
                    originalTitle = v;
                }
            }
        }

        // Parent-scope reads (topmost Work row).
        string? author = null, description = null;
        if (rootParentId is not null)
        {
            using var parentCmd = conn.CreateCommand();
            parentCmd.CommandText = """
                SELECT 'author' AS key, value
                FROM canonical_value_arrays
                WHERE entity_id = @parentId
                  AND key IN ('author', 'creator')
                ORDER BY ordinal
                LIMIT 1
                """;
            parentCmd.Parameters.Add("@parentId", SqliteType.Blob).Value = GuidSql.ToBlob(rootParentId.Value);
            using var pr = parentCmd.ExecuteReader();
            while (pr.Read())
            {
                var k = pr.GetString(0);
                var v = pr.IsDBNull(1) ? null : pr.GetString(1);
                if (k == "author")
                {
                    author = v;
                }
            }

            using var descriptionCmd = conn.CreateCommand();
            descriptionCmd.CommandText = """
                SELECT value
                FROM canonical_values
                WHERE entity_id = @parentId
                  AND key = 'description'
                LIMIT 1
                """;
            descriptionCmd.Parameters.Add("@parentId", SqliteType.Blob).Value = GuidSql.ToBlob(rootParentId.Value);
            description = descriptionCmd.ExecuteScalar() as string;
        }

        // Alternate titles live in canonical_value_arrays. They are Self-scope
        // by default, so they live on the asset row.
        string? alternateTitles = null;
        if (assetId is not null)
        {
            using var altCmd = conn.CreateCommand();
            altCmd.CommandText = """
                SELECT GROUP_CONCAT(value, ' ')
                FROM canonical_value_arrays
                WHERE entity_id = @assetId AND key = 'alternate_title'
                """;
            altCmd.Parameters.Add("@assetId", SqliteType.Blob).Value = GuidSql.ToBlob(assetId.Value);
            var altObj = altCmd.ExecuteScalar();
            alternateTitles = altObj as string;
        }

        // Skip empty rows entirely.
        if (string.IsNullOrWhiteSpace(title)
            && string.IsNullOrWhiteSpace(originalTitle)
            && string.IsNullOrWhiteSpace(author))
        {
            return Task.CompletedTask;
        }

        // FTS5 does not support UPSERT — delete then insert.
        using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM search_index WHERE entity_id = @workId;";
        del.Parameters.Add("@workId", SqliteType.Blob).Value = GuidSql.ToBlob(workId.Value);
        del.ExecuteNonQuery();

        using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO search_index (entity_id, title, original_title, alternate_titles, author, description)
            VALUES (@workId, @title, @originalTitle, @alternateTitles, @author, @description);
            """;
        ins.Parameters.Add("@workId", SqliteType.Blob).Value = GuidSql.ToBlob(workId.Value);
        ins.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
        ins.Parameters.AddWithValue("@originalTitle", (object?)originalTitle ?? DBNull.Value);
        ins.Parameters.AddWithValue("@alternateTitles", (object?)alternateTitles ?? DBNull.Value);
        ins.Parameters.AddWithValue("@author", (object?)author ?? DBNull.Value);
        ins.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        ins.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Guid>> SearchAsync(string query, int limit = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<Guid>>([]);
        }

        ct.ThrowIfCancellationRequested();
        var trimmed = query.Trim();
        using var conn = _db.CreateConnection();

        // The trigram tokenizer requires at least 3 characters per token.
        // For short queries (< 3 chars) fall back to a LIKE scan on canonical_values
        // because the FTS5 trigram index cannot match them.
        if (trimmed.Length < 3)
        {
            // Phase 4 — title/original_title are Self (asset row), author is Parent
            // (topmost Work row). Search both targets via UNION.
            using var likeCmd = conn.CreateCommand();
            likeCmd.CommandText = $"""
                SELECT DISTINCT w.id
                FROM works w
                JOIN editions e ON e.work_id = w.id
                JOIN media_assets ma ON ma.edition_id = e.id
                JOIN canonical_values cv ON cv.entity_id = ma.id
                WHERE cv.key IN ('title', 'original_title')
                  AND cv.value LIKE @pattern
                  AND {VaultSql.NotVaultEntity("w.id")}
                UNION
                SELECT DISTINCT w.id
                FROM works w
                LEFT JOIN works p  ON p.id  = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                JOIN canonical_value_arrays cv
                  ON cv.entity_id = COALESCE(gp.id, p.id, w.id)
                WHERE cv.key IN ('author', 'creator')
                  AND cv.value LIKE @pattern
                  AND {VaultSql.NotVaultEntity("w.id")}
                LIMIT @limit
                """;
            likeCmd.Parameters.AddWithValue("@pattern", $"%{trimmed}%");
            likeCmd.Parameters.AddWithValue("@limit", limit);

            var likeResults = new List<Guid>();
            using var likeReader = likeCmd.ExecuteReader();
            while (likeReader.Read())
            {
                likeResults.Add(GuidSql.FromDb(likeReader.GetValue(0)));
            }
            return Task.FromResult<IReadOnlyList<Guid>>(likeResults);
        }

        // FTS5 trigram search: quote the phrase for exact substring matching.
        // The trigram tokenizer does not support prefix (*) or BM25 ranking —
        // omit the trailing * and ORDER BY rank clause.
        var escaped = trimmed.Replace("\"", "\"\"");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT entity_id FROM search_index
            WHERE search_index MATCH @ftsQuery
              AND {VaultSql.NotVaultEntity("search_index.entity_id")}
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@ftsQuery", $"\"{escaped}\"");
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<Guid>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(GuidSql.FromDb(reader.GetValue(0)));
        }
        return Task.FromResult<IReadOnlyList<Guid>>(results);
    }

    /// <inheritdoc/>
    public Task RebuildAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();

        using var clear = conn.CreateCommand();
        clear.CommandText = "DELETE FROM search_index;";
        clear.ExecuteNonQuery();

        // Phase 4 — repopulate using lineage-aware sources:
        //   • title, original_title, alternate_titles → SELF (asset row)
        //   • author, description                     → PARENT (topmost Work)
        using var populate = conn.CreateCommand();
        populate.CommandText = $"""
            INSERT INTO search_index (entity_id, title, original_title, alternate_titles, author, description)
            SELECT
                w.id,
                (SELECT MAX(cv.value)
                 FROM canonical_values cv
                 JOIN media_assets ma2 ON ma2.id = cv.entity_id
                 JOIN editions e2 ON e2.id = ma2.edition_id
                 WHERE e2.work_id = w.id AND cv.key = 'title'),
                (SELECT MAX(cv.value)
                 FROM canonical_values cv
                 JOIN media_assets ma3 ON ma3.id = cv.entity_id
                 JOIN editions e3 ON e3.id = ma3.edition_id
                 WHERE e3.work_id = w.id AND cv.key = 'original_title'),
                (SELECT GROUP_CONCAT(cva.value, ' ')
                 FROM canonical_value_arrays cva
                 JOIN editions e4 ON e4.work_id = w.id
                 JOIN media_assets ma4 ON ma4.edition_id = e4.id
                 WHERE cva.entity_id = ma4.id AND cva.key = 'alternate_title'),
                (SELECT cv.value
                 FROM canonical_value_arrays cv
                 WHERE cv.entity_id = COALESCE(gp.id, p.id, w.id)
                   AND cv.key IN ('author', 'creator')
                 ORDER BY cv.ordinal
                 LIMIT 1),
                (SELECT cv.value
                 FROM canonical_values cv
                 WHERE cv.entity_id = COALESCE(gp.id, p.id, w.id)
                   AND cv.key = 'description'
                 LIMIT 1)
            FROM works w
            LEFT JOIN works p  ON p.id  = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            -- Only index leaf works that have at least one media asset, and never a work with a file in a Vault library.
            WHERE EXISTS (
                SELECT 1 FROM editions ex
                JOIN media_assets max ON max.edition_id = ex.id
                WHERE ex.work_id = w.id
            )
            AND {VaultSql.NotVaultEntity("w.id")};
            """;
        populate.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}
