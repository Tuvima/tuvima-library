using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Transactional storage boundary for a TV episode target that the Engine has
/// already resolved and reviewed. This is intentionally not an HTTP contract:
/// provider lookup and catalogue validation belong before this write lock.
/// </summary>
public sealed class MediaEditorCommitRepository(IDatabaseConnection database)
{
    /// <summary>
    /// Saves a bounded, server-verified TV plan as one unit. Every row is checked
    /// before any row is moved. A shared Edition can move only when all its assets
    /// are selected for the same target; otherwise its identity stays intact.
    /// </summary>
    public Task<MediaEditorPlanCommitResult> CommitVerifiedTvEpisodePlanAsync(
        IReadOnlyList<VerifiedTvEpisodeMove> operations,
        CancellationToken ct = default) =>
        CommitVerifiedTvEpisodePlanAsync(operations, null, ct);

    /// <summary>
    /// An optional, already-reviewed episode still is committed with the moves.
    /// The caller must authorize the target owner and validate the image before
    /// constructing this internal plan; this repository rechecks durable facts.
    /// </summary>
    public Task<MediaEditorPlanCommitResult> CommitVerifiedTvEpisodePlanAsync(
        IReadOnlyList<VerifiedTvEpisodeMove> operations,
        VerifiedEpisodeStillAssignment? episodeStill,
        CancellationToken ct = default) =>
        CommitVerifiedTvEpisodePlanAsync(operations, episodeStill, null, ct);

    /// <summary>
    /// Reads per-file identity revisions for a server-owned artwork review.
    /// The caller must separately authorize every returned asset and owner.
    /// </summary>
    public Task<IReadOnlyDictionary<Guid, string>> GetTvArtworkAssetRevisionsAsync(
        IReadOnlyList<Guid> assetIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult(MediaEditorTvArtworkIdentityRevision.Read(connection, null, assetIds));
    }

    /// <summary>Opaque preference revision for a reviewed TV show or season.</summary>
    public Task<string?> GetTvPreferredArtworkOwnerRevisionAsync(Guid ownerWorkId,
        string scope, string role, CancellationToken ct = default)
    {
        if (scope is not ("TvShow" or "TvSeason"))
        {
            return Task.FromResult<string?>(null);
        }
        return new MediaEditorPreferredArtworkRepository(database)
            .GetOwnerRevisionAsync(ownerWorkId, scope, role, ct);
    }

    /// <summary>
    /// Probes an existing receipt for an already-authorized frozen plan. Null
    /// means no receipt; a reused token with changed content returns Conflict.
    /// The normal commit path performs the exact hash and receipt check again.
    /// </summary>
    public async Task<MediaEditorPlanCommitResult?> TryReplayVerifiedTvEpisodePlanAsync(
        IReadOnlyList<VerifiedTvEpisodeMove> operations,
        VerifiedEpisodeStillAssignment? episodeStill,
        VerifiedTvPreferredArtworkAssignment? sharedArtwork,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count is < 1 or > 1000
            || string.IsNullOrWhiteSpace(operations[0].OperationToken)
            || operations.Any(row => row.OperationToken != operations[0].OperationToken))
        {
            throw new ArgumentException("A replay probe requires one valid reviewed operation token.",
                    nameof(operations));
        }
        ct.ThrowIfCancellationRequested();
        using (var connection = database.CreateConnection())
        {
            if (connection.ExecuteScalar<int>("""
                    SELECT EXISTS(SELECT 1 FROM media_editor_commits
                        WHERE operation_token=@token);
                    """, new { token = operations[0].OperationToken }) == 0)
            {
                return null;
            }
        }
        return await CommitVerifiedTvEpisodePlanAsync(operations, episodeStill,
            sharedArtwork, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A reviewed show or season preference joins the TV move and its receipt.
    /// This is a storage contract, not an HTTP mutation endpoint. The caller
    /// must bind the review to the actor and authorize every affected file.
    /// </summary>
    public async Task<MediaEditorPlanCommitResult> CommitVerifiedTvEpisodePlanAsync(
        IReadOnlyList<VerifiedTvEpisodeMove> operations,
        VerifiedEpisodeStillAssignment? episodeStill,
        VerifiedTvPreferredArtworkAssignment? sharedArtwork,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count is < 1 or > 1000)
        {
            throw new ArgumentException("A reviewed TV plan must contain 1 to 1,000 files.", nameof(operations));
        }
        var tokenValue = operations[0].OperationToken;
        if (string.IsNullOrWhiteSpace(tokenValue) || tokenValue.Length > 128
            || operations.Any(row => row.OperationToken != tokenValue))
        {
            throw new ArgumentException("Every reviewed row must use the same operation token.", nameof(operations));
        }
        if (episodeStill is not null && (episodeStill.ExpectedAffectedAssetIds is null
            || episodeStill.ExpectedAffectedAssetLibraries is null))
        {
            throw new ArgumentException("Reviewed artwork must identify every affected file and library.", nameof(episodeStill));
        }
        if (sharedArtwork is not null && sharedArtwork.ExpectedAffectedAssets is null)
        {
            throw new ArgumentException("Reviewed artwork must identify every affected file.", nameof(sharedArtwork));
        }

        var ordered = operations.OrderBy(row => row.AssetId).ToArray();
        var normalizedStill = episodeStill is null ? null : episodeStill with
        {
            ExpectedAffectedAssetIds = episodeStill.ExpectedAffectedAssetIds.OrderBy(id => id).ToArray(),
            ExpectedAffectedAssetLibraries = episodeStill.ExpectedAffectedAssetLibraries
                .OrderBy(item => item.AssetId).ToArray()
        };
        var normalizedShared = sharedArtwork is null ? null : sharedArtwork with
        {
            ExpectedAffectedAssets = sharedArtwork.ExpectedAffectedAssets
                .OrderBy(item => item.AssetId).ToArray()
        };
        var requestBody = normalizedShared is not null
            ? JsonSerializer.Serialize(new
            {
                Moves = ordered,
                EpisodeStill = normalizedStill,
                SharedArtwork = normalizedShared
            })
            : normalizedStill is null
                ? JsonSerializer.Serialize(ordered) // preserve older replay receipts
                : JsonSerializer.Serialize(new { Moves = ordered, EpisodeStill = normalizedStill });
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestBody)));
        try
        {
            return await database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var previous = connection.QuerySingleOrDefault<CommitRow>("""
                SELECT request_hash AS RequestHash, asset_id AS AssetId,
                       source_work_id AS SourceWorkId, target_work_id AS TargetWorkId,
                       sync_state AS SyncState
                FROM media_editor_commits WHERE operation_token = @tokenValue;
                """, new { tokenValue }, transaction);
            if (previous is not null)
            {
                if (previous.RequestHash != requestHash)
                {
                    return PlanConflict(operations, "This operation token was already used for different changes.");
                }
                var saved = connection.Query<CommitItemRow>("""
                    SELECT asset_id AS AssetId, source_work_id AS SourceWorkId,
                           target_work_id AS TargetWorkId
                    FROM media_editor_commit_items WHERE operation_token = @tokenValue;
                    """, new { tokenValue }, transaction).ToArray();
                if (normalizedStill is not null)
                {
                    var artworkReceipt = connection.QuerySingleOrDefault<ArtworkReceiptRow>("""
                        SELECT owner_work_id AS OwnerWorkId, artwork_asset_id AS ArtworkAssetId,
                               expected_preference_revision AS ExpectedPreferenceRevision
                        FROM media_editor_commit_artwork WHERE operation_token=@tokenValue;
                        """, new { tokenValue }, transaction);
                    if (artworkReceipt is null
                        || artworkReceipt.OwnerWorkId != normalizedStill.ExpectedOwnerWorkId
                        || artworkReceipt.ArtworkAssetId != normalizedStill.ArtworkAssetId
                        || artworkReceipt.ExpectedPreferenceRevision != normalizedStill.ExpectedPreferenceRevision)
                    {
                        return PlanConflict(operations, "The saved artwork receipt is incomplete.");
                    }
                }
                if (normalizedShared is not null)
                {
                    var sharedReceipt = connection.QuerySingleOrDefault<SharedArtworkReceiptRow>("""
                        SELECT owner_work_id AS OwnerWorkId, artwork_asset_id AS ArtworkAssetId,
                               owner_scope AS Scope, role AS Role
                        FROM media_editor_preferred_artwork_commits
                        WHERE operation_token=@tokenValue;
                        """, new { tokenValue }, transaction);
                    if (sharedReceipt is null
                        || sharedReceipt.OwnerWorkId != normalizedShared.OwnerWorkId
                        || sharedReceipt.ArtworkAssetId != normalizedShared.ArtworkAssetId
                        || sharedReceipt.Scope != normalizedShared.Scope
                        || sharedReceipt.Role != normalizedShared.Role)
                    {
                        return PlanConflict(operations, "The saved shared-artwork receipt is incomplete.");
                    }
                }
                return saved.Length == operations.Count
                    ? new MediaEditorPlanCommitResult(MediaEditorCommitOutcome.Replayed,
                        saved.Select(row => new MediaEditorCommitResult(MediaEditorCommitOutcome.Replayed,
                            row.AssetId, row.SourceWorkId, row.TargetWorkId, previous.SyncState)).ToArray())
                    : PlanConflict(operations, "The saved operation receipt is incomplete.");
            }

            var errors = new Dictionary<Guid, string>();
            var validated = new List<ValidatedMove>(ordered.Length);
            var duplicateIds = ordered.GroupBy(row => row.AssetId)
                .Where(group => group.Key == Guid.Empty || group.Count() > 1)
                .Select(group => group.Key).ToHashSet();
            foreach (var row in ordered)
            {
                token.ThrowIfCancellationRequested();
                if (duplicateIds.Contains(row.AssetId) || row.ExpectedEditionId == Guid.Empty
                    || row.ExpectedSourceWorkId == Guid.Empty || row.ExpectedSourceSeasonWorkId == Guid.Empty
                    || row.ExpectedTargetSeasonWorkId == Guid.Empty || row.ExpectedSourceShowWorkId == Guid.Empty
                    || row.ExpectedTargetShowWorkId == Guid.Empty || row.TargetWorkId == Guid.Empty
                    || string.IsNullOrWhiteSpace(row.ExpectedSourceTvdbSeriesId)
                    || string.IsNullOrWhiteSpace(row.ExpectedTargetTvdbSeriesId)
                    || string.IsNullOrWhiteSpace(row.TargetTvdbEpisodeId)
                    || row.ExpectedTargetWorkKind is not ("child" or "catalog")
                    || row.ExpectedLibraryId == Guid.Empty)
                {
                    errors[row.AssetId] = "The reviewed row has missing or duplicate identity context.";
                    continue;
                }

                var source = connection.QuerySingleOrDefault<SourceRow>("""
                    SELECT a.id AS AssetId, a.edition_id AS EditionId,
                           a.library_id AS LibraryId, e.work_id AS WorkId,
                           w.parent_work_id AS ParentWorkId, season.parent_work_id AS ShowWorkId,
                           showIdentity.value AS ShowIdentityRevision, w.media_type AS MediaType,
                           w.work_kind AS WorkKind,
                           (SELECT id_value FROM bridge_ids WHERE entity_id = show.id
                            AND id_type = @SeriesKey) AS ShowTvdbId,
                           (SELECT COUNT(*) FROM media_assets ma WHERE ma.edition_id = e.id) AS EditionAssetCount,
                           COALESCE((SELECT value FROM canonical_values
                                     WHERE entity_id = w.id AND key = @RevisionKey), '') AS IdentityRevision
                    FROM media_assets a
                    JOIN editions e ON e.id = a.edition_id
                    JOIN works w ON w.id = e.work_id
                    JOIN works season ON season.id = w.parent_work_id
                        AND season.media_type = 'TV' AND season.work_kind = 'parent'
                    JOIN works show ON show.id = season.parent_work_id
                        AND show.media_type = 'TV' AND show.work_kind = 'parent'
                    LEFT JOIN canonical_values showIdentity ON showIdentity.entity_id = show.id
                        AND showIdentity.key = @RevisionKey
                    WHERE a.id = @AssetId AND a.status = 'Normal' AND a.is_orphaned = 0;
                    """, new
                {
                    row.AssetId,
                    SeriesKey = BridgeIdKeys.TvdbId,
                    RevisionKey = MetadataFieldConstants.IdentityRevision
                }, transaction);
                if (source is null || source.EditionId != row.ExpectedEditionId
                    || !Guid.TryParse(source.LibraryId, out var currentLibraryId)
                    || currentLibraryId != row.ExpectedLibraryId
                    || source.WorkId != row.ExpectedSourceWorkId
                    || source.ParentWorkId != row.ExpectedSourceSeasonWorkId
                    || source.ShowWorkId != row.ExpectedSourceShowWorkId
                    || source.MediaType != "TV" || source.WorkKind != "child"
                    || source.ShowTvdbId != row.ExpectedSourceTvdbSeriesId
                    || (source.ShowIdentityRevision ?? "") != row.ExpectedShowIdentityRevision
                    || source.IdentityRevision != row.ExpectedSourceIdentityRevision)
                {
                    errors[row.AssetId] = "The selected file or its source episode/show changed.";
                    continue;
                }

                var target = connection.QuerySingleOrDefault<TargetRow>("""
                    SELECT w.id AS WorkId, w.parent_work_id AS ParentWorkId,
                           season.parent_work_id AS ShowWorkId,
                           w.media_type AS MediaType, w.work_kind AS WorkKind,
                           COALESCE((SELECT value FROM canonical_values
                                     WHERE entity_id = w.id AND key = @RevisionKey), '') AS IdentityRevision,
                           (SELECT id_value FROM bridge_ids
                            WHERE entity_id = w.id AND id_type = @EpisodeKey) AS TvdbEpisodeId,
                           (SELECT id_value FROM bridge_ids
                            WHERE entity_id = show.id AND id_type = @SeriesKey) AS ShowTvdbId,
                           COALESCE((SELECT value FROM canonical_values
                                     WHERE entity_id = show.id AND key = @RevisionKey), '') AS ShowIdentityRevision,
                           (SELECT COUNT(*) FROM editions e JOIN media_assets a ON a.edition_id = e.id
                            WHERE e.work_id = w.id) AS AssetCount
                    FROM works w
                    JOIN works season ON season.id = w.parent_work_id
                        AND season.media_type = 'TV' AND season.work_kind = 'parent'
                    JOIN works show ON show.id = season.parent_work_id
                        AND show.media_type = 'TV' AND show.work_kind = 'parent'
                    WHERE w.id = @TargetWorkId;
                    """, new
                {
                    row.TargetWorkId,
                    RevisionKey = MetadataFieldConstants.IdentityRevision,
                    SeriesKey = BridgeIdKeys.TvdbId,
                    EpisodeKey = BridgeIdKeys.TvdbEpisodeId
                }, transaction);
                if (target is null || target.WorkId == source.WorkId
                    || target.ParentWorkId != row.ExpectedTargetSeasonWorkId
                    || target.ShowWorkId != row.ExpectedTargetShowWorkId
                    || target.MediaType != "TV" || (target.WorkKind is not "child" and not "catalog")
                    || target.WorkKind != row.ExpectedTargetWorkKind
                    || (target.WorkKind == "catalog" && target.AssetCount != 0)
                    || target.TvdbEpisodeId != row.TargetTvdbEpisodeId
                    || target.ShowTvdbId != row.ExpectedTargetTvdbSeriesId
                    || target.ShowIdentityRevision != row.ExpectedTargetShowIdentityRevision
                    || target.IdentityRevision != row.ExpectedTargetIdentityRevision)
                {
                    errors[row.AssetId] = "The verified target episode, season, or show changed.";
                    continue;
                }
                validated.Add(new ValidatedMove(row, source));
            }

            foreach (var editionGroup in validated.GroupBy(item => item.Row.ExpectedEditionId))
            {
                var distinctTargets = editionGroup.Select(item => item.Row.TargetWorkId).Distinct().Count();
                var editionAssetCount = editionGroup.First().Source.EditionAssetCount;
                if (distinctTargets != 1 || editionGroup.Count() != editionAssetCount)
                {
                    foreach (var item in editionGroup)
                    {
                        errors[item.Row.AssetId] = "This edition contains an unselected file or conflicting targets; review all files in the edition together.";
                    }
                }
            }
            ValidatedEpisodeStill? stagedStill = null;
            if (errors.Count == 0 && normalizedStill is not null)
            {
                var stillConflict = ValidateEpisodeStill(connection, transaction, ordered, normalizedStill,
                    out stagedStill);
                if (stillConflict is not null)
                {
                    return PlanConflict(operations, stillConflict);
                }
            }
            if (errors.Count == 0 && normalizedShared is not null)
            {
                var artworkConflict = ValidateSharedArtworkBeforeMove(connection, transaction,
                    ordered, normalizedShared);
                if (artworkConflict is not null)
                {
                    return PlanConflict(operations, artworkConflict);
                }
            }
            if (errors.Count > 0)
            {
                return PlanConflict(operations, errors);
            }

            foreach (var editionGroup in validated.GroupBy(item => item.Row.ExpectedEditionId))
            {
                var row = editionGroup.First().Row;
                if (connection.Execute("""
                    UPDATE editions SET work_id = @TargetWorkId
                    WHERE id = @ExpectedEditionId AND work_id = @ExpectedSourceWorkId;
                    """, row, transaction) != 1)
                {
                    throw new InvalidOperationException("An edition changed during the reviewed transaction.");
                }
            }

            // Re-pairing a combined file's host moves the whole file, so the extra episodes it
            // covered stop being covered (they would otherwise point at the wrong show or season).
            // Dropped in this same transaction, then every touched episode is recomputed.
            var releasedWorkIds = new List<Guid>();
            foreach (var movedAssetId in ordered.Select(row => row.AssetId))
            {
                releasedWorkIds.AddRange(connection.Query<Guid>(
                    "SELECT work_id FROM media_asset_coverage WHERE asset_id = @movedAssetId;",
                    new { movedAssetId }, transaction));
                connection.Execute(
                    "DELETE FROM media_asset_coverage WHERE asset_id = @movedAssetId;",
                    new { movedAssetId }, transaction);
            }

            WorkOwnershipSync.Recompute(
                connection,
                transaction,
                ordered.SelectMany(row => new[] { row.ExpectedSourceWorkId, row.TargetWorkId })
                    .Concat(releasedWorkIds));

            if (stagedStill is not null)
            {
                ApplyEpisodeStill(connection, transaction, stagedStill);
            }
            if (normalizedShared is not null)
            {
                var assignment = new VerifiedPreferredArtworkAssignment(tokenValue,
                    normalizedShared.OwnerWorkId, normalizedShared.Scope,
                    normalizedShared.Role, normalizedShared.ArtworkAssetId,
                    normalizedShared.ExpectedVariantContentHash,
                    normalizedShared.ExpectedOwnerRevision,
                    normalizedShared.ExpectedAffectedAssets.Select(item =>
                        new VerifiedArtworkAssetLibrary(item.AssetId, item.LibraryId)).ToArray());
                var artworkResult = MediaEditorPreferredArtworkRepository.ApplyVerifiedInTransaction(
                    connection, transaction, assignment, token);
                if (artworkResult.Outcome != PreferredArtworkCommitOutcome.Committed)
                {
                    throw new SharedArtworkAtomicConflictException(artworkResult.ConflictReason
                            ?? "The reviewed shared artwork changed before Save.");
                }
            }

            // Both artwork scopes may affect unselected sibling files. Persist
            // one durable retag intent per distinct affected file alongside the
            // identity/artwork transaction; pending never claims completion.
            var writebackAssetIds = ordered.Select(row => row.AssetId)
                .Concat(normalizedStill?.ExpectedAffectedAssetIds ?? [])
                .Concat(normalizedShared?.ExpectedAffectedAssets.Select(item => item.AssetId) ?? [])
                .Distinct();
            foreach (var assetId in writebackAssetIds)
            {
                connection.Execute("""
                    UPDATE media_assets SET writeback_fields_hash=@pendingHash,
                        writeback_status='pending', writeback_last_error=NULL,
                        writeback_attempts=0, writeback_next_retry_at=NULL
                    WHERE id=@assetId;
                    """, new { assetId, pendingHash = "editor:pending:" + tokenValue }, transaction);
                connection.Execute("""
                    INSERT INTO media_file_write_intents
                        (asset_id, generation, operation_token, trigger, status, attempts,
                         lease_expires_at, last_error, created_at, updated_at)
                    VALUES (@assetId, 1, @tokenValue, 'editor_commit', 'pending', 0,
                            NULL, NULL, @intentNow, @intentNow)
                    ON CONFLICT(asset_id) DO UPDATE SET
                        generation = media_file_write_intents.generation + 1,
                        operation_token = excluded.operation_token,
                        trigger = excluded.trigger,
                        status = 'pending', attempts = 0, lease_expires_at = NULL,
                        last_error = NULL, updated_at = excluded.updated_at;
                    """, new
                {
                    assetId,
                    tokenValue,
                    intentNow = DateTimeOffset.UtcNow.ToString("O")
                }, transaction);
            }

            var first = ordered[0];
            var now = DateTimeOffset.UtcNow.ToString("O");
            connection.Execute("""
                INSERT INTO media_editor_commits
                    (operation_token, request_hash, asset_id, source_work_id, target_work_id,
                     target_tvdb_episode_id, committed_at, sync_state)
                VALUES (@OperationToken, @RequestHash, @AssetId, @ExpectedSourceWorkId,
                        @TargetWorkId, @TargetTvdbEpisodeId, @Now, 'pending');
                """, new
            {
                first.OperationToken,
                RequestHash = requestHash,
                first.AssetId,
                first.ExpectedSourceWorkId,
                first.TargetWorkId,
                first.TargetTvdbEpisodeId,
                Now = now
            }, transaction);
            foreach (var row in ordered)
            {
                connection.Execute("""
                        INSERT INTO media_editor_commit_items
                            (operation_token, asset_id, source_edition_id, source_work_id,
                             target_work_id, source_season_work_id, target_season_work_id)
                        VALUES (@OperationToken, @AssetId, @ExpectedEditionId, @ExpectedSourceWorkId,
                                @TargetWorkId, @ExpectedSourceSeasonWorkId, @ExpectedTargetSeasonWorkId);
                        """, row, transaction);
            }
            if (stagedStill is not null)
            {
                connection.Execute("""
                        INSERT INTO media_editor_commit_artwork
                            (operation_token, owner_work_id, artwork_asset_id,
                             expected_preference_revision, previous_preferred_ids_json,
                             affected_asset_ids_json, committed_at)
                        VALUES (@tokenValue, @OwnerWorkId, @ArtworkAssetId,
                                @ExpectedPreferenceRevision, @PreviousPreferredIdsJson,
                                @AffectedAssetIdsJson, @now);
                        """, new
                {
                    tokenValue,
                    OwnerWorkId = stagedStill.Assignment.ExpectedOwnerWorkId,
                    stagedStill.Assignment.ArtworkAssetId,
                    stagedStill.Assignment.ExpectedPreferenceRevision,
                    stagedStill.PreviousPreferredIdsJson,
                    AffectedAssetIdsJson = JsonSerializer.Serialize(stagedStill.Assignment.ExpectedAffectedAssetIds
                            .OrderBy(id => id)),
                    now,
                }, transaction);
            }
            return new MediaEditorPlanCommitResult(MediaEditorCommitOutcome.Committed,
                ordered.Select(row => new MediaEditorCommitResult(MediaEditorCommitOutcome.Committed,
                    row.AssetId, row.ExpectedSourceWorkId, row.TargetWorkId, "pending")).ToArray());
        }, ct).ConfigureAwait(false);
        }
        catch (SharedArtworkAtomicConflictException error)
        {
            return PlanConflict(operations, error.Message);
        }
    }

    private static MediaEditorPlanCommitResult PlanConflict(
        IReadOnlyList<VerifiedTvEpisodeMove> operations, string reason)
        => PlanConflict(operations, operations.Select(row => row.AssetId)
            .Distinct().ToDictionary(id => id, _ => reason));

    private static string? ValidateSharedArtworkBeforeMove(System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction, IReadOnlyList<VerifiedTvEpisodeMove> moves,
        VerifiedTvPreferredArtworkAssignment artwork)
    {
        if (artwork.OwnerWorkId == Guid.Empty || artwork.ArtworkAssetId == Guid.Empty
            || string.IsNullOrWhiteSpace(artwork.ExpectedVariantContentHash)
            || string.IsNullOrWhiteSpace(artwork.ExpectedOwnerRevision)
            || artwork.ExpectedAffectedAssets.Count is < 1 or > 1000
            || artwork.ExpectedAffectedAssets.Any(item => item.AssetId == Guid.Empty
                || item.LibraryId == Guid.Empty || string.IsNullOrWhiteSpace(item.IdentityRevision))
            || artwork.ExpectedAffectedAssets.Select(item => item.AssetId).Distinct().Count()
                != artwork.ExpectedAffectedAssets.Count
            || (artwork.Scope, artwork.Role) is not
                (("TvShow", "Primary") or ("TvShow", "Background") or ("TvShow", "Logo")
                    or ("TvSeason", "Primary")))
        {
            return "The reviewed shared-artwork assignment is incomplete or unsupported.";
        }

        var show = moves[0].ExpectedTargetShowWorkId;
        if (moves.Any(row => row.ExpectedTargetShowWorkId != show))
        {
            return "A shared artwork choice cannot span different shows.";
        }
        if (artwork.Scope == "TvShow" && artwork.OwnerWorkId != show)
        {
            return "The reviewed artwork owner is not this show's Work.";
        }
        if (artwork.Scope == "TvSeason" && !moves.Any(row =>
                row.ExpectedSourceSeasonWorkId == artwork.OwnerWorkId
                || row.ExpectedTargetSeasonWorkId == artwork.OwnerWorkId))
        {
            return "The reviewed season artwork owner is outside this plan.";
        }

        var owner = connection.QuerySingleOrDefault<(Guid? ParentWorkId, string MediaType,
            string WorkKind)>("""
            SELECT parent_work_id AS ParentWorkId, media_type AS MediaType,
                   work_kind AS WorkKind FROM works WHERE id=@OwnerWorkId;
            """, new { artwork.OwnerWorkId }, transaction);
        if (owner.MediaType != "TV" || owner.WorkKind != "parent"
            || (artwork.Scope == "TvShow" && owner.ParentWorkId is not null)
            || (artwork.Scope == "TvSeason" && owner.ParentWorkId != show))
        {
            return "The reviewed artwork owner changed its TV lineage.";
        }

        var revisions = MediaEditorTvArtworkIdentityRevision.Read(connection, transaction,
            artwork.ExpectedAffectedAssets.Select(item => item.AssetId).ToArray());
        if (revisions.Count != artwork.ExpectedAffectedAssets.Count
            || artwork.ExpectedAffectedAssets.Any(item =>
                !revisions.TryGetValue(item.AssetId, out var current)
                || current != item.IdentityRevision))
        {
            return "An affected file's identity changed after artwork review.";
        }

        // The generic preference primitive checks the exact complete descendant
        // set, libraries, owner preference, and managed variant after the moves.
        // A late conflict throws out of ExecuteWriteAsync to roll back everything.
        return null;
    }

    private static MediaEditorPlanCommitResult PlanConflict(
        IReadOnlyList<VerifiedTvEpisodeMove> operations, IReadOnlyDictionary<Guid, string> errors)
        => new(MediaEditorCommitOutcome.Conflict, operations.Select(row =>
            new MediaEditorCommitResult(MediaEditorCommitOutcome.Conflict, row.AssetId,
                row.ExpectedSourceWorkId, row.TargetWorkId, null,
                errors.GetValueOrDefault(row.AssetId) ?? "Not saved because another row has a conflict.")).ToArray());

    /// <summary>Opaque preference state for a server-owned episode-artwork preview.</summary>
    public Task<string?> GetEpisodeStillPreferenceRevisionAsync(Guid ownerWorkId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var validOwner = connection.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM works WHERE id=@ownerWorkId AND media_type='TV'
                AND work_kind IN ('child','catalog');
            """, new { ownerWorkId }) == 1;
        return Task.FromResult(validOwner ? ReadEpisodeStillRevision(connection, null, ownerWorkId) : null);
    }

    private static string? ValidateEpisodeStill(System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction, IReadOnlyList<VerifiedTvEpisodeMove> moves,
        VerifiedEpisodeStillAssignment assignment, out ValidatedEpisodeStill? validated)
    {
        validated = null;
        var expected = assignment.ExpectedAffectedAssetIds?.OrderBy(id => id).ToArray() ?? [];
        if (assignment.ExpectedOwnerWorkId == Guid.Empty || assignment.ArtworkAssetId == Guid.Empty
            || string.IsNullOrWhiteSpace(assignment.ExpectedVariantContentHash)
            || string.IsNullOrWhiteSpace(assignment.ExpectedPreferenceRevision)
            || expected.Length is < 1 or > 1000 || expected.Distinct().Count() != expected.Length
            || !moves.Any(row => row.TargetWorkId == assignment.ExpectedOwnerWorkId))
        {
            return "The reviewed episode-artwork owner or affected file set does not match this plan.";
        }

        // An episode Work owns one preferred still for every file beneath it.
        // Compare the reviewed impact with the complete post-move membership,
        // including files that already belonged to the target episode.
        var existing = connection.Query<ArtworkImpactAssetRow>("""
            SELECT a.id AS AssetId, a.library_id AS LibraryId,
                   a.status AS Status, a.is_orphaned AS IsOrphaned
            FROM editions e JOIN media_assets a ON a.edition_id=e.id
            WHERE e.work_id=@ownerWorkId;
            """, new { ownerWorkId = assignment.ExpectedOwnerWorkId }, transaction).ToArray();
        var movedOut = moves.Where(row => row.ExpectedSourceWorkId == assignment.ExpectedOwnerWorkId)
            .Select(row => row.AssetId).ToHashSet();
        var actualLibraries = new Dictionary<Guid, Guid>();
        foreach (var asset in existing.Where(item => !movedOut.Contains(item.AssetId)))
        {
            if (asset.Status != "Normal" || asset.IsOrphaned
                || !Guid.TryParse(asset.LibraryId, out var libraryId) || libraryId == Guid.Empty)
            {
                return "The target episode contains a file that cannot safely share this artwork.";
            }
            actualLibraries[asset.AssetId] = libraryId;
        }
        foreach (var move in moves.Where(row => row.TargetWorkId == assignment.ExpectedOwnerWorkId))
        {
            actualLibraries[move.AssetId] = move.ExpectedLibraryId;
        }

        var actual = actualLibraries.Keys.OrderBy(id => id).ToArray();
        if (!expected.SequenceEqual(actual))
        {
            return "The target episode's affected file set changed after artwork review.";
        }
        var reviewedLibraries = assignment.ExpectedAffectedAssetLibraries;
        if (reviewedLibraries.Count != expected.Length
            || reviewedLibraries.Any(item => item.LibraryId == Guid.Empty)
            || !reviewedLibraries.Select(item => item.AssetId).OrderBy(id => id).SequenceEqual(expected)
            || reviewedLibraries.Any(item => !actualLibraries.TryGetValue(item.AssetId, out var libraryId)
                || libraryId != item.LibraryId))
        {
            return "An affected file's library changed after artwork review.";
        }

        var variant = connection.QuerySingleOrDefault<ArtworkVariantRow>("""
            SELECT id AS Id, content_hash AS ContentHash, original_path AS OriginalPath
            FROM artwork_assets WHERE id=@ArtworkAssetId;
            """, assignment, transaction);
        if (variant is null || variant.ContentHash != assignment.ExpectedVariantContentHash
            || string.IsNullOrWhiteSpace(variant.OriginalPath))
        {
            return "The staged managed artwork variant changed or is unavailable.";
        }

        if (ReadEpisodeStillRevision(connection, transaction, assignment.ExpectedOwnerWorkId)
            != assignment.ExpectedPreferenceRevision)
        {
            return "The episode's preferred artwork changed after review.";
        }

        var priorPreferred = connection.Query<Guid>("""
            SELECT artwork_asset_id FROM entity_artwork_links
            WHERE entity_id=@ownerWorkId AND entity_type='Work' AND role='Primary'
              AND context='Episode' AND is_preferred=1
            ORDER BY artwork_asset_id;
            """, new { ownerWorkId = assignment.ExpectedOwnerWorkId }, transaction).ToArray();
        validated = new(assignment, JsonSerializer.Serialize(priorPreferred));
        return null;
    }

    private static string ReadEpisodeStillRevision(System.Data.IDbConnection connection,
        System.Data.IDbTransaction? transaction, Guid ownerWorkId)
    {
        var links = connection.Query<ArtworkPreferenceStateRow>("""
            SELECT id AS Id, artwork_asset_id AS ArtworkAssetId,
                   is_preferred AS IsPreferred, is_user_override AS IsUserOverride
            FROM entity_artwork_links
            WHERE entity_id=@ownerWorkId AND entity_type='Work' AND role='Primary'
              AND context='Episode'
            ORDER BY id;
            """, new { ownerWorkId }, transaction).ToArray();
        var legacy = connection.Query<ArtworkPreferenceStateRow>("""
            SELECT id AS Id, is_preferred AS IsPreferred,
                   is_user_override AS IsUserOverride
            FROM entity_assets
            WHERE entity_id=@ownerWorkId AND entity_type='Work'
              AND asset_type='EpisodeStill'
            ORDER BY id;
            """, new { ownerWorkId }, transaction).ToArray();
        var state = JsonSerializer.Serialize(new { Version = 1, ownerWorkId, Links = links, Legacy = legacy });
        return "v1:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
    }

    private static void ApplyEpisodeStill(System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction, ValidatedEpisodeStill staged)
    {
        var assignment = staged.Assignment;
        var now = DateTimeOffset.UtcNow.ToString("O");
        var newLinkId = Guid.NewGuid();
        connection.Execute("""
            UPDATE entity_artwork_links SET is_preferred=0, updated_at=@now
            WHERE entity_id=@ExpectedOwnerWorkId AND entity_type='Work'
              AND role='Primary' AND context='Episode' AND is_preferred=1;
            INSERT INTO entity_artwork_links
                (id, entity_id, entity_type, artwork_asset_id, role, context,
                 source_asset_type, is_preferred, is_user_override, created_at)
            VALUES (@newLinkId, @ExpectedOwnerWorkId, 'Work', @ArtworkAssetId,
                    'Primary', 'Episode', 'EpisodeStill', 1, 1, @now)
            ON CONFLICT(entity_id, entity_type, artwork_asset_id, role, context)
            DO UPDATE SET is_preferred=1, is_user_override=1,
                          source_asset_type='EpisodeStill', updated_at=@now;
            """, new { assignment.ExpectedOwnerWorkId, assignment.ArtworkAssetId, newLinkId, now }, transaction);
        var durableLinkId = connection.ExecuteScalar<Guid>("""
            SELECT id FROM entity_artwork_links
            WHERE entity_id=@ExpectedOwnerWorkId AND entity_type='Work'
              AND artwork_asset_id=@ArtworkAssetId AND role='Primary' AND context='Episode';
            """, assignment, transaction);
        connection.Execute("""
            UPDATE entity_assets SET is_preferred=0, updated_at=@now
            WHERE entity_id=@ExpectedOwnerWorkId AND entity_type='Work'
              AND asset_type='EpisodeStill' AND is_preferred=1;
            INSERT INTO entity_assets
                (id, entity_id, entity_type, asset_type, image_url,
                 local_image_path, local_image_path_s, local_image_path_m, local_image_path_l,
                 source_provider, width_px, height_px, aspect_class,
                 primary_hex, secondary_hex, accent_hex, asset_class,
                 storage_location, owner_scope, is_preferred, is_user_override, created_at)
            SELECT @durableLinkId, @ExpectedOwnerWorkId, 'Work', 'EpisodeStill',
                   '/api/v1/display/artwork/assets/' || lower(hex(art.id)) || '/content',
                   art.original_path, art.small_path, art.medium_path, art.large_path,
                   art.source_provider, art.width_px, art.height_px, art.aspect_class,
                   art.primary_hex, art.secondary_hex, art.accent_hex,
                   'Artwork', 'Central', 'Episode', 1, 1, @now
            FROM artwork_assets art WHERE art.id=@ArtworkAssetId
            ON CONFLICT(id) DO UPDATE SET
                local_image_path=excluded.local_image_path,
                local_image_path_s=excluded.local_image_path_s,
                local_image_path_m=excluded.local_image_path_m,
                local_image_path_l=excluded.local_image_path_l,
                is_preferred=1, is_user_override=1, updated_at=@now;
            """, new { assignment.ExpectedOwnerWorkId, assignment.ArtworkAssetId, durableLinkId, now }, transaction);
    }

    private sealed record ValidatedEpisodeStill(
        VerifiedEpisodeStillAssignment Assignment, string PreviousPreferredIdsJson);

    private sealed class ArtworkVariantRow
    {
        public Guid Id { get; set; }
        public string ContentHash { get; set; } = "";
        public string? OriginalPath { get; set; }
    }

    private sealed class ArtworkPreferenceStateRow
    {
        public Guid Id { get; set; }
        public Guid? ArtworkAssetId { get; set; }
        public bool IsPreferred { get; set; }
        public bool IsUserOverride { get; set; }
    }

    private sealed class ArtworkReceiptRow
    {
        public Guid OwnerWorkId { get; set; }
        public Guid ArtworkAssetId { get; set; }
        public string ExpectedPreferenceRevision { get; set; } = "";
    }

    private sealed class ArtworkImpactAssetRow
    {
        public Guid AssetId { get; set; }
        public string? LibraryId { get; set; }
        public string Status { get; set; } = "";
        public bool IsOrphaned { get; set; }
    }

    private sealed record ValidatedMove(VerifiedTvEpisodeMove Row, SourceRow Source);

    public async Task<MediaEditorCommitResult> CommitVerifiedTvEpisodeMoveAsync(
        VerifiedTvEpisodeMove operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var result = await CommitVerifiedTvEpisodePlanAsync([operation], ct);
        return result.Items[0];
    }
    private sealed class SourceRow
    {
        public Guid AssetId { get; set; }
        public Guid EditionId { get; set; }
        public string? LibraryId { get; set; }
        public Guid WorkId { get; set; }
        public Guid? ParentWorkId { get; set; }
        public Guid? ShowWorkId { get; set; }
        public string? ShowIdentityRevision { get; set; }
        public string? ShowTvdbId { get; set; }
        public string MediaType { get; set; } = "";
        public string WorkKind { get; set; } = "";
        public int WorkAssetCount { get; set; }
        public int EditionAssetCount { get; set; }
        public string IdentityRevision { get; set; } = "";
    }

    private sealed class TargetRow
    {
        public Guid WorkId { get; set; }
        public Guid? ParentWorkId { get; set; }
        public Guid? ShowWorkId { get; set; }
        public string MediaType { get; set; } = "";
        public string WorkKind { get; set; } = "";
        public string IdentityRevision { get; set; } = "";
        public string? TvdbEpisodeId { get; set; }
        public string? ShowTvdbId { get; set; }
        public string ShowIdentityRevision { get; set; } = "";
        public int AssetCount { get; set; }
    }

    private sealed class CommitRow
    {
        public string RequestHash { get; set; } = "";
        public Guid AssetId { get; set; }
        public Guid SourceWorkId { get; set; }
        public Guid TargetWorkId { get; set; }
        public string TargetTvdbEpisodeId { get; set; } = "";
        public string CommittedAt { get; set; } = "";
        public string SyncState { get; set; } = "";
    }

    private sealed class CommitItemRow
    {
        public Guid AssetId { get; set; }
        public Guid SourceWorkId { get; set; }
        public Guid TargetWorkId { get; set; }
    }

    private sealed class SharedArtworkReceiptRow
    {
        public Guid OwnerWorkId { get; set; }
        public Guid ArtworkAssetId { get; set; }
        public string Scope { get; set; } = "";
        public string Role { get; set; } = "";
    }

    private sealed class SharedArtworkAtomicConflictException(string message) : Exception(message);
}

/// <summary>
/// Internal handoff from a server-side TV catalogue/preview validator. It carries
/// identity references only; titles and numbering are never accepted as commit facts.
/// </summary>
public sealed record VerifiedTvEpisodeMove(
    string OperationToken,
    Guid AssetId,
    Guid ExpectedEditionId,
    Guid ExpectedSourceWorkId,
    Guid ExpectedSourceSeasonWorkId,
    Guid TargetWorkId,
    Guid ExpectedTargetSeasonWorkId,
    Guid ExpectedSourceShowWorkId,
    Guid ExpectedTargetShowWorkId,
    string ExpectedSourceTvdbSeriesId,
    string ExpectedTargetTvdbSeriesId,
    string TargetTvdbEpisodeId,
    string ExpectedSourceIdentityRevision,
    string ExpectedTargetIdentityRevision,
    string ExpectedShowIdentityRevision,
    string ExpectedTargetShowIdentityRevision,
    string ExpectedTargetWorkKind,
    Guid ExpectedLibraryId);

/// <summary>
/// Internal reviewed episode-still choice. Caller authorizes this Work and
/// verifies the managed image before passing the plan to storage.
/// </summary>
public sealed record VerifiedEpisodeStillAssignment(
    Guid ExpectedOwnerWorkId,
    Guid ArtworkAssetId,
    string ExpectedVariantContentHash,
    string ExpectedPreferenceRevision,
    IReadOnlyList<Guid> ExpectedAffectedAssetIds,
    IReadOnlyList<VerifiedArtworkAssetLibrary> ExpectedAffectedAssetLibraries);

public sealed record VerifiedArtworkAssetLibrary(Guid AssetId, Guid LibraryId);

/// <summary>
/// Server-reviewed show/season preference for the same TV show as the moves.
/// Every expected asset is authorized by the caller; Storage rechecks its
/// identity before the move and the complete descendant/library set after it.
/// </summary>
public sealed record VerifiedTvPreferredArtworkAssignment(
    Guid OwnerWorkId,
    string Scope,
    string Role,
    Guid ArtworkAssetId,
    string ExpectedVariantContentHash,
    string ExpectedOwnerRevision,
    IReadOnlyList<VerifiedTvArtworkAssetReview> ExpectedAffectedAssets);

public sealed record VerifiedTvArtworkAssetReview(Guid AssetId, Guid LibraryId,
    string IdentityRevision);

public enum MediaEditorCommitOutcome { Committed, Replayed, Conflict }

public sealed record MediaEditorCommitResult(
    MediaEditorCommitOutcome Outcome,
    Guid AssetId,
    Guid SourceWorkId,
    Guid TargetWorkId,
    string? SyncState,
    string? ConflictReason = null);

public sealed record MediaEditorPlanCommitResult(
    MediaEditorCommitOutcome Outcome,
    IReadOnlyList<MediaEditorCommitResult> Items);
