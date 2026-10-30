using System.Globalization;
using MediaEngine.Contracts.Activity;
using MediaEngine.Contracts.Taste;

namespace MediaEngine.Web.Services.Formatting;

/// <summary>One friendly number of the Profile Overview's stat strip.</summary>
public sealed record OverviewStat(string Key, string Value, string Label, string? Note);

/// <summary>One day of the last-30-days calendar. <c>Level</c> is 0 (nothing) to 4 (the busiest days).</summary>
public sealed record HeatmapDay(DateOnly Date, long PlayedSeconds, int Entries, int Level, bool InWindow);

/// <summary>One slice of the genre donut. <c>Percent</c> is of the whole ring.</summary>
public sealed record TasteSlice(string Label, double Percent, int Index);

/// <summary>One segment of the format-mix bar.</summary>
public sealed record FormatShare(string Label, double Percent, string Kind);

/// <summary>One bar of "How your taste shifts": a month and its genre split, biggest genre first.</summary>
public sealed record ShiftBar(string Month, string Label, int Finished, IReadOnlyList<ShiftSegment> Segments);

/// <summary>A genre's share of one month's bar. <c>Index</c> picks the same colour as the legend.</summary>
public sealed record ShiftSegment(string Genre, int Count, int Index);

/// <summary>
/// Everything the Profile pages work out from the Engine's figures: the stat strip, the 30-day calendar, the genre donut, the
/// "You love ..." sentence, the format mix and the monthly shifts. Nothing is invented: a figure the Engine did not supply is left
/// out, and zero is shown only where zero is an answer.
/// </summary>
public static class ProfileOverviewPresentation
{
    /// <summary>The rolling window of the Overview, in days. The Engine uses the same number.</summary>
    public const int RollingDays = 30;

    /// <summary>How many genres get their own slice or colour before the rest are grouped as Other.</summary>
    public const int NamedGenres = 5;

    /// <summary>How many genres the monthly chart names; the rest of each bar is "Other".</summary>
    public const int ShiftGenres = 3;

    /// <summary>The slice colour index used for "Other" on the donut and the monthly chart.</summary>
    public const int OtherIndex = NamedGenres;

    // ----- Stat strip -----

    public static IReadOnlyList<OverviewStat> Stats(PersonalActivitySummaryDto summary)
    {
        var enjoyed = summary.WatchSeconds + summary.ReadSeconds + summary.ListenSeconds;
        return
        [
            new("in-progress", DisplayFormat.FormatCount(summary.InProgressCount), "In progress", "Right now"),
            new("finished", DisplayFormat.FormatCount(summary.FinishedLast30Days), "Finished", "Last 30 days"),
            new("hours", HoursEnjoyed(enjoyed), "Hours enjoyed", "All time"),
            new("days-active", DisplayFormat.FormatCount(summary.DaysActiveLast30), "Days active", $"Last {RollingDays} days"),
        ];
    }

    /// <summary>Hours with no unit word ("12", "3.5", "0"): the label says "Hours enjoyed".</summary>
    public static string HoursEnjoyed(long seconds)
    {
        var hours = Math.Max(0, seconds) / 3600d;
        return hours >= 10
            ? ((int)Math.Round(hours)).ToString("N0", CultureInfo.CurrentCulture)
            : Math.Round(hours, 1).ToString("0.#", CultureInfo.CurrentCulture);
    }

    /// <summary>True when the person has nothing at all yet, so the page invites them in instead of showing zeros alone.</summary>
    public static bool IsNewProfile(PersonalActivitySummaryDto summary) =>
        summary.InProgressCount == 0 && summary.FinishedCount == 0 &&
        summary.WatchSeconds + summary.ReadSeconds + summary.ListenSeconds == 0;

    // ----- 30-day calendar -----

    /// <summary>
    /// The calendar as whole Monday-to-Sunday weeks ending with the week that holds <paramref name="today"/>, covering the rolling
    /// window. Days before the window (padding in the first week) and after today (the rest of the last week) are marked outside it.
    /// </summary>
    public static IReadOnlyList<HeatmapDay> Calendar(IReadOnlyList<PersonalActivityDayDto>? daily, DateOnly today)
    {
        var first = today.AddDays(-(RollingDays - 1));
        var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        var end = today.AddDays(6 - (((int)today.DayOfWeek + 6) % 7));
        var byDate = new Dictionary<DateOnly, PersonalActivityDayDto>();
        foreach (var day in daily ?? [])
        {
            if (DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                byDate[date] = day;
            }
        }

        var busiest = byDate.Values.Where(day => day.PlayedSeconds > 0).Select(day => day.PlayedSeconds).DefaultIfEmpty(0).Max();
        var cells = new List<HeatmapDay>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var inWindow = date >= first && date <= today;
            byDate.TryGetValue(date, out var day);
            var seconds = inWindow ? day?.PlayedSeconds ?? 0 : 0;
            var entries = inWindow ? day?.Entries ?? 0 : 0;
            cells.Add(new HeatmapDay(date, seconds, entries, inWindow ? Level(seconds, entries, busiest) : 0, inWindow));
        }

        return cells;
    }

    // A day with any activity is at least level 1, even when its time was too short to measure.
    private static int Level(long seconds, int entries, long busiest)
    {
        if (entries == 0 && seconds == 0)
        {
            return 0;
        }

        if (busiest <= 0)
        {
            return 1;
        }

        var share = (double)seconds / busiest;
        return share switch
        {
            > 0.75 => 4,
            > 0.5 => 3,
            > 0.25 => 2,
            _ => 1,
        };
    }

    public static string DayLabel(HeatmapDay day)
    {
        var date = day.Date.ToString("d MMMM", CultureInfo.CurrentCulture);
        if (day.Entries == 0 && day.PlayedSeconds == 0)
        {
            return $"{date}: nothing";
        }

        var time = PersonalActivityPresentation.FormatHours(day.PlayedSeconds);
        var things = day.Entries == 1 ? "1 session" : $"{day.Entries} sessions";
        return time is null ? $"{date}: {things}" : $"{date}: {things}, {time}";
    }

    // ----- Timeline -----

    /// <summary>"Today", "Yesterday" or "12 October", in the viewer's local time.</summary>
    public static string DayHeading(DateTimeOffset startedAt, DateTimeOffset now)
    {
        var local = startedAt.ToLocalTime().Date;
        var today = now.ToLocalTime().Date;
        if (local == today)
        {
            return "Today";
        }

        return local == today.AddDays(-1)
            ? "Yesterday"
            : local.ToString(local.Year == today.Year ? "d MMMM" : "d MMMM yyyy", CultureInfo.CurrentCulture);
    }

    // ----- Continue enjoying -----

    /// <summary>The three doing-words of the Continue enjoying cards, in the order they are drawn.</summary>
    public static readonly IReadOnlyList<string> ContinueKinds = ["Reading", "Watching", "Listening"];

    /// <summary>Reading, Watching or Listening for a media type; null for anything else.</summary>
    public static string? ContinueKind(string? mediaType)
    {
        var key = mediaType?.Trim().ToLowerInvariant() ?? string.Empty;
        return key switch
        {
            _ when key.Contains("book", StringComparison.Ordinal) && !key.Contains("audio", StringComparison.Ordinal) => "Reading",
            _ when key.Contains("comic", StringComparison.Ordinal) || key.Contains("epub", StringComparison.Ordinal) => "Reading",
            _ when key.Contains("audio", StringComparison.Ordinal) || key.Contains("music", StringComparison.Ordinal) => "Listening",
            _ when key.Contains("movie", StringComparison.Ordinal) || key.Contains("tv", StringComparison.Ordinal)
                || key.Contains("show", StringComparison.Ordinal) || key.Contains("episode", StringComparison.Ordinal)
                || key.Contains("video", StringComparison.Ordinal) => "Watching",
            _ => null,
        };
    }

    // ----- Taste -----

    /// <summary>"You love sci-fi, drama and mystery." from the top three genres; null when there are none.</summary>
    public static string? Sentence(TastePersonDto person, bool learned)
    {
        if (!learned)
        {
            return null;
        }

        var top = person.Genres
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .OrderByDescending(item => item.Weight ?? 0)
            .ThenBy(item => item.Value, StringComparer.CurrentCultureIgnoreCase)
            .Take(3)
            .Select(item => item.Value.Trim().ToLowerInvariant())
            .ToList();
        if (top.Count == 0)
        {
            return null;
        }

        var who = person.IsYou
            ? "You love"
            : string.IsNullOrWhiteSpace(person.Person) ? "They love" : $"{person.Person} loves";
        return $"{who} {JoinList(top)}.";
    }

    /// <summary>The donut: the biggest genres by weight and the rest as Other. Added-by-hand genres without a weight share equally.</summary>
    public static IReadOnlyList<TasteSlice> GenreSlices(TastePersonDto person)
    {
        var weighted = person.Genres
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => (Label: item.Value.Trim(), Weight: item.Weight is > 0 ? item.Weight.Value : 0d))
            .Where(item => item.Weight > 0)
            .OrderByDescending(item => item.Weight)
            .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var total = weighted.Sum(item => item.Weight);
        if (total <= 0)
        {
            return [];
        }

        var slices = weighted
            .Take(NamedGenres)
            .Select((item, index) => new TasteSlice(Capitalise(item.Label), item.Weight / total * 100d, index))
            .ToList();
        var rest = weighted.Skip(NamedGenres).Sum(item => item.Weight);
        if (rest > 0)
        {
            slices.Add(new TasteSlice("Other", rest / total * 100d, OtherIndex));
        }

        return slices;
    }

    /// <summary>The format mix as shares of 100, biggest first. Kinds name their media-type colour.</summary>
    public static IReadOnlyList<FormatShare> FormatMix(TastePersonDto person)
    {
        var parts = person.MediaMix
            .Where(pair => pair.Value > 0 && !string.IsNullOrWhiteSpace(pair.Key))
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var total = parts.Sum(pair => pair.Value);
        return total <= 0
            ? []
            : [.. parts.Select(pair => new FormatShare(pair.Key.Trim(), pair.Value / total * 100d, MediaKind(pair.Key)))];
    }

    /// <summary>The CSS class suffix of a media type's colour (<c>--tl-media-*</c>).</summary>
    public static string MediaKind(string mediaType)
    {
        var key = mediaType.Trim().ToLowerInvariant();
        return key switch
        {
            _ when key.Contains("audiobook", StringComparison.Ordinal) => "audiobooks",
            _ when key.Contains("comic", StringComparison.Ordinal) => "comics",
            _ when key.Contains("book", StringComparison.Ordinal) => "books",
            _ when key.Contains("movie", StringComparison.Ordinal) || key.Contains("film", StringComparison.Ordinal) => "movies",
            _ when key.Contains("tv", StringComparison.Ordinal) || key.Contains("show", StringComparison.Ordinal) || key.Contains("episode", StringComparison.Ordinal) => "tv",
            _ when key.Contains("music", StringComparison.Ordinal) || key.Contains("song", StringComparison.Ordinal) => "music",
            _ => "unknown",
        };
    }

    /// <summary>
    /// "How your taste shifts": one bar per month with a finished title, oldest first. The three genres finished most across the
    /// whole window are named (same colour in every bar); everything else, and titles with no known genre, is Other.
    /// </summary>
    public static (IReadOnlyList<ShiftBar> Bars, IReadOnlyList<string> Legend) Shifts(IReadOnlyList<TasteMonthDto> months)
    {
        var named = months
            .SelectMany(month => month.Genres)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(group => (Genre: group.Key, Count: group.Sum(pair => pair.Value)))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Genre, StringComparer.Ordinal)
            .Take(ShiftGenres)
            .Select(item => item.Genre)
            .ToList();
        var bars = new List<ShiftBar>();
        foreach (var month in months.Where(month => month.Finished > 0).OrderBy(month => month.Month, StringComparer.Ordinal))
        {
            var segments = new List<ShiftSegment>();
            foreach (var (genre, index) in named.Select((genre, index) => (genre, index)))
            {
                if (month.Genres.TryGetValue(genre, out var count) && count > 0)
                {
                    segments.Add(new ShiftSegment(Capitalise(genre), count, index));
                }
            }

            var other = month.Finished - segments.Sum(segment => segment.Count);
            if (other > 0)
            {
                segments.Add(new ShiftSegment("Other", other, ShiftGenres));
            }

            bars.Add(new ShiftBar(month.Month, MonthLabel(month.Month), month.Finished, segments));
        }

        var legend = named.Select(Capitalise).ToList();
        if (bars.Any(bar => bar.Segments.Any(segment => segment.Index == ShiftGenres)))
        {
            legend.Add("Other");
        }

        return (bars, legend);
    }

    /// <summary>"Oct" for this year's months and "Oct 25" for earlier years.</summary>
    public static string MonthLabel(string month) =>
        DateTime.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("MMM", CultureInfo.CurrentCulture)
            : month;

    /// <summary>"Based on 12 finished titles" or "Based on 1 finished title"; null when none.</summary>
    public static string? BasedOn(int finishedTitles) =>
        finishedTitles <= 0
            ? null
            : $"Based on {DisplayFormat.FormatCount(finishedTitles)} finished {(finishedTitles == 1 ? "title" : "titles")}";

    public static string Capitalise(string value)
    {
        var text = value.Trim();
        return text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
    }

    private static string JoinList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
