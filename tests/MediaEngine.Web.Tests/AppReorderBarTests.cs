using Bunit;
using MediaEngine.Web.Components.Rules;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The Reorder bar (spec Collections and View fixes, #877) and the two small rules built beside it: the viewer's size and the type-aware rule fields.</summary>
public sealed class AppReorderBarTests : AsyncBunitContext
{
    public AppReorderBarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<AppReorderBar> RenderBar(bool active, bool dirty, Func<Task<bool>>? save = null, Action? cancel = null, bool guardLeaving = true) =>
        Render<AppReorderBar>(parameters => parameters
            .Add(bar => bar.Active, active)
            .Add(bar => bar.Dirty, dirty)
            .Add(bar => bar.GuardLeaving, guardLeaving)
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
    public async Task LeavingThePage_WithAnUnsavedOrder_AsksSaveOrDiscard_ByDefault()
    {
        var host = Render<AppDialogHost>();
        var bar = RenderBar(active: true, dirty: true);
        var navigation = Services.GetRequiredService<NavigationManager>();

        await bar.InvokeAsync(() => navigation.NavigateTo("/elsewhere"));

        host.WaitForElement(".app-confirm");
        Assert.Equal("Save or discard?", host.Find(".app-dialog-shell__title").TextContent.Trim());
    }

    [Fact]
    public async Task LeavingThePage_WithGuardLeavingOff_AsksNothingAndLeaves()
    {
        var host = Render<AppDialogHost>();
        var bar = RenderBar(active: true, dirty: true, guardLeaving: false);
        var navigation = Services.GetRequiredService<NavigationManager>();

        await bar.InvokeAsync(() => navigation.NavigateTo("/elsewhere"));

        Assert.Empty(host.FindAll(".app-confirm"));
        Assert.EndsWith("/elsewhere", navigation.Uri, StringComparison.Ordinal);
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

    [Fact]
    public void EditorListSurface_WhileReordering_ReservesTheBarsHeightFromTheToken_AndItsRowsAreStyledGlobally()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "wwwroot");
        var app = File.ReadAllText(Path.Combine(web, "app.css"));
        var tokens = File.ReadAllText(Path.Combine(web, "tuvima.tokens.css"));

        Assert.Matches(@"\.tl-popover\.tl-hnav__surface--reordering\s*\{\s*--tl-popover-bottom-reserve:\s*var\(--tl-reorder-bar-height\);", app);
        Assert.Contains("--tl-reorder-bar-height:", tokens, StringComparison.Ordinal);
        // The rows are AppRowButtons, which do not carry the header component's scope marker, so their box rules cannot live in its scoped sheet.
        Assert.Contains(".tl-hnav__surface .tl-hnav__option {", app, StringComparison.Ordinal);
        var scoped = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Components", "Editor", "EditorHeaderNav.razor.css"));
        Assert.DoesNotMatch(@"(?m)^\.tl-hnav__option(:hover|\.is-selected|--plain|:has\([^)]*\))?\s*\{", scoped);
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
