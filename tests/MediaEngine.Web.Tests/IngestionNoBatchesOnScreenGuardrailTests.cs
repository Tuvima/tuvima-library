using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// There are no batches anywhere a person can see on the live Library and Ingestion page: no batch history, no
/// "Open batch", no run links. (The Engine still keeps its own batch records until they are removed there.)
/// </summary>
public sealed class IngestionNoBatchesOnScreenGuardrailTests
{
    private static readonly string[] LivePageSources =
    [
        "Components/Pages/RecentlyAddedPageContent.razor",
        "Components/Settings/Libraries/LibraryStatusLine.razor",
        "Components/Settings/IngestionMediaCard.razor",
        "Components/Settings/IngestionFacetIndicators.razor",
        "Components/Settings/IngestionMediaDrawer.razor",
        "Components/Settings/IngestionNowText.cs",
        "Components/Settings/IngestionNowWindow.cs",
    ];

    private static readonly string[] RetiredFiles =
    [
        "Components/Settings/IngestionBatchHistory.razor",
        "Components/Settings/IngestionBatchHistory.razor.css",
        "Components/Settings/IngestionBatchDisplay.cs",
        "Components/Settings/IngestionMediaPagedView.razor",
        "Components/Settings/IngestionMediaPagedView.razor.css",
        "Components/Settings/IngestionRecentlyAddedPreview.razor",
        "Components/Settings/IngestionRecentlyAddedPreview.razor.css",
    ];

    [Fact]
    public void TheLivePage_SaysNothingAboutBatchesOrRunsAndLinksToNoRun()
    {
        var wording = new Regex(@"\bbatch(es)?\b|ingestion runs?\b|open batch|view batch|runId", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (var relativePath in LivePageSources)
        {
            var offenders = wording.Matches(Read(relativePath)).Select(match => match.Value).ToArray();
            Assert.True(offenders.Length == 0, $"{relativePath} mentions: {string.Join(", ", offenders)}");
        }
    }

    [Fact]
    public void TheRetiredBatchScreens_AreGone()
    {
        foreach (var relativePath in RetiredFiles)
        {
            Assert.False(File.Exists(Path.Combine(SourceRoot, relativePath)), $"{relativePath} should be deleted.");
        }
    }

    [Fact]
    public void TheSettingsShell_NoLongerReadsARunOrItemFromTheAddress()
    {
        var settings = Read("Components/Pages/Settings.razor");

        Assert.DoesNotContain("runId", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("itemId", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadBatchBreadcrumbAsync", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDrawer_HasNoViewBatchButton()
    {
        Assert.DoesNotContain("View batch", Read("Components/Settings/IngestionMediaDrawer.razor"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAttentionRoutesTheEngineBuilds_NeverCarryARunId()
    {
        var engine = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Api", "Services", "ReadServices", "IngestionPresentationReadService.cs")));
        var snapshotStart = engine.IndexOf("GetSnapshotAsync(", StringComparison.Ordinal);
        var snapshotEnd = engine.IndexOf("GetCurrentMediaAsync(", snapshotStart, StringComparison.Ordinal);
        var snapshot = engine[snapshotStart..snapshotEnd];

        Assert.DoesNotContain("runId", snapshot, StringComparison.Ordinal);
        Assert.Contains("Route = \"/settings/review\"", snapshot, StringComparison.Ordinal);
    }

    private static string SourceRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web"));

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(SourceRoot, relativePath));
}
