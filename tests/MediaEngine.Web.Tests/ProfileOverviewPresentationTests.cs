using MediaEngine.Contracts.Activity;
using MediaEngine.Contracts.Taste;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Tests;

/// <summary>The figures the Profile pages work out: the stat strip, the 30-day calendar, the donut, the sentence and the monthly shifts.</summary>
public sealed class ProfileOverviewPresentationTests
{
    // A Wednesday, so the window starts on a Thursday and both ends sit mid-week.
    private static readonly DateOnly Today = new(2026, 10, 28);

    [Fact]
    public void TheCalendar_IsWholeMondayToSundayWeeks_WithTheWindowInsideThem()
    {
        var cells = ProfileOverviewPresentation.Calendar([], Today);

        Assert.Equal(DayOfWeek.Monday, cells[0].Date.DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, cells[^1].Date.DayOfWeek);
        Assert.Equal(0, cells.Count % 7);
        Assert.Equal(30, cells.Count(cell => cell.InWindow));
        Assert.Equal(Today, cells.Last(cell => cell.InWindow).Date);
        Assert.Equal(Today.AddDays(-29), cells.First(cell => cell.InWindow).Date);
        Assert.All(cells.Where(cell => !cell.InWindow), cell => Assert.Equal(0, cell.Level));
    }

    [Fact]
    public void TheCalendar_GivesBusierDaysADarkerLevel_AndAnyActivityAtLeastLevelOne()
    {
        var daily = new List<PersonalActivityDayDto>
        {
            new(Today.ToString("yyyy-MM-dd"), 4000, 3),
            new(Today.AddDays(-1).ToString("yyyy-MM-dd"), 2500, 2),
            new(Today.AddDays(-2).ToString("yyyy-MM-dd"), 1500, 1),
            new(Today.AddDays(-3).ToString("yyyy-MM-dd"), 500, 1),
            new(Today.AddDays(-4).ToString("yyyy-MM-dd"), 0, 1),
        };

        var cells = ProfileOverviewPresentation.Calendar(daily, Today).ToDictionary(cell => cell.Date);

        Assert.Equal(4, cells[Today].Level);
        Assert.Equal(3, cells[Today.AddDays(-1)].Level);
        Assert.Equal(2, cells[Today.AddDays(-2)].Level);
        Assert.Equal(1, cells[Today.AddDays(-3)].Level);
        Assert.Equal(1, cells[Today.AddDays(-4)].Level);
        Assert.Equal(0, cells[Today.AddDays(-5)].Level);
    }

    [Fact]
    public void TheCalendar_IgnoresDaysOutsideTheWindow()
    {
        var cells = ProfileOverviewPresentation.Calendar([new(Today.AddDays(-40).ToString("yyyy-MM-dd"), 900, 1)], Today);

        Assert.DoesNotContain(cells, cell => cell.Level > 0);
    }

    [Fact]
    public void TheStatStrip_NamesTheRollingWindowOnlyWhereItApplies()
    {
        var summary = new PersonalActivitySummaryDto(3600, 0, 0, 0, 0, 40, 2, null, FinishedLast30Days: 6, DaysActiveLast30: 9);

        var stats = ProfileOverviewPresentation.Stats(summary);

        Assert.Equal([("In progress", null), ("Finished", "Last 30 days"), ("Hours enjoyed", "All time"), ("Days active", "Last 30 days")],
            stats.Select(stat => (stat.Label, stat.Note)).ToArray());
        Assert.Equal(["2", "6", "1", "9"], stats.Select(stat => stat.Value).ToArray());
    }

    [Fact]
    public void TheGenreSlices_AreSharesOfTheWhole_WithTheTailGroupedAsOther()
    {
        var person = new TastePersonDto
        {
            Genres = [.. Enumerable.Range(0, 7).Select(i => new TasteItemDto($"g{i}", 7 - i, TasteValues.SourceLearned))],
        };

        var slices = ProfileOverviewPresentation.GenreSlices(person);

        Assert.Equal(6, slices.Count);
        Assert.Equal("Other", slices[^1].Label);
        Assert.Equal(100, slices.Sum(slice => slice.Percent), precision: 6);
        Assert.Equal(ProfileOverviewPresentation.OtherIndex, slices[^1].Index);
    }

    [Fact]
    public void AGenreAddedByHandWithoutAWeight_IsInTheSentenceButNotTheDonut()
    {
        var person = new TastePersonDto
        {
            IsYou = true,
            Genres = [new("drama", 0.5, TasteValues.SourceLearned), new("western", null, TasteValues.SourceAdded)],
        };

        Assert.Equal(["Drama"], ProfileOverviewPresentation.GenreSlices(person).Select(slice => slice.Label).ToArray());
        Assert.Equal("You love drama and western.", ProfileOverviewPresentation.Sentence(person, learned: true));
    }

    [Fact]
    public void TheSentence_SpeaksAboutSomeoneElseByName_AndWaitsUntilTasteIsLearned()
    {
        var other = new TastePersonDto { Person = "Anna", Genres = [new("jazz", 1, TasteValues.SourceLearned)] };

        Assert.Equal("Anna loves jazz.", ProfileOverviewPresentation.Sentence(other, learned: true));
        Assert.Null(ProfileOverviewPresentation.Sentence(other, learned: false));
        Assert.Null(ProfileOverviewPresentation.Sentence(new TastePersonDto { IsYou = true }, learned: true));
    }

    [Theory]
    [InlineData("Movies", "movies")]
    [InlineData("TV", "tv")]
    [InlineData("Audiobooks", "audiobooks")]
    [InlineData("Books", "books")]
    [InlineData("Comics", "comics")]
    [InlineData("Music", "music")]
    [InlineData("Something else", "unknown")]
    public void TheFormatMix_UsesEachMediaTypesOwnColour(string mediaType, string kind)
    {
        Assert.Equal(kind, ProfileOverviewPresentation.MediaKind(mediaType));
    }

    [Fact]
    public void TheShifts_NameTheThreeMostFinishedGenres_AndGroupEverythingElseAsOther()
    {
        var months = new List<TasteMonthDto>
        {
            new() { Month = "2026-09", Finished = 6, Genres = new() { ["sci-fi"] = 3, ["drama"] = 1, ["comedy"] = 1, ["western"] = 1 } },
            new() { Month = "2026-10", Finished = 4, Genres = new() { ["sci-fi"] = 2, ["drama"] = 1 } },
        };

        var (bars, legend) = ProfileOverviewPresentation.Shifts(months);

        Assert.Equal(["Sci-fi", "Drama", "Comedy", "Other"], legend);
        Assert.Equal(["2026-09", "2026-10"], bars.Select(bar => bar.Month).ToArray());
        Assert.Equal(6, bars[0].Segments.Sum(segment => segment.Count));
        Assert.Equal(1, bars[0].Segments.Single(segment => segment.Genre == "Other").Count);
        // A month's unknown-genre titles are "Other" too, and a month that has none of a genre draws no segment for it.
        Assert.Equal(1, bars[1].Segments.Single(segment => segment.Genre == "Other").Count);
        Assert.DoesNotContain(bars[1].Segments, segment => segment.Genre == "Comedy");
    }

    [Fact]
    public void TheShifts_AreEmptyWithNoFinishedTitles()
    {
        var (bars, legend) = ProfileOverviewPresentation.Shifts([]);

        Assert.Empty(bars);
        Assert.Empty(legend);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "Based on 1 finished title")]
    [InlineData(1234, "Based on 1,234 finished titles")]
    public void BasedOn_CountsFinishedTitles(int count, string? expected)
    {
        Assert.Equal(expected, ProfileOverviewPresentation.BasedOn(count));
    }

    [Theory]
    [InlineData("Book", "Reading")]
    [InlineData("Comic", "Reading")]
    [InlineData("Audiobook", "Listening")]
    [InlineData("Music", "Listening")]
    [InlineData("Movie", "Watching")]
    [InlineData("TV", "Watching")]
    [InlineData("Photos", null)]
    public void ContinueKind_SortsAMediaTypeIntoReadingWatchingOrListening(string mediaType, string? kind)
    {
        Assert.Equal(kind, ProfileOverviewPresentation.ContinueKind(mediaType));
    }
}
