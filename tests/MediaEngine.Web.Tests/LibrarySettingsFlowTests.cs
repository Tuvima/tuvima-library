using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
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
    private UpdateLibrariesRequest? _lastUpdate;
    private MediaEngine.Contracts.Ingestion.ImportFolderWaitingResponse? _waiting;
    private Func<string, PathTestResultDto> _pathProbe = _ => new() { Exists = true, HasRead = true, HasWrite = true };
    private IRenderedComponent<AppPopoverHost> _popovers = null!;

    private static LibrariesConfigurationDto BuildConfiguration() => new()
    {
        StorageLocations = [new() { Id = "media", Label = "Media storage", Path = "C:\\media", AllowWrite = true }],
        Libraries =
        [
            new()
            {
                Id = MoviesId, Name = "Movies", Area = "watch", MediaTypes = ["Movies"],
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
                Id = TvId, Name = "TV Shows", Area = "watch", MediaTypes = ["TV"],
                OrganizationPolicy = new() { Mode = "tuvima_standard", WriteTags = false },
                PrimaryDestinationSourceId = "tv-main",
                Sources = [new() { Id = "tv-main", Path = "E:\\Library\\TV", ManagementMode = "managed_by_tuvima", AccessMode = "writable" }],
            },
            new()
            {
                Id = AudiobooksId, Name = "Audiobooks", Area = "listen", MediaTypes = ["Audiobooks"],
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
            stub.SetHandler(nameof(IEngineApiClient.MutateLibraryAsync), args =>
            {
                _saves++;
                _lastLibrary = ((LibraryMutationRequest)args![0]!).Library;
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
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        _popovers = Render<AppPopoverHost>();
    }

    private IRenderedComponent<LibrariesTab> Open(string typeLabel)
    {
        var cut = Render<LibrariesTab>();
        cut.WaitForElement(".library-folders__type");
        cut.FindAll(".library-folders__type").Single(x => x.TextContent.Contains(typeLabel)).Click();
        cut.WaitForElement(".folder-table, .library-folders__empty");
        return cut;
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    [Fact]
    public void OverviewListsEveryLibraryTypeWithItsFolderCount()
    {
        var cut = Render<LibrariesTab>();
        cut.WaitForElement(".library-folders__type");
        Assert.Equal(7, cut.FindAll(".library-folders__type").Count);
        Assert.Contains(cut.FindAll(".library-folders__type"), x => x.TextContent.Contains("Audiobooks") && x.TextContent.Contains("1 folder"));
        Assert.Contains(cut.FindAll(".library-folders__type"), x => x.TextContent.Contains("Movies") && x.TextContent.Contains("3 folders"));
        Assert.Contains("Add Folder", cut.Markup);
        Assert.Contains("/settings/libraries/view", cut.Markup);
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

    // I1 to I3: the Import folder is the seventh entry, reads Not set, and shows only the Set button with no type lines.
    [Fact]
    public void ImportFolderIsTheSeventhEntryAndShowsOnlySetWhenNotSet()
    {
        var cut = Render<LibrariesTab>();
        cut.WaitForElement(".library-folders__type");
        var entries = cut.FindAll(".library-folders__type");
        Assert.Equal("Import folder", Text(entries[6].QuerySelector("strong")!));
        Assert.Equal("Not set", Text(entries[6].QuerySelector("small")!));
        entries[6].Click();
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
        var cut = Render<LibrariesTab>();
        cut.WaitForElement(".library-folders__type");
        var entries = cut.FindAll(".library-folders__type");
        Assert.Equal("Set", Text(entries[6].QuerySelector("small")!));
        entries[6].Click();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".library-folders__destination").Count));
        Assert.Contains("C:\\media\\Import", cut.Find(".library-folders__table-row").TextContent);
        Assert.Contains("Where imported files go", cut.Markup);
        var lines = cut.FindAll(".library-folders__destination").Select(Text).ToList();
        Assert.Contains(lines, line => line.StartsWith("Movies") && line.Contains("Library Movies") && line.Contains("E:\\Library\\Movies"));
        Assert.Contains(lines, line => line.StartsWith("Audiobooks") && line.Contains("Add a managed folder to import Audiobooks") && line.Contains("Choose"));
        Assert.DoesNotContain("Merge into", cut.Markup);
    }

    // Choose jumps to that type's folders and points at Add Folder.
    [Fact]
    public void ChooseOnAnImportLineOpensThatTypeAndHighlightsAddFolder()
    {
        _configuration.ImportFolderStatus = ConfiguredImport();
        var cut = Render<LibrariesTab>();
        cut.WaitForElement(".library-folders__type");
        cut.FindAll(".library-folders__type")[6].Click();
        cut.WaitForElement(".library-folders__destination");
        cut.FindAll(".library-folders__destination").Single(x => x.TextContent.Contains("Audiobooks")).QuerySelector("button")!.Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Audiobooks", Text(cut.Find("#library-folders-title")));
            Assert.NotEmpty(cut.FindAll(".library-folders__add.is-highlighted"));
        });
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
        var cut = Render<LibrariesTab>();
        cut.WaitForElement(".library-folders__type");
        cut.FindAll(".library-folders__type")[6].Click();
        cut.WaitForAssertion(() =>
        {
            var tv = cut.FindAll(".library-folders__destination").Single(x => x.TextContent.StartsWith("TV Shows"));
            Assert.Contains("Folder unavailable", tv.TextContent);
            Assert.Contains("because the New media folder is offline", tv.TextContent);
        });
    }

    // A1: the per-library page and its link are gone.
    [Fact]
    public void NoLibrarySettingsLinkRemains()
    {
        var cut = Open("Movies");
        Assert.DoesNotContain("Library settings", cut.Markup);
        Assert.DoesNotContain("/settings/libraries/" + MoviesId, cut.Markup);
        Assert.False(SettingsNav.IsMobileRouteAvailable(SettingsSection.Libraries, MoviesId));
        Assert.True(SettingsNav.IsMobileRouteAvailable(SettingsSection.Libraries, "view"));
    }

    // A2: four columns plus the actions menu, one row per folder, no numbering, arrows or drag handles.
    [Fact]
    public void FolderTableHasFourColumnsAndAMenuPerFolder()
    {
        var cut = Open("Movies");
        Assert.Equal(["Folder", "Handling", "New media", "Files", ""], cut.FindAll(".folder-table__head [role=columnheader]").Select(Text));
        var rows = cut.FindAll(".folder-table__row");
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.NotNull(row.QuerySelector(".folder-table__handling"));
            Assert.NotNull(row.QuerySelector(".folder-table__marker"));
            Assert.NotNull(row.QuerySelector(".folder-table__files"));
            Assert.NotNull(row.QuerySelector(".folder-table__actions"));
        });
        Assert.DoesNotContain("draggable", cut.Find(".folder-table").InnerHtml);
        Assert.Contains("Library Movies", Text(rows[0]));
        Assert.Contains("E:\\Library\\Movies", Text(rows[0]));
    }

    // A3: exactly one managed folder holds the marker; tapping another managed folder moves it and saves; as-is folders have no radio.
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

    // A5 (this packet): tapping Handling on an as-is folder confirms, then uses the existing switch to Managed.
    [Fact]
    public void TappingAsIsHandlingConfirmsThenSwitchesToManaged()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        cut.FindAll(".folder-table__row")[2].QuerySelector(".folder-table__chip")!.Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Let Tuvima manage F:\\Movies?", dialogs.Markup));
        Assert.Equal(0, _saves);
        dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Let Tuvima manage it").Click();
        cut.WaitForAssertion(() => Assert.Equal("managed_by_tuvima", _lastLibrary?.Sources.Single(x => x.Id == "movies-old").ManagementMode));
    }

    [Fact]
    public void KeepAsIsChangesNothing()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        cut.FindAll(".folder-table__row")[2].QuerySelector(".folder-table__chip")!.Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Let Tuvima manage F:\\Movies?", dialogs.Markup));
        dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Keep as-is").Click();
        Assert.Equal(0, _saves);
    }

    // A6: the Files cell follows the type's tag setting.
    [Fact]
    public void FilesCellReadsNamedTaggedNamedOrNotTouched()
    {
        var movies = Open("Movies");
        Assert.Equal(["Named + tagged", "Named + tagged", "Not touched"], movies.FindAll(".folder-table__files").Select(Text));
        Assert.Contains("Advanced > Movies", movies.Find(".folder-table__files").GetAttribute("title"));
        var tv = Open("TV Shows");
        Assert.Equal(["Named"], tv.FindAll(".folder-table__files").Select(Text));
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
        cut.WaitForAssertion(() => Assert.Equal(["Named", "Named", "Not touched"], cut.FindAll(".folder-table__files").Select(Text)));
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

    // The setup wizard reuses the same panel, without the Advanced section.
    [Fact]
    public void SetupWizardPanelHidesAdvanced()
    {
        var cut = Render<LibraryFoldersPanel>(parameters => parameters.Add(x => x.Embedded, true).Add(x => x.SetupSession, "session"));
        cut.WaitForElement(".library-folders__type");
        Assert.Empty(cut.FindAll(".library-folders__advanced"));
    }

    private void OpenMenuItem(IRenderedComponent<LibrariesTab> cut, int row, string label)
    {
        cut.FindAll(".folder-table__row")[row].QuerySelector(".folder-table__menu-trigger")!.Click();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role=menuitem]"), x => Text(x) == label));
        cut.FindAll("[role=menuitem]").First(x => Text(x) == label).Click();
    }

    [Fact]
    public void KeepManagedCancelsTheSwitchToAsIs()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        cut.FindAll(".folder-table__row")[1].QuerySelector(".folder-table__chip")!.Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Use D:\\Plex\\Movies as-is?", dialogs.Markup));
        dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Keep managed").Click();
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void UseAsIsConfirmsThenSwitchesTheFolderToReadOnly()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        cut.FindAll(".folder-table__row")[1].QuerySelector(".folder-table__chip")!.Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Use D:\\Plex\\Movies as-is?", dialogs.Markup));
        dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Use as-is").Click();
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
    public void LabelEditSavesTheNewLabel()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        OpenMenuItem(cut, 0, "Label");
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
        Assert.Contains("Converting…", Text(row.QuerySelector(".folder-table__handling")!));
        Assert.Single(cut.FindAll(".folder-table__marker.is-marked"));
    }

    [Fact]
    public void MarkerMovesWhenItsFolderIsRemovedOrSwitchedToAsIs()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = Open("Movies");
        OpenMenuItem(cut, 0, "Remove folder");
        dialogs.WaitForAssertion(() => Assert.Contains("Remove this folder?", dialogs.Markup));
        dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Remove").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".folder-table__row").Count));
        Assert.Equal("movies-plex", _lastLibrary?.PrimaryDestinationSourceId);
        Assert.Contains("is-marked", cut.FindAll(".folder-table__row")[0].ClassList);

        // Switching the marked folder to As-is hands the marker to the next managed folder; with none left it is cleared.
        _configuration.Libraries[1].Sources.Add(new() { Id = "tv-second", Path = "E:\\Library\\TV2", ManagementMode = "managed_by_tuvima", AccessMode = "writable" });
        var tv = Open("TV Shows");
        tv.FindAll(".folder-table__row")[0].QuerySelector(".folder-table__chip")!.Click();
        dialogs.WaitForAssertion(() => Assert.Contains("as-is?", dialogs.Markup));
        dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Use as-is").Click();
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

    // A11: the table collapses to stacked cards under 800px and adds no horizontal scroll of its own.
    [Fact]
    public void FolderTableCollapsesToCardsOnPhones()
    {
        var css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components", "Settings", "LibraryFolderTable.razor.css"));
        Assert.Contains("@media (max-width: 840px)", css);
        Assert.Contains("grid-template-areas", css);
        Assert.DoesNotContain("overflow-x", css);
    }

    [Fact]
    public async Task MobileKeepsTheTableReadOnly()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync("mobile");
        var cut = Open("Movies");
        Assert.Empty(cut.FindAll(".folder-table__menu-trigger"));
        Assert.All(cut.FindAll(".folder-table__chip, .folder-table__radio"), button => Assert.True(button.HasAttribute("disabled")));
    }

    [Fact]
    public async Task EditingThePathBarImmediatelyInvalidatesAnEarlierSelection()
    {
        var cut = Render<AppDialogHost>();
        await cut.InvokeAsync(() => Services.GetRequiredService<IAppDialogService>().ShowAsync<ServerFolderPicker>(""));
        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Add").HasAttribute("disabled")));
        cut.Find(".server-folder-picker__pathbar input").Input("C:\\media\\changed");
        Assert.True(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Add").HasAttribute("disabled"));
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Go").Click();
        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Add").HasAttribute("disabled")));
    }
}
