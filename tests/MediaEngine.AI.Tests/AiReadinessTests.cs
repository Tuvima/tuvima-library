using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.AI.Tests;

/// <summary>Readiness is read from this machine (model file, native runtime), never assumed.</summary>
public sealed class AiReadinessTests : IDisposable
{
    private readonly string _modelsDirectory = Path.Combine(Path.GetTempPath(), $"tuvima-ready-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_modelsDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written for this test.
        }
        catch (IOException)
        {
            // Test cleanup is best-effort on platforms with delayed file-handle release.
        }
    }

    [Fact]
    public async Task Status_WithTheModelFileMissing_ReportsNotReadyEvenWhenTheRuntimeLoads()
    {
        var settings = Settings();
        var inventory = Inventory(settings);
        await using var lifecycle = Lifecycle(settings, inventory, new FixedRuntimeStatus(textLoads: true));

        Assert.False(lifecycle.GetHealthStatus().IsReady);
    }

    [Fact]
    public async Task Status_WithTheModelFilePresentButNoRuntime_ReportsNotReady()
    {
        var settings = Settings();
        PlaceTextModel(settings);
        var inventory = Inventory(settings);
        await using var lifecycle = Lifecycle(settings, inventory, new FixedRuntimeStatus(textLoads: false));

        Assert.False(lifecycle.GetHealthStatus().IsReady);
    }

    [Fact]
    public async Task Status_WithTheModelFileAndRuntime_ReportsReady()
    {
        var settings = Settings();
        PlaceTextModel(settings);
        var inventory = Inventory(settings);
        await using var lifecycle = Lifecycle(settings, inventory, new FixedRuntimeStatus(textLoads: true));

        Assert.True(lifecycle.GetHealthStatus().IsReady);
    }

    [Fact]
    public async Task Status_WithTheOptionalAudioPackEnabledButNotDownloaded_StillReportsReady()
    {
        var settings = Settings();
        settings.AudioPackEnabled = true;
        PlaceTextModel(settings);
        var inventory = Inventory(settings);
        await using var lifecycle = Lifecycle(settings, inventory, new FixedRuntimeStatus(textLoads: true));

        var status = lifecycle.GetHealthStatus();

        Assert.True(status.IsReady);
        Assert.Contains(status.Models, model => model.Role == AiModelRole.Audio && model.State == AiModelState.NotDownloaded);
    }

    [Fact]
    public void Advisor_ReadsTheRuntimeAndModelFileFromThisMachine()
    {
        var settings = Settings();
        var inventory = Inventory(settings);
        var runtime = new FixedRuntimeStatus(textLoads: false);
        var advisor = new AiModelSelectionAdvisor(settings, inventory, runtime);

        var before = advisor.GetDecision(AiModelRole.TextQuality);

        Assert.True(before.ConfigurationReady);
        Assert.False(before.RuntimeReady);
        Assert.False(before.Validated);

        PlaceTextModel(settings);
        inventory.Refresh();
        runtime.TextRuntimeLoads = true;
        var after = advisor.GetDecision(AiModelRole.TextQuality);

        Assert.True(after.ConfigurationReady);
        Assert.True(after.RuntimeReady);
        Assert.True(after.Validated);
    }

    [Fact]
    public void Advisor_WithoutLiveFacts_NeverClaimsTheRuntimeOrModelIsReady()
    {
        var decision = new AiModelSelectionAdvisor(Settings()).GetDecision(AiModelRole.TextQuality);

        Assert.True(decision.ConfigurationReady);
        Assert.False(decision.RuntimeReady);
        Assert.False(decision.Validated);
    }

    [Fact]
    public void Advisor_WithAnIncompleteDefinition_ReportsConfigurationNotReady()
    {
        var settings = Settings();
        settings.Models.TextQuality.DownloadUrl = "";
        var advisor = new AiModelSelectionAdvisor(settings, inventory: null, new FixedRuntimeStatus(textLoads: true));

        var decision = advisor.GetDecision(AiModelRole.TextQuality);

        Assert.False(decision.ConfigurationReady);
        Assert.False(decision.CanEnable);
    }

    [Fact]
    public void FeatureGate_StopsWorkWhenTheRuntimeDoesNotLoad_AndAllowsItWhenEverythingIsInPlace()
    {
        var settings = Settings();
        var switches = new FakeAiFeatureSwitches(AiFeature.VibeTags);
        PlaceTextModel(settings);
        var inventory = Inventory(settings);
        var runtime = new FixedRuntimeStatus(textLoads: false);
        var gate = new AiFeatureGate(switches, inventory, new AiModelSelectionAdvisor(settings, inventory, runtime));

        Assert.False(gate.CanExecute(AiFeature.VibeTags, AiModelRole.TextQuality));

        runtime.TextRuntimeLoads = true;

        Assert.True(gate.CanExecute(AiFeature.VibeTags, AiModelRole.TextQuality));
    }

    private AiSettings Settings()
    {
        var settings = new AiSettings { ModelsDirectory = _modelsDirectory };
        foreach (var definition in new[]
                 {
                     settings.Models.TextFast,
                     settings.Models.TextQuality,
                     settings.Models.TextScholar,
                     settings.Models.TextCjk,
                 })
        {
            definition.Sha256 = null; // The test model file is a few bytes, not the real weights.
        }

        return settings;
    }

    private static ModelInventory Inventory(AiSettings settings) =>
        new(settings, NullLogger<ModelInventory>.Instance);

    private void PlaceTextModel(AiSettings settings)
    {
        var path = Inventory(settings).GetModelPath(AiModelRole.TextQuality);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
    }

    private static ModelLifecycleManager Lifecycle(AiSettings settings, ModelInventory inventory, IAiRuntimeStatus runtime) =>
        new(settings, inventory, runtime, new NoOpPublisher(), NullLogger<ModelLifecycleManager>.Instance);

    private sealed class NoOpPublisher : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull => Task.CompletedTask;
    }
}
