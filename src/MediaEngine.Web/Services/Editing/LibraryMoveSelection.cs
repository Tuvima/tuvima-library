using MediaEngine.Contracts.Library;

namespace MediaEngine.Web.Services.Editing;

/// <summary>What the person has decided for one item in the Match step.</summary>
public enum LibraryMoveRowChoice
{
    /// <summary>Nothing chosen yet: the row is highlighted and Apply stays off.</summary>
    Undecided,

    /// <summary>The item takes the number and identity of one entry in the destination's list (or, with no list, simply joins it).</summary>
    Match,

    /// <summary>The item joins with no number, in the Unnumbered group at the end (Details overlay D13).</summary>
    AddAnyway,

    /// <summary>The chosen entry already has a file: the item becomes another version of it (D16).</summary>
    AddAsVersion,
}

/// <summary>What happened when the person clicked an entry in the destination's list.</summary>
public enum LibraryMovePickResult
{
    /// <summary>The entry is now this item's match.</summary>
    Matched,

    /// <summary>The entry already has a file: nothing changed yet, the person decides (add as another version, or pick another).</summary>
    EntryTaken,

    /// <summary>Another item in this move already has that entry.</summary>
    PickedByAnotherItem,

    /// <summary>The entry or the item is not in this preview.</summary>
    Unknown,
}

/// <summary>One item in the Match step: where it is now, the Engine's best guess and what the person decided.</summary>
public sealed class LibraryMoveRowState
{
    internal LibraryMoveRowState(LibraryMovePreviewRow source) => Source = source;

    public LibraryMovePreviewRow Source { get; }

    public Guid ItemId => Source.ItemId;

    public string Title => Source.Title;

    public LibraryMoveRowChoice Choice { get; internal set; }

    /// <summary>The entry chosen for <see cref="LibraryMoveRowChoice.Match"/> or <see cref="LibraryMoveRowChoice.AddAsVersion"/>.</summary>
    public string? EntryKey { get; internal set; }

    /// <summary>True when the Engine's best guess is the entry now chosen, so its reason can be shown beside it.</summary>
    public bool IsSuggestionChosen =>
        Source.SuggestedEntryKey is { } suggested && string.Equals(suggested, EntryKey, StringComparison.Ordinal);

    public bool IsBlocked => !string.IsNullOrWhiteSpace(Source.BlockedReason);
}

/// <summary>
/// The Match step's state for one move preview (details overlay spec D12 to D16): one row per item, each matched to an entry of the
/// destination's list, added anyway, or added as another version. It decides when Apply may turn on, and turns the choices into the
/// commit request. A destination with no list needs no choices: every item simply joins it (D14). The same class serves one item
/// and a batch, so the mapping table the batch editing will open is this one.
/// </summary>
public sealed class LibraryMoveSelection
{
    private readonly List<LibraryMoveRowState> _rows;
    private readonly Dictionary<string, LibraryMoveEntry> _entries;

    private LibraryMoveSelection(LibraryMovePreview preview)
    {
        Preview = preview;
        _rows = [.. preview.Rows.Select(row => new LibraryMoveRowState(row))];
        _entries = new Dictionary<string, LibraryMoveEntry>(StringComparer.Ordinal);
        foreach (var entry in preview.Entries)
        {
            _entries[entry.EntryKey] = entry;
        }
    }

    public LibraryMovePreview Preview { get; }

    public IReadOnlyList<LibraryMoveRowState> Rows => _rows;

    /// <summary>True when the destination has its own list, so every item goes through the Match step (D12). False means D14: no list, no gate.</summary>
    public bool HasList => !string.Equals(Preview.ListKind, LibraryMoveListKinds.None, StringComparison.Ordinal);

    public bool IsSingle => _rows.Count == 1;

    /// <summary>
    /// Starts from a fresh preview: the Engine's best guess is pre-selected when it is free (a guess that already has a file is shown
    /// but not chosen, because that is the person's decision, D16). When <paramref name="previous"/> is given (the Engine answered that
    /// something changed) the person's earlier choices are kept wherever they are still possible.
    /// </summary>
    public static LibraryMoveSelection From(LibraryMovePreview preview, LibraryMoveSelection? previous = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var selection = new LibraryMoveSelection(preview);
        foreach (var row in selection._rows)
        {
            if (!selection.HasList)
            {
                row.Choice = LibraryMoveRowChoice.Match;
                continue;
            }

            if (previous?.Find(row.ItemId) is { Choice: not LibraryMoveRowChoice.Undecided } earlier && selection.TryKeep(row, earlier))
            {
                continue;
            }

            if (row.Source.SuggestedEntryKey is { } suggested
                && selection._entries.TryGetValue(suggested, out var entry)
                && IsFreeFor(entry, row.ItemId)
                && !selection.IsPickedByOtherRow(row.ItemId, suggested))
            {
                row.Choice = LibraryMoveRowChoice.Match;
                row.EntryKey = suggested;
            }
        }

        return selection;
    }

    public LibraryMoveRowState? Find(Guid itemId) => _rows.FirstOrDefault(row => row.ItemId == itemId);

    public LibraryMoveEntry? Entry(string? entryKey) =>
        entryKey is not null && _entries.TryGetValue(entryKey, out var entry) ? entry : null;

    /// <summary>The row, if any, whose chosen entry is <paramref name="entryKey"/>.</summary>
    public LibraryMoveRowState? RowChoosing(string entryKey) =>
        _rows.FirstOrDefault(row => row.Choice is LibraryMoveRowChoice.Match or LibraryMoveRowChoice.AddAsVersion
            && string.Equals(row.EntryKey, entryKey, StringComparison.Ordinal));

    public bool IsPickedByOtherRow(Guid itemId, string entryKey) => RowChoosing(entryKey) is { } holder && holder.ItemId != itemId;

    /// <summary>True when the entry already has a file that is not this item's own.</summary>
    public bool IsTaken(string entryKey, Guid itemId) => Entry(entryKey) is { } entry && !IsFreeFor(entry, itemId);

    /// <summary>The person clicks an entry for an item. A free entry becomes the match; a taken one asks first (D16) and changes nothing yet.</summary>
    public LibraryMovePickResult Pick(Guid itemId, string entryKey)
    {
        if (Find(itemId) is not { } row || Entry(entryKey) is not { } entry)
        {
            return LibraryMovePickResult.Unknown;
        }

        if (IsPickedByOtherRow(itemId, entryKey))
        {
            return LibraryMovePickResult.PickedByAnotherItem;
        }

        if (!IsFreeFor(entry, itemId))
        {
            return LibraryMovePickResult.EntryTaken;
        }

        row.Choice = LibraryMoveRowChoice.Match;
        row.EntryKey = entryKey;
        return LibraryMovePickResult.Matched;
    }

    /// <summary>The person accepted "Add as another version" for a taken entry. Refused unless the Engine said it can honour it.</summary>
    public bool AddAsVersion(Guid itemId, string entryKey)
    {
        if (Find(itemId) is not { } row
            || Entry(entryKey) is not { CanAddVersion: true } entry
            || IsFreeFor(entry, itemId)
            || IsPickedByOtherRow(itemId, entryKey))
        {
            return false;
        }

        row.Choice = LibraryMoveRowChoice.AddAsVersion;
        row.EntryKey = entryKey;
        return true;
    }

    /// <summary>Add anyway for one item: no number, no borrowed details, end of the list.</summary>
    public void AddAnyway(Guid itemId)
    {
        if (Find(itemId) is { } row)
        {
            row.Choice = LibraryMoveRowChoice.AddAnyway;
            row.EntryKey = null;
        }
    }

    /// <summary>"Add unmatched without numbers" (D13): every item still without a choice is added anyway.</summary>
    public void AddUnmatchedWithoutNumbers()
    {
        foreach (var row in _rows.Where(row => row.Choice == LibraryMoveRowChoice.Undecided))
        {
            row.Choice = LibraryMoveRowChoice.AddAnyway;
        }
    }

    /// <summary>Takes a choice back, so the item is highlighted as unmatched again.</summary>
    public void Clear(Guid itemId)
    {
        if (Find(itemId) is { } row && HasList)
        {
            row.Choice = LibraryMoveRowChoice.Undecided;
            row.EntryKey = null;
        }
    }

    public int UndecidedCount => _rows.Count(row => row.Choice == LibraryMoveRowChoice.Undecided);

    public bool HasBlockedRow => _rows.Any(row => row.IsBlocked);

    public bool HasAddAnyway => _rows.Any(row => row.Choice == LibraryMoveRowChoice.AddAnyway);

    public bool HasAddAsVersion => _rows.Any(row => row.Choice == LibraryMoveRowChoice.AddAsVersion);

    /// <summary>
    /// Apply is on only when nothing is blocked (a title still being processed) and every item has a choice: matched to an entry,
    /// added as another version, or added anyway. With no list there is nothing to choose (D14).
    /// </summary>
    public bool CanApply => _rows.Count > 0 && !HasBlockedRow && UndecidedCount == 0;

    /// <summary>The commit the Engine confirms against the preview's token. Only valid when <see cref="CanApply"/>.</summary>
    public LibraryMoveCommitRequest ToCommitRequest() => new()
    {
        PreviewToken = Preview.PreviewToken,
        Rows =
        [
            .. _rows.Select(row => new LibraryMoveCommitRow
            {
                ItemId = row.ItemId,
                Mode = row.Choice switch
                {
                    LibraryMoveRowChoice.AddAnyway => LibraryMoveModes.AddAnyway,
                    LibraryMoveRowChoice.AddAsVersion => LibraryMoveModes.AddAsVersion,
                    _ => LibraryMoveModes.Match,
                },
                EntryKey = row.Choice is LibraryMoveRowChoice.Match or LibraryMoveRowChoice.AddAsVersion && HasList ? row.EntryKey : null,
            }),
        ],
    };

    private static bool IsFreeFor(LibraryMoveEntry entry, Guid itemId) =>
        entry.TakenByItemId is null || entry.TakenByItemId == itemId;

    // Keeps an earlier choice when the fresh list still allows it exactly as before.
    private bool TryKeep(LibraryMoveRowState row, LibraryMoveRowState earlier)
    {
        if (earlier.Choice == LibraryMoveRowChoice.AddAnyway)
        {
            row.Choice = LibraryMoveRowChoice.AddAnyway;
            return true;
        }

        if (earlier.EntryKey is not { } key || !_entries.TryGetValue(key, out var entry) || IsPickedByOtherRow(row.ItemId, key))
        {
            return false;
        }

        if (earlier.Choice == LibraryMoveRowChoice.Match && IsFreeFor(entry, row.ItemId))
        {
            row.Choice = LibraryMoveRowChoice.Match;
            row.EntryKey = key;
            return true;
        }

        if (earlier.Choice == LibraryMoveRowChoice.AddAsVersion && entry is { CanAddVersion: true } && !IsFreeFor(entry, row.ItemId))
        {
            row.Choice = LibraryMoveRowChoice.AddAsVersion;
            row.EntryKey = key;
            return true;
        }

        return false;
    }
}
