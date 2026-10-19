using System.Globalization;
using MediaEngine.Contracts.Activity;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Tests;

/// <summary>The words and date maths of the Activity page (D27, D31, D33).</summary>
public sealed class ActivityListPresentationTests
{
    // Midday UTC is the same calendar day everywhere from UTC-11 to UTC+11, so these do not depend on the machine's time zone.
    private static PersonalActivityItemDto Attempt(string started, long seconds, double? progress, bool finished = false) => new()
    {
        Id = started,
        Kind = PersonalActivityKindValues.Watch,
        Title = "Tremors",
        StartedAt = started,
        PlayedSeconds = seconds,
        ProgressPct = progress,
        Finished = finished,
    };

    private static PersonalActivityItemDto Group(params PersonalActivityItemDto[] attempts) => new()
    {
        Id = "g",
        Kind = PersonalActivityKindValues.Watch,
        Title = "Tremors",
        StartedAt = attempts[^1].StartedAt,
        PlayedSeconds = attempts.Sum(attempt => attempt.PlayedSeconds),
        AttemptCount = attempts.Length,
        Attempts = attempts,
    };

    private static void InEnglish(Action assertions)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        try
        {
            assertions();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void AttemptsSentence_ReadsLikeTheTremorsExample_StoppedAtThenFinishedWithTheTotal()
    {
        var group = Group(
            Attempt("2026-09-06T12:00:00Z", 30 * 60, 31),
            Attempt("2026-09-09T12:00:00Z", 12 * 60, 43),
            Attempt("2026-09-13T12:00:00Z", 54 * 60, 100, finished: true));

        InEnglish(() => Assert.Equal(
            "Started Sep 6, 30 m, stopped at 31% · Sep 9, 12 m, stopped at 43% · Sep 13, finished, 1 h 36 m in total",
            ActivityListPresentation.AttemptsSentence(group)));
    }

    [Fact]
    public void AttemptsSentence_OfOneStoppedAttempt_DoesNotRepeatItsLengthAsATotal()
    {
        InEnglish(() => Assert.Equal(
            "Started Sep 6, 30 m, stopped at 31%",
            ActivityListPresentation.AttemptsSentence(Group(Attempt("2026-09-06T12:00:00Z", 30 * 60, 31)))));
        InEnglish(() => Assert.Equal(
            "Started Sep 6, finished, 1 h 30 m in total",
            ActivityListPresentation.AttemptsSentence(Group(Attempt("2026-09-06T12:00:00Z", 90 * 60, 100, finished: true)))));
        Assert.Equal(string.Empty, ActivityListPresentation.AttemptsSentence(new PersonalActivityItemDto { Id = "x" }));
    }

    [Theory]
    [InlineData(PersonalActivityKindValues.Watch, "Watched")]
    [InlineData(PersonalActivityKindValues.Listen, "Listened")]
    [InlineData(PersonalActivityKindValues.Read, "Read")]
    [InlineData(PersonalActivityKindValues.MyList, "Added to My list")]
    [InlineData(PersonalActivityKindValues.MusicSession, "Played")]
    public void Verb_NamesWhatHappened(string kind, string expected)
    {
        Assert.Equal(expected, ActivityListPresentation.Verb(kind));
    }

    [Fact]
    public void HowFarAndOutcome_AreLeftOutWhereProgressDoesNotApply()
    {
        var song = new PersonalActivityItemDto { Kind = PersonalActivityKindValues.Listen, ProgressPct = null };
        var myList = new PersonalActivityItemDto { Kind = PersonalActivityKindValues.MyList };
        var session = new PersonalActivityItemDto { Kind = PersonalActivityKindValues.MusicSession };
        var stopped = new PersonalActivityItemDto { Kind = PersonalActivityKindValues.Watch, ProgressPct = 30.6 };
        var finished = new PersonalActivityItemDto { Kind = PersonalActivityKindValues.Read, ProgressPct = 100, Finished = true };

        Assert.Null(ActivityListPresentation.HowFar(song));
        Assert.Null(ActivityListPresentation.Outcome(song));
        Assert.Null(ActivityListPresentation.Outcome(myList));
        Assert.Null(ActivityListPresentation.HowLong(myList));
        Assert.Null(ActivityListPresentation.Outcome(session));
        Assert.Equal("31%", ActivityListPresentation.HowFar(stopped));
        Assert.Equal("Stopped", ActivityListPresentation.Outcome(stopped));
        Assert.Equal("100%", ActivityListPresentation.HowFar(finished));
        Assert.Equal("Finished", ActivityListPresentation.Outcome(finished));
    }

    [Fact]
    public void AMusicSession_IsCalledMusic_AndCountsItsSongsAndAlbums()
    {
        var session = new PersonalActivityItemDto { Kind = PersonalActivityKindValues.MusicSession, Title = string.Empty, SongCount = 50, AlbumCount = 12 };

        Assert.Equal("Music", ActivityListPresentation.Title(session));
        Assert.Equal("50 songs from 12 albums", ActivityListPresentation.Subtitle(session));
        Assert.Equal("1 song from 1 album", ActivityListPresentation.MusicCounts(new PersonalActivityItemDto { SongCount = 1, AlbumCount = 1 }));
        Assert.Null(ActivityListPresentation.Href(session));
    }

    [Fact]
    public void Href_OpensTheItemInTheWayItWasUsed_AndIsNullOnceTheWorkIsGone()
    {
        var id = Guid.NewGuid();

        Assert.Equal($"/details/work/{id:D}?context=watch", ActivityListPresentation.Href(new PersonalActivityItemDto { Kind = PersonalActivityKindValues.Watch, WorkId = id.ToString("D") }));
        Assert.Equal($"/details/work/{id:D}?context=read", ActivityListPresentation.Href(new PersonalActivityItemDto { Kind = PersonalActivityKindValues.Read, WorkId = id.ToString("D") }));
        Assert.Equal($"/details/work/{id:D}", ActivityListPresentation.Href(new PersonalActivityItemDto { Kind = PersonalActivityKindValues.MyList, WorkId = id.ToString("D") }));
        Assert.Null(ActivityListPresentation.Href(new PersonalActivityItemDto { Kind = PersonalActivityKindValues.Watch, WorkId = null }));
        Assert.Null(ActivityListPresentation.Href(new PersonalActivityItemDto { Kind = PersonalActivityKindValues.Watch, WorkId = "not a guid" }));
    }

    [Fact]
    public void Sections_AreTodayYesterdayThenDates_ByDay_AndWeeksAndMonthsLikewise()
    {
        var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        PersonalActivityItemDto At(string iso) => new() { StartedAt = iso };

        InEnglish(() =>
        {
            Assert.Equal("Today", ActivityListPresentation.Section(At("2026-10-19T12:00:00Z"), ActivityGrouping.Day, now).Label);
            Assert.Equal("Yesterday", ActivityListPresentation.Section(At("2026-10-18T12:00:00Z"), ActivityGrouping.Day, now).Label);
            Assert.Equal("Monday, Oct 12", ActivityListPresentation.Section(At("2026-10-12T12:00:00Z"), ActivityGrouping.Day, now).Label);
            Assert.Equal("Wednesday, Jan 15, 2025", ActivityListPresentation.Section(At("2025-01-15T12:00:00Z"), ActivityGrouping.Day, now).Label);

            Assert.Equal("This month", ActivityListPresentation.Section(At("2026-10-02T12:00:00Z"), ActivityGrouping.Month, now).Label);
            Assert.Equal("September 2026", ActivityListPresentation.Section(At("2026-09-30T12:00:00Z"), ActivityGrouping.Month, now).Label);

            Assert.Equal("This week", ActivityListPresentation.Section(At("2026-10-19T12:00:00Z"), ActivityGrouping.Week, now).Label);
            Assert.Equal("Last week", ActivityListPresentation.Section(At("2026-10-12T12:00:00Z"), ActivityGrouping.Week, now).Label);
        });
        // Rows on the same day share a key, so they sit under one heading; a title list has no headings.
        Assert.Equal(
            ActivityListPresentation.Section(At("2026-10-12T09:00:00Z"), ActivityGrouping.Day, now).Key,
            ActivityListPresentation.Section(At("2026-10-12T10:00:00Z"), ActivityGrouping.Day, now).Key);
        Assert.Equal((string.Empty, string.Empty), ActivityListPresentation.Section(At("2026-10-12T12:00:00Z"), ActivityGrouping.Title, now));
    }

    [Fact]
    public void Periods_AreYearsNewestFirst_EachWithItsMonths_AndLeaveOutEmptyOnes()
    {
        var periods = new PersonalActivityPeriodsDto(
        [
            new(2025, 3, [new(12, 2), new(3, 1)]),
            new(2026, 14, [new(9, 5), new(10, 9)]),
            new(2024, 0, []),
        ]);

        InEnglish(() =>
        {
            var rail = ActivityListPresentation.Periods(periods);

            Assert.Equal([2026, 2025], rail.Select(period => period.Key).ToArray());
            Assert.Equal(["2026", "2025"], rail.Select(period => period.Label).ToArray());
            Assert.Equal([14, 3], rail.Select(period => period.Count).ToArray());
            Assert.Equal([10, 9], rail[0].Children!.Select(month => month.Key).ToArray());
            Assert.Equal(["Oct", "Sep"], rail[0].Children!.Select(month => month.Label).ToArray());
        });
        Assert.Empty(ActivityListPresentation.Periods(null));
    }

    [Fact]
    public void JumpBefore_IsTheFirstInstantAfterThePeriod_InUtc()
    {
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), ActivityListPresentation.JumpBefore(2026, null));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), ActivityListPresentation.JumpBefore(2026, 9));
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), ActivityListPresentation.JumpBefore(2026, 12));
        InEnglish(() => Assert.Equal("Sep 2026", ActivityListPresentation.JumpLabel(2026, 9)));
        Assert.Equal("2026", ActivityListPresentation.JumpLabel(2026, null));
    }

    [Fact]
    public void ADateRange_IncludesBothDays_ByEndingAtTheStartOfTheDayAfterTheLastOne()
    {
        var start = ActivityListPresentation.StartOfLocalDay("2026-09-30");
        var end = ActivityListPresentation.EndOfLocalDay("2026-09-30");

        Assert.NotNull(start);
        Assert.NotNull(end);
        Assert.Equal(new DateTime(2026, 9, 30), start!.Value.DateTime);
        Assert.Equal(new DateTime(2026, 10, 1), end!.Value.DateTime);
        Assert.Null(ActivityListPresentation.StartOfLocalDay(null));
        Assert.Null(ActivityListPresentation.StartOfLocalDay("30/09/2026"));
        Assert.Null(ActivityListPresentation.EndOfLocalDay(""));
    }
}
