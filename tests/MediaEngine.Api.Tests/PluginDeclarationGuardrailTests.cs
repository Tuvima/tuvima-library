using MediaEngine.Api.Services.Plugins.AiFeatures;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugin.CommercialSkip;
using MediaEngine.Plugin.FandomLore;
using MediaEngine.Plugin.ListenBrainz;
using MediaEngine.Plugin.MediaSegments;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Every shipped plugin and metadata source says who may use it, how it behaves when it fails and whether to tell the administrator
/// (Plugin architecture spec PA3). A new one that leaves these out fails here, so the Plugins page never shows a blank where an
/// administrator expects an answer.
/// </summary>
public sealed class PluginDeclarationGuardrailTests : IDisposable
{
    private readonly ConfigurationDirectoryLoader _loader = new(Path.Combine(FindRepoRoot(), "config"));

    public void Dispose() => _loader.Dispose();

    [Fact]
    public void EverySource_StatesItsAudienceFailureAndNoticeBehaviour_AndHasACategory()
    {
        var sources = _loader.LoadAllProviders();
        Assert.NotEmpty(sources);

        foreach (var source in sources)
        {
            var declaration = source.Declaration;
            Assert.True(declaration is not null, $"Source '{source.Name}' has no declaration block.");
            Assert.True(PluginDeclarationRules.TryParseAudience(declaration!.Audience, out _), $"Source '{source.Name}' does not state who may use it.");
            Assert.False(string.IsNullOrWhiteSpace(declaration.OnFailure), $"Source '{source.Name}' does not state what happens when it fails.");
            Assert.False(string.IsNullOrWhiteSpace(declaration.Notify), $"Source '{source.Name}' does not say whether to tell the administrator.");
            Assert.False(string.IsNullOrWhiteSpace(declaration.WeightClass), $"Source '{source.Name}' does not state how heavy it is.");
            Assert.False(string.IsNullOrWhiteSpace(declaration.Changelog), $"Source '{source.Name}' has no changelog line.");
            Assert.True(source.UiMetadata is not null && !string.IsNullOrWhiteSpace(source.UiMetadata.Category), $"Source '{source.Name}' has no category.");
            Assert.Empty(PluginDeclarationRules.Validate(declaration));
        }
    }

    [Fact]
    public void EveryBuiltInPlugin_StatesItsAudienceWeightFailureNoticeAndChangelog_AndHasASettingsFormWhenItHasSettings()
    {
        var plugins = BuiltInPlugins();
        Assert.True(plugins.Count >= 7, "The built-in plugin scan found fewer plugins than ship.");

        foreach (var plugin in plugins)
        {
            var id = plugin.Manifest.Id;
            var declaration = plugin.Manifest.Declaration;
            Assert.True(declaration is not null, $"{id} has no declaration block.");
            Assert.True(!string.IsNullOrWhiteSpace(declaration!.Audience), $"{id} does not say who may use it.");
            Assert.True(!string.IsNullOrWhiteSpace(declaration.WeightClass), $"{id} does not say how heavy it is.");
            Assert.True(!string.IsNullOrWhiteSpace(declaration.OnFailure), $"{id} does not say what happens when it fails.");
            Assert.True(!string.IsNullOrWhiteSpace(declaration.Notify), $"{id} does not say whether to tell the administrator.");
            Assert.True(!string.IsNullOrWhiteSpace(declaration.Changelog), $"{id} has no changelog line.");
            Assert.True(!string.IsNullOrWhiteSpace(declaration.Stores?.SavedOutput) || declaration.Stores?.Records == true, $"{id} does not say what it keeps.");
            Assert.Empty(PluginDeclarationRules.Validate(declaration));
            Assert.True(
                plugin.Manifest.DefaultSettings.Count == 0 || plugin.Manifest.SettingsSchema is not null,
                $"{id} has settings but no settings form.");
        }
    }

    [Fact]
    public void ThePluginsThatNeedLocalAi_SayTheyDo()
    {
        var plugins = BuiltInPlugins().Where(plugin => plugin.Manifest.AiPermissions.Count > 0);

        Assert.All(plugins, plugin =>
            Assert.Contains(plugin.Manifest.Declaration!.Requires, requirement => requirement.Kind == "local_ai"));
    }

    [Fact]
    public void TheShippedPlugins_AreTheEightAgreed()
    {
        Assert.Equal(
            [
                "tuvima.ai-description-insights",
                "tuvima.ai-series-order",
                "tuvima.ai-smart-filing",
                "tuvima.ai-taste-summary",
                "tuvima.commercial-skip",
                "tuvima.fandom-lore",
                "tuvima.listenbrainz",
                "tuvima.skip-markers",
            ],
            BuiltInPlugins().Select(plugin => plugin.Manifest.Id).Order(StringComparer.Ordinal));
    }

    /// <summary>Every plugin class that ships in the Engine or in one of the plugin projects, found by scanning, so a new one cannot be missed.</summary>
    private static List<ITuvimaPlugin> BuiltInPlugins()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        Type[] assemblyMarkers = [typeof(AiFeaturePluginIds), typeof(SkipMarkersPlugin), typeof(CommercialSkipPlugin), typeof(FandomLorePlugin), typeof(ListenBrainzPlugin)];
        return assemblyMarkers
            .Select(marker => marker.Assembly)
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsAbstract: false, IsInterface: false } && typeof(ITuvimaPlugin).IsAssignableFrom(type))
            // The loaders for plugins from outside (declarative, service, sandboxed) take a manifest and are not built-ins.
            .Where(type => type.GetConstructor(Type.EmptyTypes) is not null || type.GetConstructor([typeof(IServiceProvider)]) is not null)
            .Select(type => (ITuvimaPlugin)(type.GetConstructor(Type.EmptyTypes) is not null
                ? Activator.CreateInstance(type)!
                : Activator.CreateInstance(type, services)!))
            .ToList();
    }

    [Fact]
    public void TheOnDemandSources_AreForEveryone_AndEachOffersItsLookupAction()
    {
        var lyrics = Assert.IsType<ProviderConfiguration>(_loader.LoadProvider("lrclib"));
        var subtitles = Assert.IsType<ProviderConfiguration>(_loader.LoadProvider("subdl"));

        Assert.Equal("everyone", lyrics.Declaration!.Audience);
        Assert.Equal("find_lyrics", Assert.Single(lyrics.Declaration.Actions).Kind);
        Assert.Equal("lyrics", lyrics.Declaration.TextTrack);
        Assert.Equal("everyone", subtitles.Declaration!.Audience);
        Assert.Equal("find_subtitles", Assert.Single(subtitles.Declaration.Actions).Kind);
        Assert.Equal("subtitles", subtitles.Declaration.TextTrack);
    }

    [Fact]
    public void EveryOtherSource_IsForAdministratorsOnly()
    {
        foreach (var source in _loader.LoadAllProviders().Where(source => source.Name is not ("lrclib" or "subdl")))
        {
            Assert.Equal("administrators", source.Declaration!.Audience);
        }
    }

    [Fact]
    public void StashBox_IsTheVaultsOwnSource()
    {
        var stash = Assert.IsType<ProviderConfiguration>(_loader.LoadProvider("stashbox"));

        Assert.True(stash.AdultOnly);
        Assert.Equal("own_source", stash.Declaration!.Vault);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
