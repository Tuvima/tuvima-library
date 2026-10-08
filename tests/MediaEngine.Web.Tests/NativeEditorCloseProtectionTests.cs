using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class NativeEditorCloseProtectionTests : AsyncBunitContext
{
    public NativeEditorCloseProtectionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.CreateDefault());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton<ICollectionPersonalMediaClient>(new EmptyPersonalMediaClient());
    }

    [Fact]
    public async Task NativePersonCloseKeepsDirtyDraftOpenAndAllowsCloseAfterReset()
    {
        var host = Render<AppDialogHost>();
        var service = Services.GetRequiredService<AppDialogService>();
        var reference = await host.InvokeAsync(() => service.ShowAsync<PersonEditorDialog>("Person", new AppDialogParameters<PersonEditorDialog> { { editor => editor.Request, new MediaEditorLaunchRequest() } }));
        host.WaitForAssertion(() => Assert.NotNull(host.FindComponent<PersonEditorDialog>()));
        var editor = host.FindComponent<PersonEditorDialog>();
        SetField(editor.Instance, "_dirty", true);
        var frame = host.FindComponent<AppDialogFrame>();
        await frame.InvokeAsync(frame.Instance.RequestCancel);
        Assert.False(reference.Result.IsCompleted);
        Assert.True(GetField<bool>(editor.Instance, "_dirty"));
        Assert.Contains(Services.GetRequiredService<AppToastService>().Toasts,
            toast => toast.Message == "Save or reset your person changes before closing.");

        SetField(editor.Instance, "_dirty", false);
        await frame.InvokeAsync(frame.Instance.RequestCancel);
        Assert.True((await reference.Result)!.Canceled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCollectionCloseHonorsExistingDiscardDecision(bool discard)
    {
        JSInterop.Setup<bool>("confirm", _ => true).SetResult(discard);
        var host = Render<AppDialogHost>();
        var service = Services.GetRequiredService<AppDialogService>();
        var reference = await host.InvokeAsync(() => service.ShowAsync<CollectionEditorShell>("Collection", new AppDialogParameters<CollectionEditorShell> { { editor => editor.Request, new CollectionEditorLaunchRequest() } }));
        host.WaitForAssertion(() => Assert.NotNull(host.FindComponent<CollectionEditorShell>()));
        var editor = host.FindComponent<CollectionEditorShell>();
        var instance = editor.Instance;
        SetField(instance, "_hasUnsavedChanges", true);
        var frame = host.FindComponent<AppDialogFrame>();
        await frame.InvokeAsync(frame.Instance.RequestCancel);

        Assert.Equal(discard, reference.Result.IsCompleted);
        Assert.Equal(!discard, GetField<bool>(instance, "_hasUnsavedChanges"));
        Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "confirm");
        if (discard)
        {
            Assert.True((await reference.Result)!.Canceled);
        }
    }

    [Fact]
    public async Task NativeGalleryCloseWaitsForPendingSave()
    {
        var host = Render<AppDialogHost>();
        var service = Services.GetRequiredService<AppDialogService>();
        var reference = await host.InvokeAsync(() => service.ShowAsync<GalleryEditorShell>("Gallery"));
        host.WaitForAssertion(() => Assert.NotNull(host.FindComponent<GalleryEditorShell>()));
        var editor = host.FindComponent<GalleryEditorShell>();
        SetField(editor.Instance, "_saving", true);
        var frame = host.FindComponent<AppDialogFrame>();
        await frame.InvokeAsync(frame.Instance.RequestCancel);
        Assert.False(reference.Result.IsCompleted);
        SetField(editor.Instance, "_saving", false);
        await frame.InvokeAsync(frame.Instance.RequestCancel);
        Assert.True((await reference.Result)!.Canceled);
    }

    [Fact]
    public async Task ToastHostShowsFiveOldestMessagesAndPromotesQueuedMessageWhenDismissed()
    {
        var host = Render<AppToastHost>();
        var service = Services.GetRequiredService<AppToastService>();
        AppToast? first = null;
        await host.InvokeAsync(() =>
        {
            for (var number = 1; number <= 6; number++)
            {
                var toast = service.Add($"Notification {number}", AppSeverity.Info, options => options.RequireInteraction = true);
                first ??= toast;
            }
        });
        host.WaitForAssertion(() => Assert.Equal(5, host.FindAll(".app-toast").Count));
        Assert.Equal(Enumerable.Range(1, 5).Select(number => $"Notification {number}"), host.FindAll(".app-toast__message").Select(element => element.TextContent));
        await host.InvokeAsync(() => service.Remove(first!));
        host.WaitForAssertion(() => Assert.Equal(Enumerable.Range(2, 5).Select(number => $"Notification {number}"), host.FindAll(".app-toast__message").Select(element => element.TextContent)));
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T GetField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private sealed class EmptyPersonalMediaClient : ICollectionPersonalMediaClient
    {
        public string? LastError => null;
        public Task<IReadOnlyList<CollectionGalleryReferenceDto>> GetEligibleGalleriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionGalleryReferenceDto>>([]);
        public Task<IReadOnlyList<CollectionPersonalMediaSourceDto>> GetSourcesAsync(Guid collectionId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionPersonalMediaSourceDto>>([]);
        public Task<CollectionPersonalMediaSourceDto?> AddSourceAsync(Guid collectionId, CollectionPersonalMediaSourceWriteRequest request, CancellationToken ct = default) => Task.FromResult<CollectionPersonalMediaSourceDto?>(null);
        public Task<CollectionPersonalMediaSourceDto?> UpdateSourceAsync(Guid collectionId, Guid sourceId, CollectionPersonalMediaSourceWriteRequest request, CancellationToken ct = default) => Task.FromResult<CollectionPersonalMediaSourceDto?>(null);
        public Task<bool> RemoveSourceAsync(Guid collectionId, Guid sourceId, CancellationToken ct = default) => Task.FromResult(false);
    }
}
