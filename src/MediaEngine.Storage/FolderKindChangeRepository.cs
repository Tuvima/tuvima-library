using System.Globalization;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite side of "Change what this folder is" (Library setup spec D40(c), D60, D64, D69): the plan of a change job
/// (<c>folder_kind_changes</c>) and the two changes made to one file's data. Re-placing a file reuses the pieces of a library move: the
/// hierarchy is resolved by <see cref="HierarchyResolver"/> exactly as ingestion resolves a new file, the Edition moves under the resolved Work,
/// what people did with the old title is carried by <see cref="WorkPersonalStateMerge"/>, and an emptied title is removed by
/// <see cref="LibraryItemDeletionRepository"/>. Every change is one write transaction and can be repeated safely.
/// </summary>
public sealed class FolderKindChangeRepository(
    IDatabaseConnection db,
    HierarchyResolver resolver,
    ILogger<FolderKindChangeRepository>? logger = null) : IFolderKindChangeRepository
{
    // The artwork sources that are the file's own or a person's, not a provider's: kept when looked-up details are dropped.
    private static readonly string[] LocalArtworkSources = ["embedded", "folder", "user_upload"];

    // Placement and bookkeeping keys that belong to the old place, never to the details a person set: they do not follow a file to a new title.
    /// <summary>Why a file is not placed again: its title holds artwork or details a person made, which are not carried to another title.</summary>
    public const string UserDetailsReason =
        "You changed the artwork or details of this title yourself, so its last file can't be placed under another kind. Nothing was changed.";

    private static readonly string[] KeysThatStayWithThePlace =
        [.. PlacementNumbers.Keys, MetadataFieldConstants.MediaTypeField, "identity_revision"];

    public Task SavePlanAsync(FolderKindChangePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        connection.Execute(
            """
            INSERT OR IGNORE INTO folder_kind_changes
                (operation_id, source_id, from_library_id, to_library_id, from_kind_key, to_kind_key, target_media_type,
                 target_is_plain, starts_first_check, started_by_profile_id, repairs_operation_id, created_at, config_applied_at)
            VALUES
                (@operationId, @sourceId, @fromLibraryId, @toLibraryId, @fromKindKey, @toKindKey, @targetMediaType,
                 @targetIsPlain, @startsFirstCheck, @startedByProfileId, @repairsOperationId, @createdAt, @configAppliedAt);
            """,
            new
            {
                operationId = plan.OperationId,
                sourceId = plan.SourceId,
                fromLibraryId = plan.FromLibraryId,
                toLibraryId = plan.ToLibraryId,
                fromKindKey = plan.FromKindKey,
                toKindKey = plan.ToKindKey,
                targetMediaType = plan.TargetMediaType,
                targetIsPlain = plan.TargetIsPlain ? 1 : 0,
                startsFirstCheck = plan.StartsFirstCheck ? 1 : 0,
                startedByProfileId = GuidSql.ToDb(plan.StartedByProfileId),
                repairsOperationId = GuidSql.ToDb(plan.RepairsOperationId),
                createdAt = plan.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                configAppliedAt = plan.ConfigAppliedAt?.ToString("O", CultureInfo.InvariantCulture),
            });
        return Task.CompletedTask;
    }

    public Task<FolderKindChangePlan?> GetPlanAsync(Guid operationId, CancellationToken ct = default) =>
        ReadPlan("WHERE operation_id = @operationId", new { operationId }, ct);

    public Task<FolderKindChangePlan?> GetLatestPlanAsync(Guid sourceId, CancellationToken ct = default) =>
        ReadPlan("WHERE source_id = @sourceId ORDER BY created_at DESC LIMIT 1", new { sourceId }, ct);

    private Task<FolderKindChangePlan?> ReadPlan(string where, object parameters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var row = connection.QueryFirstOrDefault<PlanRow>(
            $"""
            SELECT operation_id AS OperationId, source_id AS SourceId, from_library_id AS FromLibraryId, to_library_id AS ToLibraryId,
                   from_kind_key AS FromKindKey, to_kind_key AS ToKindKey, target_media_type AS TargetMediaType,
                   target_is_plain AS TargetIsPlain, starts_first_check AS StartsFirstCheck,
                   started_by_profile_id AS StartedByProfileId, repairs_operation_id AS RepairsOperationId,
                   created_at AS CreatedAt, config_applied_at AS ConfigAppliedAt
            FROM folder_kind_changes
            {where};
            """,
            parameters);
        return Task.FromResult(row is null
            ? null
            : new FolderKindChangePlan(
                row.OperationId, row.SourceId, row.FromLibraryId, row.ToLibraryId, row.FromKindKey, row.ToKindKey, row.TargetMediaType,
                row.TargetIsPlain != 0, row.StartsFirstCheck != 0,
                DateTimeOffset.Parse(row.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                row.ConfigAppliedAt is null
                    ? null
                    : DateTimeOffset.Parse(row.ConfigAppliedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                row.StartedByProfileId,
                row.RepairsOperationId));
    }

    public Task<bool> MarkConfigAppliedAsync(Guid operationId, DateTimeOffset at, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var changed = connection.Execute(
            "UPDATE folder_kind_changes SET config_applied_at = COALESCE(config_applied_at, @at) WHERE operation_id = @operationId;",
            new { operationId, at = at.ToString("O", CultureInfo.InvariantCulture) });
        return Task.FromResult(changed > 0);
    }

    public Task<AssetWorkPlacement?> GetPlacementAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        return Task.FromResult(ReadPlacement(connection, null, assetId));
    }

    public async Task<AssetReplacement> ReplaceUnderMediaTypeAsync(
        Guid assetId,
        MediaType mediaType,
        IReadOnlyDictionary<string, string> metadata,
        string? sourceFilePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ct.ThrowIfCancellationRequested();

        var placement = await GetPlacementAsync(assetId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Media asset {assetId} was not found.");
        var typeName = mediaType.ToString();
        if (string.Equals(placement.WorkMediaType, typeName, StringComparison.OrdinalIgnoreCase))
        {
            return new AssetReplacement(false, placement.WorkId, []);
        }

        // A title the person made their own is refused before anything is created (the same check runs again inside the move).
        using (var read = db.CreateConnection())
        {
            var onlyFile = read.ExecuteScalar<long>("SELECT COUNT(*) FROM editions WHERE work_id = @id;", new { id = placement.WorkId }) == 1;
            if (onlyFile && HoldsPersonalWorkDetails(read, null, placement.WorkId))
            {
                throw new InvalidOperationException(UserDetailsReason);
            }
        }

        // The same resolution ingestion makes for a new file: the show, album or series folder is found or made, then the Work under it.
        var resolved = await resolver.ResolveAsync(mediaType, metadata, sourceFilePath, ct).ConfigureAwait(false);
        try
        {
            return await db.ExecuteWriteAsync(
                (connection, transaction, _) => MoveEdition(connection, transaction, placement, resolved.WorkId, mediaType),
                ct).ConfigureAwait(false);
        }
        catch
        {
            // A Work this call made must not be left empty when the move did not happen (a repeat resolves again). Best effort:
            // the failure being rethrown is the one the caller needs to see, so a clean-up failure is logged and is only a stray empty title.
            if (resolved.NewlyCreated)
            {
                try
                {
                    await RemoveIfEmptyAsync(resolved.WorkId).ConfigureAwait(false);
                }
                catch (Exception cleanup) when (cleanup is not OperationCanceledException)
                {
                    logger?.LogWarning(cleanup, "The empty title {WorkId} made for file {AssetId} could not be removed after its move failed", resolved.WorkId, assetId);
                }
            }

            throw;
        }
    }

    public async Task<DroppedProviderDetails> DropProviderDetailsAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var placement = await GetPlacementAsync(assetId, ct).ConfigureAwait(false);
        if (placement is null)
        {
            return new DroppedProviderDetails([], 0);
        }

        Guid[] entityIds = [assetId, placement.EditionId, placement.WorkId];
        return await db.ExecuteWriteAsync((connection, transaction, _) => DropDetails(connection, transaction, placement, entityIds), ct)
            .ConfigureAwait(false);
    }

    // ----- the transactions ------------------------------------------------------------------------------------------------------

    private static AssetReplacement MoveEdition(
        SqliteConnection connection, SqliteTransaction transaction, AssetWorkPlacement placement, Guid newWorkId, MediaType mediaType)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var current = ReadPlacement(connection, transaction, placement.AssetId)
            ?? throw new InvalidOperationException($"Media asset {placement.AssetId} was not found.");
        if (current.WorkId == newWorkId)
        {
            return new AssetReplacement(false, newWorkId, []);
        }

        if (current.WorkId != placement.WorkId)
        {
            throw new InvalidOperationException("The file was moved to another title while its kind was being changed. Nothing was changed.");
        }

        var oldWorkId = placement.WorkId;
        if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM editions WHERE work_id = @id;", new { id = oldWorkId }, transaction) == 1
            && HoldsPersonalWorkDetails(connection, transaction, oldWorkId))
        {
            throw new InvalidOperationException(UserDetailsReason);
        }

        connection.Execute(
            "UPDATE editions SET work_id = @newWorkId, placement_unmatched = 0 WHERE id = @editionId;",
            new { newWorkId, editionId = placement.EditionId },
            transaction);
        connection.Execute(
            "UPDATE works SET ownership = 'Owned', is_catalog_only = 0, work_kind = CASE WHEN work_kind = 'catalog' THEN 'child' ELSE work_kind END WHERE id = @id;",
            new { id = newWorkId },
            transaction);

        // What kind of file this is now: the choice of the person who changed the folder, so it outranks the claim made at ingestion.
        LibraryMoveRepository.SetNumbers(
            connection, transaction, [placement.AssetId, newWorkId], [(MetadataFieldConstants.MediaTypeField, mediaType.ToString())], now);

        var ownedFiles = new List<string>();
        var left = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM editions WHERE work_id = @id;", new { id = oldWorkId }, transaction);
        if (left == 0)
        {
            // The old title goes, so what people did with it and the details they set on it go to the file's new title first.
            CarryPersonalDetails(connection, transaction, oldWorkId, newWorkId);
            WorkPersonalStateMerge.Carry(connection, transaction, oldWorkId, newWorkId);
            RemoveEmptyLeaf(connection, transaction, oldWorkId, ownedFiles);
        }

        return new AssetReplacement(true, newWorkId, ownedFiles);
    }

    // Details a person set on the old title (their locked claims and manual ones) follow the file; the numbers that say where it sat in
    // the old list, and the media type, do not.
    private static void CarryPersonalDetails(SqliteConnection connection, SqliteTransaction transaction, Guid oldWorkId, Guid newWorkId)
    {
        var keys = connection.Query<string>(
            """
            SELECT DISTINCT claim_key FROM metadata_claims
            WHERE entity_id = @oldWorkId AND (is_user_locked = 1 OR provider_id = @manual) AND claim_key NOT IN @skip;
            """,
            new { oldWorkId, manual = WellKnownProviders.UserManual, skip = KeysThatStayWithThePlace },
            transaction).ToArray();
        if (keys.Length == 0)
        {
            return;
        }

        connection.Execute(
            """
            UPDATE metadata_claims SET entity_id = @newWorkId
            WHERE entity_id = @oldWorkId AND (is_user_locked = 1 OR provider_id = @manual) AND claim_key IN @keys;
            UPDATE OR IGNORE canonical_values SET entity_id = @newWorkId WHERE entity_id = @oldWorkId AND key IN @keys;
            UPDATE OR IGNORE canonical_value_arrays SET entity_id = @newWorkId WHERE entity_id = @oldWorkId AND key IN @keys;
            """,
            new { oldWorkId, newWorkId, manual = WellKnownProviders.UserManual, keys },
            transaction);
    }

    // Removes the title the file left (its people's state was carried to the new title first), then sweeps the places that are left empty with the
    // rule every move uses: a show, season, album or series someone saved, reacted to, collected, tagged or gave their own artwork or details stays.
    private static void RemoveEmptyLeaf(SqliteConnection connection, SqliteTransaction transaction, Guid workId, List<string> ownedFiles)
    {
        var removable = connection.ExecuteScalar<long>(
            """
            -- readiness: counts what still hangs under the title by id, whatever its readiness
            SELECT EXISTS (SELECT 1 FROM works w WHERE w.id = @workId)
               AND NOT EXISTS (SELECT 1 FROM editions e WHERE e.work_id = @workId)
               AND NOT EXISTS (SELECT 1 FROM works c WHERE c.parent_work_id = @workId);
            """,
            new { workId },
            transaction) == 1;
        if (removable)
        {
            ownedFiles.AddRange(LibraryItemDeletionRepository.DeleteWorksCore(connection, transaction, [workId]).TuvimaOwnedFiles);
        }

        WorkHierarchyMaintenanceService.CleanupEmptyParentsCore(connection, transaction);
    }

    // The details a person made on a title that go with it when it is removed and that no move carries: their own artwork and the details they
    // typed on the title. Where the last file of such a title would move out, the change is refused (as a move into an existing title is).
    private static bool HoldsPersonalWorkDetails(SqliteConnection connection, SqliteTransaction? transaction, Guid workId) =>
        connection.ExecuteScalar<long>(
            """
            -- readiness: looks at one title by id, whatever its readiness
            SELECT COALESCE(NULLIF(w.display_overrides_json, ''), '{}') NOT IN ('{}', 'null')
                OR EXISTS (SELECT 1 FROM entity_assets ea WHERE ea.entity_id = w.id AND COALESCE(ea.is_user_override, 0) = 1)
                OR EXISTS (SELECT 1 FROM entity_artwork_links link WHERE link.entity_id = w.id AND COALESCE(link.is_user_override, 0) = 1)
            FROM works w WHERE w.id = @workId;
            """,
            new { workId },
            transaction) == 1;

    private async Task RemoveIfEmptyAsync(Guid workId)
    {
        await db.ExecuteWriteAsync((connection, transaction, _) =>
        {
            var owned = new List<string>();
            RemoveEmptyLeaf(connection, transaction, workId, owned);
        }).ConfigureAwait(false);
    }

    private static DroppedProviderDetails DropDetails(
        SqliteConnection connection, SqliteTransaction transaction, AssetWorkPlacement placement, Guid[] entityIds)
    {
        var blobs = entityIds.Select(GuidSql.ToBlob).ToArray();
        var kept = new[] { WellKnownProviders.LocalProcessor, WellKnownProviders.LibraryScanner, WellKnownProviders.UserManual }
            .Select(GuidSql.ToBlob).ToArray();

        // The claims that came from a provider: not from the file itself, not a person's own edit, not a locked claim.
        var affected = connection.Query<ClaimKeyRow>(
            """
            SELECT DISTINCT entity_id AS EntityId, claim_key AS Key FROM metadata_claims
            WHERE entity_id IN @blobs AND is_user_locked = 0 AND provider_id NOT IN @kept;
            """,
            new { blobs, kept },
            transaction).ToList();
        var dropped = 0;
        if (affected.Count > 0)
        {
            dropped = connection.Execute(
                "DELETE FROM metadata_claims WHERE entity_id IN @blobs AND is_user_locked = 0 AND provider_id NOT IN @kept;",
                new { blobs, kept },
                transaction);
            foreach (var group in affected.GroupBy(pair => pair.EntityId))
            {
                // The values these claims produced go too; the caller works them out again from the claims that remain.
                var keys = group.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).ToArray();
                connection.Execute(
                    """
                    DELETE FROM canonical_values WHERE entity_id = @entityId AND key IN @keys;
                    DELETE FROM canonical_value_arrays WHERE entity_id = @entityId AND key IN @keys;
                    """,
                    new { entityId = group.Key, keys },
                    transaction);
            }
        }

        // Outside ids: bridge ids, the Wikidata ids and the external identifiers.
        connection.Execute(
            """
            DELETE FROM bridge_ids WHERE entity_id IN @blobs;
            UPDATE works SET wikidata_qid = NULL, external_identifiers = NULL WHERE id = @workId;
            UPDATE editions SET wikidata_qid = NULL WHERE id = @editionId;
            """,
            new { blobs, workId = placement.WorkId, editionId = placement.EditionId },
            transaction);

        // Provider artwork: a picture the file carried, its folder holds, or a person chose stays.
        connection.Execute(
            """
            DELETE FROM entity_artwork_links
            WHERE entity_id IN @blobs AND is_user_override = 0
              AND artwork_asset_id IN (
                  SELECT id FROM artwork_assets
                  WHERE source_provider IS NOT NULL AND source_provider NOT IN @local);
            DELETE FROM entity_assets
            WHERE entity_id IN @blobs AND is_user_override = 0
              AND source_provider IS NOT NULL AND source_provider NOT IN @local;
            """,
            new { blobs, local = LocalArtworkSources },
            transaction);

        return new DroppedProviderDetails([.. affected.Select(pair => pair.EntityId).Distinct()], dropped);
    }

    // ----- reads -----------------------------------------------------------------------------------------------------------------

    private static AssetWorkPlacement? ReadPlacement(SqliteConnection connection, SqliteTransaction? transaction, Guid assetId)
    {
        var row = connection.QueryFirstOrDefault<PlacementRow>(
            """
            SELECT ma.id AS AssetId, ma.edition_id AS EditionId, e.work_id AS WorkId, w.media_type AS WorkMediaType, ma.library_id AS LibraryId
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            WHERE ma.id = @assetId;
            """,
            new { assetId },
            transaction);
        return row is null ? null : new AssetWorkPlacement(row.AssetId, row.EditionId, row.WorkId, row.WorkMediaType, row.LibraryId);
    }

    private sealed class PlanRow
    {
        public Guid OperationId { get; set; }
        public Guid SourceId { get; set; }
        public string FromLibraryId { get; set; } = string.Empty;
        public string ToLibraryId { get; set; } = string.Empty;
        public string FromKindKey { get; set; } = string.Empty;
        public string ToKindKey { get; set; } = string.Empty;
        public string TargetMediaType { get; set; } = string.Empty;
        public long TargetIsPlain { get; set; }
        public long StartsFirstCheck { get; set; }
        public Guid? StartedByProfileId { get; set; }
        public Guid? RepairsOperationId { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string? ConfigAppliedAt { get; set; }
    }

    private sealed class PlacementRow
    {
        public Guid AssetId { get; set; }
        public Guid EditionId { get; set; }
        public Guid WorkId { get; set; }
        public string WorkMediaType { get; set; } = string.Empty;
        public string? LibraryId { get; set; }
    }

    private sealed class ClaimKeyRow
    {
        public Guid EntityId { get; set; }
        public string Key { get; set; } = string.Empty;
    }

    private sealed class ParentRow
    {
        public Guid? ParentWorkId { get; set; }
    }
}
