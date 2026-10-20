using System.Globalization;
using MediaEngine.Domain.Constants;

namespace MediaEngine.Domain.Services;

/// <summary>Which part of a Series a member sits in.</summary>
public enum SeriesSection
{
    /// <summary>Main-sequence members that have a position: the numbered run.</summary>
    NumberedRun = 0,

    /// <summary>Main-sequence members with no position: they stay in the Series after the numbered run, unnumbered.</summary>
    UnnumberedTail = 1,

    /// <summary>Supplementary, collected-content and broader-context members: the "Also in this world" list.</summary>
    AlsoInThisWorld = 2,
}

/// <summary>What the ordering rule reads about one Series member.</summary>
/// <param name="Scope">The membership scope; null or blank means main sequence (a retail-only member).</param>
/// <param name="ManifestOrdinal">The Wikidata manifest's parsed ordinal for the member in this Series, when it applies.</param>
/// <param name="WorkOrdinalSort">The Work's own decimal position (<c>works.ordinal_sort</c>).</param>
/// <param name="PublicationDate">The publication date text as stored (ISO, or just a year).</param>
/// <param name="Title">The member's title.</param>
public readonly record struct SeriesOrderInput(
    string? Scope,
    double? ManifestOrdinal,
    double? WorkOrdinalSort,
    string? PublicationDate,
    string? Title);

/// <summary>
/// The one ordering rule for a Series: the shelf, the item page and the native clients all sort with it.
/// Within the numbered run a member sorts by the manifest's ordinal (main sequence only), then the Work's own decimal
/// position, then publication date, then title. Decimals are never rounded. A position is shown as the source wrote
/// it, so 1.5 shows "1.5".
/// </summary>
public static class SeriesOrdering
{
    /// <summary>True for the scopes that appear in "Also in this world" instead of the Series run.</summary>
    public static bool IsAlsoInThisWorld(string? scope)
        => string.Equals(scope, SeriesMembershipScopeNames.Supplementary, StringComparison.OrdinalIgnoreCase)
           || string.Equals(scope, SeriesMembershipScopeNames.CollectedContent, StringComparison.OrdinalIgnoreCase)
           || string.Equals(scope, SeriesMembershipScopeNames.BroaderContext, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for the display kinds whose Series set "Also in this world" members apart from the numbered run: Book, Comic
    /// and Movie. A Series of any other kind (music, audiobooks, personal) is never split.
    /// </summary>
    public static bool SplitsAlsoInThisWorld(string? displayKind)
        => string.Equals(displayKind, "Book", StringComparison.OrdinalIgnoreCase)
           || string.Equals(displayKind, "Comic", StringComparison.OrdinalIgnoreCase)
           || string.Equals(displayKind, "Movie", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a manifest ordinal may be used: its ordinal scope is this Series (or unstated).</summary>
    public static bool OrdinalBelongsToSeries(string? ordinalScopeQid, string? seriesQid)
        => string.IsNullOrWhiteSpace(ordinalScopeQid)
           || string.Equals(ordinalScopeQid.Trim(), seriesQid?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the ordering input for one member. A manifest ordinal counts only when its ordinal scope is this Series, so
    /// a number borrowed from another container (for example a sub-collection's own numbering) is never shown or sorted by.
    /// </summary>
    public static SeriesOrderInput BuildInput(
        string? scope,
        double? manifestParsedOrdinal,
        string? ordinalScopeQid,
        string? seriesQid,
        double? workOrdinalSort,
        string? publicationDate,
        string? title)
        => new(
            scope,
            OrdinalBelongsToSeries(ordinalScopeQid, seriesQid) ? manifestParsedOrdinal : null,
            workOrdinalSort,
            publicationDate,
            title);

    /// <summary>
    /// The number this member sorts by: the manifest ordinal for main-sequence members when present, otherwise the
    /// Work's own decimal position, otherwise none.
    /// </summary>
    public static double? SortValue(SeriesOrderInput input)
    {
        if (string.Equals(input.Scope, SeriesMembershipScopeNames.Unpositioned, StringComparison.OrdinalIgnoreCase))
        {
            // The manifest says this member has no position, so a retail position does not number it.
            return null;
        }

        var isMainSequence = string.IsNullOrWhiteSpace(input.Scope)
            || string.Equals(input.Scope, SeriesMembershipScopeNames.MainSequence, StringComparison.OrdinalIgnoreCase);
        return isMainSequence && input.ManifestOrdinal.HasValue
            ? input.ManifestOrdinal
            : input.WorkOrdinalSort;
    }

    public static SeriesSection SectionOf(SeriesOrderInput input)
    {
        if (IsAlsoInThisWorld(input.Scope))
        {
            return SeriesSection.AlsoInThisWorld;
        }

        return SortValue(input).HasValue ? SeriesSection.NumberedRun : SeriesSection.UnnumberedTail;
    }

    public static int Compare(SeriesOrderInput left, SeriesOrderInput right)
    {
        var bySection = SectionOf(left).CompareTo(SectionOf(right));
        if (bySection != 0)
        {
            return bySection;
        }

        var leftSort = SortValue(left);
        var rightSort = SortValue(right);
        if (leftSort.HasValue && rightSort.HasValue)
        {
            var byNumber = leftSort.Value.CompareTo(rightSort.Value);
            if (byNumber != 0)
            {
                return byNumber;
            }
        }
        else if (leftSort.HasValue != rightSort.HasValue)
        {
            // A member with a position sorts before one without, inside "Also in this world".
            return leftSort.HasValue ? -1 : 1;
        }

        var byDate = ComparePublicationDate(left.PublicationDate, right.PublicationDate);
        return byDate != 0
            ? byDate
            : string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sorts members with the Series rule. The sort is stable, so equal members keep their given order.</summary>
    public static List<T> Order<T>(IEnumerable<T> members, Func<T, SeriesOrderInput> selector)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(selector);
        return members
            .Select((member, index) => (member, index, key: selector(member)))
            .OrderBy(entry => entry.key, Comparer<SeriesOrderInput>.Create(Compare))
            .ThenBy(entry => entry.index)
            .Select(entry => entry.member)
            .ToList();
    }

    /// <summary>
    /// The position text to show: the manifest's own text, else the retail Series position, else the Work's decimal
    /// position written without rounding. Null when the source gives no position at all.
    /// </summary>
    public static string? DisplayNumber(string? scope, string? manifestRawOrdinal, string? retailSeriesPosition, double? workOrdinalSort)
        => string.Equals(scope, SeriesMembershipScopeNames.Unpositioned, StringComparison.OrdinalIgnoreCase)
            ? null
            : DisplayNumber(manifestRawOrdinal, retailSeriesPosition, workOrdinalSort);

    public static string? DisplayNumber(string? manifestRawOrdinal, string? retailSeriesPosition, double? workOrdinalSort)
    {
        if (!string.IsNullOrWhiteSpace(manifestRawOrdinal))
        {
            return manifestRawOrdinal.Trim();
        }

        if (!string.IsNullOrWhiteSpace(retailSeriesPosition))
        {
            return retailSeriesPosition.Trim();
        }

        return workOrdinalSort.HasValue ? FormatNumber(workOrdinalSort.Value) : null;
    }

    /// <summary>Writes a position without padding or rounding: 2 shows "2", 2.5 shows "2.5".</summary>
    public static string FormatNumber(double value)
        => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static int ComparePublicationDate(string? left, string? right)
    {
        var leftText = left?.Trim();
        var rightText = right?.Trim();
        if (string.IsNullOrEmpty(leftText) && string.IsNullOrEmpty(rightText))
        {
            return 0;
        }

        if (string.IsNullOrEmpty(leftText))
        {
            return 1;
        }

        if (string.IsNullOrEmpty(rightText))
        {
            return -1;
        }

        return string.CompareOrdinal(leftText, rightText);
    }
}
