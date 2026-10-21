using System.Reflection;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class PluginBlocklistTests : IDisposable
{
    private const string PluginId = "tuvima.test.blocked";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-blocklist-tests", Guid.NewGuid().ToString("N"));
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly PluginSettingsService _settings;
    private readonly TuvimaDataPaths _paths;
    private readonly PluginBlocklist _blocklist;
    private readonly PluginCatalog _catalog;

    public PluginBlocklistTests()
    {
        var library = Path.Combine(_root, "library");
        Directory.CreateDirectory(library);
        _loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _loader.SaveCore(new CoreConfiguration { LibraryRoot = library });
        _settings = new PluginSettingsService(_loader);
        _paths = new TuvimaDataPaths(Path.Combine(_root, "data"));
        _blocklist = new PluginBlocklist(_paths, NullLogger<PluginBlocklist>.Instance);
        _catalog = new PluginCatalog([], _settings, _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance, "1.0.0", "linux-x64", _blocklist);

        var directory = Path.Combine(library, ".data", "plugins", PluginId);
        Directory.CreateDirectory(directory);
        var assembly = Assembly.GetExecutingAssembly().Location;
        File.Copy(assembly, Path.Combine(directory, Path.GetFileName(assembly)), overwrite: true);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            {
              "entry_assembly": "MediaEngine.Api.Tests.dll",
              "entry_type": "MediaEngine.Api.Tests.PluginCatalogTests+DynamicTestPlugin",
              "id": "{{PluginId}}",
              "name": "Blocked Test",
              "version": "1.2.0"
            }
            """);
        _catalog.Reload();
    }

    [Fact]
    public void PluginOnTheBlocklist_IsSwitchedOffAndSaysWhy_AndCannotBeSwitchedOn()
    {
        SaveDirectory("""[{ "id": "tuvima.test.blocked", "reason": "It sent library names to a third party." }]""");

        var registration = _catalog.Get(PluginId)!;

        Assert.False(registration.Compatibility.IsCompatible);
        Assert.Equal("Blocked: It sent library names to a third party.", registration.Compatibility.Problem);
        Assert.False(registration.Enabled);
        Assert.Throws<PluginCannotBeEnabledException>(() => _catalog.SetEnabled(PluginId, true));
    }

    [Fact]
    public void BlockNamingOtherVersions_DoesNotBlockThisVersion()
    {
        SaveDirectory("""[{ "id": "tuvima.test.blocked", "reason": "Old bug.", "versions": ["1.0.0", "1.1.0"] }]""");

        Assert.True(_catalog.Get(PluginId)!.Compatibility.IsCompatible);
    }

    [Fact]
    public void BlockNamingThisVersion_BlocksIt_AndLiftingTheBlockFreesIt()
    {
        SaveDirectory("""[{ "id": "TUVIMA.TEST.BLOCKED", "reason": "Pulled.", "versions": ["1.2.0"] }]""");
        Assert.False(_catalog.Get(PluginId)!.Compatibility.IsCompatible);

        SaveDirectory("[]");
        Assert.True(_catalog.Get(PluginId)!.Compatibility.IsCompatible);
    }

    [Fact]
    public void BlockWithoutReason_StillSaysBlocked()
    {
        SaveDirectory("""[{ "id": "tuvima.test.blocked" }, { "reason": "no id" }, 5]""");

        Assert.Contains("Blocked", _catalog.Get(PluginId)!.Compatibility.Problem);
    }

    [Fact]
    public void NoSavedDirectory_BlocksNothing()
    {
        Assert.True(_catalog.Get(PluginId)!.Compatibility.IsCompatible);
    }

    private void SaveDirectory(string blocklistJson)
    {
        var path = Path.Combine(_paths.Root, "plugin-directory", "directory.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""{ "list": { "schema_version": "2.0", "plugins": [], "blocklist": {{blocklistJson}} } }""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 100000)));
        _blocklist.NotifyChanged();
    }

    public void Dispose()
    {
        _loader.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
