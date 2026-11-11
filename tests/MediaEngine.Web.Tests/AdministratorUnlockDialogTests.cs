using Bunit;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;
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

    [Fact]
    public void PinWindow_TurnsOffBackdropPressAndEscapeAndIsNotASheet()
    {
        using var ctx = NewContext();
        var module = ctx.JSInterop.SetupModule("./js/app-dialog.js");
        module.Mode = JSRuntimeMode.Loose;

        RenderPad(ctx, open: true);

        // update(dialog, dotnet, open, backdropClick, escape, frameId, sheet)
        var update = module.Invocations.Last(call => call.Identifier == "update");
        Assert.Equal(true, update.Arguments[2]);
        Assert.Equal(false, update.Arguments[3]);
        Assert.Equal(false, update.Arguments[4]);
        Assert.Equal(false, update.Arguments[6]);
    }

    [Fact]
    public void PinWindow_WhileBusy_DisablesCancelContinueAndThePad()
    {
        using var ctx = NewContext();
        var cut = RenderPad(ctx, open: true);
        foreach (var digit in "1234")
        {
            cut.Find($"button[aria-label='{digit}']").Click();
        }

        cut.Render(parameters => parameters.Add(p => p.Busy, true));

        var footer = cut.FindAll("dialog.admin-unlock .app-dialog-shell__footer button");
        Assert.Equal(2, footer.Count);
        Assert.All(footer, button => Assert.True(button.HasAttribute("disabled")));
        Assert.All(cut.FindAll(".pin-pad__key"), key => Assert.True(key.HasAttribute("disabled")));
    }

    [Fact]
    public void PinWindow_AfterCancelAndReopen_StartsWithAnEmptyPin()
    {
        using var ctx = NewContext();
        var cut = RenderPad(ctx, open: true);
        foreach (var digit in "1234")
        {
            cut.Find($"button[aria-label='{digit}']").Click();
        }

        Assert.False(ContinueButton(cut).HasAttribute("disabled"));
        cut.FindAll("dialog.admin-unlock button").Single(button => button.TextContent.Contains("Cancel", StringComparison.Ordinal)).Click();
        cut.Render(parameters => parameters.Add(p => p.Open, false));
        cut.Render(parameters => parameters.Add(p => p.Open, true));

        Assert.True(ContinueButton(cut).HasAttribute("disabled"));
        Assert.Empty(cut.FindAll(".pin-pad__dot.is-filled"));
    }

    [Fact]
    public void PinWindow_ClearsThePinBeforeHandingItToTheCaller()
    {
        using var ctx = NewContext();
        string? received = null;
        string? heldWhenRaised = null;
        IRenderedComponent<AdministratorUnlockDialog>? cut = null;
        cut = RenderPad(ctx, open: true, unlocked: pin =>
        {
            received = pin;
            heldWhenRaised = (string?)typeof(AdministratorUnlockDialog)
                .GetField("_pin", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(cut!.Instance);
        });
        foreach (var digit in "5678")
        {
            cut.Find($"button[aria-label='{digit}']").Click();
        }

        ContinueButton(cut).Click();

        Assert.Equal("5678", received);
        Assert.Equal(string.Empty, heldWhenRaised);
    }

    [Fact]
    public void GateWindow_WithoutADialogToClose_DrawsNothing()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<AdministratorUnlockDialog>(parameters => parameters.Add(p => p.Gate, true).Add(p => p.Open, true));

        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public async Task ServiceOpenedShell_WithATitleAndNoCloseHandler_StillShowsACloseButtonThatCancelsTheWindow()
    {
        using var ctx = NewContext();
        var host = ctx.Render<AppDialogHost>();
        var service = ctx.Services.GetRequiredService<AppDialogService>();

        var reference = await host.InvokeAsync(() => service.ShowAsync<TitledShellWithoutClose>("Opened title",
            new AppDialogOptions { CloseButton = true, MaxWidth = AppMaxWidth.Small }));
        host.WaitForAssertion(() => Assert.NotNull(host.FindComponent<TitledShellWithoutClose>()));

        Assert.Equal("Opened title", host.Find(".app-dialog-shell__title").TextContent);
        host.Find(".app-dialog-shell__close").Click();

        var result = await reference.Result;
        Assert.True(result!.Canceled);
    }

    [Fact]
    public async Task ServiceOpenedShell_TheOpenerGaveNoCloseButton_StaysWithoutOne()
    {
        using var ctx = NewContext();
        var host = ctx.Render<AppDialogHost>();
        var service = ctx.Services.GetRequiredService<AppDialogService>();

        await host.InvokeAsync(() => service.ShowAsync<TitledShellWithoutClose>("Opened title",
            new AppDialogOptions { CloseButton = false, NoHeader = true, MaxWidth = AppMaxWidth.Small }));
        host.WaitForAssertion(() => Assert.NotNull(host.FindComponent<TitledShellWithoutClose>()));

        Assert.Empty(host.FindAll(".app-dialog-shell__close"));
    }

    [Fact]
    public void BareShell_DrawsOnlyTheBodyWithNoHeaderOrPadding()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<AppDialogShell>(parameters => parameters
            .Add(p => p.Bare, true)
            .Add(p => p.HostClass, "sme-dialog")
            .AddChildContent("<p>Editor</p>"));

        Assert.NotNull(cut.Find(".app-dialog-host.sme-dialog > .tl-dialog-content > p"));
        Assert.Empty(cut.FindAll("header"));
        Assert.Empty(cut.FindAll(".app-dialog-shell__pad"));
    }

    private static AngleSharp.Dom.IElement ContinueButton(IRenderedComponent<AdministratorUnlockDialog> cut) =>
        cut.FindAll("dialog.admin-unlock .app-dialog-shell__footer button").Single(button => button.TextContent.Contains("Continue", StringComparison.Ordinal));

    /// <summary>A service-opened window that titles itself and gives the shell no close handler of its own.</summary>
    public sealed class TitledShellWithoutClose : ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<AppDialogShell>(0);
            builder.AddAttribute(1, nameof(AppDialogShell.Title), "Opened title");
            builder.AddAttribute(2, nameof(AppDialogShell.Size), "Small");
            builder.AddAttribute(3, nameof(AppDialogShell.ChildContent), (RenderFragment)(content => content.AddContent(0, "Body")));
            builder.CloseComponent();
        }
    }
}
