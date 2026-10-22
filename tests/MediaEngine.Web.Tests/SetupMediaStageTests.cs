using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Setup;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Setup;
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

// First-run setup's "What do you have?" step: four tick cards, a section per ticked group with the Add folder wizard, and the
// unchanged footer (Skip for now, Continue), step key and deferral.
public sealed class SetupMediaStageTests : AsyncBunitContext
{
    private const string Session = "setup-session";
    private const string MusicId = "33333333-3333-4333-8333-333333333333";

    private LibrariesConfigurationDto _configuration = new()
    {
        StorageLocations = [new() { Id = "media", Label = "Media storage", Path = "C:\\media", AllowWrite = true }],
    };

    private readonly List<UpdateLibrariesRequest> _setupUpdates = [];
    private readonly List<string?> _setupSessions = [];
    private readonly List<string?> _updateSessions = [];
    private bool _failLoads;
    private bool _failKinds;
    private List<LibraryKindDto> _kinds = LibraryKindsTestData.BuiltIn();
    private ServerFolderValidationResultDto _picked = new() { Path = "E:\\Setup\\Films", StorageLocationId = "media", Exists = true, HasRead = true, HasWrite = true, CanSelect = true, ContainsFiles = true };
    private IRenderedComponent<AppDialogHost> _dialogs = null!;
    private int _deferred;
    private int _validated;
    private int _created;
    private static readonly Guid Me = Guid.Parse("a1a1a1a1-a1a1-4a1a-8a1a-a1a1a1a1a1a1");
    private EngineApiClientStub _stub = null!;
    private readonly List<ViewSourceAdminDto> _sharedSources = [];
    private readonly List<ViewSourceAdminDto> _mySources = [];
    private readonly List<CreateViewSourceRequest> _sharedCreates = [];
    private readonly List<(Guid Profile, CreateViewSourceRequest Request)> _profileCreates = [];
    private string? _refusal;

    public SetupMediaStageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            _stub = stub;
            stub.SetHandler("get_LastError", _ => _refusal);
            stub.SetHandler(nameof(IEngineApiClient.GetViewSharedSourcesAsync), _ => Task.FromResult<IReadOnlyList<ViewSourceAdminDto>?>([.. _sharedSources]));
            stub.SetHandler(nameof(IEngineApiClient.GetViewProfileSourcesAsync), args =>
                Task.FromResult<ViewPersonalSpaceAdminReviewDto?>(new((Guid)args![0]!, null, [.. _mySources], [])));
            stub.SetHandler(nameof(IEngineApiClient.CreateViewSharedSourceAsync), args =>
            {
                var request = (CreateViewSourceRequest)args![0]!;
                if (_refusal is not null)
                {
                    return Task.FromResult<ViewSourceAdminDto?>(null);
                }

                _sharedCreates.Add(request);
                return Task.FromResult<ViewSourceAdminDto?>(Remember(_sharedSources, request));
            });
            stub.SetHandler(nameof(IEngineApiClient.CreateViewProfileSourceAsync), args =>
            {
                var request = (CreateViewSourceRequest)args![1]!;
                if (_refusal is not null)
                {
                    return Task.FromResult<ViewSourceAdminDto?>(null);
                }

                _profileCreates.Add(((Guid)args[0]!, request));
                return Task.FromResult<ViewSourceAdminDto?>(Remember(_mySources, request));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetServerFolderRootsAsync), _ => Task.FromResult<IReadOnlyList<ServerStorageLocationDto>>(_configuration.StorageLocations));
            stub.SetHandler(nameof(IEngineApiClient.BrowseServerFoldersAsync), _ => Task.FromResult<BrowseServerFoldersResultDto?>(new() { DisplayPath = _picked.Path }));
            stub.SetHandler(nameof(IEngineApiClient.ValidateServerFolderAsync), _ => Task.FromResult<ServerFolderValidationResultDto?>(_picked));
            stub.SetHandler(nameof(IEngineApiClient.GetSetupLibrariesAsync), args =>
            {
                _setupSessions.Add((string?)args![0]);
                return Task.FromResult<LibrariesConfigurationDto?>(_failLoads ? null : _configuration);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetSetupLibraryKindsAsync), _ =>
                Task.FromResult<IReadOnlyList<LibraryKindDto>?>(_failKinds ? null : _kinds));
            stub.SetHandler(nameof(IEngineApiClient.UpdateSetupLibrariesAsync), args =>
            {
                var request = (UpdateLibrariesRequest)args![0]!;
                _setupUpdates.Add(request);
                _updateSessions.Add((string?)args[1]);
                _configuration = new LibrariesConfigurationDto { StorageLocations = request.StorageLocations, Libraries = request.Libraries, FileHandling = request.FileHandling };
                return Task.FromResult<LibrariesConfigurationDto?>(_configuration);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetSetupServerFolderRootsAsync), _ => Task.FromResult<IReadOnlyList<ServerStorageLocationDto>>(_configuration.StorageLocations));
            stub.SetHandler(nameof(IEngineApiClient.BrowseSetupServerFoldersAsync), _ => Task.FromResult<BrowseServerFoldersResultDto?>(new() { DisplayPath = _picked.Path }));
            stub.SetHandler(nameof(IEngineApiClient.ValidateSetupServerFolderAsync), _ => Task.FromResult<ServerFolderValidationResultDto?>(_picked));
        }));
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        _dialogs = Render<AppDialogHost>();
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private static ViewSourceAdminDto Remember(List<ViewSourceAdminDto> list, CreateViewSourceRequest request)
    {
        var source = new ViewSourceAdminDto(Guid.NewGuid(), "folder", request.Name, request.StorageMode, request.Path ?? string.Empty, request.IncludeSubdirectories, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, request.IncludeInTimeline, request.ContentKind ?? "photos");
        list.Add(source);
        return source;
    }

    // Setup's own session is cleared once the administrator exists, and by the media step that person is signed in.
    private void SignIn(bool administrator = true) => Services.GetRequiredService<DashboardSessionAccessor>().Set("token", Me, Me, Guid.NewGuid(),
        new DashboardAuthorityResponse(Me, Me, true, true, 1, 1, administrator, true, null, 1, [], [], []));

    private IRenderedComponent<SetupMediaStage> RenderStage(bool hasLibraries = false, string? session = Session) => Render<SetupMediaStage>(parameters => parameters
        .Add(x => x.SetupSession, session)
        .Add(x => x.HasLibraries, hasLibraries)
        .Add(x => x.DeferMedia, EventCallback.Factory.Create(this, () => _deferred++))
        .Add(x => x.ValidateMedia, EventCallback.Factory.Create(this, () => _validated++))
        .Add(x => x.LibraryCreated, EventCallback.Factory.Create(this, () => _created++)));

    private static void Tick(IRenderedComponent<SetupMediaStage> cut, string group) => cut.Find($".setup-have__card[data-group={group}]").Click();

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<SetupMediaStage> cut, string text) => cut.FindAll("button").Single(x => Text(x) == text);

    private AngleSharp.Dom.IElement DialogButton(string text) => _dialogs.FindAll("button").Single(x => Text(x) == text);

    private void WaitForLoaded(IRenderedComponent<SetupMediaStage> cut) => cut.WaitForAssertion(() => Assert.Contains(Session, _setupSessions));

    // With no setup session the stage reads through the signed-in calls, so "loaded" is the folder list showing.
    private static void WaitForSignedInLoad(IRenderedComponent<SetupMediaStage> cut) => cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".setup-have__card"), card => card.GetAttribute("data-group") == "view"));

    // 1. The cards.
    [Fact]
    public void TheStageAsksWhatDoYouHaveAndOffersFourTickCardsAndNoVault()
    {
        var cut = RenderStage();

        Assert.Equal("What do you have?", Text(cut.Find("h2")));
        Assert.Contains("Tick what you'd like Library to show. You can add more later in Settings.", cut.Markup, StringComparison.Ordinal);
        var cards = cut.FindAll(".setup-have__card");
        Assert.Equal(["Watch Movies, TV Shows, Videos", "Listen Music, Audiobooks", "Read Books, Comics", "View Photos, Personal videos, Documents"], cards.Select(Text).ToArray());
        Assert.All(cards, card =>
        {
            Assert.Equal("false", card.GetAttribute("aria-pressed"));
            Assert.Equal("BUTTON", card.TagName);
        });
        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll(".setup-have__section"));
    }

    // The cards and the wizard are drawn from the list of library kinds the setup session can read.
    [Fact]
    public async Task AKindFromAPluginAppearsOnItsCardAndInTheWizardDuringSetup()
    {
        _kinds = LibraryKindsTestData.BuiltIn(LibraryKindsTestData.Podcasts());
        var cut = RenderStage();
        WaitForLoaded(cut);

        Assert.Contains("Listen Music, Audiobooks, Podcasts", cut.FindAll(".setup-have__card").Select(Text).ToArray());
        Tick(cut, "listen");
        Assert.Equal(["Music", "Audiobooks", "Podcasts"], cut.FindAll(".setup-have__type").Select(x => Text(x.QuerySelector("strong")!)).ToArray());
        Button(cut, "+ Add folder").Click();
        _dialogs.WaitForElement(".add-wizard");
        Assert.Equal(["Music", "Audiobooks", "Podcasts"], _dialogs.FindAll(".add-wizard__kind-option").Select(Text).ToArray());
        await Task.CompletedTask;
    }

    [Fact]
    public void WhenTheListOfKindsCannotBeReadSetupSaysSoAndOffersNoAddFolder()
    {
        _failKinds = true;
        var cut = RenderStage();
        WaitForLoaded(cut);

        cut.WaitForAssertion(() => Assert.Contains("could not be loaded", cut.Markup));
        Assert.Empty(cut.FindAll(".setup-have__add"));
    }

    // 2. Watch: the wizard, one folder, Continue.
    [Fact]
    public async Task TickingWatchOpensItsWizardAndAFolderAddedThroughItListsUnderMoviesAndOffersContinue()
    {
        var cut = RenderStage();
        WaitForLoaded(cut);

        Tick(cut, "watch");
        Assert.Equal("true", cut.Find(".setup-have__card[data-group=watch]").GetAttribute("aria-pressed"));
        Assert.Equal(["Movies", "TV Shows", "Videos"], cut.FindAll(".setup-have__type").Select(x => Text(x.QuerySelector("strong")!)).ToArray());
        Button(cut, "+ Add folder").Click();

        _dialogs.WaitForElement(".add-wizard");
        Assert.Equal("1 What is it? · 2 Folder · 3 How", Text(_dialogs.Find(".add-wizard__steps")));
        Assert.Equal(["Watch", "Listen", "Read"], _dialogs.FindAll(".add-wizard__tile").Select(Text).ToArray());
        Assert.Equal("true", _dialogs.Find(".add-wizard__tile[data-group=watch]").GetAttribute("aria-pressed"));
        Assert.Equal(["Movies", "TV Shows", "Videos"], _dialogs.FindAll(".add-wizard__kind-option").Select(Text).ToArray());

        _dialogs.FindAll(".add-wizard__kind-option").Single(x => Text(x) == "Movies").Click();
        _dialogs.WaitForElement(".server-folder-picker");
        _dialogs.WaitForAssertion(() => Assert.False(DialogButton("Next").HasAttribute("disabled")));
        DialogButton("Next").Click();
        _dialogs.WaitForElement(".add-wizard__question");
        DialogButton("Add folder").Click();
        _dialogs.WaitForElement(".add-wizard__done");

        var update = Assert.Single(_setupUpdates);
        Assert.Equal("Movies", Assert.Single(update.Libraries).MediaTypes.Single());
        Assert.Equal(Session, Assert.Single(_updateSessions));
        var loadsBefore = _setupSessions.Count;
        cut.WaitForAssertion(() =>
        {
            var movies = cut.Find(".setup-have__type[data-type=movies]");
            Assert.Contains("E:\\Setup\\Films", Text(movies), StringComparison.Ordinal);
            Assert.NotNull(movies.QuerySelector("[aria-label^='Read-only']"));
            Assert.True(_created > 0);
        });
        // The stage reloaded with the setup session after the add, and told the page (which flips the footer on that).
        Assert.True(_setupSessions.Count > loadsBefore - 1);
        Assert.All(_setupSessions, session => Assert.Equal(Session, session));

        cut.Render(parameters => parameters.Add(x => x.HasLibraries, true));
        Assert.DoesNotContain(cut.FindAll("button"), x => Text(x) == "Skip for now");
        await cut.InvokeAsync(() => Button(cut, "Continue").Click());
        Assert.Equal(1, _validated);
        Assert.Equal(0, _deferred);
    }

    // Review fixes.
    private static LibrariesConfigurationDto MoviesWithTwoManagedFolders(LibrariesConfigurationDto basis) => new()
    {
        StorageLocations = basis.StorageLocations,
        Libraries =
        [
            new()
            {
                Id = "11111111-1111-4111-8111-111111111111", Name = "Movies", KindKey = "movies", Area = "watch", Kind = "catalogued", MediaTypes = ["Movies"],
                PrimaryDestinationSourceId = "movies-main",
                Sources =
                [
                    new() { Id = "movies-main", Path = "E:\\Library\\Movies", Notes = "Main", ManagementMode = "managed_by_tuvima", AccessMode = "writable" },
                    new() { Id = "movies-alt", Path = "F:\\Movies", Notes = "Alt", ManagementMode = "managed_by_tuvima", AccessMode = "writable" },
                ],
            },
        ],
    };

    private void AddMoviesFolderThroughTheWizard()
    {
        _dialogs.WaitForElement(".add-wizard");
        _dialogs.FindAll(".add-wizard__kind-option").Single(x => Text(x) == "Movies").Click();
        _dialogs.WaitForElement(".server-folder-picker");
        _dialogs.WaitForAssertion(() => Assert.False(DialogButton("Next").HasAttribute("disabled")));
        DialogButton("Next").Click();
        _dialogs.WaitForElement(".add-wizard__question");
        DialogButton("Add folder").Click();
        _dialogs.WaitForElement(".add-wizard__done");
    }

    [Fact]
    public void AFolderAddedThroughTheWizardSurvivesALaterSaveFromTheOpenFolderPanel()
    {
        _configuration = MoviesWithTwoManagedFolders(_configuration);
        var cut = RenderStage(hasLibraries: true);
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find(".setup-have__card[data-group=watch]").GetAttribute("aria-pressed")));
        Button(cut, "Change folders").Click();
        cut.WaitForElement(".folder-table");

        Button(cut, "+ Add folder").Click();
        AddMoviesFolderThroughTheWizard();
        cut.WaitForAssertion(() => Assert.Contains("E:\\Setup\\Films", cut.Find(".folder-table").TextContent, StringComparison.Ordinal));

        cut.Find("[aria-label='Make Alt the place for new Movies']").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, _setupUpdates.Count));
        var saved = _setupUpdates[^1].Libraries.Single(x => x.Name == "Movies");
        Assert.Contains(saved.Sources, source => source.Path == "E:\\Setup\\Films");
        Assert.Equal("movies-alt", saved.PrimaryDestinationSourceId);
        Assert.Equal(3, saved.Sources.Count);
    }

    [Fact]
    public void WhenTheReloadAfterAnAddFailsEveryAddFolderIsDisabledUntilAReloadWorks()
    {
        _configuration = MoviesWithTwoManagedFolders(_configuration);
        var cut = RenderStage(hasLibraries: true);
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find(".setup-have__card[data-group=watch]").GetAttribute("aria-pressed")));
        Tick(cut, "listen");
        Assert.All(cut.FindAll(".setup-have__add button"), button => Assert.False(button.HasAttribute("disabled")));

        _failLoads = true;
        cut.Find(".setup-have__section[data-section=watch] .setup-have__add button").Click();
        AddMoviesFolderThroughTheWizard();

        cut.WaitForAssertion(() =>
        {
            var buttons = cut.FindAll(".setup-have__add button");
            Assert.Equal(2, buttons.Count);
            Assert.All(buttons, button => Assert.True(button.HasAttribute("disabled")));
        });

        _failLoads = false;
        Button(cut, "Try again").Click();

        cut.WaitForAssertion(() => Assert.All(cut.FindAll(".setup-have__add button"), button => Assert.False(button.HasAttribute("disabled"))));
        Assert.DoesNotContain(cut.FindAll("button"), x => Text(x) == "Try again");
    }

    [Fact]
    public async Task TheWizardOpenedOnAPhotosTypeDuringSetupStartsAtStepOneWithNoFolderPicker()
    {
        var parameters = new AppDialogParameters<AddFolderWizard>
        {
            { component => component.StartTypeKey, "photos" },
            { component => component.SetupSession, Session },
            { component => component.Embedded, true },
            { component => component.Configuration, _configuration },
            { component => component.Kinds, LibraryKindsTestData.Catalog },
        };
        await Services.GetRequiredService<IAppDialogService>().ShowAsync<AddFolderWizard>(string.Empty, parameters, AddFolderWizard.DialogOptions);
        _dialogs.WaitForElement(".add-wizard");

        Assert.Equal("1 What is it? · 2 Folder · 3 How", Text(_dialogs.Find(".add-wizard__steps")));
        Assert.Equal(["Watch", "Listen", "Read"], _dialogs.FindAll(".add-wizard__tile").Select(Text).ToArray());
        Assert.Empty(_dialogs.FindAll(".server-folder-picker"));
    }

    [Fact]
    public void WithTheFolderPanelOpenOnlyThePanelsPhoneNoteIsInThePage()
    {
        var cut = RenderStage();
        WaitForLoaded(cut);
        Assert.Single(cut.FindAll(".setup-have__phone-note"));

        Button(cut, "Change folders").Click();
        cut.WaitForElement(".library-folders__phone-note");

        Assert.Empty(cut.FindAll(".setup-have__phone-note"));
        Assert.Single(cut.FindAll(".library-folders__phone-note"));
    }

    // 3. Skip for now.
    [Fact]
    public void SkipForNowStillDefersWhenThereAreNoFolders()
    {
        var cut = RenderStage();
        WaitForLoaded(cut);

        Assert.DoesNotContain(cut.FindAll("button"), x => Text(x) == "Continue");
        Button(cut, "Skip for now").Click();

        Assert.Equal(1, _deferred);
        Assert.Equal(0, _validated);
    }

    // 4. Resume.
    [Fact]
    public void AReloadWithExistingMusicFoldersTicksListenAutomatically()
    {
        _configuration = new LibrariesConfigurationDto
        {
            StorageLocations = _configuration.StorageLocations,
            Libraries =
            [
                new()
                {
                    Id = MusicId, Name = "Music", KindKey = "music", Area = "listen", Kind = "catalogued", MediaTypes = ["Music"],
                    Sources = [new() { Id = "music-1", Path = "E:\\Music", ManagementMode = "managed_by_tuvima", AccessMode = "writable" }],
                },
            ],
        };

        var cut = RenderStage(hasLibraries: true);

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find(".setup-have__card[data-group=listen]").GetAttribute("aria-pressed")));
        Assert.Equal("false", cut.Find(".setup-have__card[data-group=watch]").GetAttribute("aria-pressed"));
        var music = cut.Find(".setup-have__type[data-type=music]");
        Assert.Contains("E:\\Music", Text(music), StringComparison.Ordinal);
        Assert.NotNull(music.QuerySelector("[aria-label^='Managed']"));
        Assert.Contains(cut.FindAll("button"), x => Text(x) == "Continue");
    }

    // 5. View.
    [Fact]
    public void WhileASetupSessionStillExistsTickingViewShowsTheAfterSetupLineAndNoAddFolderAndMakesNoViewCall()
    {
        SignIn();
        var cut = RenderStage();
        WaitForLoaded(cut);

        Tick(cut, "view");

        Assert.Contains("You can add photo and personal video folders after setup, in Settings > Libraries > View.", Text(cut.Find("[data-section=view]")), StringComparison.Ordinal);
        Assert.DoesNotContain(cut.FindAll("button"), x => Text(x) == "+ Add folder");
        Assert.Empty(cut.FindAll("[data-section=view] .setup-have__type"));
        Assert.DoesNotContain(_stub.Calls, call => call.Contains("View", StringComparison.Ordinal));
    }

    [Fact]
    public void SignedInAsSomeoneWhoIsNotAnAdministratorViewStillShowsTheAfterSetupLine()
    {
        SignIn(administrator: false);
        var cut = RenderStage(session: null);
        WaitForSignedInLoad(cut);

        Tick(cut, "view");

        Assert.Contains("after setup", Text(cut.Find("[data-section=view]")), StringComparison.Ordinal);
        Assert.DoesNotContain(cut.FindAll("button"), x => Text(x) == "+ Add folder");
        Assert.DoesNotContain(_stub.Calls, call => call.Contains("View", StringComparison.Ordinal));
    }

    [Fact]
    public void SignedInAsTheAdministratorAfterTheSetupSessionIsGoneTickingViewListsItsThreeKindsAndOffersAddFolder()
    {
        SignIn();
        var cut = RenderStage(session: null);
        WaitForSignedInLoad(cut);

        Tick(cut, "view");

        var section = cut.Find("[data-section=view]");
        Assert.DoesNotContain("after setup", Text(section), StringComparison.Ordinal);
        Assert.Equal(["Photos", "Personal videos", "Documents"], section.QuerySelectorAll(".setup-have__type").Select(x => Text(x.QuerySelector("strong")!)).ToArray());
        Assert.Contains(cut.FindAll("button"), x => Text(x) == "+ Add folder");
    }

    private void PickViewFolder(string path)
    {
        _picked = new ServerFolderValidationResultDto { Path = path, StorageLocationId = "media", Exists = true, HasRead = true, HasWrite = true, CanSelect = true, ContainsFiles = true };
        _dialogs.WaitForElement(".server-folder-picker");
        _dialogs.WaitForAssertion(() => Assert.False(DialogButton("Next").HasAttribute("disabled")));
        DialogButton("Next").Click();
        _dialogs.WaitForElement("#add-wizard-who");
    }

    [Fact]
    public async Task SignedInAsTheAdministratorSetupAddsAPhotosFolderToTheHouseholdAndADocumentsFolderToOnlyMeShowsBothAndContinues()
    {
        SignIn();
        var cut = RenderStage(session: null);
        WaitForSignedInLoad(cut);
        Tick(cut, "view");
        Button(cut, "+ Add folder").Click();
        _dialogs.WaitForElement(".add-wizard");

        // Step 1 keeps to setup: View is offered with its three kinds, and the steps end in who it is for.
        Assert.Equal(["Watch", "Listen", "Read", "View"], _dialogs.FindAll(".add-wizard__tile").Select(Text).ToArray());
        Assert.Equal("true", _dialogs.Find(".add-wizard__tile[data-group=view]").GetAttribute("aria-pressed"));
        Assert.Equal(["Photos", "Personal videos", "Documents"], _dialogs.FindAll(".add-wizard__kind-option").Select(Text).ToArray());
        var navigation = Services.GetRequiredService<NavigationManager>();
        var before = navigation.Uri;

        _picked = new ServerFolderValidationResultDto { Path = "E:\\Family\\Photos", StorageLocationId = "media", Exists = true, HasRead = true, HasWrite = true, CanSelect = true, ContainsFiles = true };
        _dialogs.FindAll(".add-wizard__kind-option").Single(x => Text(x) == "Photos").Click();
        PickViewFolder("E:\\Family\\Photos");
        Assert.Equal("1 What is it? · 2 Folder · 3 Who for", Text(_dialogs.Find(".add-wizard__steps")));
        // The household is preselected.
        Assert.Equal("true", _dialogs.Find("[data-audience=household]").GetAttribute("aria-checked"));
        Assert.Equal("false", _dialogs.Find("[data-audience=me]").GetAttribute("aria-checked"));
        DialogButton("Add folder").Click();
        _dialogs.WaitForElement(".add-wizard__done");

        var shared = Assert.Single(_sharedCreates);
        Assert.Equal("photos", shared.ContentKind);
        Assert.Equal("linked", shared.StorageMode);
        Assert.Equal("E:\\Family\\Photos", shared.Path);
        Assert.Empty(_profileCreates);

        DialogButton("Add another").Click();
        _picked = new ServerFolderValidationResultDto { Path = "E:\\Papers", StorageLocationId = "media", Exists = true, HasRead = true, HasWrite = true, CanSelect = true, ContainsFiles = true };
        _dialogs.FindAll(".add-wizard__kind-option").Single(x => Text(x) == "Documents").Click();
        PickViewFolder("E:\\Papers");
        _dialogs.Find("[data-audience=me]").Click();
        DialogButton("Add folder").Click();
        _dialogs.WaitForElement(".add-wizard__done");

        var mine = Assert.Single(_profileCreates);
        Assert.Equal(Me, mine.Profile);
        Assert.Equal("documents", mine.Request.ContentKind);
        Assert.Single(_sharedCreates);
        Assert.Equal(before, navigation.Uri);

        cut.WaitForAssertion(() =>
        {
            var photos = Text(cut.Find(".setup-have__type[data-type=photos]"));
            Assert.Contains("E:\\Family\\Photos", photos, StringComparison.Ordinal);
            Assert.Contains("Everyone in the household", photos, StringComparison.Ordinal);
            var documents = Text(cut.Find(".setup-have__type[data-type=documents]"));
            Assert.Contains("E:\\Papers", documents, StringComparison.Ordinal);
            Assert.Contains("Only me", documents, StringComparison.Ordinal);
        });
        Assert.Empty(cut.FindAll(".setup-have__type[data-type=personal_videos] li"));
        Assert.True(_created > 0);

        // Another folder somewhere makes Continue the footer, and it moves on.
        cut.Render(parameters => parameters.Add(x => x.HasLibraries, true));
        await cut.InvokeAsync(() => Button(cut, "Continue").Click());
        Assert.Equal(1, _validated);
        Assert.Equal(0, _deferred);
        Assert.DoesNotContain(_setupSessions, session => session is not null);
    }

    [Fact]
    public async Task WithOnlyAViewFolderAddedTheFooterSaysContinueAndMovesOnWithoutTheFolderCheck()
    {
        SignIn();
        var cut = RenderStage(session: null);
        WaitForSignedInLoad(cut);
        Tick(cut, "view");
        Button(cut, "+ Add folder").Click();
        _dialogs.WaitForElement(".add-wizard");
        _picked = new ServerFolderValidationResultDto { Path = "E:\\Family\\Photos", StorageLocationId = "media", Exists = true, HasRead = true, HasWrite = true, CanSelect = true, ContainsFiles = true };
        _dialogs.FindAll(".add-wizard__kind-option").Single(x => Text(x) == "Photos").Click();
        PickViewFolder("E:\\Family\\Photos");
        DialogButton("Add folder").Click();
        _dialogs.WaitForElement(".add-wizard__done");

        cut.WaitForAssertion(() => Assert.DoesNotContain(cut.FindAll("button"), x => Text(x) == "Skip for now"));
        await cut.InvokeAsync(() => Button(cut, "Continue").Click());

        // The Watch, Listen and Read check has nothing to check, so this moves on the way Skip does; the View folder is already saved.
        Assert.Equal(1, _deferred);
        Assert.Equal(0, _validated);
        Assert.Single(_sharedCreates);
    }

    [Fact]
    public void AnEngineRefusalOfAViewFolderShowsUnchangedAndNothingIsListed()
    {
        SignIn();
        _refusal = "HTTP 422: That folder is not somewhere View may read.";
        var cut = RenderStage(session: null);
        WaitForSignedInLoad(cut);
        Tick(cut, "view");
        Button(cut, "+ Add folder").Click();
        _dialogs.WaitForElement(".add-wizard");
        _dialogs.FindAll(".add-wizard__kind-option").Single(x => Text(x) == "Photos").Click();
        PickViewFolder("E:\\Refused");

        DialogButton("Add folder").Click();

        _dialogs.WaitForAssertion(() => Assert.Contains(_refusal, Text(_dialogs.Find(".add-wizard")), StringComparison.Ordinal));
        Assert.Empty(_dialogs.FindAll(".add-wizard__done"));
        Assert.Empty(_sharedCreates);
        Assert.Equal(0, _created);
        Assert.Empty(cut.FindAll("[data-section=view] li"));
        // Setup stays usable: the same footer is still there.
        Assert.Contains(cut.FindAll("button"), x => Text(x) == "Skip for now");
    }

    [Fact]
    public async Task WithASetupSessionTheWizardOpenedOnViewStaysOnTheGroupsSetupOffersAndNeverNavigates()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        var before = navigation.Uri;
        var parameters = new AppDialogParameters<AddFolderWizard>
        {
            { component => component.StartGroupKey, "view" },
            { component => component.SetupSession, Session },
            { component => component.Embedded, true },
            { component => component.ViewOwnerProfileId, Me },
            { component => component.Configuration, _configuration },
            { component => component.Kinds, LibraryKindsTestData.Catalog },
        };
        await Services.GetRequiredService<IAppDialogService>().ShowAsync<AddFolderWizard>(string.Empty, parameters, AddFolderWizard.DialogOptions);
        _dialogs.WaitForElement(".add-wizard");

        Assert.Equal(["Watch", "Listen", "Read"], _dialogs.FindAll(".add-wizard__tile").Select(Text).ToArray());
        Assert.All(_dialogs.FindAll(".add-wizard__tile"), tile => Assert.Equal("false", tile.GetAttribute("aria-pressed")));
        Assert.Empty(_dialogs.FindAll(".add-wizard__kind"));
        Assert.Equal(before, navigation.Uri);
    }

    // 6. The step key and resume are unchanged.
    [Fact]
    public void TheStepKeyTheStepperAndTheDeferralAreUnchanged()
    {
        var setup = Read("src/MediaEngine.Web/Components/Pages/SetupPage.razor");
        var media = Read("src/MediaEngine.Web/Components/Setup/SetupMediaStage.razor");

        Assert.Equal(["server", "preflight", "administrator", "media-locations", "providers", "readiness"], SetupWorkflow.StepKeys);
        Assert.Contains("case \"media-locations\":", setup, StringComparison.Ordinal);
        Assert.Contains("DecideStepAsync(\"media-locations\", \"deferred\"", setup, StringComparison.Ordinal);
        Assert.Contains("<LibraryFoldersPanel Embedded=\"true\"", media, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeFoldersIsCollapsedUntilAskedForAndThenShowsTheFullFolderPanel()
    {
        var cut = RenderStage();
        WaitForLoaded(cut);
        Assert.Empty(cut.FindAll(".library-folders"));

        Button(cut, "Change folders").Click();

        cut.WaitForElement(".library-folders");
        Assert.Equal("true", Button(cut, "Hide folder list").GetAttribute("aria-expanded"));
        Assert.Contains(cut.FindAll(".library-folders__type"), x => Text(x).StartsWith("Import folder", StringComparison.Ordinal));
    }

    // 7. Phones.
    [Fact]
    public void OnPhonesTheCardsStayAndTheNoteTakesThePlaceOfTheAddFolderButtons()
    {
        var cut = RenderStage();
        WaitForLoaded(cut);
        Tick(cut, "read");

        Assert.Equal("Add folders on a computer or tablet. You can skip this step and set them up later.", Text(cut.Find(".setup-have__phone-note")));
        Assert.Equal(4, cut.FindAll(".setup-have__card").Count);

        // The stylesheet is what swaps them at the folders panel's own 840px breakpoint.
        var css = Read("src/MediaEngine.Web/Components/Setup/SetupMediaStage.razor.css");
        var phone = css[css.IndexOf("@media (max-width: 840px)", StringComparison.Ordinal)..];
        Assert.Contains(".setup-have__phone-note { display: block; }", phone, StringComparison.Ordinal);
        Assert.Contains(".setup-have__add { display: none; }", phone, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr))", css[css.IndexOf("@media (max-width: 600px)", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    private static string Read(string relativePath) => File.ReadAllText(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
