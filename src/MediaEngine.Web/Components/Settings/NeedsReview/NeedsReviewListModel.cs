using MediaEngine.Contracts.Review;

namespace MediaEngine.Web.Components.Settings.NeedsReview;

/// <summary>What one row is showing instead of its normal self: working on an action, finished, or failed (Needs Review redesign D8).</summary>
public sealed class NeedsReviewRowState
{
    /// <summary>True while an action on this row runs; its buttons are disabled.</summary>
    public bool Busy { get; set; }

    /// <summary>The one quiet line a finished row becomes ("04.mkv matched to ..."); null while the row still waits for a person.</summary>
    public string? Done { get; set; }

    /// <summary>The red sentence under a row whose last action failed, with the reason; null otherwise.</summary>
    public string? Failure { get; set; }
}

/// <summary>One line of a group: a single row, or a family of two or more rows that share one show, album or Series (D6).</summary>
public sealed class NeedsReviewEntry(string key, IReadOnlyList<NeedsReviewRowDto> rows, bool family = false)
{
    /// <summary>Stable across reloads: the family's id for a family, the row's own key otherwise.</summary>
    public string Key { get; } = key;

    public IReadOnlyList<NeedsReviewRowDto> Rows { get; } = rows;

    /// <summary>True for a family entry: Fix on it opens the show, album or Series itself, and its menu acts on every file in it.</summary>
    public bool IsFamily { get; } = family;

    public NeedsReviewRowDto First => Rows[0];
}

/// <summary>One of the four groups with the entries it holds and the number of items still waiting in it.</summary>
public sealed class NeedsReviewSection(string group, IReadOnlyList<NeedsReviewEntry> entries, int count)
{
    /// <summary>One of <see cref="NeedsReviewGroups"/>.</summary>
    public string Group { get; } = group;

    public IReadOnlyList<NeedsReviewEntry> Entries { get; } = entries;

    /// <summary>Items still waiting in the group (a finished row no longer counts).</summary>
    public int Count { get; } = count;
}

/// <summary>
/// Turns the Engine's <see cref="NeedsReviewListDto"/> into what the Needs Review page draws: the four groups in their order, each holding
/// single rows or family entries, plus the state of every row. It holds no services and draws nothing, so the sectioning, the families and
/// the counts are tested without a page.
/// </summary>
public sealed class NeedsReviewListModel
{
    /// <summary>The groups in the order the page shows them (spec D1).</summary>
    public static readonly IReadOnlyList<string> GroupOrder =
    [
        NeedsReviewGroups.Ready,
        NeedsReviewGroups.Choose,
        NeedsReviewGroups.NoMatch,
        NeedsReviewGroups.FileProblem,
    ];

    private readonly List<NeedsReviewRowDto> _rows = [];
    private readonly Dictionary<string, NeedsReviewRowState> _states = new(StringComparer.Ordinal);
    private Dictionary<string, int> _groupTotals = [];

    /// <summary>The rows read so far (the page asks for more with Show more).</summary>
    public IReadOnlyList<NeedsReviewRowDto> Rows => _rows;

    public bool HasRows => _rows.Count > 0;

    /// <summary>How many rows the Engine holds for the filter, before paging.</summary>
    public int EngineTotal { get; private set; }

    /// <summary>How many kept items exist for the same library filter.</summary>
    public int KeptTotal { get; private set; }

    /// <summary>Items just kept from this list are hidden at once, so the "hidden" line counts them without waiting for a reload.</summary>
    public void NoteKept(int count) => KeptTotal += count;

    /// <summary>True while rows the Engine holds have not been read yet.</summary>
    public bool HasMore => _rows.Count < EngineTotal;

    /// <summary>Starts again from one page: rows, states and totals are replaced, so a finished row's line leaves with the reload.</summary>
    public void Replace(NeedsReviewListDto page)
    {
        _rows.Clear();
        _states.Clear();
        Append(page);
    }

    /// <summary>Adds the next page.</summary>
    public void Append(NeedsReviewListDto page)
    {
        foreach (var row in page.Items)
        {
            if (_rows.All(existing => existing.Key != row.Key))
            {
                _rows.Add(row);
            }
        }

        EngineTotal = page.Total;
        KeptTotal = page.KeptTotal;
        _groupTotals = new Dictionary<string, int>(page.GroupTotals, StringComparer.Ordinal);
    }

    public NeedsReviewRowState StateOf(string key)
    {
        if (!_states.TryGetValue(key, out var state))
        {
            state = new NeedsReviewRowState();
            _states[key] = state;
        }

        return state;
    }

    /// <summary>True while the row still waits for a person (it has not been finished in this view).</summary>
    public bool IsWaiting(NeedsReviewRowDto row) => StateOf(row.Key).Done is null;

    /// <summary>The row finished: it becomes one quiet line and stops counting.</summary>
    public void Finish(NeedsReviewRowDto row, string line)
    {
        var state = StateOf(row.Key);
        state.Done = line;
        state.Failure = null;
        state.Busy = false;
    }

    public void Fail(NeedsReviewRowDto row, string reason)
    {
        var state = StateOf(row.Key);
        state.Failure = reason;
        state.Busy = false;
    }

    public void SetBusy(NeedsReviewRowDto row, bool busy)
    {
        var state = StateOf(row.Key);
        state.Busy = busy;
        if (busy)
        {
            state.Failure = null;
        }
    }

    /// <summary>Items still waiting in a group: the rows read that have not finished, plus the rows of the group not read yet.</summary>
    public int CountOf(string group)
    {
        var inGroup = _rows.Where(row => GroupOf(row) == group).ToList();
        var waiting = inGroup.Count(IsWaiting);
        var unread = Math.Max(0, _groupTotals.GetValueOrDefault(group) - inGroup.Count);
        return waiting + unread;
    }

    /// <summary>Every item still waiting: the number the page's summary line says.</summary>
    public int WaitingTotal => GroupOrder.Sum(CountOf);

    /// <summary>The groups that hold rows, in order, narrowed by the search text; an empty group is not returned.</summary>
    public IReadOnlyList<NeedsReviewSection> Sections(string? search = null)
    {
        var sections = new List<NeedsReviewSection>();
        foreach (var group in GroupOrder)
        {
            var rows = _rows.Where(row => GroupOf(row) == group && Matches(row, search)).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            sections.Add(new NeedsReviewSection(group, Entries(rows), CountOf(group)));
        }

        return sections;
    }

    /// <summary>Every entry the groups draw, in order, narrowed by the search text.</summary>
    public IReadOnlyList<NeedsReviewEntry> VisibleEntries(string? search = null) =>
        [.. Sections(search).SelectMany(section => section.Entries)];

    /// <summary>The keys of the rows a person can tick right now: still waiting and not hidden by the search.</summary>
    public HashSet<string> TickableKeys(string? search = null) =>
        [.. VisibleEntries(search).SelectMany(entry => entry.Rows).Where(IsWaiting).Select(row => row.Key)];

    /// <summary>
    /// The ticked rows that can still act, entry by entry in the order the list shows them. A finished row or a row the search hides is never
    /// part of it, so an action bar button cannot touch what the person cannot see.
    /// </summary>
    public IReadOnlyList<NeedsReviewTicked> TickedEntries(NeedsReviewSelection selection, string? search = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var ticked = new List<NeedsReviewTicked>();
        foreach (var entry in VisibleEntries(search))
        {
            var waiting = entry.Rows.Where(IsWaiting).ToList();
            var rows = waiting.Where(row => selection.Contains(row.Key)).ToList();
            if (rows.Count > 0)
            {
                ticked.Add(new NeedsReviewTicked(entry, rows, entry.IsFamily && rows.Count > 1 && rows.Count == waiting.Count));
            }
        }

        return ticked;
    }

    /// <summary>How many rows the search leaves.</summary>
    public int MatchingRows(string? search) => _rows.Count(row => Matches(row, search));

    /// <summary>The group a row is drawn in; a group the page does not know is drawn with the rows that found no match.</summary>
    public static string GroupOf(NeedsReviewRowDto row) =>
        GroupOrder.Contains(row.Group) ? row.Group : NeedsReviewGroups.NoMatch;

    // Consecutive rows that share a family become one entry (two or more); the Engine already lists a family's rows together.
    private static List<NeedsReviewEntry> Entries(IReadOnlyList<NeedsReviewRowDto> rows)
    {
        var entries = new List<NeedsReviewEntry>();
        var index = 0;
        while (index < rows.Count)
        {
            var family = rows[index].FamilyKey;
            var end = index + 1;
            if (!string.IsNullOrWhiteSpace(family))
            {
                while (end < rows.Count && string.Equals(rows[end].FamilyKey, family, StringComparison.Ordinal))
                {
                    end++;
                }
            }

            var run = rows.Skip(index).Take(end - index).ToList();
            entries.Add(run.Count >= 2
                ? new NeedsReviewEntry($"family:{rows[index].Group}:{family}", run, family: true)
                : new NeedsReviewEntry(run[0].Key, run));
            index = end;
        }

        return entries;
    }

    private static bool Matches(NeedsReviewRowDto row, string? search) =>
        string.IsNullOrWhiteSpace(search)
        || NeedsReviewRowInfo.SearchText(row).Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
}
