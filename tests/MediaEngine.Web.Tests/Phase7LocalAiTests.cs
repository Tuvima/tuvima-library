namespace MediaEngine.Web.Tests;

public sealed class Phase7LocalAiTests
{
    [Fact]
    public void LocalAiOverview_RendersRealHealthAndResources()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/LocalAiSettingsTab.razor");

        var runtime = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/LocalAiRuntimePanel.razor");

        // The overview keeps AI settings only; the runtime (profile, limits, diagnostics) is the Runtime page of Local AI.
        Assert.DoesNotContain("GetAiProfileAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetResourceSnapshotAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetAiModelStatusesAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetAiProfileAsync", runtime, StringComparison.Ordinal);
        Assert.Contains("GetResourceSnapshotAsync", runtime, StringComparison.Ordinal);
        // Whether Local AI can run is told by the permanent page notice from the shared attention state, not by an alert here.
        Assert.DoesNotContain("_status?.IsReady", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Local AI is partially available", source, StringComparison.Ordinal);
        // The unread "max at once" setting is gone; the engine room owns the queue.
        Assert.DoesNotContain("MaxConcurrentInferences", runtime, StringComparison.Ordinal);
        Assert.Contains("MinimumFreeDiskMB", runtime, StringComparison.Ordinal);
        Assert.Contains("CpuPressureLabel(double pressure)", runtime, StringComparison.Ordinal);
        Assert.Contains("switch (NormalizedSubsection)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelsTab_RendersEngineModelStatusesAndLifecycleActions()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/ModelsTab.razor");

        Assert.Contains("RunBenchmarkAsync", source, StringComparison.Ordinal);
        Assert.Contains("InvalidateBenchmarkAsync", source, StringComparison.Ordinal);
        Assert.Contains("Qwen3 1.7B Q5", source, StringComparison.Ordinal);
        Assert.Contains("Optional Whisper feature pack", source, StringComparison.Ordinal);
        Assert.Contains("result.Problem?.ToUserMessage()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", source, StringComparison.Ordinal);

        // Model files, downloads and loading moved to Settings > Tools and requirements.
        Assert.DoesNotContain("GetAiModelStatusesAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CancelAiModelDownloadAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadAiModelAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UnloadAiModelAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelsAndHardwareTab_RendersEngineModelStatusesAndLifecycleActions()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/LocalAiModelFilesPanel.razor");

        Assert.Contains("GetToolRequirementsAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetAiModelStatusesAsync", source, StringComparison.Ordinal);
        Assert.Contains("StartAiModelDownloadAsync", source, StringComparison.Ordinal);
        Assert.Contains("CancelAiModelDownloadAsync", source, StringComparison.Ordinal);
        Assert.Contains("LoadAiModelAsync", source, StringComparison.Ordinal);
        Assert.Contains("UnloadAiModelAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvancedSettings_UseTheSharedUrlBackedSubsectionNavigationOnly()
    {
        var localAi = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/LocalAiSettingsTab.razor");
        var plugins = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/PluginSettingsTab.razor");

        Assert.Contains("NormalizedSubsection", localAi, StringComparison.Ordinal);
        // Plugins is one page: installed plugins, then Available plugins, then the advanced sections.
        Assert.DoesNotContain("NormalizedSubsection", plugins, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppTabs", localAi, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppTabs", plugins, StringComparison.Ordinal);
        Assert.DoesNotContain("<AvailablePluginsPanel", plugins, StringComparison.Ordinal);
        Assert.Contains("PluginHealthJobsPanel(plugin)", plugins, StringComparison.Ordinal);
        Assert.Contains("PluginCapabilitiesPanel(plugin)", plugins, StringComparison.Ordinal);
        Assert.Contains("DangerPanel(plugin)", plugins, StringComparison.Ordinal);
        Assert.DoesNotContain("marketplace flows are planned", plugins, StringComparison.Ordinal);
        Assert.DoesNotContain("Approved Catalog", plugins, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalAiOverview_SurfacesUsefulControlsAndLinksTechnicalDetails()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/LocalAiSettingsTab.razor");

        Assert.Contains("DeviceContext.IsMobile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Local AI is partially available", source, StringComparison.Ordinal);
        // Every AI feature is a plugin: Status points at the Plugins page and holds no feature list or switches.
        Assert.Contains("AI features are plugins", source, StringComparison.Ordinal);
        Assert.Contains("/settings/plugins", source, StringComparison.Ordinal);
        Assert.DoesNotContain("tuvima.ai-vibe-tags", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Save features", source, StringComparison.Ordinal);
        // Budget and Usage are their own pages, as well as Model and hardware.
        Assert.Contains("LocalAiBudgetPanel", source, StringComparison.Ordinal);
        Assert.Contains("LocalAiUsagePanel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/settings/ai/vocabulary", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/settings/ai/automation", source, StringComparison.Ordinal);
        Assert.Contains("/settings/ai/models", source, StringComparison.Ordinal);
        Assert.Contains("OnParametersSetAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginOverview_UsesInstalledTable_AndItsSubPagesAreMenuRowsNotLinkCards()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Web/Components/Settings/PluginSettingsTab.razor");

        Assert.Contains("@PluginsListPage()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@AdvancedPluginLinks()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/settings/plugins/catalog", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsAdvancedLinkRow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@PluginManager()", source, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath) =>
        LocalizedSource.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            relativePath)));
}
