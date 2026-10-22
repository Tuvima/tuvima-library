using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Storage.Tests;

/// <summary>The kind of a catalogued library decides what it may look up (Library setup D17, D38), and the file says so in plain words when its own fields disagree.</summary>
public sealed class LibraryKindLookupValidationTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine([dir!, .. parts]);
    }

    private static LibrariesConfiguration Load(params string[] parts) =>
        JsonSerializer.Deserialize<LibrariesConfiguration>(File.ReadAllText(RepoFile(parts)))!;

    private static LibrariesConfiguration ShippedWith(LibraryFolderConfig extra)
    {
        var config = Load("config", "libraries.json");
        config.Libraries.Add(extra);
        return config;
    }

    private static LibraryFolderConfig Plain(string kindKey, string mediaType, string area, string name) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Name = name,
        Category = mediaType,
        Kind = LibraryKinds.Catalogued,
        KindKey = kindKey,
        Area = area,
        Presentation = LibraryPresentations.Catalogue,
        MetadataPolicy = LibraryMetadataPolicies.LocalOnly,
        YoutubeLookup = false,
        MediaTypes = [mediaType],
        Visibility = LibraryVisibility.Household,
        Sources =
        [
            new LibrarySourceConfig
            {
                Id = Guid.NewGuid().ToString("D"),
                Path = Path.Combine(Path.GetTempPath(), "tuvima-plain-" + Guid.NewGuid().ToString("N")),
                ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
                AccessMode = LibrarySourceAccessModes.ReadOnly,
            },
        ],
        OrganizationPolicy = new LibraryOrganizationPolicyConfig { Mode = LibraryOrganizationModes.KeepOriginalFolders, WriteTags = false },
    };

    private static IReadOnlyList<string> Errors(LibrariesConfiguration config) =>
        JsonConfigValidator.Validate(config, "libraries.json", LibraryKindsConfiguration.BuiltIn());

    [Theory]
    [InlineData("other_audio", "Music", LibraryAreas.Listen, "Recordings")]
    [InlineData("other_reading", "Books", LibraryAreas.Read, "Loose books")]
    public void ACataloguedPlainLibraryWithLocalOnlyIsAccepted(string kindKey, string mediaType, string area, string name)
    {
        var errors = Errors(ShippedWith(Plain(kindKey, mediaType, area, name)));

        Assert.DoesNotContain(errors, error => !error.Contains("absolute path"));
    }

    [Theory]
    [InlineData("other_audio", "Music", LibraryAreas.Listen, "Recordings")]
    [InlineData("other_reading", "Books", LibraryAreas.Read, "Loose books")]
    public void APlainLibraryWithEnrichedMetadataIsRefusedAndToldWhy(string kindKey, string mediaType, string area, string name)
    {
        var library = Plain(kindKey, mediaType, area, name);
        library.MetadataPolicy = LibraryMetadataPolicies.Enriched;

        var errors = Errors(ShippedWith(library));

        var message = Assert.Single(errors, error => error.Contains("never looks anything up", StringComparison.Ordinal));
        Assert.Contains($"libraries[{Load("config", "libraries.json").Libraries.Count}]", message, StringComparison.Ordinal);
        Assert.Contains("metadata_policy is \"enriched\". Use local_only.", message, StringComparison.Ordinal);
        Assert.DoesNotContain(errors, error => error.Contains("must use enriched metadata", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOtherVideosLibraryWhoseYoutubeLookupIsTrueIsRefusedInPlainWords()
    {
        var config = Load("config", "libraries.json");
        var index = config.Libraries.FindIndex(library => library.KindKey == "other_videos");
        config.Libraries[index].YoutubeLookup = true;

        var errors = Errors(config);

        Assert.Contains(
            $"libraries[{index}] is a Videos library, which never looks anything up, but youtube_lookup is true. Set youtube_lookup to false.",
            errors);
    }

    [Fact]
    public void ALibraryOfAKindOnlyAPluginCouldSupply_IsNotHeldToEnrichedByThisFile()
    {
        // The file cannot see a plugin's kinds; the Engine checks the library against the full list where it is saved.
        var library = Plain("podcasts", "Music", LibraryAreas.Listen, "Shows");

        var errors = Errors(ShippedWith(library));

        Assert.DoesNotContain(errors, error => !error.Contains("absolute path"));
    }

    [Fact]
    public void ALookupKindStillNeedsEnrichedMetadata()
    {
        var config = Load("config", "libraries.json");
        config.Libraries.Single(library => library.KindKey == "music").MetadataPolicy = LibraryMetadataPolicies.LocalOnly;

        Assert.Contains(Errors(config), error => error.Contains("must use enriched metadata", StringComparison.Ordinal));
    }

    [Fact]
    public void AVideosLibraryStillHasToBeALibraryOfItsOwn()
    {
        var config = Load("config", "libraries.json");
        config.Libraries.Single(library => library.KindKey == "other_videos").MediaTypes = ["Videos", "Movies"];

        Assert.Contains(Errors(config), error => error.Contains("Videos must be a library of its own", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("config")]
    [InlineData("docker/config")]
    public void TheShippedLibrariesFilesAgreeWithTheirKinds(string folder)
    {
        var config = Load([.. folder.Split('/'), "libraries.json"]);

        var errors = Errors(config).Where(error => !error.Contains("absolute path")).ToList();

        Assert.Empty(errors);
        Assert.All(
            config.Libraries.Where(library => library.KindKey == "other_videos"),
            library =>
            {
                Assert.Equal(LibraryMetadataPolicies.LocalOnly, library.MetadataPolicy);
                Assert.False(library.YoutubeLookup);
            });
    }

    [Fact]
    public void TheFourKindsThatWereHiddenAreAvailableInTheShippedKindsFile()
    {
        var kinds = JsonSerializer.Deserialize<LibraryKindsConfiguration>(File.ReadAllText(RepoFile("config", "library_kinds.json")))!.Kinds;

        Assert.All(new[] { "youtube", "other_videos", "other_audio", "other_reading" }, key => Assert.True(kinds.Single(kind => kind.Key == key).Available, key));
        Assert.Equal(["youtube"], kinds.Single(kind => kind.Key == "youtube").Lookup.Providers);
        Assert.All(new[] { "other_videos", "other_audio", "other_reading" }, key => Assert.True(kinds.Single(kind => kind.Key == key).Lookup.IsNone, key));
        Assert.Equal("Music", kinds.Single(kind => kind.Key == "other_audio").Format);
        Assert.Equal("Books", kinds.Single(kind => kind.Key == "other_reading").Format);
        Assert.Equal("Videos", kinds.Single(kind => kind.Key == "youtube").Format);
        Assert.Equal("Videos", kinds.Single(kind => kind.Key == "other_videos").Format);
    }
}
