using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Server pages are regrouped by the question the administrator is asking (Network, Streaming, Tools and
/// requirements, Local AI, Plugins). Nothing a page could do is removed: every panel that moved is listed here with
/// where it used to be and where it is now, and each is checked against the pages' source and the menu.
/// </summary>
public sealed class ServerPageRegroupTests
{
    private const string Settings = "src/MediaEngine.Web/Components/Settings/";

    /// <summary>One moved panel: what it is called, its old and new address, and the markup that proves each side.</summary>
    public sealed record MovedPanel(
        string Panel,
        string OldLocation,
        string NewLocation,
        SettingsSection NewSection,
        string? NewSubsection,
        string NewComponent,
        string NewMarker,
        string? OldComponent,
        string? OldMarker)
    {
        public override string ToString() => $"{Panel}: {OldLocation} -> {NewLocation}";
    }

    public static TheoryData<MovedPanel> MovedPanels =>
    [
        new("Remote Streaming Quality", "/settings/network/streaming", "/settings/delivery", SettingsSection.Delivery, null,
            "StreamingLimitsPanel.razor", "Remote Streaming Quality", "NetworkRemoteAccessSettings.razor", "StreamingLimits"),
        new("Upload Capacity and the bandwidth test", "/settings/network/streaming", "/settings/delivery", SettingsSection.Delivery, null,
            "StreamingLimitsPanel.razor", "TestNetworkBandwidthAsync", "NetworkRemoteAccessSettings.razor", "StreamingLimits"),
        new("Concurrent Remote Streaming", "/settings/network/streaming", "/settings/delivery", SettingsSection.Delivery, null,
            "StreamingLimitsPanel.razor", "Concurrent Remote Streaming", "NetworkRemoteAccessSettings.razor", "StreamingLimits"),
        new("Hardware acceleration setting", "/settings/delivery (Delivery settings) and /settings/network/streaming (read-only)",
            "/settings/delivery/hardware", SettingsSection.Delivery, "hardware",
            "EncodeSettingsTab.razor", "Label=\"Hardware acceleration\"", "PlaybackDeliverySettingsTab.razor", "Hardware acceleration"),
        new("Prepared download quality and maximum active encodes", "/settings/delivery (Delivery settings)",
            "/settings/delivery/optimized-copies", SettingsSection.Delivery, "optimized-copies",
            "EncodeSettingsTab.razor", "Label=\"Maximum active encodes\"", "PlaybackDeliverySettingsTab.razor", "Maximum active encodes"),
        new("Scheduling", "/settings/delivery/scheduling", "/settings/delivery/optimized-copies", SettingsSection.Delivery, "optimized-copies",
            "EncodeSettingsTab.razor", "Title=\"Scheduling\"", null, null),
        new("Optimized copy storage", "/settings/delivery/storage", "/settings/delivery/optimized-copies", SettingsSection.Delivery, "optimized-copies",
            "EncodeSettingsTab.razor", "Title=\"Optimized copy storage\"", null, null),
        new("Active jobs", "/settings/delivery/active-jobs", "/settings/delivery/optimized-copies", SettingsSection.Delivery, "optimized-copies",
            "EncodeSettingsTab.razor", "<OfflineDownloadsTab />", "PlaybackDeliverySettingsTab.razor", "OfflineDownloadsTab"),
        new("Playback tools and services (was Current status)", "/settings/delivery", "/settings/tools", SettingsSection.Tools, null,
            "PlaybackToolsStatusCard.razor", "Title=\"Playback tools and services\"", "PlaybackDeliverySettingsTab.razor", "Current status"),
        // The runtime stays on Local AI > Runtime until Developer tools rebuilds Local AI; it is not on Tools and requirements.
        new("Resource profile and benchmark", "/settings/tools", "/settings/ai/models", SettingsSection.LocalAi, "models",
            "LocalAiRuntimePanel.razor", "<ModelsTab />", "ToolsRequirementsTab.razor", "LocalAiRuntimePanel"),
        new("Runtime settings", "/settings/tools", "/settings/ai/models", SettingsSection.LocalAi, "models",
            "LocalAiRuntimePanel.razor", "Title=\"Runtime settings\"", "ToolsRequirementsTab.razor", "LocalAiRuntimePanel"),
        new("Runtime diagnostics", "/settings/tools", "/settings/ai/models", SettingsSection.LocalAi, "models",
            "LocalAiRuntimePanel.razor", "Title=\"Runtime diagnostics\"", "ToolsRequirementsTab.razor", "LocalAiRuntimePanel"),
        new("AI features", "/settings/ai/models", "/settings/ai", SettingsSection.LocalAi, null,
            "LocalAiSettingsTab.razor", "Title=\"AI features are plugins\"", null, null),
        new("Plugin health and jobs", "/settings/plugins/jobs-health", "/settings/plugins", SettingsSection.Plugins, null,
            "PluginSettingsTab.razor", "PluginHealthJobsPanel(plugin)", null, null),
        new("Available plugins", "/settings/plugins/catalog", "/settings/plugins", SettingsSection.Plugins, null,
            "PluginSettingsTab.razor", "<AvailablePluginsPanel OnChanged=\"ReloadAsync\" />", null, null),
        new("Plugin capabilities", "/settings/plugins/capabilities", "/settings/plugins", SettingsSection.Plugins, null,
            "PluginSettingsTab.razor", "PluginCapabilitiesPanel(plugin)", null, null),
        new("Plugin danger zone", "/settings/plugins/danger", "/settings/plugins", SettingsSection.Plugins, null,
            "PluginSettingsTab.razor", "DangerPanel(plugin)", null, null),
    ];

    [Theory]
    [MemberData(nameof(MovedPanels))]
    public void EveryMovedPanel_IsAtItsNewPlace_AndNoLongerAtItsOldOne(MovedPanel moved)
    {
        // The new address is a real page in the menu.
        var location = SettingsNav.FindMenuLocation(moved.NewSection, moved.NewSubsection);
        Assert.NotNull(location);
        Assert.Equal(moved.NewLocation, location!.Node.Route);

        // The panel is rendered by the page that now owns it, and not by the page it left.
        Assert.Contains(moved.NewMarker, Read(moved.NewComponent), StringComparison.Ordinal);
        if (moved.OldComponent is not null && moved.OldMarker is not null)
        {
            Assert.DoesNotContain(moved.OldMarker, Read(moved.OldComponent), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(SettingsSection.Network, "streaming")]
    [InlineData(SettingsSection.Delivery, "scheduling")]
    [InlineData(SettingsSection.Delivery, "storage")]
    [InlineData(SettingsSection.Delivery, "active-jobs")]
    [InlineData(SettingsSection.Plugins, "jobs-health")]
    [InlineData(SettingsSection.Plugins, "catalog")]
    [InlineData(SettingsSection.Plugins, "capabilities")]
    [InlineData(SettingsSection.Plugins, "danger")]
    public void TheRetiredServerSubPages_AreNotInTheMenuAndHaveNoRoute(SettingsSection section, string slug)
    {
        Assert.Null(SettingsNav.ResolveSubsection(section, slug));
        var rows = SettingsNav.Flatten(SettingsNav.MenuGroups.SelectMany(group => group.Nodes));
        Assert.DoesNotContain(rows, node => node.Section == section && string.Equals(node.Subsection, slug, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StreamingIsListedOnceInTheMenu_AndNetworkOpensOnConnectionStatus()
    {
        var rows = SettingsNav.Flatten(SettingsNav.MenuGroups.SelectMany(group => group.Nodes)).ToArray();

        Assert.Single(rows, node => node.Label == "Streaming");
        Assert.Equal(["Local Network", "Remote Access", "Advanced"],
            rows.Single(node => node is { Section: SettingsSection.Network, Subsection: null }).Children.Select(node => node.Label));
        Assert.Equal(["Hardware Acceleration", "Optimized Copies", "Diagnostics"],
            rows.Single(node => node is { Section: SettingsSection.Delivery, Subsection: null }).Children.Select(node => node.Label));
        Assert.Equal(["Status", "Model and hardware", "Budget", "Usage"],
            rows.Single(node => node is { Section: SettingsSection.LocalAi, Subsection: null }).Children.Select(node => node.Label));
        Assert.Empty(rows.Single(node => node is { Section: SettingsSection.Plugins, Subsection: null }).Children);
    }

    [Fact]
    public void UserFacingServerPages_SayOptimizedCopies_NotVariants()
    {
        foreach (var file in new[] { "EncodeSettingsTab.razor", "OfflineDownloadsTab.razor", "PlaybackDeliverySettingsTab.razor", "StreamingLimitsPanel.razor" })
        {
            // Field names such as _variantRoot are code; the words people read must not say "variant".
            Assert.DoesNotMatch(@"[Vv]ariants?", Read(file));
        }
    }

    private static string Read(string componentFile) => File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", Settings + componentFile)));
}
