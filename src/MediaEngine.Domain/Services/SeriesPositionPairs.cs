using MediaEngine.Domain.Contracts;

namespace MediaEngine.Domain.Services;

/// <summary>
/// Reads the (Series, position) pairs a Work stores under <see cref="MetadataFieldConstants.SeriesPlacement"/>
/// (<c>canonical_value_arrays</c>: value = position text as the source gave it, value QID = the Series). A Work in two
/// Series keeps each Series' own number, so a reader asks for the position in the Series it is showing.
/// </summary>
public static class SeriesPositionPairs
{
    /// <summary>
    /// The position the Work holds in the Series <paramref name="seriesQid"/>: the stored pair for that Series when one
    /// exists, otherwise <paramref name="fallbackPosition"/> (today's single-number chain). The text is returned as
    /// stored, so 1.5 stays "1.5".
    /// </summary>
    public static string? PositionFor(
        string? seriesQid,
        IReadOnlyList<CanonicalArrayEntry>? placements,
        string? fallbackPosition)
    {
        if (!string.IsNullOrWhiteSpace(seriesQid) && placements is { Count: > 0 })
        {
            var wanted = seriesQid.Trim();
            var pair = placements
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Value)
                    && string.Equals(entry.ValueQid?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Ordinal)
                .FirstOrDefault();
            if (pair is not null)
            {
                return pair.Value.Trim();
            }
        }

        return string.IsNullOrWhiteSpace(fallbackPosition) ? null : fallbackPosition.Trim();
    }

    /// <summary>
    /// The number a position text sorts by, with decimals intact ("7.5" is 7.5, never 7), or null when the text carries
    /// no number.
    /// </summary>
    public static double? SortValueOf(string? position)
        => string.IsNullOrWhiteSpace(position) ? null : OrdinalNormalizer.Normalize(position).SortValue;
}
