using System.Text.Json;

namespace MediaEngine.Web.Tests;

public sealed class ResponsivePwaGuardrailTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void PhoneNavigation_UsesStableIconTargetsAndWrappingDestinationLabels()
    {
        var layout = Read("src/MediaEngine.Web/Shared/MainLayout.razor.css").Replace("\r\n", "\n", StringComparison.Ordinal);
        var global = Read("src/MediaEngine.Web/wwwroot/app.css");
        var account = Read("src/MediaEngine.Web/Components/Navigation/TopNavAccountMenu.razor.css");
        var phone = layout[layout.IndexOf("html[data-form-factor=\"phone\"] .layout-shell__intent-dock {\n    display: flex;", StringComparison.Ordinal)..];

        Assert.Contains("grid-template-columns: minmax(0, 1fr) 0 auto;", phone, StringComparison.Ordinal);
        Assert.Contains("--shell-topbar-columns: minmax(0, 1fr) 0 auto;", global, StringComparison.Ordinal);
        // The three dock tabs share the pill equally, each a 48 px target; labels wrap rather than clip.
        Assert.Contains("flex: 1 1 0;", phone, StringComparison.Ordinal);
        Assert.Contains("min-width: 48px;", phone, StringComparison.Ordinal);
        Assert.Contains("min-height: 48px;", phone, StringComparison.Ordinal);
        Assert.Contains("grid-template-rows: 22px auto;", phone, StringComparison.Ordinal);
        Assert.Contains("white-space: normal;", phone, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere;", phone, StringComparison.Ordinal);
        Assert.Contains("width: 24px;", phone, StringComparison.Ordinal);
        Assert.Contains("width: 22px;\n    height: 22px;\n    font-size: 22px;", phone, StringComparison.Ordinal);
        // The phone top bar has no search icon (Search is a dock tab) and no wordmark (the icon only).
        Assert.DoesNotContain("layout-shell__search-action", phone, StringComparison.Ordinal);
        Assert.DoesNotContain("layout-shell__brand-lockup", phone, StringComparison.Ordinal);
        Assert.Contains(".layout-shell__my-list-slot ::deep .layout-shell__my-list-icon,", phone, StringComparison.Ordinal);
        Assert.Contains(".system-activity-indicator__ring .tl-progress-circular {\n    width: 32px !important;\n    height: 32px !important;", phone, StringComparison.Ordinal);
        var accountPhone = account[account.IndexOf("@media (max-width: 840px)", StringComparison.Ordinal)..];
        Assert.Contains("width: 48px;", accountPhone, StringComparison.Ordinal);
        Assert.Contains("height: 48px;", accountPhone, StringComparison.Ordinal);
        Assert.Contains("width: 32px !important;", accountPhone, StringComparison.Ordinal);
    }

    [Fact]
    public void TabletTopBar_KeepsItsNavigationLabelsClearOfTheIcons()
    {
        var global = Read("src/MediaEngine.Web/wwwroot/app.css").Replace("\r\n", "\n", StringComparison.Ordinal);
        var layout = Read("src/MediaEngine.Web/Shared/MainLayout.razor.css").Replace("\r\n", "\n", StringComparison.Ordinal);
        var markup = Read("src/MediaEngine.Web/Shared/MainLayout.razor");
        var account = Read("src/MediaEngine.Web/Components/Navigation/TopNavAccountMenu.razor.css").Replace("\r\n", "\n", StringComparison.Ordinal);

        // Only the phone shell drops the navigation column. A width query would also hit tablets and narrow desktop
        // windows, which keep the top navigation, and their labels would then run under Search, My List and activity.
        const string collapsedColumns = "--shell-topbar-columns: minmax(0, 1fr) 0 auto;";
        var collapse = global.IndexOf(collapsedColumns, StringComparison.Ordinal);
        Assert.True(collapse >= 0, "app.css should still collapse the navigation column for the phone shell.");
        var collapseRule = global[(global.LastIndexOf('}', collapse) + 1)..collapse];
        Assert.DoesNotContain("@media", collapseRule, StringComparison.Ordinal);
        Assert.Contains("html[data-form-factor=\"phone\"] .layout-shell__appbar .tl-toolbar {", collapseRule, StringComparison.Ordinal);
        Assert.Equal(collapse, global.LastIndexOf(collapsedColumns, StringComparison.Ordinal));

        // At 840px and under, the logo and the icons keep their own width and the labels take what is left.
        var compact = global[global.IndexOf("html:not([data-form-factor=\"phone\"]) .layout-shell__appbar .tl-toolbar {", StringComparison.Ordinal)..];
        compact = compact[..compact.IndexOf('}', StringComparison.Ordinal)];
        Assert.Contains("--shell-topbar-columns: auto minmax(0, 1fr) auto;", compact, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: var(--shell-topbar-columns) !important;", global, StringComparison.Ordinal);

        // The same width swaps the wordmark for the icon logo and lets the labels scroll inside their own column.
        Assert.Contains("layout-shell__brand-icon-shell--compact", markup, StringComparison.Ordinal);
        var shellCompact = layout[layout.IndexOf(".layout-shell__brand-icon-shell--compact {\n    display: none;", StringComparison.Ordinal)..];
        shellCompact = shellCompact[shellCompact.IndexOf("@media (max-width: 840px)", StringComparison.Ordinal)..];
        Assert.Contains(".layout-shell__brand-lockup-shell {\n        display: none;", shellCompact, StringComparison.Ordinal);
        Assert.Contains("justify-content: safe center;", shellCompact, StringComparison.Ordinal);
        Assert.Contains("overflow-x: auto;", shellCompact, StringComparison.Ordinal);

        // The account button is the avatar alone there, so a long display name cannot push into the labels.
        var accountCompact = account[account.IndexOf("@media (max-width: 840px)", StringComparison.Ordinal)..];
        Assert.Contains(".top-nav-account-menu__trigger-copy {\n        display: none;", accountCompact, StringComparison.Ordinal);
    }

    [Fact]
    public void PhoneChrome_HasFixedRowsAFloatingDockAndOneHeroVariable()
    {
        var tokens = Read("src/MediaEngine.Web/wwwroot/tuvima.tokens.css").Replace("\r\n", "\n", StringComparison.Ordinal);
        var layout = Read("src/MediaEngine.Web/Shared/MainLayout.razor.css").Replace("\r\n", "\n", StringComparison.Ordinal);
        var hero = Read("src/MediaEngine.Web/Components/Cinematic/PhoneHeroCard.razor.css");
        var global = Read("src/MediaEngine.Web/wwwroot/app.css");

        // The top chrome is a fixed 56 px bar row plus a fixed 48 px chips row; --app-topbar-height is their sum, so every
        // hero offset and sticky top keeps working from the one variable.
        var phoneTokens = tokens[tokens.IndexOf("html[data-form-factor=\"phone\"] {", StringComparison.Ordinal)..];
        Assert.Contains("--tl-appbar-row-height: 56px;", phoneTokens, StringComparison.Ordinal);
        Assert.Contains("--tl-phone-chips-height: 48px;", phoneTokens, StringComparison.Ordinal);
        Assert.Contains("--app-topbar-height: calc(var(--tl-appbar-row-height) + var(--tl-phone-chips-height));", phoneTokens, StringComparison.Ordinal);
        Assert.Contains("--tl-phone-dock-height: 56px;", phoneTokens, StringComparison.Ordinal);
        Assert.Contains("max(var(--tl-phone-dock-gap), var(--tl-safe-area-bottom))", phoneTokens, StringComparison.Ordinal);
        Assert.Contains("grid-template-rows: var(--tl-appbar-row-height) var(--tl-phone-chips-height);", layout, StringComparison.Ordinal);

        // The dock is a floating pill that keeps its own grid row, so it never covers the last row of the page.
        var dock = layout[layout.IndexOf("html[data-form-factor=\"phone\"] .layout-shell__intent-dock {\n    display: flex;", StringComparison.Ordinal)..];
        dock = dock[..dock.IndexOf('}', StringComparison.Ordinal)];
        Assert.Contains("border-radius: 999px;", dock, StringComparison.Ordinal);
        Assert.Contains("var(--tl-safe-area-bottom)", dock, StringComparison.Ordinal);
        Assert.Contains("height: var(--tl-phone-dock-height);", dock, StringComparison.Ordinal);
        Assert.Contains("grid-row: 4;", layout, StringComparison.Ordinal);

        // The Home hero measures from the whole chrome and has one reserve variable for the bottom bar.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(hero, @"--phone-hero-bottom-bar:\s*[^;]+;"));
        Assert.Contains("var(--app-topbar-height, 65px)", hero, StringComparison.Ordinal);
        Assert.Contains("var(--phone-hero-bottom-bar)", hero, StringComparison.Ordinal);

        // The menu frame respects reduced motion: it only animates for people who have not asked for less.
        var menuMotion = global[global.IndexOf(".phone-nav-menu-dialog[open]", StringComparison.Ordinal)..];
        Assert.Contains("prefers-reduced-motion: no-preference", global[(global.LastIndexOf("@media", global.IndexOf(".phone-nav-menu-dialog[open]", StringComparison.Ordinal), StringComparison.Ordinal))..], StringComparison.Ordinal);
        Assert.Contains("animation: phone-nav-menu-in", menuMotion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PhoneNavigationMenu")]
    [InlineData("PhoneSectionChips")]
    [InlineData("PhoneDownloadsAction")]
    public void PhoneNavigationComponents_FollowTheDashboardStyleRules(string component)
    {
        var markup = Read($"src/MediaEngine.Web/Components/Navigation/{component}.razor");
        var styles = Read($"src/MediaEngine.Web/Components/Navigation/{component}.razor.css");

        // No inline styles, no ::deep, no raw form inputs; colours and spacing come from the CSS tokens.
        Assert.DoesNotContain(" style=", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<input", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("::deep", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("!important", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("@media (max-width", styles, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b", styles);
    }

    [Fact]
    public void PhoneNavigationScripts_ArePlainModulesThatCleanUpAfterThemselves()
    {
        var menu = Read("src/MediaEngine.Web/wwwroot/js/phone-nav-menu.js");
        var roving = Read("src/MediaEngine.Web/wwwroot/js/roving-focus.js");

        // Back closes the menu through a history entry the menu owns; the entry is removed when the menu closes another way.
        Assert.Contains("history.pushState", menu, StringComparison.Ordinal);
        Assert.Contains("popstate", menu, StringComparison.Ordinal);
        Assert.Contains("stopImmediatePropagation", menu, StringComparison.Ordinal);
        Assert.Contains("history.back()", menu, StringComparison.Ordinal);
        Assert.Contains("removeEventListener('popstate'", menu, StringComparison.Ordinal);
        Assert.Contains("ArrowRight", roving, StringComparison.Ordinal);
        Assert.Contains("removeEventListener('keydown'", roving, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackFrame_ContainsContentWidthWhilePreservingSidebarAndCinematicScrollOwnership()
    {
        var styles = Read("src/MediaEngine.Web/Shared/MainLayout.razor.css");
        var sidebar = Read("src/MediaEngine.Web/Components/Shared/ContextSidebarShell.razor.css");

        foreach (var selector in new[]
        {
            ".playback-app-frame",
            ".playback-app-frame__content",
            ".playback-app-frame ::deep .layout-context-sidebar"
        })
        {
            var start = styles.IndexOf(selector + " {", StringComparison.Ordinal);
            Assert.True(start >= 0);
            var end = styles.IndexOf('}', start);
            var rule = styles[start..end];
            Assert.Contains("box-sizing: border-box;", rule, StringComparison.Ordinal);
            Assert.Contains("width: 100%;", rule, StringComparison.Ordinal);
            Assert.Contains("max-width: 100%;", rule, StringComparison.Ordinal);
            Assert.Contains("min-width: 0;", rule, StringComparison.Ordinal);
        }

        Assert.Contains("grid-template-columns: minmax(0, 1fr);", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-rows: auto minmax(0, 1fr) auto;", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1fr) minmax(0, var(--context-sidebar-width, 340px));", sidebar, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto;", styles, StringComparison.Ordinal);
        Assert.Contains("margin-top: calc(-1 * var(--app-topbar-height, 65px));", styles, StringComparison.Ordinal);
    }

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
