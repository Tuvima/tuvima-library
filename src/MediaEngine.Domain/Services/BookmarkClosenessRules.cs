using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Services;

/// <summary>
/// The one place that decides how close two bookmarks are (Global bookmarks spec D7, D12). The Dashboard never repeats this: it
/// shows the label and the gap the Engine sends. Pure, so the rules are tested without a data store.
/// </summary>
public static class BookmarkClosenessRules
{
    /// <summary>Labels a gap (in seconds, pages, or chapters for text) against the limits of its kind of position.</summary>
    public static BookmarkCloseness Classify(BookmarkGapLimits limits, double gap)
    {
        var distance = Math.Abs(gap);
        if (!double.IsFinite(distance))
        {
            return BookmarkCloseness.New;
        }

        if (distance <= limits.SameSpot)
        {
            return BookmarkCloseness.SameSpot;
        }

        if (distance <= limits.Close)
        {
            return BookmarkCloseness.Close;
        }

        return distance <= limits.Far ? BookmarkCloseness.Far : BookmarkCloseness.New;
    }

    /// <summary>
    /// What the person keeps when they just press Apply (spec D7): the incoming bookmark replaces one at the same spot, an existing
    /// one stays when the incoming is merely close or far, and a new one is added.
    /// </summary>
    public static BookmarkRowChoice DefaultChoice(BookmarkCloseness closeness) => closeness switch
    {
        BookmarkCloseness.SameSpot => BookmarkRowChoice.KeepIncoming,
        BookmarkCloseness.New => BookmarkRowChoice.KeepIncoming,
        _ => BookmarkRowChoice.KeepYours,
    };
}

/// <summary>Which of the two cards on a compare row the person kept.</summary>
public enum BookmarkRowChoice
{
    KeepIncoming,
    KeepYours,

    /// <summary>A New row the person ticked off: it is not added.</summary>
    Skip,
}
