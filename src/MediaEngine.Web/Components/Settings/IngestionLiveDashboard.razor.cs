using System.Globalization;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Formatting;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Components;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Settings;

public partial class IngestionLiveDashboard
{
    [Parameter] public string Subsection { get; set; } = "overview";
    [Parameter] public LibraryUpdateStatusViewModel? Status { get; set; }
    [Parameter] public IngestionOperationsSnapshotDto? Snapshot { get; set; }
    [Parameter] public IngestionDashboardMetrics Metrics { get; set; } = new(0, 0, 0, 0);
    [Parameter] public IngestionOverallProgress OverallProgress { get; set; } = new(0, 0, 0, "Ingestion_StageScanning", "Ingestion_StageScanningDetail", 0, 0, 0, null);
    [Parameter] public IReadOnlyList<IngestionDashboardStage> Stages { get; set; } = [];
    [Parameter] public IReadOnlyList<IngestionOperationsJobDto> Jobs { get; set; } = [];
    [Parameter] public IReadOnlyList<IngestionCurrentActivityDto> CurrentActivities { get; set; } = [];
    [Parameter] public IReadOnlyList<ActivityEntryResponse> Activities { get; set; } = [];
    [Parameter] public IReadOnlyList<ReviewItemViewModel> PendingReviews { get; set; } = [];
    [Parameter] public IReadOnlyList<IngestionReviewReasonDto> ReviewReasons { get; set; } = [];
    [Parameter] public IReadOnlyList<IngestionProviderActivityDto> ProviderActivity { get; set; } = [];
    [Parameter] public IngestionLiveMode LiveMode { get; set; }
    [Parameter] public EngineConnectionState ConnectionState { get; set; }
    [Parameter] public DateTimeOffset? LastUpdated { get; set; }
    [Parameter] public bool Loading { get; set; }
    [Parameter] public bool ScanStarting { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public EventCallback OnRefresh { get; set; }
    [Parameter] public EventCallback OnScanNow { get; set; }

    private bool IsSubsection(string value) =>
        string.Equals(Subsection, value, StringComparison.OrdinalIgnoreCase);

    private string? _lastRenderSignature;
    private readonly IngestionDashboardSelectionState _selection = new();

    private LibraryUpdateStatusViewModel Model => Status
        ?? IngestionLiveDashboardState.BuildLibraryUpdateStatus(
            Snapshot,
            Jobs,
            EffectiveCurrentActivities,
            Activities,
            PendingReviews,
            Metrics,
            Error,
            LastUpdated,
            DateTimeOffset.UtcNow);
    private IReadOnlyList<IngestionDashboardStage> StageRows => Stages
        .Where(stage => !IsReviewStage(stage))
        .OrderBy(stage => stage.StageNumber > 0 ? stage.StageNumber : int.MaxValue)
        .ThenBy(stage => stage.Key)
        .ToList();
    private IReadOnlyList<IngestionDashboardStage> DisplayStageRows =>
        UseSelectedBatchSnapshot && SelectedBatch is { } batch
            ? BuildBatchStages(batch)
            : StageRows;
    private IReadOnlyList<IngestionOperationsBatchDto> OrderedRecentBatches => RecentBatches
        .OrderByDescending(batch => IsActiveBatchStatus(batch.Status))
        .ThenByDescending(batch => batch.StartedAt)
        .Take(5)
        .ToList();
    private IngestionOperationsBatchDto? SelectedBatch => ResolveSelectedBatch();
    private bool UseSelectedBatchSnapshot => SelectedBatch is { StageProgress.Count: > 0 };
    private IngestionDashboardStage? SelectedStage =>
        DisplayStageRows.FirstOrDefault(stage => StageSelectionKey(stage).Equals(_selection.StageKey, StringComparison.OrdinalIgnoreCase))
        ?? DefaultSelectedStage(DisplayStageRows);

    protected override void OnParametersSet()
    {
        _selection.Synchronize(OrderedRecentBatches, DisplayStageRows, StageSelectionKey, DefaultSelectedStage);
    }

    protected override bool ShouldRender()
    {
        var signature = BuildRenderSignature(
            Model,
            Loading,
            ScanStarting,
            Error,
            RecentBatches,
            RecentIdentityJobs,
            DisplayStageRows,
            EffectiveProviderActivity,
            SelectionSignature);
        if (string.Equals(_lastRenderSignature, signature, StringComparison.Ordinal))
        {
            return false;
        }

        _lastRenderSignature = signature;
        return true;
    }

    private string MainStatusClass => $"library-update-status is-{Model.PageState.ToString().ToLowerInvariant()}";
    private IReadOnlyList<IngestionOperationsBatchDto> RecentBatches => Snapshot?.RecentBatches ?? [];
    private IReadOnlyList<IngestionOperationsJobDto> RecentIdentityJobs => Snapshot?.RecentIdentityJobs
        .OrderByDescending(job => job.LastUpdatedTime)
        .Take(8)
        .ToList() ?? [];
    private IReadOnlyList<IngestionCurrentActivityDto> EffectiveCurrentActivities =>
        CurrentActivities.Count > 0 ? CurrentActivities : Snapshot?.CurrentActivities ?? [];
    private IReadOnlyList<IngestionProviderActivityDto> EffectiveProviderActivity =>
        ProviderActivity.Count > 0 ? ProviderActivity : Snapshot?.ProviderActivity ?? [];
    private static string BuildRenderSignature(
        LibraryUpdateStatusViewModel model,
        bool loading,
        bool scanStarting,
        string? error,
        IReadOnlyList<IngestionOperationsBatchDto> recentBatches,
        IReadOnlyList<IngestionOperationsJobDto> recentIdentityJobs,
        IReadOnlyList<IngestionDashboardStage> stages,
        IReadOnlyList<IngestionProviderActivityDto> providerActivity,
        string expandedStageSignature)
    {
        var steps = string.Join(';', model.Steps.Select(step => $"{step.Label}:{step.Status}"));
        var enrichment = string.Join(';', model.EnrichmentStats.Select(stat => string.Join(':',
            stat.Key,
            stat.CompletedCount,
            stat.TotalCount,
            stat.ActiveCount,
            stat.QueuedCount)));
        var recent = string.Join(';', recentBatches.Select(batch => string.Join(':',
            batch.BatchId,
            batch.Status,
            batch.TotalFiles,
            batch.ProcessedFiles,
            batch.MoviesCount,
            batch.TvShowsCount,
            batch.BooksCount,
            batch.AudiobooksCount,
            batch.MusicCount,
            batch.ComicsCount,
            batch.RegisteredCount,
            batch.PeopleGeneratedCount,
            batch.ArtworkDownloadedCount,
            batch.MetadataUpdatedCount,
            batch.ReviewCount,
            batch.FailedCount,
            batch.CompletedAt?.ToUnixTimeSeconds() ?? 0,
            batch.StartedAt.ToUnixTimeSeconds())));
        var recentItems = string.Join(';', recentIdentityJobs.Select(job => string.Join(':',
            job.JobId,
            job.Status,
            job.CurrentStage,
            job.CurrentItem,
            job.MediaType,
            Math.Round(job.PercentComplete, 1),
            job.LastUpdatedTime?.ToUnixTimeSeconds() ?? 0,
            job.WarningSummary)));
        var reasons = string.Join(';', model.AttentionReasons.Select(reason => $"{reason.Label}:{reason.Count}"));
        var stageSignature = string.Join(';', stages.Select(stage => string.Join(':',
            stage.StageNumber,
            stage.Key,
            stage.Count,
            stage.Total,
            Math.Round(stage.Percent, 1),
            stage.StatusLabel,
            stage.ArtifactCount,
            stage.ActiveItemLabel,
            stage.ActiveGroupLabel,
            stage.ActiveCount,
            stage.QueuedCount,
            stage.IsStale,
            string.Join(',', StageDetailLines(stage).Select(detail => $"{detail.Label}={detail.Value}")))));
        var providerSignature = string.Join(';', providerActivity.Select(provider => string.Join(':',
            provider.ProviderName,
            provider.ActiveRequests,
            provider.WaitingRequests,
            provider.RequestsTotal,
            provider.RequestsLastMinute,
            provider.MaxActiveLastMinute,
            provider.ErrorsTotal,
            provider.ErrorsLastMinute,
            provider.WaitMsLastMinute,
            Math.Round(provider.AverageWaitMs, 1),
            Math.Round(provider.AverageLatencyMs, 1),
            provider.LastSuccessAt?.ToUnixTimeSeconds() ?? 0,
            provider.LastRequestAt?.ToUnixTimeSeconds() ?? 0,
            provider.LastError)));

        return string.Join('|',
            loading,
            scanStarting,
            error,
            model.PageState,
            model.TotalFiles,
            model.ProcessedFiles,
            model.MatchedItems,
            model.EnrichmentCompleted,
            model.EnrichmentTotal,
            Math.Round(model.EnrichmentPercent, 1),
            model.ReviewItems,
            model.ExpectedReviewItems,
            model.UnexpectedReviewItems,
            model.ActiveItems,
            model.QueuedItems,
            Math.Round(model.ProgressPercent, 1),
            model.IsIndeterminate,
            model.Heading,
            model.ActivityLine,
            model.SecondaryLine,
            model.StatusLabel,
            model.CurrentItemTitle,
            model.CurrentStep,
            model.CurrentProvider,
            steps,
            enrichment,
            reasons,
            stageSignature,
            providerSignature,
            expandedStageSignature,
            recentItems,
            recent);
    }

    private static string RecentJobTitle(IngestionOperationsJobDto job) =>
        string.IsNullOrWhiteSpace(job.CurrentItem) ? "Matched item" : job.CurrentItem;

    private static string RecentJobStatusLabel(IngestionOperationsJobDto job) =>
        job.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            ? "Complete"
            : job.Status.Equals("attention", StringComparison.OrdinalIgnoreCase)
                ? "Attention"
                : "Running";

    private static string RecentJobStatusClass(IngestionOperationsJobDto job) =>
        $"library-update-batch-chip is-{(job.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) ? "green" : job.Status.Equals("attention", StringComparison.OrdinalIgnoreCase) ? "amber" : "blue")}";

    private static string RecentJobIcon(IngestionOperationsJobDto job) =>
        job.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            ? AppMaterialIcons.Outlined.CheckCircle
            : job.Status.Equals("attention", StringComparison.OrdinalIgnoreCase)
                ? AppMaterialIcons.Outlined.WarningAmber
                : AppMaterialIcons.Outlined.Sync;

    private static string RecentJobTiming(IngestionOperationsJobDto job) =>
        job.LastUpdatedTime.HasValue ? FormatRelative(job.LastUpdatedTime.Value) : "recently";

    private static string RecentJobTooltip(IngestionOperationsJobDto job) =>
        string.IsNullOrWhiteSpace(job.WarningSummary)
            ? $"{RecentJobTitle(job)}: {job.CurrentStage}. Updated {RecentJobTiming(job)}."
            : $"{RecentJobTitle(job)}: {job.WarningSummary}";

    private static string RecentJobMediaLabel(IngestionOperationsJobDto job) =>
        string.IsNullOrWhiteSpace(job.MediaType)
            ? "Media item"
            : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(job.MediaType.Replace('_', ' '));

    private static string RecentJobMediaIconClass(IngestionOperationsJobDto job) =>
        $"library-update-batch-media-icon is-{((job.MediaType ?? string.Empty).Contains("Music", StringComparison.OrdinalIgnoreCase) ? "green" : "purple")}";

    private static string RecentJobMediaIcon(IngestionOperationsJobDto job)
    {
        var mediaType = job.MediaType ?? string.Empty;
        if (mediaType.Contains("Music", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("Album", StringComparison.OrdinalIgnoreCase))
        {
            return AppMaterialIcons.Outlined.Album;
        }

        if (mediaType.Contains("Audiobook", StringComparison.OrdinalIgnoreCase))
        {
            return AppMaterialIcons.Outlined.Headphones;
        }

        if (mediaType.Contains("TV", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("Episode", StringComparison.OrdinalIgnoreCase))
        {
            return AppMaterialIcons.Outlined.LiveTv;
        }

        if (mediaType.Contains("Movie", StringComparison.OrdinalIgnoreCase))
        {
            return AppMaterialIcons.Outlined.Movie;
        }

        if (mediaType.Contains("Comic", StringComparison.OrdinalIgnoreCase))
        {
            return AppMaterialIcons.Outlined.AutoStories;
        }

        return AppMaterialIcons.Outlined.MenuBook;
    }

    private string SelectionSignature => _selection.Signature;

    private RenderFragment MainLineContent() => builder =>
    {
        if (Model.PageState is LibraryUpdatePageState.Running or LibraryUpdatePageState.Complete)
        {
            var remaining = Math.Max(0, Model.TotalFiles - Model.ProcessedFiles);
            var remainderText = ResolveMainRemainderText(Model.PageState, remaining);
            builder.OpenElement(0, "span");
            builder.AddContent(1, DisplayFormat.FormatCount(Model.ProcessedFiles));
            builder.CloseElement();
            builder.AddContent(2, " of ");
            builder.AddContent(3, DisplayFormat.FormatCount(Model.TotalFiles));
            builder.AddContent(4, " files finished");
            builder.AddContent(5, " - ");
            builder.OpenElement(6, "span");
            builder.AddAttribute(7, "class", "library-update-status__remaining");
            builder.AddContent(8, remainderText);
            builder.CloseElement();
            return;
        }

        builder.AddContent(9, Model.MainLine);
    };

    private static string ResolveMainRemainderText(
        LibraryUpdatePageState pageState,
        int remainingFiles)
    {
        if (pageState == LibraryUpdatePageState.Running)
        {
            if (remainingFiles > 0)
            {
                return $"{DisplayFormat.FormatCount(remainingFiles)} still in pipeline";
            }

            return "waiting for latest worker status";
        }

        return remainingFiles > 0
            ? $"{DisplayFormat.FormatCount(remainingFiles)} still in pipeline"
            : "all file work complete";
    }

    private string StageCardClass(IngestionDashboardStage stage)
    {
        var state = StageVisualState(stage);
        var selected = SelectedStage is { } selectedStage
            && StageSelectionKey(selectedStage).Equals(StageSelectionKey(stage), StringComparison.OrdinalIgnoreCase)
            ? " is-selected"
            : string.Empty;
        var wikidata = IsWikidataStage(stage) ? " is-wikidata" : string.Empty;
        return $"library-update-stage-card is-{state}{selected}{wikidata}";
    }

    private static string StageVisualState(IngestionDashboardStage stage)
    {
        if (stage.IsStale)
        {
            return "stale";
        }

        if (IsStageComplete(stage))
        {
            return "complete";
        }

        if (stage.StatusKey == "Ingestion_StatusActive" || stage.ActiveCount > 0)
        {
            return "active";
        }

        if (stage.Percent > 0)
        {
            return "pending";
        }

        return "idle";
    }

    private static bool IsStageComplete(IngestionDashboardStage stage) =>
        stage.Percent >= 99.5
        || stage.StatusKey == "Ingestion_StatusComplete"
        || string.Equals(stage.StatusLabel, "Complete", StringComparison.OrdinalIgnoreCase);

    private static bool IsWikidataStage(IngestionDashboardStage stage) =>
        stage.Key.Equals("wikidata", StringComparison.OrdinalIgnoreCase);

    private static string StageRingOffset(IngestionDashboardStage stage) =>
        (100 - Math.Clamp(stage.Percent, 0, 100)).ToString("0.##", CultureInfo.InvariantCulture);

    private static string StageSelectionKey(IngestionDashboardStage stage) =>
        stage.StageNumber > 0 ? stage.StageNumber.ToString(CultureInfo.InvariantCulture) : stage.Key;

    private static string StageRenderKey(IngestionDashboardStage stage) =>
        $"{stage.StageNumber}:{stage.Key}";

    private static string StageSelectionLabel(IngestionDashboardStage stage) =>
        $"Show {DisplayStageLabel(stage)} details";

    private static string StageIconTooltip(IngestionDashboardStage stage)
    {
        var key = stage.Key.Replace('_', '-').ToLowerInvariant();
        return key switch
        {
            "scan" or "scanned" => "Files found in watched folders and handed to ingestion.",
            "retail" => "Matches files to retail catalog data like title, cover, and description.",
            "wikidata" => "Links matched items to Wikidata IDs so relationships can be built.",
            "people" => "Finds authors, cast, narrators, artists, and crew connected to the files.",
            "universes" or "relationships" => "Builds series, universe, character, and story relationship links.",
            "artwork" or "deep-artwork" => "Downloads covers, posters, backdrops, logos, and other artwork.",
            _ => $"{DisplayStageLabel(stage)} details for this ingestion stage.",
        };
    }

    private static string StageProgressTooltip(IngestionDashboardStage stage)
    {
        var label = DisplayStageLabel(stage);
        if (stage.IsStale)
        {
            return $"{label} has not sent a fresh update recently.";
        }

        if (IsStageComplete(stage))
        {
            return $"{label} is complete for this batch.";
        }

        if (stage.StatusKey == "Ingestion_StatusActive" || stage.ActiveCount > 0)
        {
            return $"{label} is running now.";
        }

        if (stage.QueuedCount > 0)
        {
            return $"{DisplayFormat.FormatCount(stage.QueuedCount)} items are waiting for {label}.";
        }

        if (stage.Percent > 0)
        {
            return $"{label} has started but is not finished.";
        }

        return $"{label} has not started yet.";
    }

    private static string StageCardSummary(IngestionDashboardStage stage)
    {
        var artifact = StageArtifactChip(stage);
        if (!string.IsNullOrWhiteSpace(artifact))
        {
            return artifact;
        }

        return StageCountLine(stage);
    }

    private void SelectStage(IngestionDashboardStage stage)
    {
        _selection.SelectStage(StageSelectionKey(stage));
    }

    private void SelectBatch(IngestionOperationsBatchDto batch)
    {
        _selection.SelectBatch(batch.BatchId);
        OnParametersSet();
    }

    private bool IsSelectedBatch(IngestionOperationsBatchDto batch) =>
        SelectedBatch is { } selected && selected.BatchId == batch.BatchId;

    private IngestionOperationsBatchDto? ResolveSelectedBatch()
        => _selection.ResolveBatch(OrderedRecentBatches);

    private static IngestionDashboardStage? DefaultSelectedStage(IReadOnlyList<IngestionDashboardStage> stages) =>
        stages.FirstOrDefault(stage => stage.StatusKey == "Ingestion_StatusActive")
        ?? stages.FirstOrDefault(stage => !IsStageComplete(stage))
        ?? stages.FirstOrDefault();

    private static string BatchRenderKey(IngestionOperationsBatchDto batch) =>
        batch.BatchId == Guid.Empty
            ? batch.StartedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
            : batch.BatchId.ToString("N");

    private static IReadOnlyList<IngestionDashboardStage> BuildBatchStages(IngestionOperationsBatchDto batch)
    {
        if (batch.StageProgress.Count > 0)
        {
            return IngestionLiveDashboardState.BuildStages(
                new IngestionOperationsSnapshotDto { StageProgress = batch.StageProgress },
                [],
                Math.Max(0, batch.TotalFiles));
        }

        var total = Math.Max(0, Math.Max(batch.TotalFiles, batch.ProcessedFiles));
        var processed = Math.Clamp(Math.Max(0, batch.ProcessedFiles), 0, total);
        var reviewOrFailed = Math.Max(0, batch.ReviewCount + batch.FailedCount);
        var typeTotal = batch.BooksCount
            + batch.MoviesCount
            + batch.TvShowsCount
            + batch.MusicCount
            + batch.ComicsCount
            + batch.AudiobooksCount;
        var unidentified = Math.Max(0, total - typeTotal);
        var complete = !IsActiveBatchStatus(batch.Status);
        var completeCount = complete ? total : processed;

        return
        [
            BatchStage(
                1,
                "scan",
                "Scanned",
                AppMaterialIcons.Outlined.Folder,
                processed,
                total,
                "scanned",
                Math.Max(0, total - reviewOrFailed),
                Detail("Books", batch.BooksCount),
                Detail("Movies", batch.MoviesCount),
                Detail("TV", batch.TvShowsCount),
                Detail("Music", batch.MusicCount),
                Detail("Comics", batch.ComicsCount),
                Detail("Audiobooks", batch.AudiobooksCount),
                Detail("Unidentified", unidentified, unidentified > 0 ? "warning" : "neutral"),
                Detail("Skipped / duplicate", 0),
                Detail("Failed", batch.FailedCount, batch.FailedCount > 0 ? "danger" : "neutral")),
            BatchStage(
                2,
            "retail",
            "Retail Match",
            AppMaterialIcons.Outlined.Search,
                Math.Max(0, batch.RegisteredCount + batch.ReviewCount + batch.FailedCount),
                total,
                "matches",
                batch.RegisteredCount,
                Detail("Matches", batch.RegisteredCount, "success"),
                Detail("Books", batch.BooksCount),
                Detail("Movies", batch.MoviesCount),
                Detail("TV", batch.TvShowsCount),
                Detail("Music", batch.MusicCount),
                Detail("Comics", batch.ComicsCount),
                Detail("Audiobooks", batch.AudiobooksCount),
                Detail("Failed / unresolved", reviewOrFailed, reviewOrFailed > 0 ? "warning" : "neutral"),
                Detail("Metadata fields", batch.MetadataUpdatedCount, "info"),
                Detail("Cover art assets", batch.ArtworkDownloadedCount, "success")),
            BatchStage(
                3,
            "wikidata",
            "Wikidata",
            AppMaterialIcons.Outlined.TravelExplore,
            completeCount,
            total,
            "QIDs",
            batch.RegisteredCount,
                Detail("Files with media/work QID", batch.RegisteredCount, "success"),
                Detail("Related QIDs discovered", batch.MetadataUpdatedCount, "info")),
            BatchStage(
                4,
                "people",
                "People",
                AppMaterialIcons.Outlined.Groups,
                completeCount,
                total,
                "people",
                batch.PeopleGeneratedCount,
                Detail("Cast", 0),
                Detail("Directors", 0),
                Detail("Authors", 0),
                Detail("Narrators", 0),
                Detail("Music artists", 0),
                Detail("Deeply enriched people", batch.PeopleGeneratedCount, "success")),
            BatchStage(
                5,
            "universes",
            "Universes",
            AppMaterialIcons.Outlined.Public,
            completeCount,
            total,
                "links",
                batch.MetadataUpdatedCount,
                Detail("Files with universe links", batch.MetadataUpdatedCount, "success"),
                Detail("Series / shelves", 0),
                Detail("Universe roots", 0),
                Detail("Characters / locations / orgs", 0),
                Detail("Adaptation / story links", 0)),
            BatchStage(
                6,
            "artwork",
            "Artwork",
            AppMaterialIcons.Outlined.Image,
                completeCount,
                total,
                "assets",
                batch.ArtworkDownloadedCount,
                Detail("Covers / posters", batch.ArtworkDownloadedCount, "success"),
                Detail("People headshots", 0),
                Detail("Backdrops", 0),
                Detail("Season / episode art", 0),
                Detail("Album / music art", 0),
                Detail("Logos", 0)),
        ];
    }

    private static IngestionDashboardStage BatchStage(
        int stageNumber,
        string key,
        string label,
        string icon,
        int count,
        int total,
        string artifactLabel,
        int artifactCount,
        params IngestionStageDetailItemDto[] details)
    {
        count = Math.Max(0, count);
        total = Math.Max(0, total);
        if (total > 0)
        {
            count = Math.Clamp(count, 0, total);
        }

        var percent = total > 0 ? count * 100d / total : 0;
        var status = percent >= 99.5
            ? "Ingestion_StatusComplete"
            : count > 0
                ? "Ingestion_StatusPending"
                : "Ingestion_StatusIdle";
        return new IngestionDashboardStage(
            key,
            label,
            label,
            icon,
            count,
            total,
            percent,
            status,
            percent,
            false,
            0,
            0,
            0,
            false,
            stageNumber,
            artifactLabel,
            Math.Max(0, artifactCount),
            DetailItems: details);
    }

    private static IngestionStageDetailItemDto Detail(string label, int value, string tone = "neutral") =>
        new()
        {
            Label = label,
            Value = DisplayFormat.FormatCount(value),
            Tone = tone,
        };

    private static bool IsReviewStage(IngestionDashboardStage stage) =>
        stage.StageNumber == 9 || stage.Key.Equals("review", StringComparison.OrdinalIgnoreCase);

    private static string DisplayStageLabel(IngestionDashboardStage stage) =>
        stage.LabelKey switch
        {
            "Ingestion_StageScanning" => "Scanned",
            "Ingestion_StageRetailIdentification" => "Retail Match",
            "Ingestion_StageWikidataMatch" => "Wikidata",
            "Ingestion_StageEnrichment" => "Enrichment",
            var value => value,
        };

    private static string StageCountLine(IngestionDashboardStage stage)
    {
        if (stage.Total <= 0)
        {
            return stage.Count > 0
                ? $"{DisplayFormat.FormatCount(stage.Count)} files"
                : "Waiting for files";
        }

        return $"{DisplayFormat.FormatCount(stage.Count)} / {DisplayFormat.FormatCount(stage.Total)} files";
    }

    private static string FormatArtifact(IngestionDashboardStage stage)
    {
        var count = Math.Max(0, stage.ArtifactCount ?? 0);
        return $"{DisplayFormat.FormatCount(count)} {stage.ArtifactLabel}";
    }

    private static string? StageArtifactChip(IngestionDashboardStage stage) =>
        string.IsNullOrWhiteSpace(stage.ArtifactLabel)
            ? null
            : FormatArtifact(stage);

    private static IReadOnlyList<StageDetailLine> StageDetailLines(IngestionDashboardStage stage)
    {
        var details = stage.DetailItems ?? [];
        return stage.Key.ToLowerInvariant() switch
        {
            "scan" => [
                Metric("Books", DetailCount(details, "Books"), "books", AppMaterialIcons.Outlined.MenuBook),
                Metric("Movies", DetailCount(details, "Movies"), "movies", AppMaterialIcons.Outlined.Movie),
                Metric("TV", DetailCount(details, "TV"), "tv", AppMaterialIcons.Outlined.LiveTv),
                Metric("Music", DetailCount(details, "Music"), "music", AppMaterialIcons.Outlined.Album),
                Metric("Comics", DetailCount(details, "Comics"), "comics", AppMaterialIcons.Outlined.AutoStories),
                Metric("Audiobooks", DetailCount(details, "Audiobooks"), "audiobooks", AppMaterialIcons.Outlined.Headphones),
                Metric("Unidentified", DetailCount(details, "Unidentified"), "warning", AppMaterialIcons.Outlined.HelpOutline),
                Metric("Skipped / duplicate", DetailCount(details, "Skipped / duplicate"), "neutral", AppMaterialIcons.Outlined.SkipNext),
                Metric("Failed", DetailCount(details, "Failed"), "warning", AppMaterialIcons.Outlined.WarningAmber),
            ],
            "retail" => [
                Metric("Matches", DetailCount(details, "Matches"), "success", AppMaterialIcons.Outlined.Link),
                Metric("Books", DetailCount(details, "Books"), "books", AppMaterialIcons.Outlined.MenuBook),
                Metric("Movies", DetailCount(details, "Movies"), "movies", AppMaterialIcons.Outlined.Movie),
                Metric("TV", DetailCount(details, "TV"), "tv", AppMaterialIcons.Outlined.LiveTv),
                Metric("Music", DetailCount(details, "Music"), "music", AppMaterialIcons.Outlined.Album),
                Metric("Comics", DetailCount(details, "Comics"), "comics", AppMaterialIcons.Outlined.AutoStories),
                Metric("Audiobooks", DetailCount(details, "Audiobooks"), "audiobooks", AppMaterialIcons.Outlined.Headphones),
                Metric("Metadata", DetailCount(details, "Metadata fields"), "info", AppMaterialIcons.Outlined.FactCheck),
                Metric("Cover Art", DetailCount(details, "Cover art assets"), "success", AppMaterialIcons.Outlined.Image),
                Metric("Unresolved", DetailCountAny(details, "Unresolved", "Failed / unresolved"), "warning", AppMaterialIcons.Outlined.HelpOutline),
                Metric("Failed", DetailCount(details, "Failed"), "warning", AppMaterialIcons.Outlined.WarningAmber),
            ],
            "wikidata" => [
                Metric("Media QIDs", DetailCountAny(details, "Media QIDs", "Files with media/work QID"), "success", AppMaterialIcons.Outlined.Link),
                Metric("Relevant QIDs", DetailCount(details, "Relevant QIDs"), "info", AppMaterialIcons.Outlined.AccountTree),
                Metric("Related QIDs", DetailCount(details, "Related QIDs discovered"), "info", AppMaterialIcons.Outlined.AccountTree),
                Metric("Unresolved", DetailCountAny(details, "Unresolved", "Retail retained without QID"), "warning", AppMaterialIcons.Outlined.HelpOutline),
                Metric("Failed", DetailCount(details, "Failed"), "warning", AppMaterialIcons.Outlined.WarningAmber),
            ],
            "people" => [
                Metric("Cast", DetailCount(details, "Cast"), "neutral", AppMaterialIcons.Outlined.Groups),
                Metric("Directors", DetailCount(details, "Directors"), "neutral", AppMaterialIcons.Outlined.Movie),
                Metric("Authors", DetailCount(details, "Authors"), "neutral", AppMaterialIcons.Outlined.MenuBook),
                Metric("Narrators", DetailCount(details, "Narrators"), "neutral", AppMaterialIcons.Outlined.Mic),
                Metric("Music Artists", DetailCount(details, "Music artists"), "neutral", AppMaterialIcons.Outlined.MusicNote),
                Metric("Enriched", DetailCount(details, "Deeply enriched people"), "success", AppMaterialIcons.Outlined.AutoAwesome),
            ],
            "universes" or "relationships" => [
                Metric("Links", DetailCountFirst(details, "Relationship links", "Files with universe links"), "success", AppMaterialIcons.Outlined.Public),
                Metric("Linked Files", DetailCount(details, "Files with universe links"), "success", AppMaterialIcons.Outlined.FolderShared),
                Metric("Series", DetailCount(details, "Series / shelves"), "neutral", AppMaterialIcons.Outlined.Collections),
                Metric("Universe Detail", DetailCount(details, "Universe roots") + DetailCount(details, "Characters / locations / orgs"), "neutral", AppMaterialIcons.Outlined.AccountTree),
                Metric("Adaptations", DetailCount(details, "Adaptation / story links"), "neutral", AppMaterialIcons.Outlined.Link),
            ],
            "artwork" or "deep_artwork" => [
                Metric("Covers", DetailCount(details, "Covers / posters"), "success", AppMaterialIcons.Outlined.Image),
                Metric("Headshots", DetailCount(details, "People headshots"), "neutral", AppMaterialIcons.Outlined.Groups),
                Metric("Backdrops", DetailCount(details, "Backdrops"), "neutral", AppMaterialIcons.Outlined.Collections),
                Metric("Season Art", DetailCount(details, "Season / episode art"), "neutral", AppMaterialIcons.Outlined.LiveTv),
                Metric("Album Art", DetailCount(details, "Album / music art"), "neutral", AppMaterialIcons.Outlined.Album),
                Metric("Logos", DetailCount(details, "Logos"), "neutral", AppMaterialIcons.Outlined.Image),
            ],
            _ => details
                .Where(detail => !string.IsNullOrWhiteSpace(detail.Label) && !string.IsNullOrWhiteSpace(detail.Value))
                .OrderBy(detail => DetailTerminalSort(detail.Label))
                .Select(detail => new StageDetailLine(detail.Label, detail.Value, detail.Tone ?? "neutral", ResolveDetailIcon(detail)))
                .ToList(),
        };
    }

    private static string StageDetailClass(StageDetailLine detail) =>
        $"library-update-stage-detail is-{detail.Tone}";

    private static string StageDetailKey(StageDetailLine detail) =>
        $"{detail.Label}:{detail.Tone}";

    private static string StageDetailTooltip(StageDetailLine detail)
    {
        var label = detail.Label.ToLowerInvariant();
        if (label.Contains("match"))
        {
            return "Files matched to a retail catalog result.";
        }

        if (label.Contains("metadata"))
        {
            return "Metadata fields collected from providers.";
        }

        if (label.Contains("cover art"))
        {
            return "Covers or posters saved for the matched media.";
        }

        if (label.Contains("media qid") || label.Contains("media / work qid") || label.Contains("files with media"))
        {
            return "Files or works that have a direct Wikidata ID.";
        }

        if (label.Contains("related-only qid"))
        {
            return "Extra Wikidata IDs found for people, series, universes, or story details.";
        }

        if (label.Contains("relevant qid"))
        {
            return "Unique Wikidata IDs connected to this batch, including files, people, series, and story details.";
        }

        if (label.Contains("related qid"))
        {
            return "Extra Wikidata IDs found for people, series, universes, or story details.";
        }

        if (label.Contains("unresolved"))
        {
            return "Items Tuvima Library could not confidently finish.";
        }

        if (label.Contains("failed"))
        {
            return "Items where the worker stopped with an error.";
        }

        if (label.Contains("book"))
        {
            return "Book files counted in this stage.";
        }

        if (label.Contains("movie"))
        {
            return "Movie files counted in this stage.";
        }

        if (label.Equals("tv", StringComparison.OrdinalIgnoreCase) || label.Contains("season"))
        {
            return "TV files or episode artwork counted in this stage.";
        }

        if (label.Contains("music") || label.Contains("album"))
        {
            return "Music files or album artwork counted in this stage.";
        }

        if (label.Contains("comic"))
        {
            return "Comic files counted in this stage.";
        }

        if (label.Contains("audiobook"))
        {
            return "Audiobook files counted in this stage.";
        }

        if (label.Contains("unidentified"))
        {
            return "Files whose media type could not be identified.";
        }

        if (label.Contains("skipped") || label.Contains("duplicate"))
        {
            return "Files skipped because they were duplicates or should not be processed.";
        }

        if (label.Contains("cast"))
        {
            return "Actors and voice actors linked to the files.";
        }

        if (label.Contains("director"))
        {
            return "Directors linked to the files.";
        }

        if (label.Contains("author"))
        {
            return "Authors linked to the files.";
        }

        if (label.Contains("narrator"))
        {
            return "Narrators linked to the files.";
        }

        if (label.Contains("artist"))
        {
            return "Music artists linked to the files.";
        }

        if (label.Contains("creator") || label.Contains("crew"))
        {
            return "Other creators and crew linked to the files.";
        }

        if (label.Contains("enriched"))
        {
            return "People with extra profile data such as biography or images.";
        }

        if (label.Equals("links", StringComparison.OrdinalIgnoreCase))
        {
            return "Relationship links created between works, collections, people, or story entities.";
        }

        if (label.Contains("linked files"))
        {
            return "Files connected to a series, universe, or relationship graph.";
        }

        if (label.Contains("series"))
        {
            return "Series or shelf groups found for this batch.";
        }

        if (label.Contains("universe detail"))
        {
            return "Universe roots, characters, locations, or organizations found.";
        }

        if (label.Contains("adaptation"))
        {
            return "Story or adaptation links found between works.";
        }

        if (label.Contains("headshot"))
        {
            return "Person images saved or linked.";
        }

        if (label.Contains("backdrop"))
        {
            return "Backdrop images saved.";
        }

        if (label.Contains("logo"))
        {
            return "Logo artwork saved.";
        }

        return $"{detail.Label} count for this stage.";
    }

    private static StageDetailLine Metric(string label, int value, string tone, string icon) =>
        new(label, DisplayFormat.FormatCount(value), tone, icon);

    private static int DetailCount(IReadOnlyList<IngestionStageDetailItemDto> details, string label)
    {
        var value = details.FirstOrDefault(detail => detail.Label.Equals(label, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        return int.TryParse(value, NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var parsed)
            ? Math.Max(0, parsed)
            : 0;
    }

    private static int DetailCountAny(IReadOnlyList<IngestionStageDetailItemDto> details, params string[] labels) =>
        labels.Sum(label => DetailCount(details, label));

    private static int DetailCountFirst(IReadOnlyList<IngestionStageDetailItemDto> details, params string[] labels)
    {
        foreach (var label in labels)
        {
            var count = DetailCount(details, label);
            if (count > 0)
            {
                return count;
            }
        }

        return 0;
    }

    private static int DetailTerminalSort(string label)
    {
        if (label.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (label.Contains("unresolved", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 0;
    }

    private static string ResolveDetailIcon(IngestionStageDetailItemDto detail) =>
        (detail.Icon ?? detail.Label).ToLowerInvariant() switch
        {
            var value when value.Contains("book") => AppMaterialIcons.Outlined.MenuBook,
            var value when value.Contains("movie") => AppMaterialIcons.Outlined.Movie,
            var value when value.Contains("tv") => AppMaterialIcons.Outlined.LiveTv,
            var value when value.Contains("music") => AppMaterialIcons.Outlined.Album,
            var value when value.Contains("comic") => AppMaterialIcons.Outlined.AutoStories,
            var value when value.Contains("audio") => AppMaterialIcons.Outlined.Headphones,
            var value when value.Contains("art") || value.Contains("cover") => AppMaterialIcons.Outlined.Image,
            var value when value.Contains("people") || value.Contains("cast") => AppMaterialIcons.Outlined.Groups,
            var value when value.Contains("qid") || value.Contains("wikidata") => AppMaterialIcons.Outlined.AccountTree,
            var value when value.Contains("relationship") || value.Contains("universe") => AppMaterialIcons.Outlined.Public,
            var value when value.Contains("warning") || value.Contains("review") || value.Contains("failed") => AppMaterialIcons.Outlined.WarningAmber,
            _ => AppMaterialIcons.Outlined.FactCheck,
        };

    private static string BatchTitle(IngestionOperationsBatchDto batch)
    {
        if (batch.BatchId != Guid.Empty)
        {
            return $"Update {batch.BatchId.ToString("N")[..6]}";
        }

        return "Update";
    }

    private static string BatchHistoryHref(IngestionOperationsBatchDto batch) =>
        batch.BatchId == Guid.Empty
            ? "/settings/ingestion"
            : $"/settings/ingestion?runId={batch.BatchId:D}&view=all";

    private string BatchCardClass(IngestionOperationsBatchDto batch)
    {
        var selected = IsSelectedBatch(batch) ? " is-selected" : string.Empty;
        return $"library-update-batch is-{BatchStatusTone(batch)}{selected}";
    }

    private static string BatchStatusTone(IngestionOperationsBatchDto batch) =>
        IsActiveBatchStatus(batch.Status)
            ? "info"
            : IsFailedBatchStatus(batch.Status)
                ? "danger"
                : IsInterruptedBatchStatus(batch.Status)
                    ? "warning"
                    : batch.ReviewCount > 0
                        ? "warning"
                        : "success";

    private static string BatchStatusLabel(IngestionOperationsBatchDto batch) =>
        IsActiveBatchStatus(batch.Status)
            ? "Active"
            : IsFailedBatchStatus(batch.Status)
                ? "Failed"
                : IsInterruptedBatchStatus(batch.Status)
                    ? "Interrupted"
                    : "Complete";

    private static bool IsActiveBatchStatus(string? status) =>
        status is not null
        && (status.Equals("running", StringComparison.OrdinalIgnoreCase)
            || status.Equals("processing", StringComparison.OrdinalIgnoreCase)
            || status.Equals("active", StringComparison.OrdinalIgnoreCase)
            || status.Equals("queued", StringComparison.OrdinalIgnoreCase));

    private static bool IsFailedBatchStatus(string? status) =>
        status is not null
        && status.Equals("failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsInterruptedBatchStatus(string? status) =>
        status is not null
        && (status.Equals("abandoned", StringComparison.OrdinalIgnoreCase)
            || status.Equals("interrupted", StringComparison.OrdinalIgnoreCase));

    private static string BatchTiming(IngestionOperationsBatchDto batch)
    {
        var started = batch.StartedAt.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture);
        if (IsActiveBatchStatus(batch.Status))
        {
            var elapsed = DateTimeOffset.UtcNow - batch.StartedAt.ToUniversalTime();
            return $"Active - started {started} - {DisplayFormat.FormatDurationCompact(elapsed)} elapsed - {DisplayFormat.FormatCount(batch.ProcessedFiles)} / {DisplayFormat.FormatCount(batch.TotalFiles)} through pipeline";
        }

        if (IsFailedBatchStatus(batch.Status) || IsInterruptedBatchStatus(batch.Status))
        {
            var failedWhen = (batch.CompletedAt ?? batch.StartedAt).ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture);
            var failedDuration = batch.DurationSeconds is > 0
                ? DisplayFormat.FormatDurationCompact(TimeSpan.FromSeconds(batch.DurationSeconds.Value))
                : FormatRelative(batch.CompletedAt ?? batch.StartedAt);
            return $"{BatchStatusLabel(batch)} - {started} to {failedWhen} - {failedDuration}";
        }

        if (batch.DurationSeconds is > 0)
        {
            var completed = (batch.CompletedAt ?? batch.StartedAt).ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture);
            return $"Complete - {started} to {completed} - {DisplayFormat.FormatDurationCompact(TimeSpan.FromSeconds(batch.DurationSeconds.Value))}";
        }

        return $"Complete - started {started}";
    }

    private static IReadOnlyList<BatchChip> BatchMediaChips(IngestionOperationsBatchDto batch)
    {
        var chips = new List<BatchChip>
        {
            new("Books", batch.BooksCount, AppMaterialIcons.Outlined.MenuBook, "purple"),
            new("Movies", batch.MoviesCount, AppMaterialIcons.Outlined.Movie, "blue"),
            new("TV", batch.TvShowsCount, AppMaterialIcons.Outlined.LiveTv, "blue"),
            new("Music", batch.MusicCount, AppMaterialIcons.Outlined.Album, "green"),
            new("Comics", batch.ComicsCount, AppMaterialIcons.Outlined.AutoStories, "amber"),
            new("Audiobooks", batch.AudiobooksCount, AppMaterialIcons.Outlined.Headphones, "pink"),
        };

        return chips.Where(chip => chip.Count > 0).ToList();
    }

    private static IReadOnlyList<BatchChip> BatchArtifactChips(IngestionOperationsBatchDto batch) =>
    [
        new("matched", batch.RegisteredCount, AppMaterialIcons.Outlined.Link, "green"),
        new("review", batch.ReviewCount, AppMaterialIcons.Outlined.WarningAmber, "amber"),
    ];

    private string BatchSelectionTooltip(IngestionOperationsBatchDto batch) =>
        IsSelectedBatch(batch)
            ? "This batch is selected. Its stage details are shown above."
            : "Select this batch to show its stage details above.";

    private static string BatchMediaTooltip(BatchChip chip) =>
        $"{chip.Label}: {DisplayFormat.FormatCount(chip.Count)} files in this batch.";

    private static string BatchArtifactTooltip(BatchChip chip) =>
        chip.Label.ToLowerInvariant() switch
        {
            "matched" => $"{DisplayFormat.FormatCount(chip.Count)} files were confidently matched and added.",
            "review" => $"{DisplayFormat.FormatCount(chip.Count)} files need review before they are fully added.",
            _ => $"{chip.Label}: {DisplayFormat.FormatCount(chip.Count)} items in this batch.",
        };

    private static string BatchActivityTooltip(IngestionOperationsBatchDto batch) =>
        $"Open the full activity log for {BatchTitle(batch)}.";

    private static string BatchChipClass(BatchChip chip) =>
        $"library-update-batch-chip is-{chip.Tone}";

    private static string BatchMediaIconClass(BatchChip chip) =>
        $"library-update-batch-media-icon is-{chip.Tone}";

    private static string FormatExpectedReview(LibraryUpdateStatusViewModel model) =>
        model.UnexpectedReviewItems > 0
            ? $"{DisplayFormat.FormatCount(model.UnexpectedReviewItems)} unexpected"
            : $"{DisplayFormat.FormatCount(model.ExpectedReviewItems)} expected";

    private static string FormatPercent(double percent) =>
        $"{Math.Clamp(percent, 0, 100).ToString("0", CultureInfo.CurrentCulture)}%";

    private static string BatchStatusLabel(string? status) =>
        string.IsNullOrWhiteSpace(status)
            ? "Unknown"
            : IsInterruptedBatchStatus(status)
                ? "Interrupted"
                : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(status.Replace('_', ' '));

    private static string BatchStatusTone(string? status)
    {
        var value = status ?? string.Empty;
        if (value.Contains("complete", StringComparison.OrdinalIgnoreCase))
        {
            return "success";
        }

        if (value.Contains("run", StringComparison.OrdinalIgnoreCase) || value.Contains("active", StringComparison.OrdinalIgnoreCase))
        {
            return "info";
        }

        if (value.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            return "danger";
        }

        if (value.Contains("review", StringComparison.OrdinalIgnoreCase)
            || value.Contains("partial", StringComparison.OrdinalIgnoreCase)
            || value.Contains("abandon", StringComparison.OrdinalIgnoreCase)
            || value.Contains("interrupt", StringComparison.OrdinalIgnoreCase))
        {
            return "warning";
        }

        return "neutral";
    }

    private static string FormatRelative(DateTimeOffset value)
    {
        var elapsed = DateTimeOffset.UtcNow - value.ToUniversalTime();
        if (elapsed.TotalSeconds < 60)
        {
            return "just now";
        }

        if (elapsed.TotalMinutes < 60)
        {
            return $"{(int)elapsed.TotalMinutes}m ago";
        }

        if (elapsed.TotalHours < 24)
        {
            return $"{(int)elapsed.TotalHours}h ago";
        }

        return value.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture);
    }


    private static string ProviderLabel(string providerName) =>
        string.IsNullOrWhiteSpace(providerName)
            ? "Provider"
            : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(providerName.Replace("_", " ").Replace("-", " "));

    private static string ProviderActivitySummary(IngestionProviderActivityDto provider)
    {
        var recentWait = FormatMilliseconds(provider.WaitMsLastMinute);
        var averageWait = provider.AverageWaitMs > 0 ? $"{provider.AverageWaitMs:N0}ms avg wait" : "no avg wait";
        var latency = provider.AverageLatencyMs > 0 ? $"{provider.AverageLatencyMs:N0}ms average" : "latency pending";
        var last = provider.LastSuccessAt is not null
            ? $"last success {FormatRelative(provider.LastSuccessAt.Value)}"
            : provider.LastRequestAt is null
                ? "no requests yet"
                : $"last request {FormatRelative(provider.LastRequestAt.Value)}";
        return $"{DisplayFormat.FormatCount(provider.RequestsLastMinute)} requests/min, {DisplayFormat.FormatCount(provider.RequestsTotal)} total, {recentWait} recent wait, {averageWait}, {latency}, {last}";
    }

    private static string ProviderActivityTooltip(IngestionProviderActivityDto provider) =>
        string.IsNullOrWhiteSpace(provider.LastError)
            ? ProviderActivitySummary(provider)
            : $"{ProviderActivitySummary(provider)}. Last error: {provider.LastError}";

    private static string ProviderActivityIcon(IngestionProviderActivityDto provider) =>
        ProviderHasVisibleError(provider)
            ? AppMaterialIcons.Outlined.WarningAmber
            : provider.WaitingRequests > 0
                ? AppMaterialIcons.Outlined.HourglassTop
                : provider.ActiveRequests > 0
                    ? AppMaterialIcons.Outlined.Sync
                    : provider.RequestsLastMinute > 0 || provider.MaxActiveLastMinute > 0
                        ? AppMaterialIcons.Outlined.Bolt
                        : AppMaterialIcons.Outlined.CheckCircle;

    private static string ProviderStatusIcon(IngestionProviderActivityDto provider) =>
        provider.WaitingRequests > 0
            ? AppMaterialIcons.Outlined.HourglassTop
            : provider.ActiveRequests > 0
                ? AppMaterialIcons.Outlined.Sync
                : provider.RequestsLastMinute > 0 || provider.MaxActiveLastMinute > 0
                    ? AppMaterialIcons.Outlined.Bolt
                    : AppMaterialIcons.Outlined.CheckCircle;

    private static string ProviderStatusValue(IngestionProviderActivityDto provider) =>
        provider.WaitingRequests > 0
            ? DisplayFormat.FormatCount(provider.WaitingRequests)
            : provider.ActiveRequests > 0
                ? DisplayFormat.FormatCount(provider.ActiveRequests)
                : provider.RequestsLastMinute > 0
                    ? DisplayFormat.FormatCount(provider.RequestsLastMinute)
                    : provider.MaxActiveLastMinute > 0
                        ? DisplayFormat.FormatCount(provider.MaxActiveLastMinute)
                        : "Healthy";

    private static string ProviderStatusLabel(IngestionProviderActivityDto provider) =>
        provider.WaitingRequests > 0
            ? "waiting"
            : provider.ActiveRequests > 0
                ? "in flight"
                : provider.RequestsLastMinute > 0
                    ? "req/min"
                    : provider.MaxActiveLastMinute > 0
                        ? "recent peak"
                        : "idle";

    private static string ProviderStatusChipClass(IngestionProviderActivityDto provider) =>
        $"library-update-batch-count is-{ProviderActivityTone(provider)}";

    private static string ProviderErrorChipClass(IngestionProviderActivityDto provider) =>
        $"library-update-batch-count is-{(provider.ErrorsLastMinute > 0 ? "warning" : "success")}";

    private static string ProviderWaitChipClass(IngestionProviderActivityDto provider) =>
        $"library-update-batch-count is-{(provider.WaitingRequests > 0 || provider.WaitMsLastMinute > 0 ? "warning" : "neutral")}";

    private static string ProviderWaitValue(IngestionProviderActivityDto provider) =>
        provider.WaitMsLastMinute > 0
            ? FormatMillisecondsCompact(provider.WaitMsLastMinute)
            : provider.AverageWaitMs > 0
                ? $"{provider.AverageWaitMs:N0}ms"
                : "0ms";

    private static string ProviderWaitLabel(IngestionProviderActivityDto provider) =>
        provider.WaitMsLastMinute > 0 ? "recent wait" : "avg wait";

    private static string ProviderActivityTone(IngestionProviderActivityDto provider)
    {
        if (ProviderHasVisibleError(provider))
        {
            return "warning";
        }

        if (provider.WaitingRequests > 0)
        {
            return "warning";
        }

        if (provider.ActiveRequests > 0 || provider.RequestsLastMinute > 0 || provider.MaxActiveLastMinute > 0)
        {
            return "info";
        }

        return "success";
    }

    private static bool ProviderHasVisibleError(IngestionProviderActivityDto provider) =>
        ProviderHasRecentErrors(provider) || ProviderHasUnrecoveredError(provider);

    private static bool ProviderHasRecentErrors(IngestionProviderActivityDto provider) =>
        provider.ErrorsLastMinute > 0;

    private static bool ProviderHasUnrecoveredError(IngestionProviderActivityDto provider) =>
        !string.IsNullOrWhiteSpace(provider.LastError) && provider.LastSuccessAt is null;

    private static string ProviderErrorValue(IngestionProviderActivityDto provider) =>
        ProviderHasRecentErrors(provider) ? DisplayFormat.FormatCount(provider.ErrorsLastMinute) : "Last";

    private static string ProviderErrorLabel(IngestionProviderActivityDto provider) =>
        ProviderHasRecentErrors(provider) ? "errors/min" : "error";

    private static string FormatMilliseconds(long value)
    {
        var safeValue = Math.Max(0, value);
        return safeValue >= 1000
            ? $"{TimeSpan.FromMilliseconds(safeValue).TotalSeconds:N1}s"
            : $"{safeValue:N0}ms";
    }

    private static string FormatMillisecondsCompact(long value)
    {
        var safeValue = Math.Max(0, value);
        return safeValue >= 1000
            ? $"{TimeSpan.FromMilliseconds(safeValue).TotalSeconds:N1}s"
            : $"{safeValue:N0}ms";
    }

    private sealed record StageDetailLine(string Label, string Value, string Tone, string Icon);

    private sealed record BatchChip(string Label, int Count, string Icon, string Tone);
}
