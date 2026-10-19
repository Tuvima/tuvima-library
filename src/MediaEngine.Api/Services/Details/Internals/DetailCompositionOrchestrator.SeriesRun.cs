using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Details.Internals;

// The Series run and "Also in this world": the item page and the Series page order and split a Series' members with
// the same Domain rule (SeriesOrdering) that the shelf and the native clients use, so they cannot disagree.
internal sealed partial class DetailCompositionOrchestrator
{
    /// <summary>
    /// Whether a page's members are one Series (a book, audiobook, comic or film Series), whose supplementary,
    /// collected-content and broader-context members are listed apart. TV shows, seasons and albums keep their own
    /// season, episode and track grouping.
    /// </summary>
    private static bool UsesSeriesRunSplit(DetailEntityType entityType)
        => entityType is DetailEntityType.Movie
            or DetailEntityType.MovieSeries
            or DetailEntityType.Book
            or DetailEntityType.BookSeries
            or DetailEntityType.Audiobook
            or DetailEntityType.ComicIssue
            or DetailEntityType.ComicSeries;

    /// <summary>
    /// Splits a Series' members into the Series run (main-sequence members, numbered ones first, then the unnumbered
    /// tail) and "Also in this world" (supplementary, collected-content and broader-context members). Each list is in
    /// Series order; the second never mixes into the numbered run.
    /// </summary>
    private static (List<SequenceItemViewModel> Run, List<SequenceItemViewModel> AlsoInThisWorld) SplitSeriesRun(
        IEnumerable<SequenceItemViewModel> items)
    {
        var run = new List<SequenceItemViewModel>();
        var also = new List<SequenceItemViewModel>();
        foreach (var item in items)
        {
            (SeriesOrdering.IsAlsoInThisWorld(item.MembershipScope) ? also : run).Add(item);
        }

        return (
            SeriesOrdering.Order(run, SeriesOrderOf),
            SeriesOrdering.Order(also, SeriesOrderOf));
    }

    /// <summary>
    /// A sequence item's position already carries the manifest's ordinal for main-sequence members and the work's own
    /// decimal position otherwise, so it is the sort value as-is.
    /// </summary>
    private static SeriesOrderInput SeriesOrderOf(SequenceItemViewModel item)
        => new(item.MembershipScope, null, item.PositionSort ?? item.PositionNumber, item.PublicationDate, item.Title);

    private static SeriesOrderInput WorkSeriesOrderOf(CollectionWorkSummary work)
        => new(work.MembershipScope, null, work.SequenceSort ?? work.OrdinalSort, work.Year, work.Title);
}
