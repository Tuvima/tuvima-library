using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests;

public sealed class AppOverlayServiceTests
{
    private sealed class TestDialog : ComponentBase { [Parameter] public int Number { get; set; } }

    [Fact]
    public async Task TypedParametersAndResultsRetainTheirValues()
    {
        using var service = new AppDialogService();
        var reference = await service.ShowAsync<TestDialog>("Edit", new AppDialogParameters<TestDialog> { { component => component.Number, 42 } });
        var entry = Assert.Single(service.Dialogs);
        Assert.Equal(42, entry.Parameters[nameof(TestDialog.Number)]);
        entry.Close(AppDialogResult.Ok(7));
        var result = await reference.Result;
        Assert.NotNull(result);
        Assert.False(result.Canceled);
        Assert.Equal(7, result.Data);
        Assert.Equal(typeof(int), result.DataType);
        Assert.Empty(service.Dialogs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardVetoesBothCancelAndCloseUntilAllowed(bool success)
    {
        using var service = new AppDialogService();
        var reference = await service.ShowAsync<TestDialog>();
        var context = Assert.Single(service.Dialogs);
        var allowed = false;
        var attempts = 0;
        using var guard = context.RegisterCloseGuard(() => { attempts++; return Task.FromResult(allowed); });
        if (success)
        {
            await context.CloseAsync(AppDialogResult.Ok(true));
        }
        else
        {
            await context.CancelAsync();
        }
        Assert.False(reference.Result.IsCompleted);
        Assert.Single(service.Dialogs);
        allowed = true;
        if (success)
        {
            await context.CloseAsync(AppDialogResult.Ok(true));
        }
        else
        {
            await context.CancelAsync();
        }
        Assert.Equal(2, attempts);
        Assert.Equal(!success, (await reference.Result)!.Canceled);
    }

    [Fact]
    public async Task ConcurrentCloseDoesNotRunConfirmationTwice()
    {
        using var service = new AppDialogService();
        var reference = await service.ShowAsync<TestDialog>();
        var context = Assert.Single(service.Dialogs);
        var confirmation = new TaskCompletionSource<bool>();
        var attempts = 0;
        context.SetCloseGuard(() => { attempts++; return confirmation.Task; });
        var first = context.CancelAsync();
        await context.CancelAsync();
        Assert.Equal(1, attempts);
        confirmation.SetResult(true);
        await first;
        Assert.True((await reference.Result)!.Canceled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task MessageBoxPreservesYesNoAndCancel(bool? answer)
    {
        using var service = new AppDialogService();
        var pending = service.ShowMessageBoxAsync("Confirm", "Keep changes?", yesText: "Yes", noText: "No", cancelText: "Cancel");
        var dialog = Assert.Single(service.Dialogs);
        Assert.Equal("Keep changes?", dialog.Parameters[nameof(AppMessageBox.Message)]);
        Assert.Equal("Cancel", dialog.Parameters[nameof(AppMessageBox.CancelText)]);
        if (answer.HasValue)
        {
            await dialog.CloseAsync(AppDialogResult.Ok(answer.Value));
        }
        else
        {
            await dialog.CancelAsync();
        }
        Assert.Equal(answer, await pending);
    }

    [Fact]
    public async Task OptionsCanChangeWithoutReplacingPendingResult()
    {
        using var service = new AppDialogService();
        var reference = await service.ShowAsync<TestDialog>("Editor", new AppDialogOptions { CloseOnEscapeKey = true });
        var context = Assert.Single(service.Dialogs);
        await context.SetOptionsAsync(context.Options with { CloseOnEscapeKey = false });
        Assert.False(context.Options.CloseOnEscapeKey);
        Assert.False(reference.Result.IsCompleted);
    }

    [Fact]
    public async Task DisposingCircuitCompletesPendingDialogsAsCancelled()
    {
        var service = new AppDialogService();
        var reference = await service.ShowAsync<TestDialog>();
        service.Dispose();
        Assert.True((await reference.Result)!.Canceled);
        Assert.Empty(service.Dialogs);
    }

    [Fact]
    public async Task ToastActionRunsAsynchronouslyOnceAndRemovesMessage()
    {
        using var service = new AppToastService();
        var action = new TaskCompletionSource();
        var calls = 0;
        var toast = service.Add("Status updated", AppSeverity.Success, options =>
        {
            options.Action = "Undo";
            options.OnClick = async _ => { calls++; await action.Task; };
        });
        var pending = service.InvokeActionAsync(toast);
        await service.InvokeActionAsync(toast);
        Assert.Equal(1, calls);
        Assert.Single(service.Toasts);
        action.SetResult();
        await pending;
        Assert.Empty(service.Toasts);
    }

    [Fact]
    public async Task CloseGuardRegistrationReleasesOnlyItsOwnGuard()
    {
        using var service = new AppDialogService();
        var reference = await service.ShowAsync<TestDialog>();
        var context = Assert.Single(service.Dialogs);
        var old = context.RegisterCloseGuard(() => Task.FromResult(false));
        using var current = context.RegisterCloseGuard(() => Task.FromResult(false));
        old.Dispose();
        context.Cancel();
        Assert.False(reference.Result.IsCompleted);
        current.Dispose();
        context.Cancel();
        Assert.True(reference.Result.IsCompleted);
    }

    [Fact]
    public void ActiveDialogFrameSelectsOnlyTheTopToastHostAndRestoresItsParent()
    {
        using var service = new AppDialogService();
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        service.SetFrameVisibility(parent, true);
        service.SetFrameVisibility(child, true);
        service.SetFrameVisibility(parent, true);
        Assert.Equal(child, service.ActiveFrameId);
        service.SetFrameVisibility(child, false);
        Assert.Equal(parent, service.ActiveFrameId);
        service.SetFrameVisibility(parent, false);
        Assert.Null(service.ActiveFrameId);
    }

    [Fact]
    public void ToastDefaultsRetainBaselineTimingOpacityAndDuplicateSuppression()
    {
        using var service = new AppToastService();
        var toast = service.Add("Saved", AppSeverity.Success);
        var duplicate = service.Add("Saved", AppSeverity.Success);
        Assert.Same(toast, duplicate);
        Assert.Single(service.Toasts);
        Assert.Equal(5000, toast.Options.VisibleStateDuration);
        Assert.Equal(1000, toast.Options.ShowTransitionDuration);
        Assert.Equal(2000, toast.Options.HideTransitionDuration);
        Assert.Equal(95, toast.Options.MaximumOpacity);
        Assert.True(toast.Options.ShowCloseIcon);
        Assert.Null(toast.Options.RequireInteraction);
    }
}
