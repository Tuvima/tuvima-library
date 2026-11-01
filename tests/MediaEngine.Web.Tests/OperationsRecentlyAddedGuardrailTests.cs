namespace MediaEngine.Web.Tests;

public sealed class OperationsRecentlyAddedGuardrailTests
{
    [Fact]
    public void Administration_ConsolidatesLiveHistoryAndReviewWithoutParallelEditors()
    {
        var operations = Read(@"src/MediaEngine.Web/Components/Pages/Operations.razor");
        var recent = Read(@"src/MediaEngine.Web/Components/Pages/RecentlyAddedPageContent.razor");
        var review = Read(@"src/MediaEngine.Web/Components/Settings/NeedsReviewPageContent.razor");
        var statusLine = Read(@"src/MediaEngine.Web/Components/Settings/Libraries/LibraryStatusLine.razor");
        var card = Read(@"src/MediaEngine.Web/Components/Settings/IngestionMediaCard.razor");
        var settings = Read(@"src/MediaEngine.Web/Components/Pages/Settings.razor");

        Assert.Contains("@page \"/operations/ingestion\"", operations, StringComparison.Ordinal);
        Assert.Contains("@page \"/operations/recently-added\"", operations, StringComparison.Ordinal);
        Assert.Contains("@page \"/operations/needs-review\"", operations, StringComparison.Ordinal);
        Assert.Contains("/settings/libraries", operations, StringComparison.Ordinal);
        Assert.Contains("/settings/library-activity", operations, StringComparison.Ordinal);
        Assert.Contains("/settings/review", operations, StringComparison.Ordinal);
        Assert.DoesNotContain("<MediaSectionShell", operations, StringComparison.Ordinal);
        Assert.Contains("GetPendingReviewsAttemptAsync(int.MaxValue)", review, StringComparison.Ordinal);
        Assert.Contains("RefreshReviewCountAsync", review, StringComparison.Ordinal);
        Assert.Contains("EditingSection.Match", review, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaEditorLauncher", review, StringComparison.Ordinal);
        Assert.Contains("<AppOverflowMenu", review, StringComparison.Ordinal);
        Assert.Contains("AppCompactPager", recent, StringComparison.Ordinal);
        Assert.Contains("OfferEditResume", recent, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaEditorLauncher", recent, StringComparison.Ordinal);
        Assert.Contains("<AppOverflowMenu", recent, StringComparison.Ordinal);
        Assert.DoesNotContain("review-pinned", recent, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPendingReviews", recent, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedMediaEditorMode.Review", recent, StringComparison.Ordinal);
        Assert.Contains("data-activity-state", recent, StringComparison.Ordinal);
        Assert.Contains("IngestionNowWindow", recent, StringComparison.Ordinal);
        Assert.DoesNotContain("<IngestionBatchHistory", recent, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"library-status-line\"", statusLine, StringComparison.Ordinal);
        Assert.Contains("ingestion-media-list-row__identity", card, StringComparison.Ordinal);
        Assert.Contains("ingestion-media-list-row__step", card, StringComparison.Ordinal);
        Assert.Contains("ingestion-media-list-row__details", card, StringComparison.Ordinal);
        Assert.Contains("<RecentlyAddedPageContent", settings, StringComparison.Ordinal);
        Assert.Contains("<NeedsReviewPageContent", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("<IngestionSettingsTab", settings, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
