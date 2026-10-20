using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Config-controlled Stage 3 music retry/backfill pass. Stage 1 identity still
/// follows <c>config/pipelines.json</c>; this service only revisits visible music
/// items that are missing MusicBrainz identifiers.
/// </summary>
public sealed class MusicBrainzEnrichmentHostedService : BackgroundService
{
    private const int BatchSize = 25;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan PerLookupDelay = TimeSpan.FromSeconds(1);

    private readonly IDatabaseConnection _db;
    private readonly IEnumerable<IExternalMetadataProvider> _providers;
    private readonly IConfigurationLoader _configLoader;
    private readonly IWorkRepository _workRepo;
    private readonly IBridgeIdRepository _bridgeIdRepo;
    private readonly IMetadataClaimRepository _claimRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly ICanonicalValueArrayRepository _arrayRepo;
    private readonly IScoringEngine _scoringEngine;
    private readonly CoverArtWorker _coverArtWorker;
    private readonly EnrichmentPipelineExecutionGate _executionGate;
    private readonly ILogger<MusicBrainzEnrichmentHostedService> _logger;

    public MusicBrainzEnrichmentHostedService(
        IDatabaseConnection db,
        IEnumerable<IExternalMetadataProvider> providers,
        IConfigurationLoader configLoader,
        IWorkRepository workRepo,
        IBridgeIdRepository bridgeIdRepo,
        IMetadataClaimRepository claimRepo,
        ICanonicalValueRepository canonicalRepo,
        ICanonicalValueArrayRepository arrayRepo,
        IScoringEngine scoringEngine,
        CoverArtWorker coverArtWorker,
        EnrichmentPipelineExecutionGate executionGate,
        ILogger<MusicBrainzEnrichmentHostedService> logger)
    {
        _db = db;
        _providers = providers;
        _configLoader = configLoader;
        _workRepo = workRepo;
        _bridgeIdRepo = bridgeIdRepo;
        _claimRepo = claimRepo;
        _canonicalRepo = canonicalRepo;
        _arrayRepo = arrayRepo;
        _scoringEngine = scoringEngine;
        _coverArtWorker = coverArtWorker;
        _executionGate = executionGate;
        _logger = logger;
    }

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
            CancellationToken resetCancellationToken = default;
            try
            {
                using var executionLease =
                    await _executionGate.EnterAsync(nameof(MusicBrainzEnrichmentHostedService), stoppingToken).ConfigureAwait(false);
                resetCancellationToken = executionLease.PauseCancellationToken;
                using var sweepCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken,
                    resetCancellationToken);
                {
                    await RunSweepAsync(sweepCancellation.Token).ConfigureAwait(false);
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
                _logger.LogWarning(ex, "MusicBrainz enrichment sweep failed; music items remain visible and will be retried later");
            }

            if (interruptedByReset)
            {
                continue;
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task RunSweepAsync(CancellationToken ct)
    {
        var providerConfigs = _configLoader.LoadAllProviders();
        var providerConfig = providerConfigs.FirstOrDefault(config =>
            string.Equals(config.Name, "musicbrainz", StringComparison.OrdinalIgnoreCase));
        if (providerConfig?.HydrationStages.Contains(3) != true)
        {
            _logger.LogDebug("MusicBrainz Stage 3 backfill skipped because config/providers/musicbrainz.json does not include hydration stage 3");
            return;
        }

        var provider = ProviderExecutionFilter.FindEnabledProvider(_providers, providerConfigs, "musicbrainz");
        if (provider is null)
        {
            _logger.LogDebug("MusicBrainz enrichment skipped because the provider is disabled or not registered");
            return;
        }

        var candidates = await LoadCandidatesAsync(ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await EnrichOneAsync(provider, candidate, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MusicBrainz enrichment failed for asset {AssetId}; it will be retried by a later sweep", candidate.AssetId);
            }

            await Task.Delay(PerLookupDelay, ct).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<MusicBrainzCandidate>> LoadCandidatesAsync(CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<MusicBrainzCandidate>(new CommandDefinition(
            """
            SELECT a.id AS AssetId,
                   w.id AS WorkId,
                   COALESCE(gp.id, p.id, w.id) AS RootWorkId,
                   COALESCE(
                       NULLIF((SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = a.id AND key = 'title' LIMIT 1), ''),
                       NULLIF((SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = w.id AND key = 'title' LIMIT 1), '')
                   ) AS Title,
                   COALESCE(
                       NULLIF((SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = COALESCE(gp.id, p.id, w.id) AND key = 'album' LIMIT 1), ''),
                       NULLIF((SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = w.id AND key = 'album' LIMIT 1), '')
                   ) AS Album,
                   COALESCE(
                       NULLIF((SELECT value FROM canonical_value_arrays WHERE entity_id = a.id AND key IN ('artist', 'album_artist') ORDER BY ordinal LIMIT 1), ''),
                       NULLIF((SELECT value FROM canonical_value_arrays WHERE entity_id = w.id AND key IN ('artist', 'album_artist') ORDER BY ordinal LIMIT 1), ''),
                       NULLIF((SELECT value FROM canonical_value_arrays WHERE entity_id = COALESCE(gp.id, p.id, w.id) AND key IN ('album_artist', 'artist', 'author') ORDER BY ordinal LIMIT 1), '')
                   ) AS Artist,
                   COALESCE(
                       NULLIF((SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = a.id AND key = 'track_number' LIMIT 1), ''),
                       NULLIF((SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = w.id AND key = 'track_number' LIMIT 1), '')
                   ) AS TrackNumber,
                   COALESCE(
                       NULLIF((SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = a.id AND key = 'year' LIMIT 1), ''),
                       NULLIF((SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = w.id AND key = 'year' LIMIT 1), ''),
                       NULLIF((SELECT CAST(value AS TEXT) FROM canonical_values WHERE entity_id = COALESCE(gp.id, p.id, w.id) AND key = 'year' LIMIT 1), '')
                   ) AS Year
            FROM media_assets a
            JOIN editions e ON e.id = a.edition_id
            JOIN works w ON w.id = e.work_id
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE LOWER(w.media_type) = 'music'
              AND a.presented_at IS NOT NULL
              AND COALESCE(w.curator_state, '') NOT IN ('rejected', 'provisional')
              AND COALESCE(w.is_catalog_only, 0) = 0
              AND COALESCE(a.file_path_root, '') NOT LIKE '%/.data/staging/%'
              AND COALESCE(a.file_path_root, '') NOT LIKE '%\.data\staging\%'
              AND COALESCE(a.file_path_root, '') NOT LIKE '%/quarantine/%'
              AND COALESCE(a.file_path_root, '') NOT LIKE '%\quarantine\%'
              AND (
                    NOT EXISTS (
                        SELECT 1 FROM canonical_values mb
                        WHERE mb.entity_id = COALESCE(gp.id, p.id, w.id)
                          AND mb.key = 'musicbrainz_release_group_id'
                          AND NULLIF(CAST(mb.value AS TEXT), '') IS NOT NULL
                    )
                    OR NOT EXISTS (
                        SELECT 1 FROM canonical_values mb
                        WHERE mb.entity_id IN (a.id, w.id)
                          AND mb.key = 'musicbrainz_recording_id'
                          AND NULLIF(CAST(mb.value AS TEXT), '') IS NOT NULL
                    )
                  )
            ORDER BY RootWorkId, CAST(COALESCE(TrackNumber, '9999') AS INTEGER), Title
            LIMIT @limit;
            """,
            new { limit = BatchSize },
            cancellationToken: ct)).ConfigureAwait(false);

        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.Title))
            .ToList();
    }

    private async Task EnrichOneAsync(
        IExternalMetadataProvider provider,
        MusicBrainzCandidate candidate,
        CancellationToken ct)
    {
        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddHint(hints, MetadataFieldConstants.Title, candidate.Title);
        AddHint(hints, MetadataFieldConstants.Album, candidate.Album);
        AddHint(hints, MetadataFieldConstants.Artist, candidate.Artist);
        AddHint(hints, MetadataFieldConstants.TrackNumber, candidate.TrackNumber);
        AddHint(hints, MetadataFieldConstants.Year, candidate.Year);

        var request = new ProviderLookupRequest
        {
            EntityId = candidate.AssetId,
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Music,
            Title = candidate.Title,
            Album = candidate.Album,
            Artist = candidate.Artist,
            TrackNumber = candidate.TrackNumber,
            Year = candidate.Year,
            Hints = hints,
        };

        var claims = await provider.FetchAsync(request, ct).ConfigureAwait(false);
        if (claims.Count == 0)
        {
            return;
        }

        var lineage = await _workRepo.GetLineageByAssetAsync(candidate.AssetId, ct).ConfigureAwait(false);
        await ScoringHelper.PersistAndScoreWithLineageAsync(
            candidate.AssetId,
            claims,
            provider.ProviderId,
            lineage,
            _claimRepo,
            _canonicalRepo,
            _scoringEngine,
            _configLoader,
            _providers,
            ct,
            arrayRepo: _arrayRepo,
            logger: _logger).ConfigureAwait(false);

        var bridgeEntries = claims
            .Where(claim => BridgeIdHelper.IsBridgeId(claim.Key) && !string.IsNullOrWhiteSpace(claim.Value))
            .Select(claim => new BridgeIdEntry
            {
                EntityId = ResolveBridgeIdEntityId(lineage, candidate.AssetId, claim.Key),
                IdType = claim.Key,
                IdValue = claim.Value,
                ProviderId = provider.ProviderId.ToString(),
            })
            .ToList();

        if (bridgeEntries.Count > 0)
        {
            await _bridgeIdRepo.UpsertBatchAsync(bridgeEntries, ct).ConfigureAwait(false);
        }

        if (claims.Any(claim =>
                string.Equals(claim.Key, MetadataFieldConstants.CoverUrl, StringComparison.OrdinalIgnoreCase)))
        {
            await _coverArtWorker.DownloadAndPersistAsync(candidate.AssetId, wikidataQid: null, ct)
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "MusicBrainz enriched '{Title}' by {Artist} with {ClaimCount} claim(s)",
            candidate.Title,
            candidate.Artist ?? "unknown artist",
            claims.Count);
    }

    private static Guid ResolveBridgeIdEntityId(WorkLineage? lineage, Guid assetId, string key)
    {
        if (lineage is null)
        {
            return assetId;
        }

        return ClaimScopeCatalog.IsParentScoped(key, lineage.MediaType)
            ? lineage.TargetForParentScope
            : lineage.TargetForSelfScope;
    }

    private static void AddHint(IDictionary<string, string> hints, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            hints[key] = value.Trim();
        }
    }

    private sealed class MusicBrainzCandidate
    {
        public Guid AssetId { get; set; }
        public Guid WorkId { get; set; }
        public Guid RootWorkId { get; set; }
        public string? Title { get; set; }
        public string? Album { get; set; }
        public string? Artist { get; set; }
        public string? TrackNumber { get; set; }
        public string? Year { get; set; }
    }
}
