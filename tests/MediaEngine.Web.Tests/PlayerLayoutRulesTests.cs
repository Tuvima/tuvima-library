namespace MediaEngine.Web.Tests;

/// <summary>Source checks for the unified player layout decisions that a render test cannot see (the CSS is not applied in bUnit).</summary>
public sealed class PlayerLayoutRulesTests
{
    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }

    [Fact]
    public void DesktopVideoBar_KeepsPlayInTheTrueCentreWithTheTitleOnTheLeft()
    {
        // Unified player v2 D2: one shell draws the bar for every media, with equal outer columns so Play is at the true centre.
        var css = RepoFile("src", "MediaEngine.Web", "Components", "Shared", "PlaybackShell.razor.css");

        Assert.Contains("grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr);", css, StringComparison.Ordinal);
        Assert.DoesNotContain("grid-template-columns: auto minmax(0, 1fr) auto;", css, StringComparison.Ordinal);
        var identityCss = RepoFile("src", "MediaEngine.Web", "Components", "Shared", "PlaybackIdentity.razor.css");
        Assert.Contains(".playback-identity__title,", identityCss, StringComparison.Ordinal);
        Assert.Contains("text-overflow: ellipsis;", identityCss, StringComparison.Ordinal);
    }

    [Fact]
    public void AudiobookChapterStrip_IsOutOfTheDockGridSoTheTransportRowStaysInsideTheWindow()
    {
        var css = RepoFile("src", "MediaEngine.Web", "Components", "Listen", "ListenNowPlayingBar.razor.css");

        Assert.Contains(".listen-player__scope { position:absolute;", css, StringComparison.Ordinal);
        Assert.Contains(".listen-player--audiobook .playback-shell__timeline { height:56px; }", css, StringComparison.Ordinal);
    }
}
