namespace MediaEngine.Web.Tests;

/// <summary>
/// Look pass WP5: icon color is chosen by meaning in one place. Green means healthy or done only, so the idle Activity indicator
/// and the setup "Private by default" panel never use it; the setup welcome icons are the shared tone tile.
/// </summary>
public sealed class IconToneGuardrailTests
{
    private static string Read(string relative) => File.ReadAllText(Path.Combine(FindRepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void TheIdleActivityIndicatorIsNotGreen()
    {
        var css = Read("src/MediaEngine.Web/Components/Navigation/SystemActivityIndicator.razor.css");
        var idle = css[css.IndexOf(".is-idle", StringComparison.Ordinal)..];
        idle = idle[..idle.IndexOf('}', StringComparison.Ordinal)];
        Assert.DoesNotContain("status-success", idle, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSetupWelcomeUsesTheSharedToneTileAndNoGreenFallback()
    {
        var razor = Read("src/MediaEngine.Web/Components/Setup/SetupIntroStage.razor");
        var css = Read("src/MediaEngine.Web/Components/Setup/SetupIntroStage.razor.css");
        Assert.Contains("<AppIconTile", razor, StringComparison.Ordinal);
        Assert.Contains("Tone=\"AppIconTone.", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("tl-success", css, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryIconToneHasAColorInTheTokenFile()
    {
        var tokens = Read("src/MediaEngine.Web/wwwroot/tuvima.tokens.css");
        foreach (var token in new[] { "--tl-tone-neutral", "--tl-tone-accent", "--tl-tone-info", "--tl-tone-success", "--tl-tone-warning", "--tl-tone-danger", "--tl-area-watch", "--tl-area-listen", "--tl-area-read", "--tl-area-view", "--tl-area-vault" })
        {
            Assert.Contains(token + ":", tokens, StringComparison.Ordinal);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
