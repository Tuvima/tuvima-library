using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Library;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// One ticked row: what the selection bar and the bulk panel need to name it. <see cref="ParentId"/> is the album, season or
/// audiobook it sits in (null when the list does not know), <see cref="MediaType"/> the editor media type ("Music", "TV"...) when the
/// row knows it better than the page does.
/// </summary>
public sealed record BulkEditRow(Guid Id, string Title, DetailEntityType Kind, Guid? ParentId = null, string? MediaType = null);

/// <summary>What the right-hand panel shows for the ticked rows (universal editor spec D14).</summary>
public enum BulkPanelKind
{
    None,

    /// <summary>"Edit 4 songs": the Details fields, Renumber sequentially and Change album.</summary>
    Edit,

    /// <summary>"Move to album" or "Move to season": pick the place, stage the move for every ticked row.</summary>
    Move,

    /// <summary>"Match": pair the ticked rows with the entries of the album or season they already sit in.</summary>
    Match,
}

/// <summary>
/// The rows ticked in one list (a track or episode list on a detail page, the songs list, a library table) while the editor is
/// available (universal editor spec D14). It holds only ids and names plus which bulk panel is open; the staged changes live in the
/// page's <see cref="PendingEditList"/>, so ticking, unticking or closing the panel never loses them. A list sets
/// <see cref="ListOrder"/> so "Renumber sequentially" follows the order the list shows now.
/// </summary>
public sealed class BulkEditSelection
{
    private readonly List<BulkEditRow> _rows = [];

    public IReadOnlyList<BulkEditRow> Rows => _rows;

    public int Count => _rows.Count;

    public IReadOnlyList<Guid> Ids => [.. _rows.Select(row => row.Id)];

    /// <summary>The Engine takes at most this many items in one request; ticking stops there.</summary>
    public bool IsFull => _rows.Count >= LibraryBatchEditRequest.MaxEntityIds;

    /// <summary>What is said when a row cannot be ticked because the selection is full.</summary>
    public static string FullNotice => $"You can tick up to {LibraryBatchEditRequest.MaxEntityIds} items at once.";

    /// <summary>The page's editor media type ("Music", "TV", "Books"...), which names the place a move goes to.</summary>
    public string? MediaType { get; set; }

    /// <summary>Refreshes the page behind the rows after something changed outside the pending list (a delete). Set by the page.</summary>
    public Func<Task>? RefreshAsync { get; set; }

    /// <summary>The ids of the list's rows in the order it shows them now. Set by the list that renders the rows; null means tick order.</summary>
    public Func<IReadOnlyList<Guid>>? ListOrder { get; set; }

    /// <summary>The bulk panel that should be open, or <see cref="BulkPanelKind.None"/>. The host shows it; closing the panel keeps the ticks.</summary>
    public BulkPanelKind Panel { get; private set; }

    /// <summary>Goes up after a save reached the library, so a panel that read the items' values reads them again.</summary>
    public int DataVersion { get; private set; }

    public event Action? Changed;

    /// <summary>A save reached the library: the values the panel read are out of date.</summary>
    public void NotifySaved()
    {
        DataVersion++;
        Changed?.Invoke();
    }

    public bool Contains(Guid id) => _rows.Any(row => row.Id == id);

    /// <summary>The ticked rows in the order the list shows them (tick order when the list has not said).</summary>
    public IReadOnlyList<BulkEditRow> OrderedRows()
    {
        if (ListOrder?.Invoke() is not { Count: > 0 } order)
        {
            return _rows;
        }

        var position = new Dictionary<Guid, int>();
        for (var index = 0; index < order.Count; index++)
        {
            position.TryAdd(order[index], index);
        }

        return [.. _rows
            .Select((row, index) => (row, index))
            .OrderBy(pair => position.GetValueOrDefault(pair.row.Id, int.MaxValue))
            .ThenBy(pair => pair.index)
            .Select(pair => pair.row)];
    }

    /// <summary>Ticks the row, or unticks it when it is ticked. Returns false when the row could not be added (the selection is full).</summary>
    public bool Toggle(BulkEditRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var existing = _rows.FindIndex(candidate => candidate.Id == row.Id);
        if (existing >= 0)
        {
            _rows.RemoveAt(existing);
        }
        else if (IsFull || row.Id == Guid.Empty)
        {
            return false;
        }
        else
        {
            _rows.Add(row);
        }

        CloseWhenEmpty();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Makes exactly these rows the selection (a list that keeps its own ticks tells the host). Does nothing when nothing changed.</summary>
    public void Replace(IEnumerable<BulkEditRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var next = rows.Where(row => row.Id != Guid.Empty).Take(LibraryBatchEditRequest.MaxEntityIds).ToList();
        if (next.Count == _rows.Count && next.Zip(_rows).All(pair => pair.First == pair.Second))
        {
            return;
        }

        _rows.Clear();
        _rows.AddRange(next);
        CloseWhenEmpty();
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_rows.Count == 0 && Panel == BulkPanelKind.None)
        {
            return;
        }

        _rows.Clear();
        Panel = BulkPanelKind.None;
        Changed?.Invoke();
    }

    public void OpenPanel(BulkPanelKind kind)
    {
        if (_rows.Count == 0 || Panel == kind)
        {
            return;
        }

        Panel = kind;
        Changed?.Invoke();
    }

    public void ClosePanel()
    {
        if (Panel == BulkPanelKind.None)
        {
            return;
        }

        Panel = BulkPanelKind.None;
        Changed?.Invoke();
    }

    private void CloseWhenEmpty()
    {
        if (_rows.Count == 0)
        {
            Panel = BulkPanelKind.None;
        }
    }
}
