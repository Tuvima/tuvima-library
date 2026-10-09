namespace MediaEngine.Web.Tests;

public sealed class Phase7LocalAiTests
{
    [Fact]
    public void LocalAiOverview_RendersRealHealthAndResources()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/LocalAiSettingsTab.razor");

        Assert.Contains("GetAiStatusAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetAiProfileAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetResourceSnapshotAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetEnrichmentProgressAsync", source, StringComparison.Ordinal);
        Assert.Contains("_status?.IsReady != true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Local AI is partially available", source, StringComparison.Ordinal);
        Assert.Contains("MaxConcurrentInferences", source, StringComparison.Ordinal);
        Assert.Contains("MinimumFreeDiskMB", source, StringComparison.Ordinal);
        Assert.Contains("CpuPressureLabel(double pressure)", source, StringComparison.Ordinal);
        Assert.Contains("switch (NormalizedSubsection)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelsTab_RendersEngineModelStatusesAndLifecycleActions()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/ModelsTab.razor");

        Assert.Contains("GetAiModelStatusesAsync", source, StringComparison.Ordinal);
        Assert.Contains("StartAiModelDownloadAsync", source, StringComparison.Ordinal);
        Assert.Contains("CancelAiModelDownloadAsync", source, StringComparison.Ordinal);
        Assert.Contains("LoadAiModelAsync", source, StringComparison.Ordinal);
        Assert.Contains("UnloadAiModelAsync", source, StringComparison.Ordinal);
        Assert.Contains("RunBenchmarkAsync", source, StringComparison.Ordinal);
        Assert.Contains("InvalidateBenchmarkAsync", source, StringComparison.Ordinal);
        Assert.Contains("Qwen3 1.7B Q5", source, StringComparison.Ordinal);
        Assert.Contains("Optional Whisper feature pack", source, StringComparison.Ordinal);
        Assert.Contains("result.Problem?.ToUserMessage()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AiFeatures_LoadFromConfigAndExplainDependencies()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/AiFeaturesTab.razor");

        Assert.Contains("GetAiConfigAsync", source, StringComparison.Ordinal);
        Assert.Contains("SaveAiConfigAsync", source, StringComparison.Ordinal);
        Assert.Contains("Missing model", source, StringComparison.Ordinal);
        Assert.Contains("Local AI is partial", source, StringComparison.Ordinal);
        Assert.Contains("Media-type assistance", source, StringComparison.Ordinal);
        Assert.Contains("Series alignment", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Audiobook chapter naming", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AudiobookChapterNamingAutomation_IsRemoved()
    {
        var shell = ReadRepoFile(@"src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadRepoFile(@"src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");

        Assert.DoesNotContain("Suggest chapter names", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("SuggestAudiobookChapterNamesAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaybackChapterTitleSources.AiSuggested", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduleAndVocabulary_LoadAndSaveAiConfig()
    {
        var schedule = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/AiScheduleTab.razor");
        var vocabulary = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/VibeVocabularyTab.razor");

        Assert.Contains("GetAiConfigAsync", schedule, StringComparison.Ordinal);
        Assert.Contains("SaveAiConfigAsync", schedule, StringComparison.Ordinal);
        Assert.Contains("Saving wakes waiting workers", schedule, StringComparison.Ordinal);
        Assert.Contains("description_intelligence_cron", schedule, StringComparison.Ordinal);
        Assert.Contains("GetAiConfigAsync", vocabulary, StringComparison.Ordinal);
        Assert.Contains("SaveAiConfigAsync", vocabulary, StringComparison.Ordinal);
        Assert.Contains("Duplicate tag", vocabulary, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvancedSettings_UseTheSharedUrlBackedSubsectionNavigationOnly()
    {
        var localAi = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/LocalAiSettingsTab.razor");
        var plugins = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/PluginSettingsTab.razor");

        Assert.Contains("NormalizedSubsection", localAi, StringComparison.Ordinal);
        Assert.Contains("NormalizedSubsection", plugins, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppTabs", localAi, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppTabs", plugins, StringComparison.Ordinal);
        Assert.Contains("PluginSubsection(plugin)", plugins, StringComparison.Ordinal);
        Assert.Contains("Install and update marketplace flows are planned", plugins, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalAiOverview_SurfacesUsefulControlsAndLinksTechnicalDetails()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/LocalAiSettingsTab.razor");

        Assert.Contains("DeviceContext.IsMobile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Local AI is partially available", source, StringComparison.Ordinal);
        Assert.Contains("Enabled features", source, StringComparison.Ordinal);
        Assert.Contains("Media-type assistance", source, StringComparison.Ordinal);
        Assert.Contains("/settings/ai/models", source, StringComparison.Ordinal);
        Assert.Contains("/settings/ai/vocabulary", source, StringComparison.Ordinal);
        Assert.Contains("/settings/ai/automation", source, StringComparison.Ordinal);
        Assert.Contains("OnParametersSetAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginOverview_UsesInstalledTableAndLinksAdvancedOperations()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/PluginSettingsTab.razor");

        Assert.Contains("@InstalledPluginsPanel()", source, StringComparison.Ordinal);
        Assert.Contains("@AdvancedPluginLinks()", source, StringComparison.Ordinal);
        Assert.Contains("/settings/plugins/jobs-health", source, StringComparison.Ordinal);
        Assert.Contains("/settings/plugins/catalog", source, StringComparison.Ordinal);
        Assert.Contains("/settings/plugins/capabilities", source, StringComparison.Ordinal);
        Assert.Contains("/settings/plugins/danger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@PluginManager()", source, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            relativePath)));
}
