using Bunit;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Tests;

/// <summary>The PIN number pad: touch and keyboard entry, dots, and feedback for a wrong PIN.</summary>
public sealed class PinPadTests
{
    private static readonly object Owner = new();

    private static IRenderedComponent<PinPad> Render(BunitContext ctx, string value, Action<string>? changed = null, Action? submitted = null, Action? cancelled = null, int shake = 0, bool disabled = false)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx.Render<PinPad>(parameters => parameters
            .Add(p => p.Value, value)
            .Add(p => p.ShakeCount, shake)
            .Add(p => p.Disabled, disabled)
            .Add(p => p.ValueChanged, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<string>(Owner, text => changed?.Invoke(text)))
            .Add(p => p.OnSubmit, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(Owner, () => submitted?.Invoke()))
            .Add(p => p.OnCancel, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(Owner, () => cancelled?.Invoke())));
    }

    [Fact]
    public void Tapping_ADigit_AppendsItToThePin()
    {
        using var ctx = new BunitContext();
        string? latest = null;
        var cut = Render(ctx, "12", text => latest = text);

        cut.Find("button[aria-label='7']").Click();

        Assert.Equal("127", latest);
    }

    [Fact]
    public void ThePad_HasTenDigitsAndADeleteKey()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, "1");

        foreach (var digit in "0123456789")
        {
            Assert.Single(cut.FindAll($"button[aria-label='{digit}']"));
        }

        Assert.Single(cut.FindAll("button[aria-label='Delete last digit']"));
    }

    [Fact]
    public void Dots_FillAsDigitsGoIn_AndNeverShowTheDigits()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, "482");

        Assert.Equal(4, cut.FindAll(".pin-pad__dot").Count);
        Assert.Equal(3, cut.FindAll(".pin-pad__dot.is-filled").Count);
        Assert.Equal("3 digits entered", cut.Find(".pin-pad__dots").GetAttribute("aria-label"));
        Assert.DoesNotContain("482", cut.Find(".pin-pad__dots").OuterHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void Dots_GrowForLongerPins()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, "123456789");

        Assert.Equal(9, cut.FindAll(".pin-pad__dot").Count);
    }

    [Fact]
    public void Keyboard_DigitsBackspaceEnterAndEscapeWork()
    {
        using var ctx = new BunitContext();
        string? latest = null;
        var submitted = 0;
        var cancelled = 0;
        var cut = Render(ctx, "12", text => latest = text, () => submitted++, () => cancelled++);

        cut.Find(".pin-pad").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "5" });
        Assert.Equal("125", latest);

        cut.Find(".pin-pad").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Backspace" });
        Assert.Equal("1", latest);

        cut.Find(".pin-pad").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        Assert.Equal(1, submitted);

        cut.Find(".pin-pad").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void Enter_OnAKey_PressesThatKeyInsteadOfSubmitting()
    {
        using var ctx = new BunitContext();
        var submitted = 0;
        var cut = Render(ctx, "12", submitted: () => submitted++);

        cut.Find("button[aria-label='3']").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        Assert.Equal(0, submitted);
    }

    [Fact]
    public void Typing_StopsAtTheLongestPin()
    {
        using var ctx = new BunitContext();
        string? latest = null;
        var cut = Render(ctx, "123456789012", text => latest = text);

        cut.Find("button[aria-label='1']").Click();

        Assert.Null(latest);
    }

    [Fact]
    public void WhileChecking_NothingCanBeTyped()
    {
        using var ctx = new BunitContext();
        string? latest = null;
        var cut = Render(ctx, "12", text => latest = text, disabled: true);

        cut.Find(".pin-pad").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "9" });

        Assert.Null(latest);
        Assert.All(cut.FindAll(".pin-pad__key"), key => Assert.True(key.HasAttribute("disabled")));
    }

    [Fact]
    public void AWrongPin_ShakesTheDots()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, string.Empty);
        Assert.Empty(cut.FindAll(".pin-pad__dots.is-wrong"));

        cut.Render(parameters => parameters.Add(p => p.ShakeCount, 1));

        Assert.Single(cut.FindAll(".pin-pad__dots.is-wrong"));
    }
}
