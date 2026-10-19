using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Weekly background service that attempts to recover a Wikidata QID for
/// works that were previously matched only via retail providers
/// (<c>wikidata_status = 'missing'</c>).
///
/// On each sweep (initial delay 60 s, then every 7 days) the service:
/// <list type="number">
///   <item>Queries the <c>works</c> table for up to 100 items with <c>wikidata_status = 'missing'</c>.</item>
///   <item>For each work, reads its title and media type from <c>canonical_values</c>, then calls
///         <see cref="ISearchService.SearchUniverseAsync"/> to find a Wikidata candidate.</item>
///   <item>If the top candidate reaches confidence ≥ 0.90, applies the match:
///         writes a user-locked <c>wikidata_qid</c> claim, updates
///         <c>wikidata_status</c> to <c>'confirmed'</c>, and triggers a Universe-pass
///         hydration via <see cref="IHydrationPipelineService"/>.</item>
///   <item>Logs a <see cref="SystemActionType.UniverseMatchRecovered"/> entry to the
///         activity ledger for each recovery.</item>
///   <item>Waits 2 seconds between items to respect external API rate limits.</item>
/// </list>
/// </summary>
public sealed class MissingUniverseSweepService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ItemDelay = TimeSpan.FromSeconds(2);

    /// <summary>Cron expression for the sweep schedule. Default: 4 AM Sundays.</summary>
    private const string DefaultSchedule = "0 4 * * 0";

    private const int MaxItemsPerSweep = 100;
    private const double AutoAcceptThreshold = 0.90;

    // User-locked claim provider GUID (the provider that owns user-entered, locked claims)
    private static readonly Guid UserProviderId = WellKnownProviders.UserManual;

    private readonly IDatabaseConnection _db;
    private readonly ISearchService _search;
    private readonly IMetadataClaimRepository _claimRepo;
    private readonly ICollectionRepository _collectionRepo;
    private readonly IHydrationPipelineService _pipeline;
    private readonly ISystemActivityRepository _activityRepo;
    private readonly IConfigurationLoader _configLoader;
    private readonly ILogger<MissingUniverseSweepService> _logger;

    public MissingUniverseSweepService(
        IDatabaseConnection db,
        ISearchService search,
        IMetadataClaimRepository claimRepo,
        ICollectionRepository collectionRepo,
        IHydrationPipelineService pipeline,
        ISystemActivityRepository activityRepo,
        IConfigurationLoader configLoader,
        ILogger<MissingUniverseSweepService> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(claimRepo);
        ArgumentNullException.ThrowIfNull(collectionRepo);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(activityRepo);
        ArgumentNullException.ThrowIfNull(configLoader);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _search = search;
        _claimRepo = claimRepo;
        _collectionRepo = collectionRepo;
        _pipeline = pipeline;
        _activityRepo = activityRepo;
        _configLoader = configLoader;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "MissingUniverseSweepService started — initial delay {Seconds}s, schedule: {Schedule}",
            InitialDelay.TotalSeconds, DefaultSchedule);

        // Let the rest of the app fully start before the first sweep.
        await Task.Delay(InitialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MissingUniverseSweepService: sweep failed; will retry next cycle");
            }

            string cronSchedule;
            try
            {
                var maintenanceConfig = _configLoader.LoadMaintenance();
                cronSchedule = maintenanceConfig.Schedules.GetValueOrDefault("missing_universe_sweep", DefaultSchedule);
                if (string.IsNullOrWhiteSpace(cronSchedule))
                {
                    cronSchedule = DefaultSchedule;
                }
            }
            catch
            {
                cronSchedule = DefaultSchedule;
            }
            var delay = CronScheduler.UntilNext(cronSchedule, TimeSpan.FromDays(7));
            await Task.Delay(delay, stoppingToken);
        }
    }

    // ── Sweep logic ───────────────────────────────────────────────────────────

    private async Task RunSweepAsync(CancellationToken ct)
    {
        var candidates = LoadMissingWorks();
        if (candidates.Count == 0)
        {
            _logger.LogDebug("MissingUniverseSweepService: no 'missing' works found — nothing to do");
            return;
        }

        _logger.LogInformation(
            "MissingUniverseSweepService: processing {Count} works with wikidata_status='missing'",
            candidates.Count);

        int recovered = 0;

        foreach (var (workId, title, mediaType) in candidates)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            try
            {
                recovered += await TryRecoverWorkAsync(workId, title, mediaType, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MissingUniverseSweepService: error processing work {WorkId} ({Title})",
                    workId, title);
            }

            // Respectful delay between items.
            await Task.Delay(ItemDelay, ct);
        }

        _logger.LogInformation(
            "MissingUniverseSweepService: sweep complete — recovered {Recovered}/{Total}",
            recovered, candidates.Count);
    }

    /// <summary>
    /// Attempts to recover a Wikidata QID for one work.
    /// Returns 1 if the work was successfully recovered, 0 otherwise.
    /// </summary>
    private async Task<int> TryRecoverWorkAsync(
        Guid workId,
        string title,
        string mediaType,
        CancellationToken ct)
    {
        // Search Wikidata for candidates.
        var searchResult = await _search.SearchUniverseAsync(
            new SearchUniverseRequest(title, mediaType, MaxCandidates: 5),
            ct);

        if (searchResult.Candidates.Count == 0)
        {
            return 0;
        }

        var top = searchResult.Candidates[0];
        if (top.Confidence < AutoAcceptThreshold)
        {
            _logger.LogDebug(
                "MissingUniverseSweepService: work {WorkId} ({Title}) top candidate QID={Qid} confidence={Confidence:F2} — below threshold, skipping",
                workId, title, top.Qid, top.Confidence);
            return 0;
        }

        // Resolve the asset ID from the work ID.
        var assetId = LoadAssetIdForWork(workId);
        if (!assetId.HasValue)
        {
            _logger.LogWarning(
                "MissingUniverseSweepService: no media asset found for work {WorkId}", workId);
            return 0;
        }

        _logger.LogInformation(
            "MissingUniverseSweepService: recovering work {WorkId} ({Title}) → QID={Qid} (confidence={Confidence:F2})",
            workId, title, top.Qid, top.Confidence);

        // Write a user-locked wikidata_qid claim.
        var claim = new MetadataClaim
        {
            Id = Guid.NewGuid(),
            EntityId = assetId.Value,
            ProviderId = UserProviderId,
            ClaimKey = "wikidata_qid",
            ClaimValue = top.Qid,
            Confidence = 1.0,
            ClaimedAt = DateTimeOffset.UtcNow,
            IsUserLocked = true,
        };
        await _claimRepo.InsertBatchAsync([claim], ct);

        // Update the work's wikidata_status to 'confirmed'.
        await _collectionRepo.UpdateWorkWikidataStatusAsync(workId, WorkWikidataStatus.Confirmed, ct);

        // Trigger Universe-pass hydration so the Wikidata SPARQL pass runs.
        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wikidata_qid"] = top.Qid,
            ["title"] = title,
        };
        if (!string.IsNullOrWhiteSpace(top.Author))
        {
            hints["author"] = top.Author;
        }

        if (!string.IsNullOrWhiteSpace(top.Year))
        {
            hints["release_year"] = top.Year;
        }

        try
        {
            await _pipeline.RunSynchronousAsync(new HarvestRequest
            {
                EntityId = assetId.Value,
                EntityType = EntityType.MediaAsset,
                MediaType = MediaType.Unknown,
                Hints = hints,
                Pass = HydrationPass.Universe,
            }, ct);
        }
        catch (Exception ex)
        {
            // Hydration failure does not undo the QID claim — log and continue.
            _logger.LogWarning(ex,
                "MissingUniverseSweepService: hydration failed for work {WorkId} after QID recovery — metadata claims are intact",
                workId);
        }

        // Record the recovery in the activity ledger.
        await _activityRepo.LogAsync(new SystemActivityEntry
        {
            ActionType = SystemActionType.UniverseMatchRecovered,
            Detail = $"Recovered QID {top.Qid} for \"{title}\" (confidence {top.Confidence:P0})",
            ChangesJson = $"{{\"work_id\":\"{workId}\",\"qid\":\"{top.Qid}\",\"confidence\":{top.Confidence}}}",
        }, ct);

        return 1;
    }

    // ── Raw SQL helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns up to <see cref="MaxItemsPerSweep"/> (workId, title, mediaType) tuples
    /// for works whose <c>wikidata_status</c> is <c>'missing'</c>.
    /// Title and media type are read from the <c>canonical_values</c> table.
    /// </summary>
    private List<(Guid WorkId, string? Title, string MediaType)> LoadMissingWorks()
    {
        var results = new List<(Guid, string?, string)>();

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                w.id                                          AS work_id,
                MAX(CASE WHEN cv.key = 'title'      THEN cv.value END) AS title,
                MAX(CASE WHEN cv.key = 'media_type' THEN cv.value END) AS media_type
            FROM works w
            LEFT JOIN canonical_values cv ON cv.entity_id = (
                SELECT ma.id
                FROM editions e
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                WHERE e.work_id = w.id
                LIMIT 1
            )
            WHERE w.wikidata_status = 'missing'
            GROUP BY w.id
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", MaxItemsPerSweep);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var workId = GuidSql.FromDb(reader.GetValue(0));
            string? title = reader.IsDBNull(1) ? null : reader.GetString(1);
            string mediaType = reader.IsDBNull(2) ? "Unknown" : reader.GetString(2);
            results.Add((workId, title, mediaType));
        }

        return results;
    }

    /// <summary>
    /// Resolves the media asset ID for a given work ID.
    /// Returns <c>null</c> if no asset exists.
    /// </summary>
    private Guid? LoadAssetIdForWork(Guid workId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ma.id
            FROM editions e
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            WHERE e.work_id = @workId
            LIMIT 1
            """;
        cmd.Parameters.Add("@workId", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(workId);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : GuidSql.FromDb(result);
    }
}
