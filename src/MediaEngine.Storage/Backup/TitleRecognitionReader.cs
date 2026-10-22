using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Backup;

/// <summary>
/// Reads, for one catalogue row, what is needed to recognise the same title after a rescan: external ids, running
/// time, edition, file fingerprints, name and year. This is the only place that knows how to do that, so a backup, a
/// restore and bookmark packs agree. It reads only the recognition data; descriptions, artwork and file paths are
/// never read.
/// </summary>
internal sealed class TitleRecognitionReader
{
    /// <summary>The most file fingerprints kept for one title.</summary>
    private const int FileHashCap = 20;

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction? _transaction;

    public TitleRecognitionReader(SqliteConnection connection, SqliteTransaction? transaction)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _transaction = transaction;
    }

    /// <summary>
    /// Reads the recognition data of one catalogue row. The returned title has an empty key for the caller to set.
    /// Returns null when the row no longer exists. <see cref="CatalogueTarget.Other"/> yields a title with only the
    /// target set, because such a row has no recognition data.
    /// </summary>
    public BackupTitle? Read(CatalogueTarget target, byte[] id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var title = target switch
        {
            CatalogueTarget.MediaAsset => ReadAsset(id),
            CatalogueTarget.Edition => ReadEdition(id),
            CatalogueTarget.Work => ReadWork(id),
            CatalogueTarget.Collection => ReadCollection(id),
            CatalogueTarget.Person => ReadPerson(id),
            CatalogueTarget.LocalItem => ReadLocalItem(id),
            CatalogueTarget.FictionalEntity => ReadFictionalEntity(id),
            CatalogueTarget.Other => BackupTitle.Empty(string.Empty, nameof(CatalogueTarget.Other)),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown catalogue target."),
        };

        return title is null ? null : IsWithheldTitle(target, id) ? Withhold(title) : title;
    }

    /// <summary>
    /// Finds what a polymorphic catalogue id points at. When the row says what kind it is (a saved item's entity
    /// kind), that decides; otherwise the id is probed against works, collections, persons, editions, media assets,
    /// local items and fictional things, in that order. Returns null when nothing has that id.
    /// </summary>
    public CatalogueTarget? ResolveOther(string? kind, byte[] id)
    {
        ArgumentNullException.ThrowIfNull(id);
        switch (kind)
        {
            case "Movie":
            case "TvShow":
            case "Book":
            case "Comic":
            case "Audiobook":
            case "Album":
            case "Song":
                return Exists("works", id) ? CatalogueTarget.Work : null;
            case "Collection":
            case "Playlist":
                return Exists("collections", id) ? CatalogueTarget.Collection : null;
        }

        (string Table, CatalogueTarget Target)[] probes =
        [
            ("works", CatalogueTarget.Work),
            ("collections", CatalogueTarget.Collection),
            ("persons", CatalogueTarget.Person),
            ("editions", CatalogueTarget.Edition),
            ("media_assets", CatalogueTarget.MediaAsset),
            ("local_items", CatalogueTarget.LocalItem),
            ("fictional_entities", CatalogueTarget.FictionalEntity),
        ];
        foreach (var (table, target) in probes)
        {
            if (Exists(table, id))
            {
                return target;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a title must not carry a name, parent name or year in a backup. Today nothing is withheld; this is
    /// wired to the adult-video libraries when Videos Wave 3 merges, so such a title appears only as ids and
    /// fingerprints.
    /// </summary>
    private static bool IsWithheldTitle(CatalogueTarget target, byte[] id)
    {
        _ = target;
        _ = id;
        return false;
    }

    private static BackupTitle Withhold(BackupTitle title) =>
        title with { Name = null, ParentName = null, Year = null };

    private BackupTitle? ReadAsset(byte[] assetId)
    {
        var rows = Query(
            "SELECT content_hash, edition_id FROM media_assets WHERE id = $id",
            ("$id", assetId));
        if (rows.Count == 0)
        {
            return null;
        }

        var hash = Text(rows[0][0]);
        var edition = ReadEditionFields(rows[0][1] as byte[]);
        var duration = Query(
            """
            SELECT duration_secs FROM playback_inspection_cache
            WHERE asset_id = $id AND duration_secs IS NOT NULL
            ORDER BY inspected_at DESC LIMIT 1
            """,
            ("$id", assetId));

        return edition with
        {
            Target = nameof(CatalogueTarget.MediaAsset),
            FileHashes = hash is null ? [] : [hash.ToLowerInvariant()],
            RunningTimeSeconds = duration.Count == 0 ? null : Real(duration[0][0]),
        };
    }

    private BackupTitle? ReadEdition(byte[] editionId)
    {
        if (!Exists("editions", editionId))
        {
            return null;
        }

        var edition = ReadEditionFields(editionId);
        var originals = Query(
            """
            SELECT ma.content_hash FROM media_assets ma
            WHERE ma.edition_id = $id AND ma.rendition_purpose = 'Original'
            ORDER BY ma.content_hash LIMIT $cap
            """,
            ("$id", editionId),
            ("$cap", (long)FileHashCap));

        // The running time comes from the edition's earliest-presented original (then the lowest id), so the same
        // library always gives the same answer rather than whichever file sorts first by hash.
        var primary = Query(
            """
            SELECT ma.id FROM media_assets ma
            WHERE ma.edition_id = $id AND ma.rendition_purpose = 'Original'
            ORDER BY ma.presented_at IS NULL, ma.presented_at, ma.id LIMIT 1
            """,
            ("$id", editionId));

        double? runningTime = null;
        if (primary.Count > 0 && primary[0][0] is byte[] firstAsset)
        {
            var duration = Query(
                """
                SELECT duration_secs FROM playback_inspection_cache
                WHERE asset_id = $id AND duration_secs IS NOT NULL
                ORDER BY inspected_at DESC LIMIT 1
                """,
                ("$id", firstAsset));
            runningTime = duration.Count == 0 ? null : Real(duration[0][0]);
        }

        return edition with
        {
            Target = nameof(CatalogueTarget.Edition),
            FileHashes = HashesOf(originals, 0),
            RunningTimeSeconds = runningTime,
        };
    }

    /// <summary>The edition label and ids, and the recognition of the edition's work. Empty when the edition is gone.</summary>
    private BackupTitle ReadEditionFields(byte[]? editionId)
    {
        var empty = BackupTitle.Empty(string.Empty, string.Empty);
        if (editionId is null)
        {
            return empty;
        }

        var rows = Query(
            "SELECT work_id, format_label, wikidata_qid FROM editions WHERE id = $id",
            ("$id", editionId));
        if (rows.Count == 0)
        {
            return empty;
        }

        var work = rows[0][0] is byte[] workId ? ReadWorkFields(workId) : null;
        var title = work ?? empty;
        var ids = new Dictionary<string, string>(title.Ids, StringComparer.Ordinal);
        var editionQid = Text(rows[0][2]);
        if (!string.IsNullOrWhiteSpace(editionQid))
        {
            ids.TryAdd("edition_wikidata_qid", editionQid);
        }

        return title with
        {
            Ids = ids,
            Edition = NullIfBlank(Text(rows[0][1])),
            FileHashes = [],
        };
    }

    private BackupTitle? ReadWork(byte[] workId)
    {
        var title = ReadWorkFields(workId);
        if (title is null)
        {
            return null;
        }

        var originals = Query(
            """
            SELECT ma.content_hash FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            WHERE e.work_id = $id AND ma.rendition_purpose = 'Original'
            ORDER BY ma.content_hash LIMIT $cap
            """,
            ("$id", workId),
            ("$cap", (long)FileHashCap));

        return title with
        {
            Target = nameof(CatalogueTarget.Work),
            FileHashes = HashesOf(originals, 0),
        };
    }

    /// <summary>Ids, media type, ordinal, name, year and the same for the parent. Null when the work is gone.</summary>
    private BackupTitle? ReadWorkFields(byte[] workId)
    {
        var rows = Query(
            "SELECT media_type, ordinal, parent_work_id, wikidata_qid FROM works WHERE id = $id",
            ("$id", workId));
        if (rows.Count == 0)
        {
            return null;
        }

        var row = rows[0];
        var title = BackupTitle.Empty(string.Empty, nameof(CatalogueTarget.Work)) with
        {
            MediaType = NullIfBlank(Text(row[0])),
            Ordinal = Int(row[1]),
            Ids = ReadWorkIds(workId, Text(row[3])),
            Name = ReadCanonical(workId, "title"),
        };

        var year = ParseYear(ReadCanonical(workId, "year"));
        if (row[2] is byte[] parentId)
        {
            var parent = Query(
                "SELECT ordinal, wikidata_qid FROM works WHERE id = $id",
                ("$id", parentId));
            if (parent.Count > 0)
            {
                title = title with
                {
                    ParentIds = ReadWorkIds(parentId, Text(parent[0][1])),
                    ParentOrdinal = Int(parent[0][0]),
                    ParentName = ReadCanonical(parentId, "title"),
                };
                year ??= ParseYear(ReadCanonical(parentId, "year"));
            }
        }

        return title with { Year = year };
    }

    /// <summary>
    /// The work's external ids: its Wikidata id, then the bridge ids of the work and its editions (the work's own
    /// rows first; the first value for a type wins). Types are lower case.
    /// </summary>
    private Dictionary<string, string> ReadWorkIds(byte[] workId, string? wikidataQid)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(wikidataQid))
        {
            ids["wikidata_qid"] = wikidataQid;
        }

        var rows = Query(
            """
            SELECT id_type, id_value FROM bridge_ids
            WHERE entity_id = $id OR entity_id IN (SELECT id FROM editions WHERE work_id = $id)
            ORDER BY CASE WHEN entity_id = $id THEN 0 ELSE 1 END, created_at, id_type
            """,
            ("$id", workId));
        foreach (var row in rows)
        {
            var type = Text(row[0])?.Trim().ToLowerInvariant();
            var value = Text(row[1]);
            if (!string.IsNullOrEmpty(type) && !string.IsNullOrWhiteSpace(value))
            {
                ids.TryAdd(type, value);
            }
        }

        return ids;
    }

    private BackupTitle? ReadCollection(byte[] id)
    {
        var rows = Query(
            "SELECT wikidata_qid, display_name, media_type FROM collections WHERE id = $id",
            ("$id", id));
        if (rows.Count == 0)
        {
            return null;
        }

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var qid = Text(rows[0][0]);
        if (!string.IsNullOrWhiteSpace(qid))
        {
            ids["wikidata_qid"] = qid;
        }

        return BackupTitle.Empty(string.Empty, nameof(CatalogueTarget.Collection)) with
        {
            Ids = ids,
            Name = NullIfBlank(Text(rows[0][1])),
            MediaType = NullIfBlank(Text(rows[0][2])),
        };
    }

    private BackupTitle? ReadPerson(byte[] id)
    {
        var rows = Query(
            "SELECT name, wikidata_qid FROM persons WHERE id = $id",
            ("$id", id));
        if (rows.Count == 0)
        {
            return null;
        }

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var qid = Text(rows[0][1]);
        if (!string.IsNullOrWhiteSpace(qid))
        {
            ids["wikidata_qid"] = qid;
        }

        return BackupTitle.Empty(string.Empty, nameof(CatalogueTarget.Person)) with
        {
            Ids = ids,
            Name = NullIfBlank(Text(rows[0][0])),
        };
    }

    private BackupTitle? ReadLocalItem(byte[] id)
    {
        var rows = Query(
            "SELECT title, media_kind FROM local_items WHERE id = $id",
            ("$id", id));
        if (rows.Count == 0)
        {
            return null;
        }

        var files = Query(
            """
            SELECT f.content_hash FROM local_item_files lif
            JOIN local_files f ON f.id = lif.file_id
            WHERE lif.item_id = $id
            ORDER BY CASE lif.role WHEN 'primary' THEN 0 WHEN 'original' THEN 1 ELSE 2 END, lif.position, f.content_hash
            LIMIT $cap
            """,
            ("$id", id),
            ("$cap", (long)FileHashCap));

        return BackupTitle.Empty(string.Empty, nameof(CatalogueTarget.LocalItem)) with
        {
            Name = NullIfBlank(Text(rows[0][0])),
            MediaType = NullIfBlank(Text(rows[0][1])),
            FileHashes = HashesOf(files, 0),
        };
    }

    private BackupTitle? ReadFictionalEntity(byte[] id)
    {
        var rows = Query(
            "SELECT wikidata_qid, label FROM fictional_entities WHERE id = $id",
            ("$id", id));
        if (rows.Count == 0)
        {
            return null;
        }

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var qid = Text(rows[0][0]);
        if (!string.IsNullOrWhiteSpace(qid))
        {
            ids["wikidata_qid"] = qid;
        }

        return BackupTitle.Empty(string.Empty, nameof(CatalogueTarget.FictionalEntity)) with
        {
            Ids = ids,
            Name = NullIfBlank(Text(rows[0][1])),
        };
    }

    private string? ReadCanonical(byte[] entityId, string key)
    {
        var rows = Query(
            "SELECT value FROM canonical_values WHERE entity_id = $id AND key = $key LIMIT 1",
            ("$id", entityId),
            ("$key", key));
        return rows.Count == 0 ? null : NullIfBlank(Text(rows[0][0]));
    }

    private bool Exists(string table, byte[] id) =>
        Query($"SELECT 1 FROM \"{table}\" WHERE id = $id LIMIT 1", ("$id", id)).Count > 0;

    private List<object?[]> Query(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static List<string> HashesOf(List<object?[]> rows, int column) =>
        rows.Select(row => Text(row[column]))
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Select(hash => hash!.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(FileHashCap)
            .ToList();

    private static string? Text(object? value) => value switch
    {
        null => null,
        string text => text,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static double? Real(object? value) => value switch
    {
        double real => real,
        long integer => integer,
        _ => null,
    };

    private static int? Int(object? value) =>
        value is long integer && integer is >= int.MinValue and <= int.MaxValue ? (int)integer : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? ParseYear(string? value)
    {
        if (value is null || value.Length < 4)
        {
            return null;
        }

        return int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null;
    }
}
