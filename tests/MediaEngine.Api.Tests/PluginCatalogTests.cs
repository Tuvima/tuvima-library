using System.Reflection;
using System.Text.Json;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugin.FandomLore;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class PluginCatalogTests
{
    [Fact]
    public void BuiltInFandomPlugin_ExposesUniverseLoreCapabilityAndSettingsSchema()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "tuvima-fandom-plugin-tests", Guid.NewGuid().ToString("N"));
        var configRoot = Path.Combine(tempRoot, "config");
        var libraryRoot = Path.Combine(tempRoot, "library");
        Directory.CreateDirectory(configRoot);
        Directory.CreateDirectory(libraryRoot);

        try
        {
            using var loader = new ConfigurationDirectoryLoader(configRoot);
            loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });

            var catalog = new PluginCatalog(
                [new FandomLorePlugin()],
                new PluginSettingsService(loader),
                loader,
                AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);

            var registration = Assert.Single(catalog.List());
            Assert.True(registration.IsBuiltIn);
            Assert.False(registration.Enabled);
            Assert.Equal("tuvima.fandom-lore", registration.Manifest.Id);
            Assert.Contains(registration.Manifest.Capabilities, capability => capability.Kind == "universe-lore-provider");
            Assert.NotNull(registration.SettingsSchema);
            Assert.True(registration.SettingsSchema!.Value.TryGetProperty("properties", out var properties));
            Assert.True(properties.TryGetProperty("source_mode", out var sourceMode));
            Assert.Equal("Source mode", sourceMode.GetProperty("title").GetString());
            Assert.False(Directory.Exists(Path.Combine(libraryRoot, ".data", "plugin-config")));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void DynamicPluginManifest_CanBeEditedAndDeleted()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "tuvima-plugin-tests", Guid.NewGuid().ToString("N"));
        var configRoot = Path.Combine(tempRoot, "config");
        var libraryRoot = Path.Combine(tempRoot, "library");
        Directory.CreateDirectory(configRoot);
        Directory.CreateDirectory(libraryRoot);

        try
        {
            using var loader = new ConfigurationDirectoryLoader(configRoot);
            loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });

            var settingsService = new PluginSettingsService(loader);
            var pluginRoot = Path.Combine(libraryRoot, ".data", "plugins", "sample-plugin");
            Directory.CreateDirectory(pluginRoot);

            var testAssemblyPath = Assembly.GetExecutingAssembly().Location;
            File.Copy(testAssemblyPath, Path.Combine(pluginRoot, Path.GetFileName(testAssemblyPath)));
            File.WriteAllText(Path.Combine(pluginRoot, "plugin.json"), """
                {
                  "id": "tuvima.test.dynamic",
                  "name": "Dynamic Test",
                  "version": "1.0.0",
                  "description": "Loaded from plugin.json.",
                  "entry_assembly": "MediaEngine.Api.Tests.dll",
                  "entry_type": "MediaEngine.Api.Tests.PluginCatalogTests+DynamicTestPlugin",
                  "default_settings": {
                    "enabled_feature": true
                  }
                }
                """);

            var catalog = new PluginCatalog([], settingsService, loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
            var plugin = Assert.Single(catalog.List());
            Assert.False(plugin.IsBuiltIn);
            Assert.Equal("Dynamic Test", plugin.Manifest.Name);
            Assert.Equal("1.0.0", plugin.Manifest.Version);
            Assert.NotNull(plugin.SettingsSchema);
            Assert.True(plugin.SettingsSchema!.Value.TryGetProperty("properties", out var schemaProperties));
            Assert.True(schemaProperties.TryGetProperty("enabled_feature", out var enabledFeatureSchema));
            Assert.Equal("Enable Feature", enabledFeatureSchema.GetProperty("title").GetString());

            catalog.SetEnabled("tuvima.test.dynamic", true);
            Assert.True(catalog.Get("tuvima.test.dynamic")!.Enabled);
            catalog.SaveSettings("tuvima.test.dynamic", new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["enabled_feature"] = JsonSerializer.SerializeToElement(false),
                ["batch_size"] = JsonSerializer.SerializeToElement(5),
            });
            Assert.False(catalog.Get("tuvima.test.dynamic")!.Settings["enabled_feature"].GetBoolean());

            var invalidSettings = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["enabled_feature"] = JsonSerializer.SerializeToElement("yes"),
                ["batch_size"] = JsonSerializer.SerializeToElement(5),
            };
            var invalid = Assert.Throws<InvalidOperationException>(() =>
                catalog.SaveSettings("tuvima.test.dynamic", invalidSettings));
            Assert.Contains("enabled_feature", invalid.Message, StringComparison.Ordinal);

            var manifestJson = catalog.GetManifestJson("tuvima.test.dynamic");
            Assert.Contains("\"Dynamic Test\"", manifestJson, StringComparison.Ordinal);

            using var document = JsonDocument.Parse(manifestJson);
            var manifest = document.Deserialize<PluginManifest>()!;
            var updatedManifest = manifest with { Version = "1.1.0", Description = "Edited through JSON." };
            catalog.SaveManifestJson("tuvima.test.dynamic", JsonSerializer.Serialize(updatedManifest));

            var updated = catalog.Get("tuvima.test.dynamic")!;
            Assert.Equal("1.1.0", updated.Manifest.Version);
            Assert.Equal("Edited through JSON.", updated.Manifest.Description);

            catalog.DeletePlugin("tuvima.test.dynamic");

            Assert.Empty(catalog.List());
            Assert.False(Directory.Exists(pluginRoot));
            Assert.False(File.Exists(Path.Combine(libraryRoot, ".data", "plugin-config", "tuvima.test.dynamic.json")));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    public sealed class DynamicTestPlugin : ITuvimaPlugin
    {
        public PluginManifest Manifest { get; } = new()
        {
            Id = "tuvima.test.dynamic",
            Name = "Assembly Fallback",
            Version = "0.0.1",
            Description = "The editable manifest should override this metadata.",
        };

        public IReadOnlyList<IPluginCapability> CreateCapabilities() => [new DynamicTestSettingsSchema()];
    }

    public sealed class DynamicTestSettingsSchema : IPluginSettingsSchemaProvider
    {
        public string Kind => "settings-schema";

        public JsonElement GetSettingsSchema() => JsonSerializer.SerializeToElement(new
        {
            properties = new Dictionary<string, object>
            {
                ["enabled_feature"] = new
                {
                    type = "boolean",
                    title = "Enable Feature",
                    description = "Turns on the test feature.",
                },
                ["batch_size"] = new
                {
                    type = "integer",
                    title = "Batch Size",
                    minimum = 1,
                    maximum = 10,
                    @default = 5,
                    advanced = true,
                },
            },
            required = new[] { "enabled_feature" },
        });
    }
}
