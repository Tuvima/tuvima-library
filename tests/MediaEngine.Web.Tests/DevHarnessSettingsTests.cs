namespace MediaEngine.Web.Tests;

public sealed class DevHarnessSettingsTests
{
    [Fact]
    public void SettingsShell_RendersTemporaryDevHarnessTab()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Pages/Settings.razor");
        var nav = ReadRepoFile(@"src/MediaEngine.Web/Models/ViewDTOs/SettingsNav.cs");

        Assert.Contains("case SettingsSection.DevHarness", source, StringComparison.Ordinal);
        Assert.Contains("RenderInternalTool(", source, StringComparison.Ordinal);
        Assert.Contains("MediaEngine.Web.Components.Settings.DevHarnessTab", source, StringComparison.Ordinal);
        Assert.Contains("This internal tool is not included in this build.", source, StringComparison.Ordinal);
        Assert.Contains("dev-harness", nav, StringComparison.Ordinal);
        Assert.Contains("Developer Tools", nav, StringComparison.Ordinal);
    }

    [Fact]
    public void DevHarnessTab_ExposesIntentBasedResetSeedAndRescanWorkflow()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/DevHarnessTab.razor");
        var client = ReadEngineApiClientSources("IEngineApiClient*.cs");
        var implementation = ReadEngineApiClientSources("EngineApiClient*.cs");

        Assert.Contains("/dev/reset-and-seed", source, StringComparison.Ordinal);
        Assert.Contains("/dev/reset-library-data", source, StringComparison.Ordinal);
        Assert.Contains("/dev/factory-reset", source, StringComparison.Ordinal);
        Assert.Contains("/dev/view-photo-harness", source, StringComparison.Ordinal);
        Assert.Contains("TriggerRescanAsync", source, StringComparison.Ordinal);
        Assert.Contains("Reset & Seed Test Library", source, StringComparison.Ordinal);
        Assert.Contains("Rescan All Libraries", source, StringComparison.Ordinal);
        Assert.Contains("Seed View Photos", source, StringComparison.Ordinal);
        Assert.Contains("2004–2026", source, StringComparison.Ordinal);
        Assert.Contains("360 labeled synthetic images", source, StringComparison.Ordinal);
        Assert.Contains("Chicago/Naperville", source, StringComparison.Ordinal);
        Assert.Contains("Standard", source, StringComparison.Ordinal);
        Assert.Contains("Stress", source, StringComparison.Ordinal);
        Assert.Contains("types", source, StringComparison.Ordinal);

        foreach (var mediaType in new[] { "books", "audiobooks", "movies", "tv", "music", "comics" })
        {
            Assert.Contains(mediaType, source, StringComparison.Ordinal);
        }

        Assert.Contains("RunDevHarnessAsync", client, StringComparison.Ordinal);
        Assert.Contains("RunDevHarnessAsync", implementation, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaEngine.Storage", source, StringComparison.Ordinal);
        Assert.Contains("/settings/ingestion", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/settings/media-management", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Integration depth", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stage 1", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Test Harness", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Provider Tester", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Enrichment Tester", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Last Result", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Allow full source wipe", source, StringComparison.Ordinal);
        Assert.DoesNotContain("dev-harness-table", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RealMediaControlsKeepDedicatedActionColumnsAcrossBreakpoints()
    {
        var css = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/DevHarnessTab.razor.css");
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/DevHarnessTab.razor");
        Assert.Contains("grid-template-columns: minmax(0, 1fr) auto auto", css);
        Assert.Contains("align-items: center", css);
        Assert.Contains("max-width: 960px", css);
        Assert.Contains("grid-column: 1 / -1", css);
        Assert.Contains("max-width: 600px", css);
        Assert.Contains("/dev/real-media/status", source);
        Assert.Contains("/dev/real-media/verify", source);
        Assert.Contains("_realMediaActive || !_modeKnown", source);
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));

    private static string ReadEngineApiClientSources(string pattern)
    {
        var directory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "MediaEngine.Web", "Services", "Integration"));

        return string.Join(
            "\n",
            Directory.EnumerateFiles(directory, pattern)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));
    }
}
