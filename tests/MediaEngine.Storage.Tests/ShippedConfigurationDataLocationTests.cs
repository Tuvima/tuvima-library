using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The shipped configuration contains no absolute or developer paths: Tuvima's own library folder follows the server's
/// data location, and no import folder exists until someone sets one.
/// </summary>
public sealed class ShippedConfigurationDataLocationTests : IDisposable
{
    private readonly string _previousDataDir = Environment.GetEnvironmentVariable("TUVIMA_DATA_DIR") ?? string.Empty;
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"tuvima-data-location-{Guid.NewGuid():N}");

    public ShippedConfigurationDataLocationTests()
    {
        Environment.SetEnvironmentVariable("TUVIMA_DATA_DIR", _dataRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TUVIMA_DATA_DIR", string.IsNullOrEmpty(_previousDataDir) ? null : _previousDataDir);
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    [Fact]
    public void NoShippedConfigFileContainsADeveloperPath()
    {
        var configDirectory = Path.Combine(FindRepoRoot(), "config");
        var files = Directory.EnumerateFiles(configDirectory, "*.json", SearchOption.TopDirectoryOnly).ToList();
        Assert.Contains(files, file => Path.GetFileName(file) == "core.json");
        Assert.Contains(files, file => Path.GetFileName(file) == "libraries.json");

        foreach (var file in files)
        {
            var json = File.ReadAllText(file);
            foreach (var forbidden in new[] { "C:\\\\", "C:/", "real-media", "real_import", "tuvima-import", "ProgramData" })
            {
                Assert.False(json.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(file)} contains {forbidden}");
            }
        }
    }

    [Fact]
    public void FreshShippedConfigFollowsTheServerDataLocationAndValidates()
    {
        var configDirectory = Path.Combine(Path.GetTempPath(), $"tuvima-fresh-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configDirectory);
        try
        {
            foreach (var name in new[] { "core.json", "libraries.json" })
            {
                File.Copy(Path.Combine(FindRepoRoot(), "config", name), Path.Combine(configDirectory, name));
            }

            var loader = new ConfigurationDirectoryLoader(configDirectory);
            var libraries = loader.LoadLibraries();
            var core = loader.LoadCore();

            var ownFolder = Path.Combine(Path.GetFullPath(_dataRoot), "library");
            var media = Assert.Single(libraries.StorageLocations, location => location.Id == TuvimaOwnFolders.LibraryLocationId);
            Assert.Equal(ownFolder, media.Path);
            Assert.True(media.AllowWrite);
            Assert.Equal(ownFolder, core.LibraryRoot);
            Assert.Equal(TuvimaOwnFolders.LibraryLocationId, libraries.ViewStorage.StorageLocationId);
            Assert.DoesNotContain(libraries.StorageLocations, location => location.Id == ImportFolderRules.LocationId);
            Assert.Null(ImportFolderRules.Find(libraries));
            Assert.All(
                libraries.Libraries.SelectMany(library => library.Sources),
                source => Assert.StartsWith(ownFolder, source.Path, StringComparison.OrdinalIgnoreCase));
            Assert.Empty(JsonConfigValidator.Validate(libraries, "libraries.json"));
            Assert.Empty(JsonConfigValidator.Validate(core, "core.json"));
            Assert.DoesNotContain(TuvimaDataPaths.ConfigToken, string.Concat(libraries.StorageLocations.Select(location => location.Path)));
        }
        finally
        {
            Directory.Delete(configDirectory, recursive: true);
        }
    }

    [Fact]
    public void DatabasePathFollowsTheExpandedLibraryRoot()
    {
        var configDirectory = Path.Combine(Path.GetTempPath(), $"tuvima-db-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configDirectory);
        try
        {
            File.Copy(Path.Combine(FindRepoRoot(), "config", "core.json"), Path.Combine(configDirectory, "core.json"));

            var path = TuvimaDataPathResolver.ResolveDatabasePath(configDirectory, null, null);

            Assert.Equal(
                Path.Combine(Path.GetFullPath(_dataRoot), "library", ".data", "database", "library.db"),
                path);
        }
        finally
        {
            Directory.Delete(configDirectory, recursive: true);
        }
    }

    [Fact]
    public void ConfigTokenOnlyAppliesToTheStartOfAValue()
    {
        Assert.Equal("C:\\Media", TuvimaDataPaths.ExpandConfigToken("C:\\Media"));
        Assert.Equal("/media/{TuvimaData}", TuvimaDataPaths.ExpandConfigToken("/media/{TuvimaData}"));
        Assert.Equal(Path.GetFullPath(_dataRoot), TuvimaDataPaths.ExpandConfigToken("{TuvimaData}"));
        Assert.Null(TuvimaDataPaths.ExpandConfigToken(null));
        Assert.Equal("{\"a\":1}", TuvimaDataPaths.ExpandConfigTokens("{\"a\":1}"));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find repository root.");
    }
}
