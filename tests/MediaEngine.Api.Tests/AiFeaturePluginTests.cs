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
    public void ThePluginsThatRunOnASchedule_AskToRunScheduledWork_AndDeclareTheirJobAsAValidOne()
    {
        foreach (var id in new[] { AiFeaturePluginIds.SeriesOrder, AiFeaturePluginIds.VibeTags, AiFeaturePluginIds.DescriptionInsights })
        {
            var plugin = _catalog.Get(id)!;
            Assert.Contains(PluginPermissionIds.ScheduledWork, plugin.Manifest.Permissions);
            Assert.Single(plugin.Capabilities.OfType<IPluginScheduledJob>());
            Assert.Null(PluginHostCapabilityRules.Validate(plugin.Capabilities));
        }

        foreach (var id in new[] { AiFeaturePluginIds.SmartLabelling, AiFeaturePluginIds.TypeHelper })
        {
            Assert.DoesNotContain(PluginPermissionIds.ScheduledWork, _catalog.Get(id)!.Manifest.Permissions);
        }
    }

    [Fact]
    public void TheJobs_KeepTheSchedulesTheOldSettingsGave()
    {
        Assert.Equal("0 3 * * *", Job(AiFeaturePluginIds.SeriesOrder).DefaultSchedule.Cron);
        Assert.Equal("0 4 * * *", Job(AiFeaturePluginIds.VibeTags).DefaultSchedule.Cron);
        Assert.Equal(TimeSpan.FromMinutes(15), Job(AiFeaturePluginIds.DescriptionInsights).DefaultSchedule.Interval);
    }

    [Fact]
    public void AFeature_IsOffUntilItsPluginIsOn_AndOnlyThatFeatureTurnsOn()
    {
        Assert.All(Enum.GetValues<AiFeature>(), feature => Assert.False(_switches.IsOn(feature)));
        Assert.False(_switches.AnyOn);

        _catalog.SetEnabled(AiFeaturePluginIds.VibeTags, true);

        Assert.True(_switches.IsOn(AiFeature.VibeTags));
        Assert.False(_switches.IsOn(AiFeature.SmartLabeling));
        Assert.False(_switches.IsOn(AiFeature.DescriptionIntelligence));
        Assert.True(_switches.AnyOn);

        _catalog.SetEnabled(AiFeaturePluginIds.VibeTags, false);

        Assert.False(_switches.IsOn(AiFeature.VibeTags));
        Assert.False(_switches.AnyOn);
    }

    [Fact]
    public void EveryFeatureMapsToItsOwnPlugin()
    {
        var plugins = Enum.GetValues<AiFeature>().Select(AiFeaturePluginIds.For).ToList();

        Assert.Equal(plugins.Count, plugins.Distinct(StringComparer.Ordinal).Count());
        Assert.All(plugins, id => Assert.NotNull(_catalog.Get(id)));
    }

    [Fact]
    public void LocalAiCountsAsOn_WhenAnyFeaturePluginOrTheAudioPackIsOn()
    {
        var settings = new AiSettings();
        Assert.False(_switches.IsLocalAiOn(settings));

        settings.AudioPackEnabled = true;
        Assert.True(_switches.IsLocalAiOn(settings));

        settings.AudioPackEnabled = false;
        _catalog.SetEnabled(AiFeaturePluginIds.TypeHelper, true);
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
    public void TheVibeWords_FollowTheVibeTagsPluginSettings_TrimmedAndWithoutRepeats()
    {
        var source = new PluginVibeVocabularySource(_services);
        var settings = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            [VibeTagsPlugin.BooksKey] = JsonSerializer.SerializeToElement(new[] { "Calm ", "calm", "bold", "" }),
            [VibeTagsPlugin.MoviesTvKey] = JsonSerializer.SerializeToElement(new[] { "tense" }),
            [VibeTagsPlugin.MusicKey] = JsonSerializer.SerializeToElement(new[] { "mellow" }),
            [VibeTagsPlugin.ComicsKey] = JsonSerializer.SerializeToElement(new[] { "noir" }),
        };

        _catalog.SaveSettings(AiFeaturePluginIds.VibeTags, settings);

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
            [VibeTagsPlugin.BooksKey] = JsonSerializer.SerializeToElement(Array.Empty<string>()),
            [VibeTagsPlugin.MoviesTvKey] = JsonSerializer.SerializeToElement(new[] { "tense" }),
            [VibeTagsPlugin.MusicKey] = JsonSerializer.SerializeToElement(new[] { "mellow" }),
            [VibeTagsPlugin.ComicsKey] = JsonSerializer.SerializeToElement(new[] { "noir" }),
        };

        _catalog.SaveSettings(AiFeaturePluginIds.VibeTags, settings);

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

    private IPluginScheduledJob Job(string pluginId) =>
        Assert.Single(_catalog.Get(pluginId)!.Capabilities.OfType<IPluginScheduledJob>());

    private static IEnumerable<ITuvimaPlugin> Plugins(IServiceProvider services) =>
    [
        new SmartLabellingPlugin(),
        new TypeHelperPlugin(),
        new SeriesOrderPlugin(services),
        new VibeTagsPlugin(services),
        new DescriptionInsightsPlugin(services),
    ];

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
