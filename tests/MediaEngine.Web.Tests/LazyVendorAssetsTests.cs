namespace MediaEngine.Web.Tests;

// The page shell no longer carries the two heavy vendor files; they load on demand through wwwroot/js/tuvima-lazy.js.
public sealed class LazyVendorAssetsTests
{
    private static readonly string WebRoot = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")), "src", "MediaEngine.Web");

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([WebRoot, .. parts]));

    [Fact]
    public void AppShell_DoesNotLoadHlsOrMapLibreOnEveryPage()
    {
        var shell = Read("Components", "App.razor");

        Assert.DoesNotContain("<script src=\"@Assets[\"vendor/hls", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("maplibre", shell, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("js/tuvima-lazy.js", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void AppShell_PassesTheFingerprintedHlsAddressToTheOnDemandLoader()
    {
        var shell = Read("Components", "App.razor");

        Assert.Contains("data-hls-src=\"@Assets[\"vendor/hls/hls.min.js\"]\"", shell, StringComparison.Ordinal);
        Assert.True(
            shell.IndexOf("js/tuvima-lazy.js", StringComparison.Ordinal) < shell.IndexOf("@Assets[\"app.js\"]", StringComparison.Ordinal),
            "the loader must exist before app.js can ask for hls.js");
    }

    [Fact]
    public void AppShell_FingerprintsTheLoaderAndTheTooltipModuleSoAnOldCachedCopyCannotLinger()
    {
        var shell = Read("Components", "App.razor");

        Assert.Contains("<script src=\"@Assets[\"js/tuvima-lazy.js\"]\"></script>", shell, StringComparison.Ordinal);
        Assert.Contains("<script type=\"module\" src=\"@Assets[\"js/app-overflow-tooltip.js\"]\"></script>", shell, StringComparison.Ordinal);
        // The module's own import of playback-tooltip.js is rewritten to its fingerprinted address by the import map.
        Assert.Contains("<ImportMap />", shell, StringComparison.Ordinal);
        Assert.True(
            shell.IndexOf("<ImportMap />", StringComparison.Ordinal) < shell.IndexOf("type=\"module\"", StringComparison.Ordinal),
            "the import map must come before the first module script");
    }

    [Fact]
    public void AudioSourceSync_LoadsHlsThroughTheLoaderInsteadOfAssumingAGlobal()
    {
        var script = Read("wwwroot", "js", "playback-core.js");

        Assert.Contains("window.tuvimaLazy.loadHls()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("window.Hls?.isSupported()", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PlacesMap_WaitsForItsStylesheetBeforeCreatingTheMap()
    {
        var script = Read("wwwroot", "js", "view-map.js");

        var styles = script.IndexOf("loadMapLibreStyles()", StringComparison.Ordinal);
        var map = script.IndexOf("new maplibregl.Map(", StringComparison.Ordinal);
        Assert.True(styles >= 0 && styles < map, "the stylesheet is awaited before the map is created");
    }
}
