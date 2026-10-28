using Xunit;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Read, Listen and Watch are one family (spec library-area-pages, F69): each is a thin page over the same lane page and
/// browse shell, so the top bar, the Continue row and the Cards/List switch cannot drift apart.
/// </summary>
public sealed class LibraryAreaPagesGuardrailTests
{
    [Theory]
    [InlineData("ReadPage.razor")]
    [InlineData("WatchPage.razor")]
    [InlineData("ListenBrowsePage.razor")]
    public void AreaPage_IsAThinWrapperOverTheSharedLanePage(string page)
    {
        var source = ReadSource($"src/MediaEngine.Web/Components/Pages/{page}");

        Assert.Contains("<MediaLanePage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CompactContinueStrip", source, StringComparison.Ordinal);
        Assert.DoesNotContain("layout-toggle", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLanePage_HostsTheSharedBrowseShell_WhichOwnsContinueAndTheCardsListSwitch()
    {
        var lane = ReadSource("src/MediaEngine.Web/Components/Pages/MediaLanePage.razor");
        var shell = ReadSource("src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor");

        Assert.Contains("<MediaBrowseShell", lane, StringComparison.Ordinal);
        Assert.Contains("<CompactContinueStrip", shell, StringComparison.Ordinal);
        Assert.Contains("browse-shell__layout-toggle", shell, StringComparison.Ordinal);
        Assert.Contains("Continue Reading", shell, StringComparison.Ordinal);
        Assert.Contains("Continue Listening", shell, StringComparison.Ordinal);
        Assert.Contains("Continue Watching", shell, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
