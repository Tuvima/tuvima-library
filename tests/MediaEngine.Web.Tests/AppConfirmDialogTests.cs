using Bunit;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The one shared confirmation dialog (details overlay spec D17): a title, a message, an optional preview, a primary and a
/// secondary button with the labels the caller chooses, and Cancelled as the third answer when it is closed without choosing.
/// </summary>
public sealed class AppConfirmDialogTests : AsyncBunitContext
{
    public AppConfirmDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IAppDialogService Dialogs => Services.GetRequiredService<IAppDialogService>();

    private Task<AppConfirmResult> Show(bool destructive = false, RenderFragment? preview = null) =>
        AppConfirmDialog.ShowAsync(Dialogs, "Save or discard?", "You have changes that are not saved yet.", "Save", "Discard", destructive, preview);

    [Fact]
    public async Task ThePrimaryButtonReturnsPrimary()
    {
        var host = Render<AppDialogHost>();
        var answer = Show();
        host.WaitForElement(".app-confirm");

        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Save").Click();

        Assert.Equal(AppConfirmResult.Primary, await answer);
    }

    [Fact]
    public async Task TheSecondaryButtonReturnsSecondary()
    {
        var host = Render<AppDialogHost>();
        var answer = Show();
        host.WaitForElement(".app-confirm");

        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Discard").Click();

        Assert.Equal(AppConfirmResult.Secondary, await answer);
    }

    [Fact]
    public async Task ClosingTheDialogWithoutChoosingReturnsCancelled()
    {
        var host = Render<AppDialogHost>();
        var answer = Show();
        host.WaitForElement(".app-confirm");

        host.Find("button[aria-label='Close dialog']").Click();

        Assert.Equal(AppConfirmResult.Cancelled, await answer);
        Assert.Empty(host.FindAll(".app-confirm"));
    }

    [Fact]
    public async Task EscapeAndTheBackdropEndInTheSameCancelledAnswerAsTheCloseButton()
    {
        var host = Render<AppDialogHost>();
        var answer = Show();
        host.WaitForElement(".app-confirm");
        var entry = Services.GetRequiredService<AppDialogService>().Dialogs.Single();

        // app-dialog.js reports Esc and a click on the backdrop through the frame's RequestCancel, which cancels the dialog.
        await host.InvokeAsync(() => host.FindComponent<AppDialogFrame>().Instance.RequestCancel());

        Assert.Equal(AppConfirmResult.Cancelled, await answer);
        Assert.True(entry.Options.CloseOnEscapeKey);
    }

    [Fact]
    public async Task TheDialogShowsTheTitleTheMessageAndTheCallersLabelsInTheSharedFrame()
    {
        var host = Render<AppDialogHost>();
        _ = Show();
        host.WaitForElement(".app-confirm");

        Assert.Equal("Save or discard?", host.Find(".app-dialog-shell__title").TextContent.Trim());
        Assert.Equal("You have changes that are not saved yet.", host.Find(".app-confirm__message").TextContent.Trim());
        Assert.Equal(["Discard", "Save"], host.FindAll(".app-dialog-shell__footer button").Select(button => button.TextContent.Trim()));
        Assert.Empty(host.FindAll(".app-confirm__preview"));
        Assert.Contains("app-dialog-shell--small", host.Find(".app-dialog-shell").ClassList);

        host.Find("button[aria-label='Close dialog']").Click();
        await Task.Yield();
    }

    [Fact]
    public async Task APreviewIsShownUnderTheMessageInTheWiderFrame()
    {
        var host = Render<AppDialogHost>();
        RenderFragment preview = builder =>
        {
            builder.OpenElement(0, "p");
            builder.AddAttribute(1, "class", "move-preview");
            builder.AddContent(2, "The file will be renamed to 02 - Paranoid Android.flac");
            builder.CloseElement();
        };
        _ = Show(preview: preview);
        host.WaitForElement(".app-confirm__preview");

        Assert.Contains("02 - Paranoid Android.flac", host.Find(".app-confirm__preview .move-preview").TextContent);
        Assert.Contains("app-dialog-shell--medium", host.Find(".app-dialog-shell").ClassList);

        host.Find("button[aria-label='Close dialog']").Click();
        await Task.Yield();
    }

    [Fact]
    public async Task ADestructivePrimaryIsRedAndTheSaferButtonHasTheFocus()
    {
        var host = Render<AppDialogHost>();
        _ = Show(destructive: true);
        host.WaitForElement(".app-confirm");

        var primary = host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Save");
        var secondary = host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Discard");
        Assert.Contains("app-tone--error", primary.ClassList);
        Assert.True(secondary.HasAttribute("autofocus"));
        Assert.False(primary.HasAttribute("autofocus"));

        host.Find("button[aria-label='Close dialog']").Click();
        await Task.Yield();
    }

    [Fact]
    public async Task APlainPrimaryIsThePrimaryToneAndHasTheFocus()
    {
        var host = Render<AppDialogHost>();
        _ = Show();
        host.WaitForElement(".app-confirm");

        var primary = host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Save");
        Assert.DoesNotContain("app-tone--error", primary.ClassList);
        Assert.True(primary.HasAttribute("autofocus"));

        host.Find("button[aria-label='Close dialog']").Click();
        await Task.Yield();
    }

    [Fact]
    public async Task TheCancelButtonIsOutlinedAndComesBeforeTheMainButton()
    {
        var host = Render<AppDialogHost>();
        _ = Show();
        host.WaitForElement(".app-confirm");

        var buttons = host.FindAll(".app-dialog-shell__footer button").ToList();
        Assert.Equal("Discard", buttons[0].TextContent.Trim());
        Assert.Contains("app-button--outlined", buttons[0].ClassList);
        Assert.Equal("Save", buttons[1].TextContent.Trim());
        Assert.Contains("app-button--filled", buttons[1].ClassList);

        host.Find("button[aria-label='Close dialog']").Click();
        await Task.Yield();
    }

    [Theory]
    [InlineData(false, null, "warning")]
    [InlineData(true, null, "error")]
    [InlineData(false, AppUiTone.Info, "info")]
    public async Task TheIconFollowsTheTone(bool destructive, AppUiTone? tone, string expected)
    {
        var host = Render<AppDialogHost>();
        _ = AppConfirmDialog.ShowAsync(Dialogs, "Title", "Message", "OK", "Cancel", destructive, tone: tone);
        host.WaitForElement(".app-confirm");

        Assert.Contains($"app-dialog-shell__icon--{expected}", host.Find(".app-dialog-shell__icon").ClassList);
        Assert.NotNull(host.Find(".app-dialog-shell__header .app-dialog-shell__close"));

        host.Find("button[aria-label='Close dialog']").Click();
        await Task.Yield();
    }

    [Fact]
    public async Task TheMessageBoxIsTheSharedCardWithCancelBeforeTheMainButtonAndOneTitle()
    {
        var host = Render<AppDialogHost>();
        _ = Dialogs.ShowMessageBoxAsync("Heads up", "Something to read.", yesText: "OK", cancelText: "Cancel");
        host.WaitForElement(".app-dialog-shell");

        Assert.Equal("Heads up", host.Find(".app-dialog-shell__title").TextContent.Trim());
        Assert.Empty(host.FindAll(".app-native-dialog__header"));
        Assert.Contains("Something to read.", host.Find(".app-message-box__message").TextContent);
        var buttons = host.FindAll(".app-dialog-shell__footer button").ToList();
        Assert.Equal(["Cancel", "OK"], buttons.Select(button => button.TextContent.Trim()));
        Assert.Contains("app-button--outlined", buttons[0].ClassList);
        Assert.Contains("app-button--filled", buttons[1].ClassList);

        host.Find("button[aria-label='Close dialog']").Click();
        await Task.Yield();
    }
}
