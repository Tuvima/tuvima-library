using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MediaEngine.Web.Tests;

public sealed class Wave6HardeningGuardrailTests
{
    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void SharedStringResources_HaveMatchingKeysAcrossSupportedCultures()
    {
        var neutral = ResourceKeys("SharedStrings.resx");

        foreach (var cultureFile in new[] { "SharedStrings.fr.resx", "SharedStrings.de.resx", "SharedStrings.es.resx" })
        {
            var localized = ResourceKeys(cultureFile);
            Assert.Empty(neutral.Except(localized, StringComparer.Ordinal).Order(StringComparer.Ordinal));
            Assert.Empty(localized.Except(neutral, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void SelectedHighVisibilityComponents_UseSharedStringsForWaveSixCopy()
    {
        var layout = Read("src/MediaEngine.Web/Shared/MainLayout.razor");
        var popup = Read("src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor");

        Assert.Contains("@L[\"Layout_SkipToContent\"]", layout);
        Assert.Contains("L[\"TopBar_ServiceDegradedBanner\"]", layout);
        Assert.Contains("@L[\"Listen_NothingPlaying\"]", popup);
        Assert.Contains("@L[\"Listen_MiniPlayerTitle\"]", popup);
        Assert.Contains("<PlaybackFullPlayer", popup);
        Assert.Contains("Recent listening positions", Read("src/MediaEngine.Web/Components/Listen/PlaybackContextPanel.razor"));
        Assert.DoesNotContain(">Nothing is playing right now.<", popup, StringComparison.Ordinal);
        Assert.DoesNotContain(">Playing Next<", popup, StringComparison.Ordinal);
    }

    [Fact]
    public void LargeInlineStyleBlocks_AreMovedFromSelectedComponents()
    {
        var popup = Read("src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor");
        var popupCss = Path.Combine(RepoRoot, "src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor.css");

        Assert.DoesNotContain("<style>", popup, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(popupCss));
    }

    [Fact]
    public void JavaScriptInteropRegistrations_HavePairedCleanupForTouchedPaths()
    {
        var appJs = Read("src/MediaEngine.Web/wwwroot/app.js");
        var layout = Read("src/MediaEngine.Web/Shared/MainLayout.razor");
        var nowPlaying = Read("src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor");
        var popup = Read("src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor");

        Assert.Contains("window.unregisterCtrlK", appJs);
        Assert.Contains("unregisterStateHandler", appJs);
        Assert.Contains("unregisterCommandHandler", appJs);
        Assert.Contains("unregisterPopupWindow", appJs);
        Assert.Contains("@implements IAsyncDisposable", layout);
        Assert.Contains("unregisterCtrlK", layout);
        Assert.Contains("@implements IAsyncDisposable", nowPlaying);
        Assert.Contains("unregisterCommandHandler", nowPlaying);
        Assert.Contains("@implements IAsyncDisposable", popup);
        Assert.Contains("unregisterStateHandler", popup);
        Assert.Contains("unregisterPopupWindow", popup);
    }

    [Fact]
    public void DotNetObjectReferenceComponents_DisposeTheirReferences()
    {
        var componentPaths = Directory.EnumerateFiles(
                Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components"),
                "*.razor",
                SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(
                Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Shared"),
                "*.razor",
                SearchOption.AllDirectories));

        var offenders = componentPaths
            .Select(path => new { Path = path, Text = File.ReadAllText(path) })
            .Where(file => file.Text.Contains("DotNetObjectReference", StringComparison.Ordinal)
                && !DotNetDisposeRegex.IsMatch(file.Text))
            .Select(file => Path.GetRelativePath(RepoRoot, file.Path))
            .ToList();

        Assert.Empty(offenders);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static HashSet<string> ResourceKeys(string fileName)
    {
        var path = Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Resources", fileName);
        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .Select(element => element.Attribute("name")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static readonly Regex DotNetDisposeRegex =
        new(@"DotNetObjectReference[\s\S]*?\.Dispose\(\)", RegexOptions.Compiled);
}
