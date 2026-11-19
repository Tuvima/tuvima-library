using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Engine reorders by position moves, not by a whole list. The planner turns a new order into those moves: one move for a
/// drag or Move to top, and moves that really do produce the new order for anything larger.
/// </summary>
public sealed class CollectionItemMovePlannerTests
{
    private static readonly Guid[] Ids = Enumerable.Range(1, 8).Select(number => new Guid(number, 0, 0, new byte[8])).ToArray();

    [Fact]
    public void AnUnchangedOrder_NeedsNoMoves()
    {
        Assert.Empty(CollectionItemMovePlanner.Plan(Ids, Ids)!);
    }

    [Fact]
    public void DraggingOneSongUp_IsOneMoveToItsNewPlace()
    {
        var target = Apply(Ids, new CollectionItemMove(Ids[6], 2));

        var moves = CollectionItemMovePlanner.Plan(Ids, target)!;

        Assert.Equal([new CollectionItemMove(Ids[6], 2)], moves);
    }

    [Fact]
    public void DraggingOneSongDown_IsOneMoveToItsNewPlace()
    {
        var target = Apply(Ids, new CollectionItemMove(Ids[1], 6));

        var moves = CollectionItemMovePlanner.Plan(Ids, target)!;

        Assert.Equal([new CollectionItemMove(Ids[1], 6)], moves);
    }

    [Fact]
    public void MoveToTop_IsOneMoveToPlaceOne()
    {
        var target = Apply(Ids, new CollectionItemMove(Ids[5], 1));

        Assert.Equal([new CollectionItemMove(Ids[5], 1)], CollectionItemMovePlanner.Plan(Ids, target)!);
    }

    [Fact]
    public void ReversingTheWholeList_GivesMovesThatProduceTheReversedOrder()
    {
        var target = Ids.Reverse().ToArray();

        var moves = CollectionItemMovePlanner.Plan(Ids, target)!;

        Assert.Equal(target, Apply(Ids, [.. moves]));
    }

    [Fact]
    public void ShufflingTheList_GivesMovesThatProduceTheNewOrder()
    {
        var target = new[] { Ids[3], Ids[0], Ids[7], Ids[1], Ids[2], Ids[6], Ids[4], Ids[5] };

        var moves = CollectionItemMovePlanner.Plan(Ids, target)!;

        Assert.Equal(target, Apply(Ids, [.. moves]));
        Assert.True(moves.Count <= Ids.Length);
    }

    [Fact]
    public void AnOrderThatDoesNotHoldTheSameSongs_IsNotPlanned()
    {
        Assert.Null(CollectionItemMovePlanner.Plan(Ids, Ids.Take(7).ToArray()));
        Assert.Null(CollectionItemMovePlanner.Plan(Ids, Ids.Take(7).Append(Guid.NewGuid()).ToArray()));
    }

    /// <summary>What the Engine does with a move: take the item out and put it back at the 1-based place.</summary>
    private static Guid[] Apply(IReadOnlyList<Guid> order, params CollectionItemMove[] moves)
    {
        var list = order.ToList();
        foreach (var move in moves)
        {
            list.Remove(move.ItemId);
            list.Insert(Math.Clamp(move.Position, 1, list.Count + 1) - 1, move.ItemId);
        }

        return [.. list];
    }
}
