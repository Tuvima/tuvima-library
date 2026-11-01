using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Components.Import;

/// <summary>
/// The list a person is turning into a collection in the New collection flow (Collections and lists spec 4b "From a list"): the
/// file they dropped, what Library found in it, the choices they made and the unmatched titles they fixed by hand. Nothing is saved
/// until the last step creates the collection(s).
/// </summary>
public sealed class ListImportDraft
{
    private readonly Dictionary<int, ListEntryOverrideDto> _overrides = [];
    private readonly HashSet<string> _shelves = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file or pasted text, kept in memory.</summary>
    public ImportRecognition? Recognition { get; private set; }

    /// <summary>The list as read from the whole file, with every shelf. Set when the file is recognised.</summary>
    public ListImportPreviewDto? Whole { get; private set; }

    /// <summary>The list as it stands for the chosen shelf (or the whole file), with the hand fixes applied.</summary>
    public ListImportPreviewDto? Current { get; set; }

    public string Order { get; set; } = ListImportChoices.OrderList;

    public string Refresh { get; set; } = ListImportChoices.RefreshNever;

    public string SyncMode { get; set; } = ListImportChoices.SyncFollow;

    /// <summary>The shelves ticked. None ticked means one collection of the whole list; each ticked shelf makes its own collection.</summary>
    public IReadOnlySet<string> Shelves => _shelves;

    public IReadOnlyList<ListEntryOverrideDto> Overrides => _overrides.Values.ToList();

    public bool HasList => Whole is not null && Recognition is not null;

    /// <summary>True when the list came from a web address, so it can be looked at again where it came from.</summary>
    public bool IsWeb => Whole?.IsWebSource == true;

    /// <summary>The single shelf to preview, or null for the whole list (none ticked, or several that each make their own collection).</summary>
    public string? PreviewShelf => _shelves.Count == 1 ? _shelves.First() : null;

    /// <summary>How many collections the Save will make.</summary>
    public int CollectionCount => Math.Max(1, _shelves.Count);

    /// <summary>The titles Library owns across what will be made. Several shelves count the owned titles of each ticked shelf.</summary>
    public int OwnedCount => _shelves.Count > 1 && Whole is not null
        ? Whole.Shelves.Where(shelf => _shelves.Contains(shelf.Name)).Sum(shelf => shelf.OwnedCount)
        : Current?.OwnedCount ?? 0;

    public int EntryCount => _shelves.Count > 1 && Whole is not null
        ? Whole.Shelves.Where(shelf => _shelves.Contains(shelf.Name)).Sum(shelf => shelf.EntryCount)
        : Current?.EntryCount ?? 0;

    public void Set(ImportRecognition recognition, ListImportPreviewDto list)
    {
        Recognition = recognition;
        Whole = list;
        Current = list;
        _overrides.Clear();
        _shelves.Clear();

        // A web list is looked at again every week unless the person says otherwise; a file has nowhere to look again.
        Refresh = list.IsWebSource ? ListImportChoices.RefreshWeekly : ListImportChoices.RefreshNever;
    }

    public void Clear()
    {
        Recognition = null;
        Whole = null;
        Current = null;
        _overrides.Clear();
        _shelves.Clear();
    }

    public void SetShelf(string shelf, bool selected)
    {
        if (selected)
        {
            _shelves.Add(shelf);
        }
        else
        {
            _shelves.Remove(shelf);
        }
    }

    public void Pick(int position, Guid workId) =>
        _overrides[position] = new ListEntryOverrideDto { Position = position, WorkId = workId };

    public void Ignore(int position) =>
        _overrides[position] = new ListEntryOverrideDto { Position = position, Ignore = true };

    public void Restore(int position) => _overrides.Remove(position);
}
