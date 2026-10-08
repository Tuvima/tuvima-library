using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed record VerifiedMusicReleaseTrackPairing(
    string OperationToken, Guid AssetId, Guid ExpectedEditionId,
    Guid ExpectedWorkId, Guid ExpectedAlbumWorkId, Guid ExpectedLibraryId,
    string ExpectedIdentityRevision, string ReleaseId, string ReleaseTrackId);

/// <summary>Atomically persists reviewed exact-release and exact-release-track identity.</summary>
public sealed class MusicPairingCommitRepository(IDatabaseConnection database)
{
    public async Task<MediaEditorPlanCommitResult?> TryReplayAsync(
        IReadOnlyList<VerifiedMusicReleaseTrackPairing> operations, CancellationToken ct = default)
    {
        if (operations.Count < 1)
        {
            return null;
        }
        using (var connection = database.CreateConnection())
        {
            if (connection.ExecuteScalar<int>("""
                    SELECT EXISTS(SELECT 1 FROM media_editor_music_pairing_commits
                      WHERE operation_token=@token);
                    """, new { token = operations[0].OperationToken }) == 0)
            {
                return null;
            }
        }
        return await CommitAsync(operations, ct).ConfigureAwait(false);
    }

    public async Task<MediaEditorPlanCommitResult> CommitAsync(
        IReadOnlyList<VerifiedMusicReleaseTrackPairing> operations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count is < 1 or > 1000)
        {
            throw new ArgumentException("A music pairing plan must contain 1 to 1,000 files.");
        }
        var tokenValue = operations[0].OperationToken;
        if (string.IsNullOrWhiteSpace(tokenValue) || tokenValue.Length > 128
            || operations.Any(row => row.OperationToken != tokenValue))
        {
            throw new ArgumentException("Every pairing must use the same operation token.");
        }
        var ordered = operations.OrderBy(row => row.AssetId).ToArray();
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ordered))));

        return await database.ExecuteWriteAsync((connection, transaction, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var receipt = connection.QuerySingleOrDefault<string>("""
                SELECT request_hash FROM media_editor_music_pairing_commits WHERE operation_token=@tokenValue;
                """, new { tokenValue }, transaction);
            if (receipt is not null)
            {
                return receipt == requestHash
                        ? Result(MediaEditorCommitOutcome.Replayed, ordered)
                        : Conflict(ordered, "This operation token was already used for different changes.");
            }

            // Identity is written to an Edition/Work, so its unselected files must
            // not inherit a correction committed for only one selected asset.
            var reviewedAssetIds = ordered.Select(row => row.AssetId).ToHashSet();
            if (reviewedAssetIds.Count != ordered.Length)
            {
                return Conflict(ordered, "A file was included more than once in this review.");
            }
            var affectedAssetIds = connection.Query<Guid>("""
                SELECT asset.id FROM media_assets asset
                JOIN editions edition ON edition.id=asset.edition_id
                WHERE edition.work_id IN @workIds;
                """, new { workIds = ordered.Select(row => GuidSql.ToBlob(row.ExpectedWorkId)).Distinct().ToArray() }, transaction);
            if (affectedAssetIds.Any(assetId => !reviewedAssetIds.Contains(assetId)))
            {
                return Conflict(ordered, "This track contains an unselected file; its shared identity cannot be changed by this selection.");
            }

            var live = connection.Query<LiveRow>("""
                SELECT asset.id AS AssetId, asset.edition_id AS EditionId,
                       edition.work_id AS WorkId, work.parent_work_id AS AlbumWorkId,
                       asset.library_id AS LibraryId, asset.status AS Status,
                       asset.is_orphaned AS IsOrphaned, work.media_type AS MediaType,
                       work.work_kind AS WorkKind,
                       COALESCE((SELECT value FROM canonical_values WHERE entity_id=work.id AND key='identity_revision' LIMIT 1),'') AS IdentityRevision,
                       (SELECT COUNT(*) FROM editions WHERE work_id=work.id) AS WorkEditionCount,
                       (SELECT value FROM canonical_values WHERE entity_id=edition.id AND key=@releaseKey LIMIT 1) AS CanonicalReleaseId,
                       (SELECT id_value FROM bridge_ids WHERE entity_id=edition.id AND id_type=@releaseKey LIMIT 1) AS BridgeReleaseId,
                       (SELECT value FROM canonical_values WHERE entity_id=work.id AND key=@trackKey LIMIT 1) AS CanonicalTrackId,
                       (SELECT id_value FROM bridge_ids WHERE entity_id=work.id AND id_type=@trackKey LIMIT 1) AS BridgeTrackId
                FROM media_assets asset JOIN editions edition ON edition.id=asset.edition_id
                JOIN works work ON work.id=edition.work_id WHERE asset.id IN @assetIds;
                """, new { assetIds = ordered.Select(row => GuidSql.ToBlob(row.AssetId)).ToArray(),
                    releaseKey = BridgeIdKeys.MusicBrainzReleaseId,
                    trackKey = "musicbrainz_release_track_id" }, transaction).ToDictionary(row => row.AssetId);
            foreach (var row in ordered)
            {
                if (!live.TryGetValue(row.AssetId, out var current)
                    || current.EditionId != row.ExpectedEditionId || current.WorkId != row.ExpectedWorkId
                    || current.AlbumWorkId != row.ExpectedAlbumWorkId
                    || !Guid.TryParse(current.LibraryId, out var library) || library != row.ExpectedLibraryId
                    || current.Status != "Normal" || current.IsOrphaned || current.MediaType != "Music"
                    || current.WorkKind != "child" || current.WorkEditionCount != 1
                    || current.IdentityRevision != row.ExpectedIdentityRevision
                    || Disagrees(current.CanonicalReleaseId, current.BridgeReleaseId)
                    || Disagrees(current.CanonicalTrackId, current.BridgeTrackId)
                    || Conflicts(current.CanonicalReleaseId ?? current.BridgeReleaseId, row.ReleaseId)
                    || Conflicts(current.CanonicalTrackId ?? current.BridgeTrackId, row.ReleaseTrackId))
                {
                    return Conflict(ordered, "The reviewed music identity changed or cannot represent an exact local target.");
                }
            }
            if (ordered.GroupBy(row => row.ExpectedWorkId).Any(group => group.Select(row => row.ReleaseTrackId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                || ordered.GroupBy(row => row.ExpectedEditionId).Any(group => group.Select(row => row.ReleaseId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1))
            {
                return Conflict(ordered, "One local track or Edition was assigned conflicting MusicBrainz targets.");
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            foreach (var row in ordered)
            {
                UpsertCanonical(connection, transaction, row.ExpectedEditionId, BridgeIdKeys.MusicBrainzReleaseId, row.ReleaseId, now);
                UpsertBridge(connection, transaction, row.ExpectedEditionId, BridgeIdKeys.MusicBrainzReleaseId, row.ReleaseId, now);
                UpsertCanonical(connection, transaction, row.ExpectedWorkId, "musicbrainz_release_track_id", row.ReleaseTrackId, now);
                UpsertBridge(connection, transaction, row.ExpectedWorkId, "musicbrainz_release_track_id", row.ReleaseTrackId, now);
            }
            connection.Execute("""
                INSERT INTO media_editor_music_pairing_commits(operation_token,request_hash,committed_at)
                VALUES(@tokenValue,@requestHash,@now);
                """, new { tokenValue, requestHash, now }, transaction);
            connection.Execute("""
                INSERT INTO media_editor_music_pairing_commit_items
                  (operation_token,asset_id,edition_id,work_id,release_id,release_track_id)
                VALUES(@OperationToken,@AssetId,@ExpectedEditionId,@ExpectedWorkId,@ReleaseId,@ReleaseTrackId);
                """, ordered, transaction);
            return Result(MediaEditorCommitOutcome.Committed, ordered);
        }, ct).ConfigureAwait(false);
    }

    private static void UpsertCanonical(System.Data.IDbConnection connection, System.Data.IDbTransaction tx,
        Guid entityId, string key, string value, string now) => connection.Execute("""
        INSERT INTO canonical_values(entity_id,key,value,last_scored_at,is_conflicted,winning_provider_id,needs_review)
        VALUES(@entityId,@key,@value,@now,0,@provider,0)
        ON CONFLICT(entity_id,key) DO UPDATE SET value=excluded.value,last_scored_at=excluded.last_scored_at,
          is_conflicted=0,winning_provider_id=excluded.winning_provider_id,needs_review=0;
        """, new { entityId, key, value, now, provider = WellKnownProviders.MusicBrainz }, tx);

    private static void UpsertBridge(System.Data.IDbConnection connection, System.Data.IDbTransaction tx,
        Guid entityId, string key, string value, string now) => connection.Execute("""
        INSERT INTO bridge_ids(id,entity_id,id_type,id_value,provider_id,created_at)
        VALUES(@id,@entityId,@key,@value,@provider,@now)
        ON CONFLICT(entity_id,id_type) DO UPDATE SET id_value=excluded.id_value,provider_id=excluded.provider_id;
        """, new { id = Guid.NewGuid(), entityId, key, value, now, provider = WellKnownProviders.MusicBrainz }, tx);

    private static bool Disagrees(string? left, string? right) => left is not null && right is not null
        && !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool Conflicts(string? existing, string proposed) => existing is not null
        && !string.Equals(existing, proposed, StringComparison.OrdinalIgnoreCase);
    private static MediaEditorPlanCommitResult Result(MediaEditorCommitOutcome outcome,
        IReadOnlyList<VerifiedMusicReleaseTrackPairing> rows) => new(outcome, rows.Select(row =>
            new MediaEditorCommitResult(outcome, row.AssetId, row.ExpectedWorkId, row.ExpectedWorkId,
                outcome == MediaEditorCommitOutcome.Committed ? "pending" : "pending")).ToArray());
    private static MediaEditorPlanCommitResult Conflict(IReadOnlyList<VerifiedMusicReleaseTrackPairing> rows, string reason) =>
        new(MediaEditorCommitOutcome.Conflict, rows.Select(row => new MediaEditorCommitResult(
            MediaEditorCommitOutcome.Conflict, row.AssetId, row.ExpectedWorkId, row.ExpectedWorkId, null, reason)).ToArray());

    private sealed class LiveRow
    {
        public Guid AssetId { get; set; } public Guid EditionId { get; set; } public Guid WorkId { get; set; }
        public Guid? AlbumWorkId { get; set; } public string? LibraryId { get; set; } public string Status { get; set; } = "";
        public bool IsOrphaned { get; set; } public string MediaType { get; set; } = ""; public string WorkKind { get; set; } = "";
        public string IdentityRevision { get; set; } = ""; public int WorkEditionCount { get; set; }
        public string? CanonicalReleaseId { get; set; } public string? BridgeReleaseId { get; set; }
        public string? CanonicalTrackId { get; set; } public string? BridgeTrackId { get; set; }
    }
}
