using System.Text.Json;
using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.AiFeatures;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Every Local AI feature is an Official plugin: it is on exactly when its plugin is on, its scheduled passes are plugin jobs,
/// and the words vibe tags may use live in the Vibe tags plugin's settings.
/// </summary>
public sealed class AiFeaturePluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-ai-feature-plugins", Guid.NewGuid().ToString("N"));
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly ServiceProvider _services;
    private readonly PluginCatalog _catalog;
    private readonly PluginAiFeatureSwitches _switches;

    public AiFeaturePluginTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(libraryRoot);
        _loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });

        // The jobs look their runners up when they run, which these tests never do, so an empty container is enough.
        var empty = new ServiceCollection().BuildServiceProvider();
        _catalog = new PluginCatalog(
            Plugins(empty),
            new PluginSettingsService(_loader),
            _loader,
            new ApprovesEverything(),
            NullLogger<PluginCatalog>.Instance);
        _services = new ServiceCollection().AddSingleton(_catalog).BuildServiceProvider();
        _switches = new PluginAiFeatureSwitches(_services);
    }

    public void Dispose()
    {
        _services.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is left for the OS to clean up.
        }
    }

    [Fact]
    public void EveryAiFeaturePlugin_LoadsCleanly_AsAnOfficialBuiltIn_SwitchedOffUntilAnAdministratorTurnsItOn()
    {
        Assert.Equal(AiFeaturePluginIds.All.Order(StringComparer.Ordinal), _catalog.List().Select(plugin => plugin.Manifest.Id).Order(StringComparer.Ordinal));

        Assert.All(_catalog.List(), plugin =>
        {
            Assert.Null(plugin.LoadError);
            Assert.True(plugin.Compatibility.IsCompatible);
            Assert.True(plugin.IsBuiltIn);
            Assert.Equal(PluginKind.Official, plugin.Manifest.Kind);
            Assert.Equal(PluginTrustLevel.Official, plugin.Trust);
            Assert.False(plugin.Enabled);
        });
    }

    [Fact]
    public void EveryAiFeaturePlugin_AsksForLocalAiAndNothingElse_AndNamesARoleTheEngineRoomKnows()
    {
        Assert.All(_catalog.List(), plugin =>
        {
            Assert.Contains(PluginPermissionIds.AiInfer, plugin.Manifest.Permissions);
            Assert.DoesNotContain(PluginPermissionIds.NetworkHttp, plugin.Manifest.Permissions);
            Assert.DoesNotContain(PluginPermissionIds.MediaRead, plugin.Manifest.Permissions);
            var permission = Assert.Single(plugin.Manifest.AiPermissions);
            Assert.True(permission.MaxTokens > 0);
            _ = PluginAiClient.ResolveAbility(permission.Role);
        });
    }

    [Fact]
    public void TheTasteSummaryPlugin_AsksToBeToldWhatPeopleWatch_AndTheOthersDoNot()
    {
        Assert.Contains(PluginPermissionIds.WatchActivity, _catalog.Get(AiFeaturePluginIds.TasteSummary)!.Manifest.Permissions);
        Assert.All(
            AiFeaturePluginIds.All.Where(id => id != AiFeaturePluginIds.TasteSummary),
            id => Assert.DoesNotContain(PluginPermissionIds.WatchActivity, _catalog.Get(id)!.Manifest.Permissions));
    }

    [Fact]
    public void ThePluginsThatRunOnASchedule_AskToRunScheduledWork_AndDeclareTheirJobAsAValidOne()
    {
        foreach (var (id, jobs) in new[] { (AiFeaturePluginIds.SeriesOrder, 1), (AiFeaturePluginIds.DescriptionInsights, 2) })
        {
            var plugin = _catalog.Get(id)!;
            Assert.Contains(PluginPermissionIds.ScheduledWork, plugin.Manifest.Permissions);
            Assert.Equal(jobs, plugin.Capabilities.OfType<IPluginScheduledJob>().Count());
            Assert.Null(PluginHostCapabilityRules.Validate(plugin.Capabilities));
        }

        Assert.DoesNotContain(PluginPermissionIds.ScheduledWork, _catalog.Get(AiFeaturePluginIds.SmartFiling)!.Manifest.Permissions);
    }

    [Fact]
    public void TheJobs_KeepTheSchedulesTheOldSettingsGave()
    {
        Assert.Equal("0 3 * * *", Job(AiFeaturePluginIds.SeriesOrder, SeriesOrderPlugin.JobKey).DefaultSchedule.Cron);
        Assert.Equal("0 4 * * *", Job(AiFeaturePluginIds.DescriptionInsights, DescriptionInsightsPlugin.VibeJobKey).DefaultSchedule.Cron);
        Assert.Equal(TimeSpan.FromMinutes(15), Job(AiFeaturePluginIds.DescriptionInsights, DescriptionInsightsPlugin.JobKey).DefaultSchedule.Interval);
    }

    [Fact]
    public void AFeature_IsOffUntilItsPluginIsOn_AndOnlyThatFeatureTurnsOn()
    {
        Assert.All(Enum.GetValues<AiFeature>(), feature => Assert.False(_switches.IsOn(feature)));
        Assert.False(_switches.AnyOn);

        _catalog.SetEnabled(AiFeaturePluginIds.SeriesOrder, true);

        Assert.True(_switches.IsOn(AiFeature.SeriesAlignment));
        Assert.False(_switches.IsOn(AiFeature.SmartLabeling));
        Assert.False(_switches.IsOn(AiFeature.DescriptionIntelligence));
        Assert.True(_switches.AnyOn);

        _catalog.SetEnabled(AiFeaturePluginIds.SeriesOrder, false);

        Assert.False(_switches.IsOn(AiFeature.SeriesAlignment));
        Assert.False(_switches.AnyOn);
    }

    [Fact]
    public void EveryFeatureMapsToAPluginThatExists_AndTwoPluginsCarryTwoFeaturesEach()
    {
        var plugins = Enum.GetValues<AiFeature>().Select(AiFeaturePluginIds.For).ToList();

        Assert.All(plugins, id => Assert.NotNull(_catalog.Get(id)));
        Assert.Equal(AiFeaturePluginIds.SmartFiling, AiFeaturePluginIds.For(AiFeature.SmartLabeling));
        Assert.Equal(AiFeaturePluginIds.SmartFiling, AiFeaturePluginIds.For(AiFeature.TypeLogic));
        Assert.Equal(AiFeaturePluginIds.DescriptionInsights, AiFeaturePluginIds.For(AiFeature.VibeTags));
        Assert.Equal(AiFeaturePluginIds.DescriptionInsights, AiFeaturePluginIds.For(AiFeature.DescriptionIntelligence));
        Assert.Equal(AiFeaturePluginIds.All.Order(StringComparer.Ordinal), plugins.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DescriptionInsights_TurnsBothOfItsFeaturesOn_AndSmartFilingIsHiddenUntilItsHookExists()
    {
        _catalog.SetEnabled(AiFeaturePluginIds.DescriptionInsights, true);

        Assert.True(_switches.IsOn(AiFeature.VibeTags));
        Assert.True(_switches.IsOn(AiFeature.DescriptionIntelligence));
        Assert.False(_switches.IsOn(AiFeature.SeriesAlignment));

        Assert.True(_catalog.Get(AiFeaturePluginIds.SmartFiling)!.Manifest.Hidden);
        Assert.All(
            AiFeaturePluginIds.All.Where(id => id != AiFeaturePluginIds.SmartFiling),
            id => Assert.False(_catalog.Get(id)!.Manifest.Hidden));
    }

    [Fact]
    public void EveryAiFeaturePlugin_DeclaresWhoMayUseIt_WhatItNeedsAndWhatItKeeps()
    {
        Assert.All(_catalog.List(), plugin =>
        {
            var declaration = plugin.Manifest.ResolvedDeclaration;
            var expected = plugin.Manifest.Id == AiFeaturePluginIds.TasteSummary ? PluginAudience.Everyone : PluginAudience.Administrators;
            Assert.Equal(expected, declaration.Audience);
            Assert.Contains(plugin.Manifest.Declaration!.Requires, requirement => requirement.Kind == "local_ai");
            Assert.False(string.IsNullOrWhiteSpace(plugin.Manifest.Declaration.Stores?.SavedOutput));
            Assert.Empty(PluginDeclarationRules.Validate(plugin.Manifest.Declaration));
        });
    }

    [Fact]
    public void LocalAiCountsAsOn_WhenAnyFeaturePluginOrTheAudioPackIsOn()
    {
        var settings = new AiSettings();
        Assert.False(_switches.IsLocalAiOn(settings));

        settings.AudioPackEnabled = true;
        Assert.True(_switches.IsLocalAiOn(settings));

        settings.AudioPackEnabled = false;
        _catalog.SetEnabled(AiFeaturePluginIds.SmartFiling, true);
        Assert.True(_switches.IsLocalAiOn(settings));
    }

    [Fact]
    public void TheVibeWords_StartAsTheBuiltInLists()
    {
        var source = new PluginVibeVocabularySource(_services);
        var defaults = new AiVibeVocabulary();

        Assert.Equal(defaults.Books, source.Current.Books);
        Assert.Equal(defaults.MoviesTv, source.Current.MoviesTv);
        Assert.Equal(defaults.Music, source.Current.Music);
        Assert.Equal(defaults.Comics, source.Current.Comics);
    }

    [Fact]
    public void TheVibeWords_FollowTheDescriptionInsightsPluginSettings_TrimmedAndWithoutRepeats()
    {
        var source = new PluginVibeVocabularySource(_services);
        var settings = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            [DescriptionInsightsPlugin.BooksKey] = JsonSerializer.SerializeToElement(new[] { "Calm ", "calm", "bold", "" }),
            [DescriptionInsightsPlugin.MoviesTvKey] = JsonSerializer.SerializeToElement(new[] { "tense" }),
            [DescriptionInsightsPlugin.MusicKey] = JsonSerializer.SerializeToElement(new[] { "mellow" }),
            [DescriptionInsightsPlugin.ComicsKey] = JsonSerializer.SerializeToElement(new[] { "noir" }),
        };

        _catalog.SaveSettings(AiFeaturePluginIds.DescriptionInsights, settings);

        Assert.Equal(["Calm", "bold"], source.Current.Books);
        Assert.Equal(["tense"], source.Current.MoviesTv);
        Assert.Equal(["mellow"], source.Current.Music);
        Assert.Equal(["noir"], source.Current.Comics);
    }

    [Fact]
    public void AListThatHasNoWords_FallsBackToTheBuiltInWords_SoTaggingNeverRunsWithoutVocabulary()
    {
        var source = new PluginVibeVocabularySource(_services);
        var settings = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            [DescriptionInsightsPlugin.BooksKey] = JsonSerializer.SerializeToElement(Array.Empty<string>()),
            [DescriptionInsightsPlugin.MoviesTvKey] = JsonSerializer.SerializeToElement(new[] { "tense" }),
            [DescriptionInsightsPlugin.MusicKey] = JsonSerializer.SerializeToElement(new[] { "mellow" }),
            [DescriptionInsightsPlugin.ComicsKey] = JsonSerializer.SerializeToElement(new[] { "noir" }),
        };

        _catalog.SaveSettings(AiFeaturePluginIds.DescriptionInsights, settings);

        Assert.Equal(new AiVibeVocabulary().Books, source.Current.Books);
        Assert.Equal(["tense"], source.Current.MoviesTv);
    }

    [Fact]
    public async Task AJobPass_ThatFailsItself_FailsTheRun_ButOneThatSkipsOrSucceedsDoesNot()
    {
        var context = new NoContext();

        var failing = new AiFeatureScheduledJob("j", "Job", PluginSchedule.Every(TimeSpan.FromHours(1)), _ => Task.FromResult(TaskRunResult.Failed("The model broke.")));
        var skipping = new AiFeatureScheduledJob("j", "Job", PluginSchedule.Every(TimeSpan.FromHours(1)), _ => Task.FromResult(TaskRunResult.Skipped("Waiting: the machine is busy")));
        var succeeding = new AiFeatureScheduledJob("j", "Job", PluginSchedule.Every(TimeSpan.FromHours(1)), _ => Task.FromResult(TaskRunResult.Succeeded(3)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.RunAsync(context, default));
        Assert.Equal("The model broke.", error.Message);
        await skipping.RunAsync(context, default);
        await succeeding.RunAsync(context, default);
    }

    [Theory]
    [InlineData(AiFeaturePluginIds.SeriesOrder, "series_alignment")]
    [InlineData(AiFeaturePluginIds.DescriptionInsights, "description_intelligence", "vibe")]
    public async Task SwitchingAFeaturePluginOff_RetractsTheResultsItRecorded(string pluginId, params string[] featureKeys)
    {
        var persistence = new RetractionSpy();
        var service = new AiFeatureRetractionService(persistence, NullLogger<AiFeatureRetractionService>.Instance);

        var retracted = await service.RetractForPluginAsync(pluginId, default);

        Assert.Equal(featureKeys.Length, retracted);
        Assert.Equal(featureKeys, persistence.Retracted);
    }

    [Theory]
    [InlineData(AiFeaturePluginIds.SmartFiling)]
    [InlineData(AiFeaturePluginIds.TasteSummary)]
    [InlineData("someone.elses-plugin")]
    public async Task APluginThatRecordsNoAiResults_RetractsNothing(string pluginId)
    {
        var persistence = new RetractionSpy();
        var service = new AiFeatureRetractionService(persistence, NullLogger<AiFeatureRetractionService>.Instance);

        Assert.Equal(0, await service.RetractForPluginAsync(pluginId, default));
        Assert.Empty(persistence.Retracted);
    }

    private IPluginScheduledJob Job(string pluginId, string key) =>
        Assert.Single(_catalog.Get(pluginId)!.Capabilities.OfType<IPluginScheduledJob>(), job => job.Key == key);

    private static IEnumerable<ITuvimaPlugin> Plugins(IServiceProvider services) =>
    [
        new SmartFilingPlugin(),
        new SeriesOrderPlugin(services),
        new DescriptionInsightsPlugin(services),
        new TasteSummaryPlugin(),
    ];

    private sealed class RetractionSpy : MediaEngine.Domain.Contracts.IAiFeaturePersistenceRepository
    {
        public List<string> Retracted { get; } = [];

        public Task<int> RetractAiFeatureAsync(string featureKey, CancellationToken ct = default)
        {
            Retracted.Add(featureKey);
            return Task.FromResult(1);
        }

        public Task<MediaEngine.Domain.Models.AiFeatureWriteResult> ReplaceAiFeatureAsync(MediaEngine.Domain.Models.AiFeatureWriteRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<MediaEngine.Domain.Models.AiFeatureState?> GetAiFeatureStateAsync(Guid entityId, string featureKey, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<MediaEngine.Domain.Models.AiFeatureState> RecordAiFeatureFailureAsync(MediaEngine.Domain.Models.AiFeatureFailureRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ApprovesEverything : IPluginApprovalCheck
    {
        public bool IsCovered(PluginManifest manifest, PluginTrustLevel trust) => true;
    }

    private sealed class NoContext : IPluginExecutionContext
    {
        public string PluginId => "test";

        public IReadOnlyDictionary<string, JsonElement> Settings => new Dictionary<string, JsonElement>();

        public IPluginMediaAccess Media => throw new NotSupportedException();

        public IPluginHttpClient Http => throw new NotSupportedException();

        public IPluginStorage Storage => throw new NotSupportedException();

        public IPluginToolRuntime Tools => throw new NotSupportedException();

        public IPluginAiClient Ai => throw new NotSupportedException();

        public IPluginSecrets Secrets => throw new NotSupportedException();

        public IPluginRecordStore Records => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
