namespace MediaEngine.Web.Tests;

/// <summary>
/// One editor, one bulk path (universal editor v3 WP3): the pop-over is hosted once (<c>EditorHost</c>), every list reaches it through
/// <c>ListEditLauncher</c> or <c>BulkListEntry</c>, and no second bulk host or session owns staged changes. A list's Edit, Fix or Identify
/// never navigates to an item's page to open the editor there.
/// </summary>
public sealed class EditorEntryPointGuardrailTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    private static IEnumerable<string> DashboardSources() =>
        Directory.EnumerateFiles(Path.Combine(Root, "src", "MediaEngine.Web"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Fact]
    public void ThereIsNoSecondBulkHost_AndNoSecondSessionThatOwnsStagedChanges()
    {
        Assert.False(File.Exists(Path.Combine(Root, "src/MediaEngine.Web/Components/Details/BulkEditHost.razor")));
        Assert.False(File.Exists(Path.Combine(Root, "src/MediaEngine.Web/Services/Editing/BulkEditSession.cs")));

        foreach (var path in DashboardSources())
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("BulkEditHost", text, StringComparison.Ordinal);
            Assert.DoesNotContain("BulkEditSession", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OnlyTheEditorHostMakesTheStagedChangesAndTheSaver()
    {
        var offenders = DashboardSources()
            .Where(path => !path.EndsWith("EditorHost.cs", StringComparison.Ordinal))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("new PendingEditList(", StringComparison.Ordinal)
                    || text.Contains("new EditSessionSaver(", StringComparison.Ordinal)
                    || text.Contains("new EditSessionFlow(", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(Root, path))
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("src/MediaEngine.Web/Components/Settings/NeedsReviewPageContent.razor")]
    [InlineData("src/MediaEngine.Web/Components/Settings/Libraries/LibraryUnidentifiedTab.razor")]
    [InlineData("src/MediaEngine.Web/Components/Pages/RecentlyAddedPageContent.razor")]
    [InlineData("src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor")]
    [InlineData("src/MediaEngine.Web/Components/Pages/ListenPage.razor.cs")]
    [InlineData("src/MediaEngine.Web/Components/Listen/PlaybackLyrics.razor")]
    public void AListsEntryPoint_OpensThePopOverThroughTheLauncher_AndNeverSendsThePersonToAnItemsPageToEdit(string file)
    {
        var text = Read(file);

        Assert.Contains("ListEditLauncher", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OfferEditResume", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DetailsEditResume", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ListsThatTickRowsUseTheOneBulkEntry_NotAHostOfTheirOwn()
    {
        Assert.Contains("<BulkListEntry", Read("src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor"), StringComparison.Ordinal);
        Assert.Contains("<BulkListEntry", Read("src/MediaEngine.Web/Components/Listen/ListenSongTable.razor"), StringComparison.Ordinal);
        var entry = Read("src/MediaEngine.Web/Components/Details/BulkListEntry.razor");
        Assert.DoesNotContain("PendingEditList", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("ReviewChangesDialog", entry, StringComparison.Ordinal);
    }
}
