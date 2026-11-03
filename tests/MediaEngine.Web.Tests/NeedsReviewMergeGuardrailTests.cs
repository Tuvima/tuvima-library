using MediaEngine.Web.Components.Settings;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Universal editor v3 WP7 and D9: Needs Review is the one list for items the Engine is unsure about and files it could not match, so a library
/// has no Unidentified tab of its own and every count and link that used to point at it points at Needs Review instead.
/// </summary>
public sealed class NeedsReviewMergeGuardrailTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void TheUnidentifiedTabIsGone_FromTheLibraryAndFromTheSourceTree()
    {
        Assert.False(File.Exists(Path.Combine(Root, "src", "MediaEngine.Web", "Components", "Settings", "Libraries", "LibraryUnidentifiedTab.razor")));
        var shell = File.ReadAllText(Path.Combine(Root, "src", "MediaEngine.Web", "Components", "Settings", "Libraries", "LibraryShell.razor"));
        Assert.DoesNotContain("LibraryUnidentifiedTab", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("UnidentifiedTab", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("tab=unidentified", shell, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("movies", "/settings/review?library=movies&show=not-matched")]
    [InlineData("a b", "/settings/review?library=a%20b&show=not-matched")]
    public void ALibrarysNotIdentifiedLink_OpensNeedsReviewFilteredToThatLibrarysUnmatchedFiles(string library, string expected) =>
        Assert.Equal(expected, LibraryListing.NotIdentifiedAddress(library));

    [Fact]
    public void NeedsReviewReadsTheOneMergedList_NotTwoLists()
    {
        var page = File.ReadAllText(Path.Combine(Root, "src", "MediaEngine.Web", "Components", "Settings", "NeedsReviewPageContent.razor"));

        Assert.Contains("GetNeedsReviewListAsync(", page, StringComparison.Ordinal);
        Assert.DoesNotContain("GetUnmatchedItemsAsync", page, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPendingReviews", page, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
