using System.IO;

namespace MediaEngine.Web.Tests;

public sealed class MetadataSettingsCompositionTests
{
    [Fact]
    public void MetadataSettings_ComposesOnlyTheIngestionFlow_AndSourcesAreOnThePluginsPage()
    {
        var page = Read("src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");
        var nav = Read("src/MediaEngine.Web/Models/ViewDTOs/SettingsNav.cs");

        Assert.Contains("IngestionFlowEditor", page, StringComparison.Ordinal);
        Assert.DoesNotContain("ProvidersPage()", page, StringComparison.Ordinal);
        Assert.DoesNotContain("ProviderDetailPage()", page, StringComparison.Ordinal);
        Assert.DoesNotContain("\"providers\", \"Providers\"", nav, StringComparison.Ordinal);
        Assert.Contains("IngestionFlowSubsection, \"Ingestion Flow\"", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("\"enrichment\", \"Enrichment\"", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("\"canonical\", \"Canonical & Universes\"", nav, StringComparison.Ordinal);
    }

    [Fact]
    public void Providers_AreListedAsPluginTilesByTheEngine()
    {
        var state = Read("src/MediaEngine.Web/Services/Integration/MetadataSettingsStateService.cs");
        var listings = Read("src/MediaEngine.Api/Services/Plugins/Sources/ProviderListings.cs");
        var tab = Read("src/MediaEngine.Web/Components/Settings/PluginSettingsTab.razor");

        Assert.Contains("Where(IsUserVisibleProvider)", state, StringComparison.Ordinal);
        Assert.DoesNotContain("ContextualProviderName", state, StringComparison.Ordinal);
        Assert.Contains("ListingKind = \"provider\"", listings, StringComparison.Ordinal);
        Assert.Contains("IsProvider(plugin)", tab, StringComparison.Ordinal);
    }

    [Fact]
    public void IngestionFlow_DerivesRolesStagesAndOutputsFromLiveConfiguration()
    {
        var page = Read("src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");
        var state = Read("src/MediaEngine.Web/Services/Integration/MetadataSettingsStateService.cs");

        Assert.Contains("GetPipelinesAsync", state, StringComparison.Ordinal);
        Assert.Contains("GetProviderStatusAsync", state, StringComparison.Ordinal);
        Assert.Contains("GetHydrationSettingsAsync", state, StringComparison.Ordinal);
        Assert.Contains("RoleLabel(entry, index)", state, StringComparison.Ordinal);
        Assert.Contains("\"Primary\"", state, StringComparison.Ordinal);
        Assert.Contains("\"Secondary\"", state, StringComparison.Ordinal);
        Assert.Contains("\"Fallback\"", state, StringComparison.Ordinal);
        Assert.Contains("catalogue.RequiredSystemProvider ? \"Required\" : \"Optional\"", state, StringComparison.Ordinal);
        var editor = Read("src/MediaEngine.Web/Components/Settings/IngestionFlowEditor.razor");
        Assert.Contains("IngestionFlowEditor", page, StringComparison.Ordinal);
        Assert.Contains("GetPipelinesAsync", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderConfiguration_ProtectsSecretsAndRequiredProviders()
    {
        var page = Read("src/MediaEngine.Web/Components/Settings/Plugins/ProviderDetail.razor");
        var dialog = Read("src/MediaEngine.Web/Components/Shared/Providers/ProviderOnboardingDialog.razor");
        var form = Read("src/MediaEngine.Web/Components/Shared/Providers/ProviderCredentialForm.razor");

        Assert.Contains("Replace key", page, StringComparison.Ordinal);
        Assert.Contains("Remove key", page, StringComparison.Ordinal);
        Assert.DoesNotContain("CredentialReplacements", page, StringComparison.Ordinal);
        Assert.Contains("The stored value is encrypted and is never returned", form, StringComparison.Ordinal);
        Assert.Contains("field.InputType, \"password\"", form, StringComparison.Ordinal);
        Assert.Contains("AppInputType.Password", form, StringComparison.Ordinal);
        Assert.Contains("AppHostedDialog", dialog, StringComparison.Ordinal);
        Assert.Contains("TestProviderAsync", page, StringComparison.Ordinal);
        Assert.Contains("TestProviderCredentialsAsync", dialog, StringComparison.Ordinal);
        Assert.Contains("SaveProviderCredentialsAsync", dialog, StringComparison.Ordinal);
        Assert.Contains("SaveProviderConfigAsync", page, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateHydrationSettingsAsync", page, StringComparison.Ordinal);
    }

    [Fact]
    public void EnrichmentRefreshSchedule_DoesNotCompeteWithMediaFirstIngestionPage()
    {
        var metadata = Read("src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");
        var ingestion = Read("src/MediaEngine.Web/Components/Settings/IngestionTasksTab.razor");
        var schedule = Read("src/MediaEngine.Web/Components/Settings/EnrichmentRefreshSchedulePanel.razor");

        Assert.DoesNotContain("<EnrichmentRefreshSchedulePanel", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("<EnrichmentRefreshSchedulePanel", ingestion, StringComparison.Ordinal);
        Assert.Contains("GetEnrichmentRefreshScheduleAsync", schedule, StringComparison.Ordinal);
        Assert.Contains("QueueEnrichmentRefreshNowAsync", schedule, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryOverview_UsesTheSameConfiguredFlowSnapshot()
    {
        var summary = Read("src/MediaEngine.Web/Components/Settings/MetadataPipelineStrip.razor");

        Assert.Contains("_pipeline.Identification", summary, StringComparison.Ordinal);
        Assert.Contains("_pipeline.Enrichment", summary, StringComparison.Ordinal);
        Assert.Contains("/settings/metadata/ingestion-flow", summary, StringComparison.Ordinal);
        Assert.Contains("Local metadata only", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovedMetadataWorkspaceComponents_RemainRetired()
    {
        Assert.False(File.Exists(PathFor("src/MediaEngine.Web/Components/Settings/ProviderPriorityTab.razor")));
        Assert.False(File.Exists(PathFor("src/MediaEngine.Web/Components/Settings/ProviderPrioritySurface.razor")));
        Assert.False(File.Exists(PathFor("src/MediaEngine.Web/Components/Settings/ProviderHealthSurface.razor")));
    }

    private static string Read(string relativePath) => LocalizedSource.ReadAllText(PathFor(relativePath));

    private static string PathFor(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
