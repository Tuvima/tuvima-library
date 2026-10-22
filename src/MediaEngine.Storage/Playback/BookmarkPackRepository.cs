using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Playback;

/// <summary>What the data store knows about a file that helps a bookmark pack recognise its title (Videos spec D46).</summary>
public sealed record BookmarkPackRecognition(Guid WorkId, string? EditionLabel, IReadOnlyDictionary<string, string> Ids);

/// <summary>
/// Imported bookmark packs (Videos spec D46): read-only pack bookmarks that belong to the profile that imported the pack, on a title.
/// A pack on a file the caller cannot see (a Vault file without an unlocked session) contributes nothing, as for ordinary bookmarks.
/// </summary>
public sealed class BookmarkPackRepository
{
    private readonly IDatabaseConnection _db;

    public BookmarkPackRepository(IDatabaseConnection db)
    {
        _db = db;
    }

    /// <summary>
    /// The canonical IDs a pack may use to recognise the title the file belongs to, plus the edition's label. Only IDs on the
    /// recognition list are returned; internal and person IDs never leave the data store.
    /// </summary>
    public Task<BookmarkPackRecognition?> GetRecognitionAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var asset = conn.QueryFirstOrDefault<AssetRow>(
            """
            SELECT e.work_id AS WorkId, e.id AS EditionId, e.format_label AS EditionLabel, e.wikidata_qid AS EditionQid
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            WHERE ma.id = @assetId;
            """,
            new { assetId });
        if (asset is null)
        {
            return Task.FromResult<BookmarkPackRecognition?>(null);
        }

        var rows = conn.Query<BridgeRow>(
            """
            SELECT id_type AS IdType, id_value AS IdValue
            FROM bridge_ids
            WHERE entity_id IN (@workId, @editionId)
            ORDER BY CASE WHEN entity_id = @workId THEN 0 ELSE 1 END, id_type;
            """,
            new { workId = asset.WorkId, editionId = asset.EditionId });
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (BookmarkPackMatcher.RecognitionIdKeys.Contains(row.IdType) && !string.IsNullOrWhiteSpace(row.IdValue))
            {
                ids.TryAdd(row.IdType.Trim().ToLowerInvariant(), row.IdValue.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(asset.EditionQid))
        {
            ids.TryAdd("wikidata_qid", asset.EditionQid.Trim());
        }

        return Task.FromResult<BookmarkPackRecognition?>(new BookmarkPackRecognition(asset.WorkId, asset.EditionLabel, ids));
    }

    /// <summary>True when the profile already has a pack with the same content on the title.</summary>
    public Task<bool> ExistsAsync(Guid profileId, Guid workId, string contentHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var found = conn.ExecuteScalar<long>(
            "SELECT EXISTS (SELECT 1 FROM bookmark_packs WHERE profile_id = @profileId AND work_id = @workId AND content_hash = @contentHash);",
            new { profileId, workId, contentHash });
        return Task.FromResult(found != 0);
    }

    /// <summary>Adds a pack and its bookmarks. Returns null when the same pack is already on the title for this profile.</summary>
    public Task<BookmarkPackDto?> AddAsync(
        Guid profileId,
        Guid workId,
        Guid assetId,
        string name,
        string? author,
        string contentHash,
        double? sourceRunningTimeSeconds,
        string? sourceEdition,
        bool isAligned,
        IReadOnlyList<BookmarkPackBookmark> bookmarks,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync<BookmarkPackDto?>((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var duplicate = conn.ExecuteScalar<long>(
                "SELECT EXISTS (SELECT 1 FROM bookmark_packs WHERE profile_id = @profileId AND work_id = @workId AND content_hash = @contentHash);",
                new { profileId, workId, contentHash },
                tx);
            if (duplicate != 0)
            {
                return null;
            }

            var packId = Guid.NewGuid();
            var importedAt = DateTimeOffset.UtcNow;
            conn.Execute(
                """
                INSERT INTO bookmark_packs
                    (id, profile_id, work_id, asset_id, name, author, content_hash,
                     source_running_time_seconds, source_edition, is_aligned, is_enabled, imported_at)
                VALUES
                    (@packId, @profileId, @workId, @assetId, @name, @author, @contentHash,
                     @sourceRunningTimeSeconds, @sourceEdition, @isAligned, 1, @importedAt);
                """,
                new
                {
                    packId,
                    profileId,
                    workId,
                    assetId,
                    name,
                    author,
                    contentHash,
                    sourceRunningTimeSeconds,
                    sourceEdition,
                    isAligned = isAligned ? 1 : 0,
                    importedAt,
                },
                tx);

            var items = new List<PackBookmarkDto>(bookmarks.Count);
            foreach (var bookmark in bookmarks.OrderBy(item => item.Time))
            {
                var item = new PackBookmarkDto
                {
                    Id = Guid.NewGuid(),
                    PositionSeconds = bookmark.Time,
                    EndSeconds = bookmark.EndTime,
                    ChapterTitle = bookmark.Chapter,
                    Note = bookmark.Note,
                    Description = bookmark.Description,
                };
                conn.Execute(
                    """
                    INSERT INTO bookmark_pack_items (id, pack_id, position_seconds, end_seconds, chapter_title, note, description)
                    VALUES (@Id, @packId, @PositionSeconds, @EndSeconds, @ChapterTitle, @Note, @Description);
                    """,
                    new { item.Id, packId, item.PositionSeconds, item.EndSeconds, item.ChapterTitle, item.Note, item.Description },
                    tx);
                items.Add(item);
            }

            return new BookmarkPackDto
            {
                Id = packId,
                WorkId = workId,
                Name = name,
                Author = author,
                IsEnabled = true,
                IsAligned = isAligned,
                ImportedAt = importedAt,
                Bookmarks = items,
            };
        }, ct);
    }

    /// <summary>The profile's own packs on the title, newest first, limited to the files the caller may see (null means no limit).</summary>
    public Task<IReadOnlyList<BookmarkPackDto>> GetByWorkAsync(
        Guid profileId,
        Guid workId,
        CancellationToken ct = default,
        IReadOnlySet<Guid>? authorizedAssetIds = null)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var packs = conn.Query<PackRow>(
            """
            SELECT id AS Id, work_id AS WorkId, name AS Name, author AS Author, is_aligned AS IsAligned,
                   is_enabled AS IsEnabled, imported_at AS ImportedAt
            FROM bookmark_packs
            WHERE profile_id = @profileId
              AND work_id = @workId
              AND (@unrestricted = 1 OR asset_id IN @allowedAssets)
            ORDER BY imported_at DESC;
            """,
            new
            {
                profileId,
                workId,
                unrestricted = authorizedAssetIds is null ? 1 : 0,
                allowedAssets = (authorizedAssetIds ?? new HashSet<Guid>()).Select(GuidSql.ToBlob).ToArray(),
            }).ToList();
        if (packs.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<BookmarkPackDto>>([]);
        }

        var items = conn.Query<ItemRow>(
            """
            SELECT pack_id AS PackId, id AS Id, position_seconds AS PositionSeconds, end_seconds AS EndSeconds,
                   chapter_title AS ChapterTitle, note AS Note, description AS Description
            FROM bookmark_pack_items
            WHERE pack_id IN @packIds
            ORDER BY position_seconds;
            """,
            new { packIds = packs.Select(pack => GuidSql.ToBlob(pack.Id)).ToArray() })
            .GroupBy(item => item.PackId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<PackBookmarkDto>)group.Select(item => new PackBookmarkDto
            {
                Id = item.Id,
                PositionSeconds = item.PositionSeconds,
                EndSeconds = item.EndSeconds,
                ChapterTitle = item.ChapterTitle,
                Note = item.Note,
                Description = item.Description,
            }).ToList());

        IReadOnlyList<BookmarkPackDto> result = packs.Select(pack => new BookmarkPackDto
        {
            Id = pack.Id,
            WorkId = pack.WorkId,
            Name = pack.Name,
            Author = pack.Author,
            IsAligned = pack.IsAligned,
            IsEnabled = pack.IsEnabled,
            ImportedAt = pack.ImportedAt,
            Bookmarks = items.TryGetValue(pack.Id, out var list) ? list : [],
        }).ToList();
        return Task.FromResult(result);
    }

    /// <summary>Switches one of the profile's packs on or off. Anyone else's pack is left alone and reported as not found.</summary>
    public Task<bool> SetEnabledAsync(
        Guid profileId, Guid packId, bool isEnabled, CancellationToken ct = default, IReadOnlySet<Guid>? authorizedAssetIds = null)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var affected = conn.Execute(
                """
                UPDATE bookmark_packs SET is_enabled = @isEnabled
                WHERE id = @packId AND profile_id = @profileId
                  AND (@unrestricted = 1 OR asset_id IN @allowedAssets);
                """,
                new
                {
                    packId,
                    profileId,
                    isEnabled = isEnabled ? 1 : 0,
                    unrestricted = authorizedAssetIds is null ? 1 : 0,
                    allowedAssets = (authorizedAssetIds ?? new HashSet<Guid>()).Select(GuidSql.ToBlob).ToArray(),
                },
                tx);
            return affected > 0;
        }, ct);
    }

    /// <summary>Removes one of the profile's packs with its bookmarks. Anyone else's pack is left alone and reported as not found.</summary>
    public Task<bool> DeleteAsync(
        Guid profileId, Guid packId, CancellationToken ct = default, IReadOnlySet<Guid>? authorizedAssetIds = null)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var affected = conn.Execute(
                """
                DELETE FROM bookmark_packs
                WHERE id = @packId AND profile_id = @profileId
                  AND (@unrestricted = 1 OR asset_id IN @allowedAssets);
                """,
                new
                {
                    packId,
                    profileId,
                    unrestricted = authorizedAssetIds is null ? 1 : 0,
                    allowedAssets = (authorizedAssetIds ?? new HashSet<Guid>()).Select(GuidSql.ToBlob).ToArray(),
                },
                tx);
            return affected > 0;
        }, ct);
    }

    private sealed record AssetRow
    {
        public Guid WorkId { get; init; }
        public Guid EditionId { get; init; }
        public string? EditionLabel { get; init; }
        public string? EditionQid { get; init; }
    }

    private sealed record BridgeRow
    {
        public string IdType { get; init; } = string.Empty;
        public string IdValue { get; init; } = string.Empty;
    }

    private sealed record PackRow
    {
        public Guid Id { get; init; }
        public Guid WorkId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Author { get; init; }
        public bool IsAligned { get; init; }
        public bool IsEnabled { get; init; }
        public DateTimeOffset ImportedAt { get; init; }
    }

    private sealed record ItemRow
    {
        public Guid PackId { get; init; }
        public Guid Id { get; init; }
        public double PositionSeconds { get; init; }
        public double? EndSeconds { get; init; }
        public string? ChapterTitle { get; init; }
        public string? Note { get; init; }
        public string? Description { get; init; }
    }
}
