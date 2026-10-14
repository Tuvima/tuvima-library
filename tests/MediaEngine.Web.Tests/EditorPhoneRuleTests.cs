using System.Reflection;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>Phones are consumption devices: no editor shell ever opens there; tablets and desktops keep it.</summary>
public sealed class EditorPhoneRuleTests
{
    private static async Task<DeviceContextService> DeviceAsync(string formFactor)
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor != "desktop"));
        return device;
    }

    private static MediaEditorLaunchRequest MediaRequest() => new()
    {
        EntityIds = [Guid.Parse("22222222-3333-4444-5555-666666666666")],
        LaunchEntityId = Guid.Parse("22222222-3333-4444-5555-666666666666"),
        LaunchEntityKind = "Work",
        Mode = SharedMediaEditorMode.Normal,
    };

    [Fact]
    public async Task MediaLauncherRefusesOnPhoneOpensNothingAndShowsTheNote()
    {
        var dialogs = DispatchProxy.Create<IAppDialogService, DialogSpy>();
        var spy = (DialogSpy)(object)dialogs;
        var toasts = new AppToastService();
        var launcher = new MediaEditorLauncherService(dialogs, null, await DeviceAsync("phone"), toasts);

        var result = await launcher.TryOpenAsync(MediaRequest());

        Assert.Equal(EditorLaunchResult.NotAvailableOnPhone, result);
        Assert.False(await launcher.OpenAsync(MediaRequest()));
        Assert.Equal(0, spy.ShowCount);
        Assert.NotEmpty(toasts.Toasts);
        Assert.All(toasts.Toasts, toast => Assert.Equal("Edit on a computer or tablet", toast.Message));
    }

    [Theory]
    [InlineData("tablet")]
    [InlineData("desktop")]
    public async Task MediaLauncherOpensTheEditorOnTabletAndDesktop(string formFactor)
    {
        var dialogs = DispatchProxy.Create<IAppDialogService, DialogSpy>();
        var spy = (DialogSpy)(object)dialogs;
        var launcher = new MediaEditorLauncherService(dialogs, null, await DeviceAsync(formFactor), new AppToastService());

        Assert.Equal(EditorLaunchResult.Applied, await launcher.TryOpenAsync(MediaRequest()));
        Assert.Equal(1, spy.ShowCount);
        Assert.Equal(typeof(SharedMediaEditorShell), spy.LastShell);
    }

    [Fact]
    public async Task CollectionAndGalleryLaunchersRefuseOnPhoneAndOpenOnTablet()
    {
        var phoneDialogs = DispatchProxy.Create<IAppDialogService, DialogSpy>();
        var phoneSpy = (DialogSpy)(object)phoneDialogs;
        var phone = await DeviceAsync("phone");
        Assert.Equal(EditorLaunchResult.NotAvailableOnPhone,
            await new CollectionEditorLauncherService(phoneDialogs, null, phone, new AppToastService()).TryOpenAsync(new CollectionEditorLaunchRequest()));
        Assert.Equal(EditorLaunchResult.NotAvailableOnPhone,
            await new GalleryEditorLauncherService(phoneDialogs, phone, new AppToastService()).TryOpenAsync(new GalleryEditorLaunchRequest()));
        Assert.Equal(0, phoneSpy.ShowCount);

        var tabletDialogs = DispatchProxy.Create<IAppDialogService, DialogSpy>();
        var tabletSpy = (DialogSpy)(object)tabletDialogs;
        var tablet = await DeviceAsync("tablet");
        Assert.Equal(EditorLaunchResult.Applied,
            await new CollectionEditorLauncherService(tabletDialogs, null, tablet, new AppToastService()).TryOpenAsync(new CollectionEditorLaunchRequest()));
        Assert.Equal(EditorLaunchResult.Applied,
            await new GalleryEditorLauncherService(tabletDialogs, tablet, new AppToastService()).TryOpenAsync(new GalleryEditorLaunchRequest()));
        Assert.Equal(2, tabletSpy.ShowCount);
    }

    private class DialogSpy : DispatchProxy
    {
        public int ShowCount { get; private set; }
        public Type? LastShell { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "ShowAsync" && targetMethod.IsGenericMethod)
            {
                ShowCount++;
                LastShell = targetMethod.GetGenericArguments()[0];
                return Task.FromResult<IAppDialogReference?>(DispatchProxy.Create<IAppDialogReference, ReferenceSpy>());
            }

            return null;
        }
    }

    private class ReferenceSpy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_Result" ? Task.FromResult<AppDialogResult?>(AppDialogResult.Ok(true)) : null;
    }
}
