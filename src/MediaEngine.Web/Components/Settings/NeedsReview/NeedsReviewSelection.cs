namespace MediaEngine.Web.Components.Settings.NeedsReview;

/// <summary>How many of a set of rows are ticked: none, some (the partly ticked state) or all.</summary>
public enum NeedsReviewTickState
{
    None,
    Some,
    All,
}

/// <summary>
/// The rows a person has ticked on the Needs Review page (spec needs-review-redesign D4, WP5), by the row's key. It holds only keys: the
/// page decides which rows may be ticked (a finished row cannot) and what the action bar does with them. On a phone the tick boxes show
/// only while something is ticked, so this set is also what "selection mode" is: it ends when the set is empty.
/// </summary>
public sealed class NeedsReviewSelection
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <summary>How many rows are ticked.</summary>
    public int Count => _keys.Count;

    public bool Contains(string key) => _keys.Contains(key);

    /// <summary>Ticks (or unticks) every one of these rows.</summary>
    public void Set(IEnumerable<string> keys, bool selected)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys)
        {
            if (selected)
            {
                _keys.Add(key);
            }
            else
            {
                _keys.Remove(key);
            }
        }
    }

    /// <summary>Unticks everything (the bar's close button and Escape).</summary>
    public void Clear() => _keys.Clear();

    /// <summary>Keeps only the ticks the page still allows (a finished row, a row the search hides or a row that left the list loses its tick).</summary>
    public void Retain(Func<string, bool> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        _keys.RemoveWhere(key => !allowed(key));
    }

    /// <summary>Whether none, some or all of these rows are ticked; an empty set reads as none.</summary>
    public NeedsReviewTickState StateOf(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var total = 0;
        var ticked = 0;
        foreach (var key in keys)
        {
            total++;
            if (_keys.Contains(key))
            {
                ticked++;
            }
        }

        return ticked == 0 ? NeedsReviewTickState.None
            : ticked == total ? NeedsReviewTickState.All
            : NeedsReviewTickState.Some;
    }
}
