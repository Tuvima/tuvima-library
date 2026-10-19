using System.Globalization;
using MediaEngine.Contracts.Activity;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Services.Formatting;

/// <summary>How the Activity list groups its rows under headings.</summary>
public enum ActivityGrouping
{
    Day,
    Week,
    Month,

    /// <summary>One entry per title that expands to its attempts. Has no date headings.</summary>
    Title,
}

/// <summary>
/// The words and the date maths of the Activity page (D27, D31, D33): the verb on each row, how far and how it ended, the
/// attempts sentence of a title, the date headings and the date rail's periods. Kept apart from the component so the wording
/// and the boundaries are tested on their own.
/// </summary>
public static class ActivityListPresentation
{
    public static readonly IReadOnlyList<(string Key, string Label)> KindOptions =
    [
        (PersonalActivityKindValues.Watch, "Watched"),
        (PersonalActivityKindValues.Listen, "Listened"),
        (PersonalActivityKindValues.Read, "Read"),
        (PersonalActivityKindValues.MyList, "Added to My list"),
    ];

    /// <summary>"Watched", "Listened", "Read", "Played" (a music session) or "Added to My list".</summary>
    public static string Verb(string kind) => kind switch
    {
        PersonalActivityKindValues.Watch => "Watched",
        PersonalActivityKindValues.Listen => "Listened",
        PersonalActivityKindValues.Read => "Read",
        PersonalActivityKindValues.MusicSession => "Played",
        PersonalActivityKindValues.MyList => "Added to My list",
        _ => "Opened",
    };

    public static bool IsMusicSession(PersonalActivityItemDto item) =>
        string.Equals(item.Kind, PersonalActivityKindValues.MusicSession, StringComparison.Ordinal);

    public static bool IsTitleGroup(PersonalActivityItemDto item) => item.Attempts is { Count: > 0 };

    /// <summary>How long it was played or read; null for a My list add, which has no time.</summary>
    public static string? HowLong(PersonalActivityItemDto item) =>
        string.Equals(item.Kind, PersonalActivityKindValues.MyList, StringComparison.Ordinal)
            ? null
            : PersonalActivityPresentation.FormatDuration(item.PlayedSeconds);

    /// <summary>"31%", the furthest point reached; null where progress is not tracked (music, My list).</summary>
    public static string? HowFar(PersonalActivityItemDto item) =>
        item.ProgressPct is { } pct ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(Math.Clamp(pct, 0, 100))}%") : null;

    /// <summary>"Finished" or "Stopped"; null where finishing does not apply (music, My list).</summary>
    public static string? Outcome(PersonalActivityItemDto item)
    {
        if (item.Kind is PersonalActivityKindValues.MyList or PersonalActivityKindValues.MusicSession)
        {
            return null;
        }

        // A song has no progress to finish; an audiobook, book or video does.
        if (item.Kind == PersonalActivityKindValues.Listen && item.ProgressPct is null && !item.Finished)
        {
            return null;
        }

        return item.Finished ? "Finished" : "Stopped";
    }

    /// <summary>The line under the title: the episode, author or artist; for a music session, "50 songs from 12 albums".</summary>
    public static string? Subtitle(PersonalActivityItemDto item)
    {
        if (IsMusicSession(item))
        {
            return MusicCounts(item);
        }

        return string.IsNullOrWhiteSpace(item.Subtitle) ? null : item.Subtitle;
    }

    /// <summary>The title, or "Music" for a session (whose title is a placeholder).</summary>
    public static string Title(PersonalActivityItemDto item) =>
        IsMusicSession(item) && string.IsNullOrWhiteSpace(item.Title) ? "Music" : item.Title;

    public static string? MusicCounts(PersonalActivityItemDto item)
    {
        if (item.SongCount is not { } songs)
        {
            return null;
        }

        var songText = $"{songs} {(songs == 1 ? "song" : "songs")}";
        return item.AlbumCount is { } albums
            ? $"{songText} from {albums} {(albums == 1 ? "album" : "albums")}"
            : songText;
    }

    /// <summary>Where a tap on the row goes: the item while it exists, otherwise nowhere.</summary>
    public static string? Href(PersonalActivityItemDto item)
    {
        if (IsMusicSession(item) || !Guid.TryParse(item.WorkId, out var workId))
        {
            return null;
        }

        var context = item.Kind switch
        {
            PersonalActivityKindValues.Watch => "?context=watch",
            PersonalActivityKindValues.Read => "?context=read",
            PersonalActivityKindValues.Listen => "?context=listen",
            _ => string.Empty,
        };
        return $"/details/work/{workId:D}{context}";
    }

    public static DateTimeOffset? StartedAt(PersonalActivityItemDto item) =>
        DateTimeOffset.TryParse(item.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var started)
            ? started.ToLocalTime()
            : null;

    /// <summary>"9:41 PM", the time of day in the viewer's local time.</summary>
    public static string Time(PersonalActivityItemDto item) =>
        StartedAt(item)?.ToString("t", CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>"Sep 6, 9:41 PM" for rows that are not under a date heading.</summary>
    public static string DateAndTime(PersonalActivityItemDto item) =>
        StartedAt(item) is { } started
            ? $"{started.ToString("MMM d", CultureInfo.CurrentCulture)}, {started.ToString("t", CultureInfo.CurrentCulture)}"
            : string.Empty;

    private static string ShortDate(PersonalActivityItemDto item) =>
        StartedAt(item)?.ToString("MMM d", CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>
    /// The attempts of a title in one sentence, oldest first, like "Started Sep 6, 30 m, stopped at 31% · Sep 9, 12 m,
    /// stopped at 43% · Sep 13, finished, 1 h 36 m in total".
    /// </summary>
    public static string AttemptsSentence(PersonalActivityItemDto group)
    {
        var attempts = group.Attempts ?? [];
        if (attempts.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(attempts.Count);
        for (var index = 0; index < attempts.Count; index++)
        {
            var attempt = attempts[index];
            var lead = index == 0 ? "Started " : string.Empty;
            var piece = attempt.Finished
                ? $"{lead}{ShortDate(attempt)}, finished"
                : $"{lead}{ShortDate(attempt)}, {PersonalActivityPresentation.FormatDuration(attempt.PlayedSeconds)}, {StoppedAt(attempt)}";
            parts.Add(piece);
        }

        // A lone attempt that stopped already says how long it was; the total only adds something once there is more than one
        // session or the title was finished.
        if (attempts.Count > 1 || attempts[0].Finished)
        {
            parts[^1] = $"{parts[^1]}, {PersonalActivityPresentation.FormatDuration(group.PlayedSeconds)} in total";
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"stopped at 31%", or just "stopped" where no point was recorded.</summary>
    public static string StoppedAt(PersonalActivityItemDto attempt) =>
        HowFar(attempt) is { } far ? $"stopped at {far}" : "stopped";

    /// <summary>"1 session" or "3 sessions".</summary>
    public static string SessionCount(PersonalActivityItemDto group)
    {
        var count = group.AttemptCount ?? group.Attempts?.Count ?? 1;
        return $"{count} {(count == 1 ? "session" : "sessions")}";
    }

    /// <summary>The key and heading of the date section a row falls under, in the viewer's local time.</summary>
    public static (string Key, string Label) Section(PersonalActivityItemDto item, ActivityGrouping grouping, DateTimeOffset now)
    {
        var started = StartedAt(item);
        if (started is null || grouping == ActivityGrouping.Title)
        {
            return (string.Empty, string.Empty);
        }

        var date = started.Value.Date;
        var today = now.ToLocalTime().Date;
        switch (grouping)
        {
            case ActivityGrouping.Day:
                var dayLabel = date == today
                    ? "Today"
                    : date == today.AddDays(-1)
                        ? "Yesterday"
                        : date.ToString(date.Year == today.Year ? "dddd, MMM d" : "dddd, MMM d, yyyy", CultureInfo.CurrentCulture);
                return (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dayLabel);
            case ActivityGrouping.Week:
                var weekStart = StartOfWeek(date);
                var thisWeek = StartOfWeek(today);
                var weekLabel = weekStart == thisWeek
                    ? "This week"
                    : weekStart == thisWeek.AddDays(-7)
                        ? "Last week"
                        : $"Week of {weekStart.ToString(weekStart.Year == today.Year ? "MMM d" : "MMM d, yyyy", CultureInfo.CurrentCulture)}";
                return (weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), weekLabel);
            default:
                var monthLabel = date.Year == today.Year && date.Month == today.Month
                    ? "This month"
                    : date.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
                return (date.ToString("yyyy-MM", CultureInfo.InvariantCulture), monthLabel);
        }
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var offset = ((int)date.DayOfWeek - (int)first + 7) % 7;
        return date.AddDays(-offset);
    }

    /// <summary>The date rail's periods: years newest first, each with its months newest first (UTC, like the counts).</summary>
    public static IReadOnlyList<TimelinePeriod> Periods(PersonalActivityPeriodsDto? periods) =>
        periods is null
            ? []
            : [.. periods.Years
                .Where(year => year.Count > 0)
                .OrderByDescending(year => year.Year)
                .Select(year => new TimelinePeriod(
                    year.Year,
                    year.Year.ToString(CultureInfo.InvariantCulture),
                    year.Count,
                    [.. year.Months
                        .Where(month => month.Count > 0)
                        .OrderByDescending(month => month.Month)
                        .Select(month => new TimelinePeriod(
                            month.Month,
                            CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(month.Month),
                            month.Count))]))];

    /// <summary>
    /// The exclusive upper bound that makes the list start at a period: the first instant after the year, or after the month
    /// when one is given. UTC, because the rail's counts are per UTC month.
    /// </summary>
    public static DateTimeOffset JumpBefore(int year, int? month) =>
        month is { } m
            ? new DateTimeOffset(year, m, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1)
            : new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).AddYears(1);

    /// <summary>"Oct 2026" or "2026": what the chip for a jump says.</summary>
    public static string JumpLabel(int year, int? month) =>
        month is { } m
            ? $"{CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(m)} {year}"
            : year.ToString(CultureInfo.InvariantCulture);

    /// <summary>The start of a day the person picked, in their local time, as the instant the Engine filters on.</summary>
    public static DateTimeOffset? StartOfLocalDay(string? isoDate) =>
        DateOnly.TryParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue)))
            : null;

    /// <summary>The start of the day after the one the person picked: the exclusive end of a date range that includes that day.</summary>
    public static DateTimeOffset? EndOfLocalDay(string? isoDate) =>
        DateOnly.TryParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? StartOfLocalDay(day.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            : null;
}
