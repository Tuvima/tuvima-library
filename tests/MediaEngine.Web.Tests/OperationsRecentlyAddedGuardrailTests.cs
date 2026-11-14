namespace MediaEngine.Web.Tests;

public sealed class OperationsRecentlyAddedGuardrailTests
{
    [Fact]
    public void Administration_ConsolidatesLiveHistoryAndReviewWithoutParallelEditors()
    {
        var review = Read(@"src/MediaEngine.Web/Components/Settings/NeedsReviewPageContent.razor");
        var row = Read(@"src/MediaEngine.Web/Components/Settings/NeedsReview/NeedsReviewRow.razor");
        var statusLine = Read(@"src/MediaEngine.Web/Components/Settings/Libraries/LibraryStatusLine.razor");
        var card = Read(@"src/MediaEngine.Web/Components/Settings/IngestionMediaCard.razor");
        var settings = Read(@"src/MediaEngine.Web/Components/Pages/Settings.razor");

        Assert.Contains("GetNeedsReviewListAsync(", review, StringComparison.Ordinal);
        Assert.Contains("RefreshReviewCountAsync", review, StringComparison.Ordinal);
        Assert.Contains("EditLauncher.OpenAsync(target, EditTool.Match,", review, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaEditorLauncher", review, StringComparison.Ordinal);
        Assert.Contains("<AppRowMenu", row, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"library-status-line\"", statusLine, StringComparison.Ordinal);
        Assert.Contains("ingestion-media-list-row__identity", card, StringComparison.Ordinal);
        Assert.Contains("ingestion-media-list-row__step", card, StringComparison.Ordinal);
        Assert.Contains("ingestion-media-list-row__details", card, StringComparison.Ordinal);
        Assert.Contains("<LibraryActivityPage", Read("src/MediaEngine.Web/Components/Settings/General/GeneralPage.razor"), StringComparison.Ordinal);
        Assert.Contains("<NeedsReviewPageContent", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("<IngestionSettingsTab", settings, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
