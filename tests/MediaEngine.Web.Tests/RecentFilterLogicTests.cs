using MediaEngine.Web.Components.MediaTiles;

namespace MediaEngine.Web.Tests;

public sealed class RecentFilterLogicTests
{
    private static readonly string[] Three = ["watch", "read", "listen"];

    [Theory]
    [InlineData("all", "watch", "watch")]
    [InlineData("watch", "read", "watch,read")]
    [InlineData("watch,read", "watch", "read")]
    [InlineData("watch", "watch", "all")]
    [InlineData("watch,read", "listen", "all")]
    [InlineData("watch,listen", "all", "all")]
    public void TogglingAPillAddsOrRemovesIt(string scope, string tapped, string expected) =>
        Assert.Equal(expected, RecentFilterLogic.Toggle(scope, tapped, Three).Replace("\r", ""));

    [Fact]
    public void ATypeWithNothingBehindItHasNoPillAndASingleTypeHasNone()
    {
        Assert.Equal(new[] { "watch", "listen" }, RecentFilterLogic.VisibleTypes(["listen", "watch"]));
        Assert.Empty(RecentFilterLogic.VisibleTypes(["watch"]));
        Assert.Empty(RecentFilterLogic.VisibleTypes(null));
    }
}
