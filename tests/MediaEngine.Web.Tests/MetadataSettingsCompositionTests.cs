using System.IO;

namespace MediaEngine.Web.Tests;

public sealed class MetadataSettingsCompositionTests
{
    [Fact]
    public void MetadataSettings_ComposesOnlyProvidersAndIngestionFlow()
    {
        var page = Read("src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");
        var nav = Read("src/MediaEngine.Web/Models/ViewDTOs/SettingsNav.cs");

        Assert.Contains("ProvidersPage()", page, StringComparison.Ordinal);
        Assert.Contains("ProviderDetailPage()", page, StringComparison.Ordinal);
        Assert.Contains("IngestionFlowPage()", page, StringComparison.Ordinal);
        Assert.Contains("\"providers\", \"Providers\"", nav, StringComparison.Ordinal);
        Assert.Contains("\"ingestion-flow\", \"Ingestion Flow\"", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("\"enrichment\", \"Enrichment\"", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("\"canonical\", \"Canonical & Universes\"", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("EnrichmentOverview", page, StringComparison.Ordinal);
        Assert.DoesNotContain("CanonicalOverview", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Providers_AreOneProviderKeyInventoryWithTruthfulFilters()
    {
        var page = Read("src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");
        var state = Read("src/MediaEngine.Web/Services/Integration/MetadataSettingsStateService.cs");

        Assert.Contains("_snapshot!.Providers", page, StringComparison.Ordinal);
        Assert.Contains("Search providers", page, StringComparison.Ordinal);
        Assert.Contains("Needs setup", page, StringComparison.Ordinal);
        Assert.Contains("Connected", page, StringComparison.Ordinal);
        Assert.Contains("ProviderRoute(provider.Key)", page, StringComparison.Ordinal);
        Assert.Contains("Where(IsUserVisibleProvider)", state, StringComparison.Ordinal);
        Assert.DoesNotContain("ContextualProviderName", state, StringComparison.Ordinal);
        Assert.DoesNotContain("ProviderRoute(pipeline.MediaType)", page, StringComparison.Ordinal);
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
        Assert.Contains("flow.Results", page, StringComparison.Ordinal);
        Assert.Contains("Order and roles come from the active Engine configuration", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderConfiguration_ProtectsSecretsAndRequiredProviders()
    {
        var page = Read("src/MediaEngine.Web/Components/Settings/MetadataSettingsPage.razor");
        var dialog = Read("src/MediaEngine.Web/Components/Shared/Providers/ProviderOnboardingDialog.razor");
        var form = Read("src/MediaEngine.Web/Components/Shared/Providers/ProviderCredentialForm.razor");

        Assert.Contains("Provider enabled", page, StringComparison.Ordinal);
        Assert.Contains("Disabled=\"@provider.RequiredSystemProvider\"", page, StringComparison.Ordinal);
        Assert.Contains("Connect provider", page, StringComparison.Ordinal);
        Assert.Contains("Replace credential", page, StringComparison.Ordinal);
        Assert.Contains("Remove credential", page, StringComparison.Ordinal);
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
        var libraries = Read("src/MediaEngine.Web/Components/Settings/LibrariesTab.razor");
        var summary = Read("src/MediaEngine.Web/Components/Settings/MetadataPipelineStrip.razor");

        Assert.Contains("/settings/metadata/ingestion-flow", libraries, StringComparison.Ordinal);
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

    private static string Read(string relativePath) => File.ReadAllText(PathFor(relativePath));

    private static string PathFor(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
