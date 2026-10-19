namespace MediaEngine.Web.Tests;

public sealed class SettingsBreadcrumbGuardrailTests
{
    [Fact]
    public void SettingsShell_OwnsRouteAwareBreadcrumbsAndIngestionHeaderAction()
    {
        var settings = Read(@"src/MediaEngine.Web/Components/Pages/Settings.razor");
        var frame = Read(@"src/MediaEngine.Web/Components/Settings/Frame/SettingsPageFrame.razor");
        var frameCss = Read(@"src/MediaEngine.Web/Components/Settings/Frame/SettingsPageFrame.razor.css");
        var metadata = Read(@"src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");

        Assert.Contains("<AppBreadcrumbs Class=\"settings-breadcrumbs\">", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppBreadcrumbs", settings, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\"", frame, StringComparison.Ordinal);
        Assert.Contains("[SupplyParameterFromQuery(Name = \"runId\")]", settings, StringComparison.Ordinal);
        Assert.Contains("IngestionBatchDisplay.Title(batch)", settings, StringComparison.Ordinal);
        Assert.Contains("SettingsSection.Ingestion => \"What your library holds", settings, StringComparison.Ordinal);
        Assert.Contains("<IngestionTasksTab", settings, StringComparison.Ordinal);
        Assert.Contains("<RecentlyAddedPageContent", settings, StringComparison.Ordinal);
        Assert.Contains("focus-visible", frameCss, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppBreadcrumbs>", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsBreadcrumbLinks_KeepA44PixelTapTargetInTheSharedBlock()
    {
        var css = Read(@"src/MediaEngine.Web/wwwroot/app.css");
        var block = css[css.IndexOf("Shared 44px tap-target contract", StringComparison.Ordinal)..];
        var rule = block[block.IndexOf(":root:root .app-breadcrumbs.settings-breadcrumbs a {", StringComparison.Ordinal)..];
        rule = rule[..rule.IndexOf('}', StringComparison.Ordinal)];

        Assert.Contains("min-width: var(--tl-tap-target-min);", rule, StringComparison.Ordinal);
        Assert.Contains("min-height: var(--tl-tap-target-min);", rule, StringComparison.Ordinal);
        Assert.Contains("justify-content: center;", rule, StringComparison.Ordinal);
        // The base rule keeps the links inline-flex so the label is centred inside the target.
        Assert.Contains(":root:root .settings-breadcrumbs a,", block, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
