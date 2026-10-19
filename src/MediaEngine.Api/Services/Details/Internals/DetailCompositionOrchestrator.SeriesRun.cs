using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Constants;
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
            if (SeriesOrdering.IsAlsoInThisWorld(item.MembershipScope))
            {
                also.Add(item);
            }
            else
            {
                run.Add(string.Equals(item.MembershipScope, SeriesMembershipScopeNames.Unpositioned, StringComparison.OrdinalIgnoreCase)
                    && (item.PositionSort.HasValue || item.PositionNumber.HasValue || item.PositionLabel is not null || item.PositionText is not null)
                    ? WithoutPosition(item)
                    : item);
            }
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

    /// <summary>
    /// The media type of the Series a page shows, so its manifest rows come from that Series' own shelf: a book Series and
    /// an audiobook Series with the same QID never meet. Null (no filter) for media that have no Wikidata Series shelf.
    /// </summary>
    private static string? SeriesMediaTypeName(DetailEntityType entityType) => entityType switch
    {
        DetailEntityType.Book or DetailEntityType.BookSeries => "Books",
        DetailEntityType.Audiobook => "Audiobooks",
        DetailEntityType.Movie or DetailEntityType.MovieSeries => "Movies",
        DetailEntityType.ComicIssue or DetailEntityType.ComicSeries => "Comics",
        DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode => "TV",
        _ => null,
    };

    /// <summary>
    /// The list the previous/next navigation walks and where the current item sits in it: its own list (the Series run, or
    /// "Also in this world" when the current item is there). Null when there is nothing to show. When no item is marked
    /// current (every item is in "Also in this world"), the first item of the non-empty list stands in.
    /// </summary>
    private static (List<SequenceItemViewModel> Navigation, int CurrentIndex, bool CurrentIsAlso)? ResolveSequenceNavigation(
        List<SequenceItemViewModel> run,
        List<SequenceItemViewModel> alsoInThisWorld)
    {
        var currentIsAlso = alsoInThisWorld.Any(item => item.IsCurrent);
        var navigation = currentIsAlso || run.Count == 0 ? alsoInThisWorld : run;
        if (navigation.Count == 0)
        {
            return null;
        }

        return (navigation, Math.Max(0, navigation.FindIndex(item => item.IsCurrent)), navigation == alsoInThisWorld);
    }

    /// <summary>
    /// A member the manifest calls Unpositioned stays in the Series after the numbered run and is not numbered: its own
    /// retail position is dropped.
    /// </summary>
    private static SequenceItemViewModel WithoutPosition(SequenceItemViewModel item) => new()
    {
        Id = item.Id,
        EntityType = item.EntityType,
        Title = item.Title,
        ArtworkUrl = item.ArtworkUrl,
        EpisodeStillUrl = item.EpisodeStillUrl,
        EpisodeStillWidthPx = item.EpisodeStillWidthPx,
        EpisodeStillHeightPx = item.EpisodeStillHeightPx,
        Route = item.Route,
        Description = item.Description,
        Duration = item.Duration,
        PublicationDate = item.PublicationDate,
        PositionNumber = null,
        PositionSort = null,
        PositionLabel = null,
        PositionText = null,
        GroupKey = item.GroupKey,
        GroupTitle = item.GroupTitle,
        MembershipScope = item.MembershipScope,
        IsCurrent = item.IsCurrent,
        IsOwned = item.IsOwned,
        ProgressState = item.ProgressState,
        EpisodeContext = item.EpisodeContext,
        ProgressPercent = item.ProgressPercent,
        PositionSeconds = item.PositionSeconds,
        DurationSeconds = item.DurationSeconds,
        RemainingSeconds = item.RemainingSeconds,
        ProgressLabel = item.ProgressLabel,
        CoveredWith = item.CoveredWith,
    };

    private static SeriesOrderInput WorkSeriesOrderOf(CollectionWorkSummary work)
        => new(work.MembershipScope, null, work.SequenceSort ?? work.OrdinalSort, work.Year, work.Title);
}
