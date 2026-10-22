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
    private bool _withConfiguredLocation;
    private int _rootsCalls;

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
                        : _withConfiguredLocation ? [_root, ConfiguredMovies] : [_root];
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

    // The address bar shows folder names; its full path is what the text box holds, so open the box, read it and press Esc.
    private static string PathBarValue(IRenderedComponent<AppDialogHost> host)
    {
        var value = OpenPathBox(host).GetAttribute("value") ?? string.Empty;
        host.Find(".server-folder-picker__address-input").KeyDown(Key("Escape"));
        host.WaitForElement(".server-folder-picker__address-end");
        return value;
    }

    private static IElement OpenPathBox(IRenderedComponent<AppDialogHost> host)
    {
        host.Find(".server-folder-picker__address-end").Click();
        return host.Find(".server-folder-picker__address-input");
    }

    private static string[] Segments(IRenderedComponent<AppDialogHost> host) =>
        host.FindAll(".server-folder-picker__crumb").Select(crumb => crumb.TextContent.Trim()).ToArray();

    private static string SelectedPathValue(IRenderedComponent<AppDialogHost> host) =>
        host.Find("input[aria-label='Selected path']").GetAttribute("value") ?? string.Empty;

    private static IElement Row(IRenderedComponent<AppDialogHost> host, string name) =>
        host.FindAll(".server-folder-picker__folder").Single(row => row.QuerySelector("strong")!.TextContent == name);

    private static IElement ConfirmButton(IRenderedComponent<AppDialogHost> host) =>
        host.FindAll("button").Single(button => button.TextContent.Trim() == "Add");

    private static KeyboardEventArgs Key(string key) => new() { Key = key };

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
    public async Task ThePathRowSitsAboveTheLocationListAndTheBrowserAndSpansTheDialog()
    {
        var host = await OpenPickerAsync();

        var markup = host.Markup;
        var pathRow = markup.IndexOf("server-folder-picker__pathbar", StringComparison.Ordinal);
        Assert.True(pathRow >= 0);
        Assert.True(pathRow < markup.IndexOf("server-folder-picker__locations", StringComparison.Ordinal));
        Assert.True(pathRow < markup.IndexOf("server-folder-picker__browser", StringComparison.Ordinal));
        Assert.Null(host.Find(".server-folder-picker__browser").QuerySelector(".server-folder-picker__pathbar"));
        Assert.Null(host.Find(".server-folder-picker__locations").QuerySelector(".server-folder-picker__pathbar"));
        Assert.Equal("server-folder-picker", host.Find(".server-folder-picker__pathbar").ParentElement!.ClassList.First());
    }

    [Fact]
    public async Task TheFooterShowsTheSelectedPathWithACopyButtonBesideCancelAndTheConfirmButton()
    {
        var host = await OpenPickerAsync();

        host.WaitForAssertion(() => Assert.Equal("C:\\srv", SelectedPathValue(host)));
        Assert.True(host.Find("input[aria-label='Selected path']").HasAttribute("readonly"));
        Assert.NotNull(host.Find("button[aria-label='Copy selected path']"));
        Assert.Contains(host.FindAll("button"), button => button.TextContent.Trim() == "Cancel");
        Assert.NotNull(ConfirmButton(host));
    }

    [Fact]
    public async Task ASingleClickSelectsAFolderAndADoubleClickOpensIt()
    {
        var host = await OpenPickerAsync();
        host.WaitForAssertion(() => Assert.Equal("C:\\srv", PathBarValue(host)));

        Row(host, "Movies").Click();

        host.WaitForAssertion(() => Assert.Equal("C:\\srv\\Movies", SelectedPathValue(host)));
        Assert.Equal("C:\\srv", PathBarValue(host));
        Assert.Contains("is-selected", Row(host, "Movies").ClassName);
        Assert.DoesNotContain("Movies", _browsed);
        host.WaitForAssertion(() => Assert.False(ConfirmButton(host).HasAttribute("disabled")));

        Row(host, "Movies").DoubleClick();

        host.WaitForAssertion(() => Assert.Equal("C:\\srv\\Movies", PathBarValue(host)));
        Assert.Contains("Action", host.Find(".server-folder-picker__list").TextContent);
        Assert.Contains("Movies", _browsed);
        host.WaitForAssertion(() => Assert.Equal("C:\\srv\\Movies", SelectedPathValue(host)));
    }

    [Fact]
    public async Task EveryFolderRowHasAnExplicitOpenButtonThatOpensIt()
    {
        var host = await OpenPickerAsync();

        var openButtons = host.FindAll(".server-folder-picker__open");
        Assert.Equal(2, openButtons.Count);
        Assert.Equal(["Open folder Movies", "Open folder TV"], openButtons.Select(button => button.GetAttribute("aria-label") ?? string.Empty).ToArray());
        Assert.All(host.FindAll(".server-folder-picker__row"), row => Assert.Single(row.QuerySelectorAll("button.server-folder-picker__open")));
        Assert.DoesNotContain("Enter path manually", host.Markup, StringComparison.Ordinal);

        openButtons[0].Click();

        host.WaitForAssertion(() => Assert.Equal("C:\\srv\\Movies", PathBarValue(host)));
    }

    [Fact]
    public async Task TypingAPathAndPressingEnterNavigatesThere()
    {
        var host = await OpenPickerAsync();

        var input = OpenPathBox(host);
        input.Input("C:\\srv\\Movies");
        Assert.True(ConfirmButton(host).HasAttribute("disabled"));
        input.KeyDown(Key("Enter"));

        host.WaitForAssertion(() => Assert.Contains("Action", host.Find(".server-folder-picker__list").TextContent));
        Assert.Equal("C:\\srv\\Movies", PathBarValue(host));
        Assert.Equal("C:\\srv\\Movies", SelectedPathValue(host));
        host.WaitForAssertion(() => Assert.False(ConfirmButton(host).HasAttribute("disabled")));
    }

    [Fact]
    public async Task APathThatDoesNotExistSaysSoBesideThePathBoxAndSelectsNothing()
    {
        var host = await OpenPickerAsync();
        host.WaitForAssertion(() => Assert.False(ConfirmButton(host).HasAttribute("disabled")));

        var input = OpenPathBox(host);
        input.Input("C:\\srv\\missing");
        input.KeyDown(Key("Enter"));

        host.WaitForAssertion(() => Assert.Contains("That folder doesn't exist.", host.Find(".server-folder-picker__pathbar").TextContent, StringComparison.Ordinal));
        Assert.Equal(string.Empty, SelectedPathValue(host));
        Assert.True(ConfirmButton(host).HasAttribute("disabled"));
        Assert.DoesNotContain("Tuvima cannot access", host.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:\\srv\\locked")]
    [InlineData("D:\\outside")]
    public async Task APathTheLibraryCannotOpenSaysSoPlainlyAndSelectsNothing(string typed)
    {
        var host = await OpenPickerAsync();
        host.WaitForAssertion(() => Assert.False(ConfirmButton(host).HasAttribute("disabled")));

        var input = OpenPathBox(host);
        input.Input(typed);
        input.KeyDown(Key("Enter"));

        host.WaitForAssertion(() => Assert.Contains("The Library can't open that folder.", host.Find(".server-folder-picker__pathbar").TextContent, StringComparison.Ordinal));
        Assert.Equal(string.Empty, SelectedPathValue(host));
        Assert.True(ConfirmButton(host).HasAttribute("disabled"));
        Assert.DoesNotContain("Tuvima cannot read", host.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditingThePathAfterAMessageClearsTheMessage()
    {
        var host = await OpenPickerAsync();
        var input = OpenPathBox(host);
        input.Input("C:\\srv\\missing");
        input.KeyDown(Key("Enter"));
        host.WaitForAssertion(() => Assert.Contains("That folder doesn't exist.", host.Markup, StringComparison.Ordinal));

        host.Find(".server-folder-picker__address-input").Input("C:\\srv\\Mov");

        Assert.DoesNotContain("That folder doesn't exist.", host.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackAndForwardWalkTheFoldersVisitedInTheDialog()
    {
        var host = await OpenPickerAsync();
        host.WaitForAssertion(() => Assert.Equal("C:\\srv", PathBarValue(host)));
        Assert.True(host.Find("button[aria-label='Back']").HasAttribute("disabled"));
        Assert.True(host.Find("button[aria-label='Forward']").HasAttribute("disabled"));

        Row(host, "Movies").DoubleClick();
        host.WaitForAssertion(() => Assert.Equal("C:\\srv\\Movies", PathBarValue(host)));
        Assert.False(host.Find("button[aria-label='Back']").HasAttribute("disabled"));

        host.Find("button[aria-label='Back']").Click();
        host.WaitForAssertion(() => Assert.Equal("C:\\srv", PathBarValue(host)));
        Assert.False(host.Find("button[aria-label='Forward']").HasAttribute("disabled"));
        Assert.True(host.Find("button[aria-label='Back']").HasAttribute("disabled"));

        host.Find("button[aria-label='Forward']").Click();
        host.WaitForAssertion(() => Assert.Equal("C:\\srv\\Movies", PathBarValue(host)));
        Assert.True(host.Find("button[aria-label='Forward']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task UpAndTheBreadcrumbTrailMoveBackUpTheFolderTree()
    {
        var host = await OpenPickerAsync();
        Row(host, "Movies").DoubleClick();
        host.WaitForAssertion(() => Assert.Contains("Action", host.Find(".server-folder-picker__list").TextContent));
        Row(host, "Action").DoubleClick();
        host.WaitForAssertion(() => Assert.Equal("C:\\srv\\Movies\\Action", PathBarValue(host)));

        Assert.Equal(["C:", "Movies", "Action"], Segments(host));
        Assert.Contains("is-current", host.FindAll(".server-folder-picker__crumb").Last().ClassName);

        host.Find("button[aria-label='Up one level']").Click();
        host.WaitForAssertion(() => Assert.Equal("C:\\srv\\Movies", PathBarValue(host)));

        host.FindAll("button.server-folder-picker__crumb").First(crumb => crumb.TextContent.Trim() == "C:").Click();
        host.WaitForAssertion(() => Assert.Equal("C:\\srv", PathBarValue(host)));
        Assert.True(host.Find("button[aria-label='Up one level']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task OnlyFoldersAreEverRendered()
    {
        var host = await OpenPickerAsync();

        var rows = host.FindAll(".server-folder-picker__folder");
        Assert.Equal(["Movies", "TV"], rows.Select(row => row.QuerySelector("strong")!.TextContent).ToArray());
        Assert.DoesNotContain(".mkv", host.Find(".server-folder-picker__list").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KeyboardMovesTheHighlightSelectsWithSpaceOpensWithEnterAndBackspaceGoesUp()
    {
        var host = await OpenPickerAsync();
        var list = host.Find(".server-folder-picker__list");

        list.KeyDown(Key("ArrowDown"));
        host.WaitForAssertion(() => Assert.Contains("is-highlighted", host.FindAll(".server-folder-picker__folder")[1].ClassName));
        list.KeyDown(Key(" "));
        host.WaitForAssertion(() => Assert.Equal("C:\\srv\\TV", SelectedPathValue(host)));
        list.KeyDown(Key("ArrowUp"));
        list.KeyDown(Key("Enter"));
        host.WaitForAssertion(() => Assert.Equal("C:\\srv\\Movies", PathBarValue(host)));

        host.Find(".server-folder-picker__list").KeyDown(Key("Backspace"));
        host.WaitForAssertion(() => Assert.Equal("C:\\srv", PathBarValue(host)));
    }

    [Fact]
    public async Task CopyingPutsTheCompleteSelectedPathOnTheClipboardAndSaysCopied()
    {
        var host = await OpenPickerAsync();
        Row(host, "Movies").Click();
        host.WaitForAssertion(() => Assert.Equal("C:\\srv\\Movies", SelectedPathValue(host)));

        host.Find("button[aria-label='Copy selected path']").Click();

        host.WaitForAssertion(() => Assert.Contains("Copied", host.Find(".server-folder-picker__copied").TextContent, StringComparison.Ordinal));
        var copy = JSInterop.Invocations["navigator.clipboard.writeText"].Single();
        Assert.Equal("C:\\srv\\Movies", copy.Arguments[0]);
    }

    [Fact]
    public async Task PathsAreShownExactlyAsTheEngineSeesThemInsideAContainer()
    {
        _root = ContainerRoot;
        _separator = '/';
        var host = await OpenPickerAsync();

        host.WaitForAssertion(() => Assert.Equal("/media", PathBarValue(host)));
        Row(host, "Movies").Click();
        host.WaitForAssertion(() => Assert.Equal("/media/Movies", SelectedPathValue(host)));
        Row(host, "Movies").DoubleClick();
        host.WaitForAssertion(() => Assert.Equal("/media/Movies", PathBarValue(host)));

        // No host-side translation: nothing in the picker or the Dashboard's folder calls rewrites a path.
        Assert.DoesNotContain("C:", host.Find(".server-folder-picker").TextContent, StringComparison.Ordinal);
        var typed = OpenPathBox(host);
        typed.Input("/media/Movies");
        typed.KeyDown(Key("Enter"));
        host.WaitForAssertion(() => Assert.Equal("/media/Movies", SelectedPathValue(host)));
    }

    [Fact]
    public async Task ConfiguredMediaLocationsAreListedBeforeTheDrives()
    {
        _withConfiguredLocation = true;
        var host = await OpenPickerAsync();

        var headings = host.FindAll(".server-folder-picker__locations h3").Select(heading => heading.TextContent).ToArray();
        Assert.Equal(["Media locations", "Drives on this server"], headings);
        var labels = host.FindAll(".server-folder-picker__location strong").Select(label => label.TextContent).ToArray();
        Assert.Equal(["Movies library", "C:"], labels);
    }

    [Fact]
    public async Task OnlyDrivesAreListedUnderTheirOwnHeadingWhenNothingIsConfigured()
    {
        var host = await OpenPickerAsync();

        Assert.Equal(["Drives on this server"], host.FindAll(".server-folder-picker__locations h3").Select(heading => heading.TextContent).ToArray());
    }

    [Fact]
    public async Task TheTitleBarIconIsDifferentForEachOfTheSixMediaTypesAndGenericWithoutOne()
    {
        var icons = new Dictionary<string, string>();
        foreach (var type in new[] { "Movies", "TV", "Music", "Books", "Comics", "Audiobooks", null })
        {
            var host = await OpenPickerAsync(mediaType: type);
            icons[type ?? "(none)"] = host.Find(".app-dialog-shell__icon").InnerHtml;
            host.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();
            host.WaitForAssertion(() => Assert.Empty(host.FindAll(".server-folder-picker")));
        }

        Assert.Equal(7, icons.Values.Distinct().Count());
        Assert.Equal(AppMediaPresentation.IconFor(null, outlined: true), AppMediaPresentation.IconFor("Photos", outlined: true));
    }

    [Fact]
    public async Task SelectingASecondFolderMovesTheSelectionToIt()
    {
        var host = await OpenPickerAsync();
        host.WaitForAssertion(() => Assert.False(ConfirmButton(host).HasAttribute("disabled")));

        Row(host, "TV").Click();
        Row(host, "Movies").Click();

        host.WaitForAssertion(() => Assert.Equal("C:\\srv\\Movies", SelectedPathValue(host)));
        Assert.Single(host.FindAll(".server-folder-picker__folder.is-selected"));
    }

    [Fact]
    public async Task AccessFactsAreHiddenWhenTheFolderIsFine()
    {
        var host = await OpenPickerAsync(ServerFolderSelectionModes.ManagedLibrary);
        host.WaitForAssertion(() => Assert.False(ConfirmButton(host).HasAttribute("disabled")));

        var text = host.Markup;
        Assert.DoesNotContain("Read access", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Write access", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Space available", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NTFS", text, StringComparison.Ordinal);
        Assert.Empty(host.FindAll(".server-folder-picker__issues"));
    }

    [Fact]
    public async Task ProblemsAreShownInPlainEnglish()
    {
        _hasWrite = false;
        var host = await OpenPickerAsync(ServerFolderSelectionModes.ManagedLibrary);

        host.WaitForAssertion(() => Assert.Contains("can't write to this folder", host.Find(".server-folder-picker__notices").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADriveIsShownOnceInTheLocationListAndOnceInTheAddressBar()
    {
        _root = WholeDrive;
        var host = await OpenPickerAsync();

        var location = host.Find(".server-folder-picker__location");
        Assert.Equal("C:", location.TextContent.Trim());
        Assert.Empty(location.QuerySelectorAll("small"));
        Assert.Equal(["C:"], Segments(host));
    }

    [Fact]
    public async Task TheAddressBarShowsOneClickableSegmentPerFolderAndClickingOneOpensIt()
    {
        _root = SlashRoot;
        _separator = '/';
        var host = await OpenPickerAsync();
        var input = OpenPathBox(host);
        input.Input("/media/movies/2024");
        input.KeyDown(Key("Enter"));

        host.WaitForAssertion(() => Assert.Equal(["/", "media", "movies", "2024"], Segments(host)));
        Assert.Equal("location", host.FindAll(".server-folder-picker__crumb").Last().GetAttribute("aria-current"));
        _browsed.Clear();

        host.FindAll(".server-folder-picker__crumb").Single(crumb => crumb.TextContent.Trim() == "media").Click();

        host.WaitForAssertion(() => Assert.Equal(["/", "media"], Segments(host)));
        Assert.Equal(["media"], _browsed);
    }

    [Fact]
    public async Task ClickingTheEndOfTheAddressBarShowsTheFullPathAndEnterGoesThere()
    {
        var host = await OpenPickerAsync();
        host.WaitForAssertion(() => Assert.Equal("C:\\srv", SelectedPathValue(host)));

        var input = OpenPathBox(host);

        Assert.Equal("C:\\srv", input.GetAttribute("value"));
        Assert.Empty(host.FindAll(".server-folder-picker__crumb"));
        input.Input("C:\\srv\\Movies");
        input.KeyDown(Key("Enter"));

        host.WaitForAssertion(() => Assert.Equal(["C:", "Movies"], Segments(host)));
        Assert.Empty(host.FindAll(".server-folder-picker__address-input"));
        Assert.Contains("Movies", _browsed);
    }

    [Fact]
    public async Task EscapeInTheAddressBarTextBoxGoesBackToTheSegmentsWithoutBrowsing()
    {
        var host = await OpenPickerAsync();
        host.WaitForAssertion(() => Assert.False(ConfirmButton(host).HasAttribute("disabled")));
        _browsed.Clear();

        var input = OpenPathBox(host);
        input.Input("C:\\srv\\TV");
        Assert.True(ConfirmButton(host).HasAttribute("disabled"));
        input.KeyDown(Key("Escape"));

        host.WaitForAssertion(() => Assert.Equal(["C:"], Segments(host)));
        Assert.Empty(host.FindAll(".server-folder-picker__address-input"));
        Assert.Empty(_browsed);
        host.WaitForAssertion(() => Assert.False(ConfirmButton(host).HasAttribute("disabled")));
        Assert.Equal("C:\\srv", SelectedPathValue(host));
    }

    [Fact]
    public async Task ASegmentChevronListsTheFoldersInsideItAndChoosingOneOpensIt()
    {
        var host = await OpenPickerAsync();
        host.WaitForAssertion(() => Assert.Equal("C:\\srv", SelectedPathValue(host)));

        host.Find("button[aria-label='Folders inside C:']").Click();

        host.WaitForAssertion(() => Assert.Equal(["Movies", "TV"], host.FindAll(".server-folder-segment-menu [role='menuitem']").Select(item => item.TextContent.Trim()).ToArray()));
        host.FindAll(".server-folder-segment-menu [role='menuitem']").Single(item => item.TextContent.Trim() == "Movies").Click();

        host.WaitForAssertion(() => Assert.Equal(["C:", "Movies"], Segments(host)));
        Assert.Contains("Action", host.Find(".server-folder-picker__list").TextContent);
        Assert.Contains("Movies", _browsed);
    }

    [Fact]
    public void InlineRendersNoDialogChromeAndConfirmingCallsOnConfirmedInsteadOfClosingADialog()
    {
        ServerFolderValidationResultDto? confirmed = null;
        var cut = Render<ServerFolderPicker>(parameters => parameters
            .Add(picker => picker.Inline, true)
            .Add(picker => picker.ConfirmLabel, "Next")
            .Add(picker => picker.OnConfirmed, EventCallback.Factory.Create<ServerFolderValidationResultDto>(this, result => confirmed = result)));
        cut.WaitForElement(".server-folder-picker__folder");

        Assert.Empty(cut.FindAll(".app-dialog-shell"));
        Assert.Empty(cut.FindAll("dialog"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Cancel");
        Assert.NotNull(cut.Find(".server-folder-picker__address"));
        Assert.NotNull(cut.Find("input[aria-label='Selected path']"));
        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Next").HasAttribute("disabled")));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Next").Click();

        cut.WaitForAssertion(() => Assert.NotNull(confirmed));
        Assert.Equal("C:\\srv", confirmed!.Path);
    }

    // A path typed but not entered stays in the box when focus leaves it (moving to Next), so Next can neither confirm the earlier folder
    // nor lose the typing unseen. Only Enter uses the path.
    [Fact]
    public void LeavingTheBoxWithATypedButNotEnteredPathKeepsItAndLeavesNextOffUntilEnter()
    {
        ServerFolderValidationResultDto? confirmed = null;
        var cut = Render<ServerFolderPicker>(parameters => parameters
            .Add(picker => picker.Inline, true)
            .Add(picker => picker.ConfirmLabel, "Next")
            .Add(picker => picker.OnConfirmed, EventCallback.Factory.Create<ServerFolderValidationResultDto>(this, result => confirmed = result)));
        cut.WaitForElement(".server-folder-picker__folder");
        IElement Next() => cut.FindAll("button").Single(button => button.TextContent.Trim() == "Next");
        cut.WaitForAssertion(() => Assert.False(Next().HasAttribute("disabled")));

        cut.Find(".server-folder-picker__address-end").Click();
        cut.Find(".server-folder-picker__address-input").Input("C:\\srv\\Films");
        cut.Find(".server-folder-picker__address-input").Blur();

        Assert.Equal("C:\\srv\\Films", cut.Find(".server-folder-picker__address-input").GetAttribute("value"));
        Assert.True(Next().HasAttribute("disabled"));
        Assert.Null(confirmed);

        cut.Find(".server-folder-picker__address-input").KeyDown(Key("Enter"));
        cut.WaitForAssertion(() => Assert.False(Next().HasAttribute("disabled")));
        Next().Click();
        cut.WaitForAssertion(() => Assert.NotNull(confirmed));
        Assert.EndsWith("Films", confirmed!.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavingTheBoxUntouchedGoesBackToTheFolderNamesAndKeepsTheSelection()
    {
        var cut = Render<ServerFolderPicker>(parameters => parameters.Add(picker => picker.Inline, true).Add(picker => picker.ConfirmLabel, "Next"));
        cut.WaitForElement(".server-folder-picker__folder");
        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Next").HasAttribute("disabled")));

        cut.Find(".server-folder-picker__address-end").Click();
        cut.Find(".server-folder-picker__address-input").Blur();

        cut.WaitForElement(".server-folder-picker__address-end");
        Assert.False(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Next").HasAttribute("disabled"));
    }

    // Focus is a convenience: when the browser says the element it was asked to focus is gone, the picker carries on.
    [Fact]
    public async Task AFocusThatFailsBecauseTheElementIsGoneDoesNotBreakThePicker()
    {
        var host = await OpenPickerAsync();
        JSInterop.SetupVoid("Blazor._internal.domWrapper.focus", _ => true).SetException(new Microsoft.JSInterop.JSException("Unable to focus an invalid element."));
        JSInterop.SetupVoid("selectText", _ => true).SetException(new Microsoft.JSInterop.JSException("Unable to focus an invalid element."));

        // Selecting a folder moves focus to the list; opening the path box selects its text.
        Row(host, "Movies").Click();
        host.WaitForAssertion(() => Assert.Contains("is-selected", Row(host, "Movies").ClassName));
        host.Find(".server-folder-picker__address-end").Click();
        host.WaitForElement(".server-folder-picker__address-input");
        host.Find(".server-folder-picker__address-input").KeyDown(Key("Escape"));

        host.WaitForElement(".server-folder-picker__address-end");
        Assert.Contains("Movies", SelectedPathValue(host), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedTypedPathMarksTheBoxInvalidAndPointsAtItsMessage()
    {
        var host = await OpenPickerAsync();
        var input = OpenPathBox(host);
        Assert.Null(input.GetAttribute("aria-invalid"));

        input.Input("C:\\srv\\missing");
        host.Find(".server-folder-picker__address-input").KeyDown(Key("Enter"));

        host.WaitForAssertion(() => Assert.Equal("true", host.Find(".server-folder-picker__address-input").GetAttribute("aria-invalid")));
        var describedBy = host.Find(".server-folder-picker__address-input").GetAttribute("aria-describedby");
        Assert.False(string.IsNullOrWhiteSpace(describedBy));
        var message = host.Find($"#{describedBy}");
        Assert.Equal("alert", message.GetAttribute("role"));
        Assert.Contains("That folder doesn't exist.", message.TextContent, StringComparison.Ordinal);

        // Typing again clears the message and the markers with it.
        host.Find(".server-folder-picker__address-input").Input("C:\\srv\\Mov");
        host.WaitForAssertion(() => Assert.Null(host.Find(".server-folder-picker__address-input").GetAttribute("aria-invalid")));
        Assert.Null(host.Find(".server-folder-picker__address-input").GetAttribute("aria-describedby"));
    }

    [Fact]
    public async Task NotInlineConfirmingClosesTheDialogWithTheValidationResult()
    {
        var host = Render<AppDialogHost>();
        var reference = await host.InvokeAsync(() => Services.GetRequiredService<IAppDialogService>().ShowAsync<ServerFolderPicker>(string.Empty));
        host.WaitForElement(".server-folder-picker__folder");
        host.WaitForAssertion(() => Assert.False(ConfirmButton(host).HasAttribute("disabled")));

        ConfirmButton(host).Click();

        var result = await reference.Result.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(result);
        Assert.False(result.Canceled);
        Assert.Equal("C:\\srv", Assert.IsType<ServerFolderValidationResultDto>(result.Data).Path);
    }

    [Fact]
    public async Task PhoneDevicesGetThePhoneLayoutClass()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync("mobile");
        var host = await OpenPickerAsync();

        Assert.Contains("server-folder-picker--phone", host.Find(".server-folder-picker").ClassName);
    }
}
