using MediaEngine.Contracts.Activity;
using MediaEngine.Web.Services.Formatting;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

/// <summary>The words and marks of the Overview's Recent activity strip (D13, D17, D18), tested apart from the components.</summary>
public sealed class PersonalActivityPresentationTests
{
    private static readonly string Key = new('a', 64);
    private static readonly string WorkId = "11111111-2222-3333-4444-555555555555";

    internal static PersonalActivityRecentTileDto Tile(
        string kind,
        long seconds = 0,
        double? progress = null,
        bool finished = false,
        string shape = PersonalActivityShapeValues.Landscape,
        string title = "Tremors",
        string? subtitle = null,
        string? parent = null,
        string? artwork = null,
        IReadOnlyList<string>? covers = null,
        int? songs = null,
        int? albums = null,
        string? workId = null,
        string startedAt = "2026-10-19T14:30:00Z") =>
        new(kind, title, subtitle, parent, startedAt, null, seconds, progress, finished, shape, artwork, covers, songs, albums, workId);

    [Theory]
    [InlineData(0, "under 1 m")]
    [InlineData(29, "under 1 m")]
    [InlineData(30, "1 m")]
    [InlineData(52 * 60, "52 m")]
    [InlineData(60 * 60, "1 h 00 m")]
    [InlineData(65 * 60, "1 h 05 m")]
    [InlineData(80 * 60, "1 h 20 m")]
    [InlineData(3 * 3600 + 12 * 60, "3 h 12 m")]
    public void FormatDuration_ReadsAsMinutesThenHoursAndMinutes(long seconds, string expected)
    {
        Assert.Equal(expected, PersonalActivityPresentation.FormatDuration(seconds));
    }

    [Theory]
    [InlineData(PersonalActivityKindValues.Watch, 52 * 60, "Watched 52 m")]
    [InlineData(PersonalActivityKindValues.Read, 80 * 60, "Read 1 h 20 m")]
    [InlineData(PersonalActivityKindValues.Listen, 3 * 3600 + 12 * 60, "Listened 3 h 12 m")]
    [InlineData(PersonalActivityKindValues.MusicSession, 65 * 60, "Played 1 h 05 m")]
    [InlineData(PersonalActivityKindValues.MyList, 0, "Added to My list")]
    public void Caption_StartsWithAVerbSoItIsNotReadAsTheLengthOfTheItem(string kind, long seconds, string expected)
    {
        Assert.Equal(expected, PersonalActivityPresentation.Caption(Tile(kind, seconds)));
    }

    [Fact]
    public void Caption_ForAnEntryWithNoTimeKeepsJustTheVerb()
    {
        Assert.Equal("Watched", PersonalActivityPresentation.Caption(Tile(PersonalActivityKindValues.Watch, 0)));
    }

    [Fact]
    public void ProgressMark_IsABarWhilePartWayThrough_ACheckWhenFinished_AndNothingForMusicOrMyList()
    {
        Assert.Equal(ActivityProgressMark.Bar, PersonalActivityPresentation.ProgressMark(Tile(PersonalActivityKindValues.Watch, progress: 40)));
        Assert.Equal(ActivityProgressMark.Finished, PersonalActivityPresentation.ProgressMark(Tile(PersonalActivityKindValues.Read, progress: 100, finished: true)));
        Assert.Equal(ActivityProgressMark.Finished, PersonalActivityPresentation.ProgressMark(Tile(PersonalActivityKindValues.Listen, progress: 99.8)));
        Assert.Equal(ActivityProgressMark.None, PersonalActivityPresentation.ProgressMark(Tile(PersonalActivityKindValues.Watch, progress: 0)));
        Assert.Equal(ActivityProgressMark.None, PersonalActivityPresentation.ProgressMark(Tile(PersonalActivityKindValues.Watch, progress: null)));
        Assert.Equal(ActivityProgressMark.None, PersonalActivityPresentation.ProgressMark(Tile(PersonalActivityKindValues.MusicSession, progress: 50, finished: true)));
        Assert.Equal(ActivityProgressMark.None, PersonalActivityPresentation.ProgressMark(Tile(PersonalActivityKindValues.MyList, finished: true)));
    }

    [Fact]
    public void CollageCovers_AreAtMostNine_AndSkipBlankAddresses()
    {
        var covers = Enumerable.Range(0, 12).Select(i => $"/engine-activity-artwork/{i}").Append(" ").ToList();

        var collage = PersonalActivityPresentation.CollageCovers(Tile(PersonalActivityKindValues.MusicSession, covers: covers));

        Assert.Equal(9, collage.Count);
        Assert.Empty(PersonalActivityPresentation.CollageCovers(Tile(PersonalActivityKindValues.MusicSession, covers: null)));
    }

    [Theory]
    [InlineData(50, 12, "50 songs from 12 albums")]
    [InlineData(1, 1, "1 song from 1 album")]
    public void MusicCounts_SayHowManySongsFromHowManyAlbums(int songs, int albums, string expected)
    {
        Assert.Equal(expected, PersonalActivityPresentation.MusicCounts(Tile(PersonalActivityKindValues.MusicSession, songs: songs, albums: albums)));
    }

    [Fact]
    public void MusicCounts_AreOnlyForASession()
    {
        Assert.Null(PersonalActivityPresentation.MusicCounts(Tile(PersonalActivityKindValues.Watch, songs: 4, albums: 2)));
        Assert.Equal("50 songs", PersonalActivityPresentation.MusicCounts(Tile(PersonalActivityKindValues.MusicSession, songs: 50)));
    }

    [Fact]
    public void Href_OpensTheItem_ByWhatWasDone_AndASessionOrOrphanOpensTheActivityPage()
    {
        Assert.Equal($"/details/work/{WorkId}?context=watch", PersonalActivityPresentation.Href(Tile(PersonalActivityKindValues.Watch, workId: WorkId)));
        Assert.Equal($"/details/work/{WorkId}?context=read", PersonalActivityPresentation.Href(Tile(PersonalActivityKindValues.Read, workId: WorkId)));
        Assert.Equal($"/details/work/{WorkId}?context=listen", PersonalActivityPresentation.Href(Tile(PersonalActivityKindValues.Listen, workId: WorkId)));
        Assert.Equal($"/details/work/{WorkId}", PersonalActivityPresentation.Href(Tile(PersonalActivityKindValues.MyList, workId: WorkId)));
        Assert.Equal("/settings/overview/activity", PersonalActivityPresentation.Href(Tile(PersonalActivityKindValues.MusicSession, workId: WorkId)));
        Assert.Equal("/settings/overview/activity", PersonalActivityPresentation.Href(Tile(PersonalActivityKindValues.Watch, workId: null)));
        Assert.Equal("/settings/overview/activity", PersonalActivityPresentation.Href(Tile(PersonalActivityKindValues.Watch, workId: "not-a-guid")));
        Assert.True(PersonalActivityPresentation.OpensItem(Tile(PersonalActivityKindValues.Watch, workId: WorkId)));
        Assert.False(PersonalActivityPresentation.OpensItem(Tile(PersonalActivityKindValues.MusicSession, workId: WorkId)));
    }

    [Fact]
    public void AccessibleName_CarriesTheTitleAndEpisodeThatTheTileDoesNotPrint()
    {
        var name = PersonalActivityPresentation.AccessibleName(Tile(PersonalActivityKindValues.Watch, 52 * 60, subtitle: "S2 E4 · Fear", title: "Tremors"));

        Assert.Equal("Tremors, S2 E4 · Fear, Watched 52 m", name);
    }

    [Fact]
    public void When_SaysTodayAndYesterdayInLocalTime_AndOtherwiseTheDate()
    {
        // Local noon, so an hour either side stays on the same local day whatever the time zone.
        var now = new DateTimeOffset(DateTime.Today.AddHours(12));
        var older = now.AddDays(-18);

        Assert.StartsWith("Today, ", PersonalActivityPresentation.When(now.AddHours(-1).ToString("O"), now));
        Assert.StartsWith("Yesterday, ", PersonalActivityPresentation.When(now.AddDays(-1).ToString("O"), now));
        Assert.StartsWith(older.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture) + ", ", PersonalActivityPresentation.When(older.ToString("O"), now));
        Assert.Equal(string.Empty, PersonalActivityPresentation.When("not a time", now));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(59, null)]
    [InlineData(40 * 60, "40 m")]
    [InlineData(9000, "2.5 h")]
    [InlineData(7200, "2 h")]
    [InlineData(12 * 3600, "12 h")]
    public void FormatHours_LeavesOutNothingWorthShowing_AndReadsInHours(double seconds, string? expected)
    {
        Assert.Equal(expected, PersonalActivityPresentation.FormatHours(seconds));
    }

    [Fact]
    public void TasteSummary_NamesTheTopGenresAndMoods_FallsBackToOpenedGenres_AndIsNullWithNothing()
    {
        var genres = new Dictionary<string, double> { ["Drama"] = 0.4, ["Science fiction"] = 0.9, ["Thriller"] = 0.6, ["Comedy"] = 0.1 };
        var moods = new Dictionary<string, double> { ["Tense"] = 0.8, ["Reflective"] = 0.5, ["Cosy"] = 0.1 };

        Assert.Equal("Mostly Science fiction, Thriller and Drama, in a tense and reflective mood.",
            PersonalActivityPresentation.TasteSummary(genres, moods, null));
        Assert.Equal("Mostly Science fiction, Thriller and Drama.", PersonalActivityPresentation.TasteSummary(genres, null, null));
        Assert.Equal("Mostly Horror and Comedy.",
            PersonalActivityPresentation.TasteSummary(null, null, new Dictionary<string, int> { ["Comedy"] = 2, ["Horror"] = 5 }));
        Assert.Null(PersonalActivityPresentation.TasteSummary(null, null, null));
        Assert.Null(PersonalActivityPresentation.TasteSummary(new Dictionary<string, double>(), null, new Dictionary<string, int>()));
    }

    [Fact]
    public void ActivityPictures_AreServedByTheDashboardAsThePersonWhoOwnsThem()
    {
        Assert.Equal($"/engine-activity-artwork/{Key}", PersonalActivityArtworkPath.ToBrowserUrl($"/api/v1/activity/artwork/{Key}"));
        Assert.Equal($"/engine-activity-artwork/{Key}?scope=household", PersonalActivityArtworkPath.ToBrowserUrl($"/api/v1/activity/artwork/{Key}?scope=household"));
        Assert.Equal($"/api/v1/activity/artwork/{Key}", PersonalActivityArtworkPath.EnginePath(Key));

        // Anything that is not a well-formed activity picture address is never handed to the browser.
        Assert.Null(PersonalActivityArtworkPath.ToBrowserUrl(null));
        Assert.Null(PersonalActivityArtworkPath.ToBrowserUrl("/api/v1/activity/artwork/short"));
        Assert.Null(PersonalActivityArtworkPath.ToBrowserUrl($"/api/v1/activity/artwork/{new string('A', 64)}"));
        Assert.Null(PersonalActivityArtworkPath.ToBrowserUrl($"/api/v1/activity/artwork/../{Key}"));
        Assert.Null(PersonalActivityArtworkPath.ToBrowserUrl("https://example.test/picture.jpg"));
        Assert.False(PersonalActivityArtworkPath.IsValidKey("../../etc/passwd"));
    }

    [Fact]
    public void ForBrowser_TurnsEveryPictureAddressOfATileIntoADashboardAddress()
    {
        var tile = Tile(
            PersonalActivityKindValues.MusicSession,
            artwork: $"/api/v1/activity/artwork/{Key}",
            covers: [$"/api/v1/activity/artwork/{Key}", "https://elsewhere.test/x.jpg"]);

        var mapped = PersonalActivityArtworkPath.ForBrowser(tile);

        Assert.Equal($"/engine-activity-artwork/{Key}", mapped.ArtworkUrl);
        Assert.Equal([$"/engine-activity-artwork/{Key}"], mapped.CoverUrls);
    }
}
