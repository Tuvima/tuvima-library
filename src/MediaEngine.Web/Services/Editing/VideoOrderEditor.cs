using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;

namespace MediaEngine.Web.Services.Editing;

/// <summary>How the sort button puts a list in order (universal editor v3.1, D8). A one-shot ordering of the list, never a standing rule.</summary>
public enum VideoSortBy
{
    FileName,
    DateAdded,
}

/// <summary>One video in an order list.</summary>
public sealed record VideoOrderRow(Guid Id, string Title, string FileName, string? AddedAt);

/// <summary>One list of videos: a series, or one section of a series, as saved and as the person has arranged it.</summary>
public sealed class VideoOrderList
{
    public VideoOrderList(Guid parentId, string title, IReadOnlyList<VideoOrderRow> rows, bool canRenameFiles)
    {
        ParentId = parentId;
        Title = title;
        Original = rows;
        Current = [.. rows];
        CanRenameFiles = canRenameFiles;
    }

    public Guid ParentId { get; }

    public string Title { get; }

    /// <summary>The videos as saved.</summary>
    public IReadOnlyList<VideoOrderRow> Original { get; }

    /// <summary>The videos as the person has arranged them (Save has not run yet).</summary>
    public List<VideoOrderRow> Current { get; }

    /// <summary>True when every video of the list is in a Managed folder, so "Rename the files to match this order" may be offered.</summary>
    public bool CanRenameFiles { get; }

    public bool IsChanged => !Current.Select(row => row.Id).SequenceEqual(Original.Select(row => row.Id));

    /// <summary>The videos that were moved into this list from another one (another section).</summary>
    public IReadOnlyList<VideoOrderRow> MovedIn => [.. Current.Where(row => Original.All(saved => saved.Id != row.Id))];
}

/// <summary>
/// The working order of a series' videos while the Match tool is open (universal editor v3.1, WP2 and WP3): move a video up or down, drop it
/// at a place (inside its list or into another section), sort a list once, and turn the staged result into changes on the pending list. Pure
/// state: the Engine is only asked (by the panel) to read the lists, and nothing is saved here; Save changes saves what was staged.
/// </summary>
public sealed class VideoOrderEditor
{
    private readonly List<VideoOrderList> _lists = [];

    /// <summary>The lists in the order shown: one for a series without sections, one per section otherwise.</summary>
    public IReadOnlyList<VideoOrderList> Lists => _lists;

    /// <summary>True when the series has sections (more than one list), so a video can be dragged between them.</summary>
    public bool HasSections => _lists.Count > 1;

    public string SeriesTitle { get; private set; } = string.Empty;

    /// <summary>Starts from what the Engine says, or from nothing for a video that stands alone.</summary>
    public void Load(VideoOrderResponse? response, string? seriesTitle = null)
    {
        _lists.Clear();
        SeriesTitle = seriesTitle ?? string.Empty;
        foreach (var group in response?.Groups ?? [])
        {
            _lists.Add(new VideoOrderList(
                group.ParentId,
                group.Title,
                [.. group.Entries.Select(entry => new VideoOrderRow(entry.Id, entry.Title, entry.FileName, entry.AddedAt))],
                group.CanRenameFiles));
        }

        if (string.IsNullOrWhiteSpace(SeriesTitle) && _lists.Count == 1)
        {
            SeriesTitle = _lists[0].Title;
        }
    }

    /// <summary>Whether any list has two or more videos, the only time an order is worth showing.</summary>
    public bool HasSomethingToOrder => _lists.Any(list => list.Current.Count >= 2) || HasSections;

    public bool CanMoveUp(int list, int index) => InRange(list, index) && index > 0;

    public bool CanMoveDown(int list, int index) => InRange(list, index) && index < _lists[list].Current.Count - 1;

    /// <summary>The arrow buttons: one place up or down inside the list. False when the video is already at that end.</summary>
    public bool Nudge(int list, int index, int step)
    {
        if (!InRange(list, index))
        {
            return false;
        }

        return Move(list, index, list, Math.Clamp(index + step, 0, _lists[list].Current.Count - 1));
    }

    /// <summary>
    /// Drag and drop: takes the video at <paramref name="fromIndex"/> of one list and puts it so it ends up at <paramref name="toIndex"/> of
    /// another (or the same) list. Dropping into a different list is a move into that section.
    /// </summary>
    public bool Move(int fromList, int fromIndex, int toList, int toIndex)
    {
        if (!InRange(fromList, fromIndex) || toList < 0 || toList >= _lists.Count)
        {
            return false;
        }

        var row = _lists[fromList].Current[fromIndex];
        if (fromList == toList)
        {
            var target = Math.Clamp(toIndex, 0, _lists[fromList].Current.Count - 1);
            if (target == fromIndex)
            {
                return false;
            }

            _lists[fromList].Current.RemoveAt(fromIndex);
            _lists[fromList].Current.Insert(target, row);
            return true;
        }

        // A section keeps at least one video, so the last one in a list cannot be dragged out of it.
        if (_lists[fromList].Current.Count <= 1)
        {
            return false;
        }

        _lists[fromList].Current.RemoveAt(fromIndex);
        _lists[toList].Current.Insert(Math.Clamp(toIndex, 0, _lists[toList].Current.Count), row);
        return true;
    }

    /// <summary>
    /// Drag and drop's own wording: the video at <paramref name="fromIndex"/> is dropped on the gap <paramref name="insertAt"/> of a list (0 is
    /// before the first video, the list's count is after the last), counted before the video leaves its place.
    /// </summary>
    public bool Drop(int fromList, int fromIndex, int toList, int insertAt)
    {
        if (toList < 0 || toList >= _lists.Count)
        {
            return false;
        }

        var target = fromList == toList && fromIndex < insertAt ? insertAt - 1 : insertAt;
        return Move(fromList, fromIndex, toList, target);
    }

    /// <summary>The list that has the given series or section id, or -1.</summary>
    public int IndexOfList(Guid parentId) => _lists.FindIndex(list => list.ParentId == parentId);

    /// <summary>The sort button: puts one list in order by file name (numbers read as numbers) or by when it was added, once.</summary>
    public void Sort(int list, VideoSortBy by)
    {
        if (list < 0 || list >= _lists.Count)
        {
            return;
        }

        var sorted = by == VideoSortBy.FileName
            ? _lists[list].Current.OrderBy(row => string.IsNullOrEmpty(row.FileName) ? row.Title : row.FileName, NaturalComparer.Instance)
            : _lists[list].Current
                .OrderBy(row => string.IsNullOrWhiteSpace(row.AddedAt) ? 1 : 0)
                .ThenBy(row => row.AddedAt, StringComparer.Ordinal)
                .ThenBy(row => row.Title, NaturalComparer.Instance);
        var result = sorted.ToList();
        _lists[list].Current.Clear();
        _lists[list].Current.AddRange(result);
    }

    /// <summary>Puts every list back as it was saved.</summary>
    public void Reset()
    {
        foreach (var list in _lists)
        {
            list.Current.Clear();
            list.Current.AddRange(list.Original);
        }
    }

    /// <summary>
    /// Turns the arrangement into staged changes: a video moved into another section becomes a series change for that video, and a list
    /// whose videos are in a new order becomes one order change. Earlier staging for these lists is replaced, so the pending list always
    /// says what Save would do; a list put back as it was stages nothing.
    /// </summary>
    public void Stage(PendingEditList pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        foreach (var list in _lists)
        {
            if (pending.FindVideoOrder(list.ParentId) is { } order)
            {
                pending.Remove(order.Id);
            }

            foreach (var row in list.Original.Concat(list.Current))
            {
                if (pending.FindVideoSeries(row.Id) is { } move && Guid.TryParse(move.TargetId, out var target) && _lists.Any(other => other.ParentId == target))
                {
                    pending.Remove(move.Id);
                }
            }
        }

        foreach (var list in _lists.Where(list => list.IsChanged))
        {
            var movedIn = list.MovedIn;
            foreach (var row in movedIn)
            {
                pending.SetTarget(
                    EditSessionChangeKinds.VideoSeries,
                    row.Id,
                    list.ParentId.ToString(),
                    label: list.Title,
                    note: null,
                    before: "In another section");
            }

            // A move puts the video last; the order is only staged when the list should read differently from that.
            var movedIds = movedIn.Select(row => row.Id).ToHashSet();
            var wanted = list.Current.Select(row => row.Id).ToList();
            var movedOnly = list.Current.Where(row => movedIds.Contains(row.Id)).Select(row => row.Id);
            if (!wanted.SequenceEqual(list.Original.Where(row => wanted.Contains(row.Id)).Select(row => row.Id).Concat(movedOnly)))
            {
                pending.SetVideoOrder(list.ParentId, wanted, list.Title, "As it is now");
            }
        }
    }

    /// <summary>Plain words for what Save would do with this arrangement, shown under the list ("What will happen"); empty when nothing changed. <paramref name="filesRenamed"/> says the rename switch is on, so the sentence does not claim the files stay as they are.</summary>
    public string WhatWillHappen(bool filesRenamed = false)
    {
        var parts = new List<string>();
        foreach (var list in _lists.Where(list => list.IsChanged))
        {
            foreach (var row in list.MovedIn)
            {
                parts.Add($"\"{row.Title}\" moves into {list.Title}.");
            }

            var kept = list.Current.Where(row => list.Original.Any(saved => saved.Id == row.Id)).Select(row => row.Id).ToList();
            var before = list.Original.Where(row => kept.Contains(row.Id)).Select(row => row.Id).ToList();
            if (!kept.SequenceEqual(before))
            {
                parts.Add(HasSections ? $"{list.Title} is put in a new order." : "The videos are put in a new order.");
            }
        }

        if (filesRenamed)
        {
            parts.Add(parts.Count == 0
                ? "The files are renamed to match the order. This cannot be undone."
                : "The order is saved in Library, and the files are renamed to match it. Renaming cannot be undone.");
            return string.Join(' ', parts);
        }

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        parts.Add("This is saved in Library only. Your files are not moved or renamed.");
        return string.Join(' ', parts);
    }

    private bool InRange(int list, int index) =>
        list >= 0 && list < _lists.Count && index >= 0 && index < _lists[list].Current.Count;

    /// <summary>Orders text the way a person counts: "Ep 2" before "Ep 10".</summary>
    private sealed class NaturalComparer : IComparer<string?>
    {
        public static NaturalComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            x ??= string.Empty;
            y ??= string.Empty;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    var startX = i;
                    var startY = j;
                    while (i < x.Length && char.IsDigit(x[i]))
                    {
                        i++;
                    }

                    while (j < y.Length && char.IsDigit(y[j]))
                    {
                        j++;
                    }

                    var numberX = x[startX..i].TrimStart('0');
                    var numberY = y[startY..j].TrimStart('0');
                    var byLength = numberX.Length.CompareTo(numberY.Length);
                    if (byLength != 0)
                    {
                        return byLength;
                    }

                    var byDigits = string.CompareOrdinal(numberX, numberY);
                    if (byDigits != 0)
                    {
                        return byDigits;
                    }

                    continue;
                }

                var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (byChar != 0)
                {
                    return byChar;
                }

                i++;
                j++;
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
