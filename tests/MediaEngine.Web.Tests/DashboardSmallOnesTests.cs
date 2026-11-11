using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Reading;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Setup;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Shared;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Dashboard remediation packet M8, "small ones": the editor footer's wording, the phone audiobook tools without chapters, the Sleep timer
/// panel's width, the hero's Open button tooltip, the setup Metadata step's wording, and the reader starting its device context.
/// </summary>
public sealed class DashboardSmallOnesTests : AsyncBunitContext
{
    private static readonly string Web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");

    public DashboardSmallOnesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Web, .. parts]));

    // ---- Item 3: the editor footer's words come from the string table ----

    [Theory]
    [InlineData(false, "Save changes")]
    [InlineData(true, "Saving…")]
    public void TheEditorFooterSaveButton_SaysSaveChangesAndWhileSavingSaysSaving_FromTheStringTable(bool saving, string expected)
    {
        var cut = Render<EditorWindowChrome>(parameters => parameters
            .Add(component => component.ShowTabs, false)
            .Add(component => component.ChangeCount, 2)
            .Add(component => component.Saving, saving));

        Assert.Equal(expected, cut.Find("button.tl-editor-window__save").TextContent.Trim());
    }

    [Fact]
    public void TheEditorFooterWording_IsNotTypedIntoTheComponentOrTheDeleteRules()
    {
        var chrome = Read("Components", "Editor", "EditorWindowChrome.razor");
        Assert.DoesNotContain("\"Save changes\"", chrome, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Saving", chrome, StringComparison.Ordinal);
        Assert.DoesNotContain("FooterLabel", Read("Services", "Editing", "EditorDeleteRules.cs"), StringComparison.Ordinal);
        Assert.Contains("L[\"EditorFooter_Delete\"]", Read("Components", "Editor", "EditorPopover.razor"), StringComparison.Ordinal);
    }

    // ---- Items 1 and 2: the editor header, read from the style sheets ----

    [Fact]
    public void TheEditorHeaderDropdowns_HaveNoAccentBorderAtRest_OnlyWhenOpen()
    {
        foreach (var file in new[] { "EditorHeaderNav.razor.css", "ViewEditHeaderNav.razor.css" })
        {
            var css = Read("Components", "Editor", file);
            var rest = Regex.Match(css, @"\.[\w-]+ ::deep \.[\w-]+__trigger \{[^}]*\}").Value;
            Assert.Contains("border: 1px solid var(--tl-border-strong)", rest, StringComparison.Ordinal);
            Assert.DoesNotContain("accent", rest, StringComparison.Ordinal);
            Assert.Matches(@"__trigger\.is-open \{\s*border-color: var\(--tl-accent-primary\);", css);
        }
    }

    // ---- Item 4: the phone audiobook tool row has no stand-in for a missing Chapters tool ----

    [Fact]
    public void OnAPhone_AnAudiobookWithNoChapters_ShowsNoChaptersToolAndHistoryDoesNotStandInForIt()
    {
        var state = new PlaybackBarState(HasChapters: false, CanBookmark: true);

        var tools = PlaybackControlCatalog.BuildPhoneTools(PlaybackBarMedia.Audiobook, state);

        Assert.Equal([PlaybackControlKey.Speed, PlaybackControlKey.SleepTimer, PlaybackControlKey.Bookmarks], tools.Select(tool => tool.Key).ToArray());
    }

    [Fact]
    public void OnAPhone_AnAudiobookWithChapters_StillLeadsItsToolRowWithChapters()
    {
        var state = new PlaybackBarState(HasChapters: true, CanBookmark: true);

        var tools = PlaybackControlCatalog.BuildPhoneTools(PlaybackBarMedia.Audiobook, state);

        Assert.Equal([PlaybackControlKey.Chapters, PlaybackControlKey.Speed, PlaybackControlKey.SleepTimer, PlaybackControlKey.Bookmarks], tools.Select(tool => tool.Key).ToArray());
    }

    // ---- Item 5: the Sleep timer panel is 380 px wide ----

    [Fact]
    public void TheSleepTimerPanel_IsItsOwnPanelKindAt380Pixels()
    {
        Assert.Contains("--playback-panel-sleep-width: 380px;", Read("wwwroot", "tuvima.tokens.css"), StringComparison.Ordinal);
        Assert.Matches(@"\.playback-panel--sleep \{\s*width: var\(--playback-panel-sleep-width\);", Read("Components", "Player", "PlaybackPanel.razor.css"));
        Assert.Contains("Kind=\"sleep\"", Read("Components", "Player", "SleepTimerPanel.razor"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSleepTimerPanel_DrawsWithTheSleepPanelClass()
    {
        var cut = Render<SleepTimerPanel>(parameters => parameters
            .Add(component => component.State, new AudiobookSleepTimerStateDto())
            .Add(component => component.Open, true));

        Assert.NotEmpty(cut.FindAll(".playback-panel--sleep"));
    }

    // ---- Item 6: the hero's Open button does not repeat its label in a tooltip ----

    [Fact]
    public void TheHeroOpenButton_WithItsNameUnderIt_HasNoTooltip()
    {
        var cut = Render<HeroActionRow>(parameters => parameters
            .Add(component => component.FolderHref, "/details/tvshow/folder")
            .Add(component => component.ShowLabels, true));

        var open = cut.Find(".hero-open-folder");
        Assert.Equal("Open folder", open.GetAttribute("aria-label"));
        Assert.Equal("Open", open.Closest(".app-icon-button-stack")!.TextContent.Trim());
        Assert.Null(open.Closest("[data-playback-tooltip]"));
    }

    [Fact]
    public void TheHeroOpenButton_WhenIconOnly_KeepsItsTooltip()
    {
        var cut = Render<HeroActionRow>(parameters => parameters
            .Add(component => component.FolderHref, "/details/tvshow/folder")
            .Add(component => component.ShowLabels, false));

        Assert.Equal("Open folder", cut.Find(".hero-open-folder").Closest("[data-playback-tooltip]")!.GetAttribute("data-playback-tooltip"));
    }

    // ---- Item 9: the setup Metadata step's group titles and chips follow the setup mock ----

    private static ProviderCatalogueDto Source(string name, bool needsKey, bool configured = false) => new()
    {
        DisplayName = name,
        RequiresKey = needsKey,
        Onboarding = needsKey
            ? new ProviderOnboardingDto
            {
                Credentials = [new ProviderCredentialFieldDto { Ownership = "user_supplied", Configured = configured }],
            }
            : null,
    };

    [Fact]
    public void TheMetadataStep_TitlesItsTwoGroupsNeedsAKeyAndReadyNoKeyNeeded()
    {
        var cut = Render<SetupMetadataStage>(parameters => parameters
            .Add(component => component.Catalogue, [Source("Keyed", needsKey: true), Source("Free", needsKey: false)]));

        Assert.Equal(["Needs a key", "Ready, no key needed"], cut.FindAll(".setup-provider-group h3").Select(heading => heading.TextContent.Trim()).ToArray());
    }

    [Fact]
    public void TheMetadataStep_ChipsAndButtonsReadFromTheStringTable()
    {
        var cut = Render<SetupMetadataStage>(parameters => parameters
            .Add(component => component.Catalogue, [Source("Keyed", needsKey: true), Source("Saved", needsKey: true, configured: true), Source("Free", needsKey: false)]));

        var chips = cut.FindAll(".app-status-badge").Select(chip => chip.TextContent.Trim()).ToArray();
        Assert.Contains("Needs a key", chips);
        Assert.Contains("Key saved", chips);
        Assert.Contains("No key needed", chips);
        var buttons = cut.FindAll("button").Select(button => button.TextContent.Trim()).ToList();
        Assert.Contains("Connect", buttons);
        Assert.Contains("Review key", buttons);
        var source = Read("Components", "Setup", "SetupMetadataStage.razor");
        foreach (var typed in new[] { "\"Key saved\"", "\"Needs a key\"", "\"No key needed\"", "\"Connect\"", "\"Review key\"", "\"Optional metadata enrichment.\"" })
        {
            Assert.DoesNotContain(typed, source, StringComparison.Ordinal);
        }
    }

    // ---- Item 10: the reader starts the device context itself, and its phone "..." is horizontal dots ----

    [Fact]
    public void TheReaderLayout_StartsTheDeviceContextFromTheBrowserSoAPhoneReloadShowsThePhoneTools()
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        Services.AddSingleton(device);
        Services.AddSingleton<DashboardSessionAccessor>();
        JSInterop.Setup<DeviceContextSnapshot>("detectDeviceSnapshot").SetResult(new DeviceContextSnapshot("mobile", "phone", true));
        JSInterop.SetupVoid("tuvimaResponsive.registerDeviceClassObserver", _ => true).SetVoidResult();

        Render<ReaderLayout>(parameters => parameters.Add(layout => layout.Body, (RenderFragment)(builder => builder.AddContent(0, "reader"))));

        Assert.True(device.IsInitialised);
        Assert.True(device.IsPhone);
    }

    [Fact]
    public async Task TheReaderLayout_RegistersTheBrowsersDeviceObserverAndUnregistersItWhenTheReaderCloses()
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(new DeviceContextSnapshot("desktop", "desktop", false));
        Services.AddSingleton(device);
        Services.AddSingleton<DashboardSessionAccessor>();
        JSInterop.SetupVoid("tuvimaResponsive.registerDeviceClassObserver", _ => true).SetVoidResult();
        JSInterop.SetupVoid("tuvimaResponsive.unregisterDeviceClassObserver", _ => true).SetVoidResult();

        var cut = Render<ReaderLayout>(parameters => parameters.Add(layout => layout.Body, (RenderFragment)(builder => builder.AddContent(0, "reader"))));

        var registration = Assert.Single(JSInterop.Invocations, call => call.Identifier == "tuvimaResponsive.registerDeviceClassObserver");
        var owner = Assert.IsType<string>(registration.Arguments[1]);
        Assert.Equal(true, registration.Arguments[2]);

        await cut.Instance.DisposeAsync();

        var removal = Assert.Single(JSInterop.Invocations, call => call.Identifier == "tuvimaResponsive.unregisterDeviceClassObserver");
        Assert.Equal(owner, removal.Arguments[0]);
    }

    [Fact]
    public async Task TheReaderLayout_LeavesADeviceContextTheMainLayoutAlreadyStartedAlone()
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(new DeviceContextSnapshot("desktop", "desktop", false));
        Services.AddSingleton(device);
        Services.AddSingleton<DashboardSessionAccessor>();
        JSInterop.Setup<DeviceContextSnapshot>("detectDeviceSnapshot").SetResult(new DeviceContextSnapshot("mobile", "phone", true));
        JSInterop.SetupVoid("tuvimaResponsive.registerDeviceClassObserver", _ => true).SetVoidResult();

        Render<ReaderLayout>(parameters => parameters.Add(layout => layout.Body, (RenderFragment)(builder => builder.AddContent(0, "reader"))));

        Assert.False(device.IsPhone);
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "detectDeviceSnapshot");
    }

    [Fact]
    public async Task OnAPhone_TheReadersMoreButtonDrawsHorizontalDotsAsAPlainReaderIconButton()
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        Services.AddSingleton(device);

        var cut = Render<AppRowMenu>(parameters => parameters
            .Add(component => component.Class, "rf-ib rf-more")
            .Add(component => component.Plain, true)
            .Add(component => component.Icon, AppMaterialIcons.Outlined.MoreHoriz)
            .Add(component => component.AriaLabel, "More actions"));

        var trigger = cut.Find("button.rf-more");
        Assert.Contains("rf-ib", trigger.ClassList);
        Assert.DoesNotContain("app-overflow-menu__trigger", trigger.ClassList);
        var horizontal = Render<AppMaterialIcon>(parameters => parameters.Add(icon => icon.Icon, AppMaterialIcons.Outlined.MoreHoriz).Add(icon => icon.Size, AppIconSize.Control20));
        Assert.Equal(horizontal.Find("svg").InnerHtml, trigger.QuerySelector("svg")!.InnerHtml);
    }

    [Fact]
    public async Task OnAPhone_AnotherRowMenuKeepsItsBoxedVerticalDots()
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        Services.AddSingleton(device);

        var cut = Render<AppRowMenu>(parameters => parameters.Add(component => component.AriaLabel, "More actions"));

        Assert.Contains("app-overflow-menu__trigger--icon", cut.Find("button").ClassList);
        var vertical = Render<AppMaterialIcon>(parameters => parameters.Add(icon => icon.Icon, AppMaterialIcons.Outlined.MoreVert));
        Assert.Equal(vertical.Find("svg").InnerHtml, cut.Find("button svg").InnerHtml);
    }

    [Fact]
    public void TheReaderFramesMoreButton_AsksForPlainHorizontalDots()
    {
        Assert.Contains("Class=\"rf-ib rf-more\" Plain=\"true\" Icon=\"@AppMaterialIcons.Outlined.MoreHoriz\"", Read("Components", "Reader", "ReaderFrame.razor"), StringComparison.Ordinal);
    }

    // ---- Rules files: no older hero size lines ----

    [Fact]
    public void TheRulesFiles_SayWhatIsBuiltForTheHeroButtonAndHeight_AndNoToastsOrRemovedBatchParts()
    {
        var root = FindRepoRoot();
        var rules = File.ReadAllText(Path.Combine(root, ".claude", "rules", "dashboard-surfaces-home-player.md"));
        Assert.DoesNotContain("22rem", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("4.6rem", rules, StringComparison.Ordinal);
        Assert.Contains("56 px tall", rules, StringComparison.Ordinal);
        Assert.Contains("70 percent of the viewport height", rules, StringComparison.Ordinal);

        var web = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "CLAUDE.md"));
        foreach (var gone in new[] { "DetailBatchPanel", "DetailBatchSection", "DetailBatchMoveFlow" })
        {
            Assert.DoesNotContain(gone, web, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "src"), gone + "*", SearchOption.AllDirectories));
        }

        Assert.Contains("No toasts: see the feedback rule in engineering/design/design-system.md", web, StringComparison.Ordinal);
    }
}
