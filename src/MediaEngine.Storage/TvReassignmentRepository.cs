using System.Globalization;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Services;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="ITvReassignmentRepository"/>. Only data-store rows change:
/// the Edition that holds the asset is re-parented under a TV episode Work, so the media asset's
/// file path and the file itself are never touched. Every step runs in one write transaction on one
/// connection, so a failure leaves no orphan Show/Season/Episode Works and the review item pending.
/// </summary>
public sealed class TvReassignmentRepository(IDatabaseConnection database, HierarchyResolver resolver)
    : ITvReassignmentRepository
{
    private sealed class PlacementRow
    {
        public Guid EditionId { get; set; }
        public Guid WorkId { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string? LibraryId { get; set; }
    }

    private sealed class WorkStateRow
    {
        public string WorkKind { get; set; } = string.Empty;
        public bool IsCatalogOnly { get; set; }
    }

    public Task<TvSpecialReassignment> ReassignToTvShowSpecialAsync(
        Guid assetId,
        string showName,
        string? episodeTitle,
        CancellationToken ct = default)
        => ApplyAsync(assetId, showName, episodeTitle, move: null, ct);

    public Task<TvSpecialReassignment> MoveToTvShowSpecialAsync(
        TvMoveRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LibraryId);
        ArgumentNullException.ThrowIfNull(request.BuildDecision);
        return ApplyAsync(request.AssetId, request.ShowName, request.EpisodeTitle, request, ct);
    }

    private Task<TvSpecialReassignment> ApplyAsync(
        Guid assetId,
        string showName,
        string? episodeTitle,
        TvMoveRequest? move,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(showName);
        ct.ThrowIfCancellationRequested();

        // Show -> Season 0 -> Episode 1, found or created by the same strategy ingestion uses.
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["show_name"] = showName.Trim(),
            [MetadataFieldConstants.SeasonNumber] = TvSpecialPlacement.SeasonNumber.ToString(CultureInfo.InvariantCulture),
            [MetadataFieldConstants.EpisodeNumber] = TvSpecialPlacement.EpisodeNumber.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(episodeTitle))
        {
            metadata["title"] = episodeTitle.Trim();
        }

        return database.ExecuteWriteAsync((connection, transaction, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();

            var current = ReadPlacement(connection, transaction, assetId)
                ?? throw new InvalidOperationException($"Media asset {assetId} was not found.");

            if (!string.Equals(current.MediaType, nameof(MediaType.Movies), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(current.MediaType, nameof(MediaType.TV), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Only a Movies asset can be filed as a TV special; this one is {current.MediaType}.");
            }

            var resolved = resolver.ResolveTvInTransaction(connection, transaction, metadata);
            if (resolved.ParentWorkId is not { } seasonWorkId)
            {
                throw new InvalidOperationException("The TV hierarchy for the show could not be resolved.");
            }

            var showWorkId = connection.QuerySingle<Guid>(
                "SELECT parent_work_id FROM works WHERE id = @seasonWorkId;",
                new { seasonWorkId }, transaction);

            // The Edition moves through its aggregate method; the guarded UPDATE persists it.
            var edition = new Edition { Id = current.EditionId, WorkId = current.WorkId };
            var moved = edition.MoveToWork(resolved.WorkId);
            if (moved && connection.Execute("""
                    UPDATE editions SET work_id = @target
                    WHERE id = @editionId AND work_id = @source;
                    """, new { target = edition.WorkId, editionId = edition.Id, source = current.WorkId }, transaction) != 1)
            {
                throw new InvalidOperationException("The asset's edition changed while it was being re-filed.");
            }

            // The episode, its season and the show now hold an owned file.
            foreach (var workId in new[] { resolved.WorkId, seasonWorkId, showWorkId })
            {
                MarkWorkOwned(connection, transaction, workId);
            }

            var removed = moved && RemoveWorkIfEmpty(connection, transaction, current.WorkId);
            var placement = new TvSpecialReassignment(
                assetId, current.EditionId, current.WorkId, removed, showWorkId, seasonWorkId, resolved.WorkId);

            if (move is not null)
            {
                ApplyMove(connection, transaction, current, placement, move);
            }

            return placement;
        }, ct);
    }

    /// <summary>
    /// The rest of the "Move to TV" decision, on the same transaction as the re-filing: library
    /// membership, the user's locked claims and values, the show's bridge id, and the review item.
    /// </summary>
    private static void ApplyMove(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlacementRow current,
        TvSpecialReassignment placement,
        TvMoveRequest move)
    {
        // Library membership is data only; the media asset's file path is not touched.
        var asset = new MediaAsset { Id = move.AssetId, LibraryId = current.LibraryId };
        asset.AssignToLibrary(move.LibraryId);
        MediaAssetRepository.SetLibraryId(connection, transaction, asset.Id, asset.LibraryId);

        var decision = move.BuildDecision(placement);
        MetadataClaimRepository.InsertBatchInTransaction(connection, transaction, decision.Claims);
        CanonicalValueRepository.UpsertBatchInTransaction(connection, transaction, decision.Values);
        BridgeIdRepository.UpsertBatchInTransaction(connection, transaction, decision.BridgeIds);

        if (!ReviewQueueRepository.ResolvePendingInTransaction(
                connection, transaction, move.ReviewItemId, ReviewStatus.Resolved, move.ResolvedBy))
        {
            throw new InvalidOperationException("The review item is no longer pending.");
        }
    }

    private static PlacementRow? ReadPlacement(SqliteConnection connection, SqliteTransaction transaction, Guid assetId) =>
        connection.QueryFirstOrDefault<PlacementRow>("""
            SELECT a.edition_id AS EditionId, e.work_id AS WorkId, w.media_type AS MediaType, a.library_id AS LibraryId
            FROM media_assets a
            JOIN editions e ON e.id = a.edition_id
            JOIN works w ON w.id = e.work_id
            WHERE a.id = @assetId;
            """, new { assetId }, transaction);

    /// <summary>
    /// Hydrates the Work's ownership state explicitly, applies <see cref="Work.MarkOwned"/>, and
    /// persists the result.
    /// </summary>
    private static void MarkWorkOwned(SqliteConnection connection, SqliteTransaction transaction, Guid workId)
    {
        var row = connection.QueryFirstOrDefault<WorkStateRow>(
            "SELECT work_kind AS WorkKind, is_catalog_only AS IsCatalogOnly FROM works WHERE id = @workId;",
            new { workId }, transaction)
            ?? throw new InvalidOperationException($"Work {workId} was not found.");

        var work = new Work
        {
            Id = workId,
            WorkKind = AggregateStateSerializer.ParseWorkKind(row.WorkKind),
            IsCatalogOnly = row.IsCatalogOnly,
        };
        work.MarkOwned();

        connection.Execute("""
            UPDATE works SET ownership = @ownership, is_catalog_only = @isCatalogOnly, work_kind = @workKind
            WHERE id = @workId;
            """, new
        {
            workId,
            ownership = work.Ownership.ToStorageValue(),
            isCatalogOnly = work.IsCatalogOnly ? 1 : 0,
            workKind = work.WorkKind.ToStorageValue(),
        }, transaction);
    }

    /// <summary>
    /// Deletes a standalone Work that no longer holds any Edition, with the entity-keyed rows that
    /// described only that Work. Parent containers and Works that still hold an Edition are kept.
    /// </summary>
    private static bool RemoveWorkIfEmpty(SqliteConnection connection, SqliteTransaction transaction, Guid workId)
    {
        var removable = connection.ExecuteScalar<int>("""
            SELECT EXISTS (
                SELECT 1 FROM works w
                WHERE w.id = @workId
                  AND w.work_kind = 'standalone'
                  AND NOT EXISTS (SELECT 1 FROM editions e WHERE e.work_id = w.id)
                  AND NOT EXISTS (SELECT 1 FROM works c WHERE c.parent_work_id = w.id));
            """, new { workId }, transaction) == 1;
        if (!removable)
        {
            return false;
        }

        foreach (var statement in new[]
                 {
                     "DELETE FROM entity_assets WHERE entity_id = @workId;",
                     "DELETE FROM canonical_values WHERE entity_id = @workId;",
                     "DELETE FROM canonical_value_arrays WHERE entity_id = @workId;",
                     "DELETE FROM metadata_claims WHERE entity_id = @workId;",
                     "DELETE FROM bridge_ids WHERE entity_id = @workId;",
                     "DELETE FROM review_queue WHERE entity_id = @workId;",
                     "DELETE FROM works WHERE id = @workId;",
                 })
        {
            connection.Execute(statement, new { workId }, transaction);
        }

        return true;
    }
}
