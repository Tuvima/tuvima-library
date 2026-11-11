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
        var css = RepoFile("src", "MediaEngine.Web", "Components", "Player", "PlaybackShell.razor.css");

        Assert.Contains("grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr);", css, StringComparison.Ordinal);
        Assert.DoesNotContain("grid-template-columns: auto minmax(0, 1fr) auto;", css, StringComparison.Ordinal);
        var identityCss = RepoFile("src", "MediaEngine.Web", "Components", "Player", "PlaybackIdentity.razor.css");
        Assert.Contains(".playback-identity__title,", identityCss, StringComparison.Ordinal);
        Assert.Contains("text-overflow: ellipsis;", identityCss, StringComparison.Ordinal);
    }

    [Fact]
    public void AudiobookChapterLabel_SitsOnTheTimelineSoTheBarIsNotTallerThanOtherMedia()
    {
        // Unified player v2 D9: the audiobook's "Chapter 8" label is part of the 28 px timeline row; there is no extra strip above it.
        var css = RepoFile("src", "MediaEngine.Web", "Components", "Player", "ListenNowPlayingBar.razor.css");
        var markup = RepoFile("src", "MediaEngine.Web", "Components", "Player", "ListenNowPlayingBar.razor");

        Assert.DoesNotContain("listen-player__scope", css, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-player__scope", markup, StringComparison.Ordinal);
        Assert.DoesNotContain(".playback-shell__timeline { height:56px; }", css, StringComparison.Ordinal);
    }
}
