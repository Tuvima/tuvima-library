using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IVaultBrowseRepository"/> (Videos spec D39, D41): the Vault's own lists of scenes, folders,
/// performers, studios and tags. It reads only files that sit in a Vault library and the Vault's own name and artwork tables, so
/// nothing from here can join a shared People, Tag or Series record. Performers, studios and tags are counted through the scenes the
/// person actually owns (not orphaned, not a derived copy), so a name with no scene left is never listed.
/// </summary>
public sealed class VaultBrowseRepository(IDatabaseConnection database, IConfigSecretsStore secrets) : IVaultBrowseRepository
{
    private const int MaxTake = 200;
    private const int MaxStillsPerScene = 4;
    private const int MaxTagsPerScene = 12;
    private const char TagSeparator = '\u001f';
    private const string FolderKeyOwner = "vault";
    private const string FolderKeyName = "folder_id_key";

    private readonly Lazy<byte[]> _folderKey = new(() => LoadOrCreateFolderKey(secrets), LazyThreadSafetyMode.ExecutionAndPublication);

    // One row per owned Vault scene. The picture is the earliest stash-box cover, else the earliest saved still frame, so what a card
    // shows never changes between visits (D41). Date added is when the Vault activity list first saw the file.
    private const string SceneInner = """
        SELECT ma.id AS AssetId, e.work_id AS WorkId, ma.file_path_root AS FilePath,
               COALESCE(NULLIF((SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'title'), ''),
                        NULLIF((SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = e.work_id AND cv.key = 'title'), '')) AS StoredTitle,
               COALESCE((SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'description'), '') AS Description,
               (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'original_release_date') AS ReleaseDate,
               CASE WHEN vm.asset_id IS NULL THEN 0 ELSE 1 END AS Matched,
               (SELECT c.duration_secs FROM playback_inspection_cache c WHERE c.asset_id = ma.id AND c.source_hash = ma.content_hash LIMIT 1) AS DurationSeconds,
               (SELECT c.file_size FROM playback_inspection_cache c WHERE c.asset_id = ma.id AND c.source_hash = ma.content_hash LIMIT 1) AS SizeBytes,
               (SELECT MIN(a.created_at) FROM vault_activity a WHERE a.asset_id = ma.id AND a.kind = 'added') AS AddedAt,
               (SELECT s.name FROM vault_asset_studios l JOIN vault_studios s ON s.id = l.studio_id WHERE l.asset_id = ma.id ORDER BY s.name LIMIT 1) AS Studio,
               (SELECT group_concat(name, char(31)) FROM
                   (SELECT t.name AS name FROM vault_asset_tags l JOIN vault_tags t ON t.id = l.tag_id WHERE l.asset_id = ma.id ORDER BY t.name LIMIT 12)) AS Tags,
               COALESCE(
                   (SELECT va.id FROM vault_artwork va WHERE va.owner_kind = 'asset' AND va.owner_id = ma.id AND va.artwork_kind = 'cover' ORDER BY va.created_at, va.id LIMIT 1),
                   (SELECT va.id FROM vault_artwork va WHERE va.owner_kind = 'asset' AND va.owner_id = ma.id AND va.artwork_kind = 'still' ORDER BY va.created_at, va.id LIMIT 1)) AS ImageArtworkId
        FROM media_assets ma
        JOIN editions e ON e.id = ma.edition_id
        JOIN vault_libraries vl ON vl.library_id = ma.library_id COLLATE NOCASE
        LEFT JOIN vault_matches vm ON vm.asset_id = ma.id
        WHERE ma.is_orphaned = 0 AND ma.derived_from_asset_id IS NULL
        """;

    public Task<VaultSceneList> ListScenesAsync(VaultSceneQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var match = VaultSceneMatchFilter.IsValid(query.Match) ? query.Match : VaultSceneMatchFilter.All;
        var sort = VaultSceneSorts.IsValid(query.Sort) ? query.Sort : VaultSceneSorts.DateAdded;
        var take = Math.Clamp(query.Take, 1, MaxTake);
        var skip = Math.Max(0, query.Skip);

        var filters = new StringBuilder();
        if (match == VaultSceneMatchFilter.Matched)
        {
            filters.Append(" AND vm.asset_id IS NOT NULL");
        }
        else if (match == VaultSceneMatchFilter.Unmatched)
        {
            filters.Append(" AND vm.asset_id IS NULL");
        }

        if (query.PerformerId is not null)
        {
            filters.Append(" AND EXISTS (SELECT 1 FROM vault_asset_performers x WHERE x.asset_id = ma.id AND x.performer_id = @performerId)");
        }

        if (query.StudioId is not null)
        {
            filters.Append(" AND EXISTS (SELECT 1 FROM vault_asset_studios x WHERE x.asset_id = ma.id AND x.studio_id = @studioId)");
        }

        if (query.TagId is not null)
        {
            filters.Append(" AND EXISTS (SELECT 1 FROM vault_asset_tags x WHERE x.asset_id = ma.id AND x.tag_id = @tagId)");
        }

        var like = string.IsNullOrWhiteSpace(query.Text) ? null : "%" + EscapeLike(query.Text.Trim()) + "%";
        var outer = new StringBuilder("SELECT * FROM (").Append(SceneInner).Append(filters).Append(") scenes");
        if (like is not null)
        {
            outer.Append(
                """
                 WHERE (StoredTitle LIKE @like ESCAPE '\' OR FilePath LIKE @like ESCAPE '\' OR Description LIKE @like ESCAPE '\'
                    OR EXISTS (SELECT 1 FROM vault_asset_performers l JOIN vault_performers p ON p.id = l.performer_id WHERE l.asset_id = scenes.AssetId AND p.name LIKE @like ESCAPE '\')
                    OR EXISTS (SELECT 1 FROM vault_asset_studios l JOIN vault_studios s ON s.id = l.studio_id WHERE l.asset_id = scenes.AssetId AND s.name LIKE @like ESCAPE '\')
                    OR EXISTS (SELECT 1 FROM vault_asset_tags l JOIN vault_tags t ON t.id = l.tag_id WHERE l.asset_id = scenes.AssetId AND t.name LIKE @like ESCAPE '\'))
                """);
        }

        outer.Append(" ORDER BY ").Append(OrderBy(sort, query.Descending)).Append(", AssetId;");

        using var connection = database.CreateConnection();
        var rows = connection.Query<SceneRow>(
            outer.ToString(),
            new { performerId = query.PerformerId, studioId = query.StudioId, tagId = query.TagId, like }).ToList();

        var scenes = rows.Select(Map).ToList();
        if (!string.IsNullOrEmpty(query.FolderId))
        {
            scenes = scenes.Where(s => string.Equals(s.FolderId, query.FolderId, StringComparison.Ordinal)).ToList();
        }

        var page = scenes.Skip(skip).Take(take).ToList();
        page = WithStills(connection, page);
        return Task.FromResult(new VaultSceneList(page, scenes.Count, Heading(connection, query)));
    }

    public Task<VaultSceneDetailRow?> GetSceneAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<SceneRow>(
            "SELECT * FROM (" + SceneInner + ") scenes WHERE AssetId = @assetId;", new { assetId });
        if (row is null)
        {
            return Task.FromResult<VaultSceneDetailRow?>(null);
        }

        var scene = WithStills(connection, [Map(row)])[0];
        var matchedBy = connection.QueryFirstOrDefault<string?>("SELECT matched_by FROM vault_matches WHERE asset_id = @assetId;", new { assetId });
        var performers = Linked(connection,
            "SELECT p.id AS Id, p.name AS Name FROM vault_asset_performers l JOIN vault_performers p ON p.id = l.performer_id WHERE l.asset_id = @assetId ORDER BY p.name;", assetId);
        var studios = Linked(connection,
            "SELECT s.id AS Id, s.name AS Name FROM vault_asset_studios l JOIN vault_studios s ON s.id = l.studio_id WHERE l.asset_id = @assetId ORDER BY s.name;", assetId);
        var tags = Linked(connection,
            "SELECT t.id AS Id, t.name AS Name FROM vault_asset_tags l JOIN vault_tags t ON t.id = l.tag_id WHERE l.asset_id = @assetId ORDER BY t.name;", assetId);
        var description = string.IsNullOrWhiteSpace(row.Description) ? null : row.Description;
        return Task.FromResult<VaultSceneDetailRow?>(new VaultSceneDetailRow(scene, description, matchedBy, performers, studios, tags));
    }

    public Task<IReadOnlyList<VaultFolderRow>> ListFoldersAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var scenes = connection.Query<SceneRow>("SELECT * FROM (" + SceneInner + ") scenes ORDER BY AddedAt, AssetId;").Select(Map).ToList();
        var folders = scenes
            .GroupBy(s => s.FolderId, StringComparer.Ordinal)
            .Select(g => new VaultFolderRow(
                g.Key,
                g.First().FolderName,
                g.Count(),
                g.Select(s => s.ImageArtworkId).FirstOrDefault(id => id is not null)))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<VaultFolderRow>>(folders);
    }

    public Task<IReadOnlyList<VaultNameTileRow>> ListPerformersAsync(string sort, bool descending, CancellationToken ct = default) =>
        Tiles("vault_performers", "vault_asset_performers", "performer_id", "performer", "portrait", null, sort, descending, ct);

    public Task<IReadOnlyList<VaultNameTileRow>> ListStudiosAsync(string sort, bool descending, CancellationToken ct = default) =>
        Tiles("vault_studios", "vault_asset_studios", "studio_id", "studio", "logo", null, sort, descending, ct);

    public Task<IReadOnlyList<VaultNameTileRow>> ListTagsAsync(string sort, bool descending, CancellationToken ct = default) =>
        Tiles("vault_tags", "vault_asset_tags", "tag_id", null, null, null, sort, descending, ct);

    public Task<VaultNameSearchResult> SearchNamesAsync(string text, int take, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var like = "%" + EscapeLike(text.Trim()) + "%";
        var limit = Math.Clamp(take, 1, MaxTake);
        var performers = TilesSync("vault_performers", "vault_asset_performers", "performer_id", "performer", "portrait", like, VaultNameSorts.Name, false).Take(limit).ToList();
        var studios = TilesSync("vault_studios", "vault_asset_studios", "studio_id", "studio", "logo", like, VaultNameSorts.Name, false).Take(limit).ToList();
        return Task.FromResult(new VaultNameSearchResult(performers, studios));
    }

    private Task<IReadOnlyList<VaultNameTileRow>> Tiles(
        string nameTable, string linkTable, string linkColumn, string? ownerKind, string? artworkKind, string? like, string sort, bool descending, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(TilesSync(nameTable, linkTable, linkColumn, ownerKind, artworkKind, like, sort, descending));
    }

    // Table and column names come from the callers above, never from a request. A name is listed only through a link to an owned scene.
    private IReadOnlyList<VaultNameTileRow> TilesSync(
        string nameTable, string linkTable, string linkColumn, string? ownerKind, string? artworkKind, string? like, string sort, bool descending)
    {
        var image = ownerKind is null
            ? "NULL"
            : $"(SELECT va.id FROM vault_artwork va WHERE va.owner_kind = '{ownerKind}' AND va.owner_id = n.id AND va.artwork_kind = '{artworkKind}' ORDER BY va.created_at, va.id LIMIT 1)";
        var direction = descending ? "DESC" : "ASC";
        var order = sort == VaultNameSorts.SceneCount
            ? $"SceneCount {direction}, n.name COLLATE NOCASE ASC"
            : $"n.name COLLATE NOCASE {direction}";
        var sql = $"""
            SELECT n.id AS Id, n.name AS Name, COUNT(DISTINCT l.asset_id) AS SceneCount, {image} AS ImageArtworkId
            FROM {nameTable} n
            JOIN {linkTable} l ON l.{linkColumn} = n.id
            JOIN media_assets ma ON ma.id = l.asset_id AND ma.is_orphaned = 0 AND ma.derived_from_asset_id IS NULL
            JOIN vault_libraries vl ON vl.library_id = ma.library_id COLLATE NOCASE
            WHERE (@like IS NULL OR n.name LIKE @like ESCAPE '\')
            GROUP BY n.id, n.name
            ORDER BY {order}, n.id;
            """;
        using var connection = database.CreateConnection();
        return connection.Query<TileRow>(sql, new { like }).Select(r => new VaultNameTileRow(r.Id, r.Name, r.SceneCount, r.ImageArtworkId)).ToList();
    }

    private static string? Heading(Microsoft.Data.Sqlite.SqliteConnection connection, VaultSceneQuery query)
    {
        if (query.PerformerId is { } performer)
        {
            return connection.QueryFirstOrDefault<string?>("SELECT name FROM vault_performers WHERE id = @performer;", new { performer });
        }

        if (query.StudioId is { } studio)
        {
            return connection.QueryFirstOrDefault<string?>("SELECT name FROM vault_studios WHERE id = @studio;", new { studio });
        }

        if (query.TagId is { } tag)
        {
            return connection.QueryFirstOrDefault<string?>("SELECT name FROM vault_tags WHERE id = @tag;", new { tag });
        }

        return null;
    }

    private static List<VaultLinkedName> Linked(Microsoft.Data.Sqlite.SqliteConnection connection, string sql, Guid assetId) =>
        connection.Query<LinkedRow>(sql, new { assetId }).Select(r => new VaultLinkedName(r.Id, r.Name)).ToList();

    // Up to four saved stills per scene, in the order they were saved, for the optional hover cycling. Nothing is generated here.
    private static List<VaultSceneRow> WithStills(Microsoft.Data.Sqlite.SqliteConnection connection, List<VaultSceneRow> page)
    {
        if (page.Count == 0)
        {
            return page;
        }

        var ids = page.Select(s => s.AssetId).ToList();
        var stills = connection.Query<StillRow>(
            """
            SELECT owner_id AS OwnerId, id AS Id FROM vault_artwork
            WHERE owner_kind = 'asset' AND artwork_kind = 'still' AND owner_id IN @ids
            ORDER BY created_at, id;
            """,
            new { ids = ids.Select(GuidSql.ToBlob).ToArray() }).GroupBy(r => r.OwnerId).ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(r => r.Id).Take(MaxStillsPerScene).ToList());
        return page.Select(s => stills.TryGetValue(s.AssetId, out var own) ? s with { StillArtworkIds = own } : s).ToList();
    }

    private static string OrderBy(string sort, bool descending)
    {
        var direction = descending ? "DESC" : "ASC";
        return sort switch
        {
            VaultSceneSorts.Title => $"COALESCE(StoredTitle, FilePath) COLLATE NOCASE {direction}",
            VaultSceneSorts.ReleaseDate => $"(ReleaseDate IS NULL), ReleaseDate {direction}",
            VaultSceneSorts.Duration => $"(DurationSeconds IS NULL), DurationSeconds {direction}",
            _ => $"(AddedAt IS NULL), AddedAt {direction}",
        };
    }

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private VaultSceneRow Map(SceneRow r)
    {
        var (folderName, folderId) = Folder(r.FilePath);
        var fileName = LastSegment(r.FilePath);
        return new VaultSceneRow(
            r.AssetId,
            r.WorkId,
            string.IsNullOrWhiteSpace(r.StoredTitle) ? TitleFromFileName(fileName) : r.StoredTitle.Trim(),
            r.Matched != 0,
            fileName,
            folderId,
            folderName,
            r.DurationSeconds,
            r.SizeBytes,
            DateTimeOffset.TryParse(r.AddedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var added) ? added : null,
            string.IsNullOrWhiteSpace(r.ReleaseDate) ? null : r.ReleaseDate,
            r.Studio,
            string.IsNullOrEmpty(r.Tags) ? [] : r.Tags.Split(TagSeparator, StringSplitOptions.RemoveEmptyEntries).Take(MaxTagsPerScene).ToList(),
            r.ImageArtworkId,
            []);
    }

    /// <summary>The name a video shows when nothing better is known: the file name without its extension, with dots and underscores read as spaces (D38).</summary>
    internal static string TitleFromFileName(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        var stem = dot > 0 ? fileName[..dot] : fileName;
        var spaced = stem.Replace('.', ' ').Replace('_', ' ').Trim();
        return spaced.Length == 0 ? "Untitled" : spaced;
    }

    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }

    // A folder is named by its own name and identified by a hash of its path, so a folder id never carries a path.
    private (string Name, string Id) Folder(string filePath)
    {
        var trimmed = filePath.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        var directory = cut >= 0 ? trimmed[..cut] : string.Empty;
        var name = directory.Length == 0 ? "Vault" : LastSegment(directory);
        var normal = directory.Replace('\\', '/').ToLowerInvariant();
        // Keyed with a secret that belongs to this install, so a folder id cannot be checked against a guessed path (security review L8).
        var id = Convert.ToHexString(HMACSHA256.HashData(_folderKey.Value, Encoding.UTF8.GetBytes(normal)))[..16].ToLowerInvariant();
        return (name, id);
    }

    private static byte[] LoadOrCreateFolderKey(IConfigSecretsStore secrets)
    {
        var owner = SecretOwner.Provider(FolderKeyOwner);
        // The key sits in the secrets folder (not the data store), so ids stay the same across a data store reset and never travel in a backup.
        // Loaded once, at first use, by the one thread the lazy field allows.
        var stored = secrets.GetAsync(owner, FolderKeyName).GetAwaiter().GetResult();
        if (!string.IsNullOrEmpty(stored))
        {
            try
            {
                var bytes = Convert.FromBase64String(stored);
                if (bytes.Length >= 32)
                {
                    return bytes;
                }
            }
            catch (FormatException)
            {
                // A damaged value is replaced below; ids are only names for this install's folders, so a new key is safe.
            }
        }

        var created = RandomNumberGenerator.GetBytes(32);
        secrets.SetAsync(owner, new Dictionary<string, string> { [FolderKeyName] = Convert.ToBase64String(created) }).GetAwaiter().GetResult();
        return created;
    }

    private sealed class SceneRow
    {
        public Guid AssetId { get; set; }
        public Guid WorkId { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string? StoredTitle { get; set; }
        public string? Description { get; set; }
        public string? ReleaseDate { get; set; }
        public int Matched { get; set; }
        public double? DurationSeconds { get; set; }
        public long? SizeBytes { get; set; }
        public string? AddedAt { get; set; }
        public string? Studio { get; set; }
        public string? Tags { get; set; }
        public Guid? ImageArtworkId { get; set; }
    }

    private sealed class TileRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int SceneCount { get; set; }
        public Guid? ImageArtworkId { get; set; }
    }

    private sealed class LinkedRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class StillRow
    {
        public Guid OwnerId { get; set; }
        public Guid Id { get; set; }
    }
}
