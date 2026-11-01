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
        var libraries = JsonSerializer.Deserialize<LibrariesConfiguration>(File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "fixtures", "libraries.seven-defaults.json")))!;

        Assert.Equal(
            ["books", "audiobooks", "tv", "movies", "music", "comics", "other_videos"],
            libraries.Libraries.Select(library => library.KindKey).ToArray());
        Assert.Empty(LibraryKindRules.LibraryProblems(libraries, new LibraryKindsConfiguration().Kinds));
    }

    [Fact]
    public void AMissingKindsFileMeansTheKindsThatShip()
    {
        var kinds = new ConfigurationDirectoryLoader(_directory).LoadLibraryKinds();

        Assert.Equal(14, kinds.Kinds.Count);
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
    public void ALibraryOfTheWrongFormatIsRefusedByTheLibrariesValidator_ButOneWithNoKindLikeTheVaultEntriesIsLeftAlone()
    {
        var withNoKind = Libraries("", "Movies");
        var wrongFormat = Libraries("movies", "Books");
        var fine = Libraries("movies", "Movies");

        // The Vault keeps its own entries in libraries.json with no kind (Videos spec D45); a save from Settings still needs one.
        Assert.DoesNotContain(JsonConfigValidator.Validate(withNoKind, "libraries.json"), error => error.Contains("kind_key", StringComparison.Ordinal));
        Assert.Contains(JsonConfigValidator.Validate(wrongFormat, "libraries.json"), error => error.Contains("must include Movies", StringComparison.Ordinal));
        Assert.DoesNotContain(JsonConfigValidator.Validate(fine, "libraries.json"), error => error.Contains("kind_key", StringComparison.Ordinal) || error.Contains("must include", StringComparison.Ordinal));
    }

    [Fact]
    public void ALibraryOfAKindThatIsNotBuiltInIsNotRefusedHereBecauseAPluginMayOwnIt()
    {
        var pluginKind = Libraries("podcasts", "Music");

        Assert.DoesNotContain(JsonConfigValidator.Validate(pluginKind, "libraries.json"), error => error.Contains("kind_key", StringComparison.Ordinal));
    }

    [Fact]
    public void ALibraryIsCheckedAgainstTheKindsInTheLoadedKindsFileNotOnlyTheOnesThatShip()
    {
        CopyShippedConfig();
        var libraries = File.ReadAllText(Path.Combine(_directory, "libraries.json")).Replace("\"kind_key\": \"books\"", "\"kind_key\": \"podcasts\"", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_directory, "libraries.json"), libraries);

        // The kinds file does not know "podcasts", so the library may belong to a plugin and is left to the Engine to check.
        Assert.NotEmpty(new ConfigurationDirectoryLoader(_directory).LoadLibraries().Libraries);

        // A kinds file that adds Podcasts (format Music) is honoured: the Books library does not hold that format.
        var kinds = JsonSerializer.Deserialize<LibraryKindsConfiguration>(File.ReadAllText(Path.Combine(_directory, "library_kinds.json")))!;
        kinds.Kinds.Add(new LibraryKindDefinition
        {
            Key = "podcasts",
            Area = LibraryAreas.Listen,
            Label = "Podcasts",
            Icon = "Podcasts",
            Format = "Music",
            Lookup = LibraryKindLookup.None,
            Grouping = LibraryKindGroupings.FolderAndTags,
            NamingTemplate = "default",
            Misfit = LibraryKindMisfits.Keep,
            Tooltip = "Shows you follow.",
            Order = 9,
        });
        File.WriteAllText(Path.Combine(_directory, "library_kinds.json"), JsonSerializer.Serialize(kinds));

        var ex = Assert.Throws<ConfigValidationException>(() => new ConfigurationDirectoryLoader(_directory).LoadLibraries());

        Assert.Contains("must include Music", ex.Message, StringComparison.Ordinal);
        Assert.Contains("podcasts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedKindsPassTheStartUpCheckOfTheirProvidersAndNamingTemplates()
    {
        CopyShippedConfig();

        new ConfigurationDirectoryLoader(_directory).CheckLibraryKindReferences();
    }

    [Fact]
    public void TheDockerImageDefaultsPassTheStartUpCheckOfTheShippedKinds()
    {
        // The Docker entrypoint seeds /config from config/ and then lays docker/config/ over it.
        CopyShippedConfig();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), "docker", "config"), "*.json"))
        {
            File.Copy(file, Path.Combine(_directory, Path.GetFileName(file)), overwrite: true);
        }

        new ConfigurationDirectoryLoader(_directory).CheckLibraryKindReferences();
    }

    [Fact]
    public void ALookupProviderThatDoesNotExistIsReportedAtStartUpNamingTheKindAndTheFile()
    {
        CopyShippedConfig();
        EditMovies(movies => movies.Lookup = new LibraryKindLookup { Providers = ["tmbd", "subdl"] });

        var ex = Assert.Throws<ConfigValidationException>(() => new ConfigurationDirectoryLoader(_directory).CheckLibraryKindReferences());

        Assert.Contains("looks details up from \"tmbd\"", ex.Message, StringComparison.Ordinal);
        AssertNamesTheKindsFile(ex);
    }

    [Fact]
    public void ANamingTemplateThatDoesNotExistIsReportedAtStartUpNamingTheKindAndTheFile()
    {
        CopyShippedConfig();
        EditMovies(movies => movies.NamingTemplate = "Moveis");

        var ex = Assert.Throws<ConfigValidationException>(() => new ConfigurationDirectoryLoader(_directory).CheckLibraryKindReferences());

        Assert.Contains("\"naming_template\" of \"Moveis\"", ex.Message, StringComparison.Ordinal);
        AssertNamesTheKindsFile(ex);
    }

    private void EditMovies(Action<LibraryKindDefinition> edit)
    {
        var path = Path.Combine(_directory, "library_kinds.json");
        var kinds = JsonSerializer.Deserialize<LibraryKindsConfiguration>(File.ReadAllText(path))!;
        edit(kinds.Kinds.Single(kind => kind.Key == "movies"));
        File.WriteAllText(path, JsonSerializer.Serialize(kinds));
    }

    private static void AssertNamesTheKindsFile(ConfigValidationException ex)
    {
        Assert.Contains("\"movies\"", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("library_kinds.json", ex.FilePath, StringComparison.Ordinal);
        Assert.Equal("library_kinds.schema.json", ex.SchemaName);
    }

    [Fact]
    public void AConfigFolderTheLoaderJustCreatedIsNotHeldToTheProvidersThatShip()
    {
        var fresh = Path.Combine(_directory, "fresh-check");

        new ConfigurationDirectoryLoader(fresh).CheckLibraryKindReferences();
    }

    [Fact]
    public void TheKindsSchemaFileExistsAndTheShippedKindsFileNamesOnlyFieldsItAllows()
    {
        var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), "config", "schemas", "library_kinds.schema.json"))).RootElement;
        var allowed = schema.GetProperty("$defs").GetProperty("kind").GetProperty("properties").EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var kinds = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), "config", "library_kinds.json"))).RootElement.GetProperty("kinds");

        Assert.Contains("allows_multiple", allowed);
        Assert.Contains("max_libraries", allowed);
        foreach (var kind in kinds.EnumerateArray())
        {
            Assert.All(kind.EnumerateObject().Select(property => property.Name), name => Assert.Contains(name, allowed));
        }
    }

    private void CopyShippedConfig()
    {
        var config = Path.Combine(FindRepoRoot(), "config");
        foreach (var file in new[] { "core.json", "libraries.json", "library_kinds.json" })
        {
            File.Copy(file == "libraries.json" ? Path.Combine(FindRepoRoot(), "tests", "fixtures", "libraries.seven-defaults.json") : Path.Combine(config, file), Path.Combine(_directory, file));
        }

        Directory.CreateDirectory(Path.Combine(_directory, "providers"));
        foreach (var provider in Directory.EnumerateFiles(Path.Combine(config, "providers"), "*.json"))
        {
            File.Copy(provider, Path.Combine(_directory, "providers", Path.GetFileName(provider)));
        }
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
