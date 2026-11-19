using MediaEngine.Contracts.Details;

namespace MediaEngine.Web.Components.Details;

/// <summary>
/// How many titles of a Series a person owns (issue 1191, decided by Shy: novellas and short stories count). One rule for every
/// count the details page draws, so the line under the list and the "In library" fact cannot disagree: the owned titles of the numbered
/// Series run plus the owned titles listed as "Also in this world" (supplementary, collected-content and broader-context members).
/// </summary>
public static class SeriesOwnedCount
{
    /// <summary>The owned titles of the numbered Series run, across all of its sections.</summary>
    public static int InRun(SequencePlacementViewModel placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return placement.Groups.Count > 0
            ? placement.Groups.Sum(group => group.OwnedCount)
            : OwnedEntries(placement.OrderedItems);
    }

    /// <summary>The owned titles listed as "Also in this world".</summary>
    public static int InAlsoInThisWorld(SequencePlacementViewModel placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return OwnedEntries(placement.AlsoInThisWorld);
    }

    /// <summary>The one total: the numbered run and "Also in this world" together.</summary>
    public static int Of(SequencePlacementViewModel placement) => InRun(placement) + InAlsoInThisWorld(placement);

    /// <summary>
    /// The total the owned count is "of": the Series' known total for its numbered run, plus every title listed as "Also in this
    /// world", never less than what is owned.
    /// </summary>
    public static int KnownTotal(SequencePlacementViewModel placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var mainItems = placement.OrderedItems.Sum(Weight);
        var run = Math.Max(placement.TotalKnownItems ?? 0, mainItems);
        return Math.Max(run + placement.AlsoInThisWorld.Sum(Weight), Of(placement));
    }

    // An entry for one file that covers two episodes ("E19-20: A + B") stands for both: episodes count every episode a file covers.
    private static int Weight(SequenceItemViewModel item) => Math.Max(1, item.EpisodeCount);

    private static int OwnedEntries(IEnumerable<SequenceItemViewModel> items) => items.Where(item => item.IsOwned).Sum(Weight);
}
