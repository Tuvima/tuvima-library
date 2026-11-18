using System.Globalization;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Components.Details;

/// <summary>One label/value row in a file block.</summary>
public sealed record DetailFileFactRow(string Label, string Value);

/// <summary>
/// The quiet facts a Series shows on its page, taken from the page model alone. A title's own file facts are the Engine's "Your copy"
/// (<see cref="MediaEngine.Contracts.Details.DetailYourCopyViewModel"/>); the Dashboard builds no file facts or summaries.
/// </summary>
public static class DetailFileFacts
{
    /// <summary>True for a book, comic or film Series: its About this shows series facts instead of file facts.</summary>
    public static bool IsSeriesContainer(DetailEntityType entityType) => entityType
        is DetailEntityType.MovieSeries
        or DetailEntityType.BookSeries
        or DetailEntityType.ComicSeries;

    /// <summary>
    /// The About this facts for a Series, taken from the page model alone (no extra Engine request):
    /// how many you own of the known total, the first and latest year, and the author.
    /// Facts that are not known are left out.
    /// </summary>
    public static IReadOnlyList<DetailFileFactRow> BuildSeriesFacts(
        DetailPageViewModel model,
        string inLibraryLabel,
        string firstLabel,
        string latestLabel,
        string authorLabel,
        string ofTotalFormat)
    {
        var rows = new List<DetailFileFactRow>();
        var placement = model.SequencePlacement;
        var items = placement?.OrderedItems
            .Where(item => string.Equals(item.MembershipScope, "MainSequence", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        // An entry for one file covering two episodes ("E19-20: A + B") counts both: episodes count every episode a file covers.
        var owned = items.Where(item => item.IsOwned).Sum(item => Math.Max(1, item.EpisodeCount));
        var total = Math.Max(placement?.TotalKnownItems ?? 0, items.Sum(item => Math.Max(1, item.EpisodeCount)));
        if (owned > 0 || total > 0)
        {
            var noun = (placement?.ItemPluralLabel ?? "items").ToLowerInvariant();
            var value = total > owned && owned > 0
                ? string.Format(CultureInfo.CurrentCulture, ofTotalFormat, owned, total, noun)
                : $"{(owned > 0 ? owned : total)} {noun}";
            rows.Add(new DetailFileFactRow(inLibraryLabel, value));
        }

        var years = items
            .Select(item => ParseYear(item.PublicationDate))
            .Where(year => year is not null)
            .Select(year => year!.Value)
            .ToList();
        if (years.Count > 0)
        {
            rows.Add(new DetailFileFactRow(firstLabel, years.Min().ToString(CultureInfo.InvariantCulture)));
            if (years.Max() != years.Min())
            {
                rows.Add(new DetailFileFactRow(latestLabel, years.Max().ToString(CultureInfo.InvariantCulture)));
            }
        }

        var author = model.FullContributorGroups.Concat(model.ContributorGroups)
            .Where(group => group.GroupType == CreditGroupType.Authors)
            .SelectMany(group => group.Credits)
            .Select(credit => credit.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        if (author.Count > 0)
        {
            rows.Add(new DetailFileFactRow(authorLabel, string.Join(", ", author)));
        }

        return rows;
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
        {
            return null;
        }

        return int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is > 0 and < 3000
            ? year
            : null;
    }
}
