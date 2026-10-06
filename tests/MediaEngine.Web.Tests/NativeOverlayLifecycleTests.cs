using Bunit;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class NativeOverlayLifecycleTests : AsyncBunitContext
{
    public NativeOverlayLifecycleTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
    }

    [Fact]
    public async Task HostedComponentOptionsCannotOverrideServiceEscapePolicyOrExplicitChanges()
    {
        var host = Render<AppDialogHost>();
        var service = Services.GetRequiredService<AppDialogService>();
        var reference = await host.InvokeAsync(() => service.ShowAsync<ConflictingOptionsDialog>("Picker",
            new AppDialogOptions { CloseOnEscapeKey = true, MaxWidth = AppMaxWidth.Large }));
        host.WaitForAssertion(() => Assert.NotNull(host.FindComponent<ConflictingOptionsDialog>()));
        host.WaitForAssertion(() => Assert.True(Assert.Single(service.Dialogs).Options.CloseOnEscapeKey));
        var context = (IAppDialogContext)reference;
        Assert.Equal(AppMaxWidth.Large, context.Options.MaxWidth);
        await host.InvokeAsync(() => context.SetOptionsAsync(context.Options with { CloseOnEscapeKey = false }));
        host.WaitForAssertion(() => Assert.False(Assert.Single(service.Dialogs).Options.CloseOnEscapeKey));
        await host.InvokeAsync(() => context.SetOptionsAsync(context.Options with { CloseOnEscapeKey = true }));
        host.WaitForAssertion(() => Assert.True(Assert.Single(service.Dialogs).Options.CloseOnEscapeKey));
    }

    [Fact]
    public async Task MenuClosesAndRestoresTriggerBeforeAwaitingAnActionResult()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedBeforeAction = false;
        var restoredBeforeAction = false;
        IRenderedComponent<AppOverflowMenu>? menu = null;
        menu = Render<AppOverflowMenu>(parameters => parameters.Add(menu => menu.Label, "Add Artwork")
            .AddChildContent<AppMenuItem>(item => item.Add(item => item.Label, "Choose from Library")
                .Add(item => item.OnClick, async () =>
                {
                    closedBeforeAction = menu!.Find(".app-overflow-menu__trigger").GetAttribute("aria-expanded") == "false";
                    restoredBeforeAction = JSInterop.Invocations.Any(invocation => invocation.Identifier == "Blazor._internal.domWrapper.focus");
                    started.TrySetResult();
                    await completion.Task;
                })));
        menu.Find(".app-overflow-menu__trigger").Click();
        var click = menu.Find("[role='menuitem']").ClickAsync(new());
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(closedBeforeAction);
            Assert.True(restoredBeforeAction);
            Assert.False(click.IsCompleted, "The picker result must still be pending after the menu closes.");
        }
        finally { completion.TrySetResult(); await click; }
    }

    public sealed class ConflictingOptionsDialog : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<AppDialog>(0);
            builder.AddAttribute(1, nameof(AppDialog.Options), new AppDialogOptions { CloseOnEscapeKey = false, MaxWidth = AppMaxWidth.Small });
            builder.AddAttribute(2, nameof(AppDialog.ChildContent), (RenderFragment)(content => content.AddContent(0, "Picker")));
            builder.CloseComponent();
        }
    }
}
