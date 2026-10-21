using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Library;

namespace MediaEngine.Web.Services.Editing;

/// <summary>One ticked row: what the batch section and the selection bar need to name it.</summary>
public sealed record DetailBatchRow(Guid Id, string Title, DetailEntityType Kind);

/// <summary>
/// The rows ticked on one detail page while Editing Mode is on (details overlay spec D8). It lives on the page next to
/// <see cref="EditingModeContext"/>, holds only ids and names (never the batch's field input, which lives in the open section),
/// and is emptied when Editing Mode ends. The last batch result stays here so the person can read it after Apply.
/// </summary>
public sealed class DetailBatchSelection
{
    private readonly List<DetailBatchRow> _rows = [];

    public IReadOnlyList<DetailBatchRow> Rows => _rows;

    public int Count => _rows.Count;

    public IReadOnlyList<Guid> Ids => [.. _rows.Select(row => row.Id)];

    /// <summary>The Engine takes at most this many items in one batch; ticking stops there.</summary>
    public bool IsFull => _rows.Count >= LibraryBatchEditRequest.MaxEntityIds;

    /// <summary>What is said when a row cannot be ticked because the batch is full.</summary>
    public static string FullNotice => $"You can tick up to {LibraryBatchEditRequest.MaxEntityIds} items at once.";

    /// <summary>The page's editor media type ("Music", "TV", "Books"...), which names the place a move goes to.</summary>
    public string? MediaType { get; set; }

    /// <summary>Refreshes the page behind the rows after a batch changed something (the page's existing commit-refresh path). Set by the page.</summary>
    public Func<Task>? RefreshAsync { get; set; }

    /// <summary>What the last Apply came to, until the ticked rows change.</summary>
    public DetailBatchResultView? Result { get; private set; }

    public event Action? Changed;

    public bool Contains(Guid id) => _rows.Any(row => row.Id == id);

    /// <summary>Ticks the row, or unticks it when it is ticked. Returns false when the row could not be added (the batch is full).</summary>
    public bool Toggle(DetailBatchRow row)
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

        Result = null;
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        if (_rows.Count == 0 && Result is null)
        {
            return;
        }

        _rows.Clear();
        Result = null;
        Changed?.Invoke();
    }

    public void SetResult(DetailBatchResultView? result)
    {
        Result = result;
        Changed?.Invoke();
    }
}

/// <summary>The line and the per-row notes shown after Apply.</summary>
public sealed record DetailBatchResultView(string Line, IReadOnlyList<string> Notes, bool HasProblems);
