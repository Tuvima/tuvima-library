using System.Reflection;
using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Editing is desktop-only: it needs a desktop form factor AND a fine, hover-capable primary pointer. Phone, tablet, TV,
/// a native shell, an unknown form factor and a browser that cannot report its pointer all mean "no", and every launcher
/// refuses on its own rather than relying on hidden buttons.
/// </summary>
public sealed class EditorDeviceRuleTests
{
    // deviceClass, formFactor, touch, finePointer, hover, expected
    public static TheoryData<string, string, bool, bool, bool, bool> Matrix => new()
    {
        { "web", "desktop", false, true, true, true },          // desktop with a mouse or trackpad (also a touch laptop whose primary pointer is the mouse)
        { "web", "desktop", true, false, false, false },        // desktop site requested on an iPad: coarse pointer, no hover
        { "web", "desktop", true, false, true, false },         // desktop with a coarse primary pointer
        { "web", "desktop", false, true, false, false },        // fine pointer that cannot hover (pen, some touch screens)
        { "web", "desktop", false, false, true, false },        // hover but not fine
        { "web", "desktop", false, false, false, false },       // the browser reported neither pointer signal
        { "web", "tablet", true, false, false, false },         // tablet
        { "web", "tablet", false, true, true, false },          // a tablet with a mouse is still a tablet
        { "mobile", "phone", true, false, false, false },       // phone
        { "mobile", "phone", false, true, true, false },        // phone with a fine pointer
        { "television", "tv", false, true, true, false },       // TV
        { "web", "tv", false, true, true, false },              // TV form factor reported by the browser
        { "native", "native", false, true, true, false },       // a native app shell naming itself
        { "web", "native", false, true, true, false },          // unknown form factor on the web class
        { "web", "", false, false, false, false },              // nothing reported
    };

    private static async Task<DeviceContextService> DeviceAsync(DeviceContextSnapshot snapshot)
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(snapshot);
        return device;
    }

    private static Task<DeviceContextService> DesktopAsync() =>
        DeviceAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

    private static Task<DeviceContextService> DeviceFor(string formFactor) => formFactor switch
    {
        "phone" => DeviceAsync(new DeviceContextSnapshot("mobile", "phone", true)),
        "tablet" => DeviceAsync(new DeviceContextSnapshot("web", "tablet", true)),
        "tv" => DeviceAsync(new DeviceContextSnapshot("television", "tv", false, FinePointer: true, Hover: true)),
        _ => DesktopAsync(),
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task OnlyADesktopWithAFineHoverPointerCanEdit(
        string deviceClass, string formFactor, bool touch, bool finePointer, bool hover, bool expected)
    {
        Assert.Equal(expected, EditorDeviceRule.CanEdit(deviceClass, formFactor, finePointer, hover));

        var device = await DeviceAsync(new DeviceContextSnapshot(deviceClass, formFactor, touch, finePointer, hover));

        Assert.Equal(expected, EditorDeviceRule.CanEdit(device));
        Assert.Equal(expected, device.CanEdit);
    }

    [Fact]
    public async Task ABrowserThatReportsNoPointerSignalsCanNeverEdit()
    {
        // The older three-argument snapshot (no pointer information) is "undetectable", which means no.
        var device = await DeviceAsync(new DeviceContextSnapshot("web", "desktop", false));

        Assert.False(device.CanEdit);
        Assert.False(EditorDeviceRule.CanEdit((DeviceContextService?)null));
        Assert.False(new DeviceContextService(EngineApiClientStub.CreateDefault()).CanEdit);
    }

    [Fact]
    public async Task APointerChangeAloneRevokesEditing()
    {
        var device = await DesktopAsync();
        var changes = 0;
        device.OnChanged += () => changes++;
        Assert.True(device.CanEdit);

        await device.SwitchDeviceAsync(new DeviceContextSnapshot("web", "desktop", true, FinePointer: false, Hover: false));

        Assert.False(device.CanEdit);
        Assert.True(changes >= 1);
    }

    [Fact]
    public async Task EveryLauncherOpensOnADesktopWithAMouse()
    {
        var desktopDialogs = DispatchProxy.Create<IAppDialogService, DialogSpy>();
        var desktopSpy = (DialogSpy)(object)desktopDialogs;
        var desktop = await DesktopAsync();
        Assert.Equal(EditorLaunchResult.Applied,
            await new CollectionEditorLauncherService(desktopDialogs, null, desktop, new AppToastService()).TryOpenAsync(new CollectionEditorLaunchRequest()));
        Assert.Equal(EditorLaunchResult.Applied,
            await new GalleryEditorLauncherService(desktopDialogs, desktop, new AppToastService()).TryOpenAsync(new GalleryEditorLaunchRequest()));
        Assert.Equal(2, desktopSpy.ShowCount);
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("tablet")]
    public async Task CollectionAndGalleryLaunchersRefuseOffTheDesktop(string formFactor)
    {
        var dialogs = DispatchProxy.Create<IAppDialogService, DialogSpy>();
        var spy = (DialogSpy)(object)dialogs;
        var device = await DeviceFor(formFactor);

        Assert.Equal(EditorLaunchResult.NotAvailableOnThisDevice,
            await new CollectionEditorLauncherService(dialogs, null, device, new AppToastService()).TryOpenAsync(new CollectionEditorLaunchRequest()));
        Assert.Equal(EditorLaunchResult.NotAvailableOnThisDevice,
            await new GalleryEditorLauncherService(dialogs, device, new AppToastService()).TryOpenAsync(new GalleryEditorLaunchRequest()));
        Assert.Equal(0, spy.ShowCount);
    }

    [Fact]
    public void TheBrowserSnapshotReportsTheSamePointerSignalsTheServerRuleReads()
    {
        var root = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "wwwroot", "app.js"));
        var layout = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Shared", "MainLayout.razor"));

        Assert.Contains("matchMedia('(pointer: fine)')", script, StringComparison.Ordinal);
        Assert.Contains("matchMedia('(hover: hover)')", script, StringComparison.Ordinal);
        Assert.Contains("finePointer: hasFinePointer(), hover: canHover()", script, StringComparison.Ordinal);
        Assert.Contains("public async Task HandleViewportChanged(DeviceContextSnapshot snapshot)", layout, StringComparison.Ordinal);
    }

    private static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
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
