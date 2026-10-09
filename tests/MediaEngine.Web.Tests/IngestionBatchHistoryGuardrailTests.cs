namespace MediaEngine.Web.Tests;

public sealed class IngestionBatchHistoryGuardrailTests
{
    [Fact]
    public void Ingestion_UsesItemPreviewAndRetainsPagedBatchDiagnostics()
    {
        var ingestion = Read(@"src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor");
        var history = Read(@"src/MediaEngine.Web/Components/Settings/IngestionBatchHistory.razor");
        var historyCss = Read(@"src/MediaEngine.Web/Components/Settings/IngestionBatchHistory.razor.css");
        var batchDisplay = Read(@"src/MediaEngine.Web/Components/Settings/IngestionBatchDisplay.cs");
        var pagedMedia = Read(@"src/MediaEngine.Web/Components/Settings/IngestionMediaPagedView.razor");
        var drawer = Read(@"src/MediaEngine.Web/Components/Settings/IngestionMediaDrawer.razor");
        var cardCss = Read(@"src/MediaEngine.Web/Components/Settings/IngestionMediaCard.razor.css");
        var drawerCss = Read(@"src/MediaEngine.Web/Components/Settings/IngestionMediaDrawer.razor.css");
        var settings = Read(@"src/MediaEngine.Web/Components/Pages/Settings.razor");
        var settingsNav = Read(@"src/MediaEngine.Web/Models/ViewDTOs/SettingsNav.cs");

        Assert.Contains("<IngestionRecentlyAddedPreview", ingestion, StringComparison.Ordinal);
        Assert.DoesNotContain("<IngestionBatchHistory", ingestion, StringComparison.Ordinal);
        Assert.Contains("Mode=\"batch\"", ingestion, StringComparison.Ordinal);
        Assert.Contains("runId", ingestion, StringComparison.Ordinal);
        Assert.Contains("GetActivityBatchesAsync", history, StringComparison.Ordinal);
        Assert.Contains("InitialBatchCount = 3", history, StringComparison.Ordinal);
        Assert.Contains("OlderBatchCount = 10", history, StringComparison.Ordinal);
        Assert.Contains("Label=\"Show older\"", history, StringComparison.Ordinal);
        Assert.Contains("Search batches, titles, or content", history, StringComparison.Ordinal);
        Assert.Contains("Needs attention", history, StringComparison.Ordinal);
        Assert.Contains("GroupBy(operation => operation.BatchId)", history, StringComparison.Ordinal);
        Assert.Contains("HistoricalOnly = true", history, StringComparison.Ordinal);
        Assert.Contains("ButtonStyle=\"AppButtonStyle.Filled\"", history, StringComparison.Ordinal);
        Assert.Contains("Label=\"Open batch\"", history, StringComparison.Ordinal);
        Assert.DoesNotContain("Label=\"Review items\"", history, StringComparison.Ordinal);
        Assert.Contains("/settings/ingestion?runId=", batchDisplay, StringComparison.Ordinal);
        Assert.Contains("view=all", batchDisplay, StringComparison.Ordinal);
        Assert.Contains("GetActivityBatchMediaGroupsAsync", pagedMedia, StringComparison.Ordinal);
        Assert.Contains("DisplayMode=\"list\"", pagedMedia, StringComparison.Ordinal);
        Assert.Contains("Type and files", pagedMedia, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppCompactPager", drawer, StringComparison.Ordinal);
        Assert.Contains("aspect-ratio: 1", cardCss, StringComparison.Ordinal);
        Assert.Contains("ingestion-media-list-row", cardCss, StringComparison.Ordinal);
        Assert.Contains("overflow-y:auto", drawerCss, StringComparison.Ordinal);
        Assert.Contains("@media(max-width:900px)", historyCss, StringComparison.Ordinal);
        Assert.DoesNotContain("Activity &amp; Audit", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("ActivityLogs", settingsNav, StringComparison.Ordinal);
        Assert.DoesNotContain("\"activity\"", settingsNav, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
