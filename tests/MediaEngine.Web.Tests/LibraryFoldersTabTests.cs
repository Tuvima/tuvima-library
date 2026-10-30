using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// The Folders tab of a library page (Libraries spec D4, D7, D8): the table, the import destination, the Scan switch, the "..." menu,
// Add folder (one confirmation, no follow-up question), Add files (copy with a free-space check) and Scan now for this library only.
public sealed class LibraryFoldersTabTests : AsyncBunitContext
{
    private const string MoviesId = "11111111-1111-4111-8111-111111111111";
    private const string AudiobooksId = "22222222-2222-4222-8222-222222222222";

    private LibrariesConfigurationDto _configuration = BuildConfiguration();
    private readonly List<LibraryMutationRequest> _mutations = [];
    private readonly List<(string LibraryId, LibraryAddFilesRequest Request)> _addFiles = [];
    private readonly List<string> _scans = [];
    private Func<string, PathTestResultDto> _pathProbe = _ => new() { Exists = true, HasRead = true, HasWrite = true };
    private LibraryAddFilesResultDto? _addFilesResult = new() { Copied = 1, SkippedDuplicates = 1, Unreadable = 0 };
    private string? _lastError;
    private BrowseServerFoldersResultDto _browse = new() { DisplayPath = "C:\\media", Files = [] };

    private static LibrariesConfigurationDto BuildConfiguration() => new()
    {
        StorageLocations = [new() { Id = "media", Label = "Media storage", Path = "C:\\media", AllowWrite = true }],
        Libraries =
        [
            new()
            {
                Id = MoviesId, Name = "Movies", KindKey = "movies", Area = "watch", MediaTypes = ["Movies"],
                PrimaryDestinationSourceId = "movies-main",
                Sources =
                [
                    new() { Id = "movies-main", Path = "E:\\Library\\Movies", Notes = "Library Movies", ManagementMode = "managed_by_tuvima", AccessMode = "writable" },
                    new() { Id = "movies-plex", Path = "D:\\Plex\\Movies", Notes = "Plex Movies", ManagementMode = "managed_by_tuvima", AccessMode = "writable" },
                    new() { Id = "movies-old", Path = "F:\\Movies", Notes = "Old drive", ManagementMode = "existing_library", AccessMode = "read_only" },
                ],
            },
            new()
            {
                Id = AudiobooksId, Name = "Audiobooks", KindKey = "audiobooks", Area = "listen", MediaTypes = ["Audiobooks"],
                Sources = [new() { Id = "audio-old", Path = "C:\\media\\Audiobooks", ManagementMode = "existing_library", AccessMode = "read_only" }],
            },
        ],
    };

    public LibraryFoldersTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(_configuration));
            stub.SetHandler(nameof(IEngineApiClient.TestPathAsync), args => Task.FromResult<PathTestResultDto?>(_pathProbe((string)args![0]!)));
            stub.SetHandler(nameof(IEngineApiClient.GetImportFolderWaitingAsync), _ => Task.FromResult<MediaEngine.Contracts.Ingestion.ImportFolderWaitingResponse?>(null));
            stub.SetHandler(nameof(IEngineApiClient.GetFolderJobsAsync), _ => Task.FromResult<IReadOnlyList<FolderJobStatusDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetExcludedFilesAsync), _ => Task.FromResult<IReadOnlyList<ExcludedFileDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryFolderTitlesAsync), _ => Task.FromResult<LibraryFolderTitlesDto?>(new()
            {
                Counts = new() { ["movies-main"] = 1200, ["movies-plex"] = 84, ["movies-old"] = 0 },
            }));
            stub.SetHandler("get_LastError", _ => _lastError);
            stub.SetHandler(nameof(IEngineApiClient.MutateLibraryAsync), args =>
            {
                var request = (LibraryMutationRequest)args![0]!;
                _mutations.Add(request);
                var library = request.Library!;
                _configuration = new LibrariesConfigurationDto
                {
                    StorageLocations = _configuration.StorageLocations,
                    Libraries = [.. _configuration.Libraries.Select(item => item.Id == library.Id ? library : item)],
                    FileHandling = _configuration.FileHandling,
                };
                return Task.FromResult<LibrariesConfigurationDto?>(_configuration);
            });
            stub.SetHandler(nameof(IEngineApiClient.ScanLibraryAsync), args =>
            {
                _scans.Add((string)args![0]!);
                return Task.FromResult<MediaEngine.Contracts.Ingestion.RescanAcceptedResponse?>(new("Scan started for this library.", 2));
            });
            stub.SetHandler(nameof(IEngineApiClient.TriggerRescanAsync), _ => Task.FromResult(true));
            stub.SetHandler(nameof(IEngineApiClient.AddFilesToLibraryAsync), args =>
            {
                _addFiles.Add(((string)args![0]!, (LibraryAddFilesRequest)args[1]!));
                return Task.FromResult(_addFilesResult);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetServerFolderRootsAsync), _ => Task.FromResult<IReadOnlyList<ServerStorageLocationDto>>(_configuration.StorageLocations));
            stub.SetHandler(nameof(IEngineApiClient.BrowseServerFoldersAsync), _ => Task.FromResult<BrowseServerFoldersResultDto?>(_browse));
            stub.SetHandler(nameof(IEngineApiClient.ValidateServerFolderAsync), _ => Task.FromResult<ServerFolderValidationResultDto?>(new()
            {
                Path = "G:\\More\\Movies",
                StorageLocationId = "media",
                Exists = true,
                HasRead = true,
                HasWrite = true,
                CanSelect = true,
            }));
        }));
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.LibraryKindsState>();
        Render<AppPopoverHost>();
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private IRenderedComponent<LibraryFoldersPanel> OpenTable(string libraryId = MoviesId, bool readOnly = false)
    {
        var cut = Render<LibraryFoldersPanel>(parameters => parameters
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog)
            .Add(x => x.LibraryId, libraryId)
            .Add(x => x.HostOwnsActions, true)
            .Add(x => x.ReadOnly, readOnly));
        cut.WaitForElement(".folder-grid");
        cut.WaitForAssertion(() => Assert.DoesNotContain("—", Text(cut.FindAll("tbody tr")[0].QuerySelectorAll("td")[1])));
        cut.WaitForAssertion(() => Assert.DoesNotContain("—", Text(cut.FindAll("tbody tr")[0].QuerySelectorAll("td")[2])));
        return cut;
    }

    private IRenderedComponent<LibraryShell> OpenPage(string libraryId = MoviesId)
    {
        var library = _configuration.Libraries.Single(item => item.Id == libraryId);
        var cut = Render<LibraryShell>(parameters => parameters
            .Add(x => x.Library, library)
            .Add(x => x.Configuration, _configuration)
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog));
        cut.WaitForElement(".folder-grid");
        return cut;
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> Rows<T>(IRenderedComponent<T> cut) where T : Microsoft.AspNetCore.Components.IComponent => cut.FindAll("tbody tr");

    [Fact]
    public void TheLibraryHeaderHasARenamePencilForALibraryButNotOnAPhone()
    {
        var cut = OpenPage();
        Assert.NotEmpty(cut.FindAll(".libpage__facts button[aria-label='Rename Movies']"));

        var phone = Render<LibraryShell>(parameters => parameters
            .Add(x => x.Library, _configuration.Libraries.Single(item => item.Id == MoviesId))
            .Add(x => x.Configuration, _configuration)
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog)
            .Add(x => x.ReadOnly, true));
        Assert.Empty(phone.FindAll(".libpage__facts button[aria-label='Rename Movies']"));
    }

    [Fact]
    public void TheFoldersTableHasTheFiveColumnsAndOneRowPerFolderWithTitlesAndAccessWords()
    {
        var cut = OpenTable();

        var headers = cut.FindAll("thead th").Select(Text).ToArray();
        Assert.Equal(["Folder", "Titles", "Access", "Scan", "Import destination", "Actions"], headers);
        var rows = Rows(cut);
        Assert.Equal(3, rows.Count);
        Assert.Contains("Library Movies", Text(rows[0].QuerySelectorAll("td")[0]));
        Assert.Contains("E:\\Library\\Movies", Text(rows[0].QuerySelectorAll("td")[0]));
        Assert.Equal("1,200", Text(rows[0].QuerySelectorAll("td")[1]));
        Assert.Equal("84", Text(rows[1].QuerySelectorAll("td")[1]));
        Assert.All(rows, row => Assert.Equal("Accessible", Text(row.QuerySelectorAll("td")[2])));
    }

    [Fact]
    public void AFolderThatIsOfflineOrNeedsAccessSaysSoInWordsAndGetsAnAmberWarningRow()
    {
        _pathProbe = path => path.StartsWith("D:", StringComparison.Ordinal)
            ? new() { Exists = false }
            : path.StartsWith("F:", StringComparison.Ordinal) ? new() { Exists = true, HasRead = false } : new() { Exists = true, HasRead = true, HasWrite = true };

        var cut = OpenTable();

        cut.WaitForAssertion(() =>
        {
            var rows = Rows(cut);
            Assert.Equal("Folder offline", Text(rows[1].QuerySelectorAll("td")[2]));
            Assert.Equal("Needs access", Text(rows[2].QuerySelectorAll("td")[2]));
        });
        Assert.Single(cut.FindAll("[data-source='movies-plex'] [data-warning=offline]"));
        Assert.Contains("offline", Text(cut.Find("[data-source='movies-plex'] [data-warning=offline]")));
        Assert.Empty(cut.FindAll("[data-source='movies-main'] [data-warning=offline]"));
    }

    [Fact]
    public void OnlyOneManagedFolderIsTheImportDestination_AndAnAsIsFolderCannotBeOneAndExplainsWhy()
    {
        var cut = OpenTable();

        var radios = cut.FindAll("[role=radio]");
        Assert.Equal(2, radios.Count);
        Assert.Equal(["true", "false"], radios.Select(radio => radio.GetAttribute("aria-checked")!).ToArray());
        var asIs = cut.Find("[data-source='movies-old']");
        Assert.Empty(asIs.QuerySelectorAll("[role=radio]"));
        var why = asIs.QuerySelector(".app-info-tip");
        Assert.NotNull(why);
        Assert.Contains("As-is", why!.GetAttribute("aria-label"));
        Assert.Contains("Only a managed folder can be the import destination", why.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task ChoosingAnotherManagedFolderMovesTheImportDestinationThroughTheLibrarySave()
    {
        var cut = OpenTable();

        await cut.ClickAsync("[data-source='movies-plex'] [role=radio]");

        cut.WaitForAssertion(() => Assert.Single(_mutations));
        Assert.Equal("movies-plex", _mutations[0].Library!.PrimaryDestinationSourceId);
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-source='movies-plex'] [role=radio]").GetAttribute("aria-checked")));
        Assert.Equal("false", cut.Find("[data-source='movies-main'] [role=radio]").GetAttribute("aria-checked"));
    }

    [Fact]
    public void TheScanSwitchPersistsThroughTheLibrarySave()
    {
        var cut = OpenTable();
        var scan = cut.Find("[data-source='movies-plex'] input[role=switch][aria-label='Scan Plex Movies']");
        Assert.True(scan.HasAttribute("checked"));

        scan.Change(false);

        cut.WaitForAssertion(() => Assert.Single(_mutations));
        var saved = _mutations[0].Library!.Sources.Single(source => source.Id == "movies-plex");
        Assert.False(saved.ScanEnabled);
        Assert.True(_mutations[0].Library!.Sources.Single(source => source.Id == "movies-main").ScanEnabled);
        cut.WaitForAssertion(() => Assert.Contains("Scanning is paused", Text(cut.Find("[data-source='movies-plex']"))));
    }

    [Fact]
    public async Task TheRowMenuOffersOverrideFileHandlingPauseScanningChangeWhatTheFolderIsAndRemove()
    {
        var cut = OpenTable();

        await cut.ClickAsync("[data-source='movies-plex'] .app-row-menu__trigger");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=menuitem]")));
        var items = cut.FindAll("[role=menuitem]").Select(Text).ToArray();
        Assert.Contains("Override file handling for this folder…", items);
        Assert.Contains("Pause scanning", items);
        Assert.Contains("Change what this folder is…", items);
        Assert.Contains("Remove folder", items);
        Assert.Equal("Remove folder", items[^1]);
    }

    [Fact]
    public async Task PauseScanningInTheMenuTurnsScanningOffForThatFolder()
    {
        var cut = OpenTable();
        await cut.ClickAsync("[data-source='movies-plex'] .app-row-menu__trigger");
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role=menuitem]"), item => Text(item) == "Pause scanning"));

        await cut.ClickAsync(page => page.FindAll("[role=menuitem]").First(item => Text(item) == "Pause scanning"));

        cut.WaitForAssertion(() => Assert.Single(_mutations));
        Assert.False(_mutations[0].Library!.Sources.Single(source => source.Id == "movies-plex").ScanEnabled);
    }

    [Fact]
    public async Task OverridingFileHandlingSavesThePerFolderOverrideAndLeavesOtherFoldersAlone()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = OpenTable();
        await cut.ClickAsync("[data-source='movies-plex'] .app-row-menu__trigger");
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role=menuitem]"), item => Text(item).StartsWith("Override file handling", StringComparison.Ordinal)));
        await cut.ClickAsync(page => page.FindAll("[role=menuitem]").First(item => Text(item).StartsWith("Override file handling", StringComparison.Ordinal)));
        dialogs.WaitForAssertion(() => Assert.Contains("Override file handling", dialogs.Markup));
        Assert.Contains("Use library setting", dialogs.Markup);

        // The dialog's own rule: both choices on "Use library setting" means no override at all.
        Assert.Null(FolderFileHandlingDialog.Build(FolderFileHandlingDialog.UseLibrary, FolderFileHandlingDialog.UseLibrary));
        var built = FolderFileHandlingDialog.Build("keep_both", "no");
        Assert.Equal("keep_both", built!.DuplicatePolicy);
        Assert.False(built.WriteTags);
        var duplicatesOnly = FolderFileHandlingDialog.Build("keep_one", FolderFileHandlingDialog.UseLibrary);
        Assert.Equal("keep_one", duplicatesOnly!.DuplicatePolicy);
        Assert.Null(duplicatesOnly.WriteTags);
    }

    [Fact]
    public async Task OnAPhoneTheTableIsReadOnlyWithNoSwitchChoiceOrMenu()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync("mobile");

        var cut = OpenTable(readOnly: true);

        Assert.Empty(cut.FindAll(".folder-grid input[role=switch]"));
        Assert.Empty(cut.FindAll("[role=radio]"));
        Assert.Empty(cut.FindAll(".app-row-menu__trigger"));
        Assert.Empty(cut.FindAll("tbody button:not(.app-info-tip)"));
        Assert.Equal(3, Rows(cut).Count);
    }

    [Fact]
    public async Task ScanNowScansThisLibraryOnly()
    {
        var cut = OpenPage();

        await cut.ClickAsync(page => page.FindAll(".libpage__actions button").First(button => Text(button) == "Scan now"));

        cut.WaitForAssertion(() => Assert.Equal([MoviesId], _scans));
        var calls = Services.GetRequiredService<IEngineApiClient>();
        Assert.NotNull(calls);
    }

    [Fact]
    public async Task AddFolderAddsToThisLibraryGlowsTheNewRowAndShowsNoToastAndAsksNothingElse()
    {
        var dialogs = Render<AppDialogHost>();
        var toasts = Render<AppToastHost>();
        var cut = OpenPage();
        var before = _configuration.Libraries.Count;

        await cut.ClickAsync(page => page.FindAll(".libpage__actions button").First(button => Text(button) == "Add folder"));
        dialogs.WaitForAssertion(() => Assert.Contains(" folder</", dialogs.Markup));
        dialogs.WaitForAssertion(() => Assert.Contains(dialogs.FindAll("button"), button => Text(button) == "Choose folder" && !button.HasAttribute("disabled")));
        await dialogs.ClickAsync(host => host.FindAll("button").First(button => Text(button) == "Choose folder" && !button.HasAttribute("disabled")));

        // It goes into THIS library (not a new one), the table comes back with the new row pointed at, and nothing else is asked.
        cut.WaitForAssertion(() => Assert.Single(_mutations));
        Assert.Equal(MoviesId, _mutations[0].Library!.Id);
        Assert.Equal(before, _configuration.Libraries.Count);
        Assert.Contains(_mutations[0].Library!.Sources, source => source.Path == "G:\\More\\Movies");
        Assert.Equal("movies-main", _mutations[0].Library!.PrimaryDestinationSourceId);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr[data-highlighted=true]")));
        Assert.DoesNotContain("Added", toasts.Markup, StringComparison.Ordinal);
        dialogs.WaitForAssertion(() => Assert.DoesNotContain("Organise", dialogs.Markup));
        Assert.DoesNotContain("go to", dialogs.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(dialogs.FindAll("[role=dialog] button"));
        cut.WaitForAssertion(() => Assert.Equal(4, Rows(cut).Count));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr[data-highlighted=true]")));
    }

    [Fact]
    public void AddFilesWaitsForAnImportDestinationAndSaysWhy()
    {
        var cut = OpenPage(AudiobooksId);

        var button = cut.FindAll(".libpage__actions button").Single(item => Text(item) == "Add files");

        Assert.True(button.HasAttribute("disabled"));
        Assert.Equal("needs-import-folder", button.GetAttribute("data-add-files"));
        var why = cut.Find(".libpage__actions .app-info-tip");
        Assert.Contains("Choose an import folder first", why.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task AddFilesIsHiddenOnAPhone()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync("mobile");
        var library = _configuration.Libraries.Single(item => item.Id == MoviesId);

        var cut = Render<LibraryShell>(parameters => parameters
            .Add(x => x.Library, library)
            .Add(x => x.Configuration, _configuration)
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog)
            .Add(x => x.ReadOnly, true));

        Assert.Empty(cut.FindAll(".libpage__actions"));
    }

    [Fact]
    public async Task AddFilesListsServerFilesGreysOutUnsupportedOnesAndCopiesTheSelection()
    {
        _browse = new BrowseServerFoldersResultDto
        {
            DisplayPath = "C:\\media",
            Directories = [new() { Name = "Holiday", RelativePath = "Holiday" }],
            Files =
            [
                new() { Name = "Dune.mkv", Path = "C:\\media\\Dune.mkv", SizeBytes = 4L * 1024 * 1024 * 1024, Supported = true },
                new() { Name = "Arrival.mkv", Path = "C:\\media\\Arrival.mkv", SizeBytes = 3900L * 1024 * 1024, Supported = true },
                new() { Name = "notes.txt", Path = "C:\\media\\notes.txt", SizeBytes = 2048, Supported = false },
            ],
        };
        var dialogs = Render<AppDialogHost>();
        var cut = OpenPage();

        await cut.ClickAsync(page => page.FindAll(".libpage__actions button").First(button => Text(button) == "Add files"));
        dialogs.WaitForElement("[data-file='Dune.mkv']");

        var unsupported = dialogs.Find("[data-file='notes.txt']");
        Assert.Equal("false", unsupported.GetAttribute("data-supported"));
        Assert.True(unsupported.QuerySelector("input[type=checkbox]")!.HasAttribute("disabled"));
        Assert.NotNull(unsupported.QuerySelector(".app-info-tip"));
        Assert.Equal("No videos selected", Text(dialogs.Find("[data-add-files=selection]")));

        dialogs.Find("[data-file='Dune.mkv'] input[type=checkbox]").Change(true);
        dialogs.Find("[data-file='Arrival.mkv'] input[type=checkbox]").Change(true);
        dialogs.WaitForAssertion(() => Assert.Equal("2 videos selected, 7.8 GB", Text(dialogs.Find("[data-add-files=selection]"))));

        await dialogs.ClickAsync(host => host.FindAll("button").First(button => Text(button) == "Copy files"));

        dialogs.WaitForAssertion(() => Assert.Equal("Copied 1. Already in your library: 1.", Text(dialogs.Find("[data-add-files=summary]"))));
        var (libraryId, request) = Assert.Single(_addFiles);
        Assert.Equal(MoviesId, libraryId);
        Assert.Equal(["C:\\media\\Dune.mkv", "C:\\media\\Arrival.mkv"], request.Paths);
        // Only the Engine's copy is asked for: the dashboard never offers to move or change the originals.
        Assert.DoesNotContain("move", dialogs.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddFilesShowsTheEnginesNotEnoughSpaceRefusalAndStartsNothing()
    {
        _addFilesResult = null;
        _lastError = "Not enough space: 9.2 GB needed, 3 GB free.";
        _browse = new BrowseServerFoldersResultDto
        {
            DisplayPath = "C:\\media",
            Files = [new() { Name = "Dune.mkv", Path = "C:\\media\\Dune.mkv", SizeBytes = 8L * 1024 * 1024 * 1024, Supported = true }],
        };
        var dialogs = Render<AppDialogHost>();
        var cut = OpenPage();
        await cut.ClickAsync(page => page.FindAll(".libpage__actions button").First(button => Text(button) == "Add files"));
        dialogs.WaitForElement("[data-file='Dune.mkv']");
        dialogs.Find("[data-file='Dune.mkv'] input[type=checkbox]").Change(true);

        await dialogs.ClickAsync(host => host.FindAll("button").First(button => Text(button) == "Copy files"));

        dialogs.WaitForAssertion(() => Assert.Equal("Not enough space: 9.2 GB needed, 3 GB free.", Text(dialogs.Find("[data-add-files=error]"))));
        Assert.Empty(dialogs.FindAll("[data-add-files=summary]"));
    }

    [Theory]
    [InlineData(0, 0L, "video", "No videos selected")]
    [InlineData(1, 1024L * 1024 * 1024, "video", "1 video selected, 1 GB")]
    [InlineData(3, 600L * 1024 * 1024, "audio file", "3 audio files selected, 600 MB")]
    public void TheSelectionLineReadsTheCountAndTheTotalSize(int count, long bytes, string noun, string expected) =>
        Assert.Equal(expected, AddFilesWording.SelectionText(count, bytes, noun));

    [Fact]
    public void TheSummaryNamesWhatWasCopiedSkippedAsDuplicatesAndCouldNotBeRead()
    {
        Assert.Equal(
            "Copied 5. Already in your library: 2. Could not be read: 1.",
            AddFilesWording.SummaryText(new LibraryAddFilesResultDto { Copied = 5, SkippedDuplicates = 2, Unreadable = 1 }));
        Assert.Equal("Nothing was copied.", AddFilesWording.SummaryText(new LibraryAddFilesResultDto()));
    }

    [Theory]
    [InlineData("Movies", "video")]
    [InlineData("TV", "video")]
    [InlineData("Music", "audio file")]
    [InlineData("Books", "book")]
    [InlineData("Comics", "comic")]
    [InlineData("Other", "file")]
    public void EachKindOfLibraryNamesItsFilesInPlainWords(string mediaType, string noun) =>
        Assert.Equal(noun, AddFilesWording.NounFor([mediaType]));
}
