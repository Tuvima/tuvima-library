using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class LibrarySettingsFlowTests : AsyncBunitContext
{
    private const string MoviesId = "11111111-1111-4111-8111-111111111111";
    private const string TvId = "33333333-3333-4333-8333-333333333333";
    private const string AudiobooksId = "22222222-2222-4222-8222-222222222222";

    private LibrariesConfigurationDto _configuration = BuildConfiguration();
    private int _saves;
    private LibraryFolderDto? _lastLibrary;
    private LibraryMutationRequest? _lastRequest;
    // Holds the Retry now answer open, to press the button again while the first request is still running.
    private TaskCompletionSource? _retryGate;
    private UpdateLibrariesRequest? _lastUpdate;
    private MediaEngine.Contracts.Ingestion.ImportFolderWaitingResponse? _waiting;
    private Func<string, PathTestResultDto> _pathProbe = _ => new() { Exists = true, HasRead = true, HasWrite = true };

    // The Engine's side of converting a folder: a quick count, a preview, the open jobs, and what was asked of them.
    private FolderConversionPreviewResponse _quick = new() { TotalFiles = 2140, VideoFiles = 2140, EstimatedSeconds = 10700, DuplicatePolicy = "keep_one" };
    private FolderConversionPreviewResponse _preview = new()
    {
        TotalFiles = 2140,
        Examined = 500,
        Complete = false,
        ToRename = 410,
        AlreadyCorrect = 60,
        DuplicatesToRemove = 12,
        NeedsReview = 15,
        NameCollisions = 3,
        Skipped = 0,
        EstimatedSeconds = 10700,
        DuplicatePolicy = "keep_one",
        Samples = [new() { OldPath = "raw/Dune.2021.mkv", NewPath = "Movies/Dune (2021)/Dune (2021).mkv" }],
    };
    // The Engine's side of merging a folder: a quick count, a preview, and what was asked of it.
    private FolderMergePreviewResponse _mergeQuick = new()
    {
        TotalFiles = 12,
        VideoFiles = 12,
        SourceBytes = 48L * 1024 * 1024 * 1024,
        TargetFreeBytes = 900L * 1024 * 1024 * 1024,
        EstimatedSeconds = 3000,
        DuplicatePolicy = "keep_one",
    };
    private FolderMergePreviewResponse _mergePreview = new()
    {
        TotalFiles = 12,
        Examined = 12,
        Complete = true,
        ToTransfer = 9,
        AlreadyInTarget = 1,
        NeedsReview = 2,
        EstimatedSeconds = 3000,
        DuplicatePolicy = "keep_one",
        Samples = [new() { OldPath = "raw/Dune.2021.mkv", NewPath = "Movies/Dune (2021)/Dune (2021).mkv" }],
    };
    private readonly List<FolderMergeRequest> _merges = [];
    private List<FolderJobStatusDto> _jobs = [];
    private readonly List<string> _converted = [];
    private readonly List<(string Id, string Action)> _controls = [];
    private readonly List<ExcludedFileDto> _excluded = [];
    private IRenderedComponent<AppPopoverHost> _popovers = null!;
    // The Engine refusing a library change (a duplicate name, say) with its own sentence; null means it saves.
    private string? _refuseSave;
    private string? _lastError;
    // What the library actions for Unmatched files were asked, and how many files the Engine says they changed.
    private readonly List<(string Action, string LibraryId)> _unmatchedActions = [];
    private int _unmatchedAffected = 3;
    // The folders Keep going was chosen for; the Engine drops the first check line once it has accepted that.
    private readonly List<string> _keptGoing = [];

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
                Id = TvId, Name = "TV Shows", KindKey = "tv", Area = "watch", MediaTypes = ["TV"],
                OrganizationPolicy = new() { Mode = "tuvima_standard", WriteTags = false },
                PrimaryDestinationSourceId = "tv-main",
                Sources = [new() { Id = "tv-main", Path = "E:\\Library\\TV", ManagementMode = "managed_by_tuvima", AccessMode = "writable" }],
            },
            new()
            {
                Id = AudiobooksId, Name = "Audiobooks", KindKey = "audiobooks", Area = "listen", MediaTypes = ["Audiobooks"],
                Sources = [new() { Id = "audio-old", Path = "C:\\media\\Audiobooks", ManagementMode = "existing_library", AccessMode = "read_only" }],
            },
        ],
    };

    public LibrarySettingsFlowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(_configuration));
            stub.SetHandler(nameof(IEngineApiClient.GetSetupLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(_configuration));
            stub.SetHandler(nameof(IEngineApiClient.GetImportFolderWaitingAsync), _ => Task.FromResult(_waiting));
            stub.SetHandler(nameof(IEngineApiClient.TestPathAsync), args => Task.FromResult<PathTestResultDto?>(_pathProbe((string)args![0]!)));
            stub.SetHandler(nameof(IEngineApiClient.UpdateLibrariesAsync), args =>
            {
                _saves++;
                _lastUpdate = (UpdateLibrariesRequest)args![0]!;
                _configuration = new LibrariesConfigurationDto { StorageLocations = _lastUpdate.StorageLocations, Libraries = _lastUpdate.Libraries, FileHandling = _lastUpdate.FileHandling };
                return Task.FromResult<LibrariesConfigurationDto?>(_configuration);
            });
            stub.SetHandler("get_LastError", _ => _lastError);
            stub.SetHandler(nameof(IEngineApiClient.RetryUnmatchedLibraryAsync), args => RetryAnswerAsync((string)args![0]!));
            stub.SetHandler(nameof(IEngineApiClient.KeepAllUnmatchedLibraryAsync), args =>
            {
                _unmatchedActions.Add(("keep", (string)args![0]!));
                return Task.FromResult<MediaEngine.Contracts.Library.UnmatchedActionResultDto?>(new() { Affected = _unmatchedAffected });
            });
            stub.SetHandler(nameof(IEngineApiClient.KeepGoingAsync), args =>
            {
                _keptGoing.Add((string)args![0]!);
                foreach (var source in _configuration.Libraries.SelectMany(library => library.Sources).Where(source => source.Id == (string)args[0]!))
                {
                    source.FirstCheck = null;
                }

                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.MutateLibraryAsync), args =>
            {
                if (_refuseSave is not null)
                {
                    _lastError = _refuseSave;
                    return Task.FromResult<LibrariesConfigurationDto?>(null);
                }

                _saves++;
                _lastRequest = (LibraryMutationRequest)args![0]!;
                _lastLibrary = _lastRequest.Library;
                // Mimic the Engine's Normalize: keep a managed marker, else the earliest managed folder, else none.
                var managed = _lastLibrary!.Sources.Where(x => x.ManagementMode == "managed_by_tuvima").ToList();
                if (!managed.Any(x => x.Id == _lastLibrary.PrimaryDestinationSourceId))
                {
                    _lastLibrary.PrimaryDestinationSourceId = managed.FirstOrDefault()?.Id;
                }
                var libraries = _configuration.Libraries.Select(library => library.Id == _lastLibrary!.Id ? _lastLibrary : library).ToList();
                _configuration = new LibrariesConfigurationDto { StorageLocations = _configuration.StorageLocations, Libraries = libraries, FileHandling = _configuration.FileHandling };
                return Task.FromResult<LibrariesConfigurationDto?>(_configuration);
            });
            stub.SetHandler(nameof(IEngineApiClient.PreviewFolderConversionAsync), args =>
                Task.FromResult<FolderConversionPreviewResponse?>(((FolderConversionPreviewRequest)args![0]!).Quick ? _quick : _preview));
            stub.SetHandler(nameof(IEngineApiClient.PreviewFolderMergeAsync), args =>
                Task.FromResult<FolderMergePreviewResponse?>(((FolderMergePreviewRequest)args![0]!).Quick ? _mergeQuick : _mergePreview));
            stub.SetHandler(nameof(IEngineApiClient.MergeFolderAsync), args =>
            {
                var request = (FolderMergeRequest)args![0]!;
                _merges.Add(request);
                var source = _configuration.Libraries.SelectMany(library => library.Sources).Single(item => item.Id == request.SourceId);
                source.MergeState = "merging";
                source.MergeOperationId = Guid.NewGuid().ToString("D");
                source.MergeTargetSourceId = request.TargetSourceId;
                source.MergeMode = request.Mode;
                var job = new FolderJobStatusDto { OperationId = source.MergeOperationId, SourceId = source.Id, JobType = "merge_folder", State = "queued", ItemsTotal = _mergeQuick.TotalFiles };
                _jobs = [.. _jobs.Where(item => item.SourceId != source.Id), job];
                return Task.FromResult<FolderJobStatusDto?>(job);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetFolderJobsAsync), _ => Task.FromResult<IReadOnlyList<FolderJobStatusDto>>(_jobs));
            stub.SetHandler(nameof(IEngineApiClient.GetExcludedFilesAsync), args => Task.FromResult<IReadOnlyList<ExcludedFileDto>>(_excluded.Where(file => file.LibraryId == (string?)args![0]).ToList()));
            stub.SetHandler(nameof(IEngineApiClient.ConvertFolderAsync), args =>
            {
                var id = (string)args![0]!;
                _converted.Add(id);
                var source = _configuration.Libraries.SelectMany(library => library.Sources).Single(item => item.Id == id);
                source.ManagementMode = "managed_by_tuvima";
                source.AccessMode = "writable";
                source.ConversionState = "converting";
                source.ConversionOperationId = Guid.NewGuid().ToString("D");
                var job = new FolderJobStatusDto { OperationId = source.ConversionOperationId, SourceId = id, State = "queued", ItemsTotal = _quick.TotalFiles };
                _jobs = [.. _jobs.Where(item => item.SourceId != id), job];
                return Task.FromResult<FolderJobStatusDto?>(job);
            });
            stub.SetHandler(nameof(IEngineApiClient.ControlFolderJobAsync), args =>
            {
                var id = (string)args![0]!;
                var action = (string)args![1]!;
                _controls.Add((id, action));
                var job = _jobs.Single(item => item.OperationId == id);
                job.State = action switch { "pause" => "paused", "resume" => "running", _ => "cancelled" };
                return Task.FromResult<FolderJobStatusDto?>(job);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetArtworkWritebackSettingsAsync), _ => Task.FromResult<ArtworkWritebackSettingsDto?>(new ArtworkWritebackSettingsDto(true, true)));
            stub.SetHandler(nameof(IEngineApiClient.GetOrganizationTemplateAsync), _ => Task.FromResult<OrganizationTemplateDto?>(
                new OrganizationTemplateDto("{Category}/{Title}{Ext}", null, new() { ["Movies"] = "Movies/{Title} ({Year})/{Title} ({Year}){Ext}", ["TV"] = "TV/{Series}/{Title}{Ext}" })));
            stub.SetHandler(nameof(IEngineApiClient.PreviewOrganizationTemplateAsync), args =>
            {
                var template = (string)args![0]!;
                return Task.FromResult<OrganizationTemplateDto?>(template.Contains("{Bad") ? null : new OrganizationTemplateDto(template, "Movies/Dune (2021)/Dune (2021).mkv"));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetServerFolderRootsAsync), _ => Task.FromResult<IReadOnlyList<ServerStorageLocationDto>>(_configuration.StorageLocations));
            stub.SetHandler(nameof(IEngineApiClient.BrowseServerFoldersAsync), _ => Task.FromResult<BrowseServerFoldersResultDto?>(new() { DisplayPath = "C:\\media" }));
            stub.SetHandler(nameof(IEngineApiClient.ValidateServerFolderAsync), _ => Task.FromResult<ServerFolderValidationResultDto?>(new() { Path = "C:\\media", StorageLocationId = "media", Exists = true, HasRead = true, HasWrite = true, CanSelect = true }));
        }));
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.LibraryKindsState>();
        _popovers = Render<AppPopoverHost>();
    }

    // Each type has its own page (/settings/libraries/{group}/{type}); this opens the one for a type's label.
    private static readonly Dictionary<string, (string Group, string Type)> TypePages = new()
    {
        ["Movies"] = ("watch", "movies"),
        ["TV Shows"] = ("watch", "tv"),
        ["Videos"] = ("watch", "other_videos"),
        ["Music"] = ("listen", "music"),
        ["Audiobooks"] = ("listen", "audiobooks"),
        ["Books"] = ("read", "books"),
        ["Comics"] = ("read", "comics"),
    };

    // The folders panel as a library page hosts it: the type's own page, with no type list.
    private IRenderedComponent<LibraryFoldersPanel> Open(string typeLabel, bool readOnly = false)
    {
        var (_, type) = TypePages[typeLabel];
        var cut = Render<LibraryFoldersPanel>(parameters => parameters
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog)
            .Add(x => x.TypeKey, type)
            .Add(x => x.ReadOnly, readOnly));
        cut.WaitForElement(".folder-table, .library-folders__empty");
        return cut;
    }

    private IRenderedComponent<LibraryFoldersPanel> OpenImport()
    {
        var cut = Render<LibraryFoldersPanel>(parameters => parameters
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog)
            .Add(x => x.TypeKey, "import"));
        cut.WaitForElement("#library-import-title");
        return cut;
    }

    private async Task<MediaEngine.Contracts.Library.UnmatchedActionResultDto?> RetryAnswerAsync(string libraryId)
    {
        _unmatchedActions.Add(("retry", libraryId));
        if (_retryGate is not null)
        {
            await _retryGate.Task;
        }

        return new MediaEngine.Contracts.Library.UnmatchedActionResultDto { Affected = _unmatchedAffected };
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    // The overview has the four area tabs; the selected one lists its libraries with their folder counts, and the Import folder is one line under them.
    [Fact]
    public void OverviewListsTheAreaTabsAndTheSelectedAreasLibrariesWithTheirFolderCount()
    {
        var cut = Render<LibrariesTab>();
        cut.WaitForElement(".libs__row");
        Assert.Equal(["Watch", "Listen", "Read", "View"], cut.FindAll("[role=tab]").Select(Text).ToArray());
        Assert.Contains("3 folders", Text(cut.Find($".libs__row[data-library='{MoviesId}']")));

        var listen = Render<LibrariesTab>(parameters => parameters.AddCascadingValue(RoutedPageQuery.CascadingName, "http://localhost/settings/libraries?area=listen"));
        listen.WaitForElement(".libs__row");
        Assert.Contains("1 folder", Text(listen.Find($".libs__row[data-library='{AudiobooksId}']")));
        Assert.Contains("Import folder", Text(cut.Find(".libs__note")));
    }

    private static ImportFolderStatusDto ConfiguredImport() => new()
    {
        Configured = true,
        Path = "C:\\media\\Import",
        Destinations =
        [
            new() { MediaType = "Movies", LibraryId = MoviesId, DestinationSourceId = "movies-main", Path = "E:\\Library\\Movies" },
            new() { MediaType = "TV Shows", LibraryId = TvId, DestinationSourceId = "tv-main", Path = "E:\\Library\\TV" },
            new() { MediaType = "Audiobooks" },
        ],
    };

    // I1 to I3: the front door has one Import folder row that reads Not set; its page shows only the Set button with no type lines.
    [Fact]
    public void ImportFolderIsOneRowOnTheFrontDoorAndShowsOnlySetWhenNotSet()
    {
        var front = Render<LibrariesTab>();
        front.WaitForElement(".libs__note a");
        Assert.Equal("Import folder", Text(front.Find(".libs__note a")));
        Assert.Contains("not set", Text(front.Find(".libs__note")));
        Assert.Equal("/settings/libraries/import", front.Find(".libs__note a").GetAttribute("href"));
        var cut = OpenImport();
        cut.WaitForAssertion(() => Assert.Contains("Set import folder", cut.Markup));
        Assert.Empty(cut.FindAll(".library-folders__destination"));
        Assert.DoesNotContain("Where imported files go", cut.Markup);
        foreach (var banned in new[] { "drop-off", "Ready", "Waiting for destinations", "Needs destination" })
        {
            Assert.False(Regex.IsMatch(cut.Markup, $@"\b{Regex.Escape(banned)}\b", RegexOptions.IgnoreCase), banned);
        }
    }

    // I2: once set, the folder row and one line per type appear; a type without a managed folder gets a Choose action.
    [Fact]
    public void ImportPanelShowsTheFolderRowAndOneLinePerType()
    {
        _configuration.ImportFolderStatus = ConfiguredImport();
        var front = Render<LibrariesTab>();
        front.WaitForElement(".libs__note a");
        Assert.Contains("set", Text(front.Find(".libs__note")));
        Assert.DoesNotContain("not set", Text(front.Find(".libs__note")));
        var cut = OpenImport();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".library-folders__destination").Count));
        Assert.Contains("C:\\media\\Import", cut.Find(".library-folders__table-row").TextContent);
        Assert.Contains("Where imported files go", cut.Markup);
        var lines = cut.FindAll(".library-folders__destination").Select(Text).ToList();
        Assert.Contains(lines, line => line.StartsWith("Movies") && line.Contains("Library Movies") && line.Contains("E:\\Library\\Movies"));
        Assert.Contains(lines, line => line.StartsWith("Audiobooks") && line.Contains("Add a managed folder to import Audiobooks") && line.Contains("Choose"));
        // Merge into now exists, so the panel says when to use it instead of the Import folder.
        Assert.True(LibraryFolderTable.HasMergeAction);
        Assert.Contains("To bring in a whole existing folder once, use Merge into on a managed folder.", cut.Markup);
    }

    // On the Import folder's own address there is no type list, so Choose opens that type's page instead.
    [Fact]
    public void ChooseOnAnImportLineOnItsOwnPageOpensThatTypesAddress()
    {
        _configuration.ImportFolderStatus = ConfiguredImport();
        var cut = OpenImport();
        cut.WaitForElement(".library-folders__destination");
        cut.FindAll(".library-folders__destination").Single(x => x.TextContent.Contains("Audiobooks")).QuerySelector("button")!.Click();
        Assert.EndsWith($"/settings/libraries/{AudiobooksId}?highlight=add", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    // A type whose files cannot be filed shows the Engine's own sentence under its line.
    [Fact]
    public void ImportLineShowsTheEnginesSentenceAndFolderUnavailable()
    {
        _configuration.ImportFolderStatus = ConfiguredImport();
        _waiting = new MediaEngine.Contracts.Ingestion.ImportFolderWaitingResponse
        {
            Configured = true,
            Waiting =
            [
                new() { MediaType = "TV Shows", Folder = "TV", FileCount = 2, Reason = "folder_unavailable", Message = "TV Shows are staying in the Import folder because the New media folder is offline." },
            ],
        };
        var cut = OpenImport();
        cut.WaitForAssertion(() =>
        {
            var tv = cut.FindAll(".library-folders__destination").Single(x => x.TextContent.StartsWith("TV Shows"));
            Assert.Contains("Folder unavailable", tv.TextContent);
            Assert.Contains("because the New media folder is offline", tv.TextContent);
        });
    }

    // Files that stayed on disk when their title was deleted are listed under the type's folders, only for that type, and only when there are any.
    [Fact]
    public void ExcludedFilesAreListedUnderTheirTypesFoldersOnly()
    {
        _excluded.Add(new() { Id = Guid.NewGuid(), LibraryId = MoviesId, FileName = "Dune.mkv", FolderLabel = "Movies", ExcludedAt = DateTimeOffset.UtcNow.AddMinutes(-5), ExcludedBy = "Shy" });

        var movies = Open("Movies");
        movies.WaitForAssertion(() => Assert.Contains("Dune.mkv", Text(movies.Find(".excluded-files"))));
        Assert.Contains("Add back", Text(movies.Find(".excluded-files")));

        var tv = Open("TV Shows");
        tv.WaitForElement(".folder-table");
        Assert.Empty(tv.FindAll(".excluded-files"));
    }

    // The last folder can be removed while files are still excluded; the list stays so they can be added back.
    [Fact]
    public void ExcludedFilesStayListedWhenTheLibraryHasNoFoldersLeft()
    {
        _configuration.Libraries.Single(library => library.Id == MoviesId).Sources.Clear();
        _excluded.Add(new() { Id = Guid.NewGuid(), LibraryId = MoviesId, FileName = "Dune.mkv", FolderLabel = "Movies", ExcludedAt = DateTimeOffset.UtcNow.AddMinutes(-5), ExcludedBy = "Shy" });

        var movies = Open("Movies");

        Assert.NotEmpty(movies.FindAll(".library-folders__empty"));
        movies.WaitForAssertion(() => Assert.Contains("Dune.mkv", Text(movies.Find(".excluded-files"))));
    }

    // A1: the per-library page and its link are gone.
    [Fact]
    public void NoLibrarySettingsLinkRemains()
    {
        var cut = Open("Movies");
        Assert.DoesNotContain("Library settings", cut.Markup);
        Assert.DoesNotContain("/settings/libraries/" + MoviesId, cut.Markup);
        // A library's own page is read on a phone (counts and status, nothing to change).
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Libraries, MoviesId));
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Libraries, "view"));
    }

    // A2: four columns plus the actions menu, one row per folder, no numbering, arrows or drag handles.
    [Fact]
    public void FolderTableHasFourColumnsAndAMenuPerFolder()
    {
        var cut = Open("Movies");
        Assert.Equal(["Folder", "New media", "Files", "Actions"], cut.FindAll(".folder-table__head [role=columnheader]").Select(Text));
        var rows = cut.FindAll(".folder-table__row");
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.NotNull(row.QuerySelector(".folder-table__icon"));
            Assert.NotNull(row.QuerySelector(".folder-table__marker"));
            Assert.NotNull(row.QuerySelector(".folder-table__files"));
            Assert.NotNull(row.QuerySelector(".folder-table__actions"));
        });
        Assert.DoesNotContain("draggable", cut.Find(".folder-table").InnerHtml);
        Assert.Contains("Library Movies", Text(rows[0]));
        Assert.Contains("E:\\Library\\Movies", Text(rows[0]));
    }

    // A3: exactly one managed folder holds the marker; tapping another managed folder moves it and saves; read-only folders have no radio.
    [Fact]
    public void NewMediaMarkerSitsOnOneManagedFolderAndMovesOnTap()
    {
        var cut = Open("Movies");
        var marked = cut.FindAll(".folder-table__marker.is-marked");
        Assert.Single(marked);
        Assert.Contains("New media goes here", marked[0].TextContent);
        var rows = cut.FindAll(".folder-table__row");
        Assert.Empty(rows[2].QuerySelectorAll(".folder-table__radio"));
        Assert.Equal("—", Text(rows[2].QuerySelector(".folder-table__marker")!));

        rows[1].QuerySelector(".folder-table__radio")!.Click();
        cut.WaitForAssertion(() => Assert.Equal("movies-plex", _lastLibrary?.PrimaryDestinationSourceId));
        cut.WaitForAssertion(() => Assert.Contains("is-marked", cut.FindAll(".folder-table__row")[1].ClassList));
        Assert.Single(cut.FindAll(".folder-table__marker.is-marked"));
        Assert.Equal(1, _saves);
    }

    // A4: a type with no managed folder says how to fix it.
    [Fact]
    public void TypeWithoutAManagedFolderExplainsWhatToAdd()
    {
        var audiobooks = Open("Audiobooks");
        Assert.Contains("Add a managed folder to import or upload Audiobooks.", audiobooks.Markup);
        Assert.Empty(audiobooks.FindAll(".folder-table__marker.is-marked"));
        Assert.DoesNotContain("Add a managed folder to import or upload", Open("Movies").Markup);
    }

    // Switching a read-only folder that has files to Managed asks honestly first, then queues one organising job.
    [Fact]
    public async Task SwitchingReadOnlyToManagedExplainsThenConvertsTheFolder()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        await SwitchHandlingAsync(cut, 2);
        dialogs.WaitForAssertion(() => Assert.Contains("Let Library manage F:\\Movies?", dialogs.Markup));
        Assert.Equal(0, _saves);
        Assert.Empty(_converted);
        await ClickButtonAsync(dialogs, "Convert");
        cut.WaitForAssertion(() => Assert.Equal(["movies-old"], _converted));
        // The Engine makes the folder Managed itself, in the same step: the Dashboard never saves the mode first.
        Assert.Equal(0, _saves);
        cut.WaitForAssertion(() => Assert.Contains("Converting", IconLabel(cut.FindAll(".folder-table__row")[2])));
    }

    // The confirmation says what the plan promises: renames, hours, other apps, no automatic undo, and the duplicate rule.
    [Fact]
    public async Task TheConfirmationIsHonestAboutRenamingTimeOtherAppsAndUndo()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        await SwitchHandlingAsync(cut, 2);
        dialogs.WaitForAssertion(() => Assert.Contains("2,140 files", dialogs.Markup));
        var text = Text(dialogs.Find(".convert-folder"));
        Assert.Contains("rename and reorganise the 2,140 files", text);
        Assert.Contains("write tags where the file type allows it", text);
        Assert.Contains("exact duplicate copies", text);
        Assert.Contains("Keep one copy", text);
        Assert.Contains("This can take hours for a large folder", text);
        Assert.Contains("about 3 h here, based on 2,140 files", text);
        Assert.Contains("You can keep using Library while it runs", text);
        Assert.Contains("Plex, Jellyfin", text);
        Assert.Contains("This cannot be undone automatically", text);
        Assert.Contains("full log of every move", text);
        Assert.Contains("Nothing is ever deleted except exact duplicate copies", text);
        var buttons = dialogs.FindAll("button").Select(Text).ToList();
        Assert.Contains("Preview first", buttons);
        Assert.Contains("Convert", buttons);
        Assert.Contains("Keep read-only", buttons);
    }

    // Preview first shows the counts and examples, changes nothing, and still offers Convert and Keep read-only.
    [Fact]
    public async Task PreviewFirstShowsWhatWouldChangeWithoutChangingAnything()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        await SwitchHandlingAsync(cut, 2);
        dialogs.WaitForElement(".convert-folder");
        dialogs.FindAll("button").Single(x => Text(x) == "Preview first").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Nothing has changed yet", dialogs.Markup));
        var text = Text(dialogs.Find(".convert-folder"));
        Assert.Contains("Looked at the first 500 of 2,140 files", text);
        Assert.Contains("410 to rename or move", text);
        Assert.Contains("60 already correct", text);
        Assert.Contains("12 exact duplicate copies to remove", text);
        Assert.Contains("18 will need Review", text);
        Assert.Contains("Movies/Dune (2021)/Dune (2021).mkv", text);
        Assert.Contains("Scan everything", dialogs.FindAll("button").Select(Text));
        Assert.Contains("Convert", dialogs.FindAll("button").Select(Text));
        Assert.Equal(0, _saves);
        Assert.Empty(_converted);
        dialogs.FindAll("button").Single(x => Text(x) == "Keep read-only").Click();
        Assert.Empty(_converted);
    }

    // A folder with nothing in it just asks plainly; there is no job to run.
    [Fact]
    public async Task AnEmptyFolderIsSwitchedWithoutAnOrganisingJob()
    {
        _quick = new() { TotalFiles = 0, DuplicatePolicy = "keep_one" };
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        await SwitchHandlingAsync(cut, 2);
        dialogs.WaitForAssertion(() => Assert.Contains("no media files in this folder yet", dialogs.Markup));
        await ClickButtonAsync(dialogs, "Let Library manage it");
        cut.WaitForAssertion(() => Assert.Equal("managed_by_tuvima", _lastLibrary?.Sources.Single(x => x.Id == "movies-old").ManagementMode));
        Assert.Empty(_converted);
    }

    // The folder row shows the job: the folder icon says Converting, one progress line with a time left, and Pause and Cancel.
    [Fact]
    public void ARunningJobShowsProgressOnTheFolderRowWithPauseAndCancel()
    {
        _configuration.Libraries[0].Sources[1].ConversionState = "converting";
        _configuration.Libraries[0].Sources[1].ConversionOperationId = "job-1";
        _jobs = [new() { OperationId = "job-1", SourceId = "movies-plex", State = "running", ItemsTotal = 8310, ItemsCompleted = 1240, ProgressPercent = 15, EstimatedSecondsRemaining = 9000 }];
        var cut = Open("Movies");
        var row = cut.FindAll(".folder-table__row")[1];
        cut.WaitForAssertion(() => Assert.Contains("Organising 1,240 of 8,310", Text(row.QuerySelector(".folder-job")!)));
        Assert.Contains("about 2.5 h left", Text(row.QuerySelector(".folder-job")!));
        Assert.Contains("Converting", IconLabel(row));
        Assert.NotNull(row.QuerySelector(".folder-job [role=progressbar]"));
        var buttons = row.QuerySelectorAll(".folder-job button").Select(Text).ToList();
        Assert.Equal(["Pause", "Cancel"], buttons);

        row.QuerySelectorAll(".folder-job button").Single(x => Text(x) == "Pause").Click();
        cut.WaitForAssertion(() => Assert.Equal([("job-1", "pause")], _controls));
        cut.WaitForAssertion(() => Assert.Contains("Paused at 1,240 of 8,310", Text(cut.FindAll(".folder-table__row")[1].QuerySelector(".folder-job")!)));
        Assert.Contains("Resume", cut.FindAll(".folder-table__row")[1].QuerySelectorAll(".folder-job button").Select(Text));
    }

    [Fact]
    public async Task CancellingAJobAsksFirstAndKeepsTheFolderManaged()
    {
        var dialogs = Render<AppDialogHost>();
        _configuration.Libraries[0].Sources[1].ConversionState = "converting";
        _jobs = [new() { OperationId = "job-1", SourceId = "movies-plex", State = "running", ItemsTotal = 100, ItemsCompleted = 10 }];
        var cut = Open("Movies");
        cut.WaitForElement(".folder-job");
        await cut.ClickAsync(page => page.Find(".folder-job button[aria-label='Cancel organising Plex Movies']"));
        dialogs.WaitForAssertion(() => Assert.Contains("Stop organising Plex Movies?", dialogs.Markup));
        Assert.Contains("the rest will be tidied gradually by normal scans", dialogs.Markup);
        Assert.Empty(_controls);
        await ClickButtonAsync(dialogs, "Keep going");
        Assert.Empty(_controls);

        await cut.ClickAsync(page => page.Find(".folder-job button[aria-label='Cancel organising Plex Movies']"));
        dialogs.WaitForAssertion(() => Assert.Contains("Stop organising Plex Movies?", dialogs.Markup));
        await ClickButtonAsync(dialogs, "Stop organising");
        cut.WaitForAssertion(() => Assert.Equal([("job-1", "cancel")], _controls));
    }

    [Fact]
    public void ASecondJobSaysWhichFolderItIsWaitingBehind()
    {
        _configuration.Libraries[0].Sources[0].ConversionState = "converting";
        _configuration.Libraries[0].Sources[1].ConversionState = "converting";
        _jobs =
        [
            new() { OperationId = "job-1", SourceId = "movies-main", State = "running", ItemsTotal = 100, ItemsCompleted = 10 },
            new() { OperationId = "job-2", SourceId = "movies-plex", State = "queued", ItemsTotal = 50, QueuedBehind = "Library Movies" },
        ];
        var cut = Open("Movies");

        cut.WaitForAssertion(() => Assert.Contains("Queued behind Library Movies", Text(cut.FindAll(".folder-table__row")[1].QuerySelector(".folder-job")!)));
    }

    // When a job ends the row says how it went and links the log of every move.
    [Fact]
    public void AFinishedJobSaysHowItWentAndLinksTheLog()
    {
        _jobs = [new() { OperationId = "job-9", SourceId = "movies-plex", State = "completed", ItemsTotal = 8310, ItemsCompleted = 8310, ItemsFailed = 20, ProgressPercent = 100, HasLog = true, Message = "8,290 organised, 20 could not be moved - see log." }];
        var cut = Open("Movies");
        var row = cut.FindAll(".folder-table__row")[1];

        cut.WaitForAssertion(() => Assert.Contains("8,290 organised, 20 could not be moved", Text(row.QuerySelector(".folder-job")!)));
        Assert.Equal("/engine-folder-log/job-9", row.QuerySelector(".folder-job a")!.GetAttribute("href"));
        Assert.Empty(row.QuerySelectorAll(".folder-job button"));
        Assert.Contains("Managed", IconLabel(row));
    }

    // A managed folder can be organised on request, with the same honest question.
    [Fact]
    public async Task OrganiseFilesNowOnAManagedFolderAsksThenQueuesTheJob()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        await OpenMenuItemAsync(cut, 0, "Organise files now");
        dialogs.WaitForAssertion(() => Assert.Contains("Organise E:\\Library\\Movies now?", dialogs.Markup));
        Assert.Contains("This cannot be undone automatically", dialogs.Markup);
        dialogs.FindAll("button").Single(x => Text(x) == "Organise now").Click();
        cut.WaitForAssertion(() => Assert.Equal(["movies-main"], _converted));
    }

    // ── Merge into ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MergeIntoIsOfferedForEveryFolderExceptTheNewMediaFolderAndOnlyWhenAnotherManagedFolderCanReceive()
    {
        var movies = _configuration.Libraries[0];
        var main = movies.Sources[0];
        var plex = movies.Sources[1];
        var old = movies.Sources[2];

        Assert.False(LibraryFolderTable.CanMergeInto(movies, main, jobOpen: false));
        Assert.True(LibraryFolderTable.CanMergeInto(movies, plex, jobOpen: false));
        Assert.True(LibraryFolderTable.CanMergeInto(movies, old, jobOpen: false));
        Assert.False(LibraryFolderTable.CanMergeInto(movies, plex, jobOpen: true));
        plex.ConversionState = "converting";
        Assert.False(LibraryFolderTable.CanMergeInto(movies, plex, jobOpen: false));
        plex.ConversionState = "none";
        old.MergeState = "merging";
        Assert.False(LibraryFolderTable.CanMergeInto(movies, old, jobOpen: false));
        old.MergeState = "merged";
        Assert.False(LibraryFolderTable.CanMergeInto(movies, old, jobOpen: false));
        old.MergeState = "none";

        // The only managed folder of a type has nowhere to merge into, and a merged-away folder is never a target.
        var tv = _configuration.Libraries[1];
        Assert.False(LibraryFolderTable.CanMergeInto(tv, tv.Sources[0], jobOpen: false));
        Assert.Equal(["movies-main", "movies-plex"], LibraryFolderTable.MergeTargets(movies, old).Select(x => x.Id));
        plex.MergeState = "merged";
        Assert.Equal(["movies-main"], LibraryFolderTable.MergeTargets(movies, old).Select(x => x.Id));
        plex.MergeState = "merging";
        Assert.Equal(["movies-main"], LibraryFolderTable.MergeTargets(movies, old).Select(x => x.Id));
    }

    [Fact]
    public void MergeIntoShowsInTheRowMenuNextToOrganiseFilesNow()
    {
        var cut = Open("Movies");
        cut.FindAll(".folder-table__row")[1].QuerySelector(".folder-table__menu-trigger")!.Click();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role=menuitem]"), x => Text(x) == "Merge into…"));
        var labels = cut.FindAll("[role=menuitem]").Select(Text).ToList();
        Assert.True(labels.IndexOf("Merge into…") == labels.IndexOf("Organise files now") + 1);
    }

    [Fact]
    public async Task TheMergeSheetPreselectsTheNewMediaFolderAndCopyAndStartsTheJob()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        await OpenMenuItemAsync(cut, 2, "Merge into…");
        dialogs.WaitForAssertion(() => Assert.Contains("Merge Old drive into a managed folder", dialogs.Markup));
        dialogs.WaitForAssertion(() => Assert.Contains("12 files", Text(dialogs.Find(".merge-folder__facts"))));
        Assert.Contains("48 GB", Text(dialogs.Find(".merge-folder__facts")));

        // Both managed folders are offered, with the New media folder chosen; the folder itself is not.
        var targets = dialogs.FindAll("[role=radiogroup][aria-label='Merge into'] [role=radio]");
        Assert.Equal(2, targets.Count);
        Assert.Contains("Library Movies", Text(targets[0]));
        Assert.Contains("New media folder", Text(targets[0]));
        Assert.Equal("true", targets[0].GetAttribute("aria-checked"));
        Assert.Equal("false", targets[1].GetAttribute("aria-checked"));
        Assert.Equal("true", dialogs.FindAll("[role=radiogroup][aria-label='Copy or move'] [role=radio]")[0].GetAttribute("aria-checked"));

        // A read-only folder can only be copied.
        Assert.DoesNotContain("Move instead of copy", dialogs.Markup);
        Assert.Contains("Move is not offered because this folder is read-only", dialogs.Markup);
        Assert.Contains("Your folder is never changed", dialogs.Markup);
        Assert.Empty(_merges);

        dialogs.FindAll("button").Single(x => Text(x) == "Merge").Click();
        cut.WaitForAssertion(() => Assert.Single(_merges));
        Assert.Equal(("movies-old", "movies-main", "copy"), (_merges[0].SourceId, _merges[0].TargetSourceId, _merges[0].Mode));
        cut.WaitForAssertion(() => Assert.Contains("Merging", IconLabel(cut.FindAll(".folder-table__row")[2])));
    }

    [Fact]
    public async Task AManagedFolderCanBeMovedAndThePersonCanPickAnotherTarget()
    {
        _configuration.Libraries[0].Sources.Add(new() { Id = "movies-third", Path = "G:\\Films", Notes = "Third", ManagementMode = "managed_by_tuvima", AccessMode = "writable" });
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        await OpenMenuItemAsync(cut, 1, "Merge into…");
        dialogs.WaitForAssertion(() => Assert.Contains("Merge Plex Movies into a managed folder", dialogs.Markup));
        dialogs.WaitForAssertion(() => Assert.Contains("Move instead of copy", dialogs.Markup));
        Assert.Contains("drive D:", dialogs.Markup);
        Assert.Contains("only after its copy has been checked byte for byte", dialogs.Markup);

        dialogs.FindAll("[role=radiogroup][aria-label='Merge into'] [role=radio]").Single(x => Text(x).Contains("Third")).Click();
        dialogs.WaitForAssertion(() => Assert.Equal("true", dialogs.FindAll("[role=radiogroup][aria-label='Merge into'] [role=radio]").Single(x => Text(x).Contains("Third")).GetAttribute("aria-checked")));
        dialogs.FindAll("[role=radiogroup][aria-label='Copy or move'] [role=radio]").Single(x => Text(x).StartsWith("Move")).Click();
        dialogs.WaitForAssertion(() => Assert.Equal("true", dialogs.FindAll("[role=radiogroup][aria-label='Copy or move'] [role=radio]").Single(x => Text(x).StartsWith("Move")).GetAttribute("aria-checked")));
        dialogs.FindAll("button").Single(x => Text(x) == "Merge").Click();

        cut.WaitForAssertion(() => Assert.Single(_merges));
        Assert.Equal(("movies-plex", "movies-third", "move"), (_merges[0].SourceId, _merges[0].TargetSourceId, _merges[0].Mode));
    }

    [Fact]
    public async Task PreviewFirstShowsWhatWouldHappenWithoutStartingAnything()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        await OpenMenuItemAsync(cut, 1, "Merge into…");
        dialogs.WaitForAssertion(() => Assert.Contains("12 files", dialogs.Markup));

        dialogs.FindAll("button").Single(x => Text(x) == "Preview first").Click();

        dialogs.WaitForAssertion(() => Assert.Contains("Nothing has changed yet", dialogs.Markup));
        var counts = Text(dialogs.Find(".merge-folder__counts"));
        Assert.Contains("9 to copy to their standard names", counts);
        Assert.Contains("1 already in your library", counts);
        Assert.Contains("2 to the From merge folder, waiting in Review", counts);
        Assert.Contains("Movies/Dune (2021)/Dune (2021).mkv", dialogs.Markup);
        Assert.Empty(_merges);
    }

    [Fact]
    public async Task AMergeThatCannotStartBecauseOfRoomIsExplainedAndTheMergeButtonIsOff()
    {
        _mergeQuick = new() { TotalFiles = 3, SourceBytes = 90L * 1024 * 1024 * 1024, TargetFreeBytes = 10L * 1024 * 1024 * 1024, BlockedReason = "There is not enough room on the drive that holds the target folder.", DuplicatePolicy = "keep_one" };
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        await OpenMenuItemAsync(cut, 1, "Merge into…");

        dialogs.WaitForAssertion(() => Assert.Contains("not enough room on the drive", dialogs.Markup));
        Assert.True(dialogs.FindAll("button").Single(x => Text(x) == "Merge").HasAttribute("disabled"));
    }

    [Fact]
    public void AMergedFolderSaysWhereItWentAndOffersNoSecondMerge()
    {
        var when = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var old = _configuration.Libraries[0].Sources[2];
        old.MergeState = "merged";
        old.MergeTargetSourceId = "movies-main";
        old.MergeMode = "copy";
        old.MergedAt = when;
        var cut = Open("Movies");
        var row = cut.FindAll(".folder-table__row")[2];

        Assert.Contains($"Merged into Library Movies on {when.LocalDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture)}", Text(row));

        row.QuerySelector(".folder-table__menu-trigger")!.Click();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role=menuitem]"), x => Text(x) == "Remove folder"));
        Assert.DoesNotContain(cut.FindAll("[role=menuitem]"), x => Text(x) == "Merge into…");
    }

    [Fact]
    public void ARunningMergeShowsProgressOnTheSourceRowAndStoppingItAsksFirst()
    {
        var dialogs = Render<AppDialogHost>();
        var plex = _configuration.Libraries[0].Sources[1];
        plex.MergeState = "merging";
        plex.MergeOperationId = "merge-1";
        plex.MergeTargetSourceId = "movies-main";
        plex.MergeMode = "copy";
        _jobs = [new() { OperationId = "merge-1", SourceId = "movies-plex", JobType = "merge_folder", State = "running", ItemsTotal = 12, ItemsCompleted = 4, ProgressPercent = 33, EstimatedSecondsRemaining = 1500 }];
        var cut = Open("Movies");
        var row = cut.FindAll(".folder-table__row")[1];

        cut.WaitForAssertion(() => Assert.Contains("Merging 4 of 12", Text(row.QuerySelector(".folder-job")!)));
        Assert.Contains("about 25 min left", Text(row.QuerySelector(".folder-job")!));
        Assert.Contains("Merging", IconLabel(row));
        Assert.Equal(["Pause", "Cancel"], row.QuerySelectorAll(".folder-job button").Select(Text));

        cut.Find(".folder-job button[aria-label='Cancel merging Plex Movies']").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Stop merging Plex Movies?", dialogs.Markup));
        Assert.Contains("What was already copied stays in the target folder", dialogs.Markup);
        dialogs.FindAll("button").Single(x => Text(x) == "Stop merging").Click();
        cut.WaitForAssertion(() => Assert.Equal([("merge-1", "cancel")], _controls));
    }

    // Adding a folder (read-only, merge and managed, the Videos YouTube switch and the phone-footage hint) is tested in AddFolderWizardTests.

    // Changing a naming preset asks before touching existing files; Only new files keeps today's behaviour.
    [Fact]
    public void ANamingChangeAsksWhetherToApplyToExistingFilesAndNeverStartsAJobSilently()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        cut.FindAll(".library-advanced__type button").First(x => Text(x) == "Keep original names").Click();
        cut.WaitForAssertion(() => Assert.Equal("keep_original_names", _lastLibrary?.OrganizationPolicy.Mode));
        dialogs.WaitForAssertion(() => Assert.Contains("Apply the new naming to your existing Movies?", dialogs.Markup));
        Assert.Contains("4,280 files", dialogs.Markup);
        Assert.Contains("cannot be undone automatically", dialogs.Markup);
        Assert.Empty(_converted);
        dialogs.FindAll("button").Single(x => Text(x) == "Only new files").Click();
        Assert.Empty(_converted);
    }

    [Fact]
    public void ApplyToExistingFilesNowQueuesAJobForEachManagedFolder()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        cut.FindAll(".library-advanced__type button").First(x => Text(x) == "Keep original names").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Apply the new naming to your existing Movies?", dialogs.Markup));
        dialogs.FindAll("button").Single(x => Text(x) == "Apply to existing files now").Click();
        cut.WaitForAssertion(() => Assert.Equal(["movies-main", "movies-plex"], _converted));
    }

    // The tags switch is not a naming change, so it never asks.
    [Fact]
    public void ChangingOnlyTheTagsSettingDoesNotAskAboutExistingFiles()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        cut.FindAll(".library-advanced__type input[role=switch]").Single().Change(false);
        cut.WaitForAssertion(() => Assert.False(_lastLibrary?.OrganizationPolicy.WriteTags));
        Assert.DoesNotContain("Apply the new naming", dialogs.Markup);
        Assert.Empty(_converted);
    }

    [Fact]
    public async Task SwitchingBackToReadOnlyWarnsThatRenamedFilesKeepTheirNames()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        await SwitchHandlingAsync(cut, 1);
        dialogs.WaitForAssertion(() => Assert.Contains("Use D:\\Plex\\Movies as read-only?", dialogs.Markup));
        Assert.Contains("Library will stop organising this folder", dialogs.Markup);
        Assert.Contains("Files already renamed keep their new names", dialogs.Markup);
    }

    [Fact]
    public async Task KeepReadOnlyChangesNothing()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        await SwitchHandlingAsync(cut, 2);
        dialogs.WaitForAssertion(() => Assert.Contains("Let Library manage F:\\Movies?", dialogs.Markup));
        dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Keep read-only").Click();
        Assert.Equal(0, _saves);
        Assert.Empty(_converted);
    }

    // A6: the Files cell follows the type's tag setting.
    [Fact]
    public void FilesCellReadsNamedTaggedNamedOrNotTouched()
    {
        var movies = Open("Movies");
        Assert.Equal(["Named + tagged", "Named + tagged", "Not touched"], movies.FindAll(".folder-table__files-value").Select(Text));
        var tv = Open("TV Shows");
        Assert.Equal(["Named"], tv.FindAll(".folder-table__files-value").Select(Text));
    }

    // Where to change the naming is said in visible words under the value, never only in a hover title.
    [Fact]
    public void FilesCellSaysWhereToChangeItInVisibleText()
    {
        var movies = Open("Movies");
        Assert.All(movies.FindAll(".folder-table__files"), cell =>
        {
            Assert.Equal("Change in Advanced > Movies", Text(cell.QuerySelector(".folder-table__files-hint")!));
            Assert.False(cell.HasAttribute("title"));
        });
    }

    [Fact]
    public void SettingsPanelHasTheShortPhoneNote()
    {
        var cut = Open("Movies");
        Assert.Equal("Add or change folders on a computer or tablet.", Text(cut.Find(".library-folders__phone-note")));
    }

    // The table is announced properly: a named actions column, one labelled radio group per New media cell, one live region.
    [Fact]
    public void FolderTableNamesItsActionsColumnItsRadiosAndHasOneLiveRegion()
    {
        var cut = Open("Movies");
        var groups = cut.FindAll("[role=radiogroup][aria-label='Where new Movies go']");
        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Single(group.QuerySelectorAll("[role=radio]")));
        Assert.Empty(cut.FindAll(".folder-table__row [role=status]"));
        Assert.Single(cut.FindAll("[role=status]"));
        Assert.Empty(cut.Find(".sr-only[role=status]").TextContent.Trim());
        Assert.All(cut.FindAll(".folder-table__none"), none => Assert.Equal("img", none.GetAttribute("role")));
    }

    // The type's name is the Settings frame's title and last breadcrumb step; the page starts with the folders instead of repeating it as a heading, and has no type list.
    [Fact]
    public void ThePaneDoesNotRepeatTheSelectedLibrariesNameAsAHeading()
    {
        var cut = Open("Movies");
        Assert.Empty(cut.FindAll(".libraries nav[aria-label=Breadcrumb], .libraries h1, .libraries h2, .libraries h3"));
        Assert.Empty(cut.FindAll(".library-folders__detail h3"));
        Assert.DoesNotContain("Folders that make up", cut.Markup);
        Assert.NotEmpty(cut.FindAll(".library-folders__detail .folder-table"));
    }

    // The Recycle bin switch is the last thing in the pane (the stylesheet pins that block to the pane's bottom) and carries a bin icon.
    [Fact]
    public void TheRecycleBinSwitchSitsInAPinnedFooterWithABinIcon()
    {
        var cut = Open("Movies");
        var footer = cut.Find(".library-folders__recycle");
        Assert.Contains("Recycle bin", Text(footer));
        Assert.NotNull(footer.QuerySelector(".app-switch-row__icon"));
        Assert.Equal(footer.OuterHtml, cut.Find(".library-folders__detail .app-card__body").LastElementChild!.OuterHtml);
    }

    // Simpler access D7: every folder row of every type carries the switch, with the same tooltip, and the old library-level switch is gone.
    [Fact]
    public void EveryFolderRowOfEveryTypeHasASuitableForLimitedProfilesSwitchWithItsTooltip()
    {
        AddNamedVideoLibraries();
        _configuration.Libraries.Add(new() { Id = "77777777-7777-4777-8777-777777777771", Name = "Music", KindKey = "music", Area = "listen", MediaTypes = ["Music"], Sources = [new() { Id = "music-1", Path = "C:\\media\\Music" }] });
        _configuration.Libraries.Add(new() { Id = "77777777-7777-4777-8777-777777777772", Name = "Books", KindKey = "books", Area = "read", MediaTypes = ["Books"], Sources = [new() { Id = "books-1", Path = "C:\\media\\Books" }] });
        _configuration.Libraries.Add(new() { Id = "77777777-7777-4777-8777-777777777773", Name = "Comics", KindKey = "comics", Area = "read", MediaTypes = ["Comics"], Sources = [new() { Id = "comics-1", Path = "C:\\media\\Comics" }] });

        foreach (var label in new[] { "Movies", "TV Shows", "Videos", "Music", "Audiobooks", "Books", "Comics" })
        {
            var cut = Open(label);
            var rows = cut.FindAll(".folder-table__row");
            Assert.NotEmpty(rows);
            Assert.All(rows, row =>
            {
                var holder = row.QuerySelector(".folder-table__limited");
                Assert.NotNull(holder);
                Assert.Equal(LibraryFolderTable.SuitableForLimitedHint, holder!.GetAttribute("title"));
                Assert.Contains("Suitable for limited profiles", Text(holder));
            });
            Assert.DoesNotContain("OK for limited profiles", cut.Markup);
        }
    }

    [Fact]
    public void TurningOnTheSwitchSavesThatFolderStraightAway_AndLeavesTheOthersAlone()
    {
        var cut = Open("Movies");
        var row = cut.FindAll(".folder-table__row")[1];

        row.QuerySelector(".folder-table__limited input[type=checkbox]")!.Change(true);

        cut.WaitForAssertion(() => Assert.NotNull(_lastLibrary));
        Assert.Equal(["movies-plex"], _lastLibrary!.Sources.Where(source => source.OkForLimitedProfiles).Select(source => source.Id).ToArray());
        Assert.Equal(3, _lastLibrary.Sources.Count);
        cut.WaitForAssertion(() => Assert.True(cut.FindAll(".folder-table__row")[1].QuerySelector(".folder-table__limited input[type=checkbox]")!.HasAttribute("checked")));
    }

    [Fact]
    public void TurningOffTheSwitchSavesTheFolderAsNotSuitable()
    {
        _configuration.Libraries[0].Sources[2].OkForLimitedProfiles = true;
        var cut = Open("Movies");

        cut.FindAll(".folder-table__row")[2].QuerySelector(".folder-table__limited input[type=checkbox]")!.Change(false);

        cut.WaitForAssertion(() => Assert.NotNull(_lastLibrary));
        Assert.DoesNotContain(_lastLibrary!.Sources, source => source.OkForLimitedProfiles);
    }

    [Fact]
    public void ReadOnlySettings_ShowTheSwitchButCannotChangeIt()
    {
        var cut = Open("Movies", readOnly: true);

        var switches = cut.FindAll(".folder-table__limited input[type=checkbox]");
        Assert.NotEmpty(switches);
        Assert.All(switches, input => Assert.True(input.HasAttribute("disabled")));
    }

    // A folder's icon is its state: a lock on the folder reads as read-only, and every icon carries the words as an accessible label and tooltip.
    [Fact]
    public void EachFolderIconSaysInWordsWhetherLibraryManagesOrOnlyReadsTheFolder()
    {
        var cut = Open("Movies");
        var rows = cut.FindAll(".folder-table__row");
        Assert.Empty(cut.FindAll(".folder-table__chip, .folder-table__tag, .folder-table__handling"));
        Assert.All(rows, row =>
        {
            var icon = row.QuerySelector(".folder-table__icon")!;
            Assert.Equal("img", icon.GetAttribute("role"));
            Assert.False(string.IsNullOrWhiteSpace(icon.GetAttribute("aria-label")));
            Assert.Equal(icon.GetAttribute("aria-label"), icon.GetAttribute("title"));
        });

        Assert.Contains("is-managed", rows[0].QuerySelector(".folder-table__icon")!.ClassList);
        Assert.Empty(rows[0].QuerySelectorAll(".folder-table__lock"));
        Assert.Contains("is-readonly", rows[2].QuerySelector(".folder-table__icon")!.ClassList);
        Assert.Contains("Read-only", IconLabel(rows[2]));
        Assert.NotEmpty(rows[2].QuerySelectorAll(".folder-table__lock"));
    }

    // A7: tags are on by default and a type can opt out.
    [Fact]
    public void AdvancedTagsAreOnByDefaultAndCanBeOptedOutPerType()
    {
        var cut = Open("Movies");
        var tags = cut.FindAll(".library-advanced__type input[role=switch]").Single();
        Assert.True(tags.HasAttribute("checked"));
        tags.Change(false);
        cut.WaitForAssertion(() => Assert.False(_lastLibrary?.OrganizationPolicy.WriteTags));
        cut.WaitForAssertion(() => Assert.Equal(["Named", "Named", "Not touched"], cut.FindAll(".folder-table__files-value").Select(Text)));
    }

    // A7: a live example, and an invalid custom template is refused inline without saving.
    [Fact]
    public void AdvancedShowsALiveExampleAndRefusesAnInvalidCustomTemplateInline()
    {
        var cut = Open("Movies");
        Assert.Contains("Movies/Dune (2021)/Dune (2021).mkv", cut.Find(".library-advanced__example code").TextContent);
        cut.FindAll(".library-advanced__type button").Single(x => x.TextContent.Trim() == "Custom template").Click();
        cut.WaitForElement(".library-advanced__type input:not([role=switch])");
        var field = cut.Find(".library-advanced__type input:not([role=switch])");
        field.Input("{Bad token");
        cut.WaitForAssertion(() => Assert.Contains("isn't valid", cut.Markup));
        Assert.True(cut.FindAll("button").Single(x => x.TextContent.Trim() == "Apply template").HasAttribute("disabled"));
        Assert.Equal(0, _saves);

        field.Input("{Title}/{Title} ({Year}){Ext}");
        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").Single(x => x.TextContent.Trim() == "Apply template").HasAttribute("disabled")));
        cut.FindAll("button").Single(x => x.TextContent.Trim() == "Apply template").Click();
        cut.WaitForAssertion(() => Assert.Equal("custom", _lastLibrary?.OrganizationPolicy.Mode));
        Assert.Equal("{Title}/{Title} ({Year}){Ext}", _lastLibrary!.OrganizationPolicy.CustomTemplate);
    }

    // A7: the global rows save.
    [Fact]
    public void AdvancedGlobalRowsSave()
    {
        var cut = Open("Movies");
        cut.FindAll(".library-advanced__global button").Single(x => x.TextContent.Trim() == "Keep both").Click();
        cut.WaitForAssertion(() => Assert.Equal("keep_both", _lastUpdate?.FileHandling.DuplicatePolicy));
        Assert.Contains("The new file gets a unique name; nothing is overwritten.", cut.Markup);
        Assert.Contains("Embed artwork in files", cut.Markup);
    }

    private async Task OpenMenuItemAsync(IRenderedComponent<LibraryFoldersPanel> cut, int row, string label)
    {
        await cut.ClickAsync(page => page.FindAll(".folder-table__row")[row].QuerySelector(".folder-table__menu-trigger")!);
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role=menuitem]"), x => Text(x) == label));
        await cut.ClickAsync(page => page.FindAll("[role=menuitem]").First(x => Text(x) == label));
    }

    // Switching a folder's handling is the row menu's "Switch to Read-only" or "Switch to Managed" item (the folder's icon only shows the state).
    private async Task SwitchHandlingAsync(IRenderedComponent<LibraryFoldersPanel> cut, int row)
    {
        await cut.ClickAsync(page => page.FindAll(".folder-table__row")[row].QuerySelector(".folder-table__menu-trigger")!);
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role=menuitem]"), x => Text(x).StartsWith("Switch to", StringComparison.Ordinal)));
        await cut.ClickAsync(page => page.FindAll("[role=menuitem]").First(x => Text(x).StartsWith("Switch to", StringComparison.Ordinal)));
    }

    // The words behind a folder's state icon (full access, read-only or busy); the icon alone is never the only signal.
    private static string? IconLabel(AngleSharp.Dom.IElement row) => row.QuerySelector(".folder-table__icon")!.GetAttribute("aria-label");

    // A dialog button is found and clicked in one step on the renderer thread (see RendererThreadExtensions).

    private static Task ClickButtonAsync(IRenderedComponent<AppDialogHost> dialogs, string label) =>
        dialogs.ClickAsync(host => host.FindAll("button").Single(x => Text(x) == label));

    [Fact]
    public async Task KeepManagedCancelsTheSwitchToReadOnly()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        await SwitchHandlingAsync(cut, 1);
        dialogs.WaitForAssertion(() => Assert.Contains("Use D:\\Plex\\Movies as read-only?", dialogs.Markup));
        dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Keep managed").Click();
        Assert.Equal(0, _saves);
    }

    [Fact]
    public async Task UseReadOnlyConfirmsThenSwitchesTheFolderToReadOnly()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        await SwitchHandlingAsync(cut, 1);
        dialogs.WaitForAssertion(() => Assert.Contains("Use D:\\Plex\\Movies as read-only?", dialogs.Markup));
        dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Use read-only").Click();
        cut.WaitForAssertion(() => Assert.Equal("existing_library", _lastLibrary?.Sources.Single(x => x.Id == "movies-plex").ManagementMode));
        Assert.Equal("read_only", _lastLibrary!.Sources.Single(x => x.Id == "movies-plex").AccessMode);
    }

    [Fact]
    public void FolderProblemsShowAsOneStatusLine()
    {
        _pathProbe = path => path.StartsWith("F:", StringComparison.Ordinal)
            ? new() { Exists = false }
            : path.StartsWith("D:", StringComparison.Ordinal) ? new() { Exists = true, HasRead = true, HasWrite = false } : new() { Exists = true, HasRead = true, HasWrite = true };
        var cut = Open("Movies");
        cut.WaitForAssertion(() => Assert.Contains("Folder unavailable", cut.FindAll(".folder-table__row")[2].TextContent));
        Assert.Contains("No access", cut.FindAll(".folder-table__row")[1].TextContent);
        Assert.DoesNotContain("No access", cut.FindAll(".folder-table__row")[0].TextContent);
    }

    [Fact]
    public async Task LabelEditSavesTheNewLabel()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        await OpenMenuItemAsync(cut, 0, "Label");
        dialogs.WaitForElement("input");
        dialogs.Find("input").Input("Main films");
        dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Apply").Click();
        cut.WaitForAssertion(() => Assert.Equal("Main films", _lastLibrary?.Sources.Single(x => x.Id == "movies-main").Notes));
    }

    [Fact]
    public void ConvertingFolderCanHoldTheMarkerAndShowsProgress()
    {
        _configuration.Libraries[0].PrimaryDestinationSourceId = "movies-plex";
        _configuration.Libraries[0].Sources[1].ConversionState = "converting";
        var cut = Open("Movies");
        var row = cut.FindAll(".folder-table__row")[1];
        Assert.Contains("is-marked", row.ClassList);
        Assert.Contains("Organising…", row.TextContent);
        Assert.Contains("Converting", IconLabel(row));
        Assert.Empty(row.QuerySelectorAll(".folder-job"));
        Assert.Single(cut.FindAll(".folder-table__marker.is-marked"));
    }

    [Fact]
    public async Task MarkerMovesWhenItsFolderIsRemovedOrSwitchedToReadOnly()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        await OpenMenuItemAsync(cut, 0, "Remove folder");
        dialogs.WaitForAssertion(() => Assert.Contains("Remove this folder?", dialogs.Markup));
        dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Remove").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".folder-table__row").Count));
        Assert.Equal("movies-plex", _lastLibrary?.PrimaryDestinationSourceId);
        Assert.Contains("is-marked", cut.FindAll(".folder-table__row")[0].ClassList);

        // Switching the marked folder to Read-only hands the marker to the next managed folder; with none left it is cleared.
        _configuration.Libraries[1].Sources.Add(new() { Id = "tv-second", Path = "E:\\Library\\TV2", ManagementMode = "managed_by_tuvima", AccessMode = "writable" });
        var tv = Open("TV Shows");
        await SwitchHandlingAsync(tv, 0);
        dialogs.WaitForAssertion(() => Assert.Contains("read-only?", dialogs.Markup));
        dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Use read-only").Click();
        tv.WaitForAssertion(() => Assert.Equal("tv-second", _lastLibrary?.PrimaryDestinationSourceId));
        Assert.Single(tv.FindAll(".folder-table__marker.is-marked"));
    }

    // Changing naming never re-paths existing files by itself: the only caller of the reorganisation service is its explicit plan/execute endpoints.
    [Fact]
    public void NamingChangesNeverRepathExistingFilesOnTheirOwn()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var users = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => File.ReadAllText(file).Contains("LibraryReorganizationService", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToList();
        Assert.Equal(["LibraryReorganizationEndpoints.cs", "LibraryReorganizationService.cs", "TuvimaStorageServiceCollectionExtensions.cs"], users);
    }

    // A11: the table collapses to stacked cards under 840px of its own width and adds no horizontal scroll of its own.
    // The panel and the folder picker use the same 840px, and Show details appears exactly where the Files value is folded away.
    [Fact]
    public void FolderTableCollapsesToCardsAtTheOneCardBreakpoint()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web");
        var table = File.ReadAllText(Path.Combine(src, "Components", "Settings", "LibraryFolderTable.razor.css"));
        var panel = File.ReadAllText(Path.Combine(src, "Components", "Settings", "LibraryFoldersPanel.razor.css"));
        var picker = File.ReadAllText(Path.Combine(src, "Components", "Shared", "ServerFolderPicker.razor.css"));
        var app = File.ReadAllText(Path.Combine(src, "wwwroot", "app.css"));
        Assert.Contains("@container folder-table (max-width: 840px)", table);
        Assert.DoesNotContain("@media (max-width: 840px)", table);
        Assert.Contains("grid-template-areas", table);
        Assert.DoesNotContain("overflow-x", table);
        Assert.Contains("@media (max-width: 840px)", panel);
        Assert.Contains("@media (max-width: 840px)", picker);
        Assert.DoesNotContain("max-width: 800px", panel + picker);
        Assert.Contains("@container folder-table (max-width: 840px) { .folder-table__expand {", app);
        Assert.DoesNotContain("@media (max-width: 840px) { .folder-table__expand", app);
    }

    // Named custom libraries (D51): the plain kinds hold several, each its own section on the kind's page.
    private void AddNamedVideoLibraries()
    {
        _configuration.Libraries.Add(new()
        {
            Id = "55555555-5555-4555-8555-555555555555",
            Name = "Lectures",
            Kind = "catalogued",
            KindKey = "other_videos",
            Area = "watch",
            MediaTypes = ["Videos"],
            Sources = [new() { Id = "lec-1", Path = "C:\\media\\Lectures", ManagementMode = "existing_library", AccessMode = "read_only" }],
        });
        _configuration.Libraries.Add(new()
        {
            Id = "66666666-6666-4666-8666-666666666666",
            Name = "Family recordings",
            Kind = "catalogued",
            KindKey = "other_videos",
            Area = "watch",
            MediaTypes = ["Videos"],
            Sources = [new() { Id = "fam-1", Path = "C:\\media\\Family", ManagementMode = "existing_library", AccessMode = "read_only" }, new() { Id = "fam-2", Path = "C:\\media\\Birthdays", ManagementMode = "existing_library", AccessMode = "read_only" }],
        });
    }

    [Fact]
    public void TheKindPageListsEachNamedLibraryAsItsOwnSectionWithItsFolders()
    {
        AddNamedVideoLibraries();

        var cut = Open("Videos");

        var sections = cut.FindAll(".library-folders__library");
        Assert.Equal(["Lectures", "Family recordings"], sections.Select(section => Text(section.QuerySelector("h3")!)).ToArray());
        Assert.Equal([1, 2], sections.Select(section => section.QuerySelectorAll(".folder-table__row").Length).ToArray());
        Assert.Equal(2, cut.FindAll("button[aria-label^='Rename ']").Count);
    }

    [Fact]
    public void RenamingANamedLibrarySavesItThroughTheLibraryChange()
    {
        AddNamedVideoLibraries();
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Videos");

        cut.Find("button[aria-label='Rename Lectures']").Click();
        dialogs.WaitForAssertion(() => Assert.NotEmpty(dialogs.FindAll("input")));
        dialogs.Find("input").Input("Open lectures");
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Apply").Click();

        cut.WaitForAssertion(() => Assert.Equal("Open lectures", _lastLibrary?.Name));
        Assert.Equal("55555555-5555-4555-8555-555555555555", _lastLibrary!.Id);
        // The rename keeps the library's folders and is guarded by the copy that was read.
        Assert.Equal(["lec-1"], _lastLibrary.Sources.Select(source => source.Id).ToArray());
        Assert.NotNull(_lastRequest!.ExpectedLibrary);
        Assert.Equal("Lectures", _lastRequest.ExpectedLibrary!.Name);
        Assert.Equal(["lec-1"], _lastRequest.ExpectedLibrary.Sources.Select(source => source.Id).ToArray());
        cut.WaitForAssertion(() => Assert.Contains("Open lectures", cut.FindAll(".library-folders__library h3").Select(Text)));
    }

    [Fact]
    public void TheEnginesRefusalOfADuplicateNameIsShownAsItComesAndTheNameStays()
    {
        AddNamedVideoLibraries();
        _refuseSave = "libraries[2].name \"Lectures\" is already used by another library in watch. Give each library its own name.";
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Videos");

        cut.Find("button[aria-label='Rename Family recordings']").Click();
        dialogs.WaitForAssertion(() => Assert.NotEmpty(dialogs.FindAll("input")));
        dialogs.Find("input").Input("Lectures");
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Apply").Click();

        cut.WaitForAssertion(() => Assert.Contains("is already used by another library in watch", cut.Markup));
        Assert.Equal(["Lectures", "Family recordings"], cut.FindAll(".library-folders__library h3").Select(Text).ToArray());
    }

    // Unmatched on a library (D34, D40b): the amber count with Retry now and Keep all as they are.
    [Fact]
    public void ALibraryWithUnmatchedFilesOffersRetryNowAndKeepAllAndOneWithNoneOffersNeither()
    {
        _configuration.Libraries[0].UnmatchedCount = 3;

        var cut = Open("Movies");

        Assert.Equal("3", Text(cut.Find(".lib-unmatched")));
        Assert.Contains(cut.FindAll("button"), button => Text(button) == "Retry now");
        Assert.Contains(cut.FindAll("button"), button => Text(button) == "Keep all as they are");

        var quiet = Open("TV Shows");
        Assert.Empty(quiet.FindAll(".lib-unmatched"));
        Assert.DoesNotContain(quiet.FindAll("button"), button => Text(button) == "Retry now");
    }

    [Fact]
    public void RetryNowCallsTheEnginesLibraryActionAndSaysHowManyFilesItChanged()
    {
        _configuration.Libraries[0].UnmatchedCount = 3;
        var toasts = Render<AppToastHost>();
        var cut = Open("Movies");

        cut.FindAll("button").Single(button => Text(button) == "Retry now").Click();

        cut.WaitForAssertion(() => Assert.Equal([("retry", MoviesId)], _unmatchedActions));
        toasts.WaitForAssertion(() => Assert.Contains("3 files", toasts.Markup));
    }

    [Fact]
    public void KeepAllAsksFirstThenCallsTheEngineAndCancellingSendsNothing()
    {
        _configuration.Libraries[0].UnmatchedCount = 3;
        var dialogs = Render<AppDialogHost>();
        var toasts = Render<AppToastHost>();
        var cut = Open("Movies");

        cut.FindAll("button").Single(button => Text(button) == "Keep all as they are").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Stop trying to match 3 files in Movies?", dialogs.Markup));
        Assert.Contains("You can look them up again later.", dialogs.Markup);
        dialogs.FindAll("button").Single(button => Text(button) == "Cancel").Click();
        Assert.Empty(_unmatchedActions);

        cut.FindAll("button").Single(button => Text(button) == "Keep all as they are").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Stop trying to match 3 files in Movies?", dialogs.Markup));
        dialogs.FindAll("button").Single(button => Text(button) == "Keep all as they are").Click();

        cut.WaitForAssertion(() => Assert.Equal([("keep", MoviesId)], _unmatchedActions));
        toasts.WaitForAssertion(() => Assert.Contains("3 files", toasts.Markup));
    }

    // The first check of a newly added folder (Library setup D35): checking is a quiet line; a paused folder says why and offers Keep going, Remove or Change what this folder is.
    private static MediaEngine.Contracts.Library.FolderFirstCheckDto FirstCheck(string state, int tried = 50, int unmatched = 30) =>
        new() { State = state, Tried = tried, Unmatched = unmatched, TotalFiles = 300 };

    [Fact]
    public void APausedFolderSaysMostFilesArentMatchingWithItsNumbers_AndOffersKeepGoingAndRemoveTheFolder()
    {
        _configuration.Libraries[0].Sources[1].FirstCheck = FirstCheck("paused");

        var cut = Open("Movies");

        var line = Assert.Single(cut.FindAll("[data-first-check=paused]"));
        Assert.Contains("Most files in this folder aren't matching: 30 of 50. This is often the wrong kind or the wrong folder.", Text(line));
        Assert.Contains("folder-table__status--warning", line.ClassList);
        Assert.Equal(["Keep going", "Remove the folder", "Change what this folder is"], line.QuerySelectorAll("button").Select(Text).ToArray());
        Assert.Equal("D:\\Plex\\Movies", line.ParentElement!.QuerySelector(".folder-table__path")!.TextContent);
        // Only that folder's row has it; the status is also in the live region for screen readers.
        Assert.Single(cut.FindAll("[data-first-check]"));
        Assert.Contains("is paused: most of its files aren't matching", cut.Find(".sr-only[role=status]").TextContent);
    }

    [Fact]
    public void KeepGoingCallsTheEngineForThatFolder_SaysSoInAToast_AndDropsThePausedLine()
    {
        _configuration.Libraries[0].Sources[1].FirstCheck = FirstCheck("paused");
        var toasts = Render<AppToastHost>();
        var cut = Open("Movies");

        cut.FindAll("[data-first-check=paused] button").Single(button => Text(button) == "Keep going").Click();

        cut.WaitForAssertion(() => Assert.Equal(["movies-plex"], _keptGoing));
        toasts.WaitForAssertion(() => Assert.Contains("Carrying on with", toasts.Markup));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-first-check]")));
    }

    [Fact]
    public void RemoveTheFolderAsksFirstAsTheRowMenuDoes_AndCancellingRemovesNothing()
    {
        _configuration.Libraries[0].Sources[1].FirstCheck = FirstCheck("paused");
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");

        cut.FindAll("[data-first-check=paused] button").Single(button => Text(button) == "Remove the folder").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Remove this folder?", dialogs.Markup));
        Assert.Contains("Your files stay on disk.", dialogs.Markup);
        dialogs.FindAll("button").Single(button => Text(button) == "Keep").Click();
        Assert.Equal(0, _saves);
        Assert.Equal(3, cut.FindAll(".folder-table__row").Count);

        cut.FindAll("[data-first-check=paused] button").Single(button => Text(button) == "Remove the folder").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Remove this folder?", dialogs.Markup));
        dialogs.FindAll("button").Single(button => Text(button) == "Remove").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".folder-table__row").Count));
        Assert.DoesNotContain(_lastLibrary!.Sources, source => source.Id == "movies-plex");
        Assert.Empty(_keptGoing);
    }

    [Fact]
    public void AFolderStillBeingCheckedShowsAQuietCheckingLine_WithNoButtons()
    {
        _configuration.Libraries[0].Sources[0].FirstCheck = FirstCheck("checking", tried: 3, unmatched: 1);

        var cut = Open("Movies");

        var line = Assert.Single(cut.FindAll("[data-first-check=checking]"));
        Assert.Equal("Checking the first files…", Text(line));
        Assert.DoesNotContain("folder-table__status--warning", line.ClassList);
        Assert.Empty(line.QuerySelectorAll("button"));
    }

    [Fact]
    public void AFolderWithNoCheckRunningShowsNothingExtra()
    {
        var cut = Open("Movies");

        Assert.Empty(cut.FindAll("[data-first-check]"));
        Assert.DoesNotContain(cut.FindAll("button"), button => Text(button) is "Keep going" or "Remove the folder");
    }

    [Fact]
    public void ADoubleClickOnRetryNowSendsOneRequest()
    {
        _configuration.Libraries[0].UnmatchedCount = 3;
        _retryGate = new TaskCompletionSource();
        var cut = Open("Movies");

        cut.FindAll("button").Single(button => Text(button) == "Retry now").Click();
        cut.WaitForAssertion(() => Assert.True(cut.FindAll("button").Single(button => Text(button) == "Retry now").HasAttribute("disabled")));
        _retryGate.SetResult();

        cut.WaitForAssertion(() => Assert.Equal([("retry", MoviesId)], _unmatchedActions));
        Assert.Single(_unmatchedActions);
    }

    // D23: under the one card breakpoint (840px of viewport width, so portrait tablets too) folders are read-only. With no browser to measure, the stylesheets are read: every control that adds, converts or merges is hidden
    // (display:none) and the note is shown. The gate follows the viewport, not the device class.
    [Fact]
    public void EveryControlThatAddsConvertsOrMergesIsHiddenUnderTheCardBreakpoint()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web");
        var panel = File.ReadAllText(Path.Combine(src, "Components", "Settings", "LibraryFoldersPanel.razor.css"));
        var app = File.ReadAllText(Path.Combine(src, "wwwroot", "app.css"));
        Assert.DoesNotContain("max-width: 600px", panel);
        // The last 840px block of the panel stylesheet is the read-only one.
        var phone = panel[panel.LastIndexOf("@media (max-width: 840px)", StringComparison.Ordinal)..];
        Assert.Contains(".library-folders__phone-note", phone);
        foreach (var hidden in new[] { ".library-folders__detail-actions", ".library-folders__row-actions", ".library-folders__choose", ".library-folders__empty-action", ".library-folders__hint--merge", ".library-folders__advanced" })
        {
            Assert.Contains(hidden, phone);
        }

        Assert.Contains(".folder-table__menu { display: none; }", app);
        Assert.DoesNotContain(".folder-table__chip", app);
    }

    [Fact]
    public async Task MobileKeepsTheTableReadOnly()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync("mobile");
        // The library page passes the phone's read-only state down to its folders panel.
        var cut = Open("Movies", readOnly: true);
        Assert.Empty(cut.FindAll(".folder-table__menu-trigger"));
        Assert.All(cut.FindAll(".folder-table__radio"), button => Assert.True(button.HasAttribute("disabled")));
    }

    [Fact]
    public async Task EditingThePathBarImmediatelyInvalidatesAnEarlierSelection()
    {
        var cut = Render<AppDialogHost>();
        await cut.InvokeAsync(() => Services.GetRequiredService<IAppDialogService>().ShowAsync<ServerFolderPicker>(""));
        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Add").HasAttribute("disabled")));
        cut.Find(".server-folder-picker__address-end").Click();
        var input = cut.Find(".server-folder-picker__address-input");
        input.Input("C:\\media\\changed");
        Assert.True(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Add").HasAttribute("disabled"));
        input.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Add").HasAttribute("disabled")));
    }
}
