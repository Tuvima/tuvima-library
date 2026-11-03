using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.AiFeatures;
using MediaEngine.Api.Services.Plugins.Setup;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Contracts.Plugins;
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
/// Every shipped plugin's settings come from a JSON file (Plugins and Integrations screens v5 WP26, D51 and D58): a provider source
/// file's <c>settings_schema</c>, or the settings file a compiled-in plugin ships beside its code. These tests load every shipped
/// plugin, build its page model and prove that no setting on it was described by C#.
/// </summary>
public sealed class ShippedPluginSettingsTests
{
    private static readonly Assembly[] PluginAssemblies =
    [
        typeof(CommercialSkipPlugin).Assembly,
        typeof(FandomLorePlugin).Assembly,
        typeof(ListenBrainzPlugin).Assembly,
        typeof(SkipMarkersPlugin).Assembly,
        typeof(DescriptionInsightsPlugin).Assembly,
    ];

    [Fact]
    public void EveryCompiledInPlugin_ShowsOnlySettingsThatTraceToAShippedJsonFile()
    {
        var plugins = CompiledInPlugins();
        Assert.True(plugins.Count >= 9, "The nine compiled-in plugins should all be found.");

        foreach (var plugin in plugins)
        {
            var manifest = plugin.Manifest;
            var page = ManifestPage(manifest);
            var settingsCard = page.Cards.SingleOrDefault(card => card.Key == "settings");
            var shownKeys = settingsCard?.Controls.Where(control => control.Scope == "household").Select(control => control.Key).ToList() ?? [];

            if (manifest.SettingsSchema is not { } schema)
            {
                Assert.True(shownKeys.Count == 0, $"{manifest.Id} shows settings ({string.Join(", ", shownKeys)}) but ships no settings file.");
                continue;
            }

            var file = ShippedFileEqualTo(plugin, schema);
            Assert.True(file is not null, $"{manifest.Id}: its settings description is not one of the settings files shipped in its assembly.");

            var jsonKeys = file!.Value.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(jsonKeys.Order(StringComparer.Ordinal), PluginSettingsDescriptionReader.Keys(schema).Order(StringComparer.Ordinal));
            Assert.All(shownKeys, key => Assert.Contains(key, jsonKeys));
            Assert.True(page.ThingsToKnow.Count == 0, $"{manifest.Id}: {string.Join("; ", page.ThingsToKnow.Select(note => note.Message))}");
        }
    }

    [Fact]
    public void ACredentialEachPersonLinks_IsTheOnlySettingAPluginWithoutASettingsFileShows()
    {
        foreach (var plugin in CompiledInPlugins().Where(plugin => plugin.Manifest.SettingsSchema is null))
        {
            var settingsCard = ManifestPage(plugin.Manifest).Cards.SingleOrDefault(card => card.Key == "settings");
            Assert.All(settingsCard?.Controls ?? [], control =>
            {
                Assert.Equal("secret", control.Control);
                Assert.Equal("profile", control.Scope);
            });
        }
    }

    [Fact]
    public void EveryShippedSettingsFile_BelongsToACompiledInPlugin_AndReadsWithoutLeftoverWords()
    {
        foreach (var assembly in PluginAssemblies)
        {
            foreach (var fileName in ShippedPluginSettings.FilesIn(assembly))
            {
                var description = PluginSettingsDescriptionReader.Read(ShippedPluginSettings.Read(assembly, fileName));
                Assert.NotEmpty(description.Settings);
                Assert.True(description.ThingsToKnow.Count == 0, $"{fileName}: {string.Join("; ", description.ThingsToKnow.Select(note => note.Message))}");
                Assert.All(description.Settings, setting =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(setting.Title));
                    Assert.False(string.IsNullOrWhiteSpace(setting.Description));
                });

                Assert.Contains(CompiledInPlugins(), plugin => plugin.Manifest.SettingsSchema is { } schema && ShippedFileEqualTo(plugin, schema) is not null);
            }
        }
    }

    [Fact]
    public void EveryDefaultASettingsFileStates_IsTheDefaultTheCompiledInPluginSavesWithoutAChoice()
    {
        foreach (var plugin in CompiledInPlugins().Where(plugin => plugin.Manifest.SettingsSchema is not null))
        {
            var description = PluginSettingsDescriptionReader.Read(plugin.Manifest.SettingsSchema);
            foreach (var setting in description.Settings.Where(setting => setting.Default is not null))
            {
                Assert.True(plugin.Manifest.DefaultSettings.TryGetValue(setting.Key, out var saved), $"{plugin.Manifest.Id}: {setting.Key} has a default in its settings file but no saved default.");
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(saved.GetRawText()), JsonNode.Parse(setting.Default!.Value.GetRawText())), $"{plugin.Manifest.Id}: the default of {setting.Key} differs.");
            }
        }
    }

    [Fact]
    public void EveryShippedSourceFile_DescribesAllOfItsSettings_InItsOwnSettingsSchema()
    {
        var root = FindRepoRoot();
        var loader = new ConfigurationDirectoryLoader(Path.Combine(root, "config"));
        var files = Directory.GetFiles(Path.Combine(root, "config", "providers"), "*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var config = loader.LoadProvider(name) ?? throw new InvalidOperationException($"No provider file for {name}.");
            var declared = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            var core = ProviderListings.IsCore(name);

            var page = PluginPageModelBuilder.Build(PluginPageModelInputs.FromSourceFile(
                config,
                sheet: null,
                alwaysOn: core,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, JsonElement>()));

            var settingsCard = page.Cards.SingleOrDefault(card => card.Key == "settings");
            if (core)
            {
                Assert.Empty(settingsCard?.Controls ?? []);
                continue;
            }

            var jsonKeys = declared.GetProperty("settings_schema").GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(jsonKeys, settingsCard!.Controls.Select(control => control.Key).Order(StringComparer.Ordinal).ToList());
            Assert.True(page.ThingsToKnow.Count == 0, $"{name}: {string.Join("; ", page.ThingsToKnow.Select(note => note.Message))}");
            Assert.All(settingsCard.Controls, control => Assert.False(string.IsNullOrWhiteSpace(control.Description), $"{name}: {control.Key} has no description."));
        }
    }

    [Fact]
    public void ACompiledInPluginsCode_NeverDescribesASetting()
    {
        var root = FindRepoRoot();
        var folders = Directory.GetDirectories(Path.Combine(root, "src"), "MediaEngine.Plugin.*")
            .Append(Path.Combine(root, "src", "MediaEngine.Api", "Services", "Plugins", "AiFeatures"));

        foreach (var folder in folders)
        {
            foreach (var file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                var text = File.ReadAllText(file);
                Assert.DoesNotContain("\"properties\"", text);
                Assert.DoesNotContain("IPluginSettingsSchemaProvider", text);
            }
        }
    }

    private static List<ITuvimaPlugin> CompiledInPlugins()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var plugins = new List<ITuvimaPlugin>();
        foreach (var type in PluginAssemblies.Append(typeof(MediaEngine.Api.Services.Plugins.Trakt.TraktPlugin).Assembly).Distinct()
                     .SelectMany(assembly => assembly.GetTypes())
                     .Where(type => type is { IsAbstract: false, IsInterface: false } && typeof(ITuvimaPlugin).IsAssignableFrom(type)))
        {
            // The generic loaders (declarative, service, sandboxed) are built from a manifest file, not compiled in.
            var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(candidate => candidate.GetParameters().All(parameter => parameter.ParameterType == typeof(IServiceProvider)))
                .OrderBy(candidate => candidate.GetParameters().Length)
                .FirstOrDefault();
            if (constructor is null)
            {
                continue;
            }

            var arguments = constructor.GetParameters().Select(_ => (object)services).ToArray();
            plugins.Add((ITuvimaPlugin)constructor.Invoke(arguments));
        }

        return plugins.DistinctBy(plugin => plugin.Manifest.Id).OrderBy(plugin => plugin.Manifest.Id, StringComparer.Ordinal).ToList();
    }

    private static JsonElement? ShippedFileEqualTo(ITuvimaPlugin plugin, JsonElement schema)
    {
        var assembly = plugin.GetType().Assembly;
        foreach (var fileName in ShippedPluginSettings.FilesIn(assembly))
        {
            var shipped = ShippedPluginSettings.Read(assembly, fileName);
            if (JsonNode.DeepEquals(JsonNode.Parse(shipped.GetRawText()), JsonNode.Parse(schema.GetRawText())))
            {
                return shipped;
            }
        }

        return null;
    }

    private static PluginSetupPageResponse ManifestPage(PluginManifest manifest)
    {
        var registration = new PluginRegistration(
            manifest,
            Enabled: true,
            IsBuiltIn: true,
            Capabilities: [],
            Settings: new Dictionary<string, JsonElement>(),
            LoadError: null,
            SettingsSchema: manifest.SettingsSchema,
            ManifestPath: null,
            Trust: PluginTrustLevel.Official,
            Compatibility: PluginCompatibilityResult.Compatible);
        var access = PluginAccessMapper.Map(
            manifest.Declaration,
            [.. manifest.Network.Select(destination => (destination.Host, (string?)destination.Sends))],
            manifest.Secrets.Count > 0,
            PluginAccessMapper.MediaLabelsFor(PluginCategories.ResolveMediaTypes(manifest.MediaTypes)),
            manifest.MediaTypes,
            manifest.Category);
        return PluginPageModelBuilder.Build(PluginPageModelInputs.FromManifest(
            registration,
            access,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, JsonElement>()));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
