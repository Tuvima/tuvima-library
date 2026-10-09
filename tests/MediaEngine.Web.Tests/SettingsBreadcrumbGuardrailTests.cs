namespace MediaEngine.Web.Tests;

public sealed class SettingsBreadcrumbGuardrailTests
{
    [Fact]
    public void SettingsShell_OwnsRouteAwareBreadcrumbsAndIngestionHeaderAction()
    {
        var settings = Read(@"src/MediaEngine.Web/Components/Pages/Settings.razor");
        var settingsCss = Read(@"src/MediaEngine.Web/Components/Pages/Settings.razor.css");
        var metadata = Read(@"src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");

        Assert.Contains("<AppBreadcrumbs Class=\"settings-breadcrumbs\">", settings, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\"", settings, StringComparison.Ordinal);
        Assert.Contains("[SupplyParameterFromQuery(Name = \"runId\")]", settings, StringComparison.Ordinal);
        Assert.Contains("IngestionBatchDisplay.Title(batch)", settings, StringComparison.Ordinal);
        Assert.Contains("SettingsSection.Ingestion => \"Configure how Tuvima Library discovers", settings, StringComparison.Ordinal);
        Assert.Contains("<IngestionTasksTab", settings, StringComparison.Ordinal);
        Assert.Contains("<RecentlyAddedPageContent", settings, StringComparison.Ordinal);
        Assert.Contains("focus-visible", settingsCss, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppBreadcrumbs>", metadata, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
