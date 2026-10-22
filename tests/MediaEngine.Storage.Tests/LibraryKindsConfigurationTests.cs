using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Storage.Tests;

public sealed class LibraryKindsConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tuvima-kinds-{Guid.NewGuid():N}");

    public LibraryKindsConfigurationTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void TheShippedKindsFileLoadsAndMatchesTheKindsThatShipInCode()
    {
        File.Copy(Path.Combine(FindRepoRoot(), "config", "library_kinds.json"), Path.Combine(_directory, "library_kinds.json"));

        var loaded = new ConfigurationDirectoryLoader(_directory).LoadLibraryKinds();

        Assert.Equal(JsonSerializer.Serialize(new LibraryKindsConfiguration()), JsonSerializer.Serialize(loaded));
    }

    [Fact]
    public void EveryShippedKindNamesAProviderAndANamingTemplateThatExist()
    {
        var config = Path.Combine(FindRepoRoot(), "config");
        var core = JsonSerializer.Deserialize<CoreConfiguration>(File.ReadAllText(Path.Combine(config, "core.json")))!;
        var providers = Directory.EnumerateFiles(Path.Combine(config, "providers"), "*.json").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        var knowledge = new LibraryKindKnowledge(providers!, new HashSet<string>(core.OrganizationTemplates.Keys, StringComparer.OrdinalIgnoreCase));
        var kinds = JsonSerializer.Deserialize<LibraryKindsConfiguration>(File.ReadAllText(Path.Combine(config, "library_kinds.json")))!;

        Assert.Empty(LibraryKindRules.Problems(kinds.Kinds, knowledge));
    }

    [Fact]
    public void EveryShippedLibraryNamesAKindThatShipsAndHoldsItsFormat()
    {
        var libraries = JsonSerializer.Deserialize<LibrariesConfiguration>(File.ReadAllText(Path.Combine(FindRepoRoot(), "config", "libraries.json")))!;

        Assert.Equal(
            ["books", "audiobooks", "tv", "movies", "music", "comics", "other_videos"],
            libraries.Libraries.Select(library => library.KindKey).ToArray());
        Assert.Empty(LibraryKindRules.LibraryProblems(libraries, new LibraryKindsConfiguration().Kinds));
    }

    [Fact]
    public void AMissingKindsFileMeansTheKindsThatShip()
    {
        var kinds = new ConfigurationDirectoryLoader(_directory).LoadLibraryKinds();

        Assert.Equal(11, kinds.Kinds.Count);
        Assert.Equal("movies", kinds.Kinds[0].Key);
    }

    [Fact]
    public void AFreshConfigurationFolderGetsTheKindsFileSoItCanBeEdited()
    {
        var fresh = Path.Combine(_directory, "fresh");

        _ = new ConfigurationDirectoryLoader(fresh);

        Assert.True(File.Exists(Path.Combine(fresh, "library_kinds.json")));
    }

    [Fact]
    public void AKindsFileWithAnUnknownAreaOrGroupingIsRefusedWithASentence()
    {
        File.WriteAllText(Path.Combine(_directory, "library_kinds.json"), """
            {
              "version": "1.0",
              "kinds": [
                { "key": "radio", "area": "airwaves", "label": "Radio", "icon": "Mic", "format": "Music", "lookup": "none",
                  "grouping": "folder_and_tags", "naming_template": "default", "misfit": "keep", "default_handling": "read_only",
                  "tooltip": "", "order": 1, "built_in": true, "available": true }
              ]
            }
            """);

        var ex = Assert.Throws<ConfigValidationException>(() => new ConfigurationDirectoryLoader(_directory).LoadLibraryKinds());

        Assert.Contains("\"area\"", ex.Message, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(_directory, "library_kinds.json"), File.ReadAllText(Path.Combine(_directory, "library_kinds.json")).Replace("airwaves", "listen").Replace("folder_and_tags", "by_mood"));
        var second = Assert.Throws<ConfigValidationException>(() => new ConfigurationDirectoryLoader(_directory).LoadLibraryKinds());

        Assert.Contains("\"grouping\"", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALibraryWithNoKindOrTheWrongFormatIsRefusedByTheLibrariesValidator()
    {
        var withNoKind = Libraries("", "Movies");
        var wrongFormat = Libraries("movies", "Books");
        var fine = Libraries("movies", "Movies");

        Assert.Contains(JsonConfigValidator.Validate(withNoKind, "libraries.json"), error => error.Contains("kind_key", StringComparison.Ordinal));
        Assert.Contains(JsonConfigValidator.Validate(wrongFormat, "libraries.json"), error => error.Contains("must include Movies", StringComparison.Ordinal));
        Assert.DoesNotContain(JsonConfigValidator.Validate(fine, "libraries.json"), error => error.Contains("kind_key", StringComparison.Ordinal) || error.Contains("must include", StringComparison.Ordinal));
    }

    [Fact]
    public void ALibraryOfAKindThatIsNotBuiltInIsNotRefusedHereBecauseAPluginMayOwnIt()
    {
        var pluginKind = Libraries("podcasts", "Music");

        Assert.DoesNotContain(JsonConfigValidator.Validate(pluginKind, "libraries.json"), error => error.Contains("kind_key", StringComparison.Ordinal));
    }

    private static LibrariesConfiguration Libraries(string kindKey, string mediaType) => new()
    {
        Libraries =
        [
            new LibraryFolderConfig
            {
                Id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                Name = "Library",
                Category = mediaType,
                Kind = LibraryKinds.Catalogued,
                KindKey = kindKey,
                Area = LibraryAreas.Watch,
                MediaTypes = [mediaType],
            },
        ],
    };

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
