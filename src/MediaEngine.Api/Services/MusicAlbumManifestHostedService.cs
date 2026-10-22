using Dapper;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Repairs music album roots created before ingestion persisted provider-scoped
/// track manifests. New ingestion writes manifests directly; this sweep handles
/// already-existing albums and transient provider failures.
/// </summary>
public sealed class MusicAlbumManifestHostedService(
    IDatabaseConnection db,
    ICanonicalValueRepository canonicalRepo,
    AlbumTrackManifestService manifestService,
    CoverArtWorker coverArtWorker,
    EnrichmentPipelineExecutionGate executionGate,
    ILogger<MusicAlbumManifestHostedService> logger,
    IExternalLookupGuard? lookupGuard = null) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan IncompleteRetryInterval = TimeSpan.FromMinutes(5);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interruptedByReset = false;
            var allComplete = false;
            CancellationToken resetCancellationToken = default;
            try
            {
                using var executionLease =
                    await executionGate.EnterAsync(nameof(MusicAlbumManifestHostedService), stoppingToken).ConfigureAwait(false);
                resetCancellationToken = executionLease.PauseCancellationToken;
                using var sweepCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken,
                    resetCancellationToken);
                {
                    allComplete = await RunSweepAsync(sweepCancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (resetCancellationToken.IsCancellationRequested)
            {
                interruptedByReset = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Music album manifest repair sweep failed; incomplete albums will be retried");
            }

            if (interruptedByReset)
            {
                continue;
            }

            try
            {
                await Task.Delay(
                    allComplete ? SweepInterval : IncompleteRetryInterval,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<bool> RunSweepAsync(CancellationToken ct)
    {
        var candidates = LoadCandidates(ct);
        var allComplete = true;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (await lookupGuard.RefusesAsync(candidate.AssetId, logger, "Album track list lookup", ct).ConfigureAwait(false))
                {
                    continue;
                }

                var canonicalValues = await canonicalRepo.GetByEntityAsync(candidate.RootWorkId, ct)
                    .ConfigureAwait(false);
                var manifest = await manifestService.EnsureAlbumTrackManifestAsync(
                        candidate.RootWorkId,
                        candidate.Artist,
                        candidate.Album,
                        candidate.ChildEntitiesJson,
                        canonicalValues,
                        ct)
                    .ConfigureAwait(false);

                if (!MusicAlbumManifestJson.IsComplete(manifest))
                {
                    allComplete = false;
                    continue;
                }

                await coverArtWorker.DownloadAndPersistAsync(candidate.AssetId, wikidataQid: null, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Music album manifest repair failed for root work {RootWorkId}; it will be retried",
                    candidate.RootWorkId);
                allComplete = false;
            }
        }

        return allComplete;
    }

    internal IReadOnlyList<MusicAlbumManifestCandidate> LoadCandidates(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Albums whose files sit in a library that never looks anything up (Other audio) are left out of the selection itself:
        // no track list is asked of MusicBrainz and no cover is downloaded for them (Library setup D38).
        var blockedLibraryIds = lookupGuard?.GetBlockedLibraryIds().Select(id => id.ToLowerInvariant()).ToArray() ?? [];
        using var connection = db.CreateConnection();
        return connection.Query<MusicAlbumManifestCandidate>(new CommandDefinition(
            """
            SELECT parent.id AS RootWorkId,
                   MIN(asset.id) AS AssetId,
                   COALESCE(
                       NULLIF((SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = parent.id AND key = 'album' LIMIT 1), ''),
                       NULLIF((SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = parent.id AND key = 'title' LIMIT 1), ''),
                       NULLIF((SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id IN (child.id, asset.id) AND key = 'album' LIMIT 1), '')
                   ) AS Album,
                   COALESCE(
                       NULLIF((SELECT value FROM canonical_value_arrays WHERE entity_id = parent.id AND key IN ('album_artist', 'artist', 'author') ORDER BY ordinal LIMIT 1), ''),
                       NULLIF((SELECT value FROM canonical_value_arrays WHERE entity_id IN (child.id, asset.id) AND key IN ('album_artist', 'artist', 'author') ORDER BY ordinal LIMIT 1), '')
                   ) AS Artist,
                   (SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = parent.id AND key = 'child_entities_json' LIMIT 1) AS ChildEntitiesJson
            FROM works parent
            INNER JOIN works child ON child.parent_work_id = parent.id
            INNER JOIN editions edition ON edition.work_id = child.id
            INNER JOIN media_assets asset ON asset.edition_id = edition.id
            WHERE LOWER(parent.media_type) = 'music'
              AND parent.work_kind = 'parent'
              AND COALESCE(parent.curator_state, '') NOT IN ('rejected', 'provisional')
              AND COALESCE(parent.is_catalog_only, 0) = 0
              AND asset.presented_at IS NOT NULL
              AND (asset.library_id IS NULL OR lower(asset.library_id) NOT IN @blockedLibraryIds)
              -- An album whose tracks are still being identified belongs to that work: repairing it now
              -- would download whichever cover the first finished track left, not the album's settled one.
              AND NOT EXISTS (
                  SELECT 1
                  FROM works busy_child
                  INNER JOIN editions busy_edition ON busy_edition.work_id = busy_child.id
                  INNER JOIN media_assets busy_asset ON busy_asset.edition_id = busy_edition.id
                  INNER JOIN identity_jobs busy_job ON busy_job.entity_id = busy_asset.id
                  WHERE busy_child.parent_work_id = parent.id
                    AND busy_job.state NOT IN ('Ready', 'ReadyWithoutUniverse', 'Failed', 'RetailNoMatch', 'QidNoMatch', 'QidNeedsReview'))
            GROUP BY parent.id
            ORDER BY parent.id
            LIMIT @BatchSize
            """,
            new { BatchSize, blockedLibraryIds },
            cancellationToken: ct)).AsList();
    }

    internal sealed class MusicAlbumManifestCandidate
    {
        public Guid RootWorkId { get; init; }
        public Guid AssetId { get; init; }
        public string? Album { get; init; }
        public string? Artist { get; init; }
        public string? ChildEntitiesJson { get; init; }
    }
}
