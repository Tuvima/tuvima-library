using Bunit;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace MediaEngine.Web.Tests;

public sealed class NativeFieldsBehaviorTests : AsyncBunitContext
{
    public NativeFieldsBehaviorTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Fact]
    public void SelectKeyboardSkipsDisabledOptionsAndTypeaheadChoosesTheDisplayedLabel()
    {
        string? selected = null;
        var cut = Render<AppTypedSelect<string>>(parameters => parameters
            .Add(component => component.ValueChanged, value => selected = value)
            .Add(component => component.ChildContent, Options));
        var trigger = cut.Find("button[role='combobox']");
        trigger.Click();
        Assert.Equal("true", trigger.GetAttribute("aria-expanded"));
        trigger.KeyDown(new KeyboardEventArgs { Key = "Home" });
        trigger.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        trigger.KeyDown(new KeyboardEventArgs { Key = "Enter" });
        Assert.Equal("gamma", selected);
        Assert.Equal("false", cut.Find("button").GetAttribute("aria-expanded"));
        trigger = cut.Find("button");
        trigger.KeyDown(new KeyboardEventArgs { Key = "a" });
        trigger.KeyDown(new KeyboardEventArgs { Key = "Enter" });
        Assert.Equal("alpha", selected);
    }

    [Fact]
    public void DisabledOptionAndDisabledSelectDoNotInvokeValueCallbacks()
    {
        var calls = 0;
        var cut = Render<AppTypedSelect<string>>(parameters => parameters
            .Add(component => component.ValueChanged, _ => calls++)
            .Add(component => component.ChildContent, Options));
        cut.Find("[role='option'][aria-disabled='true']").Click();
        Assert.Equal(0, calls);
        cut.Render(parameters => parameters.Add(component => component.Disabled, true));
        Assert.True(cut.Find("button").HasAttribute("disabled"));
        cut.Find("[role='option']").Click();
        Assert.Equal(0, calls);
    }

    [Fact]
    public void NonImmediateTextCommitsOnlyOnChangeAndAssociatesItsError()
    {
        var values = new List<string?>();
        var cut = Render<AppTextField>(parameters => parameters
            .Add(component => component.Immediate, false)
            .Add(component => component.Label, "Title")
            .Add(component => component.Error, true)
            .Add(component => component.ErrorText, "A title is required")
            .Add(component => component.ValueChanged, value => values.Add(value)));
        var input = cut.Find("input");
        input.Input("Draft title");
        Assert.Empty(values);
        input.Change("Draft title");
        Assert.Equal("Draft title", Assert.Single(values));
        Assert.Equal("true", input.GetAttribute("aria-invalid"));
        Assert.Equal(cut.Find("[role='alert']").Id, input.GetAttribute("aria-describedby"));
        Assert.Equal(input.Id, cut.Find("label").GetAttribute("for"));
    }

    [Fact]
    public void DecorativeAdornmentDoesNotAddAnUnusableKeyboardStop()
    {
        var cut = Render<AppTextField>(parameters => parameters
            .Add(component => component.Adornment, AppAdornment.Start)
            .Add(component => component.AdornmentIcon, AppMaterialIcons.Outlined.Search));
        Assert.Empty(cut.FindAll("button"));
        Assert.Single(cut.FindAll("span.tl-input-adornment[aria-hidden='true']"));
        cut.Render(parameters => parameters.Add(component => component.OnAdornmentClick, _ => { }));
        Assert.Single(cut.FindAll("button.tl-input-adornment"));
    }

    [Fact]
    public async Task DebounceEmitsOnlyTheLatestTextToBothCallbacks()
    {
        var values = new List<string?>();
        var elapsed = new List<string>();
        var cut = Render<AppTextField>(parameters => parameters
            .Add(component => component.DebounceInterval, 30)
            .Add(component => component.ValueChanged, value => values.Add(value))
            .Add(component => component.OnDebounceIntervalElapsed, value => elapsed.Add(value)));
        cut.Find("input").Input("Obsolete");
        cut.Find("input").Input("Current");
        await cut.WaitForAssertionAsync(() =>
        {
            Assert.Equal("Current", Assert.Single(values));
            Assert.Equal("Current", Assert.Single(elapsed));
        });
    }

    [Fact]
    public async Task AutocompleteCancelsObsoleteQueriesAndRejectsLateResults()
    {
        var first = new TaskCompletionSource<IEnumerable<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<IEnumerable<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default;
        var cut = Render<AppAutocomplete<string>>(parameters => parameters
            .Add(component => component.SearchFunc, (string? query, CancellationToken token) =>
            {
                if (query == "first") { firstToken = token; return first.Task; }
                return second.Task;
            }));
        var obsoleteEvent = cut.Find("input").InputAsync("first");
        var currentEvent = cut.Find("input").InputAsync("second");
        Assert.True(firstToken.IsCancellationRequested);
        second.SetResult(["Current result"]);
        await currentEvent;
        first.SetResult(["Obsolete result"]);
        await obsoleteEvent;
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Current result", cut.Find("[role='listbox']").TextContent);
            Assert.DoesNotContain("Obsolete result", cut.Find("[role='listbox']").TextContent);
        });
    }

    [Fact]
    public void DisabledAutocompleteCannotSelectRetainedResultsAndClosedHomeKeepsEditing()
    {
        var calls = 0;
        var searches = 0;
        var cut = Render<AppAutocomplete<string>>(parameters => parameters
            .Add(component => component.ValueChanged, _ => calls++)
            .Add(component => component.SearchFunc, (string? query, CancellationToken _) =>
            {
                searches++;
                return Task.FromResult<IEnumerable<string>>(["Alpha"]);
            }));
        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "Home" });
        Assert.Equal(0, searches);
        cut.Find("input").Input("a");
        cut.Render(parameters => parameters.Add(component => component.Disabled, true));
        Assert.Equal("false", cut.Find("input").GetAttribute("aria-expanded"));
        cut.Find("[role='option']").Click();
        Assert.Equal(0, calls);
    }

    private static RenderFragment Options => builder =>
    {
        foreach (var (value, label, disabled) in new[] { ("alpha", "Alpha", false), ("beta", "Beta", true), ("gamma", "Gamma", false) })
        {
            builder.OpenComponent<AppSelectItem<string>>(0);
            builder.AddAttribute(1, nameof(AppSelectItem<string>.Value), value);
            builder.AddAttribute(2, nameof(AppSelectItem<string>.Text), label);
            builder.AddAttribute(3, nameof(AppSelectItem<string>.Disabled), disabled);
            builder.AddAttribute(4, nameof(AppSelectItem<string>.ChildContent), (RenderFragment)(content => content.AddContent(0, label)));
            builder.CloseComponent();
        }
    };
}
