namespace MediaEngine.Web.Tests;

public sealed class OperationsRecentlyAddedGuardrailTests
{
    [Fact]
    public void Administration_ConsolidatesLiveHistoryAndReviewWithoutParallelEditors()
    {
        var operations = Read(@"src/MediaEngine.Web/Components/Pages/Operations.razor");
        var recent = Read(@"src/MediaEngine.Web/Components/Pages/RecentlyAddedPageContent.razor");
        var ingestion = Read(@"src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor");
        var card = Read(@"src/MediaEngine.Web/Components/Settings/IngestionMediaCard.razor");
        var settings = Read(@"src/MediaEngine.Web/Components/Pages/Settings.razor");

        Assert.Contains("@page \"/operations/ingestion\"", operations, StringComparison.Ordinal);
        Assert.Contains("@page \"/operations/recently-added\"", operations, StringComparison.Ordinal);
        Assert.Contains("@page \"/operations/needs-review\"", operations, StringComparison.Ordinal);
        Assert.Contains("/settings/ingestion", operations, StringComparison.Ordinal);
        Assert.Contains("/settings/recently-added", operations, StringComparison.Ordinal);
        Assert.DoesNotContain("<MediaSectionShell", operations, StringComparison.Ordinal);
        Assert.Contains("GetPendingReviewsAsync(int.MaxValue)", recent, StringComparison.Ordinal);
        Assert.Contains("RefreshReviewCountAsync", recent, StringComparison.Ordinal);
        Assert.Contains("AppCompactPager", recent, StringComparison.Ordinal);
        Assert.Contains("_reviewExpanded = false", recent, StringComparison.Ordinal);
        Assert.Contains("SharedMediaEditorMode.Review", recent, StringComparison.Ordinal);
        Assert.Contains("SharedMediaEditorMode.Normal", recent, StringComparison.Ordinal);
        Assert.Contains("<AppOverflowMenu", recent, StringComparison.Ordinal);
        Assert.Contains("<IngestionRecentlyAddedPreview", ingestion, StringComparison.Ordinal);
        Assert.DoesNotContain("<IngestionBatchHistory", ingestion, StringComparison.Ordinal);
        Assert.Contains("ingestion-media-card__art", card, StringComparison.Ordinal);
        Assert.Contains("ingestion-media-card__progress", card, StringComparison.Ordinal);
        Assert.Contains("ingestion-media-card__title", card, StringComparison.Ordinal);
        Assert.DoesNotContain("ingestion-media-card__type", card, StringComparison.Ordinal);
        Assert.DoesNotContain("<SettingsReviewQueueTab", settings, StringComparison.Ordinal);
        Assert.Contains("<IngestionTasksTab", settings, StringComparison.Ordinal);
        Assert.Contains("<RecentlyAddedPageContent", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("<IngestionSettingsTab", settings, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
