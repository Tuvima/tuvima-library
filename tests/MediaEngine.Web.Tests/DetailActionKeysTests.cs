using MediaEngine.Web.Components.Details;

namespace MediaEngine.Web.Tests;

public sealed class DetailActionKeysTests
{
    // The action keys the Engine may send without a route, which the Dashboard must handle itself. Keep this list
    // identical to DashboardHandledKeys in tests/MediaEngine.Api.Tests/DetailActionDestinationTests.cs: that test proves
    // every action the Engine composes has a route or one of these keys, so adding a key on one side only fails a test.
    private static readonly string[] EngineExpectedKeys =
    [
        "play-from-beginning", "read", "listen", "play-album", "shuffle", "shuffle-collection",
        "play-next", "add-queue", "add-playlist",
        "status-complete", "status-reset", "status-hide", "status-show", "status-history",
        "copy-link", "edit", "edit-media", "my-list",
        "reaction-dislike", "reaction-like", "reaction-love", "details",
    ];

    [Fact]
    public void TheKeysTheDashboardHandlesAreExactlyTheKeysTheEngineTestExpects()
    {
        Assert.Equal(EngineExpectedKeys.Order(StringComparer.Ordinal), DetailActionKeys.Handled.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryHandledKeyIsListedOnce()
    {
        Assert.Equal(DetailActionKeys.Handled.Count, DetailActionKeys.Handled.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("status-complete")]
    [InlineData("status-reset")]
    [InlineData("status-hide")]
    [InlineData("status-show")]
    [InlineData("status-history")]
    public void PersonalStatusKeysAreRecognised(string key)
    {
        Assert.True(DetailActionKeys.IsPersonalStatus(key));
        Assert.Contains(key, DetailActionKeys.Handled);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("open")]
    [InlineData("status-unknown")]
    public void OtherKeysAreNotPersonalStatus(string key)
    {
        Assert.False(DetailActionKeys.IsPersonalStatus(key));
    }

    [Fact]
    public void TheGenericOpenKeyIsNotSomethingTheDashboardHandles()
    {
        Assert.DoesNotContain("open", DetailActionKeys.Handled);
    }

    [Fact]
    public void ThePageHandlerSwitchesOnTheSharedKeysRatherThanOwnLiterals()
    {
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "MediaEngine.Web", "Components", "Details", "DetailPage.razor")));
        var handler = source[source.IndexOf("private async Task HandleDetailActionAsync", StringComparison.Ordinal)..];
        handler = handler[..handler.IndexOf("private bool _changingPersonalStatus", StringComparison.Ordinal)];

        foreach (var key in DetailActionKeys.Handled)
        {
            Assert.DoesNotContain($"action.Key == \"{key}\"", handler, StringComparison.Ordinal);
            Assert.DoesNotContain($"action.Key is \"{key}\"", handler, StringComparison.Ordinal);
        }

        Assert.Contains("DetailActionKeys.", handler, StringComparison.Ordinal);
    }
}
