namespace MediaEngine.Web.Services.Ui;

/// <summary>One step of reordering a collection: put <paramref name="ItemId"/> at <paramref name="Position"/> (1 is the top).</summary>
public readonly record struct CollectionItemMove(Guid ItemId, int Position);

/// <summary>
/// Turns "the list should now look like this" into the position moves the Engine understands (the Engine no longer takes a whole
/// list). A drag, Move up or Move to top is one move; a larger rearrangement is as many moves as it takes, each applied in order.
/// </summary>
public static class CollectionItemMovePlanner
{
    /// <summary>
    /// The moves that turn <paramref name="current"/> into <paramref name="target"/>, applied one after another. Both lists must hold
    /// the same ids; otherwise <see langword="null"/> is returned and nothing should be sent. An empty list means nothing to do.
    /// </summary>
    public static IReadOnlyList<CollectionItemMove>? Plan(IReadOnlyList<Guid> current, IReadOnlyList<Guid> target)
    {
        if (current.Count != target.Count || !current.ToHashSet().SetEquals(target) || target.Distinct().Count() != target.Count)
        {
            return null;
        }

        var first = -1;
        var last = -1;
        for (var index = 0; index < current.Count; index++)
        {
            if (current[index] == target[index])
            {
                continue;
            }

            first = first < 0 ? index : first;
            last = index;
        }

        if (first < 0)
        {
            return [];
        }

        // The usual case, a single item dragged or stepped somewhere: either the item now at the first changed place came from
        // further down, or the one that was there went further down.
        var pulledUp = MoveInCopy(current, target[first], first);
        if (pulledUp.SequenceEqual(target))
        {
            return [new CollectionItemMove(target[first], first + 1)];
        }

        var pushedDown = MoveInCopy(current, current[first], last);
        if (pushedDown.SequenceEqual(target))
        {
            return [new CollectionItemMove(current[first], last + 1)];
        }

        // Anything larger: settle each place from the top.
        var moves = new List<CollectionItemMove>();
        var working = current.ToList();
        for (var index = 0; index < target.Count; index++)
        {
            if (working[index] == target[index])
            {
                continue;
            }

            working = MoveInCopy(working, target[index], index);
            moves.Add(new CollectionItemMove(target[index], index + 1));
        }

        return moves;
    }

    private static List<Guid> MoveInCopy(IReadOnlyList<Guid> items, Guid itemId, int toIndex)
    {
        var copy = items.ToList();
        var from = copy.IndexOf(itemId);
        copy.RemoveAt(from);
        copy.Insert(toIndex, itemId);
        return copy;
    }
}
