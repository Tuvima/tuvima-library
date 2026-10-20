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
        Assert.Contains("_selectedItem.GroupId == groupId", source, StringComparison.Ordinal);
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

        state.PushIngestionItemProgress(new IngestionItemProgressEvent(
            Guid.NewGuid(),
            MediaAssetId: null,
            FilePath: @"C:\drop\Neuromancer.epub",
            FileName: "Neuromancer.epub",
            Stage: "hashing",
            StageOrder: 1,
            ProgressPercent: 10,
            IsTerminal: false));

        Assert.False(state.LastStateChangeRequiresSnapshotRefresh);
    }

    [Fact]
    public void IngestionSummary_UsesUnmatchedAriaAttributesWithoutConflictingWithAppPanelCapture()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor"));

        Assert.Contains("aria-live=\"polite\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AdditionalAttributes=", source, StringComparison.Ordinal);
    }

    private static string ReadIngestionDashboardStateSource() =>
        File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Services/Integration/IngestionLiveDashboardState.cs"));

    private static string GetRepoFilePath(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return Path.GetFullPath(Path.Combine(repoRoot, relativePath));
    }
}
