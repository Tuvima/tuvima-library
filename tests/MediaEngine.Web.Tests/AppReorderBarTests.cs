using Bunit;
using MediaEngine.Web.Components.Rules;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace MediaEngine.Web.Tests;

/// <summary>The Reorder bar (spec Collections and View fixes, #877) and the two small rules built beside it: the viewer's size and the type-aware rule fields.</summary>
public sealed class AppReorderBarTests : AsyncBunitContext
{
    public AppReorderBarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<AppReorderBar> RenderBar(bool active, bool dirty, Func<Task<bool>>? save = null, Action? cancel = null) =>
        Render<AppReorderBar>(parameters => parameters
            .Add(bar => bar.Active, active)
            .Add(bar => bar.Dirty, dirty)
            .Add(bar => bar.OnSave, save ?? (() => Task.FromResult(true)))
            .Add(bar => bar.OnCancel, EventCallback.Factory.Create(this, cancel ?? (() => { }))));

    [Fact]
    public void Bar_IsNotDrawn_WhileTheModeIsOff()
    {
        var bar = RenderBar(active: false, dirty: false);

        Assert.Empty(bar.FindAll("[data-reorder-bar]"));
    }

    [Fact]
    public void Bar_OffersSaveOrderAndCancel_AndSaveWaitsForAChange()
    {
        var bar = RenderBar(active: true, dirty: false);

        Assert.Contains("Drag to reorder", bar.Find("[data-reorder-bar]").TextContent);
        Assert.True(bar.Find("[data-reorder-save]").HasAttribute("disabled"));
        Assert.False(bar.Find("[data-reorder-cancel]").HasAttribute("disabled"));
    }

    [Fact]
    public void SaveOrder_HandsOverTheStagedOrder()
    {
        var saved = 0;
        var bar = RenderBar(active: true, dirty: true, save: () => { saved++; return Task.FromResult(true); });

        bar.Find("[data-reorder-save]").Click();

        Assert.Equal(1, saved);
    }

    [Fact]
    public void Escape_Cancels()
    {
        var cancelled = 0;
        var bar = RenderBar(active: true, dirty: true, cancel: () => cancelled++);

        bar.Find("[data-reorder-bar]").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void RuleFields_ForOneMediaType_HideTheOnesThatBelongToOthers()
    {
        var music = new[] { "Music" };
        var fields = CollectionRuleCatalog.Instance.Fields;

        Assert.True(fields.Single(field => field.Key == "track_count").AppliesToAny(music));
        Assert.False(fields.Single(field => field.Key == "page_count").AppliesToAny(music));
        Assert.True(fields.Single(field => field.Key == "genre").AppliesToAny(music));
        Assert.True(fields.Single(field => field.Key == "page_count").AppliesToAny([]));
    }

    [Fact]
    public void Viewer_BodyHasADefiniteRow_SoThePictureShowsBeforeInfoIsOpened()
    {
        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Components", "Shared", "MediaViewerShell.razor.css"));

        Assert.Contains(".media-viewer__body{display:grid;grid-template-columns:minmax(0,1fr);grid-template-rows:minmax(0,1fr)", css);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
