using Bunit;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The one administrator unlock window and the inline-window mode of the shared dialog card it sits in.</summary>
public sealed class AdministratorUnlockDialogTests
{
    private static readonly object Owner = new();

    private static LocalizedBunitContext NewContext()
    {
        var ctx = new LocalizedBunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddNativeUiServices();
        return ctx;
    }

    private static IRenderedComponent<AdministratorUnlockDialog> RenderPad(BunitContext ctx, bool open, string? error = null, Action<string>? unlocked = null, Action? cancelled = null) =>
        ctx.Render<AdministratorUnlockDialog>(parameters => parameters
            .Add(p => p.Open, open)
            .Add(p => p.Error, error)
            .Add(p => p.OnUnlock, EventCallback.Factory.Create<string>(Owner, pin => unlocked?.Invoke(pin)))
            .Add(p => p.OnCancel, EventCallback.Factory.Create(Owner, () => cancelled?.Invoke())));

    [Fact]
    public void ClosedPinWindow_DrawsNothing()
    {
        using var ctx = NewContext();
        var cut = RenderPad(ctx, open: false);

        Assert.Empty(cut.FindAll(".admin-unlock"));
        Assert.Empty(cut.FindAll("dialog"));
    }

    [Fact]
    public void OpenPinWindow_SitsInTheSharedNativeFrameWithNoBackdropOfItsOwn()
    {
        using var ctx = NewContext();
        var cut = RenderPad(ctx, open: true);

        var frame = cut.Find("dialog.admin-unlock");
        Assert.NotNull(frame.QuerySelector(".app-dialog-shell"));
        Assert.NotNull(frame.QuerySelector("button[aria-label='7']"));
        Assert.Empty(cut.FindAll(".admin-unlock__backdrop"));
    }

    [Fact]
    public void PinWindow_HandsTheTypedPinToTheCallerOnContinueAndKeepsItOffThePage()
    {
        using var ctx = NewContext();
        string? received = null;
        var cut = RenderPad(ctx, open: true, unlocked: pin => received = pin);

        foreach (var digit in "1234")
        {
            cut.Find($"button[aria-label='{digit}']").Click();
        }

        Assert.DoesNotContain("1234", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("dialog.admin-unlock button").Single(button => button.TextContent.Contains("Continue", StringComparison.Ordinal)).Click();

        Assert.Equal("1234", received);
    }

    [Fact]
    public void PinWindow_ShowsTheCallersErrorAndCancelsThroughTheCallersHandler()
    {
        using var ctx = NewContext();
        var cancelled = false;
        var cut = RenderPad(ctx, open: true, error: "That PIN was not right.", cancelled: () => cancelled = true);

        Assert.Equal("That PIN was not right.", cut.Find("[role='alert'].admin-unlock__error").TextContent);
        cut.FindAll("dialog.admin-unlock button").Single(button => button.TextContent.Contains("Cancel", StringComparison.Ordinal)).Click();

        Assert.True(cancelled);
    }

    [Fact]
    public void FramedShell_ClosesThroughVisibleChangedWhenTheCallerGivesNoCloseHandler()
    {
        using var ctx = NewContext();
        bool? visible = null;
        var cut = ctx.Render<AppDialogShell>(parameters => parameters
            .Add(p => p.Framed, true)
            .Add(p => p.Visible, true)
            .Add(p => p.Title, "Window")
            .Add(p => p.Size, "Small")
            .Add(p => p.VisibleChanged, EventCallback.Factory.Create<bool>(Owner, value => visible = value)));

        cut.Find(".app-dialog-shell__close").Click();

        Assert.False(visible);
    }
}
