using Bunit;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

public sealed class ContextSidebarShellTests : BunitContext
{
    public ContextSidebarShellTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Fact]
    public void OpeningSidebarKeepsMainContentAndAddsNonmodalLayoutSpace()
    {
        var cut = Render<ContextSidebarShell>(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.Width, 480)
            .Add(component => component.AriaLabel, "Ingestion details")
            .Add(component => component.MainContent, Text("Library content"))
            .AddChildContent(Text("Item details")));

        Assert.Contains("is-open", cut.Find(".context-sidebar-shell").ClassList);
        Assert.Contains("480px", cut.Find(".context-sidebar-shell").GetAttribute("style"));
        Assert.Contains("Library content", cut.Find(".context-sidebar-shell__main").TextContent);
        Assert.Contains("Item details", cut.Find(".context-sidebar-shell__aside").TextContent);
        Assert.Empty(cut.FindAll("[aria-modal='true'], .context-sidebar-shell__backdrop"));
    }

    [Fact]
    public void ClosingSidebarRestoresSingleColumnWithoutDiscardingMainContent()
    {
        var cut = Render<ContextSidebarShell>(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.MainContent, Text("Library content")));

        cut.Render(parameters => parameters
            .Add(component => component.IsOpen, false)
            .Add(component => component.MainContent, Text("Library content")));

        Assert.DoesNotContain("is-open", cut.Find(".context-sidebar-shell").ClassList);
        Assert.Empty(cut.FindAll(".context-sidebar-shell__aside"));
        Assert.Contains("Library content", cut.Find(".context-sidebar-shell__main").TextContent);
    }

    [Fact]
    public void ModalSidebarUsesNamedDialogAndClosesThroughHeaderAction()
    {
        var closed = false;
        var cut = Render<ContextSidebarShell>(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.IsModal, true)
            .Add(component => component.Appearance, "playback")
            .Add(component => component.Title, "Chapters")
            .Add(component => component.AriaLabel, "Playback context")
            .Add(component => component.OnClose, EventCallback.Factory.Create(this, () => closed = true))
            .AddChildContent(Text("Chapter list")));

        var dialog = cut.Find("aside[role='dialog']");
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));
        Assert.Equal("Chapters", dialog.GetAttribute("aria-label"));
        Assert.Equal("Chapters", cut.Find(".context-sidebar-shell__header h2").TextContent);
        Assert.Contains("context-sidebar-shell--playback", cut.Find(".context-sidebar-shell").ClassList);
        Assert.Single(cut.FindAll(".context-sidebar-shell__content"));
        Assert.Single(cut.FindAll(".context-sidebar-shell__backdrop"));

        cut.Find(".context-sidebar-shell__close").Click();

        Assert.True(closed);
    }

    [Fact]
    public async Task ModalCloseRestoresFocusBeforeInvokingOwnerDismissalAndGuardsReentry()
    {
        JSInterop.SetupModule("./js/context-sidebar.js");
        var restoreObservedByOwner = false;
        var closeCalls = 0;
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render<ContextSidebarShell>(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.IsModal, true)
            .Add(component => component.OwnerLeaseId, Guid.NewGuid())
            .Add(component => component.OnClose, EventCallback.Factory.Create(this, async () =>
            {
                closeCalls++;
                restoreObservedByOwner = JSInterop.Invocations.Any(invocation =>
                    invocation.Identifier == "restoreContextSidebarModal");
                closeStarted.TrySetResult();
                await closeGate.Task;
            }))
            .AddChildContent(Text("Modal body")));

        var firstClose = cut.Find(".context-sidebar-shell__close").ClickAsync();
        await closeStarted.Task;
        var secondClose = cut.Find(".context-sidebar-shell__close").ClickAsync();
        await secondClose;
        Assert.Equal(1, closeCalls);
        closeGate.TrySetResult();
        await firstClose;

        Assert.True(restoreObservedByOwner);
        Assert.Equal(1, closeCalls);
    }

    [Fact]
    public async Task RenderDuringAsyncCloseDoesNotReattachSameOwnerButReplacementStillAttaches()
    {
        var runtime = new GatedSidebarJsRuntime();
        Services.AddSingleton<IJSRuntime>(runtime);
        runtime.ReleaseRestore();
        var owner = Guid.NewGuid();
        var replacementOwner = Guid.NewGuid();
        var closeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeCallback = EventCallback.Factory.Create(this, async () =>
        {
            closeStarted.TrySetResult(true);
            await closeGate.Task;
        });

        var cut = Render<ContextSidebarShell>(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.IsModal, true)
            .Add(component => component.OwnerLeaseId, owner)
            .Add(component => component.Title, "Playback context")
            .Add(component => component.OnClose, closeCallback)
            .AddChildContent(Text("Initial body")));

        var closing = cut.Find(".context-sidebar-shell__close").ClickAsync();
        await closeStarted.Task;
        cut.Render(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.IsModal, true)
            .Add(component => component.OwnerLeaseId, owner)
            .Add(component => component.Title, "Playback context")
            .Add(component => component.OnClose, closeCallback)
            .AddChildContent(Text("Same owner rerender")));
        Assert.Single(runtime.AttachModalKeys);

        cut.Render(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.IsModal, true)
            .Add(component => component.OwnerLeaseId, replacementOwner)
            .Add(component => component.Title, "Playback context")
            .Add(component => component.OnClose, closeCallback)
            .AddChildContent(Text("Replacement owner body")));
        cut.WaitForAssertion(() => Assert.Equal(2, runtime.AttachModalKeys.Count));

        closeGate.SetResult(true);
        await closing;
        Assert.Contains("Replacement owner body", cut.Markup);
    }

    [Fact]
    public async Task DelayedCloseCannotDismissReplacementOwnerAndReattachesItsModal()
    {
        var runtime = new GatedSidebarJsRuntime();
        Services.AddSingleton<IJSRuntime>(runtime);
        var oldCloseCalls = 0;
        var replacementCloseCalls = 0;
        var cut = Render<ContextSidebarShell>(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.IsModal, true)
            .Add(component => component.OwnerLeaseId, Guid.NewGuid())
            .Add(component => component.Title, "Original panel")
            .Add(component => component.OnClose, EventCallback.Factory.Create(this, () => oldCloseCalls++))
            .AddChildContent(Text("Modal body")));

        var closing = cut.Find(".context-sidebar-shell__close").ClickAsync();
        await runtime.RestoreStarted.Task;
        cut.Render(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.IsModal, true)
            .Add(component => component.OwnerLeaseId, Guid.NewGuid())
            .Add(component => component.Title, "Replacement panel")
            .Add(component => component.OnClose, EventCallback.Factory.Create(this, () => replacementCloseCalls++))
            .AddChildContent(Text("Replacement body")));

        runtime.ReleaseRestore();
        await closing;
        cut.WaitForAssertion(() => Assert.Equal(2, runtime.AttachModalKeys.Count));

        Assert.Equal(0, oldCloseCalls);
        Assert.Equal(0, replacementCloseCalls);
        Assert.Contains("Replacement panel", cut.Markup);
    }

    [Fact]
    public void RetainedOpenOwnerReplacementReattachesWithNewKeyWithoutRestoringOldFocus()
    {
        var runtime = new GatedSidebarJsRuntime();
        Services.AddSingleton<IJSRuntime>(runtime);
        var firstOwner = Guid.NewGuid();
        var replacementOwner = Guid.NewGuid();
        var cut = Render<ContextSidebarShell>(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.IsModal, true)
            .Add(component => component.OwnerLeaseId, firstOwner)
            .Add(component => component.Title, "First owner")
            .AddChildContent(Text("Modal body")));

        cut.Render(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.IsModal, true)
            .Add(component => component.OwnerLeaseId, replacementOwner)
            .Add(component => component.Title, "Replacement owner")
            .AddChildContent(Text("Replacement body")));

        cut.WaitForAssertion(() => Assert.Equal(2, runtime.AttachModalKeys.Count));
        Assert.NotEqual(runtime.AttachModalKeys[0], runtime.AttachModalKeys[1]);
        Assert.Empty(runtime.RestoreRequests);
        Assert.Contains("Replacement owner", cut.Markup);
    }

    [Fact]
    public async Task DisposalDuringDelayedClosePreventsOwnerCallback()
    {
        var runtime = new GatedSidebarJsRuntime();
        Services.AddSingleton<IJSRuntime>(runtime);
        var closeCalls = 0;
        var cut = Render<ContextSidebarShell>(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.IsModal, true)
            .Add(component => component.OwnerLeaseId, Guid.NewGuid())
            .Add(component => component.OnClose, EventCallback.Factory.Create(this, () => closeCalls++))
            .AddChildContent(Text("Modal body")));

        var closing = cut.Find(".context-sidebar-shell__close").ClickAsync();
        await runtime.RestoreStarted.Task;
        var disposing = cut.Instance.DisposeAsync().AsTask();
        runtime.ReleaseRestore();
        await Task.WhenAll(closing, disposing);

        Assert.Equal(0, closeCalls);
    }

    [Fact]
    public void SidebarClampsWidthAndRendersOneBody()
    {
        var cut = Render<ContextSidebarShell>(parameters => parameters
            .Add(component => component.IsOpen, true)
            .Add(component => component.Width, 640)
            .AddChildContent(Text("Context body")));

        Assert.Contains("480px", cut.Find(".context-sidebar-shell").GetAttribute("style"));
        Assert.Contains("Context body", cut.Find(".context-sidebar-shell__content").TextContent);
        Assert.Single(cut.FindAll(".context-sidebar-shell__content"));
    }

    private static RenderFragment Text(string value) => builder => builder.AddContent(0, value);

    private sealed class GatedSidebarJsRuntime : IJSRuntime
    {
        private readonly SidebarJsModule _module;

        public GatedSidebarJsRuntime() => _module = new SidebarJsModule(this);
        public TaskCompletionSource<bool> RestoreStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> RestoreGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> AttachModalKeys => _module.AttachModalKeys;
        public List<(string Key, bool RestorePreviousFocus)> RestoreRequests => _module.RestoreRequests;
        public void ReleaseRestore() => RestoreGate.TrySetResult(true);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
            {
                return ValueTask.FromResult((TValue)(object)_module);
            }
            return ValueTask.FromResult(default(TValue)!);
        }

        private sealed class SidebarJsModule(GatedSidebarJsRuntime owner) : IJSObjectReference
        {
            public List<string> AttachModalKeys { get; } = [];
            public List<(string Key, bool RestorePreviousFocus)> RestoreRequests { get; } = [];

            public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
                await InvokeAsync<TValue>(identifier, CancellationToken.None, args);

            public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            {
                if (identifier == "attachContextSidebarModal" && args is { Length: > 2 })
                {
                    AttachModalKeys.Add((string)args[2]!);
                }
                if (identifier == "restoreContextSidebarModal")
                {
                    if (args is { Length: > 2 })
                    {
                        RestoreRequests.Add(((string)args[1]!, (bool)args[2]!));
                    }
                    owner.RestoreStarted.TrySetResult(true);
                    await owner.RestoreGate.Task.WaitAsync(cancellationToken);
                }
                return default!;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
