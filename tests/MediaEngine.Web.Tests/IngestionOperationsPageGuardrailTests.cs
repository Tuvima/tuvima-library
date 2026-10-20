using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

public sealed class IngestionOperationsPageGuardrailTests
{
    [Fact]
    public void IngestionTab_UsesCentralLiveDashboardStateAndComponents()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor"));
        var scanAction = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Settings/IngestionScanAction.razor"));
        var styles = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor.css"));
        var stateSource = ReadIngestionDashboardStateSource();
        var orchestratorSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Services/Integration/UIOrchestratorService.cs"));

        Assert.Contains("IngestionLiveDashboardState", source, StringComparison.Ordinal);
        Assert.Contains("<IngestionNowPanel", source, StringComparison.Ordinal);
        Assert.Contains("<IngestionMediaCard", File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Settings/IngestionNowPanel.razor")), StringComparison.Ordinal);
        Assert.Contains("<IngestionAddedSummary", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<IngestionBatchHistory", source, StringComparison.Ordinal);
        Assert.Contains("<IngestionMediaDrawer", source, StringComparison.Ordinal);
        Assert.Contains("RefreshPinnedDrawerAsync", source, StringComparison.Ordinal);
        Assert.Contains("_selectedItem.BatchId == batchId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IngestionMediaPagedView", source, StringComparison.Ordinal);
        Assert.Contains("IngestionQueueProgressText.Headline", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Adding {RunTotal:N0}", source, StringComparison.Ordinal);
        Assert.Contains("<IngestionScanAction", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ingestion-current__facts", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Being added now", source, StringComparison.Ordinal);
        Assert.Contains("IngestionQueueProgressText.IsWorking(Queue) ? \"is-active\"", source, StringComparison.Ordinal);
        Assert.Contains("aria-busy", source, StringComparison.Ordinal);
        Assert.Contains("ingestion-summary-breathe", styles, StringComparison.Ordinal);
        Assert.Contains("ingestion-progress-shimmer", styles, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion: reduce", styles, StringComparison.Ordinal);
        Assert.Contains("<h2 id=\"ingestion-now-title\">Now</h2>", File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Settings/IngestionNowPanel.razor")), StringComparison.Ordinal);
        Assert.DoesNotContain("Processing details", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BatchProgress", source, StringComparison.Ordinal);
        Assert.Contains("Dashboard.QueueProgress", source, StringComparison.Ordinal);
        Assert.DoesNotContain("file checks complete", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Overall batch", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Finishing current work", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ingestion-stage-detail", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PhaseStatusIcon", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Follow current stage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OverallProgressPercent", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Overall run progress", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(scanAction, "Check folders for changes"));
        Assert.Contains("Folders are watched automatically. Use this after changing files while Library was off, or for network drives.", scanAction, StringComparison.Ordinal);
        Assert.DoesNotContain("Scan now", scanAction, StringComparison.Ordinal);
        Assert.Contains("<AppOverflowMenu", scanAction, StringComparison.Ordinal);
        Assert.DoesNotContain("Label=\"Refresh\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<EnrichmentRefreshSchedulePanel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("settings/ingestion?view=history", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<IngestionActivityList", source, StringComparison.Ordinal);
        Assert.Contains("BuildQueueSignature", stateSource, StringComparison.Ordinal);
        Assert.Contains("MergePresentation", stateSource, StringComparison.Ordinal);
        Assert.Contains("Presentation", stateSource, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(2)", stateSource, StringComparison.Ordinal);
        Assert.Contains("SignalREvents.IngestionItemProgress", orchestratorSource, StringComparison.Ordinal);
        Assert.Contains("PushIngestionItemProgress", orchestratorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("<IngestionDiagnosticsPanels", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetIngestionOperationsSnapshotAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StateContainer.BatchProgress", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly CurrentRun _currentRun", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UniverseStateContainer_ItemProgressDoesNotRequestSnapshotRefresh()
    {
        var state = new UniverseStateContainer();
        var batchId = Guid.NewGuid();

        state.PushIngestionItemProgress(new IngestionItemProgressEvent(
            batchId,
            Guid.NewGuid(),
            MediaAssetId: null,
            FilePath: @"C:\drop\Neuromancer.epub",
            FileName: "Neuromancer.epub",
            Stage: "hashing",
            StageOrder: 1,
            ProgressPercent: 10,
            IsTerminal: false));

        Assert.False(state.LastStateChangeRequiresSnapshotRefresh);

        state.PushBatchProgress(new BatchProgressEvent(
            batchId,
            FilesTotal: 1,
            FilesProcessed: 1,
            FilesIdentified: 1,
            FilesReview: 0,
            FilesNoMatch: 0,
            FilesFailed: 0,
            ProgressPercent: 100,
            EstimatedSecondsRemaining: null,
            IsComplete: true));

        Assert.True(state.LastStateChangeRequiresSnapshotRefresh);
        Assert.Null(state.BatchProgress);
        Assert.Null(state.BatchProgressReceivedAt);
    }

    [Fact]
    public void IngestionRealtimeProgress_ReflectsQuickHydrationTransitions()
    {
        var workerSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Providers/Workers/QuickHydrationWorker.cs"));
        var progressSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Providers/Services/BatchProgressService.cs"));
        var progressRepositorySource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Storage/IngestionBatchRepository.cs"));
        var normalizedProgressSource = progressSource.Replace("\r\n", "\n", StringComparison.Ordinal);
        var operationsSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/IngestionOperationsStatusService.cs"));

        Assert.Contains("EmitBatchProgressAsync(job.IngestionRunId", workerSource, StringComparison.Ordinal);
        Assert.Contains("'UniverseEnriching'", progressRepositorySource, StringComparison.Ordinal);
        Assert.Contains("\"Hydrating\" => \"Retail Match\"", progressSource, StringComparison.Ordinal);
        Assert.Contains("nameof(IdentityJobState.Hydrating)", operationsSource, StringComparison.Ordinal);
        Assert.Contains("ActiveBatchFreshness", operationsSource, StringComparison.Ordinal);
        Assert.Contains("js.lease_expires_at > @now", operationsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("+ snapshot.RetailMatched\n                + snapshot.RetailMatchedNeedsReview", normalizedProgressSource, StringComparison.Ordinal);
        Assert.DoesNotContain("+ snapshot.QidResolved\n                + snapshot.Hydrating", normalizedProgressSource, StringComparison.Ordinal);
        Assert.Contains("var terminal = identified + review + noMatch + failed + snapshot.FilesSkipped;", normalizedProgressSource, StringComparison.Ordinal);
        // File intake and identity completion are different units for multipart books.
        // Their behavior is covered by IngestionPresentationReadServiceTests.
        Assert.Contains("WorkUnitsTotal: phaseTotal", normalizedProgressSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AverageProgressPercent", normalizedProgressSource, StringComparison.Ordinal);
    }

    [Fact]
    public void IngestionSummary_UsesUnmatchedAriaAttributesWithoutConflictingWithAppPanelCapture()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor"));

        Assert.Contains("aria-live=\"polite\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AdditionalAttributes=", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IngestionOperationsStatusService_UsesDomainSpecificWorkerPreviewSources()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/IngestionOperationsStatusService.cs"));

        Assert.Contains("ReadArtworkWorkerRowsAsync", source, StringComparison.Ordinal);
        Assert.Contains("ReadSeriesWorkerRowsAsync", source, StringComparison.Ordinal);
        Assert.Contains("ReadPeopleWorkerRowsAsync", source, StringComparison.Ordinal);
        Assert.Contains("ArtworkAssetCount", source, StringComparison.Ordinal);
        Assert.Contains("entity_assets", source, StringComparison.Ordinal);
        Assert.Contains("person_media_links", source, StringComparison.Ordinal);
        Assert.Contains("series_manifest_hydrations", source, StringComparison.Ordinal);
        Assert.DoesNotContain("c.name", source, StringComparison.Ordinal);
        Assert.Contains("displayRows", source, StringComparison.Ordinal);
    }

    private static string ReadIngestionDashboardStateSource() =>
        File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Services/Integration/IngestionLiveDashboardState.cs"));

    private static string GetRepoFilePath(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return Path.GetFullPath(Path.Combine(repoRoot, relativePath));
    }
}
