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
                return Task.FromResult<BrowseServerFoldersResultDto?>(Browse(relative));
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
        host.WaitForElement(".server-folder-picker__folder");
        return host;
    }

    private static IElement Row(IRenderedComponent<AppDialogHost> host, string name) =>
        host.FindAll(".server-folder-picker__folder").Single(row => row.QuerySelector("strong")!.TextContent == name);

    private static IElement ConfirmButton(IRenderedComponent<AppDialogHost> host) =>
        host.FindAll("button").Single(button => button.TextContent.Trim() is "Add" or "Add anyway");

    private static void Type(IRenderedComponent<AppDialogHost> host, string path) =>
        host.Find("input[aria-label='Folder path']").Input(path);

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

        host.WaitForElement(".server-folder-picker__folder");
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
        host.WaitForElement(".server-folder-picker__folder");

        Assert.Equal(string.Empty, host.Find("[data-folder-name] input").GetAttribute("value") ?? string.Empty);

        Row(host, "Movies").Click();
        host.WaitForAssertion(() => Assert.Equal("Movies", host.Find("[data-folder-name] input").GetAttribute("value")));
    }

    [Fact]
    public async Task BrowsingNeverChecksAFolderAndNeverShowsAnOverlapMessage()
    {
        var host = await OpenPickerAsync();

        Row(host, "Movies").Click();
        Row(host, "Movies").DoubleClick();
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
    public async Task APathOutsideEveryLocationSaysSoBesideThePathBoxAndSelectsNothing()
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

        host.WaitForAssertion(() => Assert.Empty(host.FindAll(".server-folder-picker__folder")));
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
        Assert.NotEmpty(host.FindAll(".server-folder-picker__folder"));

        ConfirmButton(host).Click();

        host.WaitForAssertion(() => Assert.Empty(host.FindAll(".server-folder-picker__folder")));
        Assert.Equal(1, _validateCalls);
    }

    [Fact]
    public async Task ThePickerHasNoAddressBarSegmentsForwardButtonOrSelectedPathFooter()
    {
        var host = await OpenPickerAsync();

        Assert.Empty(host.FindAll(".server-folder-picker__crumb"));
        Assert.Empty(host.FindAll("button[aria-label='Forward']"));
        Assert.Empty(host.FindAll("input[aria-label='Selected path']"));
        Assert.Contains("Your files are never changed.", host.Find("[data-picker-safety]").TextContent, StringComparison.Ordinal);
    }
}
