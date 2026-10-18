using System.Globalization;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
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

    public Task<TvReassignment> ReassignToTvShowSpecialAsync(
        Guid assetId,
        string showName,
        string? episodeTitle,
        CancellationToken ct = default)
        => ApplyAsync(
            assetId,
            showName,
            episodeTitle,
            TvSpecialPlacement.SeasonNumber,
            [TvSpecialPlacement.EpisodeNumber],
            move: null,
            ct);

    public Task<TvReassignment> MoveToTvShowAsync(
        TvMoveRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LibraryId);
        ArgumentNullException.ThrowIfNull(request.BuildDecision);
        ArgumentNullException.ThrowIfNull(request.EpisodeNumbers);
        if (request.SeasonNumber < 0)
        {
            throw new ArgumentException("The season number cannot be negative.", nameof(request));
        }

        if (request.EpisodeNumbers.Count == 0
            || request.EpisodeNumbers.Any(number => number < 1)
            || request.EpisodeNumbers.Distinct().Count() != request.EpisodeNumbers.Count)
        {
            throw new ArgumentException("Episode numbers must be unique and start at 1.", nameof(request));
        }

        return ApplyAsync(
            request.AssetId,
            request.ShowName,
            request.EpisodeTitle,
            request.SeasonNumber,
            request.EpisodeNumbers,
            request,
            ct);
    }

    private Task<TvReassignment> ApplyAsync(
        Guid assetId,
        string showName,
        string? episodeTitle,
        int seasonNumber,
        IReadOnlyList<int> episodeNumbers,
        TvMoveRequest? move,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(showName);
        ct.ThrowIfCancellationRequested();

        // Show -> Season -> host Episode, found or created by the same strategy ingestion uses.
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["show_name"] = showName.Trim(),
            [MetadataFieldConstants.SeasonNumber] = seasonNumber.ToString(CultureInfo.InvariantCulture),
            [MetadataFieldConstants.EpisodeNumber] = episodeNumbers[0].ToString(CultureInfo.InvariantCulture),
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

            // Any further episodes the file covers, in the same season, in episode order (host first).
            var coveredWorkIds = new List<Guid>(episodeNumbers.Count) { resolved.WorkId };
            foreach (var episode in episodeNumbers.Skip(1))
            {
                coveredWorkIds.Add(WorkRepository.GetOrCreateChild(
                    connection, transaction, MediaType.TV, seasonWorkId, episode, episode));
            }

            // Every covered episode, the season and the show now hold an owned file.
            foreach (var workId in coveredWorkIds.Append(seasonWorkId).Append(showWorkId))
            {
                MarkWorkOwned(connection, transaction, workId);
            }

            var removed = moved && RemoveWorkIfEmpty(connection, transaction, current.WorkId, resolved.WorkId);
            var placement = new TvReassignment(
                assetId, current.EditionId, current.WorkId, removed, showWorkId, seasonWorkId, resolved.WorkId,
                coveredWorkIds);

            if (move is not null)
            {
                RecordCoverage(connection, transaction, assetId, coveredWorkIds, innerCt);
                ApplyMove(connection, transaction, current, placement, move);
            }

            return placement;
        }, ct);
    }

    /// <summary>
    /// A person's decision that the file covers several episodes is stored as manual coverage, one row per
    /// episode in episode order, so the identity re-run (which only replaces filename coverage) keeps it.
    /// A single-episode file leaves no coverage rows.
    /// </summary>
    private static void RecordCoverage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        IReadOnlyList<Guid> coveredWorkIds,
        CancellationToken ct)
    {
        var rows = coveredWorkIds.Count == 1
            ? []
            : coveredWorkIds
                .Select((workId, index) => new MediaAssetCoverage(
                    assetId, workId, index + 1, null, null, MediaAssetCoverage.SourceManual))
                .ToList();
        MediaAssetCoverageRepository.ReplaceInTransaction(connection, transaction, assetId, rows, ct);
    }

    /// <summary>
    /// The rest of the "Move to TV" decision, on the same transaction as the re-filing: library
    /// membership, the user's locked claims and values, the show's bridge id, and the review item.
    /// </summary>
    private static void ApplyMove(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlacementRow current,
        TvReassignment placement,
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
    /// The Work's curated-collection memberships move to <paramref name="replacementWorkId"/> first, so a
    /// collection that held the movie now holds the episode instead of being emptied and removed.
    /// </summary>
    private static bool RemoveWorkIfEmpty(
        SqliteConnection connection, SqliteTransaction transaction, Guid workId, Guid replacementWorkId)
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

        // Memberships follow the file to its new work. collection_items has no unique (collection, work) key,
        // so rows in a collection that already holds the replacement are dropped first, then the rest re-point.
        connection.Execute("""
            DELETE FROM collection_items
            WHERE work_id = @workId
              AND collection_id IN (SELECT collection_id FROM collection_items WHERE work_id = @replacementWorkId);
            """, new { workId, replacementWorkId }, transaction);
        connection.Execute(
            "UPDATE collection_items SET work_id = @replacementWorkId WHERE work_id = @workId;",
            new { workId, replacementWorkId }, transaction);

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

        // The deleted work's collection_items rows went with it; a curated collection it was the
        // last item of goes too.
        CuratedCollectionCleanup.DeleteEmpty(connection, transaction);
        return true;
    }
}
