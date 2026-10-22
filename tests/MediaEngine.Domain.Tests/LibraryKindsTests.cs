using System.Text.Json;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Tests;

public sealed class LibraryKindsTests
{
    private static readonly LibraryKindKnowledge Knowledge = new(
        new HashSet<string>(["tmdb", "youtube"], StringComparer.Ordinal),
        new HashSet<string>(["Music", "Movies", "default"], StringComparer.OrdinalIgnoreCase));

    private static LibraryKindDefinition Podcasts() => new()
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
        DefaultHandling = LibraryKindHandling.ReadOnly,
        Tooltip = "Episodes of shows you follow.",
        Order = 100,
    };

    [Fact]
    public void TheShippedKindsListWatchListenReadAndViewInOrderWithTheNewKindsHidden()
    {
        var kinds = LibraryKindsConfiguration.BuiltIn();

        Assert.Equal(
            ["movies", "tv", "youtube", "other_videos", "music", "audiobooks", "other_audio", "books", "comics", "other_reading", "photos"],
            kinds.Select(kind => kind.Key).ToArray());
        Assert.Equal(
            ["youtube", "other_audio", "other_reading"],
            kinds.Where(kind => !kind.Available).Select(kind => kind.Key).ToArray());
        Assert.All(kinds, kind => Assert.True(kind.BuiltIn));
        Assert.Empty(LibraryKindRules.Problems(kinds));
    }

    [Fact]
    public void TheShippedKindsNameOnlyKnownGroupingRulesFormatsAndAreas()
    {
        foreach (var kind in LibraryKindsConfiguration.BuiltIn())
        {
            Assert.True(LibraryKindGroupings.IsValid(kind.Grouping), kind.Key);
            Assert.True(LibraryAreas.IsValid(kind.Area), kind.Key);
            Assert.True(kind.Format is null || LibraryKindFormats.IsValid(kind.Format), kind.Key);
        }
    }

    [Fact]
    public void YoutubeAndOtherVideosShareTheVideosFormatButOnlyYoutubeLooksThingsUp()
    {
        var kinds = LibraryKindsConfiguration.BuiltIn().ToDictionary(kind => kind.Key);

        Assert.Equal("Videos", kinds["youtube"].Format);
        Assert.Equal("Videos", kinds["other_videos"].Format);
        Assert.Equal(["youtube"], kinds["youtube"].Lookup.Providers);
        Assert.True(kinds["other_videos"].Lookup.IsNone);
        Assert.Equal(LibraryKindMisfits.Unmatched, kinds["youtube"].Misfit);
        Assert.Equal(LibraryKindMisfits.Keep, kinds["other_videos"].Misfit);
        Assert.Null(kinds["photos"].Format);
    }

    [Fact]
    public void ALookupIsWrittenAsNoneOrAProvidersList()
    {
        var kinds = new LibraryKindsConfiguration();

        var json = JsonSerializer.Serialize(kinds);
        var back = JsonSerializer.Deserialize<LibraryKindsConfiguration>(json)!;

        Assert.Contains("\"lookup\":\"none\"", json, StringComparison.Ordinal);
        Assert.Contains("\"lookup\":{\"providers\":[\"tmdb\",\"subdl\"]}", json, StringComparison.Ordinal);
        Assert.Equal(kinds.Kinds.Count, back.Kinds.Count);
        Assert.Equal(["tmdb", "subdl"], back.Kinds[0].Lookup.Providers);
        Assert.True(back.Kinds[3].Lookup.IsNone);
    }

    [Fact]
    public void ALookupThatIsNeitherNoneNorAProvidersObjectIsRefused()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LibraryKindDefinition>("{\"key\":\"x\",\"lookup\":\"tmdb\"}"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LibraryKindDefinition>("{\"key\":\"x\",\"lookup\":{\"sources\":[\"tmdb\"]}}"));
    }

    [Fact]
    public void AValidPluginStyleKindHasNoProblem()
    {
        Assert.Null(LibraryKindRules.Problem(Podcasts(), "kind", Knowledge));
    }

    [Theory]
    [InlineData("area", "radio", "area")]
    [InlineData("grouping", "by_colour", "grouping")]
    [InlineData("key", "Podcasts!", "key")]
    [InlineData("format", "Podcasts", "format")]
    [InlineData("misfit", "delete", "misfit")]
    [InlineData("default_handling", "wild", "default_handling")]
    [InlineData("naming_template", "nowhere", "naming_template")]
    public void AKindWithAnUnknownValueIsRefusedWithASentenceNamingTheField(string field, string value, string expected)
    {
        var kind = Podcasts();
        switch (field)
        {
            case "area": kind.Area = value; break;
            case "grouping": kind.Grouping = value; break;
            case "key": kind.Key = value; break;
            case "format": kind.Format = value; break;
            case "misfit": kind.Misfit = value; break;
            case "default_handling": kind.DefaultHandling = value; break;
            case "naming_template": kind.NamingTemplate = value; break;
        }

        var problem = LibraryKindRules.Problem(kind, "library_kinds item 1", Knowledge);

        Assert.NotNull(problem);
        Assert.Contains($"\"{expected}\"", problem, StringComparison.Ordinal);
        Assert.EndsWith(".", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ALookupProviderThatDoesNotExistIsRefusedButOnlyWhenTheProvidersAreKnown()
    {
        var kind = Podcasts();
        kind.Lookup = new LibraryKindLookup { Providers = ["tmdb", "nowhere"] };
        kind.Misfit = LibraryKindMisfits.Unmatched;

        Assert.Contains("\"nowhere\"", LibraryKindRules.Problem(kind, "kind", Knowledge), StringComparison.Ordinal);
        Assert.Null(LibraryKindRules.Problem(kind, "kind"));
    }

    [Fact]
    public void AKindThatSendsMisfitsToUnmatchedNeedsALookup()
    {
        var kind = Podcasts();
        kind.Misfit = LibraryKindMisfits.Unmatched;

        Assert.Contains("no lookup", LibraryKindRules.Problem(kind, "kind", Knowledge), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAViewKindMayHaveNoFormat()
    {
        var kind = Podcasts();
        kind.Format = null;
        Assert.Contains("\"format\"", LibraryKindRules.Problem(kind, "kind", Knowledge), StringComparison.Ordinal);

        kind.Area = LibraryAreas.View;
        kind.NamingTemplate = null;
        Assert.Null(LibraryKindRules.Problem(kind, "kind", Knowledge));
    }

    [Fact]
    public void AKeyUsedTwiceInOneListIsReported()
    {
        var problems = LibraryKindRules.Problems([Podcasts(), Podcasts()]);

        Assert.Equal("The library kind \"podcasts\" is listed more than once.", Assert.Single(problems));
    }

    [Fact]
    public void ACataloguedLibraryMustNameAKindWhoseFormatItHolds()
    {
        var kinds = LibraryKindsConfiguration.BuiltIn();
        var configuration = Libraries(
            Library("movies", "Movies"),
            Library("tv", "TVShows"),
            Library("other_videos", "Videos"));

        Assert.Empty(LibraryKindRules.LibraryProblems(configuration, kinds));

        var missing = LibraryKindRules.LibraryProblems(Libraries(Library("", "Movies")), kinds);
        var unknown = LibraryKindRules.LibraryProblems(Libraries(Library("nowhere", "Movies")), kinds);
        var mismatch = LibraryKindRules.LibraryProblems(Libraries(Library("movies", "Books")), kinds);

        Assert.Contains("kind_key", Assert.Single(missing), StringComparison.Ordinal);
        Assert.Contains("\"nowhere\"", Assert.Single(unknown), StringComparison.Ordinal);
        Assert.Contains("must include Movies", Assert.Single(mismatch), StringComparison.Ordinal);
        Assert.Empty(LibraryKindRules.LibraryProblems(Libraries(Library("nowhere", "Movies")), kinds, allowUnknownKeys: true));
    }

    [Fact]
    public void TheImportFolderTypesAndFoldersComeFromTheAvailableKindsWithAFormat()
    {
        Assert.Equal(
            ["Movies", "TV Shows", "Videos", "Music", "Audiobooks", "Books", "Comics"],
            ImportFolderRules.Types.Select(type => type.Label).ToArray());
        Assert.Equal(["Movies", "TV", "Videos", "Music", "Audiobooks", "Books", "Comics"], ImportFolderRules.Subfolders);
        Assert.Equal(["TV", "TVShows"], ImportFolderRules.Types.Single(type => type.Label == "TV Shows").MediaTypes);
    }

    [Fact]
    public void AKindAddedToTheListGetsAnImportTypeOnlyWhenItsFormatIsNew()
    {
        var kinds = LibraryKindsConfiguration.BuiltIn();
        kinds.Add(Podcasts());
        var comicsLike = Podcasts();
        comicsLike.Key = "magazines";
        comicsLike.Label = "Magazines";
        comicsLike.Area = LibraryAreas.Read;
        comicsLike.Format = "Comics";
        kinds.Add(comicsLike);

        var types = ImportFolderRules.TypesFrom(kinds);

        Assert.DoesNotContain(types, type => type.Label is "Podcasts" or "Magazines");
        Assert.Equal(7, types.Count);
    }

    private static LibrariesConfiguration Libraries(params LibraryFolderConfig[] libraries) => new() { Libraries = [.. libraries] };

    private static LibraryFolderConfig Library(string kindKey, string mediaType) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Name = mediaType,
        Kind = LibraryKinds.Catalogued,
        KindKey = kindKey,
        MediaTypes = [mediaType],
    };
}
