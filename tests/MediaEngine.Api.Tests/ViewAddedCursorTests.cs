using MediaEngine.Api.Endpoints;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

public sealed class ViewAddedCursorTests
{
    private static LocalAssetDto Item(DateTimeOffset createdAt) => new(
        Guid.NewGuid(), Guid.NewGuid(), null, null, LocalAssetMediaKinds.Image, "Beach", "beach.jpg", "image/jpeg",
        new DateTimeOffset(2012, 7, 4, 0, 0, 0, TimeSpan.Zero), createdAt, null, null, null, null, null, null, null, null, null,
        false, false, null, null, 1, [], [], string.Empty, string.Empty);

    [Fact]
    public void NextPage_StartsAfterTheLastItemsAddedTimeAndKey()
    {
        var item = Item(new DateTimeOffset(2026, 11, 6, 9, 30, 0, TimeSpan.Zero));

        var encoded = ViewAddedCursor.Encode(item);

        Assert.True(ViewAddedCursor.TryDecode(encoded, out var before, out var key));
        Assert.Equal(item.CreatedAt, before);
        Assert.Equal($"view:{item.Id:N}", key);
    }

    [Fact]
    public void FirstPage_HasNoBoundary()
    {
        Assert.True(ViewAddedCursor.TryDecode(null, out var before, out var key));
        Assert.Null(before);
        Assert.Null(key);
    }

    [Theory]
    [InlineData("not-a-cursor!")]
    [InlineData("MTIzfG5vdC1hLXZpZXc")]
    [InlineData("abc")]
    public void UnreadablePosition_IsRejected(string cursor) =>
        Assert.False(ViewAddedCursor.TryDecode(cursor, out _, out _));
}
