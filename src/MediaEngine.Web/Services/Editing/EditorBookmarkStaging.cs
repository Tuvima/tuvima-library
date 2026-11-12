using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// How a staged bookmark operation joins the ones already staged for a title (universal editor v5 D37, WP15). The list always says what Save
/// would do, so a later operation can cancel or replace an earlier one: taking a bookmark out of Everyone's right after adding it leaves
/// nothing staged; renaming or moving the same bookmark twice keeps only the last; start over throws the earlier operations away. Pure: no
/// calls to the Engine, nothing is changed in place.
/// </summary>
public static class EditorBookmarkStaging
{
    /// <summary>The operations after <paramref name="next"/> is staged on top of <paramref name="ops"/>.</summary>
    public static IReadOnlyList<EditorBookmarkOp> Stage(IReadOnlyList<EditorBookmarkOp> ops, EditorBookmarkOp next)
    {
        ArgumentNullException.ThrowIfNull(ops);
        ArgumentNullException.ThrowIfNull(next);
        var list = ops.ToList();
        if (next.Kind is EditorBookmarkOpKinds.Remove or EditorBookmarkOpKinds.Rename or EditorBookmarkOpKinds.ChangeTime
            && next.BookmarkId is { } target
            && FindImported(list, target) is var (opIndex, itemIndex))
        {
            // A bookmark that only exists in a staged import is changed inside that import.
            return ChangeImported(list, opIndex, itemIndex, next);
        }

        switch (next.Kind)
        {
            case EditorBookmarkOpKinds.StartOver:
                // Everything staged before is moot: the list is pooled from the profiles again, and later operations start from there.
                return [next];

            case EditorBookmarkOpKinds.Add:
                if (list.Any(op => op.Kind == EditorBookmarkOpKinds.Add && op.BookmarkId == next.BookmarkId))
                {
                    return list;
                }

                // Putting back a bookmark that was staged to leave simply cancels that removal.
                if (list.FindIndex(op => op.Kind == EditorBookmarkOpKinds.Remove && op.BookmarkId == next.BookmarkId) is var removal and >= 0)
                {
                    list.RemoveAt(removal);
                    return list;
                }

                list.Add(next);
                return list;

            case EditorBookmarkOpKinds.Remove:
                if (list.Any(op => op.Kind == EditorBookmarkOpKinds.Remove && op.BookmarkId == next.BookmarkId))
                {
                    return list;
                }

                // A rename or move of a bookmark that is now leaving means nothing; and one that was only staged to be added never was.
                var wasStagedToAdd = list.Any(op => op.Kind == EditorBookmarkOpKinds.Add && op.BookmarkId == next.BookmarkId);
                list.RemoveAll(op => op.BookmarkId == next.BookmarkId && op.Kind is EditorBookmarkOpKinds.Add or EditorBookmarkOpKinds.Rename or EditorBookmarkOpKinds.ChangeTime);
                if (!wasStagedToAdd)
                {
                    list.Add(next);
                }

                return list;

            case EditorBookmarkOpKinds.Rename or EditorBookmarkOpKinds.ChangeTime:
                return ReplaceSame(list, next);

            default:
                list.Add(next);
                return list;
        }
    }

    /// <summary>The operations without the one with <paramref name="opId"/> (Review's "take this change back out").</summary>
    public static IReadOnlyList<EditorBookmarkOp> Without(IReadOnlyList<EditorBookmarkOp> ops, string opId)
    {
        ArgumentNullException.ThrowIfNull(ops);
        return [.. ops.Where(op => !string.Equals(op.Id, opId, StringComparison.Ordinal))];
    }

    private static (int Op, int Item)? FindImported(List<EditorBookmarkOp> list, Guid bookmarkId)
    {
        for (var opIndex = 0; opIndex < list.Count; opIndex++)
        {
            if (list[opIndex].Kind != EditorBookmarkOpKinds.Import)
            {
                continue;
            }

            for (var itemIndex = 0; itemIndex < list[opIndex].Items.Count; itemIndex++)
            {
                if (EditorBookmarkProjection.ImportedId(list[opIndex].Id, itemIndex) == bookmarkId)
                {
                    return (opIndex, itemIndex);
                }
            }
        }

        return null;
    }

    private static List<EditorBookmarkOp> ChangeImported(List<EditorBookmarkOp> list, int opIndex, int itemIndex, EditorBookmarkOp change)
    {
        var import = list[opIndex];
        var items = import.Items.ToList();
        switch (change.Kind)
        {
            case EditorBookmarkOpKinds.Remove:
                items.RemoveAt(itemIndex);
                break;
            case EditorBookmarkOpKinds.Rename:
                items[itemIndex] = items[itemIndex] with { Name = change.Name?.Trim() };
                break;
            default:
                items[itemIndex] = items[itemIndex] with { PositionSeconds = change.PositionSeconds ?? items[itemIndex].PositionSeconds };
                break;
        }

        if (items.Count == 0)
        {
            list.RemoveAt(opIndex);
        }
        else
        {
            list[opIndex] = import with { Items = items };
        }

        return list;
    }

    private static List<EditorBookmarkOp> ReplaceSame(List<EditorBookmarkOp> list, EditorBookmarkOp next)
    {
        var index = list.FindIndex(op => op.Kind == next.Kind && op.BookmarkId == next.BookmarkId);
        if (index >= 0)
        {
            list[index] = next;
        }
        else
        {
            list.Add(next);
        }

        return list;
    }
}

/// <summary>What a staged operation did to a row, so the row can say so ("staged") until Save.</summary>
public enum EditorBookmarkRowMark
{
    None,

    /// <summary>Added to Everyone's.</summary>
    Added,

    /// <summary>Staged to leave Everyone's.</summary>
    Removed,

    /// <summary>Given another name.</summary>
    Renamed,

    /// <summary>Moved to another time.</summary>
    Moved,

    /// <summary>Came in from an imported file.</summary>
    Imported,
}

/// <summary>One row as the tab draws it: the Engine's row with whatever is staged on top of it.</summary>
public sealed record EditorBookmarkViewRow(EditorBookmarkRowDto Row, EditorBookmarkRowMark Mark)
{
    public Guid Id => Row.Id;
}

/// <summary>A title's two lists with the staged operations laid over them.</summary>
public sealed record EditorBookmarkView(
    string State,
    bool StateStaged,
    IReadOnlyList<EditorBookmarkViewRow> Everyone,
    IReadOnlyList<EditorBookmarkViewRow> FromProfiles,
    int StagedCount);

/// <summary>
/// Lays the staged operations over the Engine's lists (the page behind the window never changes while editing; the tab itself shows what Save
/// would do). Pure. The Engine decides the real result on Save (same-spot merging among it); this is only the preview.
/// </summary>
public static class EditorBookmarkProjection
{
    public static EditorBookmarkView Apply(EditorBookmarksDto loaded, IReadOnlyList<EditorBookmarkOp> ops)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(ops);
        var everyone = loaded.Everyone.Select(row => new EditorBookmarkViewRow(row, EditorBookmarkRowMark.None)).ToList();
        var profiles = loaded.FromProfiles.ToDictionary(row => row.Id, row => row);
        var order = loaded.FromProfiles.Select(row => row.Id).ToList();
        var state = loaded.State;
        var stateStaged = false;

        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case EditorBookmarkOpKinds.StartOver:
                    // Preview only: Global is emptied; every profile's own bookmarks stay where they are.
                    state = EditorBookmarkStates.Pooled;
                    stateStaged = true;
                    everyone = [];
                    foreach (var id in order)
                    {
                        profiles[id] = profiles[id] with { InEveryone = false };
                    }

                    break;

                case EditorBookmarkOpKinds.Add when op.BookmarkId is { } added && profiles.TryGetValue(added, out var source):
                    state = EditorBookmarkStates.Definitive;
                    stateStaged = true;
                    profiles[added] = source with { InEveryone = true };
                    if (!everyone.Any(row => row.Id == added))
                    {
                        everyone.Add(new EditorBookmarkViewRow(source with { InEveryone = true }, EditorBookmarkRowMark.Added));
                    }

                    break;

                case EditorBookmarkOpKinds.Remove when op.BookmarkId is { } removed:
                    state = EditorBookmarkStates.Definitive;
                    stateStaged = true;
                    if (profiles.TryGetValue(removed, out var leaving))
                    {
                        profiles[removed] = leaving with { InEveryone = false };
                    }

                    everyone = [.. everyone.Select(row => row.Id == removed ? row with { Mark = EditorBookmarkRowMark.Removed } : row)];
                    break;

                case EditorBookmarkOpKinds.Rename when op.BookmarkId is { } renamed:
                    state = EditorBookmarkStates.Definitive;
                    stateStaged = true;
                    everyone = [.. everyone.Select(row => row.Id == renamed
                        ? new EditorBookmarkViewRow(row.Row with { Name = op.Name?.Trim() ?? string.Empty }, KeepFirst(row.Mark, EditorBookmarkRowMark.Renamed))
                        : row)];
                    break;

                case EditorBookmarkOpKinds.ChangeTime when op.BookmarkId is { } moved && op.PositionSeconds is { } to:
                    state = EditorBookmarkStates.Definitive;
                    stateStaged = true;
                    everyone = [.. everyone.Select(row => row.Id == moved
                        ? new EditorBookmarkViewRow(row.Row with { PositionSeconds = to, FrameUrl = null }, KeepFirst(row.Mark, EditorBookmarkRowMark.Moved))
                        : row)];
                    break;

                case EditorBookmarkOpKinds.Import:
                    state = EditorBookmarkStates.Definitive;
                    stateStaged = true;
                    for (var index = 0; index < op.Items.Count; index++)
                    {
                        var item = op.Items[index];
                        if (item.ReplacesBookmarkId is { } replaced)
                        {
                            everyone = [.. everyone.Select(row => row.Id == replaced ? row with { Mark = EditorBookmarkRowMark.Removed } : row)];
                        }

                        everyone.Add(new EditorBookmarkViewRow(
                            new EditorBookmarkRowDto
                            {
                                Id = ImportedId(op.Id, index),
                                AssetId = loaded.AssetId ?? Guid.Empty,
                                PositionKind = string.IsNullOrWhiteSpace(item.Locator) ? "time" : "text",
                                PositionSeconds = string.IsNullOrWhiteSpace(item.Locator) ? item.PositionSeconds : 0,
                                EndSeconds = item.EndSeconds,
                                ChapterIndex = item.ChapterIndex,
                                ChapterTitle = item.ChapterTitle,
                                Name = item.Name ?? string.Empty,
                                InEveryone = true,
                            },
                            EditorBookmarkRowMark.Imported));
                    }

                    break;
            }
        }

        var fromProfiles = order.Select(id => profiles[id]).Select(row => new EditorBookmarkViewRow(row, EditorBookmarkRowMark.None)).ToList();
        return new EditorBookmarkView(
            state,
            stateStaged,
            [.. EditorBookmarkOrdering.InPlace(everyone)],
            [.. EditorBookmarkOrdering.InPlace(fromProfiles)],
            ops.Count);
    }

    /// <summary>The row id an imported bookmark has in the preview: stable for one operation, never a real bookmark's.</summary>
    public static Guid ImportedId(string opId, int index)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{opId}:{index}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    // A row renamed and then moved still reads as the first thing that was staged on it (Added and Imported win).
    private static EditorBookmarkRowMark KeepFirst(EditorBookmarkRowMark current, EditorBookmarkRowMark next) =>
        current is EditorBookmarkRowMark.Added or EditorBookmarkRowMark.Imported ? current : next;
}

/// <summary>The order a list is shown in: where each bookmark is in its file, then when it was made.</summary>
public static class EditorBookmarkOrdering
{
    public static IEnumerable<EditorBookmarkViewRow> InPlace(IEnumerable<EditorBookmarkViewRow> rows) =>
        rows.OrderBy(row => row.Row.AssetId)
            .ThenBy(row => row.Row.ChapterIndex ?? -1)
            .ThenBy(row => row.Row.PositionSeconds)
            .ThenBy(row => row.Row.CreatedAt);
}
