using System.Text.Json;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class TuvimaDataPathResolverTests : IDisposable
{
    private readonly string _configDirectory = Path.Combine(
        Path.GetTempPath(),
        $"tuvima_path_resolver_{Guid.NewGuid():N}");

    [Fact]
    public void ExplicitDatabasePath_HasHighestPrecedence()
    {
        var expected = Path.Combine("custom", "library.db");

        var actual = TuvimaDataPathResolver.ResolveDatabasePath(
            _configDirectory,
            expected,
            Path.Combine("ignored", "library"));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EnvironmentLibraryRoot_OverridesConfiguredLibraryRoot()
    {
        WriteCore(Path.Combine("configured", "library"));
        var environmentRoot = Path.Combine("environment", "library");

        var actual = TuvimaDataPathResolver.ResolveDatabasePath(
            _configDirectory,
            null,
            environmentRoot);

        Assert.Equal(Path.Combine(environmentRoot, ".data", "database", "library.db"), actual);
    }

    [Fact]
    public void ConfiguredLibraryRoot_IsUsedWhenEnvironmentIsAbsent()
    {
        var configuredRoot = Path.Combine("configured", "library");
        WriteCore(configuredRoot);

        var actual = TuvimaDataPathResolver.ResolveDatabasePath(
            _configDirectory,
            null,
            null);

        Assert.Equal(Path.Combine(configuredRoot, ".data", "database", "library.db"), actual);
    }

    [Fact]
    public void MissingConfiguration_UsesLocalDataDirectory()
    {
        var actual = TuvimaDataPathResolver.ResolveDatabasePath(
            _configDirectory,
            null,
            null);

        Assert.Equal(Path.Combine(".data", "database", "library.db"), actual);
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDirectory))
        {
            TestTemp.DeleteDirectory(_configDirectory);
        }
    }

    private void WriteCore(string libraryRoot)
    {
        Directory.CreateDirectory(_configDirectory);
        File.WriteAllText(
            Path.Combine(_configDirectory, "core.json"),
            JsonSerializer.Serialize(new { library_root = libraryRoot }));
    }
}
