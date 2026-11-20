namespace MediaEngine.Web.Tests;

public sealed class SettingsBreadcrumbGuardrailTests
{
    [Fact]
    public void SettingsShell_OwnsTheBackLinkAndIngestionHeaderAction()
    {
        var settings = Read(@"src/MediaEngine.Web/Components/Pages/Settings.razor");
        var frame = Read(@"src/MediaEngine.Web/Components/Settings/Frame/SettingsPageFrame.razor");
        var frameCss = Read(@"src/MediaEngine.Web/Components/Settings/Frame/SettingsPageFrame.razor.css");
        var metadata = Read(@"src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");

        // settings-alignment D1: no breadcrumb on any width; a page below a page keeps one back link.
        Assert.DoesNotContain("<AppBreadcrumbs", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppBreadcrumbs", settings, StringComparison.Ordinal);
        Assert.Contains("settings-frame__back", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("runId", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("IngestionBatchDisplay", settings, StringComparison.Ordinal);
        Assert.Contains("SettingsSection.LibraryRules => \"The rules every library follows", settings, StringComparison.Ordinal);
        Assert.Contains("<LibraryRulesPage", settings, StringComparison.Ordinal);
        Assert.Contains("<LibraryActivityPage", Read("src/MediaEngine.Web/Components/Settings/General/GeneralPage.razor"), StringComparison.Ordinal);
        Assert.Contains("focus-visible", frameCss, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppBreadcrumbs>", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsBackLink_KeepsA44PixelTapTargetInTheSharedBlock()
    {
        var css = Read(@"src/MediaEngine.Web/wwwroot/app.css");
        var block = css[css.IndexOf("Shared 44px tap-target contract", StringComparison.Ordinal)..];

        Assert.Contains(":root:root .settings-frame__back,", block, StringComparison.Ordinal);
        Assert.DoesNotContain("settings-breadcrumbs", css, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
