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
    ILogger<MusicAlbumManifestHostedService> logger) : BackgroundService
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

    private IReadOnlyList<MusicAlbumManifestCandidate> LoadCandidates(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
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
            GROUP BY parent.id
            ORDER BY parent.id
            LIMIT @BatchSize
            """,
            new { BatchSize },
            cancellationToken: ct)).AsList();
    }

    private sealed class MusicAlbumManifestCandidate
    {
        public Guid RootWorkId { get; init; }
        public Guid AssetId { get; init; }
        public string? Album { get; init; }
        public string? Artist { get; init; }
        public string? ChildEntitiesJson { get; init; }
    }
}
