using Bunit;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests;

// Look v3.1 D3 and D13: every dropdown list asks the shared pop-over for one width rule, and the chosen option carries a check.
public sealed class DropdownListWidthTests : AsyncBunitContext
{
    public DropdownListWidthTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Fact]
    public void ChosenOption_ShowsTheCheck_AndAnUnchosenOptionDoesNot()
    {
        var cut = Render<AppTypedSelect<string>>(parameters => parameters
            .Add(component => component.Value, "alpha")
            .Add(component => component.ChildContent, Options));

        var chosen = cut.Find("[role='option'][aria-selected='true']");
        var other = cut.Find("[role='option'][aria-selected='false']");
        Assert.Single(chosen.QuerySelectorAll(".tl-list-item__check"));
        Assert.Empty(other.QuerySelectorAll(".tl-list-item__check"));
        Assert.Contains("tl-icon-size-control20", chosen.QuerySelector(".tl-list-item__check")!.ClassName);
    }

    [Fact]
    public void TypedSelect_AsksThePopOverForTheListWidthRule_AndThePhoneSheet()
    {
        var cut = Render<AppTypedSelect<string>>(parameters => parameters.Add(component => component.ChildContent, Options));

        var surface = cut.Find(".app-popover");
        Assert.Equal("true", surface.GetAttribute("data-list-width"));
        Assert.Equal("true", surface.GetAttribute("data-list-sheet"));
    }

    [Fact]
    public void Autocomplete_AsksForTheListWidthRule_ButStaysAnchoredOnAPhone()
    {
        var cut = Render<AppAutocomplete<string>>();

        var surface = cut.Find(".app-popover");
        Assert.Equal("true", surface.GetAttribute("data-list-width"));
        Assert.Null(surface.GetAttribute("data-list-sheet"));
    }

    [Fact]
    public void DropdownStyleMenu_AsksForTheListWidthRule_WhenItOpens()
    {
        var cut = Render<AppOverflowMenu>(parameters => parameters
            .Add(component => component.Label, "Episodes")
            .Add(component => component.ListWidth, true)
            .AddChildContent("<button role='menuitem'>Episode one</button>"));
        cut.Find("button.app-overflow-menu__trigger").Click();

        var surface = cut.Find(".app-popover");
        Assert.Equal("true", surface.GetAttribute("data-list-width"));
        Assert.Null(surface.GetAttribute("data-popout-size"));
    }

    [Fact]
    public void LabelledActionMenu_KeepsItsMenuSize_AndDoesNotAskForTheListWidthRule()
    {
        var cut = Render<AppOverflowMenu>(parameters => parameters
            .Add(component => component.Label, "Organise")
            .AddChildContent("<button role='menuitem'>Add a tag</button>"));
        cut.Find("button.app-overflow-menu__trigger").Click();

        var surface = cut.Find(".app-popover");
        Assert.Null(surface.GetAttribute("data-list-width"));
        Assert.Equal("menu", surface.GetAttribute("data-popout-size"));
    }

    [Fact]
    public void PlainIconMenu_KeepsItsMenuSize_AndDoesNotAskForTheListWidthRule()
    {
        var cut = Render<AppOverflowMenu>(parameters => parameters
            .Add(component => component.AriaLabel, "More actions")
            .AddChildContent("<button role='menuitem'>Edit</button>"));
        cut.Find("button.app-overflow-menu__trigger").Click();

        var surface = cut.Find(".app-popover");
        Assert.Null(surface.GetAttribute("data-list-width"));
        Assert.Equal("menu", surface.GetAttribute("data-popout-size"));
    }

    private static RenderFragment Options => builder =>
    {
        foreach (var (value, label) in new[] { ("alpha", "Alpha"), ("beta", "Beta") })
        {
            builder.OpenComponent<AppSelectItem<string>>(0);
            builder.AddAttribute(1, nameof(AppSelectItem<string>.Value), value);
            builder.AddAttribute(2, nameof(AppSelectItem<string>.Text), label);
            builder.AddAttribute(3, nameof(AppSelectItem<string>.ChildContent), (RenderFragment)(content => content.AddContent(0, label)));
            builder.CloseComponent();
        }
    };
}
