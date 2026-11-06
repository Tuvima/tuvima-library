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
        var css = RepoFile("src", "MediaEngine.Web", "wwwroot", "app.css");

        Assert.Contains("grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr); gap: 16px; }", css, StringComparison.Ordinal);
        Assert.DoesNotContain("grid-template-columns: auto minmax(0, 1fr) auto; gap: 16px; }", css, StringComparison.Ordinal);
        Assert.Contains(".playback-video-controls__identity { text-align: left;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AudiobookChapterStrip_IsOutOfTheDockGridSoTheTransportRowStaysInsideTheWindow()
    {
        var css = RepoFile("src", "MediaEngine.Web", "Components", "Listen", "ListenNowPlayingBar.razor.css");

        Assert.Contains(".listen-player__scope { position:absolute;", css, StringComparison.Ordinal);
        Assert.Contains(".listen-player--audiobook { height:calc(132px", css, StringComparison.Ordinal);
    }
}
