using System.Text.Json;

namespace MediaEngine.Web.Tests;

public sealed class ResponsivePwaGuardrailTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void IsolatedStylesheets_DoNotContainEscapedMediaRules()
    {
        var files = Directory.EnumerateFiles(
            Path.Combine(RepoRoot, "src", "MediaEngine.Web"),
            "*.razor.css",
            SearchOption.AllDirectories);

        foreach (var file in files)
        {
            Assert.DoesNotContain("@@media", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NavigationBreakpoint_IsSharedByTokensJavascriptAndShell()
    {
        var tokens = Read("src/MediaEngine.Web/wwwroot/tuvima.tokens.css");
        var script = Read("src/MediaEngine.Web/wwwroot/app.js");
        var shell = Read("src/MediaEngine.Web/Shared/MainLayout.razor.css");

        Assert.Contains("--tl-breakpoint-navigation: 840;", tokens, StringComparison.Ordinal);
        Assert.Contains("readBreakpoint('navigation', 840)", script, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 840px)", shell, StringComparison.Ordinal);
        Assert.Contains("@media (min-width: 841px)", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("innerWidth <= 768", script, StringComparison.Ordinal);
        Assert.DoesNotContain("max-width: 760px", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallableManifest_HasOnlineLaunchScopeAndRequiredIcons()
    {
        var manifestPath = Path.Combine(RepoRoot, "src", "MediaEngine.Web", "wwwroot", "manifest.webmanifest");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;

        Assert.Equal("/", root.GetProperty("start_url").GetString());
        Assert.Equal("/", root.GetProperty("scope").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());
        Assert.Equal(4, root.GetProperty("icons").GetArrayLength());

        foreach (var icon in root.GetProperty("icons").EnumerateArray())
        {
            var relativePath = icon.GetProperty("src").GetString()!.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            Assert.True(File.Exists(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "wwwroot", relativePath)));
        }

        var worker = Read("src/MediaEngine.Web/wwwroot/service-worker.js");
        Assert.Contains("addEventListener('fetch'", worker, StringComparison.Ordinal);
        Assert.Contains("fetch(event.request)", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("caches.open", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalShell_RendersConfiguredDockAndSafeAreaContract()
    {
        var layout = Read("src/MediaEngine.Web/Shared/MainLayout.razor");
        var layoutStyles = Read("src/MediaEngine.Web/Shared/MainLayout.razor.css");
        var tokens = Read("src/MediaEngine.Web/wwwroot/tuvima.tokens.css");

        Assert.Contains("DeviceContext.Settings.Shell.DockVisible", layout, StringComparison.Ordinal);
        Assert.Contains("DeviceContext.Settings.Shell.IntentDockItems", layout, StringComparison.Ordinal);
        Assert.Contains("layout-shell__intent-dock", layout, StringComparison.Ordinal);
        Assert.Contains("--tl-safe-area-bottom", tokens, StringComparison.Ordinal);
        Assert.Contains("--tl-touch-target-min: 48px", tokens, StringComparison.Ordinal);
        Assert.Contains("var(--tl-safe-area-bottom)", layoutStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryAndDetailSurfaces_ExposePhoneNavigationAndTouchTargets()
    {
        var section = Read("src/MediaEngine.Web/Components/MediaHub/MediaSectionShell.razor");
        var sectionStyles = Read("src/MediaEngine.Web/Components/MediaHub/MediaSectionShell.razor.css");
        var detailStyles = Read("src/MediaEngine.Web/Components/Details/DetailPage.razor.css");

        Assert.Contains("media-section-shell__mobile-nav", section, StringComparison.Ordinal);
        Assert.Contains("<details>", section, StringComparison.Ordinal);
        Assert.Contains("min-height: var(--tl-touch-target-min, 48px)", sectionStyles, StringComparison.Ordinal);
        Assert.Contains("height: 100%;", sectionStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("var(--tl-bottom-dock-height", sectionStyles, StringComparison.Ordinal);
        Assert.Contains("@media (min-width:841px)", sectionStyles, StringComparison.Ordinal);
        Assert.Contains("@container playback-frame (max-height:700px)", sectionStyles, StringComparison.Ordinal);
        Assert.Contains("display: block;", detailStyles, StringComparison.Ordinal);
        Assert.Contains("min-height: var(--tl-touch-target-min, 48px)", detailStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticationShell_ExposesInstallMetadataAndSafeAreaSizing()
    {
        var source = Read("src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs");

        Assert.Contains("viewport-fit=cover", source, StringComparison.Ordinal);
        Assert.Contains("rel=\"manifest\"", source, StringComparison.Ordinal);
        Assert.Contains("/service-worker.js", source, StringComparison.Ordinal);
        Assert.Contains("env(safe-area-inset-bottom)", source, StringComparison.Ordinal);
        Assert.Contains("min-height: 3rem", source, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
