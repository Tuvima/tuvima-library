using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.View;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Settings;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// View folders match their mock (spec view-folders-in-libraries, mocks C, D, G and H): the page notice and its Managed button, the Add and Edit windows, the Folders filter
// menu, and the View page's Folders tab opening on Read-only.
public sealed class ViewFolderMockTests : AsyncBunitContext
{
    private readonly List<ViewSourceAdminDto> _shared = [];
    private readonly IRenderedComponent<AppDialogHost> _dialogs;
    private int _folderAdded;

    private static readonly LibrariesConfigurationDto Configuration = new()
    {
        StorageLocations = [new() { Id = "media", Label = "Library data", Path = "C:\\data", AllowWrite = true }],
        ViewStorage = new ViewStorageDto { StorageLocationId = "media", RelativeRoot = "View" },
    };

    private static ViewSourceAdminDto Folder(string name, string mode, string location, bool offline = false, int skipped = 0, string kind = "photos") =>
        new(Guid.NewGuid(), "folder", name, mode, location, true, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ContentKind: kind, SkippedFileCount: skipped, FolderOffline: offline);

    public ViewFolderMockTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler("get_LastError", _ => null);
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(Configuration));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryKindsAsync), _ => Task.FromResult<IReadOnlyList<LibraryKindDto>?>(LibraryKindsTestData.BuiltIn()));
            stub.SetHandler(nameof(IEngineApiClient.GetViewSharedSourcesAsync), _ => Task.FromResult<IReadOnlyList<ViewSourceAdminDto>?>(_shared));
            stub.SetHandler(nameof(IEngineApiClient.GetServerFolderRootsAsync), _ => Task.FromResult<IReadOnlyList<ServerStorageLocationDto>>(Configuration.StorageLocations));
            stub.SetHandler(nameof(IEngineApiClient.BrowseServerFoldersAsync), _ => Task.FromResult<BrowseServerFoldersResultDto?>(new() { DisplayPath = "E:\\Pictures" }));
        }));
        Services.AddScoped(_ => TestSessions.For(kids: false, administrator: true));
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<LibraryKindsState>();
        _dialogs = Render<AppDialogHost>();
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private IRenderedComponent<ViewUploadsNotice> RenderNotice(string kind = "photos", string noun = "photos and videos") =>
        Render<ViewUploadsNotice>(parameters => parameters
            .Add(x => x.Kind, kind)
            .Add(x => x.Noun, noun)
            .Add(x => x.OnFolderAdded, EventCallback.Factory.Create(this, () => _folderAdded++)));

    // ----- Mock H: the page notice -----------------------------------------------------------------------------------------------

    [Fact]
    public void ThePageNoticeSaysUploadsNeedAManagedFolderAndKeepsTheDropZoneOnThePageSwitchedOff()
    {
        var cut = RenderNotice();

        Assert.Equal("Uploads and phone backups need a Managed folder. Your Read-only folders are never changed.", Text(cut.Find(".view-uploads-notice__line")));
        var drop = cut.Find("[data-drop-off]");
        Assert.Equal("true", drop.GetAttribute("aria-disabled"));
        Assert.Contains("photos and videos", Text(drop), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageNoticeUsesTheWarningEdgeNotANeutralBorder()
    {
        var css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components", "View", "ViewUploadsNotice.razor.css"));

        Assert.Contains("var(--tl-status-warning-border)", css, StringComparison.Ordinal);
        Assert.Contains("background: var(--tl-status-warning-soft)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAManagedFolderOpensTheAddFolderWindowWithManagedAlreadyChosen()
    {
        var cut = RenderNotice();

        // The click handler waits for the window to close, so it is not awaited here.
        _ = cut.Find("[data-add-managed]").ClickAsync(new());

        _dialogs.WaitForElement(".add-view-folder");
        Assert.Equal("Add folder", Text(_dialogs.FindAll(".app-dialog-shell__title").Last()));
        _dialogs.WaitForAssertion(() => Assert.NotNull(_dialogs.Find("[data-managed-note]")));
        var chosen = _dialogs.FindAll("[data-folder-handling] .app-choice-row.is-selected");
        var row = Assert.Single(chosen);
        Assert.StartsWith("Managed", Text(row), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAManagedFolderIsNotOfferedOnAPhoneApp()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync("mobile");

        var cut = RenderNotice();

        Assert.Empty(cut.FindAll("[data-add-managed]"));
        Assert.NotEmpty(cut.FindAll(".view-uploads-notice__line"));
    }

    [Fact]
    public void AddAManagedFolderIsNotOfferedToSomeoneWhoIsNotAnAdministrator()
    {
        var session = Services.GetRequiredService<DashboardSessionAccessor>();
        var nonAdministrator = TestSessions.For(kids: false, administrator: false);
        session.Set("token", TestSessions.Account, TestSessions.Profile, Guid.NewGuid(), nonAdministrator.Authority);

        var cut = RenderNotice();

        Assert.Empty(cut.FindAll("[data-add-managed]"));
        Assert.NotEmpty(cut.FindAll(".view-uploads-notice__line"));
    }

    // ----- Mock C: the Add window ------------------------------------------------------------------------------------------------

    private async Task OpenAddWindowAsync(string kindKey, IReadOnlyList<ViewSourceAdminDto>? existing = null)
    {
        var kind = LibraryKindsTestData.Catalog.FindViewKind(kindKey)!;
        var parameters = new AppDialogParameters<AddViewFolderDialog>
        {
            { component => component.Kind, kind },
            { component => component.Configuration, Configuration },
            { component => component.ExistingFolders, existing },
        };
        await Services.GetRequiredService<IAppDialogService>().ShowAsync<AddViewFolderDialog>(string.Empty, parameters, AddViewFolderDialog.DialogOptions);
        _dialogs.WaitForElement(".add-view-folder");
    }

    [Fact]
    public async Task TheAddWindowHeaderNamesTheAreaAndKindAboveTheTitleAddFolder()
    {
        await OpenAddWindowAsync("photos");

        Assert.Equal("View \u00b7 Photos and Videos", Text(_dialogs.FindAll(".app-dialog-shell__eyebrow").Last()));
        Assert.Equal("Add folder", Text(_dialogs.FindAll(".app-dialog-shell__title").Last()));
        Assert.Empty(_dialogs.FindAll(".app-dialog-shell__subtitle"));
    }

    [Fact]
    public async Task TheAddWindowHeaderNamesDocumentsForTheDocumentsKind()
    {
        await OpenAddWindowAsync("documents");

        Assert.Equal("View \u00b7 Documents", Text(_dialogs.FindAll(".app-dialog-shell__eyebrow").Last()));
    }

    [Fact]
    public async Task TheAddWindowDrawsNoGreyReadOnlyLineBecauseTheReadOnlyChoiceAlreadySaysIt()
    {
        await OpenAddWindowAsync("photos");

        var footer = _dialogs.FindAll(".app-dialog-shell__footer").Last();
        Assert.Empty(_dialogs.FindAll("[data-footer-line]"));
        Assert.Empty(_dialogs.FindAll("[data-picker-safety]"));
        Assert.DoesNotContain("never", Text(footer), StringComparison.Ordinal);
        Assert.Contains("Your files are never changed.", Text(_dialogs.Find("[data-folder-handling]")), StringComparison.Ordinal);
        Assert.DoesNotContain("Library scans this folder", Text(_dialogs.Find(".add-view-folder")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAddWindowHasExactlyOneMessageLineUnderTheHandlingChoiceAndNoReservedSpace()
    {
        await OpenAddWindowAsync("photos");

        // Read-only on a kind with no Managed folder: the one line is the upload line, directly after the choice.
        var message = Assert.Single(_dialogs.FindAll("[data-picker-message]"));
        Assert.Equal("You can't upload photos and videos until you add a Managed folder.", Text(message));
        Assert.True(_dialogs.Find("[data-folder-handling]").NextElementSibling!.HasAttribute("data-picker-message"));
        Assert.Empty(_dialogs.FindAll(".server-folder-picker__notices"));

        // Managed: the Managed note replaces it; there is still only one line.
        _dialogs.Find("[data-folder-handling]").QuerySelectorAll("button").Single(button => Text(button).StartsWith("Managed", StringComparison.Ordinal)).Click();
        _dialogs.WaitForAssertion(() => Assert.NotNull(_dialogs.Find("[data-managed-note]")));
        Assert.Single(_dialogs.FindAll("[data-picker-message]"));
        Assert.Empty(_dialogs.FindAll("[data-uploads-note]"));
    }

    [Fact]
    public async Task TheAddWindowHasNoMessageLineAtAllWhenThereIsNothingToSay()
    {
        _shared.Add(Folder("Holidays", "managed", "D:\\Pictures\\Holidays"));
        await OpenAddWindowAsync("photos", existing: [.. _shared]);

        Assert.Empty(_dialogs.FindAll("[data-picker-message]"));
    }

    // ----- Mock D: the Edit window -----------------------------------------------------------------------------------------------

    private async Task OpenEditWindowAsync()
    {
        var kind = LibraryKindsTestData.Catalog.FindViewKind("photos")!;
        var parameters = new AppDialogParameters<ViewKindEditDialog>
        {
            { component => component.Kind, kind },
            { component => component.Configuration, Configuration },
            { component => component.SetupSession, "setup" },
        };
        await Services.GetRequiredService<IAppDialogService>().ShowAsync<ViewKindEditDialog>(string.Empty, parameters, ViewKindEditDialog.DialogOptions);
        _dialogs.WaitForElement("[data-folder-rows] [data-source]");
    }

    [Fact]
    public void TheEditWindowIsWindowMNotLarge()
    {
        Assert.Equal(AppWindowSize.Medium, ViewKindEditDialog.DialogOptions.Size);
        Assert.Equal(AppWindowSize.Medium, AppWindowSizing.Resolve(ViewKindEditDialog.DialogOptions, "Medium"));
    }

    [Fact]
    public async Task EachFolderBlockHasOneTopLineWithTheDotThePathTheTagAndRemoveWithTheFriendlyNameBelowIt()
    {
        _shared.Add(Folder("Camera roll originals", "linked", "\\\\nas\\photos\\camera-roll", skipped: 12));
        _shared.Add(Folder("Holidays", "managed", "D:\\Pictures\\Holidays"));
        await OpenEditWindowAsync();

        var block = _dialogs.Find("[data-source]");
        var top = block.QuerySelector("[data-folder-top]")!;
        Assert.NotNull(top.QuerySelector(".lib-edit__dot[role='img']"));
        Assert.Equal("\\\\nas\\photos\\camera-roll", Text(top.QuerySelector(".lib-edit__path")!));
        Assert.Equal("Read-only", Text(top.QuerySelector(".lib-edit__tag")!));
        Assert.NotNull(top.QuerySelector("[data-remove]"));
        // The friendly name is outside the top line, after it, and the old folder icon is gone.
        Assert.NotNull(block.QuerySelector(":scope > .app-text-field, :scope > div:not([data-folder-top]) input"));
        Assert.Null(top.QuerySelector("input"));
        Assert.Empty(block.QuerySelectorAll(":scope > .app-material-icon"));
        Assert.Contains("12 files skipped", Text(block), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOnlineFolderShowsNoOnlineWordAndAnOfflineOneNamesItself()
    {
        _shared.Add(Folder("Up", "linked", "E:\\Up"));
        _shared.Add(Folder("Away", "managed", "E:\\Away", offline: true));
        await OpenEditWindowAsync();

        var blocks = _dialogs.FindAll("[data-source]");
        var online = blocks.Single(block => Text(block).Contains("E:\\Up", StringComparison.Ordinal));
        var offline = blocks.Single(block => Text(block).Contains("E:\\Away", StringComparison.Ordinal));
        Assert.DoesNotContain("Online", Text(online), StringComparison.Ordinal);
        Assert.Equal("Online", online.QuerySelector(".lib-edit__dot")!.GetAttribute("aria-label"));
        Assert.Equal("Folder offline", Text(offline.QuerySelector(".lib-edit__state")!));
        Assert.Equal("Folder offline", offline.QuerySelector(".lib-edit__dot")!.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task TheEditWindowFooterCarriesTheNeverDeletesLineAtTheLeftAndTheBodyDoesNot()
    {
        _shared.Add(Folder("Pictures", "linked", "E:\\Pictures"));
        await OpenEditWindowAsync();

        var footer = _dialogs.FindAll(".app-dialog-shell__footer").Last();
        Assert.Equal("Removing a folder never deletes files.", Text(footer.QuerySelector("[data-footer-line]")!));
        Assert.DoesNotContain("Removing a folder never deletes files.", Text(_dialogs.Find("[data-view-kind-edit]")), StringComparison.Ordinal);
    }

    // ----- Mock G: the Folders filter --------------------------------------------------------------------------------------------

    private IRenderedComponent<ViewFolderFilter> RenderFilter(IReadOnlyList<ViewSourceAdminDto> folders, Guid? selected = null, Action<Guid?>? changed = null) =>
        Render<ViewFolderFilter>(parameters => parameters
            .Add(x => x.Folders, folders)
            .Add(x => x.Kind, "photos")
            .Add(x => x.Selected, selected)
            .Add(x => x.SelectedChanged, EventCallback.Factory.Create<Guid?>(this, value => changed?.Invoke(value))));

    [Fact]
    public void TheFoldersFilterIsAMenuButtonWhoseRowsCarryTheHandlingTagAndAllFoldersComesFirst()
    {
        var holidays = Folder("Holidays", "managed", "D:\\Pictures\\Holidays");
        var camera = Folder("Camera roll originals", "linked", "E:\\Camera");
        var cut = RenderFilter([holidays, camera], selected: holidays.Id);

        Assert.Empty(cut.FindAll("select"));
        var trigger = cut.Find(".app-overflow-menu__trigger");
        Assert.Equal("Folders: Holidays", Text(trigger));
        trigger.Click();

        var rows = cut.FindAll("[role='menuitemcheckbox']");
        Assert.Equal(3, rows.Count);
        Assert.StartsWith("All folders", Text(rows[0]), StringComparison.Ordinal);
        Assert.Equal("false", rows[0].GetAttribute("aria-checked"));
        Assert.Equal("true", rows[1].GetAttribute("aria-checked"));
        Assert.Equal("Holidays Managed", Text(rows[1]).Replace("check", string.Empty, StringComparison.OrdinalIgnoreCase).Trim());
        Assert.Contains("Read-only", Text(rows[2]), StringComparison.Ordinal);
    }

    [Fact]
    public void ChoosingAFolderFromTheMenuNarrowsToItAndChoosingAllFoldersClearsIt()
    {
        var holidays = Folder("Holidays", "managed", "D:\\Pictures\\Holidays");
        var camera = Folder("Camera roll originals", "linked", "E:\\Camera");
        var picked = new List<Guid?>();
        var cut = RenderFilter([holidays, camera], changed: picked.Add);

        Assert.Equal("Folders", Text(cut.Find(".app-overflow-menu__trigger")));
        cut.Find(".app-overflow-menu__trigger").Click();
        cut.FindAll("[role='menuitemcheckbox']")[2].Click();
        cut.WaitForAssertion(() => Assert.Equal([camera.Id], picked.ToArray()));

        cut.Find(".app-overflow-menu__trigger").Click();
        cut.FindAll("[role='menuitemcheckbox']")[0].Click();
        cut.WaitForAssertion(() => Assert.Equal([camera.Id, null], picked.ToArray()));
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations.ToArray(), call => call.Identifier == "localStorage.setItem"));
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations.ToArray(), call => call.Identifier == "localStorage.removeItem"));
    }

    [Fact]
    public void TheFoldersFilterIsNotShownForOneFolder()
    {
        var cut = RenderFilter([Folder("Pictures", "linked", "E:\\Pictures")]);

        Assert.Empty(cut.FindAll("[data-folder-filter]"));
    }

    // ----- The View page's Folders tab -------------------------------------------------------------------------------------------

    [Fact]
    public void TheFoldersTabOpensTheAddFormOnReadOnlyAndNeverCallsManagedRecommended()
    {
        var profile = new ProfileViewModel(Guid.NewGuid(), "Sam", "#C9922E", "Administrator", DateTimeOffset.UtcNow);
        var cut = Render<ViewFoldersTab>(parameters => parameters
            .Add(x => x.Configuration, Configuration)
            .Add(x => x.Profiles, [profile])
            .Add(x => x.Adding, true));

        Assert.DoesNotContain("recommended", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Choose existing folder", cut.Markup, StringComparison.Ordinal);
    }
}
