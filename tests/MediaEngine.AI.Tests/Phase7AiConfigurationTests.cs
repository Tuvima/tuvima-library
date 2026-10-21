using System.Text.Json;
using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.AI.Tests;

public sealed class Phase7AiConfigurationTests
{
    [Fact]
    public void Config_RoundTripsProfilesFeaturesAndSchedulesWithoutRawModelsOrHardware()
    {
        var settings = new AiSettings
        {
            ResourceProfile = AiResourceProfileNames.Standard,
            AudioPackEnabled = false,
        };
        var json = JsonSerializer.Serialize(settings);
        var roundTrip = JsonSerializer.Deserialize<AiSettings>(json);

        Assert.NotNull(roundTrip);
        Assert.Equal(AiResourceProfileNames.Standard, roundTrip.ResourceProfile);
        Assert.DoesNotContain("\"features\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"scheduling\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("vibe_vocabulary", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"models\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hardware_profile", json, StringComparison.Ordinal);
        Assert.DoesNotContain("model_catalog", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AiResourceProfileNames.Essential, AiResourceProfileCatalog.EssentialCatalogKey)]
    [InlineData(AiResourceProfileNames.Standard, AiResourceProfileCatalog.StandardCatalogKey)]
    public void EveryTextWorkload_UsesOneProfileArtifact(string profile, string catalogKey)
    {
        var settings = new AiSettings { ResourceProfile = profile };
        var artifacts = new[]
        {
            settings.Models.TextFast,
            settings.Models.TextQuality,
            settings.Models.TextScholar,
            settings.Models.TextCjk,
        };

        Assert.All(artifacts, model => Assert.Equal(catalogKey, model.CatalogKey));
        Assert.Single(artifacts.Select(model => model.File).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void SupportedCatalogue_ContainsOnlyLaunchArtifacts()
    {
        var catalog = AiModelCatalogDefaults.CreateCatalog();

        Assert.Equal(4, catalog.Count);
        Assert.Equal(
            new[]
            {
                AiResourceProfileCatalog.AdvancedCatalogKey,
                AiResourceProfileCatalog.EssentialCatalogKey,
                AiResourceProfileCatalog.StandardCatalogKey,
                AiResourceProfileCatalog.AudioCatalogKey,
            }.Order(),
            catalog.Keys.Order());
        Assert.All(catalog.Values, entry =>
        {
            // A catalogue entry knows only that its definition is complete; the runtime and the model file are facts about
            // this machine, read live by the advisor, so they are never claimed here.
            Assert.True(entry.Readiness.ConfigurationReady);
            Assert.False(entry.Readiness.RuntimeReady);
            Assert.False(entry.Readiness.Validated);
            Assert.False(string.IsNullOrWhiteSpace(entry.Sha256));
            Assert.False(string.IsNullOrWhiteSpace(entry.DownloadUrl));
        });
    }

    [Fact]
    public void Advanced_RemainsBlockedUntilSuccessfulCurrentMachineBenchmark()
    {
        var settings = new AiSettings { ResourceProfile = AiResourceProfileNames.Advanced };
        settings.HardwareProfile.AvailableRamMb = 16_384;
        var advisor = new AiModelSelectionAdvisor(settings);

        Assert.False(advisor.GetExecutionPlan().ConfiguredProfileEligible);
        Assert.Equal(AiResourceProfileNames.Standard, advisor.GetExecutionPlan().EffectiveProfile);

        settings.HardwareProfile.Outcome = AiBenchmarkOutcomes.Succeeded;
        settings.HardwareProfile.TokensPerSecond = 20;
        settings.HardwareProfile.AvailableRamMb = 16_384;

        Assert.True(advisor.GetExecutionPlan().ConfiguredProfileEligible);
        Assert.Equal(AiResourceProfileNames.Advanced, advisor.GetExecutionPlan().EffectiveProfile);
    }

    [Fact]
    public void Advisor_DowngradesConstrainedMachineToEssential()
    {
        var settings = new AiSettings { ResourceProfile = AiResourceProfileNames.Standard };
        settings.HardwareProfile.AvailableRamMb = 4096;
        settings.ApplyEffectiveResourceProfile();

        var plan = new AiModelSelectionAdvisor(settings).GetExecutionPlan();

        Assert.Equal(AiResourceProfileNames.Essential, plan.RecommendedProfile);
        Assert.Equal(AiResourceProfileNames.Essential, plan.EffectiveProfile);
        Assert.Equal(AiResourceProfileCatalog.EssentialCatalogKey, settings.Models.TextQuality.CatalogKey);
    }

    [Fact]
    public void FeatureGate_StopsDisabledOrMissingModelWork()
    {
        var settings = new AiSettings
        {
            ModelsDirectory = Path.Combine(Path.GetTempPath(), $"tuvima-gate-{Guid.NewGuid():N}"),
        };
        var switches = new FakeAiFeatureSwitches();
        var inventory = new ModelInventory(settings, NullLogger<ModelInventory>.Instance);
        inventory.SetState(AiModelRole.TextQuality, AiModelState.Ready);
        var gate = new AiFeatureGate(
            switches,
            inventory,
            new AiModelSelectionAdvisor(settings, inventory, new FixedRuntimeStatus(textLoads: true)));

        Assert.False(gate.CanExecute(AiFeature.VibeTags, AiModelRole.TextQuality));

        switches.Set(AiFeature.VibeTags, true);
        inventory.SetState(AiModelRole.TextQuality, AiModelState.NotDownloaded);
        Assert.False(gate.CanExecute(AiFeature.VibeTags, AiModelRole.TextQuality));
    }
}
