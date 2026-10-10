using System.Globalization;
using Dapper;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Paging;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Converts durable ingestion records into media-centric administration models.
/// Pipeline vocabulary is intentionally contained here rather than leaked into Razor.
/// </summary>
public sealed class IngestionPresentationReadService : IIngestionPresentationReadService
{
    private static readonly string[] ActiveStatuses =
        ["pending", "queued", "leased", "running", "processing", "active", "retry_waiting", "failed_retryable", "interrupted"];

    private readonly IDatabaseConnection _db;
    private readonly MediaEngine.Providers.Services.BatchProgressService? _progress;

    public IngestionPresentationReadService(IDatabaseConnection db, MediaEngine.Providers.Services.BatchProgressService? progress = null)
    {
        _db = db;
        _progress = progress;
    }

    public async Task<IngestionPresentationSnapshotDto> GetSnapshotAsync(
        int currentLimit = 8,
        int recentDayLimit = 3,
        int recentItemsPerDay = 9,
        CancellationToken ct = default)
    {
        currentLimit = Math.Clamp(currentLimit, 1, 20);
        recentDayLimit = Math.Clamp(recentDayLimit, 1, 7);
        recentItemsPerDay = Math.Clamp(recentItemsPerDay, 1, 12);

        var currentPage = await LoadCurrentGroupPageAsync(PagedRequest.From(0, currentLimit, currentLimit, 20), ct, eligibleOnly: true).ConfigureAwait(false);
        var current = currentPage.Items;
        var currentFacts = await ReadCurrentGroupFactsAsync(ct).ConfigureAwait(false);
        var batchFacts = await ReadCurrentBatchFactsAsync(ct).ConfigureAwait(false);
        var recentDays = await LoadRecentDaysAsync(
            recentDayLimit,
            recentItemsPerDay,
            batchFacts.IsRunning ? batchFacts.BatchId : null,
            ct).ConfigureAwait(false);
        var operationFacts = await ReadCurrentOperationFactsAsync(ct).ConfigureAwait(false);
        var reviewCount = await ReadPendingReviewCountAsync(ct).ConfigureAwait(false);

        var attention = new List<IngestionAttentionItemDto>();
        if (reviewCount > 0)
        {
            attention.Add(new IngestionAttentionItemDto
            {
                Kind = "review",
                Count = reviewCount,
                Label = $"{reviewCount:N0} {Pluralize("title", reviewCount)} need review",
                Description = "Missing or uncertain metadata",
                Route = "/settings/recently-added?review=expanded",
            });
        }

        if (operationFacts.RetryWaiting > 0)
        {
            attention.Add(new IngestionAttentionItemDto
            {
                Kind = "provider",
                Count = operationFacts.RetryWaiting,
                Label = $"{operationFacts.RetryWaiting:N0} {Pluralize("item", operationFacts.RetryWaiting)} waiting on provider data",
                Description = "Tuvima will retry automatically",
                Route = batchFacts.BatchId is { } batchId ? $"/settings/ingestion?runId={batchId:D}&view=all" : "/settings/ingestion",
            });
        }

        if (operationFacts.TextTrackWaiting > 0)
        {
            attention.Add(new IngestionAttentionItemDto
            {
                Kind = "textTracks",
                Count = operationFacts.TextTrackWaiting,
                Label = $"{operationFacts.TextTrackWaiting:N0} lyrics or subtitles in queue",
                Description = "Fetching from providers",
                Route = batchFacts.BatchId is { } batchId ? $"/settings/ingestion?runId={batchId:D}&view=all" : "/settings/ingestion",
            });
        }

        var isRunning = batchFacts.IsRunning || operationFacts.Active + operationFacts.Queued + operationFacts.RetryWaiting > 0;
        return new IngestionPresentationSnapshotDto
        {
            BatchProgress = batchFacts.BatchId is { } activeBatchId && _progress is not null
                ? await _progress.GetProgressAsync(activeBatchId, ct).ConfigureAwait(false) : null,
            IsRunning = isRunning,
            Status = isRunning ? "active" : "idle",
            FilesDiscovered = batchFacts.FilesTotal,
            FilesProcessed = Math.Min(batchFacts.FilesProcessed, Math.Max(batchFacts.FilesTotal, batchFacts.FilesProcessed)),
            LibraryGroups = currentFacts.TotalGroups,
            ReadyGroups = currentFacts.ReadyGroups,
            FinishingGroups = currentFacts.FinishingGroups,
            ReviewGroups = currentFacts.ReviewGroups,
            ActiveOperations = operationFacts.Active,
            QueuedOperations = operationFacts.Queued,
            RetryWaitingOperations = operationFacts.RetryWaiting,
            StartedAt = batchFacts.StartedAt,
            LastActivityAt = batchFacts.LastActivityAt ?? recentDays.FirstOrDefault()?.Items.FirstOrDefault()?.AddedAt,
            CurrentMedia = current.ToList(),
            CurrentMediaTotal = currentPage.TotalCount ?? current.Count,
            Attention = attention,
            RecentDays = recentDays,
            GeneratedAt = DateTimeOffset.UtcNow,
        };
    }

    public async Task<PagedResponse<IngestionMediaGroupDto>> GetCurrentMediaAsync(
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        var request = PagedRequest.From(offset, limit, 50, 100);
        return await LoadCurrentGroupPageAsync(request, ct, eligibleOnly: false).ConfigureAwait(false);
    }

    public async Task<PagedResponse<IngestionMediaGroupDto>> GetRecentAdditionsAsync(
        string? search,
        string? lane,
        DateTimeOffset? start,
        DateTimeOffset? end,
        int offset,
        int limit,
        string? sort = null,
        CancellationToken ct = default)
    {
        var request = PagedRequest.From(offset, limit, 50, 100);
        return await LoadHistoryGroupPageAsync(search, lane, start, end, sort, request, ct).ConfigureAwait(false);
    }

    public async Task<IngestionMediaGroupDto?> GetMediaGroupAsync(
        Guid batchId,
        Guid groupId,
        CancellationToken ct = default)
    {
        var groups = await LoadGroupsAsync(GroupScope.Batch, batchId, ct, groupIds: [groupId]).ConfigureAwait(false);
        ApplyArtworkSize(groups, "m");
        if (await IsHistoricalBatchAsync(batchId, ct).ConfigureAwait(false))
        {
            ApplyHistoricalProgress(groups);
        }

        return groups.FirstOrDefault(item => item.GroupId == groupId);
    }

    public async Task<PagedResponse<IngestionMediaChildDto>> GetChildrenAsync(
        Guid batchId,
        Guid groupId,
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        var request = PagedRequest.From(offset, limit, 50, 250);
        var rows = await LoadRowsAsync(GroupScope.Batch, batchId, ct, groupIds: [groupId]).ConfigureAwait(false);
        var children = rows
            .Where(row => PresentationGroupId(row) == groupId)
            .GroupBy(row => row.WorkId)
            .Select(group =>
            {
                var row = group.OrderByDescending(item => item.UpdatedAt).First();
                return new IngestionMediaChildDto
                {
                    Id = row.WorkId,
                    Title = MediaEngine.Domain.Services.StringHelpers
                        .FirstNonBlankOr("Untitled", row.LeafTitle, row.DetectedTitle)
                        .Trim(),
                    SequenceLabel = BuildSequenceLabel(row),
                    Status = row.ReviewCount > 0
                        ? "review"
                        : row.PresentedAt.HasValue ? "complete" : FriendlyOperationState(row.OperationStatus),
                    DurationLabel = FormatChildDuration(row.MediaType, row.DurationLabel),
                };
            })
            .OrderBy(item => SequenceSort(item.SequenceLabel))
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Page(children, request);
    }

    public async Task<ActivityHistorySummaryDto> GetActivitySummaryAsync(CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleAsync<ActivitySummaryRow>(new CommandDefinition("""
            SELECT
                COUNT(CASE WHEN status = 'completed' AND completed_at >= @weekStart THEN 1 END) AS CompletedRunsThisWeek,
                MAX(COALESCE(completed_at, updated_at, started_at)) AS LastActivityAt
            FROM ingestion_batches;
            """, new { weekStart = StartOfWeek(DateTimeOffset.UtcNow).ToString("O") }, cancellationToken: ct)).ConfigureAwait(false);

        var latestBatch = await conn.QueryFirstOrDefaultAsync<LatestBatchRow>(new CommandDefinition("""
            SELECT files_processed AS FilesProcessed
            FROM ingestion_batches
            WHERE status = 'completed'
            ORDER BY COALESCE(completed_at, updated_at, started_at) DESC
            LIMIT 1;
            """, cancellationToken: ct)).ConfigureAwait(false);
        var pending = await ReadPendingReviewCountAsync(ct).ConfigureAwait(false);
        var localToday = DateTime.Today;
        var todayStart = new DateTimeOffset(localToday, TimeZoneInfo.Local.GetUtcOffset(localToday)).ToUniversalTime();
        var tomorrowStart = todayStart.AddDays(1);
        var history = await ReadHistoryAggregateFactsAsync(conn, todayStart, tomorrowStart, ct).ConfigureAwait(false);

        return new ActivityHistorySummaryDto
        {
            CompletedRunsThisWeek = row.CompletedRunsThisWeek,
            ItemsAddedToday = history.ItemsAddedToday,
            ItemsNeedingFollowUp = pending,
            LastActivityAt = row.LastActivityAt,
            LastFilesProcessed = latestBatch?.FilesProcessed,
            LastGroupsAdded = history.LastGroupsAdded,
        };
    }

    public async Task<ActivityBatchPresentationDto?> GetBatchPresentationAsync(
        Guid batchId,
        CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var batch = await conn.QueryFirstOrDefaultAsync<BatchPresentationRow>(new CommandDefinition($"""
            SELECT
                id AS BatchId,
                CASE WHEN {IngestionBatchActivitySql.IsActive} THEN 'running' ELSE b.status END AS Status,
                source_path AS Source,
                category AS Category,
                files_processed AS FilesProcessed,
                files_review AS ReviewCount,
                files_failed AS FailureCount,
                started_at AS StartedAt,
                CASE WHEN {IngestionBatchActivitySql.IsActive} THEN NULL ELSE completed_at END AS CompletedAt
            FROM ingestion_batches b
            WHERE id = @batchId;
            """, new { batchId }, cancellationToken: ct)).ConfigureAwait(false);
        if (batch is null)
        {
            return null;
        }

        var previewPage = await LoadBatchGroupPageAsync(batchId, PagedRequest.From(0, 6, 6, 6), ct).ConfigureAwait(false);
        var groups = previewPage.Items;
        ApplyArtworkSize(groups, "s");
        ApplyHistoricalProgress(groups);
        var metrics = await conn.QuerySingleAsync<BatchMetricRow>(new CommandDefinition("""
            SELECT
                COUNT(DISTINCT CASE WHEN iba.artifact_type = 'person' THEN iba.artifact_id END) AS PeopleUpdated,
                COUNT(DISTINCT CASE WHEN iba.artifact_type IN ('artwork', 'image') THEN iba.artifact_id END) AS ArtworkAdded,
                COUNT(DISTINCT CASE WHEN iba.artifact_type IN ('lyrics', 'subtitle', 'text_track') THEN iba.artifact_id END) AS TextTracksAdded
            FROM ingestion_batch_artifacts iba
            WHERE iba.batch_id = @batchId;
            """, new { batchId }, cancellationToken: ct)).ConfigureAwait(false);

        var followUpCount = Math.Max(0, batch.ReviewCount + batch.FailureCount);
        var followUp = new List<IngestionAttentionItemDto>();
        if (batch.ReviewCount > 0)
        {
            followUp.Add(new IngestionAttentionItemDto
            {
                Kind = "review",
                Count = batch.ReviewCount,
                Label = $"{batch.ReviewCount:N0} {Pluralize("item", batch.ReviewCount)} need review",
                Route = "/settings/recently-added?review=expanded",
            });
        }
        if (batch.FailureCount > 0)
        {
            followUp.Add(new IngestionAttentionItemDto
            {
                Kind = "failure",
                Count = batch.FailureCount,
                Label = $"{batch.FailureCount:N0} {Pluralize("item", batch.FailureCount)} failed",
                Route = $"/settings/ingestion?runId={batchId:D}&view=all",
            });
        }

        return new ActivityBatchPresentationDto
        {
            BatchId = batchId,
            DisplayName = DisplayBatchName(
                batch.Category,
                batch.Source,
                groups,
                previewPage.TotalCount == groups.Count),
            Summary = BatchSummary(
                batch.Status,
                groups,
                followUpCount,
                previewPage.TotalCount == groups.Count),
            FilesProcessed = batch.FilesProcessed,
            GroupsAdded = previewPage.TotalCount ?? groups.Count,
            PeopleUpdated = metrics.PeopleUpdated > 0 ? metrics.PeopleUpdated : null,
            ArtworkAdded = metrics.ArtworkAdded > 0 ? metrics.ArtworkAdded : null,
            TextTracksAdded = metrics.TextTracksAdded > 0 ? metrics.TextTracksAdded : null,
            ItemsNeedingFollowUp = followUpCount > 0 ? followUpCount : null,
            AddedPreview = groups.ToList(),
            AddedTotal = previewPage.TotalCount ?? groups.Count,
            FollowUp = followUp,
            Timeline = await BuildMilestonesAsync(conn, batch, ct).ConfigureAwait(false),
        };
    }

    public async Task<PagedResponse<IngestionMediaGroupDto>> GetBatchMediaAsync(
        Guid batchId,
        int offset,
        int limit,
        string? search = null,
        string? lane = null,
        string? sort = null,
        CancellationToken ct = default)
    {
        var request = PagedRequest.From(offset, limit, 50, 100);
        return await LoadBatchGroupPageAsync(batchId, request, ct, search, lane, sort).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, PagedResponse<IngestionMediaGroupDto>>> GetBatchMediaPreviewsAsync(
        IReadOnlyCollection<Guid> batchIds,
        int limitPerBatch,
        CancellationToken ct = default)
    {
        if (batchIds.Count == 0)
        {
            return new Dictionary<Guid, PagedResponse<IngestionMediaGroupDto>>();
        }

        var request = PagedRequest.From(0, limitPerBatch, 6, 12);
        var keys = await LoadBatchPreviewKeysAsync(batchIds, request.Limit + 1, ct).ConfigureAwait(false);
        var selectedKeys = keys
            .GroupBy(key => key.BatchId)
            .SelectMany(group => group.Take(request.Limit))
            .ToList();
        var selectedBatchIds = selectedKeys.Select(key => key.BatchId).Distinct().ToArray();
        var selectedGroupIds = selectedKeys.Select(key => key.GroupId).Distinct().ToArray();
        var groups = selectedKeys.Count == 0
            ? []
            : await LoadGroupsAsync(
                GroupScope.BatchAdditionsSet,
                null,
                ct,
                selectedBatchIds,
                selectedGroupIds).ConfigureAwait(false);
        ApplyArtworkSize(groups, "s");
        ApplyHistoricalProgress(groups);

        var groupsByKey = groups.ToDictionary(group => (group.BatchId, group.GroupId));
        return batchIds.ToDictionary(
            id => id,
            id =>
            {
                var batchKeys = keys.Where(key => key.BatchId == id).ToList();
                var items = batchKeys
                    .Take(request.Limit)
                    .Select(key => groupsByKey.GetValueOrDefault((key.BatchId, key.GroupId)))
                    .Where(item => item is not null)
                    .Cast<IngestionMediaGroupDto>()
                    .ToList();
                var total = batchKeys.FirstOrDefault()?.TotalCount ?? 0;
                return new PagedResponse<IngestionMediaGroupDto>(
                    items,
                    request.Offset,
                    request.Limit,
                    total > request.Limit,
                    total,
                    total > request.Limit ? request.Limit.ToString(CultureInfo.InvariantCulture) : null);
            });
    }

    private async Task<PagedResponse<IngestionMediaGroupDto>> LoadBatchGroupPageAsync(
        Guid batchId,
        PagedRequest request,
        CancellationToken ct,
        string? search = null,
        string? lane = null,
        string? sort = null)
    {
        var keys = await LoadBatchGroupKeysAsync(batchId, search, lane, sort, request.Offset, request.Limit + 1, ct).ConfigureAwait(false);
        var pageKeys = keys.Take(request.Limit).ToList();
        var groups = pageKeys.Count == 0
            ? []
            : await LoadGroupsAsync(
                GroupScope.BatchAdditions,
                batchId,
                ct,
                groupIds: pageKeys.Select(key => key.GroupId).ToArray()).ConfigureAwait(false);
        ApplyArtworkSize(groups, "m");
        ApplyHistoricalProgress(groups);

        var groupsById = groups.ToDictionary(group => group.GroupId);
        var ordered = pageKeys
            .Select(key => groupsById.GetValueOrDefault(key.GroupId))
            .Where(item => item is not null)
            .Cast<IngestionMediaGroupDto>()
            .ToList();
        var total = keys.FirstOrDefault()?.TotalCount ?? 0;
        var hasMore = keys.Count > request.Limit;
        return new PagedResponse<IngestionMediaGroupDto>(
            ordered,
            request.Offset,
            request.Limit,
            hasMore,
            total,
            hasMore ? (request.Offset + ordered.Count).ToString(CultureInfo.InvariantCulture) : null);
    }

    private async Task<PagedResponse<IngestionMediaGroupDto>> LoadCurrentGroupPageAsync(
        PagedRequest request,
        CancellationToken ct,
        bool eligibleOnly)
    {
        var keys = await LoadCurrentGroupKeysAsync(request.Offset, request.Limit + 1, eligibleOnly, ct).ConfigureAwait(false);
        var pageKeys = keys.Take(request.Limit).ToList();
        var groups = pageKeys.Count == 0
            ? []
            : await LoadGroupsAsync(
                GroupScope.Current,
                null,
                ct,
                pageKeys.Select(key => key.BatchId).Distinct().ToArray(),
                pageKeys.Select(key => key.GroupId).Distinct().ToArray()).ConfigureAwait(false);
        ApplyArtworkSize(groups, "m");

        var groupsByKey = groups.ToDictionary(group => (group.BatchId, group.GroupId));
        var ordered = pageKeys
            .Select(key => groupsByKey.GetValueOrDefault((key.BatchId, key.GroupId)))
            .Where(item => item is not null)
            .Cast<IngestionMediaGroupDto>()
            .ToList();
        var total = keys.FirstOrDefault()?.TotalCount ?? 0;
        var hasMore = keys.Count > request.Limit;
        return new PagedResponse<IngestionMediaGroupDto>(
            ordered,
            request.Offset,
            request.Limit,
            hasMore,
            total,
            hasMore ? (request.Offset + ordered.Count).ToString(CultureInfo.InvariantCulture) : null);
    }

    private async Task<List<IngestionRecentDayDto>> LoadRecentDaysAsync(
        int dayLimit,
        int itemLimit,
        Guid? currentBatchId,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var keys = (await conn.QueryAsync<RecentDayGroupKeyRow>(new CommandDefinition($"""
            WITH latest_logs AS (
                SELECT *
                FROM (
                    SELECT il.*,
                           ROW_NUMBER() OVER (
                               PARTITION BY il.ingestion_run_id, il.media_asset_id
                               ORDER BY il.updated_at DESC, il.created_at DESC) AS rn
                    FROM ingestion_log il
                    WHERE il.ingestion_run_id IS NOT NULL
                      AND il.media_asset_id IS NOT NULL
                )
                WHERE rn = 1
            ),
            latest_file_operations AS (
                SELECT *
                FROM (
                    SELECT mo.*,
                           ROW_NUMBER() OVER (
                               PARTITION BY mo.batch_id, mo.entity_id
                               ORDER BY COALESCE(mo.updated_at, mo.completed_at, mo.started_at, mo.created_at) DESC) AS rn
                    FROM media_operations mo
                    WHERE mo.operation_type = 'ingestion.file'
                )
                WHERE rn = 1
            ),
            scoped AS (
                SELECT ll.ingestion_run_id AS BatchId,
                       {PresentationGroupSql} AS GroupId,
                       MIN({AddedAtSql}) AS AddedAt,
                       MAX(COALESCE(lfo.updated_at, ll.updated_at, ll.created_at)) AS UpdatedAt
                FROM latest_logs ll
                JOIN ingestion_batches b ON b.id = ll.ingestion_run_id
                JOIN media_assets ma ON ma.id = ll.media_asset_id
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                LEFT JOIN latest_file_operations lfo
                  ON lfo.batch_id = ll.ingestion_run_id AND lfo.entity_id = ll.media_asset_id
                WHERE ((@currentBatchId IS NULL AND NOT {IngestionBatchActivitySql.IsActive})
                       OR (@currentBatchId IS NOT NULL AND b.id = @currentBatchId))
                  AND {AdditionBatchPredicate}
                  AND {PresentationTitleSql}
                GROUP BY ll.ingestion_run_id, {PresentationGroupSql}
            ),
            dated AS (
                SELECT BatchId,
                       GroupId,
                       UpdatedAt,
                       DATE(AddedAt, 'localtime') AS LocalDate
                FROM scoped
            ),
            ranked AS (
                SELECT BatchId,
                       GroupId,
                       LocalDate,
                       COUNT(*) OVER (PARTITION BY LocalDate) AS TotalCount,
                       ROW_NUMBER() OVER (PARTITION BY LocalDate ORDER BY UpdatedAt DESC, HEX(BatchId), HEX(GroupId)) AS GroupRank,
                       DENSE_RANK() OVER (ORDER BY LocalDate DESC) AS DayRank
                FROM dated
                WHERE LocalDate IS NOT NULL
            )
            SELECT BatchId, GroupId, LocalDate, TotalCount, GroupRank
            FROM ranked
            WHERE DayRank <= @dayLimit AND GroupRank <= @itemLimit
            ORDER BY LocalDate DESC, GroupRank;
            """, new { dayLimit, itemLimit, currentBatchId }, cancellationToken: ct)).ConfigureAwait(false)).AsList();

        if (keys.Count == 0)
        {
            return [];
        }

        var groups = await LoadGroupsAsync(
            GroupScope.BatchAdditionsSet,
            null,
            ct,
            keys.Select(key => key.BatchId).Distinct().ToArray(),
            keys.Select(key => key.GroupId).Distinct().ToArray()).ConfigureAwait(false);
        ApplyArtworkSize(groups, "s");
        ApplyHistoricalProgress(groups);
        var groupsByKey = groups.ToDictionary(group => (group.BatchId, group.GroupId));

        return keys
            .GroupBy(key => key.LocalDate, StringComparer.Ordinal)
            .Select(day => new IngestionRecentDayDto
            {
                Date = DateOnly.ParseExact(day.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                TotalCount = day.First().TotalCount,
                Items = day
                    .OrderBy(key => key.GroupRank)
                    .Select(key => groupsByKey.GetValueOrDefault((key.BatchId, key.GroupId)))
                    .Where(item => item is not null)
                    .Cast<IngestionMediaGroupDto>()
                    .ToList(),
            })
            .OrderByDescending(day => day.Date)
            .ToList();
    }

    private async Task<List<PresentationGroupKeyRow>> LoadCurrentGroupKeysAsync(
        int offset,
        int limit,
        bool eligibleOnly,
        CancellationToken ct)
    {
        var eligibilityFilter = eligibleOnly ? "WHERE HasPrimaryCover = 1 AND ActivityRank < 3" : string.Empty;
        using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<PresentationGroupKeyRow>(new CommandDefinition($"""
            WITH current_batches AS (
                SELECT b.id
                FROM ingestion_batches b
                WHERE {IngestionBatchActivitySql.IsActive}
            ),
            latest_logs AS (
                SELECT *
                FROM (
                    SELECT il.*,
                           ROW_NUMBER() OVER (
                               PARTITION BY il.ingestion_run_id, il.media_asset_id
                               ORDER BY il.updated_at DESC, il.created_at DESC) AS rn
                    FROM ingestion_log il
                    JOIN current_batches cb ON cb.id = il.ingestion_run_id
                    WHERE il.media_asset_id IS NOT NULL
                )
                WHERE rn = 1
            ),
            latest_file_operations AS (
                SELECT *
                FROM (
                    SELECT mo.*,
                           ROW_NUMBER() OVER (
                               PARTITION BY mo.batch_id, mo.entity_id
                               ORDER BY COALESCE(mo.updated_at, mo.completed_at, mo.started_at, mo.created_at) DESC) AS rn
                    FROM media_operations mo
                    JOIN current_batches cb ON cb.id = mo.batch_id
                    WHERE mo.operation_type = 'ingestion.file'
                )
                WHERE rn = 1
            ),
            scoped AS (
                SELECT ll.ingestion_run_id AS BatchId,
                       {PresentationGroupSql} AS GroupId,
                       MAX(COALESCE(lfo.updated_at, ll.updated_at, ll.created_at),
                           COALESCE((SELECT MAX(COALESCE(activity.updated_at,activity.started_at,activity.created_at))
                               FROM media_operations activity
                               WHERE activity.batch_id = ll.ingestion_run_id
                                 AND activity.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)), ''),
                           COALESCE((SELECT MAX(ij.updated_at) FROM identity_jobs ij WHERE ij.ingestion_run_id = ll.ingestion_run_id
                               AND ij.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)), '')) AS UpdatedAt,
                       MIN(COALESCE((SELECT MIN(CASE WHEN activity.status IN ('running','processing','active') THEN 0
                                                WHEN activity.status IN ('retry_waiting','failed_retryable','interrupted') THEN 1
                                                WHEN activity.status = 'queued' THEN 2 ELSE 3 END)
                           FROM media_operations activity WHERE activity.batch_id = ll.ingestion_run_id
                             AND activity.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)), 3),
                           COALESCE((SELECT MIN(CASE WHEN ij.state IN ('RetailSearching','BridgeSearching','Hydrating','UniverseEnriching') THEN 0
                               WHEN ij.state IN ('RetailMatched','QidResolved') THEN 1
                               WHEN ij.state = 'Queued' THEN 2 ELSE 3 END)
                               FROM identity_jobs ij WHERE ij.ingestion_run_id = ll.ingestion_run_id
                                 AND ij.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)), 3)) AS ActivityRank
                       ,EXISTS (
                           SELECT 1
                           FROM entity_assets ea
                           WHERE ea.asset_type = 'CoverArt'
                             AND (
                                 (LOWER(COALESCE(w.media_type,'')) IN ('tv','television','tv show','tv shows') AND ea.entity_id = COALESCE(gp.id,p.id,w.id))
                                 OR (LOWER(COALESCE(w.media_type,'')) IN ('music','track','song','album') AND ea.entity_id = COALESCE(p.id,w.id))
                                 OR (LOWER(COALESCE(w.media_type,'')) IN ('comic','comics','cbz','cbr') AND ea.entity_id IN (w.id,p.id))
                                 OR (LOWER(COALESCE(w.media_type,'')) LIKE '%audiobook%' AND ea.entity_id = COALESCE(p.id,w.id))
                                 OR (LOWER(COALESCE(w.media_type,'')) NOT IN ('tv','television','tv show','tv shows','music','track','song','album','comic','comics','cbz','cbr')
                                     AND LOWER(COALESCE(w.media_type,'')) NOT LIKE '%audiobook%'
                                     AND ea.entity_id = w.id)
                             )
                       ) AS HasPrimaryCover
                FROM latest_logs ll
                JOIN media_assets ma ON ma.id = ll.media_asset_id
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                LEFT JOIN latest_file_operations lfo
                  ON lfo.batch_id = ll.ingestion_run_id AND lfo.entity_id = ll.media_asset_id
                WHERE {PresentationTitleSql}
            ),
            grouped AS (
                SELECT BatchId, GroupId, MAX(UpdatedAt) AS UpdatedAt,
                       MIN(ActivityRank) AS ActivityRank,
                       MAX(HasPrimaryCover) AS HasPrimaryCover
                FROM scoped
                GROUP BY BatchId, GroupId
            )
            SELECT BatchId,
                   GroupId,
                   COUNT(*) OVER () AS TotalCount
            FROM grouped
            {eligibilityFilter}
            ORDER BY ActivityRank, UpdatedAt DESC, HEX(BatchId), HEX(GroupId)
            LIMIT @limit OFFSET @offset;
            """, new { offset, limit }, cancellationToken: ct)).ConfigureAwait(false)).AsList();
    }

    private async Task<PagedResponse<IngestionMediaGroupDto>> LoadHistoryGroupPageAsync(
        string? search,
        string? lane,
        DateTimeOffset? start,
        DateTimeOffset? end,
        string? sort,
        PagedRequest request,
        CancellationToken ct)
    {
        var keys = await LoadHistoryGroupKeysAsync(
            search,
            lane,
            start,
            end,
            request.Offset,
            request.Limit + 1,
            sort,
            ct).ConfigureAwait(false);
        var pageKeys = keys.Take(request.Limit).ToList();
        var batchIds = pageKeys.Select(key => key.BatchId).Distinct().ToArray();
        var groupIds = pageKeys.Select(key => key.GroupId).Distinct().ToArray();
        var groups = pageKeys.Count == 0
            ? []
            : await LoadGroupsAsync(
                GroupScope.BatchAdditionsSet,
                null,
                ct,
                batchIds,
                groupIds).ConfigureAwait(false);
        ApplyArtworkSize(groups, "m");
        ApplyHistoricalProgress(groups);

        var groupsByKey = groups.ToDictionary(group => (group.BatchId, group.GroupId));
        var ordered = pageKeys
            .Select(key => groupsByKey.GetValueOrDefault((key.BatchId, key.GroupId)))
            .Where(item => item is not null)
            .Cast<IngestionMediaGroupDto>()
            .ToList();
        var total = keys.FirstOrDefault()?.TotalCount ?? 0;
        var hasMore = keys.Count > request.Limit;
        return new PagedResponse<IngestionMediaGroupDto>(
            ordered,
            request.Offset,
            request.Limit,
            hasMore,
            total,
            hasMore ? (request.Offset + ordered.Count).ToString(CultureInfo.InvariantCulture) : null);
    }

    private async Task<List<PresentationGroupKeyRow>> LoadHistoryGroupKeysAsync(
        string? search,
        string? lane,
        DateTimeOffset? start,
        DateTimeOffset? end,
        int offset,
        int limit,
        string? sort,
        CancellationToken ct)
    {
        var searchFilter = string.IsNullOrWhiteSpace(search) ? "" : $"AND {HistorySearchSql}";
        var laneFilter = NormalizeLaneFilter(lane) switch
        {
            "read" => $"AND {NormalizedMediaTypeSql} IN ('book','books','ebook','ebooks','epub','pdf','comic','comics')",
            "watch" => $"AND {NormalizedMediaTypeSql} IN ('tv','television','tv shows','show','shows','movie','movies','film','films')",
            "listen" => $"AND ({NormalizedMediaTypeSql} IN ('music','album','albums','track','tracks','song','songs','audiobook','audiobooks') OR ({NormalizedMediaTypeSql} LIKE '%audio%' AND {NormalizedMediaTypeSql} LIKE '%book%'))",
            "movies" => $"AND {NormalizedMediaTypeSql} IN ('movie','movies','film','films')",
            "tv" => $"AND {NormalizedMediaTypeSql} IN ('tv','television','tv shows','show','shows')",
            "music" => $"AND {NormalizedMediaTypeSql} IN ('music','album','albums','track','tracks','song','songs')",
            "books" => $"AND {NormalizedMediaTypeSql} IN ('book','books','ebook','ebooks','epub','pdf')",
            "audiobooks" => $"AND ({NormalizedMediaTypeSql} IN ('audiobook','audiobooks') OR ({NormalizedMediaTypeSql} LIKE '%audio%' AND {NormalizedMediaTypeSql} LIKE '%book%'))",
            "comics" => $"AND {NormalizedMediaTypeSql} IN ('comic','comics','cbz','cbr')",
            _ => "",
        };
        var startFilter = start.HasValue ? $"AND julianday({AddedAtSql}) >= julianday(@start)" : "";
        var endFilter = end.HasValue ? $"AND julianday({AddedAtSql}) <= julianday(@end)" : "";
        var orderBy = NormalizeHistorySort(sort) switch
        {
            "oldest" => "UpdatedAt ASC, HEX(BatchId), HEX(GroupId)",
            "title" => "SortTitle ASC, UpdatedAt DESC, HEX(BatchId), HEX(GroupId)",
            "media" => "SortMediaType ASC, UpdatedAt DESC, HEX(BatchId), HEX(GroupId)",
            _ => "UpdatedAt DESC, HEX(BatchId), HEX(GroupId)",
        };

        using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<PresentationGroupKeyRow>(new CommandDefinition($"""
            WITH latest_logs AS (
                SELECT *
                FROM (
                    SELECT il.*,
                           ROW_NUMBER() OVER (
                               PARTITION BY il.ingestion_run_id, il.media_asset_id
                               ORDER BY il.updated_at DESC, il.created_at DESC) AS rn
                    FROM ingestion_log il
                    WHERE il.ingestion_run_id IS NOT NULL
                      AND il.media_asset_id IS NOT NULL
                )
                WHERE rn = 1
            ),
            latest_file_operations AS (
                SELECT *
                FROM (
                    SELECT mo.*,
                           ROW_NUMBER() OVER (
                               PARTITION BY mo.batch_id, mo.entity_id
                               ORDER BY COALESCE(mo.updated_at, mo.completed_at, mo.started_at, mo.created_at) DESC) AS rn
                    FROM media_operations mo
                    WHERE mo.operation_type = 'ingestion.file'
                )
                WHERE rn = 1
            ),
            scoped AS (
                SELECT ll.ingestion_run_id AS BatchId,
                       {PresentationGroupSql} AS GroupId,
                       LOWER(COALESCE(
                           (SELECT cv.value
                            FROM canonical_values cv
                            WHERE cv.entity_id = {PresentationGroupSql}
                              AND cv.key IN ('title','episode_title','issue_title','album','show_name','series','book_title')
                            ORDER BY CASE cv.key WHEN 'title' THEN 0 WHEN 'album' THEN 1 WHEN 'show_name' THEN 2 ELSE 3 END
                            LIMIT 1),
                           ll.detected_title,
                           '')) AS SortTitle,
                       {NormalizedMediaTypeSql} AS SortMediaType,
                       COALESCE(lfo.updated_at, ll.updated_at, ll.created_at) AS UpdatedAt
                FROM latest_logs ll
                JOIN ingestion_batches b ON b.id = ll.ingestion_run_id
                JOIN media_assets ma ON ma.id = ll.media_asset_id
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                LEFT JOIN latest_file_operations lfo
                  ON lfo.batch_id = ll.ingestion_run_id AND lfo.entity_id = ll.media_asset_id
                -- Items of a still-active batch appear as soon as their own identity jobs settle
                -- (AdditionBatchPredicate requires an added-at time), so a long first-run import
                -- fills Recently Added while it runs. Items still in flight have no added-at yet.
                WHERE {AdditionBatchPredicate}
                  AND {PresentationTitleSql}
                  {searchFilter}
                  {laneFilter}
                  {startFilter}
                  {endFilter}
            ),
            grouped AS (
                SELECT BatchId, GroupId, MIN(SortTitle) AS SortTitle, MIN(SortMediaType) AS SortMediaType, MAX(UpdatedAt) AS UpdatedAt
                FROM scoped
                GROUP BY BatchId, GroupId
            )
            SELECT BatchId,
                   GroupId,
                   COUNT(*) OVER () AS TotalCount
            FROM grouped
            ORDER BY {orderBy}
            LIMIT @limit OFFSET @offset;
            """, new
        {
            search = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim().ToLowerInvariant()}%",
            start = start?.ToUniversalTime().ToString("O"),
            end = end?.ToUniversalTime().ToString("O"),
            offset,
            limit,
        }, cancellationToken: ct)).ConfigureAwait(false)).AsList();
    }

    private async Task<List<PresentationGroupKeyRow>> LoadBatchGroupKeysAsync(
        Guid batchId,
        string? search,
        string? lane,
        string? sort,
        int offset,
        int limit,
        CancellationToken ct)
    {
        var searchFilter = string.IsNullOrWhiteSpace(search) ? "" : $"AND {HistorySearchSql}";
        var laneFilter = NormalizeLaneFilter(lane) switch
        {
            "read" => $"AND {NormalizedMediaTypeSql} IN ('book','books','ebook','ebooks','epub','pdf','comic','comics')",
            "watch" => $"AND {NormalizedMediaTypeSql} IN ('tv','television','tv shows','show','shows','movie','movies','film','films')",
            "listen" => $"AND ({NormalizedMediaTypeSql} IN ('music','album','albums','track','tracks','song','songs','audiobook','audiobooks') OR ({NormalizedMediaTypeSql} LIKE '%audio%' AND {NormalizedMediaTypeSql} LIKE '%book%'))",
            _ => "",
        };
        var direction = sort?.Equals("oldest", StringComparison.OrdinalIgnoreCase) == true ? "ASC" : "DESC";
        using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<PresentationGroupKeyRow>(new CommandDefinition($"""
            WITH latest_logs AS (
                SELECT *
                FROM (
                    SELECT il.*,
                           ROW_NUMBER() OVER (
                               PARTITION BY il.ingestion_run_id, il.media_asset_id
                               ORDER BY il.updated_at DESC, il.created_at DESC) AS rn
                    FROM ingestion_log il
                    WHERE il.ingestion_run_id = @batchId
                      AND il.media_asset_id IS NOT NULL
                )
                WHERE rn = 1
            ),
            latest_file_operations AS (
                SELECT *
                FROM (
                    SELECT mo.*,
                           ROW_NUMBER() OVER (
                               PARTITION BY mo.batch_id, mo.entity_id
                               ORDER BY COALESCE(mo.updated_at, mo.completed_at, mo.started_at, mo.created_at) DESC) AS rn
                    FROM media_operations mo
                    WHERE mo.batch_id = @batchId
                      AND mo.operation_type = 'ingestion.file'
                )
                WHERE rn = 1
            ),
            scoped AS (
                SELECT ll.ingestion_run_id AS BatchId,
                       {PresentationGroupSql} AS GroupId,
                       COALESCE(lfo.updated_at, ll.updated_at, ll.created_at) AS UpdatedAt
                FROM latest_logs ll
                JOIN ingestion_batches b ON b.id = ll.ingestion_run_id
                JOIN media_assets ma ON ma.id = ll.media_asset_id
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                LEFT JOIN latest_file_operations lfo
                  ON lfo.batch_id = ll.ingestion_run_id AND lfo.entity_id = ll.media_asset_id
                WHERE {AdditionBatchPredicate}
                  AND {PresentationTitleSql}
                  {searchFilter}
                  {laneFilter}
            ),
            grouped AS (
                SELECT BatchId, GroupId, MAX(UpdatedAt) AS UpdatedAt
                FROM scoped
                GROUP BY BatchId, GroupId
            )
            SELECT BatchId,
                   GroupId,
                   COUNT(*) OVER () AS TotalCount
            FROM grouped
            ORDER BY UpdatedAt {direction}, HEX(GroupId)
            LIMIT @limit OFFSET @offset;
            """, new
        {
            batchId,
            search = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim().ToLowerInvariant()}%",
            offset,
            limit,
        }, cancellationToken: ct)).ConfigureAwait(false)).AsList();
    }

    private async Task<List<PresentationGroupKeyRow>> LoadBatchPreviewKeysAsync(
        IReadOnlyCollection<Guid> batchIds,
        int limit,
        CancellationToken ct)
    {
        var batchIdHexes = batchIds.Select(GuidHex).ToArray();
        using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<PresentationGroupKeyRow>(new CommandDefinition($"""
            WITH latest_logs AS (
                SELECT *
                FROM (
                    SELECT il.*,
                           ROW_NUMBER() OVER (
                               PARTITION BY il.ingestion_run_id, il.media_asset_id
                               ORDER BY il.updated_at DESC, il.created_at DESC) AS rn
                    FROM ingestion_log il
                    WHERE LOWER(HEX(il.ingestion_run_id)) IN @batchIdHexes
                      AND il.media_asset_id IS NOT NULL
                )
                WHERE rn = 1
            ),
            latest_file_operations AS (
                SELECT *
                FROM (
                    SELECT mo.*,
                           ROW_NUMBER() OVER (
                               PARTITION BY mo.batch_id, mo.entity_id
                               ORDER BY COALESCE(mo.updated_at, mo.completed_at, mo.started_at, mo.created_at) DESC) AS rn
                    FROM media_operations mo
                    WHERE LOWER(HEX(mo.batch_id)) IN @batchIdHexes
                      AND mo.operation_type = 'ingestion.file'
                )
                WHERE rn = 1
            ),
            scoped AS (
                SELECT ll.ingestion_run_id AS BatchId,
                       {PresentationGroupSql} AS GroupId,
                       COALESCE(lfo.updated_at, ll.updated_at, ll.created_at) AS UpdatedAt
                FROM latest_logs ll
                JOIN ingestion_batches b ON b.id = ll.ingestion_run_id
                JOIN media_assets ma ON ma.id = ll.media_asset_id
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                LEFT JOIN latest_file_operations lfo
                  ON lfo.batch_id = ll.ingestion_run_id AND lfo.entity_id = ll.media_asset_id
                WHERE {AdditionBatchPredicate}
                  AND {PresentationTitleSql}
            ),
            grouped AS (
                SELECT BatchId, GroupId, MAX(UpdatedAt) AS UpdatedAt
                FROM scoped
                GROUP BY BatchId, GroupId
            ),
            ranked AS (
                SELECT BatchId,
                       GroupId,
                       UpdatedAt,
                       COUNT(*) OVER (PARTITION BY BatchId) AS TotalCount,
                       ROW_NUMBER() OVER (PARTITION BY BatchId ORDER BY UpdatedAt DESC, HEX(GroupId)) AS GroupRank
                FROM grouped
            )
            SELECT BatchId, GroupId, TotalCount
            FROM ranked
            WHERE GroupRank <= @limit
            ORDER BY BatchId, GroupRank;
            """, new { batchIdHexes, limit }, cancellationToken: ct)).ConfigureAwait(false)).AsList();
    }

    private async Task<List<IngestionMediaGroupDto>> LoadGroupsAsync(
        GroupScope scope,
        Guid? batchId,
        CancellationToken ct,
        IReadOnlyCollection<Guid>? batchIds = null,
        IReadOnlyCollection<Guid>? groupIds = null)
    {
        var rows = await LoadRowsAsync(scope, batchId, ct, batchIds, groupIds).ConfigureAwait(false);
        var operations = await LoadOperationsAsync(rows, ct).ConfigureAwait(false);

        return rows
            .GroupBy(row => new { row.BatchId, GroupId = PresentationGroupId(row) })
            .Select(group => BuildGroup(group.Key.BatchId, group.Key.GroupId, group.ToList(), operations))
            .Where(group => !IsPlaceholderTitle(group.Title))
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<MediaPresentationRow>> LoadRowsAsync(
        GroupScope scope,
        Guid? batchId,
        CancellationToken ct,
        IReadOnlyCollection<Guid>? batchIds = null,
        IReadOnlyCollection<Guid>? groupIds = null)
    {
        var batchIdHexes = batchIds?.Select(GuidHex).ToArray();
        var currentBatchSelection = batchIdHexes is { Length: > 0 }
            ? "AND LOWER(HEX(b.id)) IN @batchIdHexes"
            : "";
        var currentBatchCte = scope == GroupScope.Current
            ? $"""
                current_batches AS (
                    SELECT b.id
                    FROM ingestion_batches b
                    WHERE (
                        {IngestionBatchActivitySql.IsActive})
                      {currentBatchSelection}
                ),
                """
            : "";
        var where = scope switch
        {
            GroupScope.Current => "",
            GroupScope.History => $"""
                AND NOT {IngestionBatchActivitySql.IsActive}
                AND {AdditionBatchPredicate}
                """,
            GroupScope.BatchAdditions => $"AND b.id = @batchId AND {AdditionBatchPredicate}",
            GroupScope.BatchAdditionsSet => $"AND LOWER(HEX(b.id)) IN @batchIdHexes AND {AdditionBatchPredicate}",
            _ => "AND b.id = @batchId",
        };
        var logJoin = scope == GroupScope.Current
            ? "JOIN current_batches current_log_batch ON current_log_batch.id = il.ingestion_run_id"
            : "";
        var logFilter = scope switch
        {
            GroupScope.Batch or GroupScope.BatchAdditions => "AND il.ingestion_run_id = @batchId",
            GroupScope.BatchAdditionsSet => "AND LOWER(HEX(il.ingestion_run_id)) IN @batchIdHexes",
            _ => "",
        };
        var operationJoin = scope == GroupScope.Current
            ? "JOIN current_batches current_operation_batch ON current_operation_batch.id = mo.batch_id"
            : "";
        var operationFilter = scope switch
        {
            GroupScope.Batch or GroupScope.BatchAdditions => "AND mo.batch_id = @batchId",
            GroupScope.BatchAdditionsSet => "AND LOWER(HEX(mo.batch_id)) IN @batchIdHexes",
            _ => "",
        };
        var groupIdHexes = groupIds?.Select(GuidHex).ToArray();
        var selectedGroups = groupIdHexes is { Length: > 0 }
            ? $"AND LOWER(HEX({PresentationGroupSql})) IN @groupIdHexes"
            : "";

        using var conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<MediaPresentationRow>(new CommandDefinition($"""
            WITH {currentBatchCte}latest_logs AS (
                SELECT
                    il.*,
                    ROW_NUMBER() OVER (
                        PARTITION BY il.ingestion_run_id, il.media_asset_id
                        ORDER BY il.updated_at DESC, il.created_at DESC
                    ) AS rn
                FROM ingestion_log il
                {logJoin}
                WHERE il.ingestion_run_id IS NOT NULL
                  AND il.media_asset_id IS NOT NULL
                  {logFilter}
            ),
            latest_file_operations AS (
                SELECT *
                FROM (
                    SELECT
                        mo.*,
                        ROW_NUMBER() OVER (
                            PARTITION BY mo.batch_id, mo.entity_id
                            ORDER BY COALESCE(mo.updated_at, mo.completed_at, mo.started_at, mo.created_at) DESC
                        ) AS rn
                    FROM media_operations mo
                    {operationJoin}
                    WHERE mo.operation_type = 'ingestion.file'
                      {operationFilter}
                )
                WHERE rn = 1
            )
            SELECT
                ll.id AS LogId,
                ll.ingestion_run_id AS BatchId,
                ll.media_asset_id AS AssetId,
                e.id AS EditionId,
                w.id AS WorkId,
                p.id AS ParentWorkId,
                gp.id AS RootWorkId,
                COALESCE(NULLIF(w.media_type, ''), NULLIF(ll.media_type, ''), 'Unknown') AS MediaType,
                ll.detected_title AS DetectedTitle,
                (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('title','episode_title','issue_title') ORDER BY CASE key WHEN 'title' THEN 0 ELSE 1 END LIMIT 1) AS LeafTitle,
                (SELECT value FROM canonical_values WHERE entity_id = p.id AND key IN ('title','album','show_name','series','book_title') ORDER BY CASE key WHEN 'title' THEN 0 WHEN 'album' THEN 1 ELSE 2 END LIMIT 1) AS ParentTitle,
                (SELECT value FROM canonical_values WHERE entity_id = gp.id AND key IN ('title','show_name','series') ORDER BY CASE key WHEN 'title' THEN 0 ELSE 1 END LIMIT 1) AS RootTitle,
                COALESCE(
                    (SELECT group_concat(value, '; ') FROM (SELECT value FROM canonical_value_arrays WHERE entity_id = COALESCE(gp.id,p.id,w.id) AND key IN ('artist','album_artist','author','creator','narrator') ORDER BY ordinal)),
                    (SELECT value FROM canonical_values WHERE entity_id = COALESCE(gp.id,p.id,w.id) AND key IN ('artist','album_artist','author','creator','narrator') LIMIT 1)
                ) AS Creator,
                (SELECT value FROM canonical_values WHERE entity_id IN (ll.media_asset_id,w.id,p.id,gp.id) AND key = 'season_number' LIMIT 1) AS SeasonNumber,
                (SELECT value FROM canonical_values WHERE entity_id IN (ll.media_asset_id,w.id) AND key = 'episode_number' LIMIT 1) AS EpisodeNumber,
                (SELECT value FROM canonical_values WHERE entity_id IN (ll.media_asset_id,w.id) AND key = 'track_number' LIMIT 1) AS TrackNumber,
                (SELECT value FROM canonical_values WHERE entity_id IN (ll.media_asset_id,w.id) AND key IN ('issue_number','series_position') LIMIT 1) AS IssueNumber,
                (SELECT value FROM canonical_values WHERE entity_id IN (ll.media_asset_id,w.id,p.id) AND key = 'audiobook_part_count' LIMIT 1) AS AudiobookPartCount,
                (SELECT value FROM canonical_values WHERE entity_id IN (ll.media_asset_id,w.id) AND key IN ('duration','runtime') LIMIT 1) AS DurationLabel,
                {IdentityReadySql} AS IdentityReady,
                (SELECT ij.state FROM ({WorkIdentityJobsSql}) ij
                 ORDER BY ij.updated_at DESC, ij.created_at DESC LIMIT 1) AS IdentityState,
                ll.status AS LogStatus,
                lfo.status AS OperationStatus,
                lfo.stage AS OperationStage,
                ma.presented_at AS PresentedAt,
                ll.created_at AS AddedAt,
                COALESCE(lfo.updated_at, ll.updated_at, ll.created_at) AS UpdatedAt,
                (SELECT COUNT(*) FROM review_queue rq WHERE rq.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id) AND rq.status = 'Pending' AND rq.review_ready_at IS NOT NULL) AS ReviewCount,
                (SELECT COUNT(*) FROM person_media_links pml WHERE pml.media_asset_id = ll.media_asset_id) AS PeopleCount,
                (SELECT COUNT(*) FROM text_tracks tt WHERE tt.asset_id = ll.media_asset_id) AS TextTrackCount,
                CASE
                    WHEN LOWER(COALESCE(w.media_type,'')) IN ('tv','television','tv show','tv shows') THEN
                        (SELECT ea.id FROM entity_assets ea WHERE ea.entity_id = COALESCE(gp.id,p.id,w.id) AND ea.asset_type = 'CoverArt'
                         ORDER BY COALESCE(ea.is_user_override,0) DESC, COALESCE(ea.is_preferred,0) DESC, COALESCE(ea.updated_at,ea.created_at) DESC LIMIT 1)
                    WHEN LOWER(COALESCE(w.media_type,'')) IN ('music','track','song','album')
                         OR LOWER(COALESCE(w.media_type,'')) LIKE '%audiobook%' THEN
                        (SELECT ea.id FROM entity_assets ea WHERE ea.entity_id = COALESCE(p.id,w.id) AND ea.asset_type = 'CoverArt'
                         ORDER BY COALESCE(ea.is_user_override,0) DESC, COALESCE(ea.is_preferred,0) DESC, COALESCE(ea.updated_at,ea.created_at) DESC LIMIT 1)
                    WHEN LOWER(COALESCE(w.media_type,'')) IN ('comic','comics','cbz','cbr') THEN
                        COALESCE(
                            (SELECT ea.id FROM entity_assets ea WHERE ea.entity_id = w.id AND ea.asset_type = 'CoverArt'
                             ORDER BY COALESCE(ea.is_user_override,0) DESC, COALESCE(ea.is_preferred,0) DESC, COALESCE(ea.updated_at,ea.created_at) DESC LIMIT 1),
                            (SELECT ea.id FROM entity_assets ea WHERE ea.entity_id = p.id AND ea.asset_type = 'CoverArt'
                             ORDER BY COALESCE(ea.is_user_override,0) DESC, COALESCE(ea.is_preferred,0) DESC, COALESCE(ea.updated_at,ea.created_at) DESC LIMIT 1))
                    ELSE
                        (SELECT ea.id FROM entity_assets ea WHERE ea.entity_id = w.id AND ea.asset_type = 'CoverArt'
                         ORDER BY COALESCE(ea.is_user_override,0) DESC, COALESCE(ea.is_preferred,0) DESC, COALESCE(ea.updated_at,ea.created_at) DESC LIMIT 1)
                END AS CoverAssetId
            FROM latest_logs ll
            JOIN ingestion_batches b ON b.id = ll.ingestion_run_id
            JOIN media_assets ma ON ma.id = ll.media_asset_id
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            LEFT JOIN latest_file_operations lfo ON lfo.batch_id = ll.ingestion_run_id AND lfo.entity_id = ll.media_asset_id
            WHERE ll.rn = 1
              {where}
              {selectedGroups}
            ORDER BY COALESCE(lfo.updated_at, ll.updated_at, ll.created_at) DESC;
            """, new { batchId, batchIdHexes, groupIdHexes }, cancellationToken: ct)).ConfigureAwait(false)).AsList();
        return rows;
    }

    private async Task<List<PresentationOperationRow>> LoadOperationsAsync(
        IReadOnlyCollection<MediaPresentationRow> rows,
        CancellationToken ct)
    {
        var batchIds = rows.Select(row => row.BatchId).Distinct().ToArray();
        var entityIds = rows
            .SelectMany(row => new Guid?[] { row.AssetId, row.WorkId, row.ParentWorkId, row.RootWorkId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        if (batchIds.Length == 0 || entityIds.Length == 0)
        {
            return [];
        }

        var entityIdHexes = entityIds.Select(GuidHex).ToArray();
        var batchFilter = batchIds.Length == 1 ? "batch_id = @batchId" : "batch_id IN @batchIds";
        using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<PresentationOperationRow>(new CommandDefinition($"""
            SELECT batch_id AS BatchId, entity_id AS EntityId, operation_type AS OperationType,
                   capability_id AS CapabilityId, status AS Status, stage AS Stage,
                   COALESCE(updated_at, completed_at, started_at, created_at) AS UpdatedAt
            FROM media_operations
            WHERE {batchFilter}
              AND LOWER(HEX(entity_id)) IN @entityIdHexes
            UNION ALL
            SELECT ingestion_run_id AS BatchId, entity_id AS EntityId,
                   CASE WHEN pass = 'Universe' THEN 'identity.relationships' ELSE 'identity.metadata' END AS OperationType,
                   NULL AS CapabilityId,
                   CASE WHEN state IN ('Ready','ReadyWithoutUniverse','RetailNoMatch','RetailMatchedNeedsReview','QidNoMatch','QidNeedsReview') THEN 'completed'
                        WHEN state = 'Failed' THEN 'failed_terminal'
                        WHEN state IN ('Queued','RetailMatched','QidResolved') THEN 'queued'
                        ELSE 'running' END AS Status,
                   state AS Stage, updated_at AS UpdatedAt
            FROM identity_jobs
            WHERE ingestion_run_id IN @batchIds AND LOWER(HEX(entity_id)) IN @entityIdHexes;
            """,
            new { batchId = batchIds[0], batchIds, entityIdHexes },
            cancellationToken: ct)).ConfigureAwait(false)).AsList();
    }

    private static IngestionMediaGroupDto BuildGroup(
        Guid batchId,
        Guid groupId,
        IReadOnlyList<MediaPresentationRow> rows,
        IReadOnlyList<PresentationOperationRow> operations)
    {
        var newest = rows.OrderByDescending(row => row.UpdatedAt).First();
        var mediaType = NormalizeMediaType(newest.MediaType);
        var memberIds = rows.SelectMany(row => new Guid?[] { row.AssetId, row.WorkId, row.ParentWorkId, row.RootWorkId })
            .Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
        var scopedOps = operations.Where(operation => operation.BatchId == batchId && operation.EntityId is { } id && memberIds.Contains(id)).ToList();
        var completed = rows
            .Where(row => row.IdentityReady)
            .Select(row => row.WorkId)
            .Distinct()
            .Count();
        var cover = rows.OrderByDescending(row => row.UpdatedAt).FirstOrDefault(row => row.CoverAssetId.HasValue)?.CoverAssetId;
        var needsReview = rows.Any(row => row.ReviewCount > 0 || row.IdentityState is "RetailNoMatch" or "QidNoMatch" or "QidNeedsReview" or "RetailMatchedNeedsReview");
        var terminalFailure = scopedOps.Any(operation => operation.Status is "failed_terminal" or "dead_lettered")
                              || rows.Any(row => row.OperationStatus is "failed_terminal" or "dead_lettered");
        var hasBackgroundWork = scopedOps.Any(operation => IsActive(operation.Status) && !IsFileIntake(operation.OperationType));
        var intakeComplete = rows.All(row => row.IdentityReady);
        var availability = needsReview ? "review" : terminalFailure ? "failed" : intakeComplete ? hasBackgroundWork ? "finishing" : "ready" : "adding";

        var progressGates = BuildProgressGates(availability, scopedOps, rows, cover.HasValue);
        var completedGates = progressGates.Count(gate => gate.State == "complete");
        var currentGate = progressGates.FirstOrDefault(gate => gate.State != "complete");

        return new IngestionMediaGroupDto
        {
            GroupId = groupId,
            BatchId = batchId,
            WorkId = groupId,
            EditionId = rows.Count == 1 ? newest.EditionId : null,
            MediaType = mediaType,
            Title = GroupTitle(mediaType, newest),
            Subtitle = GroupSubtitle(mediaType, rows),
            CoverUrl = cover is { } coverId ? $"/stream/artwork/{coverId:D}" : null,
            Availability = availability,
            StatusLabel = GroupStatus(availability, scopedOps),
            ProgressPercent = progressGates.Count == 0
                ? null
                : (int)Math.Round(completedGates * 100d / progressGates.Count, MidpointRounding.AwayFromZero),
            CurrentGateKey = currentGate?.Key,
            CurrentGateLabel = currentGate?.Label,
            ProgressGates = progressGates,
            ChildCompleted = completed,
            FileCount = rows.Select(row => row.AssetId).Distinct().Count(),
            ChildExpected = null,
            ChildUnit = ChildUnit(mediaType, rows),
            DetailRoute = DetailRoute(mediaType, groupId),
            People = Facet("People", "people", scopedOps, rows.Any(row => row.PeopleCount > 0), applies: true),
            Artwork = Facet("Artwork", "artwork", scopedOps, cover.HasValue, applies: true),
            Metadata = Facet("Metadata", "metadata", scopedOps, intakeComplete, applies: true),
            Relationships = Facet("Relationships", "relationship", scopedOps, false, applies: true),
            TextTracks = Facet(mediaType == "Music" ? "Lyrics" : "Subtitles", "text", scopedOps, rows.Any(row => row.TextTrackCount > 0), applies: mediaType is "Music" or "Movies" or "TV"),
            AddedAt = rows.Min(row => row.PresentedAt ?? row.AddedAt),
            UpdatedAt = scopedOps.Select(operation => operation.UpdatedAt).Append(rows.Max(row => row.UpdatedAt)).Max(),
        };
    }

    private static List<IngestionProgressGateDto> BuildProgressGates(
        string availability,
        IReadOnlyList<PresentationOperationRow> operations,
        IReadOnlyList<MediaPresentationRow> rows,
        bool hasPrimaryCover)
    {
        var matching = operations.Where(IsMatchingOperation).ToList();
        var enrichment = operations
            .Where(operation => !IsFileIntake(operation.OperationType) && !IsMatchingOperation(operation))
            .ToList();
        var identityStates = rows
            .Select(row => row.IdentityState)
            .Where(state => !string.IsNullOrWhiteSpace(state))
            .Select(state => state!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var gates = new List<IngestionProgressGateDto>
        {
            Gate("identified", "Identified", "complete", "Media type and library identity are known."),
        };

        if (matching.Count > 0 || identityStates.Count > 0)
        {
            gates.Add(Gate(
                "matched",
                "Matched metadata",
                ResolveMatchingGateState(matching, identityStates, availability),
                "Canonical identity and provider matching."));
        }

        var enrichmentState = identityStates.Any(IsIdentityEnriching)
            ? "active"
            : identityStates.Any(IsIdentityBeforeEnrichment)
                ? "pending"
            : !hasPrimaryCover
            ? ResolveGateState(enrichment, availability, defaultState: "active")
            : ResolveGateState(enrichment, availability, defaultState: "complete");
        if (hasPrimaryCover && enrichmentState == "complete")
        {
            enrichmentState = enrichment.Any(operation => !IsSettledOperation(operation.Status))
                ? ResolveGateState(enrichment, availability)
                : "complete";
        }

        gates.Add(Gate(
            "enriched",
            hasPrimaryCover ? "Enriching details" : "Finding cover art",
            enrichmentState,
            hasPrimaryCover
                ? "Primary cover art is ready; remaining required enrichment is settling."
                : "Waiting for the correct group-level primary cover."));

        gates.Add(Gate(
            "ready",
            "Ready in library",
            availability == "ready" ? "complete"
                : availability is "review" or "failed" ? "blocked"
                : "pending",
            "Organized and available in the library."));

        // Gates may execute concurrently underneath the UI, but progress must not
        // claim a later gate as complete while an earlier required gate is open.
        var priorGateOpen = false;
        foreach (var gate in gates)
        {
            if (priorGateOpen && gate.State == "complete")
            {
                gate.State = "pending";
                gate.CompletedUnits = 0;
            }

            priorGateOpen |= gate.State != "complete";
        }

        return gates;
    }

    private static string ResolveMatchingGateState(
        IReadOnlyCollection<PresentationOperationRow> matching,
        IReadOnlyCollection<string> identityStates,
        string availability)
    {
        if (availability is "review" or "failed")
        {
            return "blocked";
        }
        if (matching.Any(operation => operation.Status is "retry_waiting" or "failed_retryable" or "interrupted"))
        {
            return "retry";
        }
        if (identityStates.Any(state => state.Equals("Queued", StringComparison.OrdinalIgnoreCase)
                                        || state.Equals("RetailSearching", StringComparison.OrdinalIgnoreCase)
                                        || state.Equals("BridgeSearching", StringComparison.OrdinalIgnoreCase)))
        {
            return "active";
        }
        if (identityStates.Any(state => state.Equals("RetailMatched", StringComparison.OrdinalIgnoreCase)
                                        || state.Equals("QidResolved", StringComparison.OrdinalIgnoreCase)
                                        || IsIdentityEnriching(state)
                                        || state.Equals("Ready", StringComparison.OrdinalIgnoreCase)
                                        || state.Equals("ReadyWithoutUniverse", StringComparison.OrdinalIgnoreCase)))
        {
            return "complete";
        }
        return ResolveGateState(matching, availability);
    }

    private static bool IsIdentityEnriching(string state) =>
        state.Equals("Hydrating", StringComparison.OrdinalIgnoreCase)
        || state.Equals("UniverseEnriching", StringComparison.OrdinalIgnoreCase);

    private static bool IsIdentityBeforeEnrichment(string state) =>
        state.Equals("Queued", StringComparison.OrdinalIgnoreCase)
        || state.Equals("RetailSearching", StringComparison.OrdinalIgnoreCase)
        || state.Equals("BridgeSearching", StringComparison.OrdinalIgnoreCase)
        || state.Equals("RetailMatched", StringComparison.OrdinalIgnoreCase);

    private static IngestionProgressGateDto Gate(string key, string label, string state, string detail) => new()
    {
        Key = key,
        Label = label,
        State = state,
        CompletedUnits = state == "complete" ? 1 : 0,
        TotalUnits = 1,
        Detail = detail,
    };

    private static string ResolveGateState(
        IReadOnlyCollection<PresentationOperationRow> operations,
        string availability,
        string defaultState = "pending")
    {
        if (availability is "review" or "failed"
            || operations.Any(operation => operation.Status is "failed_terminal" or "dead_lettered" or "blocked"))
        {
            return "blocked";
        }

        if (operations.Any(operation => operation.Status is "retry_waiting" or "failed_retryable" or "interrupted"))
        {
            return "retry";
        }

        if (operations.Any(operation => IsActive(operation.Status)))
        {
            return "active";
        }

        if (operations.Count > 0 && operations.All(operation => IsSettledOperation(operation.Status)))
        {
            return "complete";
        }

        return defaultState;
    }

    private static bool IsMatchingOperation(PresentationOperationRow operation)
    {
        var value = $"{operation.OperationType} {operation.CapabilityId} {operation.Stage}".ToLowerInvariant();
        return value.Contains("retail_match")
               || value.Contains("wikidata_bridge")
               || value.Contains("retailsearch")
               || value.Contains("retailmatched")
               || value.Contains("bridgesearch")
               || value.Contains("qidresolved")
               || value.Contains("qidnomatch")
               || value.Contains("qidneedsreview");
    }

    private static bool IsSettledOperation(string? status) => status?.ToLowerInvariant() is
        "complete" or "completed" or "succeeded" or "ready" or "readywithoutuniverse"
        or "no_result" or "not_applicable" or "missing_confirmed" or "skipped";

    private static IngestionFacetStateDto Facet(
        string label,
        string facet,
        IReadOnlyList<PresentationOperationRow> operations,
        bool hasResult,
        bool applies)
    {
        if (!applies)
        {
            return IngestionFacetStateDto.NotApplicable(label);
        }

        var matching = operations.Where(operation => OperationFacet(operation) == facet).ToList();
        if (matching.Count == 0)
        {
            return hasResult
                ? new IngestionFacetStateDto { State = "complete", Label = $"{label} complete" }
                : IngestionFacetStateDto.NotApplicable(label);
        }
        var state = matching.Any(operation => operation.Status is "failed_terminal" or "dead_lettered")
            ? "failed"
            : matching.Any(operation => operation.Status is "retry_waiting" or "failed_retryable" or "interrupted")
                ? "attention"
                : matching.Any(operation => operation.Status is "running" or "processing" or "active")
                    ? "active"
                    : matching.Any(operation => operation.Status == "queued")
                        ? "pending"
                    : hasResult || (facet != "artwork" && matching.Any(operation => IsTerminalSuccess(operation.Status, operation.Status)))
                        ? "complete"
                        : "pending";
        if (facet == "artwork" && !hasResult && state == "pending"
            && matching.All(operation => IsTerminalSuccess(operation.Status, operation.Status)))
        {
            return IngestionFacetStateDto.NotApplicable("No artwork found");
        }

        return new IngestionFacetStateDto { State = state, Label = $"{label} {FacetStateLabel(state)}" };
    }

    private static string OperationFacet(PresentationOperationRow operation)
    {
        var value = $"{operation.OperationType} {operation.CapabilityId} {operation.Stage}".ToLowerInvariant();
        if (value.Contains("artwork") || value.Contains("image") || value.Contains("poster") || value.Contains("cover"))
        {
            return "artwork";
        }

        if (value.Contains("people") || value.Contains("person") || value.Contains("cast") || value.Contains("credit"))
        {
            return "people";
        }

        if (value.Contains("lyric") || value.Contains("subtitle") || value.Contains("text_track") || value.Contains("text track"))
        {
            return "text";
        }

        if (value.Contains("relationship") || value.Contains("universe") || value.Contains("collection") || value.Contains("series"))
        {
            return "relationship";
        }

        if (value.Contains("metadata") || value.Contains("identity") || value.Contains("retail") || value.Contains("wikidata") || value.Contains("hydrat"))
        {
            return "metadata";
        }

        return "other";
    }

    private static Guid PresentationGroupId(MediaPresentationRow row)
    {
        var mediaType = NormalizeMediaType(row.MediaType);
        return mediaType switch
        {
            "Music" => row.ParentWorkId ?? row.WorkId,
            "TV" => row.RootWorkId ?? row.ParentWorkId ?? row.WorkId,
            "Comics" => row.ParentWorkId ?? row.WorkId,
            "Audiobooks" when row.ParentWorkId.HasValue && ParsePositiveInt(row.AudiobookPartCount).HasValue => row.ParentWorkId.Value,
            _ => row.WorkId,
        };
    }

    private static string GroupTitle(string mediaType, MediaPresentationRow row) => mediaType switch
    {
        "Music" or "Comics" => MediaEngine.Domain.Services.StringHelpers.FirstNonBlankOr("Identifying media", row.ParentTitle, row.LeafTitle, row.DetectedTitle).Trim(),
        "TV" => MediaEngine.Domain.Services.StringHelpers.FirstNonBlankOr("Identifying show", row.RootTitle, row.ParentTitle, row.LeafTitle, row.DetectedTitle).Trim(),
        "Audiobooks" when row.ParentWorkId.HasValue && ParsePositiveInt(row.AudiobookPartCount).HasValue => MediaEngine.Domain.Services.StringHelpers.FirstNonBlankOr("Identifying audiobook", row.ParentTitle, row.LeafTitle, row.DetectedTitle).Trim(),
        _ => MediaEngine.Domain.Services.StringHelpers.FirstNonBlankOr("Identifying media", row.LeafTitle, row.DetectedTitle).Trim(),
    };

    private static string? GroupSubtitle(string mediaType, IReadOnlyList<MediaPresentationRow> rows)
    {
        var newest = rows.OrderByDescending(row => row.UpdatedAt).First();
        if (mediaType == "TV")
        {
            var seasons = rows.Select(row => ParsePositiveInt(row.SeasonNumber)).Where(value => value.HasValue).Select(value => value!.Value).Distinct().Order().ToList();
            var episodeCount = rows.Select(row => row.WorkId).Distinct().Count();
            return seasons.Count switch
            {
                1 => $"Season {seasons[0]}",
                > 1 => $"{seasons.Count:N0} seasons · {episodeCount:N0} episodes added",
                _ => "TV Show",
            };
        }
        return MediaEngine.Domain.Services.StringHelpers.FirstNonBlank(newest.Creator, mediaType)?.Trim();
    }

    private static string GroupStatus(string availability, IReadOnlyList<PresentationOperationRow> operations)
    {
        if (availability == "review")
        {
            return "Needs review";
        }

        if (availability == "failed")
        {
            return "Failed";
        }

        var active = operations.Where(operation => IsActive(operation.Status)).OrderByDescending(operation => operation.UpdatedAt).FirstOrDefault();
        if (active is not null)
        {
            return OperationFacet(active) switch
            {
                "people" => "Adding people",
                "artwork" => "Finishing artwork",
                "relationship" => "Building relationships",
                "text" when $"{active.OperationType} {active.CapabilityId}".Contains("lyric", StringComparison.OrdinalIgnoreCase) => "Adding lyrics",
                "text" => "Adding subtitles",
                "metadata" => availability == "finishing" ? "Finishing details" : "Identifying",
                _ => availability == "finishing" ? "Finishing details" : "Adding",
            };
        }
        return availability switch { "ready" => "Ready", "finishing" => "Finishing details", _ => "Adding" };
    }

    private static string? ChildUnit(string mediaType, IReadOnlyList<MediaPresentationRow> rows) => mediaType switch
    {
        "Music" => "tracks",
        "TV" => "episodes",
        "Comics" => "issues",
        "Audiobooks" when rows.Select(row => row.WorkId).Distinct().Count() > 1 => "files",
        _ => null,
    };

    private static string? DetailRoute(string mediaType, Guid groupId) => mediaType switch
    {
        "TV" => $"/details/tvshow/{groupId:D}?context=watch",
        "Music" => $"/details/musicalbum/{groupId:D}?context=listen",
        "Movies" => $"/details/work/{groupId:D}?context=watch",
        "Audiobooks" => $"/details/work/{groupId:D}?context=listen",
        "Books" => $"/details/work/{groupId:D}?context=read",
        "Comics" => $"/details/work/{groupId:D}?context=comics",
        _ => $"/details/work/{groupId:D}",
    };

    private static string BuildSequenceLabel(MediaPresentationRow row)
    {
        var mediaType = NormalizeMediaType(row.MediaType);
        return mediaType switch
        {
            "TV" when ParsePositiveInt(row.EpisodeNumber) is { } episode => $"E{episode:00}",
            "Music" when ParsePositiveInt(row.TrackNumber) is { } track => track.ToString(CultureInfo.InvariantCulture),
            "Comics" when !string.IsNullOrWhiteSpace(row.IssueNumber) => $"#{row.IssueNumber}",
            _ => "",
        };
    }

    private static string? FormatChildDuration(string mediaType, string? rawDuration)
    {
        if (string.IsNullOrWhiteSpace(rawDuration))
        {
            return null;
        }

        if (NormalizeMediaType(mediaType) is not ("Music" or "Audiobooks"))
        {
            return rawDuration;
        }

        if (!double.TryParse(rawDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
        {
            return rawDuration;
        }

        var duration = TimeSpan.FromSeconds(Math.Round(minutes * 60d, MidpointRounding.AwayFromZero));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }

    private async Task<CurrentGroupFacts> ReadCurrentGroupFactsAsync(CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleAsync<CurrentGroupFacts>(new CommandDefinition($"""
            WITH current_batches AS (
                SELECT b.id
                FROM ingestion_batches b
                WHERE {IngestionBatchActivitySql.IsActive}
            ),
            latest_logs AS (
                SELECT *
                FROM (
                    SELECT il.*,
                           ROW_NUMBER() OVER (
                               PARTITION BY il.ingestion_run_id, il.media_asset_id
                               ORDER BY il.updated_at DESC, il.created_at DESC) AS rn
                    FROM ingestion_log il
                    JOIN current_batches cb ON cb.id = il.ingestion_run_id
                    WHERE il.media_asset_id IS NOT NULL
                )
                WHERE rn = 1
            ),
            latest_file_operations AS (
                SELECT *
                FROM (
                    SELECT mo.*,
                           ROW_NUMBER() OVER (
                               PARTITION BY mo.batch_id, mo.entity_id
                               ORDER BY COALESCE(mo.updated_at, mo.completed_at, mo.started_at, mo.created_at) DESC) AS rn
                    FROM media_operations mo
                    JOIN current_batches cb ON cb.id = mo.batch_id
                    WHERE mo.operation_type = 'ingestion.file'
                )
                WHERE rn = 1
            ),
            scoped AS (
                SELECT ll.ingestion_run_id AS BatchId,
                       {PresentationGroupSql} AS GroupId,
                       ll.media_asset_id AS AssetId,
                       w.id AS WorkId,
                       p.id AS ParentWorkId,
                       gp.id AS RootWorkId,
                       CASE WHEN {IdentityReadySql}
                            THEN 1 ELSE 0 END AS IntakeComplete,
                       CASE WHEN LOWER(COALESCE(lfo.status, '')) IN ('failed_terminal','dead_lettered') THEN 1 ELSE 0 END AS RowFailed,
                       CASE WHEN EXISTS (
                           SELECT 1
                           FROM review_queue rq
                           WHERE rq.entity_id IN (ll.media_asset_id, w.id, p.id, gp.id)
                             AND rq.status = 'Pending'
                             AND rq.review_ready_at IS NOT NULL)
                           OR EXISTS (SELECT 1 FROM ({WorkIdentityJobsSql}) job
                               WHERE job.state IN ('RetailNoMatch','QidNoMatch','QidNeedsReview','RetailMatchedNeedsReview'))
                           THEN 1 ELSE 0 END AS NeedsReview
                FROM latest_logs ll
                JOIN media_assets ma ON ma.id = ll.media_asset_id
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                LEFT JOIN latest_file_operations lfo
                  ON lfo.batch_id = ll.ingestion_run_id AND lfo.entity_id = ll.media_asset_id
                WHERE {PresentationTitleSql}
            ),
            group_base AS (
                SELECT BatchId,
                       GroupId,
                       MIN(IntakeComplete) AS IntakeComplete,
                       MAX(RowFailed) AS RowFailed,
                       MAX(NeedsReview) AS NeedsReview
                FROM scoped
                GROUP BY BatchId, GroupId
            ),
            group_members AS (
                SELECT BatchId, GroupId, AssetId AS EntityId FROM scoped
                UNION
                SELECT BatchId, GroupId, WorkId FROM scoped
                UNION
                SELECT BatchId, GroupId, ParentWorkId FROM scoped WHERE ParentWorkId IS NOT NULL
                UNION
                SELECT BatchId, GroupId, RootWorkId FROM scoped WHERE RootWorkId IS NOT NULL
            ),
            operation_facts AS (
                SELECT gm.BatchId,
                       gm.GroupId,
                       MAX(CASE WHEN mo.status IN ('failed_terminal','dead_lettered') THEN 1 ELSE 0 END) AS HasFailure,
                       MAX(CASE WHEN mo.operation_type <> 'ingestion.file'
                                     AND mo.status IN ('pending','queued','leased','running','processing','active','retry_waiting','failed_retryable','interrupted')
                                THEN 1 ELSE 0 END) AS HasBackgroundWork
                FROM group_members gm
                LEFT JOIN media_operations mo ON mo.batch_id = gm.BatchId AND mo.entity_id = gm.EntityId
                GROUP BY gm.BatchId, gm.GroupId
            ),
            classified AS (
                SELECT gb.*,
                       COALESCE(ofx.HasFailure, 0) AS HasFailure,
                       MAX(COALESCE(ofx.HasBackgroundWork, 0),
                           CASE WHEN EXISTS (SELECT 1 FROM group_members gm JOIN identity_jobs ij
                               ON ij.ingestion_run_id = gm.BatchId AND ij.entity_id = gm.EntityId
                               WHERE gm.BatchId = gb.BatchId AND gm.GroupId = gb.GroupId
                                 AND ij.state IN ('Queued','RetailSearching','RetailMatched','RetailMatchedNeedsReview','BridgeSearching','QidResolved','Hydrating','UniverseEnriching'))
                           THEN 1 ELSE 0 END) AS HasBackgroundWork
                FROM group_base gb
                LEFT JOIN operation_facts ofx ON ofx.BatchId = gb.BatchId AND ofx.GroupId = gb.GroupId
            )
            SELECT COUNT(*) AS TotalGroups,
                   COUNT(CASE WHEN NeedsReview = 0 AND RowFailed = 0 AND HasFailure = 0 AND IntakeComplete = 1 AND HasBackgroundWork = 0 THEN 1 END) AS ReadyGroups,
                   COUNT(CASE WHEN NeedsReview = 0 AND RowFailed = 0 AND HasFailure = 0 AND IntakeComplete = 1 AND HasBackgroundWork = 1 THEN 1 END) AS FinishingGroups,
                   COUNT(CASE WHEN NeedsReview = 1 THEN 1 END) AS ReviewGroups
            FROM classified;
            """, cancellationToken: ct)).ConfigureAwait(false);
    }

    private async Task<HistoryAggregateFacts> ReadHistoryAggregateFactsAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        DateTimeOffset todayStart,
        DateTimeOffset tomorrowStart,
        CancellationToken ct)
    {
        return await conn.QuerySingleAsync<HistoryAggregateFacts>(new CommandDefinition($"""
            WITH latest_logs AS (
                SELECT *
                FROM (
                    SELECT il.*,
                           ROW_NUMBER() OVER (
                               PARTITION BY il.ingestion_run_id, il.media_asset_id
                               ORDER BY il.updated_at DESC, il.created_at DESC) AS rn
                    FROM ingestion_log il
                    WHERE il.ingestion_run_id IS NOT NULL
                      AND il.media_asset_id IS NOT NULL
                )
                WHERE rn = 1
            ),
            scoped AS (
                SELECT ll.ingestion_run_id AS BatchId,
                       {PresentationGroupSql} AS GroupId,
                       MIN({AddedAtSql}) AS AddedAt
                FROM latest_logs ll
                JOIN ingestion_batches b ON b.id = ll.ingestion_run_id
                JOIN media_assets ma ON ma.id = ll.media_asset_id
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE NOT {IngestionBatchActivitySql.IsActive}
                  AND {AdditionBatchPredicate}
                  AND {PresentationTitleSql}
                GROUP BY ll.ingestion_run_id, {PresentationGroupSql}
            ),
            latest_batch AS (
                SELECT BatchId
                FROM scoped
                GROUP BY BatchId
                ORDER BY MAX(AddedAt) DESC, HEX(BatchId)
                LIMIT 1
            )
            SELECT COUNT(CASE
                       WHEN julianday(AddedAt) >= julianday(@todayStart)
                        AND julianday(AddedAt) < julianday(@tomorrowStart)
                       THEN 1 END) AS ItemsAddedToday,
                   (SELECT COUNT(*)
                    FROM scoped
                    WHERE BatchId = (SELECT BatchId FROM latest_batch)) AS LastGroupsAdded
            FROM scoped;
            """, new
        {
            todayStart = todayStart.ToString("O"),
            tomorrowStart = tomorrowStart.ToString("O"),
        }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static double SequenceSort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return double.MaxValue;
        }

        var numeric = new string(value.Where(character => char.IsDigit(character) || character == '.').ToArray());
        return double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.MaxValue;
    }

    private async Task<CurrentBatchFacts> ReadCurrentBatchFactsAsync(CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<CurrentBatchFacts>(new CommandDefinition($"""
            SELECT
                b.id AS BatchId,
                CASE WHEN {IngestionBatchActivitySql.IsActive}
                     THEN 1 ELSE 0 END AS IsRunning,
                b.files_total AS FilesTotal,
                b.files_processed AS FilesProcessed,
                b.started_at AS StartedAt,
                COALESCE(b.completed_at,b.updated_at,b.started_at) AS LastActivityAt
            FROM ingestion_batches b
            ORDER BY IsRunning DESC, COALESCE(b.completed_at,b.updated_at,b.started_at) DESC
            LIMIT 1;
            """, cancellationToken: ct)).ConfigureAwait(false) ?? new CurrentBatchFacts();
    }

    private async Task<CurrentOperationFacts> ReadCurrentOperationFactsAsync(CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleAsync<CurrentOperationFacts>(new CommandDefinition("""
            SELECT
                COUNT(CASE WHEN status IN ('leased','running','processing','active') THEN 1 END) + (SELECT COUNT(*) FROM identity_jobs WHERE state IN ('RetailSearching','BridgeSearching','Hydrating','UniverseEnriching')) AS Active,
                COUNT(CASE WHEN status IN ('pending','queued') THEN 1 END) + (SELECT COUNT(*) FROM identity_jobs WHERE state IN ('Queued','RetailMatched','QidResolved')) AS Queued,
                COUNT(CASE WHEN status IN ('retry_waiting','failed_retryable','interrupted') THEN 1 END) AS RetryWaiting,
                COUNT(CASE WHEN status IN ('pending','queued','leased','running','processing','active','retry_waiting','failed_retryable','interrupted')
                             AND (LOWER(operation_type) LIKE '%lyric%' OR LOWER(operation_type) LIKE '%subtitle%' OR LOWER(COALESCE(capability_id,'')) LIKE '%lyric%' OR LOWER(COALESCE(capability_id,'')) LIKE '%subtitle%') THEN 1 END) AS TextTrackWaiting
            FROM media_operations;
            """, cancellationToken: ct)).ConfigureAwait(false);
    }

    private async Task<int> ReadPendingReviewCountAsync(CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM review_queue
            WHERE status = 'Pending' AND review_ready_at IS NOT NULL;
            """, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task<List<ActivityBatchMilestoneDto>> BuildMilestonesAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        BatchPresentationRow batch,
        CancellationToken ct)
    {
        var operationRows = (await conn.QueryAsync<MilestoneRow>(new CommandDefinition("""
            SELECT operation_type AS OperationType, capability_id AS CapabilityId, stage AS Stage,
                   status AS Status, MIN(COALESCE(started_at,created_at)) AS StartedAt,
                   MAX(COALESCE(completed_at,updated_at,started_at,created_at)) AS CompletedAt
            FROM media_operations
            WHERE batch_id = @batchId
            GROUP BY operation_type, capability_id, stage, status;
            """, new { batchId = batch.BatchId }, cancellationToken: ct)).ConfigureAwait(false)).AsList();

        var milestones = new List<ActivityBatchMilestoneDto>();
        if (batch.FilesProcessed > 0)
        {
            milestones.Add(new ActivityBatchMilestoneDto
            {
                Label = "Files scanned",
                State = batch.FailureCount > 0 ? "attention" : "complete",
                Detail = $"{batch.FilesProcessed:N0} files processed",
                OccurredAt = batch.CompletedAt ?? batch.StartedAt,
            });
        }

        foreach (var facet in new[] { "metadata", "artwork", "people", "text", "relationship" })
        {
            var matching = operationRows.Where(row => OperationFacet(new PresentationOperationRow
            {
                OperationType = row.OperationType,
                CapabilityId = row.CapabilityId,
                Stage = row.Stage,
            }) == facet).ToList();
            if (matching.Count == 0)
            {
                continue;
            }

            var state = matching.Any(row => row.Status is "failed_terminal" or "dead_lettered") ? "failed"
                : matching.Any(row => row.Status is "retry_waiting" or "failed_retryable" or "interrupted") ? "attention"
                : matching.Any(row => IsActive(row.Status)) ? "active" : "complete";
            var started = matching.Min(row => row.StartedAt);
            var completed = matching.Max(row => row.CompletedAt);
            milestones.Add(new ActivityBatchMilestoneDto
            {
                Label = facet switch
                {
                    "metadata" => "Media identified",
                    "artwork" => "Artwork downloaded",
                    "people" => "People and credits added",
                    "text" => "Lyrics and subtitles added",
                    _ => "Relationships built",
                },
                State = state,
                Detail = state == "complete" && started.HasValue && completed.HasValue
                    ? $"Completed in {FormatDuration(completed.Value - started.Value)}"
                    : FacetStateLabel(state),
                OccurredAt = completed,
            });
        }
        return milestones.OrderBy(item => item.OccurredAt).ToList();
    }

    private static string DisplayBatchName(
        string? category,
        string? source,
        IReadOnlyList<IngestionMediaGroupDto> groups,
        bool groupsAreComplete = true)
    {
        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("Mixed", StringComparison.OrdinalIgnoreCase))
        {
            return category.EndsWith("import", StringComparison.OrdinalIgnoreCase) ? category : $"{category} import";
        }

        var lanes = groupsAreComplete
            ? groups.Select(group => LaneFor(group.MediaType)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        if (lanes.Count == 1)
        {
            return $"{lanes[0]} import";
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(name))
            {
                return $"{name} scan";
            }
        }
        return "Mixed media scan";
    }

    private static string BatchSummary(
        string status,
        IReadOnlyList<IngestionMediaGroupDto> groups,
        int followUpCount,
        bool groupsAreComplete = true)
    {
        var lane = groupsAreComplete
            ? groups.Select(group => LaneFor(group.MediaType)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        var destination = lane.Count == 1 ? $" in {lane[0]}" : " in the library";
        if (status.Equals("running", StringComparison.OrdinalIgnoreCase))
        {
            return "This batch is still adding media. Finished items are available while the remaining work continues.";
        }

        if (status.Equals("failed", StringComparison.OrdinalIgnoreCase))
        {
            return "This run failed before all media could be added.";
        }

        if (status is "abandoned" or "interrupted")
        {
            return "This run was interrupted. Completed items remain available.";
        }

        return followUpCount > 0
            ? $"This run completed. Most items are now available{destination}; a small number still need follow-up."
            : $"This run completed successfully. Its items are now available{destination}.";
    }

    private static PagedResponse<T> Page<T>(IReadOnlyList<T> items, PagedRequest request)
    {
        var page = items.Skip(request.Offset).Take(request.Limit + 1).ToList();
        return PagedResponse<T>.FromPage(page, request, items.Count);
    }

    private static void ApplyArtworkSize(IEnumerable<IngestionMediaGroupDto> groups, string size)
    {
        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.CoverUrl))
            {
                continue;
            }

            var parts = group.CoverUrl.Split('?', 2);
            List<string> query = parts.Length == 1
                ? []
                : parts[1]
                    .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(value => !value.StartsWith("size=", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            query.Add($"size={size}");
            group.CoverUrl = $"{parts[0]}?{string.Join('&', query)}";
        }
    }

    private static void ApplyHistoricalProgress(IEnumerable<IngestionMediaGroupDto> groups)
    {
        foreach (var group in groups)
        {
            group.ChildExpected = null;
        }
    }

    private Task<bool> IsHistoricalBatchAsync(Guid batchId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        return Task.FromResult(conn.ExecuteScalar<bool>($"""
            SELECT EXISTS (SELECT 1 FROM ingestion_batches b
                WHERE b.id = @batchId AND NOT {IngestionBatchActivitySql.IsActive});
            """, new { batchId }));
    }

    private static bool IsActive(string? status) => !string.IsNullOrWhiteSpace(status) && ActiveStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);
    private static bool IsFileIntake(string? operationType) => operationType?.Equals("ingestion.file", StringComparison.OrdinalIgnoreCase) == true;
    private static bool IsTerminalSuccess(string? first, string? second) => new[] { first, second }.Any(value => value is not null && value.ToLowerInvariant() is "complete" or "completed" or "succeeded" or "ready" or "readywithoutuniverse" or "registered");
    private static string FriendlyOperationState(string? status) => status switch { "running" or "processing" or "active" => "active", "failed_terminal" or "dead_lettered" => "failed", "retry_waiting" or "failed_retryable" => "waiting", _ => "pending" };
    private static string FacetStateLabel(string state) => state switch { "complete" => "complete", "active" => "in progress", "attention" => "waiting to retry", "failed" => "failed", "pending" => "queued", _ => state };

    private static string NormalizeMediaType(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? "";
        if (normalized.Contains("audio") && normalized.Contains("book"))
        {
            return "Audiobooks";
        }

        if (normalized.Contains("comic"))
        {
            return "Comics";
        }

        if (normalized is "tv" or "television" or "tv shows" or "show" or "shows")
        {
            return "TV";
        }

        if (normalized is "movie" or "movies" or "film" or "films")
        {
            return "Movies";
        }

        if (normalized is "music" or "album" or "albums" or "track" or "tracks" or "song" or "songs")
        {
            return "Music";
        }

        if (normalized is "book" or "books" or "ebook" or "ebooks" or "epub" or "pdf")
        {
            return "Books";
        }

        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
    }

    private static string LaneFor(string mediaType) => NormalizeMediaType(mediaType) switch
    {
        "Books" or "Comics" => "Read",
        "Movies" or "TV" => "Watch",
        "Music" or "Audiobooks" => "Listen",
        _ => "Other",
    };

    private static string? NormalizeLaneFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim().ToLowerInvariant();

    private static string NormalizeHistorySort(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "oldest" => "oldest",
            "title" => "title",
            "media" => "media",
            _ => "newest",
        };

    private static bool IsPlaceholderTitle(string? value) => value is null
        || value.Equals("Identifying media", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Identifying show", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Identifying audiobook", StringComparison.OrdinalIgnoreCase);

    private static int? ParsePositiveInt(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;
    private static string GuidHex(Guid value) => Convert.ToHexString(MediaEngine.Storage.GuidSql.ToBlob(value)).ToLowerInvariant();
    private static string Pluralize(string word, int count) => count == 1 ? word : $"{word}s";
    private static DateTimeOffset StartOfWeek(DateTimeOffset value) => value.Date.AddDays(-((7 + (int)value.DayOfWeek - (int)DayOfWeek.Monday) % 7));
    private static string FormatDuration(TimeSpan value) => value.TotalMinutes >= 1 ? $"{(int)value.TotalMinutes}m {value.Seconds}s" : $"{Math.Max(0, value.Seconds)}s";

    private const string PresentationGroupSql = """
        CASE
            WHEN LOWER(TRIM(COALESCE(NULLIF(w.media_type, ''), NULLIF(ll.media_type, ''), ''))) IN
                 ('music', 'album', 'albums', 'track', 'tracks', 'song', 'songs')
                THEN COALESCE(p.id, w.id)
            WHEN LOWER(TRIM(COALESCE(NULLIF(w.media_type, ''), NULLIF(ll.media_type, ''), ''))) IN
                 ('tv', 'television', 'tv shows', 'show', 'shows')
                THEN COALESCE(gp.id, p.id, w.id)
            WHEN LOWER(TRIM(COALESCE(NULLIF(w.media_type, ''), NULLIF(ll.media_type, ''), ''))) LIKE '%comic%'
                THEN COALESCE(p.id, w.id)
            WHEN LOWER(TRIM(COALESCE(NULLIF(w.media_type, ''), NULLIF(ll.media_type, ''), ''))) LIKE '%audio%'
                 AND LOWER(TRIM(COALESCE(NULLIF(w.media_type, ''), NULLIF(ll.media_type, ''), ''))) LIKE '%book%'
                 AND p.id IS NOT NULL
                 AND EXISTS (
                     SELECT 1
                     FROM canonical_values audiobook_parts
                     WHERE audiobook_parts.entity_id IN (ll.media_asset_id, w.id, p.id)
                       AND audiobook_parts.key = 'audiobook_part_count'
                       AND CAST(audiobook_parts.value AS INTEGER) > 0)
                THEN p.id
            ELSE w.id
        END
        """;

    private const string NormalizedMediaTypeSql = """
        LOWER(TRIM(COALESCE(NULLIF(w.media_type, ''), NULLIF(ll.media_type, ''), '')))
        """;

    private const string HistorySearchSql = """
        (
            LOWER(COALESCE(ll.detected_title, '')) LIKE @search
            OR EXISTS (
                SELECT 1
                FROM canonical_values search_value
                WHERE search_value.entity_id IN (w.id, p.id, gp.id)
                  AND search_value.key IN ('title','episode_title','issue_title','album','show_name','series','book_title','artist','album_artist','author','creator','narrator')
                  AND LOWER(search_value.value) LIKE @search)
            OR EXISTS (
                SELECT 1
                FROM canonical_value_arrays search_array
                WHERE search_array.entity_id IN (w.id, p.id, gp.id)
                  AND search_array.key IN ('artist','album_artist','author','creator','narrator')
                  AND LOWER(search_array.value) LIKE @search)
        )
        """;

    private static readonly string PresentationTitleSql = $"""
        (
            NULLIF(TRIM(COALESCE(ll.detected_title, '')), '') IS NOT NULL
            OR EXISTS (
                SELECT 1 FROM canonical_values leaf_title
                WHERE leaf_title.entity_id = w.id
                  AND leaf_title.key IN ('title','episode_title','issue_title')
                  AND NULLIF(TRIM(COALESCE(leaf_title.value, '')), '') IS NOT NULL)
            OR (
                (
                    {NormalizedMediaTypeSql} IN ('music','album','albums','track','tracks','song','songs')
                    OR {NormalizedMediaTypeSql} LIKE '%comic%'
                    OR {NormalizedMediaTypeSql} IN ('tv','television','tv shows','show','shows')
                    OR (
                        {NormalizedMediaTypeSql} LIKE '%audio%'
                        AND {NormalizedMediaTypeSql} LIKE '%book%'
                        AND EXISTS (
                            SELECT 1 FROM canonical_values audiobook_parts
                            WHERE audiobook_parts.entity_id IN (ll.media_asset_id, w.id, p.id)
                              AND audiobook_parts.key = 'audiobook_part_count'
                              AND CAST(audiobook_parts.value AS INTEGER) > 0)))
                AND EXISTS (
                    SELECT 1 FROM canonical_values parent_title
                    WHERE parent_title.entity_id = p.id
                      AND parent_title.key IN ('title','album','show_name','series','book_title')
                      AND NULLIF(TRIM(COALESCE(parent_title.value, '')), '') IS NOT NULL))
            OR (
                {NormalizedMediaTypeSql} IN ('tv','television','tv shows','show','shows')
                AND EXISTS (
                    SELECT 1 FROM canonical_values root_title
                    WHERE root_title.entity_id = gp.id
                      AND root_title.key IN ('title','show_name','series')
                      AND NULLIF(TRIM(COALESCE(root_title.value, '')), '') IS NOT NULL))
        )
        """;

    // A multi-file audiobook shares one work identity. Intake completion alone
    // cannot make a known unmatched or still-processing work a completed addition.
    private const string WorkIdentityJobsSql = """
        SELECT ij.* FROM identity_jobs ij
        JOIN media_assets identity_asset ON identity_asset.id = ij.entity_id
        JOIN editions identity_edition ON identity_edition.id = identity_asset.edition_id
        WHERE identity_edition.work_id = w.id
        """;

    private static readonly string IdentityReadySql = $"""
        (EXISTS (SELECT 1 FROM ({WorkIdentityJobsSql}) job WHERE job.state IN ('Ready','ReadyWithoutUniverse'))
         AND NOT EXISTS (SELECT 1 FROM ({WorkIdentityJobsSql}) job WHERE job.state NOT IN ('Ready','ReadyWithoutUniverse'))
         OR (NOT EXISTS ({WorkIdentityJobsSql})
             AND LOWER(COALESCE(ll.status, '')) IN ('complete','completed','succeeded','ready','readywithoutuniverse','registered')))
        """;

    private static readonly string AddedAtSql = $"""
        CASE WHEN {IdentityReadySql} THEN COALESCE(
            (SELECT MAX(job.updated_at) FROM ({WorkIdentityJobsSql}) job), ma.presented_at) END
        """;

    private static readonly string AdditionBatchPredicate = $"""
        {AddedAtSql} IS NOT NULL
        AND b.id = (
            SELECT prior_log.ingestion_run_id
            FROM ingestion_log prior_log
            WHERE prior_log.media_asset_id = ma.id
              AND prior_log.ingestion_run_id IS NOT NULL
              AND julianday(prior_log.created_at) <= julianday({AddedAtSql})
            ORDER BY julianday(prior_log.created_at) DESC, julianday(prior_log.updated_at) DESC
            LIMIT 1
        )
        """;

    private enum GroupScope { Current, History, Batch, BatchAdditions, BatchAdditionsSet }

    private sealed class MediaPresentationRow
    {
        public Guid LogId { get; set; }
        public Guid BatchId { get; set; }
        public Guid AssetId { get; set; }
        public Guid EditionId { get; set; }
        public Guid WorkId { get; set; }
        public Guid? ParentWorkId { get; set; }
        public Guid? RootWorkId { get; set; }
        public string MediaType { get; set; } = "Unknown";
        public string? DetectedTitle { get; set; }
        public string? LeafTitle { get; set; }
        public string? ParentTitle { get; set; }
        public string? RootTitle { get; set; }
        public string? Creator { get; set; }
        public string? SeasonNumber { get; set; }
        public string? EpisodeNumber { get; set; }
        public string? TrackNumber { get; set; }
        public string? IssueNumber { get; set; }
        public string? AudiobookPartCount { get; set; }
        public string? DurationLabel { get; set; }
        public bool IdentityReady { get; set; }
        public string? IdentityState { get; set; }
        public string? LogStatus { get; set; }
        public string? OperationStatus { get; set; }
        public string? OperationStage { get; set; }
        public DateTimeOffset? PresentedAt { get; set; }
        public DateTimeOffset AddedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public int ReviewCount { get; set; }
        public int PeopleCount { get; set; }
        public int TextTrackCount { get; set; }
        public Guid? CoverAssetId { get; set; }
    }

    private sealed class PresentationGroupKeyRow
    {
        public Guid BatchId { get; set; }
        public Guid GroupId { get; set; }
        public int TotalCount { get; set; }
    }

    private sealed class RecentDayGroupKeyRow
    {
        public Guid BatchId { get; set; }
        public Guid GroupId { get; set; }
        public string LocalDate { get; set; } = "";
        public int TotalCount { get; set; }
        public int GroupRank { get; set; }
    }

    private sealed class HistoryAggregateFacts
    {
        public int ItemsAddedToday { get; set; }
        public int LastGroupsAdded { get; set; }
    }

    private sealed class CurrentGroupFacts
    {
        public int TotalGroups { get; set; }
        public int ReadyGroups { get; set; }
        public int FinishingGroups { get; set; }
        public int ReviewGroups { get; set; }
    }

    private sealed class PresentationOperationRow
    {
        public Guid BatchId { get; set; }
        public Guid? EntityId { get; set; }
        public string OperationType { get; set; } = "";
        public string? CapabilityId { get; set; }
        public string Status { get; set; } = "";
        public string? Stage { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class CurrentBatchFacts
    {
        public Guid? BatchId { get; set; }
        public bool IsRunning { get; set; }
        public int FilesTotal { get; set; }
        public int FilesProcessed { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? LastActivityAt { get; set; }
    }

    private sealed class CurrentOperationFacts { public int Active { get; set; } public int Queued { get; set; } public int RetryWaiting { get; set; } public int TextTrackWaiting { get; set; } }
    private sealed class ActivitySummaryRow { public int CompletedRunsThisWeek { get; set; } public DateTimeOffset? LastActivityAt { get; set; } }
    private sealed class LatestBatchRow { public int FilesProcessed { get; set; } }
    private sealed class BatchPresentationRow { public Guid BatchId { get; set; } public string Status { get; set; } = ""; public string? Source { get; set; } public string? Category { get; set; } public int FilesProcessed { get; set; } public int ReviewCount { get; set; } public int FailureCount { get; set; } public DateTimeOffset StartedAt { get; set; } public DateTimeOffset? CompletedAt { get; set; } }
    private sealed class BatchMetricRow { public int PeopleUpdated { get; set; } public int ArtworkAdded { get; set; } public int TextTracksAdded { get; set; } }
    private sealed class MilestoneRow { public string OperationType { get; set; } = ""; public string? CapabilityId { get; set; } public string? Stage { get; set; } public string Status { get; set; } = ""; public DateTimeOffset? StartedAt { get; set; } public DateTimeOffset? CompletedAt { get; set; } }
}
