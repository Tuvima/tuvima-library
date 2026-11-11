using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class ServerFolderPickerBehaviourTests : AsyncBunitContext
{
    private static readonly ServerStorageLocationDto WindowsDrive = new() { Id = "drive-c", Label = "C:", Path = "C:\\srv", AllowWrite = true };
    private static readonly ServerStorageLocationDto ContainerRoot = new() { Id = "server-root", Label = "This server", Path = "/media", AllowWrite = true };
    private static readonly ServerStorageLocationDto WholeDrive = new() { Id = "drive-c", Label = "C:", Path = "C:\\", AllowWrite = true };
    private static readonly ServerStorageLocationDto SlashRoot = new() { Id = "server-root", Label = "This server", Path = "/", AllowWrite = true };
    private static readonly ServerStorageLocationDto ConfiguredMovies = new() { Id = "media-movies", Label = "Movies library", Path = "C:\\srv\\Movies", AllowWrite = true };

    private ServerStorageLocationDto _root = WindowsDrive;
    private char _separator = '\\';
    private bool _hasRead = true;
    private bool _hasWrite = true;
    private readonly List<string> _browsed = [];
    private bool _rootsDown;
    private bool _missingBrowse;
    private bool _rootsEmpty;
    private int _rootsCalls;
    private int _validateCalls;
    private string? _issueCode;

    public ServerFolderPickerBehaviourTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetServerFolderRootsAsync), _ =>
            {
                _rootsCalls++;
                IReadOnlyList<ServerStorageLocationDto>? roots = _rootsDown
                    ? null
                    : _rootsEmpty
                        ? []
                        : [_root];
                return Task.FromResult(roots);
            });
            stub.SetHandler(nameof(IEngineApiClient.BrowseServerFoldersAsync), args =>
            {
                var request = (BrowseServerFoldersRequest)args![0]!;
                var relative = request.RelativePath ?? string.Empty;
                _browsed.Add(relative);
                // A folder that does not exist yet cannot be listed.
                return Task.FromResult<BrowseServerFoldersResultDto?>(_missingBrowse && relative.Contains("Not", StringComparison.Ordinal) ? null : Browse(relative));
            });
            stub.SetHandler(nameof(IEngineApiClient.ValidateServerFolderAsync), args =>
            {
                _validateCalls++;
                var request = (ValidateServerFolderRequest)args![0]!;
                if (request.ManualPath is { } typed)
                {
                    // The Engine refuses a path outside what it allows with no result, and reports a missing folder with a code.
                    if (typed.Contains("outside", StringComparison.Ordinal))
                    {
                        return Task.FromResult<ServerFolderValidationResultDto?>(null);
                    }

                    if (typed.Contains("missing", StringComparison.Ordinal))
                    {
                        return Task.FromResult<ServerFolderValidationResultDto?>(new()
                        {
                            StorageLocationId = _root.Id,
                            Issues = [new() { Code = "not_found", Message = "Tuvima cannot access this folder inside an approved server storage location." }],
                        });
                    }

                    if (typed.Contains("locked", StringComparison.Ordinal))
                    {
                        return Task.FromResult<ServerFolderValidationResultDto?>(new()
                        {
                            StorageLocationId = _root.Id,
                            Path = typed,
                            Exists = true,
                            HasRead = false,
                            CanSelect = false,
                            Issues = [new() { Code = "read_required", Message = "Tuvima cannot read this folder from the server." }],
                        });
                    }
                }

                var rootPrefix = _root.Path.TrimEnd(_separator);
                var relative = request.ManualPath is { } manual
                    ? (manual.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ? manual[rootPrefix.Length..] : manual).Trim(_separator)
                    : request.RelativePath ?? string.Empty;
                return Task.FromResult<ServerFolderValidationResultDto?>(new()
                {
                    StorageLocationId = _root.Id,
                    RelativePath = relative,
                    Path = PathOf(relative),
                    Exists = true,
                    HasRead = _hasRead,
                    HasWrite = _hasWrite,
                    AvailableBytes = 5_000_000_000,
                    FileSystem = "NTFS",
                    CanSelect = _hasRead,
                    Issues = _issueCode is null ? [] : [new() { Code = _issueCode, Message = "Another library already uses this folder." }],
                });
            });
        }));
        Services.AddScoped<DeviceContextService>();
        Render<AppPopoverHost>();
    }

    private string PathOf(string relative) => relative.Length == 0 ? _root.Path : $"{_root.Path.TrimEnd(_separator)}{_separator}{relative}";

    private BrowseServerFoldersResultDto Browse(string relative)
    {
        string Child(string name) => relative.Length == 0 ? name : $"{relative}{_separator}{name}";
        return new()
        {
            StorageLocation = _root,
            RelativePath = relative,
            ParentRelativePath = relative.Length == 0 ? null : relative.Contains(_separator) ? relative[..relative.LastIndexOf(_separator)] : string.Empty,
            DisplayPath = PathOf(relative),
            Directories = relative switch
            {
                "" => [new() { Name = "Movies", RelativePath = "Movies" }, new() { Name = "TV", RelativePath = "TV" }],
                "Movies" => [new() { Name = "Action", RelativePath = Child("Action") }],
                "media" => [new() { Name = "movies", RelativePath = Child("movies") }, new() { Name = "tv", RelativePath = Child("tv") }],
                "media/movies" => [new() { Name = "2024", RelativePath = Child("2024") }],
                _ => [],
            },
        };
    }

    private async Task<IRenderedComponent<AppDialogHost>> OpenPickerAsync(
        string selectionMode = ServerFolderSelectionModes.ExistingLibrary,
        string? mediaType = null)
    {
        var host = Render<AppDialogHost>();
        var parameters = new AppDialogParameters<ServerFolderPicker>
        {
            { picker => picker.SelectionMode, selectionMode },
            { picker => picker.MediaType, mediaType },
        };
        await host.InvokeAsync(() => Services.GetRequiredService<IAppDialogService>().ShowAsync<ServerFolderPicker>(string.Empty, parameters));
        host.WaitForElement(FolderRows);
        return host;
    }

    // A folder row is the shared browser frame's own (AppBrowserFolderRow); there is no other folder list in the picker.
    private const string FolderRows = ".app-browser-folder--folder";

    private static IElement Row(IRenderedComponent<AppDialogHost> host, string name) =>
        host.FindAll(FolderRows).Single(row => row.QuerySelector(".app-browser-folder__text")!.TextContent == name);

    private static IElement ConfirmButton(IRenderedComponent<AppDialogHost> host) =>
        host.FindAll("button").Single(button => button.TextContent.Trim() is "Add" or "Add anyway");

    // "Type a path..." is a row in the Locations list; it opens the small box that Go reads.
    private static void Type(IRenderedComponent<AppDialogHost> host, string path)
    {
        if (host.FindAll("input[aria-label='Folder path']").Count == 0)
        {
            host.Find("[data-picker-type-path]").Click();
        }

        host.Find("input[aria-label='Folder path']").Input(path);
    }

    [Fact]
    public async Task WhenTheServerCannotBeReachedThePickerSaysSoAndRetryAsksAgain()
    {
        _rootsDown = true;
        var host = Render<AppDialogHost>();
        var parameters = new AppDialogParameters<ServerFolderPicker> { { picker => picker.SelectionMode, ServerFolderSelectionModes.ExistingLibrary } };
        await host.InvokeAsync(() => Services.GetRequiredService<IAppDialogService>().ShowAsync<ServerFolderPicker>(string.Empty, parameters));

        host.WaitForAssertion(() => Assert.Contains("Could not reach the server", host.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("libraries.json", host.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No folders to browse", host.Markup, StringComparison.Ordinal);
        Assert.Equal(1, _rootsCalls);

        _rootsDown = false;
        host.Find(".app-error-state__retry").Click();

        host.WaitForElement(FolderRows);
        Assert.Equal(2, _rootsCalls);
        Assert.DoesNotContain("Could not reach the server", host.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGenuinelyEmptyListSaysThereAreNoFoldersInPlainWordsWithoutRetry()
    {
        _rootsEmpty = true;
        var host = Render<AppDialogHost>();
        var parameters = new AppDialogParameters<ServerFolderPicker> { { picker => picker.SelectionMode, ServerFolderSelectionModes.ExistingLibrary } };
        await host.InvokeAsync(() => Services.GetRequiredService<IAppDialogService>().ShowAsync<ServerFolderPicker>(string.Empty, parameters));

        host.WaitForAssertion(() => Assert.Contains("No folders to browse", host.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("libraries.json", host.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.FindAll(".app-error-state__retry"));
    }

    [Fact]
    public async Task TheFriendlyNameStartsEmptyOnABareDriveRootAndFollowsAChosenFolder()
    {
        _root = WholeDrive;
        var host = Render<AppDialogHost>();
        var parameters = new AppDialogParameters<ServerFolderPicker> { { picker => picker.ShowFolderName, true } };
        await host.InvokeAsync(() => Services.GetRequiredService<IAppDialogService>().ShowAsync<ServerFolderPicker>(string.Empty, parameters));
        host.WaitForElement(FolderRows);

        Assert.Equal(string.Empty, host.Find("[data-folder-name] input").GetAttribute("value") ?? string.Empty);

        Row(host, "Movies").Click();
        host.WaitForAssertion(() => Assert.Equal("Movies", host.Find("[data-folder-name] input").GetAttribute("value")));
    }

    [Fact]
    public async Task BrowsingNeverChecksAFolderAndNeverShowsAnOverlapMessage()
    {
        var host = await OpenPickerAsync();

        Row(host, "Movies").Click();
        host.WaitForAssertion(() => Assert.Contains("Action", host.Markup, StringComparison.Ordinal));
        host.Find("button[aria-label='Up one level']").Click();

        Assert.Equal(0, _validateCalls);
        Assert.Empty(host.FindAll(".server-folder-picker__result"));
        Assert.DoesNotContain("overlaps", host.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Checking", host.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOpenButtonOpensAFolderWithoutChecking()
    {
        var host = await OpenPickerAsync();

        host.Find("button[aria-label='Open folder Movies']").Click();

        host.WaitForAssertion(() => Assert.Contains("Action", host.Markup, StringComparison.Ordinal));
        Assert.Equal(0, _validateCalls);
    }

    [Fact]
    public async Task TypingAPathAndPressingGoListsThatFolderWithoutChecking()
    {
        var host = await OpenPickerAsync();

        Type(host, "C:\\srv\\Movies");
        host.Find("[data-picker-go]").Click();

        host.WaitForAssertion(() => Assert.Contains("Action", host.Markup, StringComparison.Ordinal));
        Assert.Contains("Movies", _browsed);
        Assert.Equal(0, _validateCalls);
    }

    [Fact]
    public async Task APathOutsideEveryLocationSaysSoBesideTheSmallBoxAndSelectsNothing()
    {
        var host = await OpenPickerAsync();

        Type(host, "D:\\elsewhere");
        host.Find("[data-picker-go]").Click();

        host.WaitForAssertion(() => Assert.Contains("can't open this folder", host.Find("[role=alert]").TextContent, StringComparison.Ordinal));
        Assert.True(ConfirmButton(host).HasAttribute("disabled"));
        Assert.Equal(0, _validateCalls);
    }

    [Fact]
    public async Task PressingAddChecksTheChosenFolderExactlyOnceAndClosesWhenItLooksRight()
    {
        var host = await OpenPickerAsync();

        Row(host, "Movies").Click();
        ConfirmButton(host).Click();

        host.WaitForAssertion(() => Assert.Empty(host.FindAll(FolderRows)));
        Assert.Equal(1, _validateCalls);
    }

    [Fact]
    public async Task AFolderAnotherLibraryUsesShowsAlreadyUsedAndAddStaysOff()
    {
        _issueCode = "already_configured";
        var host = await OpenPickerAsync();

        Row(host, "Movies").Click();
        ConfirmButton(host).Click();

        host.WaitForAssertion(() => Assert.Equal("already-used", host.Find(".server-folder-picker__result").GetAttribute("data-result")));
        Assert.True(ConfirmButton(host).HasAttribute("disabled"));
        Assert.Equal(1, _validateCalls);
    }

    [Fact]
    public async Task AHeadsUpAsksForASecondPressThatReadsAddAnyway()
    {
        _issueCode = "no_matching_files";
        var host = await OpenPickerAsync();

        Row(host, "Movies").Click();
        ConfirmButton(host).Click();
        host.WaitForAssertion(() => Assert.Equal("heads-up", host.Find(".server-folder-picker__result").GetAttribute("data-result")));
        Assert.Contains("Add anyway", host.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(host.FindAll(FolderRows));

        ConfirmButton(host).Click();

        host.WaitForAssertion(() => Assert.Empty(host.FindAll(FolderRows)));
        Assert.Equal(1, _validateCalls);
    }

    [Fact]
    public async Task ThePickerHasNoPathBoxAddressBarSegmentsForwardButtonOrSelectedPathFooter()
    {
        var host = await OpenPickerAsync();

        Assert.Empty(host.FindAll("input[aria-label='Folder path']"));
        Assert.Empty(host.FindAll(".server-folder-picker__crumb"));
        Assert.Empty(host.FindAll("button[aria-label='Forward']"));
        Assert.Empty(host.FindAll("input[aria-label='Selected path']"));
        Assert.Contains("Your files are never changed.", host.Find("[data-picker-safety]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFolderYouAreInsideIsTheSelectionAndIsNamedAboveTheList()
    {
        var host = await OpenPickerAsync();

        Assert.Contains("C:\\srv", host.Find("[data-picker-selected]").TextContent, StringComparison.Ordinal);
        Assert.Contains("2 folders inside", host.Find("[data-picker-selected]").TextContent, StringComparison.Ordinal);

        Row(host, "Movies").Click();

        host.WaitForAssertion(() => Assert.Contains("C:\\srv\\Movies", host.Find("[data-picker-selected]").TextContent, StringComparison.Ordinal));
        Assert.Contains("1 folder inside", host.Find("[data-picker-selected]").TextContent, StringComparison.Ordinal);
        Assert.False(ConfirmButton(host).HasAttribute("disabled"));
    }

    [Fact]
    public async Task AClickOnAFolderRowOpensItAndUpGoesBackToTheFolderAbove()
    {
        var host = await OpenPickerAsync();

        Row(host, "Movies").Click();
        host.WaitForAssertion(() => Assert.Equal("Action", host.Find(FolderRows + " .app-browser-folder__text").TextContent));
        host.Find("button[aria-label='Up one level']").Click();

        host.WaitForAssertion(() => Assert.Equal(2, host.FindAll(FolderRows).Count));
        Assert.Contains("C:\\srv", host.Find("[data-picker-selected]").TextContent, StringComparison.Ordinal);
        Assert.Equal(0, _validateCalls);
    }

    [Fact]
    public async Task TypeAPathIsARowInTheLocationsListThatOpensASmallBoxAndGoListsThatFolder()
    {
        var host = await OpenPickerAsync();
        Assert.Empty(host.FindAll("[data-picker-typed]"));

        host.Find("[data-picker-type-path]").Click();
        Assert.Single(host.FindAll("[data-picker-typed]"));
        Type(host, "C:\\srv\\Movies");
        host.Find("[data-picker-go]").Click();

        host.WaitForAssertion(() => Assert.Equal("Action", host.Find(FolderRows + " .app-browser-folder__text").TextContent));
        Assert.Contains("C:\\srv\\Movies", host.Find("[data-picker-selected]").TextContent, StringComparison.Ordinal);
        Assert.Empty(host.FindAll("[data-picker-typed]"));
        Assert.Equal(0, _validateCalls);
    }

    [Fact]
    public async Task TheFriendlyNameIsTheOnlyTextBoxUntilTypeAPathIsChosen()
    {
        var host = Render<AppDialogHost>();
        var parameters = new AppDialogParameters<ServerFolderPicker> { { picker => picker.ShowFolderName, true } };
        await host.InvokeAsync(() => Services.GetRequiredService<IAppDialogService>().ShowAsync<ServerFolderPicker>(string.Empty, parameters));
        host.WaitForElement(FolderRows);

        Assert.Single(host.FindAll("[data-folder-name] input"));
        Assert.Empty(host.FindAll("input[aria-label='Folder path']"));
    }

    [Fact]
    public async Task TheArrowKeysMoveTheHighlightEnterOpensTheRowAndBackspaceGoesUp()
    {
        var host = await OpenPickerAsync();
        var list = host.Find("table[role=grid]");
        Assert.Equal("server-folder-picker-folder-0-name", list.GetAttribute("aria-activedescendant"));

        list.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        host.WaitForAssertion(() => Assert.Equal("server-folder-picker-folder-1-name", host.Find("table[role=grid]").GetAttribute("aria-activedescendant")));
        host.Find("table[role=grid]").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        host.WaitForAssertion(() => Assert.Equal("server-folder-picker-folder-0-name", host.Find("table[role=grid]").GetAttribute("aria-activedescendant")));

        host.Find("table[role=grid]").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        host.WaitForAssertion(() => Assert.Equal("Action", host.Find(FolderRows + " .app-browser-folder__text").TextContent));

        host.Find("table[role=grid]").KeyDown(new KeyboardEventArgs { Key = "Backspace" });
        host.WaitForAssertion(() => Assert.Equal(2, host.FindAll(FolderRows).Count));
        Assert.Equal(0, _validateCalls);
    }

    [Fact]
    public async Task TheFolderPickerDrawsTheSharedBrowserFrameWithFolderRowsAndNoOtherFolderList()
    {
        var host = await OpenPickerAsync();

        Assert.Single(host.FindAll("[data-testid=browser-frame]"));
        Assert.Equal("folders", host.Find("[data-testid=browser-frame]").GetAttribute("data-frame"));
        Assert.Single(host.FindAll("[data-frame-sidebar]"));
        Assert.Single(host.FindAll("[data-frame-toolbar]"));
        Assert.Equal(2, host.FindAll(".app-browser-folder").Count);
        Assert.Empty(host.FindAll("[role=listbox]"));
    }

    [Fact]
    public async Task EscapeInTheTypeAPathBoxIsOwnedByTheBoxSoItClosesTheBoxAndLeavesTheWindowOpen()
    {
        var host = await OpenPickerAsync();
        host.Find("[data-picker-type-path]").Click();

        Assert.Equal("field", host.Find("[data-picker-typed]").GetAttribute("data-app-escape-owner"));
        host.Find("input[aria-label='Folder path']").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        host.WaitForAssertion(() => Assert.Empty(host.FindAll("[data-picker-typed]")));
        Assert.NotEmpty(host.FindAll("[data-testid=browser-frame]"));
    }

    [Fact]
    public async Task TheHighlightedRowIsNamedByItsNameCellWhichSpeaksOpenFolder()
    {
        var host = await OpenPickerAsync();

        var cell = host.Find("#server-folder-picker-folder-0-name");
        Assert.Equal("gridcell", cell.GetAttribute("role"));
        Assert.Equal("Open folder Movies", cell.GetAttribute("aria-label"));
        Assert.Equal("server-folder-picker-folder-0-name", host.Find("table[role=grid]").GetAttribute("aria-activedescendant"));
    }

    [Fact]
    public async Task TheFolderCountIsShownOnlyWhenTheListIsTheSelectedFolder()
    {
        _root = WindowsDrive;
        var host = Render<AppDialogHost>();
        var parameters = new AppDialogParameters<ServerFolderPicker> { { picker => picker.InitialPath, "C:\\srv\\Not\\Yet" } };
        _missingBrowse = true;
        await host.InvokeAsync(() => Services.GetRequiredService<IAppDialogService>().ShowAsync<ServerFolderPicker>(string.Empty, parameters));
        host.WaitForElement(FolderRows);

        var line = host.Find("[data-picker-selected]").TextContent;
        Assert.Contains("C:\\srv\\Not\\Yet", line, StringComparison.Ordinal);
        Assert.DoesNotContain("folders inside", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFolderPickerHidesTheModifiedCellAndItsHeadingTogether()
    {
        var host = await OpenPickerAsync();

        Assert.Single(host.FindAll("th.is-wide-only"));
        Assert.Equal(2, host.FindAll("td.is-wide-only").Count);
    }
}
