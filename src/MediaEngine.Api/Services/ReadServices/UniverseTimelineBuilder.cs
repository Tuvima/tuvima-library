using System.Globalization;
using MediaEngine.Contracts.Universe;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Builds the Universe Timeline (Universe contents spec P3 and section 4.3): a view over Events and dated first appearances,
/// never a stored kind. Stops are ordered by story date when enough of them carry one, otherwise by release year.
/// </summary>
internal static class UniverseTimelineBuilder
{
    /// <summary>Fewer dated stops than this and the Timeline (or its story order) is not offered.</summary>
    public const int MinimumStops = 3;

    internal sealed record Candidate(
        Guid Id,
        string Label,
        string Kind,
        string? PictureUrl,
        double? StoryYear,
        int? ReleaseYear,
        string? WorkLabel);

    public static UniverseTimelineDto? Build(IReadOnlyList<Candidate> candidates)
    {
        var dated = candidates.Where(candidate => candidate.StoryYear is not null || candidate.ReleaseYear is not null).ToList();
        if (dated.Count < MinimumStops)
        {
            return null;
        }

        var storyCount = dated.Count(candidate => candidate.StoryYear is not null);
        var storyAvailable = storyCount >= MinimumStops;

        // A stop without a story year cannot be placed in story order, so story order lists only the placed ones.
        IEnumerable<Candidate> ordered = storyAvailable
            ? dated.Where(candidate => candidate.StoryYear is not null)
                .OrderBy(candidate => candidate.StoryYear)
                .ThenBy(candidate => candidate.ReleaseYear ?? int.MaxValue)
                .ThenBy(candidate => candidate.Label, StringComparer.OrdinalIgnoreCase)
            : dated.OrderBy(candidate => candidate.ReleaseYear ?? int.MaxValue)
                .ThenBy(candidate => candidate.Label, StringComparer.OrdinalIgnoreCase);

        return new UniverseTimelineDto
        {
            DefaultOrder = storyAvailable ? UniverseTimelineDto.StoryOrder : UniverseTimelineDto.ReleaseOrder,
            StoryOrderAvailable = storyAvailable,
            Stops = ordered.Select(candidate => new UniverseTimelineStopDto
            {
                Id = candidate.Id,
                Label = candidate.Label,
                Kind = candidate.Kind,
                PictureUrl = candidate.PictureUrl,
                StoryYear = candidate.StoryYear,
                ReleaseYear = candidate.ReleaseYear,
                WorkLabel = candidate.WorkLabel,
            }).ToList(),
        };
    }

    /// <summary>
    /// The year a Wikidata time or a narrative-time index starts with ("+1015-00-00T00:00:00Z" is 1015, "-0044-03-15" is -44);
    /// null when it does not start with a year.
    /// </summary>
    public static double? ParseYear(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var index = 0;
        if (trimmed[0] is '+' or '-')
        {
            index = 1;
        }

        var digitsStart = index;
        while (index < trimmed.Length && char.IsAsciiDigit(trimmed[index]))
        {
            index++;
        }

        if (index == digitsStart || index - digitsStart > 9)
        {
            return null;
        }

        if (!double.TryParse(trimmed.AsSpan(digitsStart, index - digitsStart), NumberStyles.None, CultureInfo.InvariantCulture, out var magnitude))
        {
            return null;
        }

        return trimmed[0] == '-' ? -magnitude : magnitude;
    }
}
