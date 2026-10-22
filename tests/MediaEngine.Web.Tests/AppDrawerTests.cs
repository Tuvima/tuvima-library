using Bunit;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The temporary drawer is a native modal dialog. Escape is handled in native-structure.js (it cancels the browser's own
/// close and calls back into .NET), so the rendered dialog must carry no attribute the browser would refuse.
/// </summary>
public sealed class AppDrawerTests : AsyncBunitContext
{
    public AppDrawerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void TheTemporaryDrawerRendersNoAttributeNamesStartingWithAt()
    {
        var drawer = Render<AppDrawer>(parameters => parameters
            .Add(component => component.Open, true)
            .Add(component => component.Variant, AppDrawerVariant.Temporary));

        var dialog = drawer.Find("dialog");
        Assert.DoesNotContain(dialog.Attributes, attribute => attribute.Name.StartsWith('@'));
        Assert.DoesNotContain(drawer.FindAll("*").SelectMany(element => element.Attributes), attribute => attribute.Name.StartsWith('@'));
    }

    [Fact]
    public async Task EscapeFromTheBrowserClosesTheDrawerThroughOpenChanged()
    {
        bool? reported = null;
        var drawer = Render<AppDrawer>(parameters => parameters
            .Add(component => component.Open, true)
            .Add(component => component.Variant, AppDrawerVariant.Temporary)
            .Add(component => component.OpenChanged, (bool open) => reported = open));

        await drawer.Instance.CloseFromEscapeAsync();

        Assert.False(reported);
        Assert.False(drawer.Instance.Open);
        var call = JSInterop.Invocations.Last(invocation => invocation.Identifier == "setDrawerOpen");
        Assert.Equal(false, call.Arguments[1]);
    }
}
