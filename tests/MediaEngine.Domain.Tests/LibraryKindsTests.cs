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
    public void TheShippedKindsListWatchListenReadAndViewInOrderWithEveryKindAvailable()
    {
        var kinds = LibraryKindsConfiguration.BuiltIn();

        Assert.Equal(
            ["movies", "tv", "youtube", "other_videos", "music", "audiobooks", "other_audio", "books", "comics", "other_reading", "photos", "personal_videos", "documents"],
            kinds.Select(kind => kind.Key).ToArray());
        Assert.All(kinds, kind => Assert.True(kind.Available, kind.Key));
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
        Assert.Equal(["Photos", "Personal videos", "Documents"], kinds.Where(pair => pair.Value.Area == LibraryAreas.View).Select(pair => pair.Value.Label).ToArray());
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
    public void TheWholeFileCheckLeavesALibraryWithNoKindAlone_LikeTheVaultEntries_ButASaveStillNeedsOne()
    {
        var kinds = LibraryKindsConfiguration.BuiltIn();
        var vaultEntries = Libraries(Library("", "Videos"), Library("", "Videos"), Library("movies", "Movies"));

        Assert.Empty(LibraryKindRules.LibraryProblems(vaultEntries, kinds, allowUnknownKeys: true, allowMissingKeys: true));
        Assert.Equal(2, LibraryKindRules.LibraryProblems(vaultEntries, kinds).Count(problem => problem.Contains("kind_key", StringComparison.Ordinal)));
    }

    [Fact]
    public void OnlyThePlainKindsAllowSeveralLibrariesAndUpToFive()
    {
        var kinds = LibraryKindsConfiguration.BuiltIn();

        Assert.Equal(
            ["other_videos", "other_audio", "other_reading"],
            kinds.Where(kind => kind.AllowsMultiple).Select(kind => kind.Key).ToArray());
        Assert.All(kinds.Where(kind => kind.AllowsMultiple), kind => Assert.Equal(5, kind.MaxLibraries));
        Assert.All(kinds.Where(kind => !kind.AllowsMultiple), kind => Assert.Equal(1, kind.MaxLibraries));
    }

    [Fact]
    public void AKindWithMoreThanOneLibraryAllowedButNotAllowingSeveralIsRefused()
    {
        var kind = Podcasts();
        kind.MaxLibraries = 3;

        Assert.Contains("allows_multiple", LibraryKindRules.Problem(kind, "kinds[0]"), StringComparison.Ordinal);

        kind.AllowsMultiple = true;
        Assert.Null(LibraryKindRules.Problem(kind, "kinds[0]"));
    }

    [Fact]
    public void SeveralNamedLibrariesOfAPlainKindAreFineUpToTheLimit()
    {
        var kinds = LibraryKindsConfiguration.BuiltIn();
        var named = new[] { "Lectures", "Family recordings", "Talks", "Courses", "Clips" }
            .Select(name => Library("other_videos", "Videos", name, LibraryAreas.Watch))
            .ToArray();

        Assert.Empty(LibraryKindRules.LibraryProblems(Libraries(named), kinds));

        var sixth = Libraries([.. named, Library("other_videos", "Videos", "Concerts", LibraryAreas.Watch)]);
        Assert.Contains("allows at most 5", Assert.Single(LibraryKindRules.LibraryProblems(sixth, kinds)), StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondLibraryOfAKindThatHoldsOneIsRefused()
    {
        var kinds = LibraryKindsConfiguration.BuiltIn();
        var configuration = Libraries(
            Library("movies", "Movies", "Movies", LibraryAreas.Watch),
            Library("movies", "Movies", "Old films", LibraryAreas.Watch));

        Assert.Contains("holds one library", Assert.Single(LibraryKindRules.LibraryProblems(configuration, kinds)), StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryNamesAreUniqueInAnAreaIgnoringCaseAndNeverAnotherKindsLabel()
    {
        var kinds = LibraryKindsConfiguration.BuiltIn();
        var duplicate = Libraries(
            Library("other_videos", "Videos", "Lectures", LibraryAreas.Watch),
            Library("other_videos", "Videos", "LECTURES", LibraryAreas.Watch));
        var otherArea = Libraries(
            Library("other_videos", "Videos", "Lectures", LibraryAreas.Watch),
            Library("other_audio", "Music", "Lectures", LibraryAreas.Listen));
        var kindLabel = Libraries(Library("other_videos", "Videos", "Movies", LibraryAreas.Watch));
        var ownLabel = Libraries(Library("other_videos", "Videos", "Videos", LibraryAreas.Watch));

        Assert.Contains("already used", Assert.Single(LibraryKindRules.LibraryProblems(duplicate, kinds)), StringComparison.Ordinal);
        Assert.Empty(LibraryKindRules.LibraryProblems(otherArea, kinds));
        Assert.Contains("name of the library kind", Assert.Single(LibraryKindRules.LibraryProblems(kindLabel, kinds)), StringComparison.Ordinal);
        Assert.Empty(LibraryKindRules.LibraryProblems(ownLabel, kinds));
    }

    [Fact]
    public void TheImportFolderTypesAndFoldersComeFromTheAvailableKindsWithAFormatInTheOrderTheyHaveAlwaysHad()
    {
        Assert.Equal(
            ["Books", "Audiobooks", "Movies", "TV Shows", "Music", "Comics", "Videos"],
            ImportFolderRules.Types.Select(type => type.Label).ToArray());
        Assert.Equal(["Movies", "TV", "Music", "Books", "Audiobooks", "Comics", "Videos"], ImportFolderRules.Subfolders);
        Assert.DoesNotContain(ImportFolderRules.Types, type => type.Label is "YouTube" or "Other audio" or "Other reading");
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

    private static LibraryFolderConfig Library(string kindKey, string mediaType, string? name = null, string area = LibraryAreas.Read) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Name = name ?? mediaType,
        Kind = LibraryKinds.Catalogued,
        KindKey = kindKey,
        Area = area,
        MediaTypes = [mediaType],
        // A plain or YouTube kind is local only; the plain kinds never ask YouTube and the YouTube kind always does.
        MetadataPolicy = kindKey is "other_videos" or "other_audio" or "other_reading" or "youtube" ? LibraryMetadataPolicies.LocalOnly : LibraryMetadataPolicies.Enriched,
        YoutubeLookup = kindKey == "youtube",
    };

    // ── The kind decides what a library may look up (Library setup D17, D18, D38) ──

    [Theory]
    [InlineData("other_videos")]
    [InlineData("other_audio")]
    [InlineData("other_reading")]
    public void APlainKindNeverLooksAnythingUp_WhateverItsLibraryFieldsSay(string kindKey)
    {
        var library = Library(kindKey, "Videos");
        library.MetadataPolicy = LibraryMetadataPolicies.Enriched;
        library.YoutubeLookup = true;

        var effective = LibraryKindLookupPolicy.For(library, LibraryKindsConfiguration.BuiltIn());

        Assert.Equal(LibraryLookupMode.Plain, effective.Mode);
        Assert.True(effective.BypassesExternalIdentity);
        Assert.False(effective.YoutubeLookup);
        Assert.Equal(LibraryMetadataPolicies.LocalOnly, effective.MetadataPolicy);
    }

    [Fact]
    public void TheYoutubeKindKeepsIdentityLocalAndAsksYoutube_WhateverItsLibraryFieldsSay()
    {
        var library = Library("youtube", "Videos");
        library.MetadataPolicy = LibraryMetadataPolicies.Enriched;
        library.YoutubeLookup = false;

        var effective = LibraryKindLookupPolicy.For(library, LibraryKindsConfiguration.BuiltIn());

        Assert.Equal(LibraryLookupMode.YouTube, effective.Mode);
        Assert.True(effective.BypassesExternalIdentity);
        Assert.True(effective.YoutubeLookup);
    }

    [Theory]
    [InlineData("movies", "Movies")]
    [InlineData("tv", "TV")]
    [InlineData("music", "Music")]
    [InlineData("audiobooks", "Audiobooks")]
    [InlineData("books", "Books")]
    [InlineData("comics", "Comics")]
    public void ALookupKindBehavesFromItsOwnFields_AsItAlwaysHas(string kindKey, string mediaType)
    {
        var enriched = Library(kindKey, mediaType);
        var local = Library(kindKey, mediaType);
        local.MetadataPolicy = LibraryMetadataPolicies.LocalOnly;

        var kinds = LibraryKindsConfiguration.BuiltIn();

        Assert.Equal(LibraryLookupMode.FromFields, LibraryKindLookupPolicy.For(enriched, kinds).Mode);
        Assert.False(LibraryKindLookupPolicy.BypassesExternalIdentity(enriched, kinds));
        Assert.True(LibraryKindLookupPolicy.BypassesExternalIdentity(local, kinds));
    }

    [Fact]
    public void ALibraryWithNoKindFollowsItsOwnFields_ButAnUnknownKindLooksNothingUp()
    {
        var kinds = LibraryKindsConfiguration.BuiltIn();
        var vault = Library("", "Videos");
        vault.MetadataPolicy = LibraryMetadataPolicies.LocalOnly;
        vault.YoutubeLookup = true;
        var unknown = Library("recorded_tv", "Videos");

        var vaultLookup = LibraryKindLookupPolicy.For(vault, kinds);

        Assert.Equal(LibraryLookupMode.FromFields, vaultLookup.Mode);
        Assert.True(vaultLookup.YoutubeLookup);
        Assert.True(vaultLookup.BypassesExternalIdentity);
        var unknownLookup = LibraryKindLookupPolicy.For(unknown, kinds);
        Assert.Equal(LibraryLookupMode.Plain, unknownLookup.Mode);
        Assert.True(unknownLookup.BypassesExternalIdentity);
        Assert.False(unknownLookup.YoutubeLookup);
    }

    [Fact]
    public void APluginKindWithNoLookupIsPlainToo()
    {
        var kinds = LibraryKindsConfiguration.BuiltIn();
        kinds.Add(Podcasts());

        Assert.Equal(LibraryLookupMode.Plain, LibraryKindLookupPolicy.ModeOf("podcasts", kinds));
    }

    [Fact]
    public void WithoutAKindsListAPlainKindIsStillPlain()
    {
        Assert.Equal(LibraryLookupMode.Plain, LibraryKindLookupPolicy.ModeOf("other_audio", null));
    }

    [Fact]
    public void ALibraryThatDisagreesWithItsKind_IsDescribedInPlainWords_ForTheLoadTimeWarning()
    {
        var kinds = LibraryKindsConfiguration.BuiltIn();
        Assert.Empty(LibraryKindRules.LookupContradictions(Library("other_audio", "Music", "Recordings", LibraryAreas.Listen), kinds, "libraries[0]"));

        var enriched = Library("other_audio", "Music", "Recordings", LibraryAreas.Listen);
        enriched.MetadataPolicy = LibraryMetadataPolicies.Enriched;
        var lectures = Library("other_videos", "Videos", "Lectures", LibraryAreas.Watch);
        lectures.YoutubeLookup = true;
        var off = Library("youtube", "Videos");
        off.YoutubeLookup = false;

        Assert.Equal(
            "libraries[0] is an Other audio library, which never looks anything up, but metadata_policy is \"enriched\". Use local_only.",
            Assert.Single(LibraryKindRules.LookupContradictions(enriched, kinds, "libraries[0]")));
        Assert.Equal(
            "libraries[2] is a Videos library, which never looks anything up, but youtube_lookup is true. Set youtube_lookup to false.",
            Assert.Single(LibraryKindRules.LookupContradictions(lectures, kinds, "libraries[2]")));
        Assert.Contains("youtube_lookup is false", Assert.Single(LibraryKindRules.LookupContradictions(off, kinds, "libraries[3]")), StringComparison.Ordinal);
    }

    [Fact]
    public void AContradictionWithTheKindIsNotAProblemForALibrariesCheck_ItIsNormalisedAwayInstead()
    {
        var kinds = LibraryKindsConfiguration.BuiltIn();
        var enriched = Library("other_audio", "Music", "Recordings", LibraryAreas.Listen);
        enriched.MetadataPolicy = LibraryMetadataPolicies.Enriched;
        enriched.YoutubeLookup = true;
        var youtubeOff = Library("youtube", "Videos");
        youtubeOff.YoutubeLookup = false;
        youtubeOff.MetadataPolicy = LibraryMetadataPolicies.Enriched;
        var movies = Library("movies", "Movies");
        var vault = Library("", "Videos");
        vault.MetadataPolicy = LibraryMetadataPolicies.LocalOnly;
        vault.YoutubeLookup = true;
        var libraries = Libraries(enriched, youtubeOff, movies, vault);

        Assert.Empty(LibraryKindRules.LibraryProblems(libraries, kinds, allowMissingKeys: true));
        var changed = LibraryKindLookupPolicy.Normalise(libraries, kinds);

        Assert.Equal(2, changed);
        Assert.Equal((LibraryMetadataPolicies.LocalOnly, false), (enriched.MetadataPolicy, enriched.YoutubeLookup));
        Assert.Equal((LibraryMetadataPolicies.LocalOnly, true), (youtubeOff.MetadataPolicy, youtubeOff.YoutubeLookup));
        Assert.Equal(LibraryMetadataPolicies.Enriched, movies.MetadataPolicy);
        Assert.Equal((LibraryMetadataPolicies.LocalOnly, true), (vault.MetadataPolicy, vault.YoutubeLookup));
        Assert.Equal(0, LibraryKindLookupPolicy.Normalise(libraries, kinds));
    }
}
