using Bunit;
using MediaEngine.Web.Components.MediaTiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A shelf's browser clean-up must go out as one call while the shelf is still on the page. A second call arrives
/// after the page update has removed the element, so the browser can no longer find it and its listeners stay behind.
/// </summary>
public sealed class MediaShelfDisposalTests : AsyncBunitContext
{
    public MediaShelfDisposalTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
    }

    [Fact]
    public async Task DisposalCleansUpTheBrowserShelfInOneCall()
    {
        var cut = Render<MediaShelfScroller>(p => p.Add(c => c.ItemCount, 3));

        await cut.Instance.DisposeAsync();

        Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "unregisterMediaShelf");
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "unregisterMediaShelfBoundaries");
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "unregisterMediaTileShelfScrollGuard");
    }

    [Fact]
    public async Task DisposalSurvivesABrowserCleanUpFailure()
    {
        JSInterop.SetupVoid("unregisterMediaShelf").SetException(new JSException("shelf element already gone"));
        var cut = Render<MediaShelfScroller>(p => p.Add(c => c.ItemCount, 3));

        var exception = await Record.ExceptionAsync(() => cut.Instance.DisposeAsync().AsTask());

        Assert.Null(exception);
    }
}
