using Dapper;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Matching;

/// <summary>Reads server-owned artwork facts used by a reviewed episode-still choice.</summary>
public sealed class EpisodeStillReviewReadService(IDatabaseConnection db)
{
    public IReadOnlyList<Guid> GetManagedArtworkLinkIds(Guid artworkAssetId, CancellationToken ct)
    {
        using var connection = db.CreateConnection();
        return connection.Query<Guid>(new CommandDefinition("""
            SELECT id FROM entity_artwork_links WHERE artwork_asset_id=@artworkAssetId
            ORDER BY id LIMIT 1000;
            """, new { artworkAssetId }, cancellationToken: ct)).ToArray();
    }

    public ManagedArtworkVariant? LoadManagedVariant(Guid artworkAssetId)
    {
        using var connection = db.CreateConnection();
        return connection.QuerySingleOrDefault<ManagedArtworkVariant>("""
            SELECT content_hash AS ContentHash, original_path AS OriginalPath
            FROM artwork_assets WHERE id=@artworkAssetId;
            """, new { artworkAssetId });
    }

    public bool HasCurrentOwnedAsset(Guid ownerWorkId, string scope)
    {
        if (scope is not ("TvShow" or "TvSeason"))
        {
            return false;
        }
        using var connection = db.CreateConnection();
        return connection.ExecuteScalar<int>("""
            SELECT EXISTS(
                SELECT 1 FROM works w JOIN editions e ON e.work_id=w.id
                JOIN media_assets a ON a.edition_id=e.id
                WHERE (@scope='TvShow' AND w.parent_work_id IN
                    (SELECT id FROM works WHERE parent_work_id=@ownerWorkId))
                   OR (@scope='TvSeason' AND w.parent_work_id=@ownerWorkId)
            );
            """, new { ownerWorkId, scope }) != 0;
    }

    public IReadOnlyList<VerifiedArtworkAssetLibrary>? ReadPostMoveImpact(
        Guid ownerWorkId, IReadOnlyList<(PairingAssetRow Source, TvPairingLocalTarget Target)> chosen)
    {
        using var connection = db.CreateConnection();
        var existing = connection.Query<ArtworkImpactRow>("""
            SELECT a.id AS AssetId, a.library_id AS LibraryId,
                   a.status AS Status, a.is_orphaned AS IsOrphaned
            FROM editions e JOIN media_assets a ON a.edition_id=e.id
            WHERE e.work_id=@ownerWorkId;
            """, new { ownerWorkId });
        var movedOut = chosen.Where(item => item.Source.WorkId == ownerWorkId)
            .Select(item => item.Source.AssetId).ToHashSet();
        var affected = new Dictionary<Guid, Guid>();
        foreach (var row in existing.Where(item => !movedOut.Contains(item.AssetId)))
        {
            if (row.Status != "Normal" || row.IsOrphaned
                || !Guid.TryParse(row.LibraryId, out var libraryId) || libraryId == Guid.Empty)
            {
                return null;
            }
            affected[row.AssetId] = libraryId;
        }
        foreach (var item in chosen.Where(item => item.Target.WorkId == ownerWorkId))
        {
            if (!Guid.TryParse(item.Source.LibraryIdValue, out var libraryId) || libraryId == Guid.Empty)
            {
                return null;
            }
            affected[item.Source.AssetId] = libraryId;
        }
        return affected.OrderBy(item => item.Key)
            .Select(item => new VerifiedArtworkAssetLibrary(item.Key, item.Value)).ToArray();
    }

    /// <summary>
    /// Calculates the complete show/season file impact after the reviewed TV
    /// moves. Structural owner files and unsafe descendants fail closed.
    /// Storage repeats this check inside the write transaction.
    /// </summary>
    public IReadOnlyList<VerifiedArtworkAssetLibrary>? ReadPostMoveSharedImpact(
        Guid ownerWorkId, string scope,
        IReadOnlyList<(PairingAssetRow Source, TvPairingLocalTarget Target)> chosen)
    {
        if (scope is not ("TvShow" or "TvSeason"))
        {
            return null;
        }
        using var connection = db.CreateConnection();
        var existing = connection.Query<SharedImpactRow>("""
            SELECT a.id AS AssetId, a.library_id AS LibraryId,
                   a.status AS Status, a.is_orphaned AS IsOrphaned,
                   w.id AS WorkId, w.parent_work_id AS ParentWorkId,
                   w.media_type AS MediaType, w.work_kind AS WorkKind,
                   CASE WHEN w.id=@ownerWorkId THEN 0
                        WHEN w.parent_work_id=@ownerWorkId THEN 1 ELSE 2 END AS Depth
            FROM works w JOIN editions e ON e.work_id=w.id
            JOIN media_assets a ON a.edition_id=e.id
            WHERE w.id=@ownerWorkId
               OR w.parent_work_id=@ownerWorkId
               OR (@includeGrandchildren=1 AND w.parent_work_id IN
                   (SELECT id FROM works WHERE parent_work_id=@ownerWorkId));
            """, new { ownerWorkId, includeGrandchildren = scope == "TvShow" ? 1 : 0 })
            .ToArray();
        var result = new Dictionary<Guid, Guid>();
        foreach (var row in existing)
        {
            if (row.Depth != (scope == "TvShow" ? 2 : 1)
                || row.MediaType != "TV" || row.WorkKind != "child"
                || row.Status != "Normal" || row.IsOrphaned
                || !Guid.TryParse(row.LibraryId, out var libraryId)
                || libraryId == Guid.Empty)
            {
                return null;
            }
            result[row.AssetId] = libraryId;
        }
        foreach (var item in chosen)
        {
            var sourceInScope = scope == "TvShow"
                ? item.Source.ShowWorkId == ownerWorkId
                : item.Source.SeasonWorkId == ownerWorkId;
            var targetInScope = scope == "TvShow"
                ? item.Target.ShowWorkId == ownerWorkId
                : item.Target.SeasonWorkId == ownerWorkId;
            if (sourceInScope)
            {
                result.Remove(item.Source.AssetId);
            }
            if (targetInScope)
            {
                if (!Guid.TryParse(item.Source.LibraryIdValue, out var libraryId)
                    || libraryId == Guid.Empty)
                {
                    return null;
                }
                result[item.Source.AssetId] = libraryId;
            }
        }
        return result.OrderBy(item => item.Key)
            .Select(item => new VerifiedArtworkAssetLibrary(item.Key, item.Value)).ToArray();
    }

    public sealed class ManagedArtworkVariant
    {
        public string ContentHash { get; set; } = string.Empty;
        public string? OriginalPath { get; set; }
    }

    private class ArtworkImpactRow
    {
        public Guid AssetId { get; set; }
        public string? LibraryId { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool IsOrphaned { get; set; }
    }

    private sealed class SharedImpactRow : ArtworkImpactRow
    {
        public Guid WorkId { get; set; }
        public Guid? ParentWorkId { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string WorkKind { get; set; } = string.Empty;
        public int Depth { get; set; }
    }
}
