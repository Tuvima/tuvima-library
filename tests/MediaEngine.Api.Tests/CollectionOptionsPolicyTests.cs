using MediaEngine.Api.Services.Collections;
using MediaEngine.Domain.Aggregates;

namespace MediaEngine.Api.Tests;

public sealed class CollectionOptionsPolicyTests
{
    private static readonly DateOnly Day = new(2026, 10, 15);

    [Fact]
    public void Arrange_WithoutOptionsReturnsTheListUnchanged()
    {
        var ids = Ids(10);

        Assert.Equal(ids, CollectionOptionsPolicy.Arrange(new Collection { Id = Guid.NewGuid() }, ids, id => id, Day));
    }

    [Fact]
    public void Arrange_CapKeepsTheFirstNInTheCollectionsOwnOrder()
    {
        var ids = Ids(10);
        var collection = new Collection { Id = Guid.NewGuid(), MaxItems = 4 };

        Assert.Equal(ids.Take(4), CollectionOptionsPolicy.Arrange(collection, ids, id => id, Day));
    }

    [Fact]
    public void Arrange_RandomIsStableWithinADayWhateverTheInputOrder()
    {
        var ids = Ids(40);
        var collection = new Collection { Id = Guid.NewGuid(), MaxItems = 10, SortField = "random" };

        var first = CollectionOptionsPolicy.Arrange(collection, ids, id => id, Day);
        var reversed = CollectionOptionsPolicy.Arrange(collection, ids.AsEnumerable().Reverse().ToList(), id => id, Day);

        Assert.Equal(10, first.Count);
        Assert.Equal(first, reversed);
    }

    [Fact]
    public void Arrange_RandomChangesTheNextDayAndBetweenCollections()
    {
        var ids = Ids(40);
        var collection = new Collection { Id = Guid.NewGuid(), SortField = "random" };
        var other = new Collection { Id = Guid.NewGuid(), SortField = "random" };

        var today = CollectionOptionsPolicy.Arrange(collection, ids, id => id, Day);

        Assert.NotEqual(today, CollectionOptionsPolicy.Arrange(collection, ids, id => id, Day.AddDays(1)));
        Assert.NotEqual(today, CollectionOptionsPolicy.Arrange(other, ids, id => id, Day));
        Assert.Equal(ids.Order(), today.Order());
    }

    [Fact]
    public void Arrange_RandomKeepsMostOfTheSampleWhenOneItemIsAdded()
    {
        var ids = Ids(40);
        var collection = new Collection { Id = Guid.NewGuid(), MaxItems = 10, SortField = "random" };
        var before = CollectionOptionsPolicy.Arrange(collection, ids, id => id, Day);

        var after = CollectionOptionsPolicy.Arrange(collection, [.. ids, Guid.NewGuid()], id => id, Day);

        Assert.True(before.Intersect(after).Count() >= 9);
    }

    [Theory]
    [InlineData(0, CollectionSurface.Lane, 3)]
    [InlineData(0, CollectionSurface.CollectionPage, 1)]
    [InlineData(0, CollectionSurface.CollectionsPage, 0)]
    [InlineData(6, CollectionSurface.Lane, 6)]
    [InlineData(6, CollectionSurface.CollectionPage, 6)]
    [InlineData(6, CollectionSurface.CollectionsPage, 6)]
    public void EffectiveMinimum_UsesTheChosenValueElseTheSurfaceDefault(int chosen, CollectionSurface surface, int expected)
    {
        var collection = new Collection { Id = Guid.NewGuid(), MinItems = chosen };

        Assert.Equal(expected, CollectionOptionsPolicy.EffectiveMinimum(collection, surface));
        Assert.Equal(expected == 0 || expected <= 2, CollectionOptionsPolicy.MeetsMinimum(collection, 2, surface));
    }

    [Fact]
    public void TryApplyRequested_SetsClearsAndRejects()
    {
        var collection = new Collection { Id = Guid.NewGuid() };

        Assert.Null(CollectionOptionsPolicy.TryApplyRequested(collection, 4, 20, "10-01", "11-01"));
        Assert.Equal(4, collection.MinItems);
        Assert.Equal(20, collection.MaxItems);
        Assert.Equal("10-01", collection.SeasonFrom);
        Assert.Equal("11-01", collection.SeasonTo);

        // Absent values leave every option alone.
        Assert.Null(CollectionOptionsPolicy.TryApplyRequested(collection, null, null, null, null));
        Assert.Equal(20, collection.MaxItems);
        Assert.Equal("10-01", collection.SeasonFrom);

        // Zero and empty text remove them.
        Assert.Null(CollectionOptionsPolicy.TryApplyRequested(collection, 0, 0, "", ""));
        Assert.Equal(0, collection.MinItems);
        Assert.Null(collection.MaxItems);
        Assert.Null(collection.SeasonWindow);

        // A bad value changes nothing.
        Assert.NotNull(CollectionOptionsPolicy.TryApplyRequested(collection, 9, -1, null, null));
        Assert.NotNull(CollectionOptionsPolicy.TryApplyRequested(collection, 9, null, "10-01", null));
        Assert.NotNull(CollectionOptionsPolicy.TryApplyRequested(collection, 9, null, "13-01", "11-01"));
        Assert.Equal(0, collection.MinItems);
    }

    private static List<Guid> Ids(int count) =>
        Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();
}
