using Dapper;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Matching;

/// <summary>Loads only locally owned, normal files and their current identity context.</summary>
public sealed class PairingAssetReadService(IDatabaseConnection db)
{
    public IReadOnlyDictionary<Guid, PairingAssetRow> Load(IReadOnlyList<Guid> assetIds, CancellationToken ct)
    {
        using var connection = db.CreateConnection();
        var found = new Dictionary<Guid, PairingAssetRow>(assetIds.Count);
        foreach (var batch in assetIds.Chunk(400))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var row in connection.Query<PairingAssetRow>(new CommandDefinition("""
            SELECT ma.id AS AssetId,
                   ma.file_path_root AS FilePath,
                   ma.library_id AS LibraryIdValue,
                   w.media_type AS MediaType,
                   w.id AS WorkId,
                   w.work_kind AS WorkKind,
                   e.id AS EditionId,
                   w.parent_work_id AS SeasonWorkId,
                   grandparent.id AS ShowWorkId,
                   CASE WHEN w.media_type = 'Music' THEN COALESCE(parent.id, w.id)
                        ELSE COALESCE(grandparent.id, parent.id, w.id) END AS RootWorkId,
                   (SELECT value FROM canonical_values cv
                    WHERE cv.entity_id = w.id AND cv.key = 'tvdb_episode_id' LIMIT 1) AS TvdbEpisodeId,
                   COALESCE((SELECT value FROM canonical_values cv
                             WHERE cv.entity_id = w.id AND cv.key = 'identity_revision' LIMIT 1), '') AS SourceIdentityRevision,
                   COALESCE((SELECT value FROM canonical_values cv
                             WHERE cv.entity_id = grandparent.id AND cv.key = 'identity_revision' LIMIT 1), '') AS ShowIdentityRevision,
                   (SELECT value FROM canonical_values cv
                    WHERE cv.entity_id = w.id AND cv.key = 'musicbrainz_recording_id' LIMIT 1) AS RecordingId,
                   (SELECT value FROM canonical_values cv
                    WHERE cv.entity_id = COALESCE(grandparent.id, parent.id, w.id)
                      AND cv.key = 'tvdb_id' LIMIT 1) AS TvdbSeriesId,
                   (SELECT id_value FROM bridge_ids bridge
                    WHERE bridge.entity_id = COALESCE(grandparent.id, parent.id, w.id)
                      AND bridge.id_type = 'tvdb_id' LIMIT 1) AS TvdbSeriesBridgeId,
                   (SELECT value FROM canonical_values cv
                    WHERE cv.entity_id = e.id AND cv.key = 'musicbrainz_release_id' LIMIT 1) AS MusicBrainzReleaseId,
                   (SELECT id_value FROM bridge_ids bridge
                    WHERE bridge.entity_id = e.id AND bridge.id_type = 'musicbrainz_release_id' LIMIT 1) AS MusicBrainzReleaseBridgeId,
                   (SELECT value FROM canonical_values cv
                    WHERE cv.entity_id = CASE WHEN w.media_type = 'Music' THEN COALESCE(parent.id, w.id)
                                              ELSE COALESCE(grandparent.id, parent.id, w.id) END
                      AND cv.key = 'musicbrainz_release_id' LIMIT 1) AS AlbumContextReleaseId,
                   (SELECT id_value FROM bridge_ids bridge
                    WHERE bridge.entity_id = CASE WHEN w.media_type = 'Music' THEN COALESCE(parent.id, w.id)
                                                  ELSE COALESCE(grandparent.id, parent.id, w.id) END
                      AND bridge.id_type = 'musicbrainz_release_id' LIMIT 1) AS AlbumContextReleaseBridgeId
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            LEFT JOIN works parent ON parent.id = w.parent_work_id
            LEFT JOIN works grandparent ON grandparent.id = parent.parent_work_id
            WHERE ma.id IN @assetIds AND ma.status = 'Normal' AND ma.is_orphaned = 0
              AND w.ownership = 'Owned';
            """, new { assetIds = batch.Select(GuidSql.ToBlob).ToArray() }, cancellationToken: ct)))
                found[row.AssetId] = row;
        }
        return found;
    }
}

public sealed class PairingAssetRow
{
    public Guid AssetId { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string? LibraryIdValue { get; init; }
    public string MediaType { get; init; } = string.Empty;
    public Guid WorkId { get; init; }
    public string WorkKind { get; init; } = string.Empty;
    public Guid EditionId { get; init; }
    public Guid? SeasonWorkId { get; init; }
    public Guid? ShowWorkId { get; init; }
    public string SourceIdentityRevision { get; init; } = string.Empty;
    public string ShowIdentityRevision { get; init; } = string.Empty;
    public Guid RootWorkId { get; init; }
    public string? TvdbEpisodeId { get; init; }
    public string? RecordingId { get; init; }
    public string? TvdbSeriesId { get; init; }
    public string? TvdbSeriesBridgeId { get; init; }
    public string? MusicBrainzReleaseId { get; init; }
    public string? MusicBrainzReleaseBridgeId { get; init; }
    public string? AlbumContextReleaseId { get; init; }
    public string? AlbumContextReleaseBridgeId { get; init; }
}
