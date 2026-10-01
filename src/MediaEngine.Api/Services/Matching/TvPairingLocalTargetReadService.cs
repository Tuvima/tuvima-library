using Dapper;
using MediaEngine.Domain;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Matching;

/// <summary>Resolves only unambiguous existing local episode Works under one confirmed show.</summary>
public sealed class TvPairingLocalTargetReadService(IDatabaseConnection db)
{
    public (Guid ShowWorkId, IReadOnlyDictionary<string, TvPairingLocalTarget> Targets)?
        ResolveBySeriesId(string tvdbSeriesId,
            IReadOnlyList<PairingCatalogueChild> catalogue, CancellationToken ct)
    {
        using var connection = db.CreateConnection();
        var shows = connection.Query<Guid>(new CommandDefinition("""
            SELECT DISTINCT w.id
            FROM works w
            JOIN bridge_ids identity ON identity.entity_id=w.id
              AND identity.id_type=@seriesKey
            WHERE w.media_type='TV' AND w.work_kind='parent'
              AND w.parent_work_id IS NULL AND identity.id_value=@tvdbSeriesId;
            """, new { tvdbSeriesId, seriesKey = BridgeIdKeys.TvdbId },
            cancellationToken: ct)).ToArray();
        if (shows.Length != 1) return null;
        return (shows[0], Resolve(shows[0], tvdbSeriesId, catalogue, ct));
    }

    public IReadOnlyDictionary<string, TvPairingLocalTarget> Resolve(
        Guid showWorkId, string tvdbSeriesId,
        IReadOnlyList<PairingCatalogueChild> catalogue, CancellationToken ct)
    {
        var byId = catalogue.GroupBy(child => child.ChildId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var found = new List<TvPairingLocalTarget>();
        using var connection = db.CreateConnection();
        foreach (var ids in byId.Keys.Chunk(400))
        {
            ct.ThrowIfCancellationRequested();
            found.AddRange(connection.Query<TvPairingLocalTarget>(new CommandDefinition("""
                SELECT episode.id AS WorkId,
                       season.id AS SeasonWorkId,
                       show.id AS ShowWorkId,
                       episodeIdentity.id_value AS EpisodeId,
                       showIdentity.id_value AS SeriesId,
                       season.ordinal AS SeasonNumber,
                       (SELECT COUNT(*) FROM editions targetEdition
                        JOIN media_assets targetAsset ON targetAsset.edition_id = targetEdition.id
                        WHERE targetEdition.work_id = episode.id) AS ActualAssetCount,
                       COALESCE((SELECT value FROM canonical_values cv
                                 WHERE cv.entity_id = episode.id AND cv.key = @revisionKey), '') AS IdentityRevision,
                       COALESCE((SELECT value FROM canonical_values cv
                                 WHERE cv.entity_id = show.id AND cv.key = @revisionKey), '') AS ShowIdentityRevision,
                       episode.work_kind AS WorkKind
                FROM works episode
                JOIN bridge_ids episodeIdentity ON episodeIdentity.entity_id = episode.id
                  AND episodeIdentity.id_type = @episodeKey
                JOIN works season ON season.id = episode.parent_work_id
                  AND season.media_type = 'TV' AND season.work_kind = 'parent'
                JOIN works show ON show.id = season.parent_work_id
                  AND show.media_type = 'TV' AND show.work_kind = 'parent'
                JOIN bridge_ids showIdentity ON showIdentity.entity_id = show.id
                  AND showIdentity.id_type = @seriesKey
                WHERE show.id = @showWorkId AND showIdentity.id_value = @tvdbSeriesId
                  AND episode.media_type = 'TV' AND episode.work_kind IN ('child', 'catalog')
                  AND episodeIdentity.id_value IN @ids;
                """, new { showWorkId, tvdbSeriesId, ids,
                    revisionKey = MetadataFieldConstants.IdentityRevision,
                    episodeKey = BridgeIdKeys.TvdbEpisodeId,
                    seriesKey = BridgeIdKeys.TvdbId }, cancellationToken: ct)));
        }

        return found.GroupBy(row => row.EpisodeId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .Where(row => byId.TryGetValue(row.EpisodeId, out var child)
                && child.SeasonNumber == row.SeasonNumber)
            .ToDictionary(row => row.EpisodeId, StringComparer.Ordinal);
    }
}

public sealed class TvPairingLocalTarget
{
    public Guid WorkId { get; init; }
    public Guid SeasonWorkId { get; init; }
    public Guid ShowWorkId { get; init; }
    public string EpisodeId { get; init; } = string.Empty;
    public string SeriesId { get; init; } = string.Empty;
    public int? SeasonNumber { get; init; }
    public int ActualAssetCount { get; init; }
    public string IdentityRevision { get; init; } = string.Empty;
    public string ShowIdentityRevision { get; init; } = string.Empty;
    public string WorkKind { get; init; } = string.Empty;
}
