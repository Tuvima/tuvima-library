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
    private readonly HashSet<string> _carried = new(StringComparer.Ordinal);
    private Dictionary<string, int> _unreadByGroup = [];
    private int _unread;

    /// <summary>Raised when a row's state changes (busy, finished, failed), so the page draws every part that shows it again.</summary>
    public event Action? Changed;

    /// <summary>The rows read so far (the page asks for more with Show more).</summary>
    public IReadOnlyList<NeedsReviewRowDto> Rows => _rows;

    public bool HasRows => _rows.Count > 0;

    /// <summary>How many rows the Engine holds for the filter, before paging.</summary>
    public int EngineTotal { get; private set; }

    /// <summary>How many kept items exist for the same library filter.</summary>
    public int KeptTotal { get; private set; }

    /// <summary>Items just kept from this list are hidden at once, so the "hidden" line counts them without waiting for a reload.</summary>
    public void NoteKept(int count) => KeptTotal += count;

    /// <summary>True while rows the Engine holds for the filter have not been read yet (rows finished here no longer count on either side).</summary>
    public bool HasMore => _unread > 0;

    /// <summary>How many rows to skip when asking for the next page: the rows read from the Engine that still wait (a finished row is no longer listed).</summary>
    public int WaitingRead => _rows.Count(IsWaiting);

    /// <summary>Starts again from one page: rows and states are replaced, so every finished line leaves (a first load, or a changed library).</summary>
    public void Reset(NeedsReviewListDto page) => Replace(page, keepFinished: false);

    /// <summary>
    /// Replaces the rows with the Engine's fresh page. With <paramref name="keepFinished"/> a row finished in this view stays where it was
    /// with its line, until the page is left (spec D8); a fresh row with the same key wins, so a row put back or reopened is waiting again.
    /// </summary>
    public void Replace(NeedsReviewListDto page, bool keepFinished = false)
    {
        ArgumentNullException.ThrowIfNull(page);
        var oldRows = _rows.ToList();
        var oldStates = new Dictionary<string, NeedsReviewRowState>(_states, StringComparer.Ordinal);
        var fresh = new HashSet<string>(page.Items.Select(row => row.Key), StringComparer.Ordinal);

        _rows.Clear();
        _states.Clear();
        _carried.Clear();
        foreach (var row in page.Items)
        {
            if (_rows.All(existing => existing.Key != row.Key))
            {
                _rows.Add(row);
                // A row that is still listed keeps a running action or a failure; a finished line it had is dropped, the Engine says it waits.
                if (keepFinished && oldStates.TryGetValue(row.Key, out var state))
                {
                    state.Done = null;
                    _states[row.Key] = state;
                }
            }
        }

        if (keepFinished)
        {
            for (var index = 0; index < oldRows.Count; index++)
            {
                var row = oldRows[index];
                if (fresh.Contains(row.Key) || !oldStates.TryGetValue(row.Key, out var state) || state.Done is null)
                {
                    continue;
                }

                // Back in its old place: right after the nearest earlier row that is still in the list, else first.
                var at = 0;
                for (var before = index - 1; before >= 0; before--)
                {
                    var found = _rows.FindIndex(existing => existing.Key == oldRows[before].Key);
                    if (found >= 0)
                    {
                        at = found + 1;
                        break;
                    }
                }

                _rows.Insert(at, row);
                _states[row.Key] = state;
                _carried.Add(row.Key);
            }
        }

        Settle(page);
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

        Settle(page);
    }

    // What the Engine holds beyond the rows now waiting here: its totals less the waiting rows read, per group and overall. Counted when
    // the page arrives, so rows finished afterwards (which leave the Engine's list) are subtracted from the waiting side only.
    private void Settle(NeedsReviewListDto page)
    {
        EngineTotal = page.Total;
        KeptTotal = page.KeptTotal;
        var waiting = _rows.Where(IsWaiting).ToList();
        _unread = Math.Max(0, page.Total - waiting.Count);
        _unreadByGroup = page.GroupTotals.ToDictionary(
            total => total.Key,
            total => Math.Max(0, total.Value - waiting.Count(row => GroupOf(row) == total.Key)),
            StringComparer.Ordinal);
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
        Changed?.Invoke();
    }

    public void Fail(NeedsReviewRowDto row, string reason)
    {
        var state = StateOf(row.Key);
        state.Failure = reason;
        state.Busy = false;
        Changed?.Invoke();
    }

    public void SetBusy(NeedsReviewRowDto row, bool busy)
    {
        var state = StateOf(row.Key);
        state.Busy = busy;
        if (busy)
        {
            state.Failure = null;
        }

        Changed?.Invoke();
    }

    /// <summary>True while an action on this row runs; a second press on it, from a row, its menu or the bar, must do nothing.</summary>
    public bool IsBusy(NeedsReviewRowDto row) => StateOf(row.Key).Busy;

    /// <summary>Items still waiting in a group: the rows read that have not finished, plus the rows of the group not read yet.</summary>
    public int CountOf(string group) =>
        _rows.Count(row => GroupOf(row) == group && IsWaiting(row)) + _unreadByGroup.GetValueOrDefault(group);

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
