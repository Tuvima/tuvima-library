using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Navigation;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Where a row lands when a finger drops it before or after another row, and how the playlists' own order moves (listen-playlists WP5): the
/// same answers serve the song table, the queue and the playlist lists.
/// </summary>
public sealed class TouchReorderRulesTests
{
    [Theory]
    [InlineData(0, 2, false, 1)]
    [InlineData(0, 2, true, 2)]
    [InlineData(2, 0, false, 0)]
    [InlineData(2, 0, true, 1)]
    [InlineData(3, 1, true, 2)]
    [InlineData(1, 3, false, 2)]
    public void DroppingARowBeforeOrAfterAnotherLandsItWhereTheGapIs(int from, int target, bool after, int expected)
    {
        Assert.Equal(expected, TouchReorderRules.FinalIndex(from, target, after, count: 4));
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, false)]
    [InlineData(2, 1, true)]
    public void DroppingARowWhereItAlreadyIsChangesNothing(int from, int target, bool after)
    {
        Assert.Null(TouchReorderRules.FinalIndex(from, target, after, count: 4));
    }

    [Theory]
    [InlineData(-1, 1, 4)]
    [InlineData(1, -1, 4)]
    [InlineData(4, 1, 4)]
    [InlineData(1, 4, 4)]
    [InlineData(0, 0, 0)]
    public void AnIndexOutsideTheListChangesNothing(int from, int target, int count)
    {
        Assert.Null(TouchReorderRules.FinalIndex(from, target, after: false, count));
    }

    [Fact]
    public void EveryDropOfEveryRowGivesTheSameOrderAsMovingItThereByHand()
    {
        const int count = 5;
        for (var from = 0; from < count; from++)
        {
            for (var target = 0; target < count; target++)
            {
                foreach (var after in new[] { false, true })
                {
                    var order = Enumerable.Range(0, count).ToList();
                    var expected = order.ToList();
                    expected.RemoveAt(from);
                    var gap = expected.IndexOf(target) + (after ? 1 : 0);
                    if (target == from)
                    {
                        Assert.Null(TouchReorderRules.FinalIndex(from, target, after, count));
                        continue;
                    }

                    expected.Insert(gap, from);
                    var place = TouchReorderRules.FinalIndex(from, target, after, count);
                    if (expected.SequenceEqual(order))
                    {
                        Assert.Null(place);
                        continue;
                    }

                    var moved = order.ToList();
                    moved.RemoveAt(from);
                    moved.Insert(place!.Value, from);
                    Assert.Equal(expected, moved);
                }
            }
        }
    }

    private static ManagedCollectionViewModel Playlist(Guid id, bool pinned = false) => new() { Id = id, Name = id.ToString("N"), IsPinned = pinned };

    [Fact]
    public void TryMoveAfterPutsThePlaylistRightBehindTheOneItWasDroppedOn()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var moved = ListenPlaylistOrderState.TryMoveAfter(ids, ids[0], ids[2], out var updated);

        Assert.True(moved);
        Assert.Equal([ids[1], ids[2], ids[0], ids[3]], updated);
    }

    [Fact]
    public void TryMoveAfterWorksUpwardsAndAtTheEnd()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        Assert.True(ListenPlaylistOrderState.TryMoveAfter(ids, ids[2], ids[0], out var up));
        Assert.Equal([ids[0], ids[2], ids[1]], up);
        Assert.True(ListenPlaylistOrderState.TryMoveAfter(ids, ids[0], ids[2], out var end));
        Assert.Equal([ids[1], ids[2], ids[0]], end);
    }

    [Fact]
    public void TryMoveAfterRefusesWhatChangesNothingOrNamesAPlaylistThatIsNotThere()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        Assert.False(ListenPlaylistOrderState.TryMoveAfter(ids, ids[1], ids[1], out _));
        Assert.False(ListenPlaylistOrderState.TryMoveAfter(ids, ids[1], ids[0], out _));
        Assert.False(ListenPlaylistOrderState.TryMoveAfter(ids, Guid.NewGuid(), ids[0], out _));
        Assert.False(ListenPlaylistOrderState.TryMoveAfter(ids, ids[0], Guid.NewGuid(), out _));
    }

    [Fact]
    public void MoveUpAndMoveDownStopAtTheEndsOfTheList()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var shown = new[] { Playlist(a), Playlist(b), Playlist(c) };

        Assert.False(ListenPlaylistOrderState.CanMoveWithinGroup(shown, a, -1));
        Assert.True(ListenPlaylistOrderState.CanMoveWithinGroup(shown, a, 1));
        Assert.True(ListenPlaylistOrderState.CanMoveWithinGroup(shown, b, -1));
        Assert.True(ListenPlaylistOrderState.CanMoveWithinGroup(shown, b, 1));
        Assert.False(ListenPlaylistOrderState.CanMoveWithinGroup(shown, c, 1));
        Assert.False(ListenPlaylistOrderState.CanMoveWithinGroup(shown, Guid.NewGuid(), 1));
    }

    [Fact]
    public void MoveUpAndMoveDownDoNotCrossTheEdgeBetweenPinnedAndOtherPlaylists()
    {
        var pinned = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var shown = new[] { Playlist(pinned, pinned: true), Playlist(first), Playlist(second) };

        Assert.False(ListenPlaylistOrderState.CanMoveWithinGroup(shown, pinned, 1));
        Assert.False(ListenPlaylistOrderState.CanMoveWithinGroup(shown, first, -1));
        Assert.True(ListenPlaylistOrderState.CanMoveWithinGroup(shown, first, 1));
    }
}
