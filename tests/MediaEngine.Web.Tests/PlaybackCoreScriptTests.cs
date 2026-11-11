namespace MediaEngine.Web.Tests;

public sealed class PlaybackCoreScriptTests
{
    private static readonly string WebRoot = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")), "src", "MediaEngine.Web");

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([WebRoot, .. parts]));

    [Fact]
    public void AppShell_LoadsThePlaybackScriptFingerprintedAfterAppJsAndBeforeBlazor()
    {
        var shell = Read("Components", "App.razor");
        const string tag = "<script src=\"@Assets[\"js/playback-core.js\"]\"></script>";

        Assert.Contains(tag, shell, StringComparison.Ordinal);
        var app = shell.IndexOf("@Assets[\"app.js\"]", StringComparison.Ordinal);
        var playback = shell.IndexOf(tag, StringComparison.Ordinal);
        var blazor = shell.IndexOf("@Assets[\"_framework/blazor.web.js\"]", StringComparison.Ordinal);
        Assert.True(app >= 0 && app < playback, "playback-core.js loads after app.js");
        Assert.True(playback < blazor, "the playback globals must exist before Blazor starts");
    }

    [Theory]
    [InlineData("window.listenPlayback = ")]
    [InlineData("window.tuvimaPopupStateSync = ")]
    [InlineData("window.playbackTools = ")]
    public void PlaybackGlobals_LiveInThePlaybackScriptAndNotInAppJs(string definition)
    {
        Assert.Contains(definition, Read("wwwroot", "js", "playback-core.js"), StringComparison.Ordinal);
        Assert.DoesNotContain(definition, Read("wwwroot", "app.js"), StringComparison.Ordinal);
    }
}
