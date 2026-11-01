using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

public sealed class IngestionOperationsPageGuardrailTests
{
    [Fact]
    public void LibrariesTab_UsesCentralLiveDashboardStateForTheStatusLineAndTheActivityList()
    {
        var statusLine = LocalizedSource.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Settings/Libraries/LibraryStatusLine.razor"));
        var activity = LocalizedSource.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Pages/RecentlyAddedPageContent.razor"));
        var librariesTab = LocalizedSource.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Components/Settings/LibrariesTab.razor"));
        var stateSource = ReadIngestionDashboardStateSource();
        var orchestratorSource = LocalizedSource.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Services/Integration/UIOrchestratorService.cs"));

        Assert.Contains("IngestionLiveDashboardState", statusLine, StringComparison.Ordinal);
        Assert.Contains("IngestionQueueProgressText.LibraryStatus", statusLine, StringComparison.Ordinal);
        Assert.Contains("Dashboard.QueueProgress", statusLine, StringComparison.Ordinal);
        Assert.Contains("Dashboard.ScanNowAsync", statusLine, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", statusLine, StringComparison.Ordinal);
        Assert.Contains("aria-busy", statusLine, StringComparison.Ordinal);
        Assert.Contains("<LibraryStatusLine", librariesTab, StringComparison.Ordinal);

        Assert.Contains("IngestionLiveDashboardState", activity, StringComparison.Ordinal);
        Assert.Contains("<IngestionMediaDrawer", activity, StringComparison.Ordinal);
        Assert.Contains("RefreshPinnedDrawerAsync", activity, StringComparison.Ordinal);
        Assert.Contains("_selectedItem.GroupId == groupId", activity, StringComparison.Ordinal);
        Assert.DoesNotContain("IngestionMediaPagedView", activity, StringComparison.Ordinal);
        Assert.DoesNotContain("<IngestionBatchHistory", activity, StringComparison.Ordinal);
        Assert.DoesNotContain("<IngestionActivityList", activity, StringComparison.Ordinal);

        Assert.Contains("BuildQueueSignature", stateSource, StringComparison.Ordinal);
        Assert.Contains("MergePresentation", stateSource, StringComparison.Ordinal);
        Assert.Contains("Presentation", stateSource, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(2)", stateSource, StringComparison.Ordinal);
        Assert.Contains("SignalREvents.IngestionItemProgress", orchestratorSource, StringComparison.Ordinal);
        Assert.Contains("PushIngestionItemProgress", orchestratorSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRetiredIngestionOverviewPieces_StayDeleted()
    {
        foreach (var relative in new[]
                 {
                     "IngestionTasksTab.razor", "IngestionNowPanel.razor", "IngestionAddedSummary.razor", "IngestionScanAction.razor",
                     "LibrarySummary.razor", "MetadataPipelineStrip.razor", "EnrichmentRefreshSchedulePanel.razor", "RecentlyAddedDateRow.razor",
                 })
        {
            Assert.False(File.Exists(GetRepoFilePath(@"src/MediaEngine.Web/Components/Settings/" + relative)), $"{relative} was retired with the Library & Ingestion overview.");
        }
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

    private static string ReadIngestionDashboardStateSource() =>
        LocalizedSource.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Web/Services/Integration/IngestionLiveDashboardState.cs"));

    private static string GetRepoFilePath(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return Path.GetFullPath(Path.Combine(repoRoot, relativePath));
    }
}
