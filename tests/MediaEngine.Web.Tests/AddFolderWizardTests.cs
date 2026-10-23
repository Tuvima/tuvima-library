using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// The Add folder wizard (what is it, which folder, how should Library treat it) and the places that open it.
public sealed class AddFolderWizardTests : AsyncBunitContext
{
    private const string MoviesId = "11111111-1111-4111-8111-111111111111";
    private const string AudiobooksId = "22222222-2222-4222-8222-222222222222";

    private LibrariesConfigurationDto _configuration = BuildConfiguration();
    private readonly List<LibraryMutationRequest> _mutations = [];
    private readonly List<UpdateLibrariesRequest> _setupUpdates = [];
    private readonly List<FolderMergeRequest> _merges = [];
    private readonly List<string> _converted = [];
    private readonly List<(string Path, string Mode)> _validations = [];
    private ServerFolderValidationResultDto _picked = new() { Path = "E:\\Incoming\\Films", StorageLocationId = "media", Exists = true, HasRead = true, HasWrite = true, CanSelect = true, ContainsFiles = true };
    private bool _writable = true;
    private IRenderedComponent<AppDialogHost> _dialogs = null!;
    // The Engine refusing a library change with its own sentence (a duplicate name, the limit); null means it saves.
    private string? _refuseSave;
    private string? _lastError;

    private static LibrariesConfigurationDto BuildConfiguration() => new()
    {
        StorageLocations = [new() { Id = "media", Label = "Media storage", Path = "C:\\media", AllowWrite = true }],
        Libraries =
        [
            new()
            {
                Id = MoviesId, Name = "Movies", KindKey = "movies", Area = "watch", Kind = "catalogued", MediaTypes = ["Movies"],
                PrimaryDestinationSourceId = "movies-main",
                Sources =
                [
                    new() { Id = "movies-main", Path = "E:\\Library\\Movies", Notes = "Library Movies", ManagementMode = "managed_by_tuvima", AccessMode = "writable" },
                    new() { Id = "movies-old", Path = "F:\\Movies", Notes = "Old drive", ManagementMode = "existing_library", AccessMode = "read_only" },
                ],
            },
            new()
            {
                Id = AudiobooksId, Name = "Audiobooks", KindKey = "audiobooks", Area = "listen", Kind = "catalogued", MediaTypes = ["Audiobooks"],
                Sources = [new() { Id = "audio-old", Path = "C:\\media\\Audiobooks", ManagementMode = "existing_library", AccessMode = "read_only" }],
            },
        ],
    };

    public AddFolderWizardTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(_configuration));
            stub.SetHandler(nameof(IEngineApiClient.GetSetupLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(_configuration));
            stub.SetHandler("get_LastError", _ => _lastError);
            stub.SetHandler(nameof(IEngineApiClient.MutateLibraryAsync), args =>
            {
                var request = (LibraryMutationRequest)args![0]!;
                _mutations.Add(request);
                if (_refuseSave is not null)
                {
                    _lastError = _refuseSave;
                    return Task.FromResult<LibrariesConfigurationDto?>(null);
                }

                var library = request.Library!;
                // Mimic the Engine's Normalize: keep a managed marker, else the earliest managed folder, else none.
                var managed = library.Sources.Where(x => x.ManagementMode == "managed_by_tuvima").ToList();
                if (!managed.Any(x => x.Id == library.PrimaryDestinationSourceId))
                {
                    library.PrimaryDestinationSourceId = managed.FirstOrDefault()?.Id;
                }

                var libraries = _configuration.Libraries.Where(item => item.Id != library.Id).Append(library).ToList();
                _configuration = new LibrariesConfigurationDto { StorageLocations = _configuration.StorageLocations, Libraries = libraries, FileHandling = _configuration.FileHandling };
                return Task.FromResult<LibrariesConfigurationDto?>(_configuration);
            });
            stub.SetHandler(nameof(IEngineApiClient.UpdateSetupLibrariesAsync), args =>
            {
                var request = (UpdateLibrariesRequest)args![0]!;
                _setupUpdates.Add(request);
                _configuration = new LibrariesConfigurationDto { StorageLocations = request.StorageLocations, Libraries = request.Libraries, FileHandling = request.FileHandling };
                return Task.FromResult<LibrariesConfigurationDto?>(_configuration);
            });
            stub.SetHandler(nameof(IEngineApiClient.PreviewFolderConversionAsync), _ => Task.FromResult<FolderConversionPreviewResponse?>(new() { TotalFiles = 40, VideoFiles = 40, EstimatedSeconds = 200, DuplicatePolicy = "keep_one" }));
            stub.SetHandler(nameof(IEngineApiClient.PreviewFolderMergeAsync), _ => Task.FromResult<FolderMergePreviewResponse?>(new()
            {
                TotalFiles = 12,
                VideoFiles = 12,
                SourceBytes = 1024L * 1024 * 1024,
                TargetFreeBytes = 900L * 1024 * 1024 * 1024,
                EstimatedSeconds = 300,
                DuplicatePolicy = "keep_one",
            }));
            stub.SetHandler(nameof(IEngineApiClient.MergeFolderAsync), args =>
            {
                var request = (FolderMergeRequest)args![0]!;
                _merges.Add(request);
                return Task.FromResult<FolderJobStatusDto?>(new FolderJobStatusDto { OperationId = "merge-1", SourceId = request.SourceId, JobType = "merge_folder", State = "queued" });
            });
            stub.SetHandler(nameof(IEngineApiClient.ConvertFolderAsync), args =>
            {
                _converted.Add((string)args![0]!);
                return Task.FromResult<FolderJobStatusDto?>(new FolderJobStatusDto { OperationId = "convert-1", SourceId = (string)args![0]!, State = "queued" });
            });
            stub.SetHandler(nameof(IEngineApiClient.GetFolderJobsAsync), _ => Task.FromResult<IReadOnlyList<FolderJobStatusDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetServerFolderRootsAsync), _ => Task.FromResult<IReadOnlyList<ServerStorageLocationDto>>(_configuration.StorageLocations));
            stub.SetHandler(nameof(IEngineApiClient.BrowseServerFoldersAsync), _ => Task.FromResult<BrowseServerFoldersResultDto?>(new() { DisplayPath = _picked.Path }));
            stub.SetHandler(nameof(IEngineApiClient.GetSetupServerFolderRootsAsync), _ => Task.FromResult<IReadOnlyList<ServerStorageLocationDto>>(_configuration.StorageLocations));
            stub.SetHandler(nameof(IEngineApiClient.BrowseSetupServerFoldersAsync), _ => Task.FromResult<BrowseServerFoldersResultDto?>(new() { DisplayPath = _picked.Path }));
            stub.SetHandler(nameof(IEngineApiClient.ValidateServerFolderAsync), args => Task.FromResult<ServerFolderValidationResultDto?>(Validate((ValidateServerFolderRequest)args![0]!)));
            stub.SetHandler(nameof(IEngineApiClient.ValidateSetupServerFolderAsync), args => Task.FromResult<ServerFolderValidationResultDto?>(Validate((ValidateServerFolderRequest)args![0]!)));
        }));
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.LibraryKindsState>();
        _dialogs = Render<AppDialogHost>();
    }

    // The Engine's check of the folder for the chosen handling: managed needs write access.
    private ServerFolderValidationResultDto Validate(ValidateServerFolderRequest request)
    {
        _validations.Add((request.ManualPath ?? string.Empty, request.SelectionMode));
        var managed = request.SelectionMode == ServerFolderSelectionModes.ManagedLibrary;
        return new ServerFolderValidationResultDto
        {
            Path = _picked.Path,
            StorageLocationId = "media",
            Exists = true,
            HasRead = true,
            HasWrite = !managed || _writable,
            CanSelect = !managed || _writable,
            ContainsFiles = _picked.ContainsFiles,
            LooksLikePhoneFootage = _picked.LooksLikePhoneFootage,
        };
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private async Task<IAppDialogReference> OpenWizardAsync(string? group = null, string? type = null, string? setupSession = null, bool embedded = false, Func<string?, Task>? onAdded = null, LibraryKindCatalog? kinds = null)
    {
        var parameters = new AppDialogParameters<AddFolderWizard>
        {
            { component => component.StartGroupKey, group },
            { component => component.StartTypeKey, type },
            { component => component.SetupSession, setupSession },
            { component => component.Embedded, embedded },
            { component => component.Configuration, _configuration },
            { component => component.Kinds, kinds ?? LibraryKindsTestData.Catalog },
            { component => component.OnFolderAdded, onAdded },
        };
        var reference = await Services.GetRequiredService<IAppDialogService>().ShowAsync<AddFolderWizard>(string.Empty, parameters, AddFolderWizard.DialogOptions);
        _dialogs.WaitForElement(".add-wizard");
        return reference;
    }

    private AngleSharp.Dom.IElement Button(string text) => _dialogs.FindAll("button").Single(x => Text(x) == text);

    private void Click(string text) => Button(text).Click();

    private string Steps => Text(_dialogs.Find(".add-wizard__steps"));

    // Step 1 to step 2: the tile of a group, then the card of a kind.
    private void ChooseKind(string group, string kind)
    {
        _dialogs.Find($".add-wizard__tile[data-group={group}]").Click();
        _dialogs.FindAll(".add-wizard__kind-option").Single(x => Text(x) == kind).Click();
        _dialogs.WaitForElement(".server-folder-picker");
    }

    // Step 2 embeds the folder picker. Its Next button confirms the folder the Engine reports; a folder that needs no further
    // question (no phone-footage hint) moves on to step 3 by itself.
    private void PickFolder(string path = "E:\\Incoming\\Films", bool containsFiles = true, bool phoneFootage = false)
    {
        _picked = new ServerFolderValidationResultDto { Path = path, StorageLocationId = "media", Exists = true, HasRead = true, HasWrite = true, CanSelect = true, ContainsFiles = containsFiles, LooksLikePhoneFootage = phoneFootage };
        _dialogs.WaitForElement(".server-folder-picker");
        // The picker validates its starting folder when it opens; Next is live once it holds a folder it can select.
        _dialogs.WaitForAssertion(() => Assert.False(Button("Next").HasAttribute("disabled")));
        Click("Next");
        _dialogs.WaitForAssertion(() => Assert.True(_dialogs.FindAll(".add-wizard__question, .add-wizard__hint").Count > 0));
    }

    private void GoToHow() => _dialogs.WaitForElement(".add-wizard__question");

    // A kind that holds several named libraries asks which one first: a new custom library with a typed name, then on to the folder.
    private void NameNewLibrary(string name)
    {
        _dialogs.WaitForElement("[data-library=new]").Click();
        _dialogs.Find(".add-wizard__field input").Input(name);
        Click("Next");
        _dialogs.WaitForElement(".server-folder-picker");
    }

    private void AddOtherAudioLibraries(params string[] names)
    {
        foreach (var name in names)
        {
            _configuration.Libraries.Add(new()
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = name,
                Kind = "catalogued",
                KindKey = "other_audio",
                Area = "listen",
                MediaTypes = ["Music"],
                Sources = [new() { Id = $"src-{name}", Path = $"C:\\media\\{name}", ManagementMode = "existing_library", AccessMode = "read_only" }],
            });
        }
    }

    private static LibraryKindCatalog EveryKind() => new(LibraryKindsTestData.All());

    // Add folder, and wait for the confirmation (a folder whose files raise a follow-up question is clicked through by its test instead).
    private Task AddFolderAsync()
    {
        Click("Add folder");
        _dialogs.WaitForElement(".add-wizard__done");
        return Task.CompletedTask;
    }

    private LibrarySourceDto AddedSource(LibraryMutationRequest request) => request.Library!.Sources.Single(x => x.Path == _picked.Path);

    // 1. The steps each level shows.
    [Fact]
    public async Task FromTheFrontDoorTheWizardShowsThreeStepsAndNoTilePreselected()
    {
        await OpenWizardAsync();

        Assert.Equal("1 What is it? · 2 Folder · 3 How", Steps);
        Assert.Equal("step", _dialogs.Find(".add-wizard__step.is-current").GetAttribute("aria-current"));
        Assert.StartsWith("1", Text(_dialogs.Find(".add-wizard__step.is-current")));
        Assert.Equal(["Watch", "Listen", "Read", "View"], _dialogs.FindAll(".add-wizard__tile").Select(Text).ToArray());
        Assert.All(_dialogs.FindAll(".add-wizard__tile"), tile => Assert.Equal("false", tile.GetAttribute("aria-pressed")));
        Assert.Empty(_dialogs.FindAll(".add-wizard__kind"));
    }

    [Fact]
    public async Task FromAGroupPageStepOneStartsOnThatGroupWithItsKinds()
    {
        await OpenWizardAsync(group: "listen");

        Assert.Equal("true", _dialogs.Find(".add-wizard__tile[data-group=listen]").GetAttribute("aria-pressed"));
        Assert.Equal("false", _dialogs.Find(".add-wizard__tile[data-group=watch]").GetAttribute("aria-pressed"));
        Assert.Equal(["Music", "Audiobooks"], _dialogs.FindAll(".add-wizard__kind-option").Select(Text).ToArray());
    }

    [Fact]
    public async Task FromATypePageStepOneIsSkippedAndTheIndicatorReadsFolderThenHow()
    {
        await OpenWizardAsync(type: "movies");

        Assert.Equal("1 Folder · 2 How", Steps);
        Assert.Empty(_dialogs.FindAll(".add-wizard__tile"));
        Assert.NotEmpty(_dialogs.FindAll(".server-folder-picker"));
        Assert.DoesNotContain(_dialogs.FindAll("button"), x => Text(x) == "Back");
    }

    // 2. Each handling produces the mutation the panel always produced.
    [Fact]
    public async Task ReadOnlyIsPreselectedAndAddsAnAsIsReadOnlyFolderToTheTypesLibrary()
    {
        await OpenWizardAsync(type: "movies");
        PickFolder();
        GoToHow();
        Assert.Equal("true", _dialogs.Find("[data-handling=readonly]").GetAttribute("aria-checked"));
        await AddFolderAsync();

        var request = Assert.Single(_mutations);
        Assert.Equal(MoviesId, request.Library!.Id);
        Assert.Equal(MoviesId, request.ExpectedLibrary!.Id);
        Assert.Equal(2, request.ExpectedLibrary.Sources.Count);
        var added = AddedSource(request);
        Assert.Equal(3, request.Library.Sources.Count);
        Assert.Equal(("existing_library", "read_only", "local_folder", true), (added.ManagementMode, added.AccessMode, added.SourceType, added.IncludeSubdirectories));
        Assert.Equal(Path.GetFileName(_picked.Path), added.Notes);
        Assert.Equal("movies-main", request.Library.PrimaryDestinationSourceId);
        Assert.Empty(_merges);
        Assert.Empty(_converted);
        Assert.Contains("Folder added to Movies", _dialogs.Markup);
    }

    [Fact]
    public async Task ManagedChecksWriteAccessThenAddsAWritableFolderAndOffersToOrganiseItsFiles()
    {
        await OpenWizardAsync(type: "movies");
        PickFolder();
        GoToHow();
        _dialogs.Find("[data-handling=managed]").Click();
        Click("Add folder");

        // The same question the panel always asked about the files already in a managed folder; nothing is touched until it is answered.
        _dialogs.WaitForAssertion(() => Assert.Contains("Organise now", _dialogs.FindAll("button").Select(Text)));
        var request = Assert.Single(_mutations);
        var added = AddedSource(request);
        Assert.Equal(("managed_by_tuvima", "writable"), (added.ManagementMode, added.AccessMode));
        Assert.Contains((_picked.Path, ServerFolderSelectionModes.ManagedLibrary), _validations);
        Assert.Empty(_converted);

        Click("Organise now");
        _dialogs.WaitForAssertion(() => Assert.Equal([added.Id], _converted));
        _dialogs.WaitForAssertion(() => Assert.Contains("Folder added to Movies", _dialogs.Markup));
    }

    [Fact]
    public async Task ManagedFallsBackToReadOnlyWithANoticeWhenTheFolderCannotBeWritten()
    {
        _writable = false;
        await OpenWizardAsync(type: "movies");
        PickFolder();
        GoToHow();
        _dialogs.Find("[data-handling=managed]").Click();
        await AddFolderAsync();

        var added = AddedSource(Assert.Single(_mutations));
        Assert.Equal(("existing_library", "read_only"), (added.ManagementMode, added.AccessMode));
        Assert.Contains("Library can't write to this folder, so it will be used as read-only.", Text(_dialogs.Find(".add-wizard")));
        Assert.Empty(_converted);
    }

    [Fact]
    public async Task MergeAddsTheFolderReadOnlyThenAsksWhereItsFilesShouldGo()
    {
        await OpenWizardAsync(type: "movies");
        PickFolder();
        GoToHow();
        Assert.Contains(_dialogs.FindAll("[data-handling=merge]"), x => Text(x) == "Merge into your managed Movies folder");
        _dialogs.Find("[data-handling=merge]").Click();
        Click("Add folder");

        _dialogs.WaitForAssertion(() => Assert.Contains("Merge", _dialogs.FindAll("button").Select(Text)));
        var added = AddedSource(Assert.Single(_mutations));
        Assert.Equal(("existing_library", "read_only"), (added.ManagementMode, added.AccessMode));
        Assert.Empty(_merges);

        Click("Merge");
        _dialogs.WaitForAssertion(() => Assert.Single(_merges));
        Assert.Equal((added.Id, "movies-main", "copy"), (_merges[0].SourceId, _merges[0].TargetSourceId, _merges[0].Mode));
        _dialogs.WaitForAssertion(() => Assert.Contains("Folder added to Movies", _dialogs.Markup));
    }

    [Fact]
    public async Task MergeIsOfferedOnlyWhenTheTypeHasAManagedFolderTheFolderHasFilesAndItIsNotSetup()
    {
        // Audiobooks has only a read-only folder, so there is nothing to merge into.
        await OpenWizardAsync(type: "audiobooks");
        PickFolder();
        GoToHow();
        Assert.Empty(_dialogs.FindAll("[data-handling=merge]"));
        Assert.Equal(["readonly", "managed"], _dialogs.FindAll("[data-handling]").Select(x => x.GetAttribute("data-handling")!).ToArray());
    }

    [Fact]
    public async Task MergeIsNotOfferedInFirstRunSetup()
    {
        await OpenWizardAsync(type: "movies", setupSession: "session", embedded: true);
        PickFolder();
        GoToHow();
        Assert.Empty(_dialogs.FindAll("[data-handling=merge]"));
    }

    [Fact]
    public async Task AFolderForATypeWithNoLibraryYetCreatesTheLibraryWithItsFirstFolder()
    {
        await OpenWizardAsync(type: "music");
        PickFolder(path: "E:\\Albums");
        GoToHow();
        await AddFolderAsync();

        var library = Assert.Single(_mutations).Library!;
        Assert.Equal(("Music", "listen", "catalogued", "enriched"), (library.Name, library.Area, library.Kind, library.MetadataPolicy));
        Assert.Equal(["Music"], library.MediaTypes);
        Assert.Equal("E:\\Albums", Assert.Single(library.Sources).Path);
    }

    [Fact]
    public async Task AFolderAddedAsOtherVideosSavesLocalOnlyWithYoutubeOff_AndTheWizardAsksNothingAboutYoutube()
    {
        await OpenWizardAsync(type: "other_videos");
        NameNewLibrary("Courses");
        PickFolder(path: "E:\\Courses");
        GoToHow();
        Assert.DoesNotContain("contacts YouTube", _dialogs.Markup);
        Assert.Empty(_dialogs.FindAll("input[role=switch]"));
        await AddFolderAsync();

        var library = Assert.Single(_mutations).Library!;
        Assert.False(library.YoutubeLookup);
        Assert.Equal("local_only", library.MetadataPolicy);
        Assert.Equal("other_videos", library.KindKey);
        Assert.True(library.RecycleBin);
    }

    // Named custom libraries (D51) and the colour of a kind's card (D39).
    [Fact]
    public async Task ALookupKindCardIsFilledWithItsAreasColourAndAPlainKindCardIsQuietGrey()
    {
        await OpenWizardAsync(group: "watch");

        var movies = _dialogs.Find(".add-wizard__kind-option[data-type=movies] .lib-kind-tile");
        Assert.Contains("lib-kind-tile--lookup", movies.ClassList);
        Assert.Contains("lib-area--watch", movies.ClassList);
        var plain = _dialogs.Find(".add-wizard__kind-option[data-type=other_videos] .lib-kind-tile");
        Assert.DoesNotContain("lib-kind-tile--lookup", plain.ClassList);
        Assert.DoesNotContain(plain.ClassList, name => name.StartsWith("lib-area--", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AKindSuppliedByAPluginGetsItsAreasColourOnItsCardWithNoCodeChange()
    {
        var podcasts = LibraryKindsTestData.Podcasts();
        podcasts.LookupProviders = ["musicbrainz"];

        await OpenWizardAsync(group: "listen", kinds: new LibraryKindCatalog(LibraryKindsTestData.BuiltIn(podcasts)));

        var tile = _dialogs.Find(".add-wizard__kind-option[data-type=podcasts] .lib-kind-tile");
        Assert.Contains("lib-kind-tile--lookup", tile.ClassList);
        Assert.Contains("lib-area--listen", tile.ClassList);
    }

    [Fact]
    public async Task OtherAudioOffersItsExistingLibrariesByNameAndANewCustomLibrary()
    {
        AddOtherAudioLibraries("Vinyl rips", "Voice notes");

        await OpenWizardAsync(type: "other_audio", kinds: EveryKind());

        Assert.Equal("1 Library · 2 Folder · 3 How", Steps);
        Assert.Equal(["Vinyl rips", "Voice notes", "New custom library"], _dialogs.FindAll("[data-library]").Select(Text).ToArray());
        Assert.Equal("true", _dialogs.Find("[data-library]").GetAttribute("aria-checked"));
        Assert.Empty(_dialogs.FindAll("[data-library-limit]"));
    }

    [Fact]
    public async Task ANewCustomLibraryNeedsANameAndIsSavedUnderTheTypedNameNotTheKindLabel()
    {
        AddOtherAudioLibraries("Vinyl rips");
        await OpenWizardAsync(type: "other_audio", kinds: EveryKind());

        _dialogs.Find("[data-library=new]").Click();
        Assert.True(Button("Next").HasAttribute("disabled"));
        _dialogs.Find(".add-wizard__field input").Input("Lectures");
        Assert.False(Button("Next").HasAttribute("disabled"));
        Click("Next");
        _dialogs.WaitForElement(".server-folder-picker");
        PickFolder(path: "E:\\Lectures");
        GoToHow();
        await AddFolderAsync();

        var request = Assert.Single(_mutations);
        Assert.Equal("Lectures", request.Library!.Name);
        Assert.Equal("other_audio", request.Library.KindKey);
        Assert.Null(request.ExpectedLibrary);
        Assert.Single(request.Library.Sources);
        Assert.Equal(2, _configuration.Libraries.Count(library => library.KindKey == "other_audio"));
        // The confirmation names the library the folder joined, not the kind.
        _dialogs.WaitForAssertion(() => Assert.Equal("Folder added to Lectures", Text(_dialogs.Find(".add-wizard__done h3"))));
    }

    [Fact]
    public async Task TheFirstLibraryOfAKindStartsOutNamedForTheKindAndCanBeEdited()
    {
        var label = LibraryKindsTestData.Catalog.Find("other_videos")!.Label;
        await OpenWizardAsync(type: "other_videos");

        Assert.Equal("true", _dialogs.Find("[data-library=new]").GetAttribute("aria-checked"));
        Assert.Equal(label, _dialogs.Find(".add-wizard__field input").GetAttribute("value"));
        Assert.False(Button("Next").HasAttribute("disabled"));
        Click("Next");
        _dialogs.WaitForElement(".server-folder-picker");
        PickFolder(path: "E:\\Courses");
        GoToHow();
        await AddFolderAsync();

        Assert.Equal(label, Assert.Single(_mutations).Library!.Name);
    }

    [Fact]
    public async Task WhenLibrariesAlreadyExistANewCustomLibraryStartsWithAnEmptyName()
    {
        AddOtherAudioLibraries("Vinyl rips");
        await OpenWizardAsync(type: "other_audio", kinds: EveryKind());

        _dialogs.Find("[data-library=new]").Click();

        Assert.Equal(string.Empty, _dialogs.Find(".add-wizard__field input").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public async Task MergeIsOfferedForAnExistingLibraryWithAManagedFolderButNeverForANewCustomLibrary()
    {
        _configuration.Libraries.Add(new()
        {
            Id = "77777777-7777-4777-8777-777777777777",
            Name = "Podcasts",
            Kind = "catalogued",
            KindKey = "other_audio",
            Area = "listen",
            MediaTypes = ["Music"],
            PrimaryDestinationSourceId = "pod-main",
            Sources = [new() { Id = "pod-main", Path = "E:\\Library\\Podcasts", ManagementMode = "managed_by_tuvima", AccessMode = "writable" }],
        });
        await OpenWizardAsync(type: "other_audio", kinds: EveryKind());

        // The existing library is chosen first: its managed folder is a place to merge into.
        Click("Next");
        _dialogs.WaitForElement(".server-folder-picker");
        PickFolder(path: "E:\\Loose podcasts");
        GoToHow();
        Assert.NotEmpty(_dialogs.FindAll("[data-handling=merge]"));
        Click("Back");
        Click("Back");

        _dialogs.Find("[data-library=new]").Click();
        _dialogs.Find(".add-wizard__field input").Input("Lectures");
        Click("Next");
        _dialogs.WaitForElement(".server-folder-picker");
        PickFolder(path: "E:\\Loose lectures");
        GoToHow();
        Assert.Empty(_dialogs.FindAll("[data-handling=merge]"));
        Assert.NotEmpty(_dialogs.FindAll("[data-handling=readonly]"));
    }

    [Fact]
    public async Task ChoosingAnExistingLibraryAddsTheFolderToThatLibrary()
    {
        AddOtherAudioLibraries("Vinyl rips", "Voice notes");
        var voice = _configuration.Libraries.Single(library => library.Name == "Voice notes");
        await OpenWizardAsync(type: "other_audio", kinds: EveryKind());

        _dialogs.Find($"[data-library='{voice.Id}']").Click();
        Click("Next");
        _dialogs.WaitForElement(".server-folder-picker");
        PickFolder(path: "E:\\Memos");
        GoToHow();
        await AddFolderAsync();

        var request = Assert.Single(_mutations);
        Assert.Equal(voice.Id, request.Library!.Id);
        Assert.Equal("Voice notes", request.Library.Name);
        Assert.Equal(2, request.Library.Sources.Count);
    }

    [Fact]
    public async Task AtTheLimitNewCustomLibraryIsHiddenAndOnePlainLineSaysSo()
    {
        AddOtherAudioLibraries("One", "Two", "Three", "Four", "Five");

        await OpenWizardAsync(type: "other_audio", kinds: EveryKind());

        Assert.Equal(["One", "Two", "Three", "Four", "Five"], _dialogs.FindAll("[data-library]").Select(Text).ToArray());
        Assert.Empty(_dialogs.FindAll("[data-library=new]"));
        Assert.Contains("5", Text(_dialogs.Find("[data-library-limit]")));
    }

    [Fact]
    public async Task AMoviesFolderStillOffersNoLibraryChoice()
    {
        await OpenWizardAsync(type: "movies");

        Assert.Equal("1 Folder · 2 How", Steps);
        Assert.Empty(_dialogs.FindAll("[data-library]"));
    }

    [Fact]
    public async Task TheEnginesRefusalOfADuplicateLibraryNameIsShownUnchanged()
    {
        AddOtherAudioLibraries("Lectures");
        _refuseSave = "libraries[3].name \"Lectures\" is already used by another library in listen. Give each library its own name.";
        await OpenWizardAsync(type: "other_audio", kinds: EveryKind());

        NameNewLibrary("Lectures");
        PickFolder(path: "E:\\Lectures2");
        GoToHow();
        Click("Add folder");

        _dialogs.WaitForAssertion(() => Assert.Contains("is already used by another library in listen", _dialogs.Markup));
        Assert.Empty(_dialogs.FindAll(".add-wizard__done"));
    }

    [Fact]
    public async Task InFirstRunSetupTheSameLibraryChoiceAppliesAndSavesTheNameThroughTheSetupSession()
    {
        await OpenWizardAsync(type: "other_audio", setupSession: "setup-1", embedded: true, kinds: EveryKind());

        Assert.Equal("1 Library · 2 Folder · 3 How", Steps);
        NameNewLibrary("Podcast archive");
        PickFolder(path: "E:\\Archive");
        GoToHow();
        await AddFolderAsync();

        var update = Assert.Single(_setupUpdates);
        Assert.Equal("Podcast archive", Assert.Single(update.Libraries, library => library.KindKey == "other_audio").Name);
    }

    [Fact]
    public async Task AFolderAddedAsYoutubeSavesLocalIdentityWithTheYoutubeLookupOn()
    {
        await OpenWizardAsync(type: "youtube", kinds: new LibraryKindCatalog(LibraryKindsTestData.All()));
        PickFolder(path: "E:\\Saved from YouTube");
        GoToHow();
        await AddFolderAsync();

        var library = Assert.Single(_mutations).Library!;
        Assert.True(library.YoutubeLookup);
        Assert.Equal("local_only", library.MetadataPolicy);
        Assert.Equal("youtube", library.KindKey);
        Assert.Equal(["Videos"], library.MediaTypes);
    }

    [Theory]
    [InlineData("other_audio", "Music")]
    [InlineData("other_reading", "Books")]
    public async Task AFolderAddedAsAPlainKindSavesLocalOnlyWithYoutubeOff(string kind, string mediaType)
    {
        await OpenWizardAsync(type: kind, kinds: new LibraryKindCatalog(LibraryKindsTestData.All()));
        NameNewLibrary("Loose");
        PickFolder(path: "E:\\Loose files");
        GoToHow();
        await AddFolderAsync();

        var library = Assert.Single(_mutations).Library!;
        Assert.Equal(kind, library.KindKey);
        Assert.Equal("local_only", library.MetadataPolicy);
        Assert.False(library.YoutubeLookup);
        Assert.Equal([mediaType], library.MediaTypes);
    }

    [Fact]
    public async Task AFolderAddedAsALookupKindKeepsItsEnrichedPolicy()
    {
        await OpenWizardAsync(type: "movies");
        PickFolder(path: "E:\\Films");
        GoToHow();
        await AddFolderAsync();

        var library = Assert.Single(_mutations).Library!;
        Assert.Equal("enriched", library.MetadataPolicy);
        Assert.Equal("movies", library.KindKey);
    }

    // 3. An empty folder asks one question.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnEmptyFolderAsksOnlyWhetherNewMediaShouldGoHereAndAddsItManaged(bool yes)
    {
        await OpenWizardAsync(type: "movies");
        PickFolder(path: "E:\\New", containsFiles: false);
        GoToHow();

        Assert.Equal("Should new media go here?", Text(_dialogs.Find(".add-wizard__question")));
        Assert.Empty(_dialogs.FindAll("[data-handling]"));
        Assert.Equal("true", _dialogs.Find("[data-answer=yes]").GetAttribute("aria-checked"));
        _dialogs.Find(yes ? "[data-answer=yes]" : "[data-answer=no]").Click();
        await AddFolderAsync();

        var first = _mutations[0];
        var added = AddedSource(first);
        Assert.Equal(("managed_by_tuvima", "writable"), (added.ManagementMode, added.AccessMode));
        // The marker is only moved on a Yes, using the existing destination save; a No leaves it where it was.
        Assert.Equal("movies-main", first.Library!.PrimaryDestinationSourceId);
        if (yes)
        {
            Assert.Equal(2, _mutations.Count);
            Assert.Equal(added.Id, _mutations[1].Library!.PrimaryDestinationSourceId);
        }
        else
        {
            Assert.Single(_mutations);
        }

        Assert.Empty(_converted);
    }

    [Fact]
    public async Task AnEmptyFolderForATypeWithNoManagedFolderSavesOnceAndLeavesTheMarkerToTheEngine()
    {
        await OpenWizardAsync(type: "music");
        PickFolder(path: "E:\\Albums", containsFiles: false);
        GoToHow();
        await AddFolderAsync();

        var request = Assert.Single(_mutations);
        Assert.Equal(AddedSource(request).Id, request.Library!.PrimaryDestinationSourceId);
    }

    // 4. Phone footage.
    [Fact]
    public async Task APhoneFootageFolderInAWatchKindShowsTheHintAndAddToViewSwitchesBeforeAnythingIsSaved()
    {
        await OpenWizardAsync(type: "movies");
        PickFolder(path: "F:\\Phone", phoneFootage: true);

        Assert.Equal("This looks like phone or camera footage. Personal videos belong in View.", Text(_dialogs.Find(".add-wizard__hint > span")));
        Assert.Equal(["Add to View instead", "Continue anyway"], _dialogs.FindAll(".add-wizard__hint button").Select(Text).ToArray());
        // The hint shows on the folder step instead of moving on.
        Assert.Empty(_dialogs.FindAll(".add-wizard__question"));
        Click("Add to View instead");

        var navigation = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith("/settings/libraries/view/personal_videos?add=folder&path=" + Uri.EscapeDataString("F:\\Phone"), navigation.Uri, StringComparison.Ordinal);
        Assert.Empty(_mutations);
        Assert.Empty(_setupUpdates);
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".add-wizard")));
    }

    [Fact]
    public async Task ContinueAnywayOnThePhoneFootageHintMovesOnToStepThree()
    {
        await OpenWizardAsync(type: "movies");
        PickFolder(path: "F:\\Phone", phoneFootage: true);

        Click("Continue anyway");

        _dialogs.WaitForElement(".add-wizard__question");
        Assert.Empty(_dialogs.FindAll(".add-wizard__hint"));
        Assert.Empty(_mutations);
    }

    // First-run setup must not navigate out of setup: the hint only says where View is set up.
    [Fact]
    public async Task InSetupThePhoneFootageHintOnlyOffersContinueAnywayAndNeverLeavesSetup()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        var before = navigation.Uri;
        await OpenWizardAsync(type: "movies", setupSession: "session", embedded: true);
        PickFolder(path: "F:\\Phone", phoneFootage: true);

        Assert.Equal("This looks like phone or camera footage. Personal videos belong in View; you can set View up later in Settings > Libraries > View.", Text(_dialogs.Find(".add-wizard__hint > span")));
        Assert.Equal(["Continue anyway"], _dialogs.FindAll(".add-wizard__hint button").Select(Text).ToArray());
        Assert.Equal(before, navigation.Uri);

        Click("Continue anyway");
        _dialogs.WaitForElement(".add-wizard__question");
        Assert.Equal(before, navigation.Uri);
    }

    [Fact]
    public async Task InSetupStepOneDoesNotOfferViewSoNothingHandsOverToViewsSettings()
    {
        // Setup always opens the wizard on a known type, so step 1 is normally skipped; if it is shown, View is not on it.
        await OpenWizardAsync(embedded: true, setupSession: "session");

        Assert.Equal(["Watch", "Listen", "Read"], _dialogs.FindAll(".add-wizard__tile").Select(Text).ToArray());
        Assert.Empty(_dialogs.FindAll(".add-wizard__tile[data-group=view]"));
    }

    [Fact]
    public async Task OrdinaryFootageAndNonWatchKindsShowNoHint()
    {
        await OpenWizardAsync(type: "movies");
        PickFolder(path: "F:\\Films");
        Assert.Empty(_dialogs.FindAll(".add-wizard__hint"));

        // Phone footage only matters for the Watch kinds.
        Click("Cancel");
        await OpenWizardAsync(type: "music");
        PickFolder(path: "F:\\Songs", phoneFootage: true);
        Assert.Empty(_dialogs.FindAll(".add-wizard__hint"));
    }

    // 5. Cancel creates nothing.
    [Fact]
    public async Task CancellingAtAnyStepCreatesNothing()
    {
        // Step 1.
        var first = await OpenWizardAsync();
        Click("Cancel");
        Assert.True((await first.Result)!.Canceled);

        // Step 2.
        // The phone-footage hint holds the person on step 2 after confirming the folder.
        var second = await OpenWizardAsync(type: "movies");
        PickFolder(path: "F:\\Phone", phoneFootage: true);
        Assert.NotEmpty(_dialogs.FindAll(".add-wizard__hint"));
        Click("Cancel");
        Assert.True((await second.Result)!.Canceled);

        // Step 3.
        var third = await OpenWizardAsync(type: "movies");
        PickFolder();
        GoToHow();
        Click("Cancel");
        Assert.True((await third.Result)!.Canceled);

        Assert.Empty(_mutations);
        Assert.Empty(_setupUpdates);
    }

    // 6. Back keeps what was chosen.
    [Fact]
    public async Task BackFromTheFolderStepKeepsTheChosenKind()
    {
        await OpenWizardAsync();
        ChooseKind("watch", "Movies");
        Assert.Equal("2 Folder", Regex.Match(Steps, "2 Folder").Value);

        Click("Back");

        Assert.Equal("true", _dialogs.Find(".add-wizard__tile[data-group=watch]").GetAttribute("aria-pressed"));
        Assert.Contains("is-selected", _dialogs.FindAll(".add-wizard__kind-option").Single(x => Text(x) == "Movies").ClassName);
        Assert.DoesNotContain("is-selected", _dialogs.FindAll(".add-wizard__kind-option").Single(x => Text(x) == "TV Shows").ClassName);
    }

    [Fact]
    public async Task BackFromTheHowStepKeepsTheFolder()
    {
        await OpenWizardAsync(type: "movies");
        PickFolder(path: "E:\\Kept");
        GoToHow();

        Click("Back");

        // The picker opens on the folder that was chosen.
        _dialogs.WaitForAssertion(() => Assert.Equal("E:\\Kept", _dialogs.Find("input[aria-label='Selected path']").GetAttribute("value")));
        _dialogs.WaitForAssertion(() => Assert.False(Button("Next").HasAttribute("disabled")));
        Assert.Empty(_mutations);
    }

    // 7. View.
    [Fact]
    public async Task ChoosingViewInStepOneHandsOverToViewsOwnAddFormAndSavesNothing()
    {
        var reference = await OpenWizardAsync();
        _dialogs.Find(".add-wizard__tile[data-group=view]").Click();
        Assert.Equal(["Photos", "Personal videos", "Documents"], _dialogs.FindAll(".add-wizard__kind-option").Select(Text).ToArray());
        _dialogs.FindAll(".add-wizard__kind-option").Single(x => Text(x) == "Photos").Click();

        Assert.EndsWith("/settings/libraries/view/photos?add=folder", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.True((await reference.Result)!.Canceled);
        Assert.Empty(_mutations);
    }

    [Fact]
    public async Task ChoosingDocumentsInStepOneHandsOverToTheDocumentsAddFormAndSavesNothing()
    {
        var reference = await OpenWizardAsync();
        _dialogs.Find(".add-wizard__tile[data-group=view]").Click();
        _dialogs.FindAll(".add-wizard__kind-option").Single(x => Text(x) == "Documents").Click();

        Assert.EndsWith("/settings/libraries/view/documents?add=folder", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.True((await reference.Result)!.Canceled);
        Assert.Empty(_mutations);
    }

    [Fact]
    public void ViewOpensItsAddFormWhenStartedAddingAndNotOtherwise()
    {
        var plain = Render<ViewLibrarySettings>(parameters => parameters.Add(x => x.Configuration, _configuration));
        plain.WaitForAssertion(() => Assert.Empty(plain.FindAll(".libraries-form-grid")));

        var started = Render<ViewLibrarySettings>(parameters => parameters
            .Add(x => x.Configuration, _configuration)
            .Add(x => x.StartAdding, true)
            .Add(x => x.InitialPath, "F:\\Phone"));
        started.WaitForAssertion(() => Assert.Single(started.FindAll(".libraries-form-grid")));
        Assert.Contains("F:\\Phone", started.Markup);
    }

    [Fact]
    public void LibrariesTabPassesTheHandOverToViewsAddForm()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/libraries/view/photos?add=folder");
        var cut = Render<LibrariesTab>(parameters => parameters.Add(x => x.Subsection, "view").Add(x => x.Detail, "photos"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".libraries-form-grid")));
        // The address then drops the hand-over, so a reload or Back does not reopen the form.
        cut.WaitForAssertion(() => Assert.EndsWith("/settings/libraries/view/photos", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal));
        Assert.Single(cut.FindAll(".libraries-form-grid"));
    }

    // 8. Phone: no Add folder on any level.
    [Fact]
    public async Task OnAPhoneNoLevelOffersAddFolder()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync("mobile");

        var door = Render<LibraryFrontDoor>(parameters => parameters.Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.Configuration, _configuration).Add(x => x.ReadOnly, true));
        Assert.Empty(door.FindAll("button"));
        Assert.DoesNotContain("Add folder", door.Markup);

        var group = Render<LibraryGroupPage>(parameters => parameters
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog)
            .Add(x => x.Group, LibraryKindsTestData.Catalog.TryGetGroup("watch")!)
            .Add(x => x.Configuration, _configuration)
            .Add(x => x.ReadOnly, true));
        Assert.Empty(group.FindAll("button"));

        var type = Render<LibrariesTab>(parameters => parameters.Add(x => x.Subsection, "watch").Add(x => x.Detail, "movies"));
        type.WaitForElement(".library-folders__add");
        Assert.True(type.Find(".library-folders__add").HasAttribute("disabled"));
    }

    [Fact]
    public void OnDesktopTheFrontDoorAndGroupPageOfferAddFolderAndTheFrontDoorMakesItPrimaryOnlyWhenNothingExists()
    {
        var door = Render<LibraryFrontDoor>(parameters => parameters.Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.Configuration, _configuration));
        var button = door.FindAll("button").Single(x => Text(x) == "Add folder");
        Assert.DoesNotContain("primary", button.ClassName, StringComparison.OrdinalIgnoreCase);

        var empty = BuildConfiguration();
        empty.Libraries.ForEach(library => library.Sources.Clear());
        var emptyDoor = Render<LibraryFrontDoor>(parameters => parameters.Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.Configuration, empty));
        var primary = emptyDoor.FindAll("button").Single(x => Text(x) == "Add folder");
        Assert.Contains("primary", primary.ClassName, StringComparison.OrdinalIgnoreCase);

        var group = Render<LibraryGroupPage>(parameters => parameters.Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.Group, LibraryKindsTestData.Catalog.TryGetGroup("read")!).Add(x => x.Configuration, _configuration));
        Assert.Single(group.FindAll("button"), x => Text(x) == "Add folder");
    }

    [Fact]
    public async Task AddFolderOnTheFrontDoorAndAGroupPageOpensTheWizardOnTheRightStep()
    {
        var door = Render<LibraryFrontDoor>(parameters => parameters.Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.Configuration, _configuration));
        door.FindAll("button").Single(x => Text(x) == "Add folder").Click();
        _dialogs.WaitForElement(".add-wizard");
        Assert.Equal("1 What is it? · 2 Folder · 3 How", Steps);
        Click("Cancel");
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".add-wizard")));

        var group = Render<LibraryGroupPage>(parameters => parameters.Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.Group, LibraryKindsTestData.Catalog.TryGetGroup("read")!).Add(x => x.Configuration, _configuration));
        group.FindAll("button").Single(x => Text(x) == "Add folder").Click();
        _dialogs.WaitForElement(".add-wizard");
        Assert.Equal("true", _dialogs.Find(".add-wizard__tile[data-group=read]").GetAttribute("aria-pressed"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AddFolderOnATypePageOpensTheWizardAtTheFolderStepAndTheNewFolderShowsInTheTable()
    {
        var cut = Render<LibrariesTab>(parameters => parameters.Add(x => x.Subsection, "watch").Add(x => x.Detail, "movies"));
        cut.WaitForElement(".folder-table");
        cut.Find(".library-folders__add").Click();
        _dialogs.WaitForElement(".add-wizard");
        Assert.Equal("1 Folder · 2 How", Steps);

        PickFolder(path: "E:\\Incoming\\Films");
        GoToHow();
        await AddFolderAsync();

        cut.WaitForAssertion(() => Assert.Contains("E:\\Incoming\\Films", cut.Markup));
    }

    // 9. First-run setup uses the setup calls.
    [Fact]
    public async Task SetupAddsAFolderThroughTheWizardWithTheSetupCalls()
    {
        var cut = Render<LibraryFoldersPanel>(parameters => parameters.Add(x => x.Kinds, LibraryKindsTestData.Catalog).Add(x => x.Embedded, true).Add(x => x.SetupSession, "session"));
        cut.WaitForElement(".library-folders__type");
        cut.FindAll(".library-folders__type").Single(x => x.TextContent.Contains("Movies")).Click();
        cut.WaitForElement(".folder-table");
        cut.Find(".library-folders__add").Click();
        _dialogs.WaitForElement(".add-wizard");
        Assert.Equal("1 Folder · 2 How", Steps);

        PickFolder(path: "E:\\Setup");
        GoToHow();
        await AddFolderAsync();

        Assert.Empty(_mutations);
        var update = Assert.Single(_setupUpdates);
        var movies = update.Libraries.Single(x => x.Id == MoviesId);
        Assert.Equal(3, movies.Sources.Count);
        Assert.Equal("existing_library", movies.Sources.Single(x => x.Path == "E:\\Setup").ManagementMode);
        Assert.Equal(2, update.Libraries.Count);
        cut.WaitForAssertion(() => Assert.Contains("E:\\Setup", cut.Markup));
    }

    // 10. Tooltips.
    [Fact]
    public async Task EveryKindCardHasAnInfoCircleWithItsText()
    {
        await OpenWizardAsync();
        var expected = new Dictionary<string, string>
        {
            ["watch"] = "Feature films, one file or folder per film.|Series with seasons and episodes, one folder per show.|Courses, talks and downloads. Nothing is looked up.",
            ["listen"] = "Albums and songs, tagged or in artist and album folders.|Spoken books, one file or folder per book.",
            ["read"] = "EPUB and PDF books.|CBZ and CBR comics and manga.",
            ["view"] = "Pictures from phones and cameras. View sorts them by date and place.|Videos from phones and cameras, kept with the day they were taken.|PDFs, Office files and photos of paper. Text inside them can be searched.",
        };
        foreach (var (group, tips) in expected)
        {
            _dialogs.Find($".add-wizard__tile[data-group={group}]").Click();
            var kinds = _dialogs.FindAll(".add-wizard__kind");
            Assert.Equal(tips.Split('|'), kinds.Select(kind => kind.QuerySelector("[data-playback-tooltip]")!.GetAttribute("data-playback-tooltip")!).ToArray());
            Assert.All(kinds, kind =>
            {
                Assert.NotNull(kind.QuerySelector(".add-wizard__info"));
                // No second line of text on the card itself.
                Assert.Single(kind.QuerySelector(".add-wizard__kind-option")!.Children, child => child.TagName == "STRONG");
                Assert.DoesNotContain(kind.QuerySelector(".add-wizard__kind-option")!.QuerySelectorAll("span:not(.tl-icon-root)"), x => !string.IsNullOrWhiteSpace(x.TextContent));
            });
        }
    }

    // A kind a plugin adds is another entry in the list, so step 1 offers it under its group with its own (i) text.
    [Fact]
    public async Task AKindFromAPluginIsOfferedInStepOneUnderItsGroupWithItsOwnText()
    {
        var kinds = new LibraryKindCatalog(LibraryKindsTestData.BuiltIn(LibraryKindsTestData.Podcasts()));
        await OpenWizardAsync(group: "listen", kinds: kinds);

        var offered = _dialogs.FindAll(".add-wizard__kind");
        Assert.Equal(["Music", "Audiobooks", "Podcasts"], offered.Select(kind => Text(kind.QuerySelector("strong")!)).ToArray());
        Assert.Equal("Episodes of shows you follow, one folder per show.", offered[2].QuerySelector("[data-playback-tooltip]")!.GetAttribute("data-playback-tooltip"));

        _dialogs.Find(".add-wizard__kind-option[data-type=podcasts]").Click();
        _dialogs.WaitForElement(".server-folder-picker");
        Assert.Contains("holds your Podcasts", _dialogs.Find(".add-wizard__lead").TextContent);
    }

    [Fact]
    public async Task EachHowOptionHasAnInfoCircleAndManagedShowsAWarningIconWithoutHovering()
    {
        await OpenWizardAsync(type: "movies");
        PickFolder();
        GoToHow();

        string Tip(string handling) => _dialogs.Find($"[data-handling={handling}]").ParentElement!.QuerySelector("[data-playback-tooltip]")!.GetAttribute("data-playback-tooltip")!;
        Assert.Equal("Library reads the files and never changes them. Best for folders other apps also use.", Tip("readonly"));
        Assert.Equal("Library moves these files into your managed folder, renamed and tagged.", Tip("merge"));
        Assert.Equal("Library renames, moves and tags the files here. Other apps that use this folder may stop finding them.", Tip("managed"));
        Assert.NotNull(_dialogs.Find("[data-handling=managed] .add-wizard__warning"));
        Assert.Empty(_dialogs.FindAll("[data-handling=readonly] .add-wizard__warning"));
        Assert.Empty(_dialogs.FindAll("[data-handling=merge] .add-wizard__warning"));
    }

    // The confirmation.
    [Fact]
    public async Task AfterAddingTheWizardOffersGoToTheTypeAndAddAnother()
    {
        var added = new List<string?>();
        await OpenWizardAsync(type: "movies", onAdded: notice => { added.Add(notice); return Task.CompletedTask; });
        PickFolder();
        GoToHow();
        await AddFolderAsync();

        Assert.Equal("Folder added to Movies", Text(_dialogs.Find(".add-wizard__done h3")));
        Assert.Single(added);
        Assert.Contains("Go to Movies", _dialogs.FindAll("button").Select(Text));

        Click("Add another");
        // Started from a type page, so it comes back to the folder step.
        Assert.Equal("1 Folder · 2 How", Steps);
        Assert.Empty(_dialogs.FindAll(".add-wizard__question"));

        PickFolder(path: "E:\\More");
        GoToHow();
        await AddFolderAsync();
        Click("Go to Movies");
        Assert.EndsWith("/settings/libraries/watch/movies", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAnotherFromTheFrontDoorRestartsAtStepOne()
    {
        await OpenWizardAsync();
        ChooseKind("watch", "Movies");
        PickFolder();
        GoToHow();
        await AddFolderAsync();

        Click("Add another");

        Assert.Equal("1 What is it? · 2 Folder · 3 How", Steps);
        Assert.NotEmpty(_dialogs.FindAll(".add-wizard__tile"));
    }

    [Fact]
    public async Task ASaveThatTheEngineRefusesShowsTheMessageInlineAndKeepsThePersonOnTheLastStep()
    {
        var stub = Services.GetRequiredService<IEngineApiClient>();
        await OpenWizardAsync(type: "movies");
        PickFolder();
        GoToHow();
        // Another change got in first: the Engine answers nothing for this save.
        ((EngineApiClientStub)(object)stub).SetHandler(nameof(IEngineApiClient.MutateLibraryAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(null));
        Click("Add folder");

        _dialogs.WaitForAssertion(() => Assert.Contains("That change could not be saved. Check the folder and try again.", Text(_dialogs.Find(".add-wizard"))));
        Assert.NotEmpty(_dialogs.FindAll(".add-wizard__question"));
        Assert.Empty(_dialogs.FindAll(".add-wizard__done"));
    }
}
