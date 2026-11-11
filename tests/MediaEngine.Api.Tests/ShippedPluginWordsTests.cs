using System.Text.Json;
using MediaEngine.Api.Services.Plugins.AiFeatures;
using MediaEngine.Api.Services.Plugins.Trakt;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Plugin.CommercialSkip;
using MediaEngine.Plugin.FandomLore;
using MediaEngine.Plugin.ListenBrainz;
using MediaEngine.Plugin.MediaSegments;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Every plugin and source the Library ships says the same things the same way on the Plugins page (Plugins and Integrations
/// screens v4 D31, D34, D35): one of three categories, words from the fixed "provides" list, and a one-sentence description of at
/// most 90 characters.
/// </summary>
public sealed class ShippedPluginWordsTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "config", "providers")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private sealed record Shipped(string Id, string? Category, IReadOnlyList<string> Provides, string? ShortDescription, string? DisplayName);

    private static IReadOnlyList<Shipped> Sources() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "config", "providers"), "*.json")
            .Select(path =>
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                var ui = root.GetProperty("ui_metadata");
                return new Shipped(
                    Path.GetFileNameWithoutExtension(path),
                    ui.TryGetProperty("plugin_category", out var category) ? category.GetString() : null,
                    ui.TryGetProperty("provides", out var provides) ? provides.EnumerateArray().Select(item => item.GetString()!).ToList() : [],
                    ui.TryGetProperty("short_description", out var description) ? description.GetString() : null,
                    root.TryGetProperty("display_name", out var name) ? name.GetString() : null);
            })
            .ToList();

    private static IReadOnlyList<PluginManifest> Plugins() =>
    [
        new SmartFilingPlugin().Manifest,
        new SeriesOrderPlugin(null!).Manifest,
        new DescriptionInsightsPlugin(null!).Manifest,
        new TasteSummaryPlugin().Manifest,
        new CommercialSkipPlugin().Manifest,
        new SkipMarkersPlugin().Manifest,
        new FandomLorePlugin().Manifest,
        new ListenBrainzPlugin().Manifest,
        new TraktPlugin().Manifest,
    ];

    [Fact]
    public void EveryShippedSource_HasACategory_AProvidesList_AndAShortSentence()
    {
        foreach (var source in Sources())
        {
            Assert.Contains(PluginCategories.Resolve(source.Category), new[] { "intake", "enrichment", "accounts" });
            Assert.False(string.IsNullOrWhiteSpace(source.Category), $"{source.Id} names no category.");
            Assert.NotEmpty(source.Provides);
            Assert.All(source.Provides, word => Assert.Contains(word, PluginProvides.All));
            Assert.False(string.IsNullOrWhiteSpace(source.ShortDescription), $"{source.Id} has no short description.");
            Assert.True(source.ShortDescription!.Length <= PluginDescriptions.ShortLimit, $"{source.Id}: {source.ShortDescription.Length} characters.");
        }
    }

    [Fact]
    public void EveryShippedPlugin_HasACategory_AProvidesList_AndAShortSentence()
    {
        foreach (var manifest in Plugins())
        {
            Assert.False(string.IsNullOrWhiteSpace(manifest.Category), $"{manifest.Id} names no category.");
            Assert.Contains(manifest.Category, new[] { "intake", "enrichment", "accounts" });
            Assert.NotEmpty(manifest.Provides);
            Assert.All(manifest.Provides, word => Assert.Contains(word, PluginProvides.All));
            Assert.False(string.IsNullOrWhiteSpace(manifest.ShortDescription), $"{manifest.Id} has no short description.");
            Assert.True(manifest.ShortDescription!.Length <= PluginDescriptions.ShortLimit, $"{manifest.Id}: {manifest.ShortDescription.Length} characters.");
        }
    }

    [Fact]
    public void TheThreeTabs_HoldTheShippedSetByWhenLibraryUsesIt()
    {
        var categories = Sources().Select(source => source.Category).Concat(Plugins().Select(plugin => plugin.Category)).ToList();

        Assert.Equal(11, categories.Count(category => category == "intake"));
        Assert.Equal(9, categories.Count(category => category == "enrichment"));
        Assert.Equal(2, categories.Count(category => category == "accounts"));
    }

    [Fact]
    public void StashDb_IsNamedStashDbEverywhereAPersonReads()
    {
        Assert.Equal("StashDB", Sources().Single(source => source.Id == "stashbox").DisplayName);
    }

    [Fact]
    public void TheLocalAiPlugins_SayTheyNeedLocalAi()
    {
        foreach (var manifest in Plugins().Where(plugin => plugin.Id.StartsWith("tuvima.ai", StringComparison.Ordinal) || AiFeaturePluginIds.All.Contains(plugin.Id)))
        {
            Assert.Contains(manifest.Declaration!.Requires, requirement => requirement.Kind == "local_ai");
        }
    }

    [Fact]
    public void ADescriptionOverTheLimit_IsCutAtAWordWithAnEllipsis()
    {
        var text = PluginDescriptions.Short(null, "Reads a very long list of file and folder names when files are added and then decides a lot of things about them. More.");

        Assert.True(text.Length <= PluginDescriptions.ShortLimit);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void TheOldCategoryWords_MapOnce()
    {
        Assert.Equal("intake", PluginCategories.Resolve("metadata"));
        Assert.Equal("enrichment", PluginCategories.Resolve("media-processing"));
        Assert.Equal("enrichment", PluginCategories.Resolve("utilities"));
        Assert.Equal("accounts", PluginCategories.Resolve("services"));
        Assert.Equal("enrichment", PluginCategories.Resolve(null));
    }
}
