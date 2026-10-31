using System.Globalization;

namespace MediaEngine.Domain.Models;

/// <summary>
/// A day and month with no year, the unit of a collection's seasonal window ("1 October"). Stored and sent as
/// <c>MM-dd</c> so the text sorts and compares the same way the dates do.
/// </summary>
public readonly record struct DayMonth(int Month, int Day) : IComparable<DayMonth>
{
    /// <summary>The most days each month can hold in a leap year, so 29 February is a valid window edge.</summary>
    private static readonly int[] DaysInMonth = [31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    /// <summary>True when the month is 1 to 12 and the day exists in that month (29 February is allowed).</summary>
    public bool IsValid => Month is >= 1 and <= 12 && Day >= 1 && Day <= DaysInMonth[Month - 1];

    /// <summary>The day and month of a calendar date.</summary>
    public static DayMonth Of(DateOnly date) => new(date.Month, date.Day);

    /// <summary>Reads <c>MM-dd</c>. Returns false for anything else, including a day that does not exist.</summary>
    public static bool TryParse(string? text, out DayMonth value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length != 5 || text[2] != '-')
        {
            return false;
        }

        if (!int.TryParse(text.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(text.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        {
            return false;
        }

        var candidate = new DayMonth(month, day);
        if (!candidate.IsValid)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    /// <summary>The stored and wire form, <c>MM-dd</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Month:00}-{Day:00}");

    public int CompareTo(DayMonth other) =>
        Month != other.Month ? Month.CompareTo(other.Month) : Day.CompareTo(other.Day);
}

/// <summary>
/// "Show only between [day month] and [day month]" (Collections and lists spec D12). Both edges are inside the
/// window, and a window whose end is before its start runs over the new year (1 December to 2 January).
/// </summary>
public sealed record CollectionSeasonWindow(DayMonth From, DayMonth To)
{
    /// <summary>
    /// Builds a window from its stored text. Both edges must be given and valid, or neither (no window, result true).
    /// </summary>
    public static bool TryCreate(string? from, string? to, out CollectionSeasonWindow? window)
    {
        window = null;
        if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to))
        {
            return true;
        }

        if (!DayMonth.TryParse(from, out var start) || !DayMonth.TryParse(to, out var end))
        {
            return false;
        }

        window = new CollectionSeasonWindow(start, end);
        return true;
    }

    /// <summary>True when <paramref name="date"/> falls inside the window, edges included.</summary>
    public bool Contains(DateOnly date)
    {
        var day = DayMonth.Of(date);
        return From.CompareTo(To) <= 0
            ? day.CompareTo(From) >= 0 && day.CompareTo(To) <= 0
            : day.CompareTo(From) >= 0 || day.CompareTo(To) <= 0;
    }

    /// <summary>
    /// The next date on or after <paramref name="today"/> when the window opens. Used for the "Hidden until
    /// 1 October" note, so it is meaningful only while <see cref="Contains"/> is false.
    /// </summary>
    public DateOnly NextOpening(DateOnly today)
    {
        // 29 February in a year without one opens on 28 February instead, so the window never skips a year.
        DateOnly Opening(int year) =>
            new(year, From.Month, Math.Min(From.Day, DateTime.DaysInMonth(year, From.Month)));

        var thisYear = Opening(today.Year);
        return thisYear >= today ? thisYear : Opening(today.Year + 1);
    }
}
