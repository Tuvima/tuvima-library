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

    /// <summary>
    /// Enough in-flight rows for the Dashboard's Now panel (at most 10 shown) to rotate from, ordered most recently active first.
    /// </summary>
    public const int DefaultCurrentLimit = 12;

    private readonly IDatabaseConnection _db;
    private readonly IIngestionQueueProgressService? _queue;
    private readonly MediaEngine.Api.Services.LocalAssets.PersonalMediaActivityRecorder? _personalMedia;
    private readonly IIdentityBypassLibraries? _bypassLibraries;

    public IngestionPresentationReadService(
        IDatabaseConnection db,
        IIngestionQueueProgressService? queue = null,
        MediaEngine.Api.Services.LocalAssets.PersonalMediaActivityRecorder? personalMedia = null,
        IIdentityBypassLibraries? bypassLibraries = null)
    {
        _db = db;
        _queue = queue;
        _personalMedia = personalMedia;
        _bypassLibraries = bypassLibraries;
    }

    public async Task<IngestionPresentationSnapshotDto> GetSnapshotAsync(
        int currentLimit = DefaultCurrentLimit,
        int recentDayLimit = 3,
        int recentItemsPerDay = 9,
        CancellationToken ct = default)
    {
        currentLimit = Math.Clamp(currentLimit, 1, 20);
        recentDayLimit = Math.Clamp(recentDayLimit, 1, 7);
        recentItemsPerDay = Math.Clamp(recentItemsPerDay, 1, 12);

        var currentPage = await LoadCurrentGroupPageAsync(PagedRequest.From(0, currentLimit, currentLimit, 20), ct, eligibleOnly: true).ConfigureAwait(false);
        var current = currentPage.Items;
        var queue = _queue?.Current;
        var currentFacts = await ReadCurrentGroupFactsAsync(queue?.StartedAt, ct).ConfigureAwait(false);
        var recentDays = await LoadRecentDaysAsync(recentDayLimit, recentItemsPerDay, ct).ConfigureAwait(false);
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
                Route = "/settings/review",
            });
        }

        if (operationFacts.RetryWaiting > 0)
        {
            attention.Add(new IngestionAttentionItemDto
            {
                Kind = "provider",
                Count = operationFacts.RetryWaiting,
                Label = $"{operationFacts.RetryWaiting:N0} {Pluralize("item", operationFacts.RetryWaiting)} waiting on provider data",
                Description = "Library will retry automatically",
                // Shown on the page it would link to, so it is information, not a link.
                Route = null,
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
                Route = null,
            });
        }

        var isRunning = (currentPage.TotalCount ?? current.Count) > 0
            || operationFacts.Active + operationFacts.Queued + operationFacts.RetryWaiting > 0;
        return new IngestionPresentationSnapshotDto
        {
            IsRunning = isRunning,
            Status = isRunning ? "active" : "idle",
            FilesDiscovered = queue?.FilesFound ?? 0,
            FilesProcessed = queue?.FilesDone ?? 0,
            LibraryGroups = currentFacts.TotalGroups,
            ReadyGroups = currentFacts.ReadyGroups,
            FinishingGroups = currentFacts.FinishingGroups,
            ReviewGroups = currentFacts.ReviewGroups,
            ActiveOperations = operationFacts.Active,
            QueuedOperations = operationFacts.Queued,
            RetryWaitingOperations = operationFacts.RetryWaiting,
            StartedAt = queue?.StartedAt,
            LastActivityAt = queue?.LastActivityAt ?? recentDays.FirstOrDefault()?.Items.FirstOrDefault()?.AddedAt,
            CurrentMedia = current.ToList(),
            CurrentMediaTotal = currentPage.TotalCount ?? current.Count,
            Attention = attention,
            RecentDays = recentDays,
            PersonalMediaInProgress = ReadPersonalMediaInProgress(),
            GeneratedAt = DateTimeOffset.UtcNow,
        };
    }

    private IngestionPersonalMediaProgressDto ReadPersonalMediaInProgress()
    {
        var (photos, videos) = _personalMedia?.GetInProgress() ?? (0, 0);
        return new IngestionPersonalMediaProgressDto { Photos = photos, Videos = videos };
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
        string? status = null,
        CancellationToken ct = default)
    {
        var request = PagedRequest.From(offset, limit, 50, 100);
        return await LoadHistoryGroupPageAsync(search, lane, start, end, sort, status, request, ct).ConfigureAwait(false);
    }

    public async Task<IngestionMediaGroupDto?> GetMediaGroupAsync(
        Guid groupId,
        CancellationToken ct = default)
    {
        var groups = await LoadGroupsAsync([groupId], addedOnly: false, ct).ConfigureAwait(false);
        ApplyArtworkSize(groups, "m");
        return groups.FirstOrDefault(item => item.GroupId == groupId);
    }

    public async Task<PagedResponse<IngestionMediaChildDto>> GetChildrenAsync(
        Guid groupId,
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        var request = PagedRequest.From(offset, limit, 50, 250);
        var rows = await LoadRowsAsync([groupId], addedOnly: false, ct).ConfigureAwait(false);
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
                        : row.IsReady ? "complete" : FriendlyOperationState(row.OperationStatus),
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
        var pending = await ReadPendingReviewCountAsync(ct).ConfigureAwait(false);
        var localToday = DateTime.Today;
        var todayStart = new DateTimeOffset(localToday, TimeZoneInfo.Local.GetUtcOffset(localToday)).ToUniversalTime();
        var tomorrowStart = todayStart.AddDays(1);
        var history = await ReadHistoryAggregateFactsAsync(conn, todayStart, tomorrowStart, ct).ConfigureAwait(false);

        return new ActivityHistorySummaryDto
        {
            ItemsAddedToday = history.ItemsAddedToday,
            ItemsNeedingFollowUp = pending,
            LastActivityAt = history.LastActivityAt,
        };
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
            : await LoadGroupsAsync(pageKeys.Select(key => key.GroupId).Distinct().ToArray(), addedOnly: false, ct).ConfigureAwait(false);
        ApplyArtworkSize(groups, "m");

        var ordered = OrderByKeys(pageKeys, groups);
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
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        // One row per group per local day, from each file's own first-Ready time: a show that got episodes on two days
        // appears on both, counted by the episodes added on each.
        var dayScopeCtes = $"""
            {LatestLogsCte},
            {LatestFileOperationsCte},
            dated AS (
                SELECT {PresentationGroupSql} AS GroupId,
                       DATE({AddedAtSql}, 'localtime') AS LocalDate,
                       MAX(COALESCE(lfo.updated_at, ll.updated_at, ll.created_at)) AS UpdatedAt,
                       MAX({NormalizedMediaTypeSql}) AS MediaTypeRaw,
                       COUNT(DISTINCT w.id) AS LeafCount
                FROM latest_logs ll
                JOIN media_assets ma ON ma.id = ll.media_asset_id
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                LEFT JOIN latest_file_operations lfo ON lfo.entity_id = ll.media_asset_id
                WHERE {AddedAtSql} IS NOT NULL
                  AND {PresentationTitleSql}
                GROUP BY {PresentationGroupSql}, DATE({AddedAtSql}, 'localtime')
            )
            """;
        var keys = (await conn.QueryAsync<RecentDayGroupKeyRow>(new CommandDefinition($"""
            WITH {dayScopeCtes},
            ranked AS (
                SELECT GroupId,
                       LocalDate,
                       COUNT(*) OVER (PARTITION BY LocalDate) AS TotalCount,
                       ROW_NUMBER() OVER (PARTITION BY LocalDate ORDER BY UpdatedAt DESC, HEX(GroupId)) AS GroupRank,
                       DENSE_RANK() OVER (ORDER BY LocalDate DESC) AS DayRank
                FROM dated
                WHERE LocalDate IS NOT NULL
            )
            SELECT GroupId, LocalDate, TotalCount, GroupRank
            FROM ranked
            WHERE DayRank <= @dayLimit AND GroupRank <= @itemLimit
            ORDER BY LocalDate DESC, GroupRank;
            """, new { dayLimit, itemLimit }, cancellationToken: ct)).ConfigureAwait(false)).AsList();

        // Per-type counts cover every group of the day, not just the capped preview above, so the Added summary is complete.
        var typeRows = (await conn.QueryAsync<RecentDayTypeCountRow>(new CommandDefinition($"""
            WITH {dayScopeCtes}
            SELECT LocalDate, MediaTypeRaw, SUM(LeafCount) AS ItemCount
            FROM dated
            WHERE LocalDate IS NOT NULL
              AND LocalDate IN (SELECT DISTINCT LocalDate FROM dated WHERE LocalDate IS NOT NULL ORDER BY LocalDate DESC LIMIT @dayLimit)
            GROUP BY LocalDate, MediaTypeRaw;
            """, new { dayLimit }, cancellationToken: ct)).ConfigureAwait(false)).AsList();
        var typeCountsByDay = typeRows
            .GroupBy(row => row.LocalDate, StringComparer.Ordinal)
            .ToDictionary(
                day => day.Key,
                day => day
                    .GroupBy(row => NormalizeMediaType(row.MediaTypeRaw), StringComparer.Ordinal)
                    .Select(type => new IngestionTypeCountDto { MediaType = type.Key, Count = type.Sum(row => row.ItemCount) })
                    .OrderByDescending(type => type.Count)
                    .ThenBy(type => type.MediaType, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        if (keys.Count == 0)
        {
            return [];
        }

        var groups = await LoadGroupsAsync(keys.Select(key => key.GroupId).Distinct().ToArray(), addedOnly: true, ct).ConfigureAwait(false);
        ApplyArtworkSize(groups, "s");
        var groupsById = groups.ToDictionary(group => group.GroupId);

        return keys
            .GroupBy(key => key.LocalDate, StringComparer.Ordinal)
            .Select(day => new IngestionRecentDayDto
            {
                Date = DateOnly.ParseExact(day.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                TotalCount = day.First().TotalCount,
                TypeCounts = typeCountsByDay.GetValueOrDefault(day.Key) ?? [],
                Items = day
                    .OrderBy(key => key.GroupRank)
                    .Select(key => groupsById.GetValueOrDefault(key.GroupId))
                    .Where(item => item is not null)
                    .Cast<IngestionMediaGroupDto>()
                    .ToList(),
            })
            .OrderByDescending(day => day.Date)
            .ToList();
    }

    // The groups in flight, ordered by how actively they are being worked on, then most recently active first. With
    // eligibleOnly the Now panel's rule applies as well: a group is shown once it has a real cover and something is
    // actively happening to it.
    private async Task<List<PresentationGroupKeyRow>> LoadCurrentGroupKeysAsync(
        int offset,
        int limit,
        bool eligibleOnly,
        CancellationToken ct)
    {
        var eligibilityFilter = eligibleOnly ? "AND HasPrimaryCover = 1 AND ActivityRank < 3" : string.Empty;
        using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<PresentationGroupKeyRow>(new CommandDefinition($"""
            WITH {LiveScopeCtes(withRanking: true)}
            SELECT GroupId,
                   COUNT(*) OVER () AS TotalCount
            FROM grouped
            WHERE {InFlightPredicate}
              {eligibilityFilter}
            ORDER BY ActivityRank, UpdatedAt DESC, HEX(GroupId)
            LIMIT @limit OFFSET @offset;
            """, new { offset, limit, periodStart = (string?)null }, cancellationToken: ct)).ConfigureAwait(false)).AsList();
    }

    private async Task<PagedResponse<IngestionMediaGroupDto>> LoadHistoryGroupPageAsync(
        string? search,
        string? lane,
        DateTimeOffset? start,
        DateTimeOffset? end,
        string? sort,
        string? status,
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
            status,
            ct).ConfigureAwait(false);
        var pageKeys = keys.Take(request.Limit).ToList();
        var groups = pageKeys.Count == 0
            ? []
            : await LoadGroupsAsync(pageKeys.Select(key => key.GroupId).Distinct().ToArray(), addedOnly: true, ct).ConfigureAwait(false);
        ApplyArtworkSize(groups, "m");

        var ordered = OrderByKeys(pageKeys, groups);
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

    // The Recently Added history: every group with a file whose Work has been Ready (works.first_ready_at), one row per
    // group however many intakes its files arrived in.
    private async Task<List<PresentationGroupKeyRow>> LoadHistoryGroupKeysAsync(
        string? search,
        string? lane,
        DateTimeOffset? start,
        DateTimeOffset? end,
        int offset,
        int limit,
        string? sort,
        string? status,
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
        var addedOnly = NormalizeHistoryStatus(status) == "added";
        var attentionCte = addedOnly ? AttentionGroupsCte : "";
        var addedFilter = addedOnly
            ? """
              WHERE NOT EXISTS (
                  SELECT 1 FROM attention_groups ag
                  WHERE ag.GroupId = grouped.GroupId)
              """
            : "";
        var startFilter = start.HasValue ? $"AND julianday({AddedAtSql}) >= julianday(@start)" : "";
        var endFilter = end.HasValue ? $"AND julianday({AddedAtSql}) <= julianday(@end)" : "";
        var orderBy = NormalizeHistorySort(sort) switch
        {
            "oldest" => "UpdatedAt ASC, HEX(GroupId)",
            "title" => "SortTitle ASC, UpdatedAt DESC, HEX(GroupId)",
            "media" => "SortMediaType ASC, UpdatedAt DESC, HEX(GroupId)",
            _ => "UpdatedAt DESC, HEX(GroupId)",
        };

        using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<PresentationGroupKeyRow>(new CommandDefinition($"""
            WITH {LatestLogsCte},
            {LatestFileOperationsCte},
            scoped AS (
                SELECT {PresentationGroupSql} AS GroupId,
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
                JOIN media_assets ma ON ma.id = ll.media_asset_id
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                LEFT JOIN latest_file_operations lfo ON lfo.entity_id = ll.media_asset_id
                -- Items appear as soon as their own Work is Ready, so a long first-run import fills Recently Added
                -- while it runs. Items still in flight have no added-at time yet.
                WHERE {AddedAtSql} IS NOT NULL
                  AND {PresentationTitleSql}
                  {searchFilter}
                  {laneFilter}
                  {startFilter}
                  {endFilter}
            ),
            {attentionCte}
            grouped AS (
                SELECT GroupId, MIN(SortTitle) AS SortTitle, MIN(SortMediaType) AS SortMediaType, MAX(UpdatedAt) AS UpdatedAt
                FROM scoped
                GROUP BY GroupId
            )
            SELECT GroupId,
                   COUNT(*) OVER () AS TotalCount
            FROM grouped
            {addedFilter}
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

    // Cards in the order of the key query; a group dropped by the placeholder-title rule leaves no gap.
    private static List<IngestionMediaGroupDto> OrderByKeys(
        IEnumerable<PresentationGroupKeyRow> keys,
        IEnumerable<IngestionMediaGroupDto> groups)
    {
        var groupsById = groups.ToDictionary(group => group.GroupId);
        return keys
            .Select(key => groupsById.GetValueOrDefault(key.GroupId))
            .Where(item => item is not null)
            .Cast<IngestionMediaGroupDto>()
            .ToList();
    }

    private async Task<List<IngestionMediaGroupDto>> LoadGroupsAsync(
        IReadOnlyCollection<Guid> groupIds,
        bool addedOnly,
        CancellationToken ct)
    {
        if (groupIds.Count == 0)
        {
            return [];
        }

        var rows = await LoadRowsAsync(groupIds, addedOnly, ct).ConfigureAwait(false);
        var operations = await LoadOperationsAsync(rows, ct).ConfigureAwait(false);

        return rows
            .GroupBy(PresentationGroupId)
            .Select(group => BuildGroup(group.Key, group.ToList(), operations))
            .Where(group => !IsPlaceholderTitle(group.Title))
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // The member files of the given groups. A group is its structural identity alone (PresentationGroupSql): files that
    // arrived in separate intakes, or were scanned again later, still make one group. With addedOnly, only the files
    // whose Work has been Ready (the "Added" history) are read, so a card for Recently Added describes what was added.
    private async Task<List<MediaPresentationRow>> LoadRowsAsync(
        IReadOnlyCollection<Guid> groupIds,
        bool addedOnly,
        CancellationToken ct)
    {
        var addedFilter = addedOnly ? $"AND {AddedAtSql} IS NOT NULL" : "";
        var groupIdHexes = groupIds.Select(GuidHex).ToArray();
        var selectedGroups = groupIdHexes.Length > 0
            ? $"AND LOWER(HEX({PresentationGroupSql})) IN @groupIdHexes"
            : "";

        using var conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<MediaPresentationRow>(new CommandDefinition($"""
            WITH {LatestLogsCte},
            {LatestFileOperationsCte}
            SELECT
                ll.id AS LogId,
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
                CASE WHEN {WorkReadinessSql.ReadyMarkPredicate("w")} THEN 1 ELSE 0 END AS IsReady,
                CASE WHEN {WorkReadinessSql.ReadyMarkPredicate("w")} OR {WorkReadinessSql.MatchedPredicate("w")} THEN 1 ELSE 0 END AS IsMatched,
                CASE WHEN {WorkReadinessSql.ReadyMarkPredicate("w")} OR {WorkReadinessSql.PlacedPredicate("w")} THEN 1 ELSE 0 END AS IsPlaced,
                CASE WHEN {WorkReadinessSql.ReadyMarkPredicate("w")} OR {WorkReadinessSql.ArtworkPresentOrSettledPredicate("w")} THEN 1 ELSE 0 END AS HasSettledArtwork,
                (SELECT ij.state FROM ({WorkIdentityJobsSql}) ij
                 ORDER BY ij.updated_at DESC, ij.created_at DESC LIMIT 1) AS IdentityState,
                ll.status AS LogStatus,
                lfo.status AS OperationStatus,
                lfo.stage AS OperationStage,
                ma.presented_at AS PresentedAt,
                w.first_ready_at AS FirstReadyAt,
                ll.created_at AS AddedAt,
                COALESCE(lfo.updated_at, ll.updated_at, ll.created_at) AS UpdatedAt,
                (SELECT COUNT(*) FROM review_queue rq WHERE rq.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id) AND rq.status = 'Pending' AND rq.review_ready_at IS NOT NULL) AS ReviewCount,
                (SELECT COUNT(*) FROM person_media_links pml WHERE pml.media_asset_id = ll.media_asset_id) AS PeopleCount,
                (SELECT COUNT(*) FROM text_tracks tt WHERE tt.asset_id = ll.media_asset_id) AS TextTrackCount,
                (SELECT COUNT(*) FROM media_inspection_holds mih WHERE mih.asset_id = ll.media_asset_id) AS InspectionHoldCount,
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
            JOIN media_assets ma ON ma.id = ll.media_asset_id
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            LEFT JOIN latest_file_operations lfo ON lfo.entity_id = ll.media_asset_id
            WHERE 1 = 1
              {addedFilter}
              {selectedGroups}
            ORDER BY COALESCE(lfo.updated_at, ll.updated_at, ll.created_at) DESC;
            """, new
        {
            groupIdHexes,
            bypassIdentityLibraryIds = (_bypassLibraries?.GetLibraryIds() ?? []).ToArray(),
        }, cancellationToken: ct)).ConfigureAwait(false)).AsList();
        return rows;
    }

    private async Task<List<PresentationOperationRow>> LoadOperationsAsync(
        IReadOnlyCollection<MediaPresentationRow> rows,
        CancellationToken ct)
    {
        var entityIds = rows
            .SelectMany(row => new Guid?[] { row.AssetId, row.WorkId, row.ParentWorkId, row.RootWorkId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        if (entityIds.Length == 0)
        {
            return [];
        }

        using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<PresentationOperationRow>(new CommandDefinition("""
            SELECT entity_id AS EntityId, operation_type AS OperationType,
                   capability_id AS CapabilityId, status AS Status, stage AS Stage,
                   COALESCE(updated_at, completed_at, started_at, created_at) AS UpdatedAt
            FROM media_operations
            WHERE entity_id IN @entityIds
            UNION ALL
            SELECT entity_id AS EntityId,
                   CASE WHEN pass = 'Universe' THEN 'identity.relationships' ELSE 'identity.metadata' END AS OperationType,
                   NULL AS CapabilityId,
                   CASE WHEN state IN ('Ready','ReadyWithoutUniverse','RetailNoMatch','RetailMatchedNeedsReview','QidNoMatch','QidNeedsReview') THEN 'completed'
                        WHEN state = 'Failed' THEN 'failed_terminal'
                        WHEN state IN ('Queued','RetailMatched','QidResolved') THEN 'queued'
                        ELSE 'running' END AS Status,
                   state AS Stage, updated_at AS UpdatedAt
            FROM identity_jobs
            WHERE entity_id IN @entityIds;
            """,
            new { entityIds = entityIds.Select(MediaEngine.Storage.GuidSql.ToBlob).ToArray() },
            cancellationToken: ct)).ConfigureAwait(false)).AsList();
    }

    private static IngestionMediaGroupDto BuildGroup(
        Guid groupId,
        IReadOnlyList<MediaPresentationRow> rows,
        IReadOnlyList<PresentationOperationRow> operations)
    {
        var newest = rows.OrderByDescending(row => row.UpdatedAt).First();
        var mediaType = NormalizeMediaType(newest.MediaType);
        var memberIds = rows.SelectMany(row => new Guid?[] { row.AssetId, row.WorkId, row.ParentWorkId, row.RootWorkId })
            .Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
        var scopedOps = operations.Where(operation => operation.EntityId is { } id && memberIds.Contains(id)).ToList();
        var completed = rows
            .Where(row => row.IsReady)
            .Select(row => row.WorkId)
            .Distinct()
            .Count();
        var cover = rows.OrderByDescending(row => row.UpdatedAt).FirstOrDefault(row => row.CoverAssetId.HasValue)?.CoverAssetId;
        var needsReview = rows.Any(row => row.ReviewCount > 0 || row.IdentityState is "RetailNoMatch" or "QidNoMatch" or "QidNeedsReview" or "RetailMatchedNeedsReview");
        // A file's own intake failure always counts. Any other failure counts only when it is no older than the group's
        // newest file: a group spans every intake (there are no runs), so a newer episode supersedes an old failure on
        // another one, while a movie whose last step failed still shows it.
        var newestArrival = rows.Max(row => row.AddedAt);
        var terminalFailure = rows.Any(row => row.OperationStatus is "failed_terminal" or "dead_lettered")
                              || scopedOps.Any(operation => operation.Status is "failed_terminal" or "dead_lettered"
                                  && operation.UpdatedAt >= newestArrival);
        var hasBackgroundWork = scopedOps.Any(operation => IsActive(operation.Status) && !IsFileIntake(operation.OperationType));
        var intakeComplete = rows.All(row => row.IsReady);
        // A missing media tool is a wait, not a failure: it outranks adding/finishing/ready but never hides review or failed.
        var waitingForTools = rows.Any(row => row.InspectionHoldCount > 0);
        var availability = needsReview ? "review"
            : terminalFailure ? "failed"
            : waitingForTools ? "waiting_for_tools"
            : intakeComplete ? hasBackgroundWork ? "finishing" : "ready" : "adding";

        // Only a row that is still being added says what it waits for; review, failed, waiting-for-tools and Ready rows do not.
        var waitingFor = availability == "adding" ? WaitingFor(rows) : [];

        var progressGates = BuildProgressGates(availability, scopedOps, rows, cover.HasValue);
        var completedGates = progressGates.Count(gate => gate.State == "complete");
        var currentGate = progressGates.FirstOrDefault(gate => gate.State != "complete");

        return new IngestionMediaGroupDto
        {
            GroupId = groupId,
            WorkId = groupId,
            EditionId = rows.Count == 1 ? newest.EditionId : null,
            MediaType = mediaType,
            Title = GroupTitle(mediaType, newest),
            Subtitle = GroupSubtitle(mediaType, rows),
            CoverUrl = cover is { } coverId ? $"/stream/artwork/{coverId:D}" : null,
            Availability = availability,
            StatusLabel = GroupStatus(availability, scopedOps),
            WaitingFor = waitingFor,
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
            People = Facet("People", "people", scopedOps, rows.Any(row => row.PeopleCount > 0), applies: true, settled: availability == "ready"),
            Artwork = Facet("Artwork", "artwork", scopedOps, cover.HasValue, applies: true, settled: availability == "ready"),
            Metadata = Facet("Metadata", "metadata", scopedOps, intakeComplete, applies: true, settled: availability == "ready"),
            Relationships = Facet("Relationships", "relationship", scopedOps, false, applies: true, settled: availability == "ready"),
            TextTracks = Facet(mediaType == "Music" ? "Lyrics" : "Subtitles", "text", scopedOps, rows.Any(row => row.TextTrackCount > 0), applies: mediaType is "Music" or "Movies" or "TV", settled: availability == "ready"),
            // "Added" is the moment the group first became Ready (D9), the same time the Added summary and the
            // Recently Added filters use; a group not Ready yet keeps the time its files arrived.
            AddedAt = rows.Any(row => row.FirstReadyAt.HasValue)
                ? rows.Where(row => row.FirstReadyAt.HasValue).Min(row => row.FirstReadyAt!.Value)
                : rows.Min(row => row.PresentedAt ?? row.AddedAt),
            UpdatedAt = scopedOps.Select(operation => operation.UpdatedAt).Append(rows.Max(row => row.UpdatedAt)).Max(),
        };
    }

    // The Ready conditions still missing on any not-Ready Work the row covers, in the fixed order matching, placing,
    // artwork. The conditions come from WorkReadinessSql, the one definition the Ready rule itself uses.
    private static List<string> WaitingFor(IReadOnlyList<MediaPresentationRow> rows)
    {
        var notReady = rows.Where(row => !row.IsReady).ToList();
        var waiting = new List<string>(3);
        if (notReady.Any(row => !row.IsMatched))
        {
            waiting.Add(IngestionWaitingForKeys.Matching);
        }

        if (notReady.Any(row => !row.IsPlaced))
        {
            waiting.Add(IngestionWaitingForKeys.Placing);
        }

        if (notReady.Any(row => !row.HasSettledArtwork))
        {
            waiting.Add(IngestionWaitingForKeys.Artwork);
        }

        return waiting;
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
        bool applies,
        bool settled = false)
    {
        if (!applies)
        {
            return IngestionFacetStateDto.NotApplicable(label);
        }

        var matching = operations.Where(operation => OperationFacet(operation) == facet).ToList();
        if (matching.Count == 0)
        {
            if (hasResult)
            {
                return new IngestionFacetStateDto { State = "complete", Label = $"{label} complete" };
            }

            // A Ready row has nothing more queued for this symbol: it is settled with nothing found, not "queued".
            return settled
                ? new IngestionFacetStateDto { State = "complete", Label = $"No {label.ToLowerInvariant()} found" }
                : new IngestionFacetStateDto { State = "pending", Label = $"{label} {FacetStateLabel("pending")}" };
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
            // The search finished without a cover. That is a settled outcome (artwork settled counts toward Ready), not
            // "does not apply".
            return new IngestionFacetStateDto { State = "complete", Label = "No artwork found" };
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

        if (availability == "waiting_for_tools")
        {
            return "Waiting for media tools";
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

    internal static string? DetailRoute(string mediaType, Guid groupId) => mediaType switch
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

    // Counts the groups of the live set: those still in flight, plus (while the Engine has an active period) those that
    // became Ready since it began, so the numbers say how the current run is going without any run record.
    private async Task<CurrentGroupFacts> ReadCurrentGroupFactsAsync(DateTimeOffset? periodStart, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleAsync<CurrentGroupFacts>(new CommandDefinition($"""
            WITH {LiveScopeCtes(withRanking: false)}
            SELECT COUNT(*) AS TotalGroups,
                   COUNT(CASE WHEN AnyReview = 0 AND AnyFailed = 0 AND AnyHold = 0 AND AllReady = 1 AND AnyBackground = 0 THEN 1 END) AS ReadyGroups,
                   COUNT(CASE WHEN AnyReview = 0 AND AnyFailed = 0 AND AnyHold = 0 AND AllReady = 1 AND AnyBackground = 1 THEN 1 END) AS FinishingGroups,
                   COUNT(CASE WHEN AnyReview = 1 THEN 1 END) AS ReviewGroups
            FROM grouped;
            """, new
        {
            periodStart = periodStart?.ToUniversalTime().ToString("O"),
        }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private async Task<HistoryAggregateFacts> ReadHistoryAggregateFactsAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        DateTimeOffset todayStart,
        DateTimeOffset tomorrowStart,
        CancellationToken ct)
    {
        return await conn.QuerySingleAsync<HistoryAggregateFacts>(new CommandDefinition($"""
            WITH {LatestLogsCte},
            scoped AS (
                SELECT {PresentationGroupSql} AS GroupId
                FROM latest_logs ll
                JOIN media_assets ma ON ma.id = ll.media_asset_id
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE {AddedAtSql} IS NOT NULL
                  AND julianday({AddedAtSql}) >= julianday(@todayStart)
                  AND julianday({AddedAtSql}) < julianday(@tomorrowStart)
                  AND {PresentationTitleSql}
                GROUP BY {PresentationGroupSql}
            )
            SELECT COUNT(*) AS ItemsAddedToday,
                   (SELECT MAX(COALESCE(updated_at, created_at)) FROM ingestion_log) AS LastActivityAt
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

    private static string? NormalizeLaneFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim().ToLowerInvariant();

    // Only "added" narrows the history; absent or unknown values keep every group, like lane and sort.
    private static string? NormalizeHistoryStatus(string? value) =>
        string.Equals(value?.Trim(), "added", StringComparison.OrdinalIgnoreCase) ? "added" : null;

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

    // "Added" means the moment the Work first became Ready (works.first_ready_at), the one Ready rule's own timestamp.
    private const string AddedAtSql = "w.first_ready_at";

    // The newest ingestion log row of each file, from any intake. A file that is scanned again is still one file.
    private const string LatestLogsCte = """
        latest_logs AS (
            SELECT *
            FROM (
                SELECT il.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY il.media_asset_id
                           ORDER BY il.updated_at DESC, il.created_at DESC) AS rn
                FROM ingestion_log il
                WHERE il.media_asset_id IS NOT NULL
            )
            WHERE rn = 1
        )
        """;

    // The newest "ingestion.file" work row of each file.
    private const string LatestFileOperationsCte = """
        latest_file_operations AS (
            SELECT *
            FROM (
                SELECT mo.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY mo.entity_id
                           ORDER BY COALESCE(mo.updated_at, mo.completed_at, mo.started_at, mo.created_at) DESC) AS rn
                FROM media_operations mo
                WHERE mo.operation_type = 'ingestion.file'
            )
            WHERE rn = 1
        )
        """;

    private const string OpenOperationStatusSql =
        "'pending','queued','leased','running','processing','active','retry_waiting','failed_retryable','interrupted'";

    // A group is in flight when its availability (as BuildGroup works it out) is review, waiting for tools, finishing or
    // adding, that is anything but Ready or failed, or when one of its files still has an open "ingestion.file" row.
    private const string InFlightPredicate =
        "(AnyReview = 1 OR AnyFileOp = 1 OR (AnyFailed = 0 AND (AnyHold = 1 OR AllReady = 0 OR AnyBackground = 1)))";

    // The live set: one row per file that could belong to a group in flight, grouped by the group's structural identity
    // alone (no intake or run record). A file is only looked at when its Work is not Ready, it became Ready in the active
    // period (@periodStart, null when none), or something is open on it or its parents (live_signals), so the cost follows
    // the work in progress, not the size of the library. Needs @periodStart. Ends with the "grouped" CTE.
    private static string LiveScopeCtes(bool withRanking)
    {
        var rankingColumns = withRanking
            ? """
                ,
                       MAX(COALESCE(lfo.updated_at, ll.updated_at, ll.created_at),
                           COALESCE((SELECT MAX(COALESCE(activity.updated_at,activity.started_at,activity.created_at))
                               FROM media_operations activity
                               WHERE activity.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)), ''),
                           COALESCE((SELECT MAX(ij.updated_at) FROM identity_jobs ij
                               WHERE ij.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)), '')) AS UpdatedAt,
                       MIN(COALESCE((SELECT MIN(CASE WHEN activity.status IN ('running','processing','active') THEN 0
                                                WHEN activity.status IN ('retry_waiting','failed_retryable','interrupted') THEN 1
                                                WHEN activity.status = 'queued' THEN 2 ELSE 3 END)
                           FROM media_operations activity
                           WHERE activity.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)), 3),
                           COALESCE((SELECT MIN(CASE WHEN ij.state IN ('RetailSearching','BridgeSearching','Hydrating','UniverseEnriching') THEN 0
                               WHEN ij.state IN ('RetailMatched','QidResolved') THEN 1
                               WHEN ij.state = 'Queued' THEN 2 ELSE 3 END)
                               FROM identity_jobs ij
                               WHERE ij.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)), 3)) AS ActivityRank,
                       EXISTS (
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
                """
            : string.Empty;
        var rankingAggregates = withRanking
            ? """
                ,
                       MAX(UpdatedAt) AS UpdatedAt,
                       MIN(ActivityRank) AS ActivityRank,
                       MAX(HasPrimaryCover) AS HasPrimaryCover
                """
            : string.Empty;

        return $"""
            {LatestLogsCte},
            {LatestFileOperationsCte},
            live_signals AS (
                SELECT mo.entity_id AS EntityId
                FROM media_operations mo
                WHERE mo.entity_id IS NOT NULL
                  AND mo.status IN ({OpenOperationStatusSql},'failed_terminal','dead_lettered')
                UNION
                SELECT ij.entity_id FROM identity_jobs ij WHERE ij.state NOT IN ('Ready','ReadyWithoutUniverse')
                UNION
                SELECT rq.entity_id FROM review_queue rq WHERE rq.status = 'Pending' AND rq.review_ready_at IS NOT NULL
                UNION
                SELECT mih.asset_id FROM media_inspection_holds mih
            ),
            scoped AS (
                SELECT {PresentationGroupSql} AS GroupId,
                       CASE WHEN {WorkReadinessSql.ReadyMarkPredicate("w")} THEN 1 ELSE 0 END AS RowReady,
                       CASE WHEN lfo.status IN ('failed_terminal','dead_lettered') THEN 1 ELSE 0 END AS OwnIntakeFailed,
                       MAX(COALESCE((SELECT MAX(julianday(fo.updated_at)) FROM media_operations fo
                                     WHERE fo.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)
                                       AND fo.status IN ('failed_terminal','dead_lettered')), 0),
                           COALESCE((SELECT MAX(julianday(fj.updated_at)) FROM identity_jobs fj
                                     WHERE fj.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)
                                       AND fj.state = 'Failed'), 0)) AS FailedAtDay,
                       julianday(ll.created_at) AS ArrivedAtDay,
                       CASE WHEN EXISTS (SELECT 1 FROM review_queue rq
                                         WHERE rq.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)
                                           AND rq.status = 'Pending' AND rq.review_ready_at IS NOT NULL)
                                 OR (SELECT ij.state FROM ({WorkIdentityJobsSql}) ij
                                     ORDER BY ij.updated_at DESC, ij.created_at DESC LIMIT 1)
                                    IN ('RetailNoMatch','QidNoMatch','QidNeedsReview','RetailMatchedNeedsReview')
                            THEN 1 ELSE 0 END AS NeedsReview,
                       CASE WHEN EXISTS (SELECT 1 FROM media_inspection_holds mih WHERE mih.asset_id = ll.media_asset_id)
                            THEN 1 ELSE 0 END AS RowHold,
                       CASE WHEN EXISTS (SELECT 1 FROM media_operations bo
                                         WHERE bo.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)
                                           AND bo.operation_type <> 'ingestion.file'
                                           AND bo.status IN ({OpenOperationStatusSql}))
                                 OR EXISTS (SELECT 1 FROM identity_jobs bj
                                            WHERE bj.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)
                                              AND bj.state IN ('Queued','RetailSearching','RetailMatched','BridgeSearching','QidResolved','Hydrating','UniverseEnriching'))
                            THEN 1 ELSE 0 END AS RowBackground,
                       CASE WHEN lfo.status IN ({OpenOperationStatusSql}) THEN 1 ELSE 0 END AS FileOpOpen
                       {rankingColumns}
                FROM latest_logs ll
                JOIN media_assets ma ON ma.id = ll.media_asset_id
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                LEFT JOIN latest_file_operations lfo ON lfo.entity_id = ll.media_asset_id
                WHERE (NOT {WorkReadinessSql.ReadyMarkPredicate("w")}
                       OR julianday({AddedAtSql}) >= julianday(@periodStart)
                       OR ll.media_asset_id IN (SELECT EntityId FROM live_signals)
                       OR w.id IN (SELECT EntityId FROM live_signals)
                       OR p.id IN (SELECT EntityId FROM live_signals)
                       OR gp.id IN (SELECT EntityId FROM live_signals))
                  AND {PresentationTitleSql}
            ),
            grouped AS (
                SELECT GroupId,
                       MIN(RowReady) AS AllReady,
                       -- Same rule as BuildGroup: an own intake failure, or a failure no older than the group's newest file.
                       CASE WHEN MAX(OwnIntakeFailed) = 1 OR (MAX(FailedAtDay) > 0 AND MAX(FailedAtDay) >= MAX(ArrivedAtDay))
                            THEN 1 ELSE 0 END AS AnyFailed,
                       MAX(NeedsReview) AS AnyReview,
                       MAX(RowHold) AS AnyHold,
                       MAX(RowBackground) AS AnyBackground,
                       MAX(FileOpOpen) AS AnyFileOp
                       {rankingAggregates}
                FROM scoped
                GROUP BY GroupId
            )
            """;
    }

    // Groups whose BuildGroup availability would be "review" or "failed" (StatusLabel "Needs review" / "Failed"),
    // among the groups with a file whose Work has been Ready. It is evaluated over every such row of the group, as
    // BuildGroup does, so the search, lane and date filters on the key query cannot change a group's status. Keep
    // these conditions in step with BuildGroup (needsReview / terminalFailure) and LoadOperationsAsync.
    private static readonly string AttentionGroupsCte = $"""
        attention_groups AS (
            SELECT {PresentationGroupSql} AS GroupId
            FROM latest_logs ll
            JOIN media_assets ma ON ma.id = ll.media_asset_id
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            LEFT JOIN latest_file_operations lfo ON lfo.entity_id = ll.media_asset_id
            WHERE {AddedAtSql} IS NOT NULL
            GROUP BY {PresentationGroupSql}
            HAVING MAX(CASE WHEN EXISTS (SELECT 1 FROM review_queue rq
                                         WHERE rq.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)
                                           AND rq.status = 'Pending' AND rq.review_ready_at IS NOT NULL)
                                 OR (SELECT ij.state FROM ({WorkIdentityJobsSql}) ij
                                     ORDER BY ij.updated_at DESC, ij.created_at DESC LIMIT 1)
                                    IN ('RetailNoMatch','QidNoMatch','QidNeedsReview','RetailMatchedNeedsReview')
                                 OR lfo.status IN ('failed_terminal','dead_lettered')
                            THEN 1 ELSE 0 END) = 1
                -- A failure other than the file's own intake counts only when no older than the group's newest file.
                OR MAX(COALESCE((SELECT MAX(julianday(mo.updated_at)) FROM media_operations mo
                                 WHERE mo.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)
                                   AND mo.status IN ('failed_terminal','dead_lettered')), 0),
                       COALESCE((SELECT MAX(julianday(fj.updated_at)) FROM identity_jobs fj
                                 WHERE fj.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)
                                   AND fj.state = 'Failed'), 0))
                   >= MAX(julianday(ll.created_at))
                   AND MAX(COALESCE((SELECT MAX(julianday(mo.updated_at)) FROM media_operations mo
                                     WHERE mo.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)
                                       AND mo.status IN ('failed_terminal','dead_lettered')), 0),
                           COALESCE((SELECT MAX(julianday(fj.updated_at)) FROM identity_jobs fj
                                     WHERE fj.entity_id IN (ll.media_asset_id,w.id,p.id,gp.id)
                                       AND fj.state = 'Failed'), 0)) > 0
        ),
        """;

    private sealed class MediaPresentationRow
    {
        public Guid LogId { get; set; }
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
        public bool IsReady { get; set; }
        public bool IsMatched { get; set; }
        public bool IsPlaced { get; set; }
        public bool HasSettledArtwork { get; set; }
        public string? IdentityState { get; set; }
        public string? LogStatus { get; set; }
        public string? OperationStatus { get; set; }
        public string? OperationStage { get; set; }
        public DateTimeOffset? PresentedAt { get; set; }
        public DateTimeOffset? FirstReadyAt { get; set; }
        public DateTimeOffset AddedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public int ReviewCount { get; set; }
        public int PeopleCount { get; set; }
        public int TextTrackCount { get; set; }
        public int InspectionHoldCount { get; set; }
        public Guid? CoverAssetId { get; set; }
    }

    private sealed class PresentationGroupKeyRow
    {
        public Guid GroupId { get; set; }
        public int TotalCount { get; set; }
    }

    private sealed class RecentDayTypeCountRow
    {
        public string LocalDate { get; set; } = "";
        public string? MediaTypeRaw { get; set; }
        public int ItemCount { get; set; }
    }

    private sealed class RecentDayGroupKeyRow
    {
        public Guid GroupId { get; set; }
        public string LocalDate { get; set; } = "";
        public int TotalCount { get; set; }
        public int GroupRank { get; set; }
    }

    private sealed class HistoryAggregateFacts
    {
        public int ItemsAddedToday { get; set; }
        public DateTimeOffset? LastActivityAt { get; set; }
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
        public Guid? EntityId { get; set; }
        public string OperationType { get; set; } = "";
        public string? CapabilityId { get; set; }
        public string Status { get; set; } = "";
        public string? Stage { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class CurrentOperationFacts { public int Active { get; set; } public int Queued { get; set; } public int RetryWaiting { get; set; } public int TextTrackWaiting { get; set; } }
}
