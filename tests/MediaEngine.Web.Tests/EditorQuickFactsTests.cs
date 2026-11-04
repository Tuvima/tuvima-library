using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>What the Quick Facts panel shows for each kind of item (universal editor v4, section 3a, D4).</summary>
public sealed class EditorQuickFactsTests
{
    private static Func<string[], string?> Fields(params (string Key, string Value)[] values)
    {
        var map = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return keys => keys.Select(key => map.GetValueOrDefault(key)).FirstOrDefault(value => value is not null);
    }

    private static string[] Keys(QuickFactsModel model) => model.Facts.Select(fact => fact.Key).ToArray();

    private static string ValueOf(QuickFactsModel model, string key) => model.Facts.Single(fact => fact.Key == key).Value;

    private static string LabelOf(QuickFactsModel model, string key) => model.Facts.Single(fact => fact.Key == key).Label;

    [Fact]
    public void AFilm_ShowsRuntimeYearSizeAndQuality()
    {
        var model = EditorQuickFactsBuilder.Build(
            QuickFactsKind.Film,
            Fields(("runtime", "2h 35m"), ("year", "2021")),
            new ItemQuickFactsDto { FileCount = 2, SizeBytes = 62_500_000_000, BestQuality = "4K HDR" },
            fileWriteOff: false);

        Assert.Equal(["runtime", "year", "size", "quality"], Keys(model));
        Assert.Equal("2h 35m", ValueOf(model, "runtime"));
        Assert.Equal("58.2 GB", ValueOf(model, "size"));
        Assert.Equal("2 files", LabelOf(model, "size"));
        Assert.Equal("4K HDR", ValueOf(model, "quality"));
    }

    [Fact]
    public void AFilmSeries_ShowsFilmsOwnedTotalRuntimeAndSize()
    {
        var totals = new ItemQuickFactsDto
        {
            ItemCount = 3,
            RuntimeSeconds = 3 * 7200,
            SizeBytes = 30L * 1024 * 1024 * 1024,
            FileCount = 3,
            CountsByKind = [new ItemQuickFactCountDto { Label = "Films", Count = 3 }],
        };
        Assert.Equal(QuickFactsKind.FilmSeries, EditorQuickFactsBuilder.KindOf(null, null, EditorSubjectKinds.Series, totals));

        var model = EditorQuickFactsBuilder.Build(QuickFactsKind.FilmSeries, Fields(), totals, false);

        Assert.Equal(["films", "runtime", "size"], Keys(model));
        Assert.Equal("3", ValueOf(model, "films"));
        Assert.Equal("6h 0m", ValueOf(model, "runtime"));
    }

    [Fact]
    public void AShow_ShowsSeasonsEpisodesOwnedOfTotalAndSize()
    {
        var totals = new ItemQuickFactsDto
        {
            DirectOwned = 3,
            DirectTotal = 3,
            LeafOwned = 28,
            LeafTotal = 30,
            FileCount = 28,
            SizeBytes = 40L * 1024 * 1024 * 1024,
        };

        var model = EditorQuickFactsBuilder.Build(QuickFactsKind.Show, Fields(), totals, false);

        Assert.Equal(["seasons", "episodes", "size"], Keys(model));
        Assert.Equal("3", ValueOf(model, "seasons"));
        Assert.Equal("28 of 30", ValueOf(model, "episodes"));
        Assert.Equal("40 GB", ValueOf(model, "size"));
    }

    [Fact]
    public void ASeason_ShowsEpisodesOwnedOfTotalRuntimeAndSize()
    {
        var totals = new ItemQuickFactsDto
        {
            DirectOwned = 13,
            DirectTotal = 13,
            LeafOwned = 13,
            LeafTotal = 13,
            RuntimeSeconds = 13 * 2520,
            FileCount = 13,
            SizeBytes = 18L * 1024 * 1024 * 1024,
        };

        var model = EditorQuickFactsBuilder.Build(QuickFactsKind.Season, Fields(), totals, false);

        Assert.Equal(["episodes", "runtime", "size"], Keys(model));
        Assert.Equal("13", ValueOf(model, "episodes"));
        Assert.Equal("9h 6m", ValueOf(model, "runtime"));
    }

    [Fact]
    public void ASeasonMissingEpisodes_SaysHowManyAreOwned()
    {
        var model = EditorQuickFactsBuilder.Build(
            QuickFactsKind.Season, Fields(), new ItemQuickFactsDto { DirectOwned = 11, DirectTotal = 13 }, false);

        Assert.Equal("11 of 13", ValueOf(model, "episodes"));
    }

    [Fact]
    public void AnEpisode_ShowsAirDateRuntimeSizeAndQuality()
    {
        var model = EditorQuickFactsBuilder.Build(
            QuickFactsKind.Episode,
            Fields(("air_date", "8 Feb 2017"), ("runtime", "42 min")),
            new ItemQuickFactsDto { FileCount = 1, SizeBytes = 1_500_000_000, BestQuality = "1080p" },
            false);

        Assert.Equal(["air_date", "runtime", "size", "quality"], Keys(model));
        Assert.Equal("8 Feb 2017", ValueOf(model, "air_date"));
        Assert.Equal("1.4 GB", ValueOf(model, "size"));
        Assert.Equal("1 file", LabelOf(model, "size"));
    }

    [Fact]
    public void ABook_ShowsPagesEditionIsbnAndSize()
    {
        var model = EditorQuickFactsBuilder.Build(
            QuickFactsKind.Book,
            Fields(("page_count", "412"), ("edition_format", "Paperback"), ("isbn", "9780441013593")),
            new ItemQuickFactsDto { FileCount = 1, SizeBytes = 2_000_000 },
            false);

        Assert.Equal(["pages", "edition", "isbn", "size"], Keys(model));
        Assert.Equal("412", ValueOf(model, "pages"));
        Assert.Equal("9780441013593", ValueOf(model, "isbn"));
    }

    [Fact]
    public void AnAudiobook_ShowsNarratorLengthFilesAndSize()
    {
        var model = EditorQuickFactsBuilder.Build(
            QuickFactsKind.Audiobook,
            Fields(("narrator", "Scott Brick")),
            new ItemQuickFactsDto { FileCount = 12, SizeBytes = 900_000_000, RuntimeSeconds = 21 * 3600 },
            false);

        Assert.Equal(["narrator", "length", "files", "size"], Keys(model));
        Assert.Equal("Scott Brick", ValueOf(model, "narrator"));
        Assert.Equal("21h 0m", ValueOf(model, "length"));
        Assert.Equal("12", ValueOf(model, "files"));
    }

    [Fact]
    public void AComicIssue_ShowsIssueNumberPagesAndSize()
    {
        var model = EditorQuickFactsBuilder.Build(
            QuickFactsKind.ComicIssue,
            Fields(("issue_number", "12"), ("page_count", "32")),
            new ItemQuickFactsDto { FileCount = 1, SizeBytes = 80_000_000 },
            false);

        Assert.Equal(["issue", "pages", "size"], Keys(model));
        Assert.Equal("12", ValueOf(model, "issue"));
    }

    [Theory]
    [InlineData("Comics", "Issues owned")]
    [InlineData("Books", "Books owned")]
    [InlineData("Audiobooks", "Audiobooks owned")]
    public void AComicOrBookSeries_ShowsHowManyAreOwnedAndTheSize(string mediaType, string label)
    {
        var totals = new ItemQuickFactsDto { DirectOwned = 8, DirectTotal = 10, FileCount = 8, SizeBytes = 400_000_000 };
        Assert.Equal(QuickFactsKind.Series, EditorQuickFactsBuilder.KindOf(mediaType, "series", null, totals));

        var model = EditorQuickFactsBuilder.Build(QuickFactsKind.Series, Fields(), totals, false, mediaType);

        Assert.Equal(["owned", "size"], Keys(model));
        Assert.Equal("8 of 10", ValueOf(model, "owned"));
        Assert.Equal(label, LabelOf(model, "owned"));
    }

    [Fact]
    public void ASeriesRecord_ShowsItsOwnedTitlesFromTheEnginesCount()
    {
        var totals = new ItemQuickFactsDto
        {
            ItemCount = 5,
            FileCount = 5,
            SizeBytes = 10_000_000,
            CountsByKind = [new ItemQuickFactCountDto { Label = "Books", Count = 5 }],
        };

        var model = EditorQuickFactsBuilder.Build(QuickFactsKind.Series, Fields(), totals, false);

        Assert.Equal("5", ValueOf(model, "owned"));
        Assert.Equal("Books owned", LabelOf(model, "owned"));
    }

    [Fact]
    public void AnAlbum_ShowsTracksOwnedOfTotalLengthAndSize()
    {
        var model = EditorQuickFactsBuilder.Build(
            QuickFactsKind.Album,
            Fields(),
            new ItemQuickFactsDto { LeafOwned = 10, LeafTotal = 12, DirectOwned = 10, DirectTotal = 12, RuntimeSeconds = 2700, FileCount = 10, SizeBytes = 300_000_000 },
            false);

        Assert.Equal(["tracks", "length", "size"], Keys(model));
        Assert.Equal("10 of 12", ValueOf(model, "tracks"));
        Assert.Equal("45m", ValueOf(model, "length"));
    }

    [Fact]
    public void ASong_ShowsLengthTrackAndDiscAndQuality()
    {
        var model = EditorQuickFactsBuilder.Build(
            QuickFactsKind.Song,
            Fields(("duration", "3m 54s"), ("track_number", "3"), ("disc_number", "1")),
            new ItemQuickFactsDto { FileCount = 1, BestQuality = "FLAC" },
            false);

        Assert.Equal(["length", "track", "quality"], Keys(model));
        Assert.Equal("3m 54s", ValueOf(model, "length"));
        Assert.Equal("Track 3 · Disc 1", ValueOf(model, "track"));
        Assert.Equal("FLAC", ValueOf(model, "quality"));
    }

    [Fact]
    public void APersonalVideo_ShowsLengthAndSize()
    {
        var model = EditorQuickFactsBuilder.Build(
            QuickFactsKind.PersonalVideo, Fields(), new ItemQuickFactsDto { FileCount = 1, RuntimeSeconds = 754, SizeBytes = 250_000_000 }, false);

        Assert.Equal(["length", "size"], Keys(model));
        Assert.Equal("13m", ValueOf(model, "length"));
    }

    [Fact]
    public void APerson_ShowsRolesAndWorksInTheLibrary()
    {
        var model = EditorQuickFactsBuilder.Build(
            QuickFactsKind.Person, Fields(), new ItemQuickFactsDto { Roles = ["Author", "Screenwriter"], ItemCount = 7 }, false);

        Assert.Equal(["roles", "works"], Keys(model));
        Assert.Equal("Author, Screenwriter", ValueOf(model, "roles"));
        Assert.Equal("7", ValueOf(model, "works"));
    }

    [Fact]
    public void ACuratedCollection_ShowsItemsAndOwner()
    {
        var model = EditorQuickFactsBuilder.Build(
            QuickFactsKind.CuratedCollection, Fields(), new ItemQuickFactsDto { ItemCount = 24, Owner = "Library" }, false);

        Assert.Equal(["items", "owner"], Keys(model));
        Assert.Equal("24", ValueOf(model, "items"));
        Assert.Equal("Library", ValueOf(model, "owner"));
    }

    [Fact]
    public void AUniverse_ShowsWorksInTheLibraryAndCountsPerKind()
    {
        var totals = new ItemQuickFactsDto
        {
            ItemCount = 14,
            CountsByKind =
            [
                new ItemQuickFactCountDto { Label = "Books", Count = 8 },
                new ItemQuickFactCountDto { Label = "Films", Count = 4 },
                new ItemQuickFactCountDto { Label = "Shows", Count = 2 },
            ],
        };

        var model = EditorQuickFactsBuilder.Build(QuickFactsKind.Universe, Fields(), totals, false);

        Assert.Equal(["works", "kind_books", "kind_films", "kind_shows"], Keys(model));
        Assert.Equal("14", ValueOf(model, "works"));
        Assert.Equal("8", ValueOf(model, "kind_books"));
    }

    [Fact]
    public void AFactNeitherTheFieldsNorTheEngineKnows_IsLeftOutNotGuessed()
    {
        var model = EditorQuickFactsBuilder.Build(QuickFactsKind.Film, Fields(("runtime", "2h")), null, false);

        Assert.Equal(["runtime"], Keys(model));
    }

    [Fact]
    public void AnItemWithNothingToShow_HasNoQuickFacts()
    {
        Assert.True(EditorQuickFactsBuilder.Build(QuickFactsKind.None, Fields(), null, true).IsEmpty);
        Assert.True(EditorQuickFactsBuilder.Build(QuickFactsKind.Film, Fields(), null, false).IsEmpty);
    }

    [Fact]
    public void AReadOnlyLibrary_AndFileWriteBackOff_AreNamedPlainly()
    {
        var model = EditorQuickFactsBuilder.Build(
            QuickFactsKind.Film, Fields(("runtime", "2h")), new ItemQuickFactsDto { ReadOnlyLibrary = true }, fileWriteOff: true);

        Assert.Equal([EditorQuickFactsBuilder.ReadOnlyLibraryNote, EditorQuickFactsBuilder.WriteBackOffNote], model.Notes);
    }

    [Fact]
    public void NoNotes_WhenTheLibraryIsWritableAndWriteBackIsOnOrUnknown()
    {
        var model = EditorQuickFactsBuilder.Build(
            QuickFactsKind.Film, Fields(("runtime", "2h")), new ItemQuickFactsDto { ReadOnlyLibrary = false }, fileWriteOff: false);

        Assert.Empty(model.Notes);
    }

    [Theory]
    [InlineData("Movies", "movie", null, QuickFactsKind.Film)]
    [InlineData("Movies", "item", null, QuickFactsKind.Film)]
    [InlineData("TV", "series", null, QuickFactsKind.Show)]
    [InlineData("TV", "season", null, QuickFactsKind.Season)]
    [InlineData("TV", "episode", null, QuickFactsKind.Episode)]
    [InlineData("Books", "book", null, QuickFactsKind.Book)]
    [InlineData("Audiobooks", "audiobook", null, QuickFactsKind.Audiobook)]
    [InlineData("Comics", "issue", null, QuickFactsKind.ComicIssue)]
    [InlineData("Comics", "series", null, QuickFactsKind.Series)]
    [InlineData("Music", "album", null, QuickFactsKind.Album)]
    [InlineData("Music", "track", null, QuickFactsKind.Song)]
    [InlineData("Videos", "video", null, QuickFactsKind.PersonalVideo)]
    [InlineData("person", "person", EditorSubjectKinds.Person, QuickFactsKind.Person)]
    [InlineData("collection", "collection", EditorSubjectKinds.Collection, QuickFactsKind.CuratedCollection)]
    [InlineData("universe", "universe", EditorSubjectKinds.Universe, QuickFactsKind.Universe)]
    [InlineData("fictional-entity", "fictional-entity", EditorSubjectKinds.FictionalEntity, QuickFactsKind.Thing)]
    public void TheKindOfItem_ComesFromItsMediaTypeAndScope(string mediaType, string scope, string? subject, QuickFactsKind expected)
    {
        Assert.Equal(expected, EditorQuickFactsBuilder.KindOf(mediaType, scope, subject));
    }

    [Fact]
    public void NoKind_ShowsMoreThanFiveFacts()
    {
        var totals = new ItemQuickFactsDto
        {
            ItemCount = 14,
            CountsByKind = Enumerable.Range(0, 8).Select(i => new ItemQuickFactCountDto { Label = "Kind" + i, Count = i + 1 }).ToList(),
        };

        var model = EditorQuickFactsBuilder.Build(QuickFactsKind.Universe, Fields(), totals, false);

        Assert.InRange(model.Facts.Count, 3, 5);
    }
}
