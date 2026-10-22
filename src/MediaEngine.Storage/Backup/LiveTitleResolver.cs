using System.Globalization;
using MediaEngine.Domain.Services;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Backup;

/// <summary>How a title recorded in a backup was found in the Library now.</summary>
internal enum LiveMatchKind
{
    /// <summary>Not found, or more than one thing is a candidate. The state keeps waiting.</summary>
    None,

    /// <summary>Exactly one live row is this title (for a file, the same cut).</summary>
    Exact,

    /// <summary>Exactly one live file is this title but a different cut.</summary>
    DifferentCut,
}

/// <summary>The live row a restored title belongs to. <see cref="Id"/> is its id.</summary>
internal sealed record LiveTitleMatch(LiveMatchKind Kind, byte[]? Id)
{
    public static LiveTitleMatch None { get; } = new(LiveMatchKind.None, null);
}

/// <summary>
/// Finds, in the live catalogue, the row a title recorded in a backup now is. It never guesses: a title is resolved only when
/// exactly one candidate fits, and a candidate fits only when the ids both sides hold agree (or, for a file with no ids, when the
/// file fingerprint is the same). The recognition data of the live rows is read by <see cref="TitleRecognitionReader"/>, so the
/// backup and the restore agree on what a title is; the rules for the cut of a file are <see cref="RestoredTitleRules"/>.
/// </summary>
internal sealed class LiveTitleResolver
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction? _transaction;
    private readonly TitleRecognitionReader _reader;
    private readonly Dictionary<string, LiveTitleMatch> _cache = new(StringComparer.Ordinal);

    public LiveTitleResolver(SqliteConnection connection, SqliteTransaction? transaction)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _transaction = transaction;
        _reader = new TitleRecognitionReader(connection, transaction);
    }

    /// <summary>Resolves a title; the answer is kept for the rest of the pass, because many rows share a title.</summary>
    public LiveTitleMatch Resolve(BackupTitle title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var signature = Signature(title);
        if (_cache.TryGetValue(signature, out var known))
        {
            return known;
        }

        var match = Enum.TryParse<CatalogueTarget>(title.Target, out var target)
            ? Resolve(title, target)
            : LiveTitleMatch.None;
        _cache[signature] = match;
        return match;
    }

    /// <summary>The running time of a live file, from its latest inspection.</summary>
    public double? ReadRunningTime(byte[] assetId)
    {
        var rows = Query(
            "SELECT duration_secs FROM playback_inspection_cache WHERE asset_id = $id AND duration_secs IS NOT NULL ORDER BY inspected_at DESC LIMIT 1",
            ("$id", assetId));
        return rows.Count == 0 ? null : rows[0][0] switch { double real => real, long integer => integer, _ => null };
    }

    /// <summary>The chapters of a live file that have a name, with their start in seconds.</summary>
    public IReadOnlyList<BookmarkPackChapter> ReadChapters(byte[] assetId)
    {
        var rows = Query(
            "SELECT title, start_ms FROM media_asset_chapters WHERE asset_id = $id AND title IS NOT NULL AND TRIM(title) <> '' ORDER BY chapter_index",
            ("$id", assetId));
        return rows.Select(row => new BookmarkPackChapter((string)row[0]!, Convert.ToDouble(row[1], CultureInfo.InvariantCulture) / 1000.0)).ToList();
    }

    private LiveTitleMatch Resolve(BackupTitle title, CatalogueTarget target) => target switch
    {
        CatalogueTarget.MediaAsset => ResolveAsset(title),
        CatalogueTarget.Work => ResolveWork(title),
        CatalogueTarget.Edition => ResolveEdition(title),
        CatalogueTarget.Collection => ResolveCollection(title),
        CatalogueTarget.Person => ResolvePerson(title),
        CatalogueTarget.FictionalEntity => ResolveByQid("fictional_entities", title),
        CatalogueTarget.LocalItem => ResolveLocalItem(title),
        CatalogueTarget.ArtworkAsset => ResolveArtwork(title),

        // Nothing identifies the row, so it waits until it is cleared.
        _ => LiveTitleMatch.None,
    };

    // ---------------------------------------------------------------------------------------------------------
    // Files, works and editions
    // ---------------------------------------------------------------------------------------------------------

    private LiveTitleMatch ResolveAsset(BackupTitle title)
    {
        // The exact same file is the same file, whatever ids it has.
        var byHash = AssetsByHash(title.FileHashes);
        if (byHash.Count == 1)
        {
            return new LiveTitleMatch(LiveMatchKind.Exact, byHash[0]);
        }

        if (byHash.Count > 1)
        {
            return LiveTitleMatch.None;
        }

        var pairs = new List<(BookmarkPackTitle Backup, BookmarkPackTitle Live)>();
        var assets = new List<byte[]>();
        foreach (var workId in CandidateWorks(title))
        {
            foreach (var assetId in Query(
                         """
                         SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id
                         WHERE e.work_id = $id AND ma.rendition_purpose = 'Original' AND ma.is_orphaned = 0
                         ORDER BY ma.id
                         """,
                         ("$id", workId)).Select(row => (byte[])row[0]!))
            {
                if (_reader.Read(CatalogueTarget.MediaAsset, assetId) is not { } live || ComparableIds(title, live) is not { } ids)
                {
                    continue;
                }

                pairs.Add((
                    new BookmarkPackTitle(ids.Backup, title.RunningTimeSeconds, title.Edition),
                    new BookmarkPackTitle(ids.Live, live.RunningTimeSeconds, live.Edition)));
                assets.Add(assetId);
            }
        }

        var decision = RestoredTitleRules.ChooseFile(pairs);
        return decision.Outcome switch
        {
            RestoredTitleOutcome.Exact => new LiveTitleMatch(LiveMatchKind.Exact, assets[decision.Index!.Value]),
            RestoredTitleOutcome.DifferentCut => new LiveTitleMatch(LiveMatchKind.DifferentCut, assets[decision.Index!.Value]),
            _ => LiveTitleMatch.None,
        };
    }

    private LiveTitleMatch ResolveWork(BackupTitle title)
    {
        var works = VerifiedWorks(title);
        if (works.Count == 0)
        {
            // No id found it: an exact file fingerprint still names exactly one work.
            works = WorksOfFiles(title.FileHashes);
        }

        return works.Count == 1 ? new LiveTitleMatch(LiveMatchKind.Exact, works[0]) : LiveTitleMatch.None;
    }

    private LiveTitleMatch ResolveEdition(BackupTitle title)
    {
        var editions = new List<byte[]>();
        foreach (var workId in VerifiedWorks(title))
        {
            foreach (var editionId in Query("SELECT id FROM editions WHERE work_id = $id ORDER BY id", ("$id", workId)).Select(row => (byte[])row[0]!))
            {
                if (_reader.Read(CatalogueTarget.Edition, editionId) is { } live && SameEdition(title, live))
                {
                    editions.Add(editionId);
                }
            }
        }

        if (editions.Count == 0)
        {
            editions = EditionsOfFiles(title.FileHashes);
        }

        return editions.Count == 1 ? new LiveTitleMatch(LiveMatchKind.Exact, editions[0]) : LiveTitleMatch.None;
    }

    /// <summary>Two editions of the same work are the same edition by their own Wikidata id when both have one, otherwise by label.</summary>
    private static bool SameEdition(BackupTitle backup, BackupTitle live)
    {
        var backupQid = backup.Ids.GetValueOrDefault("edition_wikidata_qid");
        var liveQid = live.Ids.GetValueOrDefault("edition_wikidata_qid");
        if (!string.IsNullOrWhiteSpace(backupQid) && !string.IsNullOrWhiteSpace(liveQid))
        {
            return string.Equals(backupQid.Trim(), liveQid.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return RestoredTitleRules.SameEditionLabel(backup.Edition, live.Edition);
    }

    /// <summary>
    /// The ids a backed-up title and a live file can be compared on: their own when they share an id kind (title-level ids must then
    /// agree; an edition-level id that disagrees only makes it another edition, see <see cref="RestoredTitleRules"/>); otherwise, for a child title (a track, an episode) with no ids it shares, the ids of the parent, but only when the
    /// parent agrees and the child sits at the same position in it. Null when they cannot be compared at all.
    /// </summary>
    private static (IReadOnlyDictionary<string, string> Backup, IReadOnlyDictionary<string, string> Live)? ComparableIds(BackupTitle backup, BackupTitle live)
    {
        if (RestoredTitleRules.ShareAnIdKind(backup.Ids, live.Ids))
        {
            return (backup.Ids, live.Ids);
        }

        var samePosition = backup.Ordinal is not null
            && backup.Ordinal == live.Ordinal
            && (backup.ParentOrdinal is null || live.ParentOrdinal is null || backup.ParentOrdinal == live.ParentOrdinal);
        return samePosition && RestoredTitleRules.ShareAnIdKind(backup.ParentIds, live.ParentIds)
            ? (backup.ParentIds, live.ParentIds)
            : null;
    }

    /// <summary>
    /// The works that are this title: found through any of its ids (or, for a child, through its parent and position), then kept
    /// only when every id both sides hold agrees.
    /// </summary>
    private List<byte[]> VerifiedWorks(BackupTitle title)
    {
        var verified = new List<byte[]>();
        foreach (var workId in CandidateWorks(title))
        {
            if (_reader.Read(CatalogueTarget.Work, workId) is not { } live || ComparableIds(title, live) is not { } ids)
            {
                continue;
            }

            if (RestoredTitleRules.IsSameTitle(ids.Backup, ids.Live))
            {
                verified.Add(workId);
            }
        }

        return verified;
    }

    /// <summary>Works that have at least one of the title's ids, plus (for a child) the children at its position under its parent.</summary>
    private List<byte[]> CandidateWorks(BackupTitle title)
    {
        var found = new List<byte[]>();
        void Add(IEnumerable<byte[]> ids)
        {
            foreach (var id in ids)
            {
                if (!found.Any(known => known.AsSpan().SequenceEqual(id)))
                {
                    found.Add(id);
                }
            }
        }

        Add(WorksByIds(title.Ids));
        if (title.Ordinal is { } ordinal && title.ParentIds.Count > 0)
        {
            foreach (var parentId in WorksByIds(title.ParentIds))
            {
                Add(Query(
                    "SELECT id FROM works WHERE parent_work_id = $parent AND ordinal = $ordinal ORDER BY id",
                    ("$parent", parentId),
                    ("$ordinal", (long)ordinal)).Select(row => (byte[])row[0]!));
            }
        }

        return found;
    }

    private IEnumerable<byte[]> WorksByIds(IReadOnlyDictionary<string, string> ids)
    {
        foreach (var (type, value) in ids.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            IEnumerable<object?[]> rows;
            switch (type)
            {
                case "wikidata_qid":
                    rows = Query(
                        "SELECT id FROM works WHERE wikidata_qid = $value UNION SELECT work_id FROM editions WHERE wikidata_qid = $value",
                        ("$value", value));
                    break;
                case "edition_wikidata_qid":
                    rows = Query("SELECT work_id FROM editions WHERE wikidata_qid = $value", ("$value", value));
                    break;
                case BookmarkPackMatcher.VaultStashBoxIdKey:
                    var split = value.IndexOf('|', StringComparison.Ordinal);
                    if (split <= 0)
                    {
                        continue;
                    }

                    rows = Query(
                        """
                        SELECT e.work_id FROM vault_matches vm
                        JOIN media_assets ma ON ma.id = vm.asset_id
                        JOIN editions e ON e.id = ma.edition_id
                        WHERE vm.server_id = $server AND vm.record_id = $record
                        """,
                        ("$server", value[..split]),
                        ("$record", value[(split + 1)..]));
                    break;
                default:
                    rows = Query(
                        """
                        SELECT w.id FROM bridge_ids b JOIN works w ON w.id = b.entity_id WHERE lower(b.id_type) = $type AND b.id_value = $value
                        UNION
                        SELECT e.work_id FROM bridge_ids b JOIN editions e ON e.id = b.entity_id WHERE lower(b.id_type) = $type AND b.id_value = $value
                        """,
                        ("$type", type.ToLowerInvariant()),
                        ("$value", value));
                    break;
            }

            foreach (var row in rows)
            {
                yield return (byte[])row[0]!;
            }
        }
    }

    private List<byte[]> AssetsByHash(IReadOnlyList<string> hashes)
    {
        var found = new List<byte[]>();
        foreach (var hash in hashes.Where(hash => !string.IsNullOrWhiteSpace(hash)))
        {
            foreach (var row in Query("SELECT id FROM media_assets WHERE content_hash = $hash COLLATE NOCASE", ("$hash", hash)))
            {
                var id = (byte[])row[0]!;
                if (!found.Any(known => known.AsSpan().SequenceEqual(id)))
                {
                    found.Add(id);
                }
            }
        }

        return found;
    }

    private List<byte[]> WorksOfFiles(IReadOnlyList<string> hashes) => DistinctParents(hashes, "e.work_id");

    private List<byte[]> EditionsOfFiles(IReadOnlyList<string> hashes) => DistinctParents(hashes, "e.id");

    private List<byte[]> DistinctParents(IReadOnlyList<string> hashes, string column)
    {
        var found = new List<byte[]>();
        foreach (var hash in hashes.Where(hash => !string.IsNullOrWhiteSpace(hash)))
        {
            foreach (var row in Query(
                         $"SELECT {column} FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE ma.content_hash = $hash COLLATE NOCASE",
                         ("$hash", hash)))
            {
                var id = (byte[])row[0]!;
                if (!found.Any(known => known.AsSpan().SequenceEqual(id)))
                {
                    found.Add(id);
                }
            }
        }

        return found;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Everything else
    // ---------------------------------------------------------------------------------------------------------

    private LiveTitleMatch ResolveCollection(BackupTitle title)
    {
        if (title.Ids.TryGetValue("wikidata_qid", out var qid) && !string.IsNullOrWhiteSpace(qid))
        {
            return Single(Query("SELECT id FROM collections WHERE wikidata_qid = $qid OR universe_qid = $qid", ("$qid", qid)));
        }

        // Without an id, only a collection a person built, found by its exact name and kind of media, and only if there is one.
        return string.IsNullOrEmpty(title.Name)
            ? LiveTitleMatch.None
            : Single(Query(
                "SELECT id FROM collections WHERE collection_type IN ('Custom', 'Playlist', 'PlaylistFolder', 'Smart') AND display_name = $name AND media_type IS $mediaType",
                ("$name", title.Name),
                ("$mediaType", (object?)title.MediaType ?? DBNull.Value)));
    }

    private LiveTitleMatch ResolvePerson(BackupTitle title)
    {
        if (title.Ids.TryGetValue("wikidata_qid", out var qid) && !string.IsNullOrWhiteSpace(qid))
        {
            return Single(Query("SELECT id FROM persons WHERE wikidata_qid = $qid", ("$qid", qid)));
        }

        return string.IsNullOrEmpty(title.Name)
            ? LiveTitleMatch.None
            : Single(Query("SELECT id FROM persons WHERE name = $name", ("$name", title.Name)));
    }

    private LiveTitleMatch ResolveByQid(string table, BackupTitle title) =>
        title.Ids.TryGetValue("wikidata_qid", out var qid) && !string.IsNullOrWhiteSpace(qid)
            ? Single(Query($"SELECT id FROM {RestoredRowSql.Quote(table)} WHERE wikidata_qid = $qid", ("$qid", qid)))
            : LiveTitleMatch.None;

    /// <summary>A View item is the item whose file has the same fingerprint (the same hash column the backup read it from).</summary>
    private LiveTitleMatch ResolveLocalItem(BackupTitle title)
    {
        var items = new List<byte[]>();
        foreach (var hash in title.FileHashes.Where(hash => !string.IsNullOrWhiteSpace(hash)))
        {
            foreach (var row in Query(
                         "SELECT lif.item_id FROM local_item_files lif JOIN local_files f ON f.id = lif.file_id WHERE f.content_hash = $hash COLLATE NOCASE",
                         ("$hash", hash)))
            {
                var id = (byte[])row[0]!;
                if (!items.Any(known => known.AsSpan().SequenceEqual(id)))
                {
                    items.Add(id);
                }
            }
        }

        return items.Count == 1 ? new LiveTitleMatch(LiveMatchKind.Exact, items[0]) : LiveTitleMatch.None;
    }

    /// <summary>Artwork a provider supplied is the artwork with the same provider and the same source address or provider reference.</summary>
    private LiveTitleMatch ResolveArtwork(BackupTitle title)
    {
        var provider = title.Ids.GetValueOrDefault("source_provider");
        var url = title.Ids.GetValueOrDefault("source_url");
        var reference = title.Ids.GetValueOrDefault("provider_reference");
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(reference))
        {
            return LiveTitleMatch.None;
        }

        return Single(Query(
            """
            SELECT id FROM artwork_assets
            WHERE source_provider IS $provider
              AND (($url IS NOT NULL AND source_url = $url) OR ($reference IS NOT NULL AND provider_reference = $reference))
            """,
            ("$provider", (object?)provider ?? DBNull.Value),
            ("$url", string.IsNullOrWhiteSpace(url) ? DBNull.Value : (object)url),
            ("$reference", string.IsNullOrWhiteSpace(reference) ? DBNull.Value : (object)reference)));
    }

    private static LiveTitleMatch Single(List<object?[]> rows) =>
        rows.Count == 1 && rows[0][0] is byte[] id ? new LiveTitleMatch(LiveMatchKind.Exact, id) : LiveTitleMatch.None;

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Everything that identifies a title, as one string, so two rows that refer to the same title share one lookup.</summary>
    private static string Signature(BackupTitle title)
    {
        static string Map(IReadOnlyDictionary<string, string> map) =>
            string.Join(";", map.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));

        return string.Join(
            "\u001f",
            title.Target,
            Map(title.Ids),
            Map(title.ParentIds),
            title.Ordinal?.ToString(CultureInfo.InvariantCulture),
            title.ParentOrdinal?.ToString(CultureInfo.InvariantCulture),
            title.RunningTimeSeconds?.ToString("R", CultureInfo.InvariantCulture),
            title.Edition,
            string.Join(";", title.FileHashes),
            title.Name,
            title.MediaType);
    }

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
}
