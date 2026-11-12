using System.Reflection;
using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Two device rules (universal editor v6 D19). Item editing (the editor, everything that opens it, Delete and collection membership) works on a
/// desktop, tablet and phone; a TV, an unknown form factor and a missing device context mean "no". The desktop-only tools in Settings and the
/// Vault keep the old rule: a desktop form factor AND a fine, hover-capable primary pointer. Every launcher refuses on its own rather than
/// relying on hidden buttons.
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

    // deviceClass, formFactor, touch, finePointer, hover, expected
    public static TheoryData<string, string, bool, bool, bool, bool> ItemMatrix => new()
    {
        { "web", "desktop", false, true, true, true },          // desktop with a mouse
        { "web", "desktop", true, false, false, true },         // desktop site requested on an iPad: still allowed, any pointer
        { "web", "desktop", false, false, false, true },        // the browser reported neither pointer signal
        { "web", "tablet", true, false, false, true },          // tablet
        { "web", "tablet", false, true, true, true },           // a tablet with a mouse
        { "mobile", "phone", true, false, false, true },        // phone
        { "mobile", "phone", false, true, true, true },         // phone with a fine pointer
        { "television", "tv", false, true, true, false },       // TV
        { "web", "tv", false, true, true, false },              // TV form factor reported by the browser
        { "television", "desktop", false, true, true, false },  // a television device class is a TV whatever else it reports
        { "native", "native", false, true, true, false },       // a native app shell naming itself
        { "web", "native", false, true, true, false },          // unknown form factor on the web class
        { "web", "", false, false, false, false },              // nothing reported (the pure rule never guesses)
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
    public async Task DesktopOnlyTools_NeedADesktopWithAFineHoverPointer(
        string deviceClass, string formFactor, bool touch, bool finePointer, bool hover, bool expected)
    {
        Assert.Equal(expected, EditorDeviceRule.CanEdit(deviceClass, formFactor, finePointer, hover));

        var device = await DeviceAsync(new DeviceContextSnapshot(deviceClass, formFactor, touch, finePointer, hover));

        Assert.Equal(expected, EditorDeviceRule.CanEdit(device));
        Assert.Equal(expected, device.CanEdit);
    }

    [Theory]
    [MemberData(nameof(ItemMatrix))]
    public async Task ItemEditing_IsAllowedOnDesktopTabletAndPhone_AndRefusedOnATvOrAnUnknownDevice(
        string deviceClass, string formFactor, bool touch, bool finePointer, bool hover, bool expected)
    {
        Assert.Equal(expected, EditorDeviceRule.CanEditItems(deviceClass, formFactor));

        // Through the device context the browser snapshot fills in. An unreported form factor on the web class is read as a desktop by the
        // service itself, so that one row only exercises the pure rule above.
        if (formFactor.Length > 0)
        {
            var device = await DeviceAsync(new DeviceContextSnapshot(deviceClass, formFactor, touch, finePointer, hover));

            Assert.Equal(expected, EditorDeviceRule.CanEditItems(device));
            Assert.Equal(expected, device.CanEditItems);
            Assert.Equal(!expected, EditorDeviceRule.TryRefuseItems(device));
        }
    }

    [Fact]
    public void ItemEditing_IsRefusedWithoutADeviceContextOrBeforeTheBrowserHasReportedOne()
    {
        Assert.False(EditorDeviceRule.CanEditItems((DeviceContextService?)null));
        Assert.True(EditorDeviceRule.TryRefuseItems(null));
        Assert.False(EditorDeviceRule.CanEditItems(new DeviceContextService(EngineApiClientStub.CreateDefault())));
    }

    [Fact]
    public async Task ItemEditing_SurvivesARotateOrResizeAcrossTheTabletWidth_ButNotAChangeToATv()
    {
        var device = await DesktopAsync();
        Assert.True(device.CanEditItems);

        await device.SwitchDeviceAsync(new DeviceContextSnapshot("web", "tablet", true));
        Assert.True(device.CanEditItems);
        await device.SwitchDeviceAsync(new DeviceContextSnapshot("mobile", "phone", true));
        Assert.True(device.CanEditItems);
        await device.SwitchDeviceAsync(new DeviceContextSnapshot("television", "tv", false));
        Assert.False(device.CanEditItems);
    }

    [Fact]
    public async Task ABrowserThatReportsNoPointerSignalsCanNeverUseADesktopOnlyTool()
    {
        // The older three-argument snapshot (no pointer information) is "undetectable", which means no.
        var device = await DeviceAsync(new DeviceContextSnapshot("web", "desktop", false));

        Assert.False(device.CanEdit);
        Assert.False(EditorDeviceRule.CanEdit((DeviceContextService?)null));
        Assert.False(new DeviceContextService(EngineApiClientStub.CreateDefault()).CanEdit);
    }

    [Fact]
    public async Task APointerChangeAloneRevokesADesktopOnlyTool_ButNotItemEditing()
    {
        var device = await DesktopAsync();
        var changes = 0;
        device.OnChanged += () => changes++;
        Assert.True(device.CanEdit);

        await device.SwitchDeviceAsync(new DeviceContextSnapshot("web", "desktop", true, FinePointer: false, Hover: false));

        Assert.False(device.CanEdit);
        Assert.True(device.CanEditItems);
        Assert.True(changes >= 1);
    }

    [Fact]
    public async Task EveryLauncherOpensOnADesktopWithAMouse()
    {
        var desktopDialogs = DispatchProxy.Create<IAppDialogService, DialogSpy>();
        var desktopSpy = (DialogSpy)(object)desktopDialogs;
        var desktop = await DesktopAsync();
        Assert.Equal(EditorLaunchResult.Applied,
            await new CollectionEditorLauncherService(desktopDialogs, null, desktop).TryOpenAsync(new CollectionEditorLaunchRequest()));
        // The gallery launcher opens the one View editor window; it answers when that window closes.
        var galleryHost = new ViewEditorHost(null!, desktop, new ViewWorkspaceService(null!), NullLogger<ViewEditorHost>.Instance);
        var opening = new GalleryEditorLauncherService(galleryHost, desktop).TryOpenAsync(new GalleryEditorLaunchRequest());
        Assert.True(galleryHost.IsOpen);
        galleryHost.Close();
        Assert.Equal(EditorLaunchResult.Dismissed, await opening);
        Assert.Equal(1, desktopSpy.ShowCount);
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("tablet")]
    public async Task EveryLauncherOpensOnAPhoneAndATablet(string formFactor)
    {
        var dialogs = DispatchProxy.Create<IAppDialogService, DialogSpy>();
        var spy = (DialogSpy)(object)dialogs;
        var device = await DeviceFor(formFactor);

        Assert.Equal(EditorLaunchResult.Applied,
            await new CollectionEditorLauncherService(dialogs, null, device).TryOpenAsync(new CollectionEditorLaunchRequest()));
        var galleryHost = new ViewEditorHost(null!, device, new ViewWorkspaceService(null!), NullLogger<ViewEditorHost>.Instance);
        var opening = new GalleryEditorLauncherService(galleryHost, device).TryOpenAsync(new GalleryEditorLaunchRequest());
        Assert.True(galleryHost.IsOpen);
        galleryHost.Close();
        Assert.Equal(EditorLaunchResult.Dismissed, await opening);
        Assert.Equal(1, spy.ShowCount);
    }

    [Fact]
    public async Task CollectionAndGalleryLaunchersRefuseOnATv()
    {
        var dialogs = DispatchProxy.Create<IAppDialogService, DialogSpy>();
        var spy = (DialogSpy)(object)dialogs;
        var device = await DeviceFor("tv");

        Assert.Equal(EditorLaunchResult.NotAvailableOnThisDevice,
            await new CollectionEditorLauncherService(dialogs, null, device).TryOpenAsync(new CollectionEditorLaunchRequest()));
        Assert.Equal(EditorLaunchResult.NotAvailableOnThisDevice,
            await new GalleryEditorLauncherService(new ViewEditorHost(null!, device, new ViewWorkspaceService(null!), NullLogger<ViewEditorHost>.Instance), device).TryOpenAsync(new GalleryEditorLaunchRequest()));
        Assert.Equal(0, spy.ShowCount);
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("tablet")]
    public async Task AddToCollectionOnAPhoneOrTablet_OpensTheMembershipDialog(string formFactor)
    {
        var dialogs = DispatchProxy.Create<IAppDialogService, DialogSpy>();
        var spy = (DialogSpy)(object)dialogs;
        var services = new ServiceCollection()
            .AddSingleton(dialogs)
            .AddSingleton(await DeviceFor(formFactor))
            .BuildServiceProvider();

        // The dialog spy answers with a reference whose result is a plain "ok"; the point here is that the device rule let the flow through.
        await CollectionMembershipFlow.RunAsync(services, [new CollectionEditorSeedItem(Guid.NewGuid(), "Dune", "Movies")], "Add to collection", "Dune");

        Assert.Equal(1, spy.ShowCount);
    }

    [Fact]
    public async Task AddToCollectionOnATv_OpensNothingAndReturnsTheDeviceMessageForThePageToShowInPlace()
    {
        var dialogs = DispatchProxy.Create<IAppDialogService, DialogSpy>();
        var spy = (DialogSpy)(object)dialogs;
        var services = new ServiceCollection()
            .AddSingleton(dialogs)
            .AddSingleton(await DeviceFor("tv"))
            .BuildServiceProvider();

        var notice = await CollectionMembershipFlow.RunAsync(services, [new CollectionEditorSeedItem(Guid.NewGuid(), "Dune", "Movies")], "Add to collection", "Dune");

        Assert.Equal(EditorDeviceRule.Message, notice);
        Assert.Equal(0, spy.ShowCount);
    }

    // The desktop-only rule survives only for the owners the spec names (Settings tools, the Vault, document auto-filing rules). Every other caller
    // asks the item-editing rule, so a new editing surface cannot quietly bring the desktop-only rule back, and the Settings and Vault tools cannot
    // quietly lose it. A phone reaches these tools' "Edit on a computer" line because they call EditorDeviceRule.CanEdit / TryRefuse.
    [Fact]
    public void OnlySettingsVaultAndDocumentRulesKeepTheDesktopOnlyRule_EveryOtherCallerUsesTheItemRule()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        string[] desktopOnly =
        [
            "Components/Settings/PluginSettingsTab.razor",
            "Components/Settings/Plugins/PluginSetupPage.razor",
            "Components/Settings/Overview/OverviewImportPage.razor",
            "Components/Settings/IngestionFlowEditor.razor",
            "Components/Vault/VaultUnmatchedList.razor",
            "Components/Vault/VaultIdentifyPanel.razor",
            "Components/Vault/VaultGalleryTools.razor",
            "Components/Pages/ViewDocumentRulesPage.razor",
            "Services/Theming/DeviceContextService.cs",
        ];
        var desktopRule = new System.Text.RegularExpressions.Regex(@"EditorDeviceRule\.(CanEdit|TryRefuse)\(|\b(Device|DeviceContext|_device)\??\.CanEdit\b");

        var found = Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(path => (path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".razor", StringComparison.Ordinal))
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => desktopRule.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(web, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(desktopOnly.Order(StringComparer.Ordinal), found);
    }

    [Fact]
    public void TheDesktopOnlyOwnersStillSayEditOnAComputer()
    {
        Assert.Equal("Edit on a computer", EditorDeviceRule.Message);
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        foreach (var file in new[]
        {
            "Components/Settings/Plugins/PluginSetupPage.razor",
            "Components/Settings/Overview/OverviewImportPage.razor",
            "Components/Settings/IngestionFlowEditor.razor",
            "Components/Vault/VaultGalleryTools.razor",
        })
        {
            var text = File.ReadAllText(Path.Combine(web, file.Replace('/', Path.DirectorySeparatorChar)));
            Assert.DoesNotContain("CanEditItems", text, StringComparison.Ordinal);
            Assert.DoesNotContain("TryRefuseItems", text, StringComparison.Ordinal);
        }
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
