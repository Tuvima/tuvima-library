using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Services.Vault;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AppDialogHost = MediaEngine.Web.Components.Shared.AppDialogHost;

namespace MediaEngine.Web.Tests;

// The Add library dialog (library setup spec D0, D0d): two screens (What is it? / Choose your folder), name and content type rules, the folder
// checked as it is chosen in the shared pop-up, the Vault's own list, and what Add library does.
public sealed class CreateLibraryDialogTests : AsyncBunitContext
{
    private LibrariesConfigurationDto _configuration = BuildConfiguration();
    private readonly List<LibraryMutationRequest> _mutations = [];
    private readonly List<UpdateLibrariesRequest> _setupUpdates = [];
    private readonly List<ValidateServerFolderRequest> _validations = [];
    private readonly List<string?> _rescans = [];
    private readonly List<CreateVaultLibraryFromKindRequest> _vaultCreates = [];
    private ServerFolderValidationResultDto _picked = Folder("E:\\Incoming\\Films");
    private IRenderedComponent<AppDialogHost> _dialogs = null!;
    private string? _created;
    // Whether the signed-in administrator has Vault mode on; the Vault choices are drawn only then.
    private bool _vaultMode = true;

    private static ServerFolderValidationResultDto Folder(string path, int? count = 126, string? label = "video", bool capped = false, bool canSelect = true, params ServerFolderValidationIssueDto[] issues) => new()
    {
        Path = path,
        StorageLocationId = "media",
        Exists = true,
        HasRead = true,
        HasWrite = true,
        CanSelect = canSelect,
        SupportedFileCount = count,
        SupportedFileKindLabel = label,
        SupportedFileCountCapped = capped,
        Issues = [.. issues],
    };

    private static LibrariesConfigurationDto BuildConfiguration() => new()
    {
        StorageLocations = [new() { Id = "media", Label = "Media storage", Path = "C:\\media", AllowWrite = true }],
        Libraries =
        [
            new()
            {
                Id = "11111111-1111-4111-8111-111111111111", Name = "Movies", KindKey = "movies", Area = "watch", Kind = "catalogued", MediaTypes = ["Movies"],
                Sources = [new() { Id = "movies-main", Path = "E:\\Library\\Movies", ManagementMode = "existing_library", AccessMode = "read_only" }],
            },
        ],
    };

    private static LibraryFolderDto OtherVideos(string name) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Name = name,
        KindKey = "other_videos",
        Area = "watch",
        Kind = "catalogued",
        MediaTypes = ["Videos"],
        Sources = [new() { Id = $"src-{name}", Path = $"E:\\Videos\\{name}", ManagementMode = "existing_library", AccessMode = "read_only" }],
    };

    public CreateLibraryDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler("get_LastError", _ => null);
            stub.SetHandler(nameof(IEngineApiClient.MutateLibraryAsync), args =>
            {
                var request = (LibraryMutationRequest)args![0]!;
                _mutations.Add(request);
                var libraries = _configuration.Libraries.Where(item => item.Id != request.Library!.Id).Append(request.Library!).ToList();
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
            stub.SetHandler(nameof(IEngineApiClient.CreateVaultLibraryAsync), args =>
            {
                var request = (CreateVaultLibraryFromKindRequest)args![0]!;
                _vaultCreates.Add(request);
                return Task.FromResult<VaultLibraryActionResultDto?>(new VaultLibraryActionResultDto(true, "99999999-9999-4999-8999-999999999999"));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetNsfwStatusAsync), _ => Task.FromResult(
                VaultCall<NsfwStatusDto>.Ok(new NsfwStatusDto { Mode = "allowed", Unlocked = _vaultMode, HasPin = true, IdleLockMinutes = 60 })));
            stub.SetHandler(nameof(IEngineApiClient.TriggerRescanAsync), args =>
            {
                _rescans.Add((string?)args![0]);
                return Task.FromResult(true);
            });
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

    private ServerFolderValidationResultDto Validate(ValidateServerFolderRequest request)
    {
        _validations.Add(request);
        return _picked;
    }

    private async Task SignInAsAdministratorAsync(bool vaultMode = true)
    {
        _vaultMode = vaultMode;
        var profile = Guid.NewGuid();
        Services.GetRequiredService<DashboardSessionAccessor>().Set("token", profile, profile, Guid.NewGuid(),
            new DashboardAuthorityResponse(Guid.NewGuid(), profile, true, true, 1, 1, true, true, null, 1, [], ["settings.administration"], ["household.manage"]));
        await Services.GetRequiredService<NsfwModeState>().RefreshAsync(profile, isKids: false);
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    // A host that names the content type goes straight to the folder screen; otherwise the dialog opens on "What is it?".
    private async Task OpenAsync(string? area = null, string? kind = null, bool simple = false, string? setupSession = null, LibraryKindCatalog? kinds = null, bool chooseFolder = true, string? access = null)
    {
        var parameters = new AppDialogParameters<CreateLibraryDialog>
        {
            { component => component.Configuration, _configuration },
            { component => component.Kinds, kinds ?? LibraryKindsTestData.Catalog },
            { component => component.StartAreaKey, area },
            { component => component.StartKindKey, kind },
            { component => component.StartAccessKey, access },
            { component => component.Simple, simple },
            { component => component.SetupSession, setupSession },
            { component => component.OnCreated, (Func<string, Task>)(id => { _created = id; return Task.CompletedTask; }) },
        };
        await Services.GetRequiredService<IAppDialogService>().ShowAsync<CreateLibraryDialog>(string.Empty, parameters, CreateLibraryDialog.DialogOptions);
        _dialogs.WaitForElement(".create-library");
        if (!chooseFolder)
        {
            return;
        }

        if (Step == "1")
        {
            Next();
        }

        await ChooseFolderAsync();
    }

    private string Step => _dialogs.Find(".create-library").GetAttribute("data-step")!;

    private void Next() => _dialogs.FindAll("button").Single(x => Text(x) == "Next").Click();

    private void Back() => _dialogs.FindAll("button").Single(x => Text(x) == "Back").Click();

    // Step 2 is the folder pop-up itself: it checks its starting folder and reports the answer to the dialog, so there is no Browse and no Choose folder.
    private Task ChooseFolderAsync()
    {
        _dialogs.WaitForElement(".create-library[data-step='2'] .server-folder-picker");
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));
        return Task.CompletedTask;
    }

    private AngleSharp.Dom.IElement CreateButton => _dialogs.FindAll("button").Single(x => Text(x) == "Add library");

    private IEnumerable<AngleSharp.Dom.IElement> KindRows => _dialogs.Find("[aria-label='Content type']").QuerySelectorAll("button");

    private AngleSharp.Dom.IElement KindRow(string title) => KindRows.Single(x => x.QuerySelector("strong")!.TextContent == title);

    private AngleSharp.Dom.IElement NameInput => _dialogs.Find("[data-library-name] input");

    [Fact]
    public async Task TheDialogIsTwoScreensOneQuestionEach()
    {
        await OpenAsync(chooseFolder: false);

        Assert.Equal("Step 1 of 2", Text(_dialogs.Find(".app-dialog-shell__eyebrow")));
        Assert.Equal("What is it?", Text(_dialogs.Find(".app-dialog-shell__title")));
        Assert.Equal(["Movies", "TV Shows", "Other"], KindRows.Select(row => row.QuerySelector("strong")!.TextContent).ToArray());
        Assert.Empty(_dialogs.FindAll(".create-library [aria-label='Media area']"));
        Assert.Empty(_dialogs.FindAll(".create-library input"));
        Assert.DoesNotContain(_dialogs.FindAll(".create-library button"), x => Text(x) == "Browse");

        // The first type that can still take a library is already chosen; Next goes on.
        Assert.True(KindRow("TV Shows").ClassList.Contains("is-selected"));
        Next();

        _dialogs.WaitForAssertion(() => Assert.Equal("Step 2 of 2", Text(_dialogs.Find(".app-dialog-shell__eyebrow"))));
        Assert.Equal("Choose your TV Shows folder", Text(_dialogs.Find(".app-dialog-shell__title")));
        _dialogs.WaitForElement(".create-library .server-folder-picker");
        Assert.Empty(_dialogs.FindAll("[data-folder-path]"));
        Assert.Equal("TV Shows", NameInput.GetAttribute("value"));
        Assert.Contains("Library scans this folder and its subfolders. Your files are never changed.", _dialogs.Markup, StringComparison.Ordinal);
        Assert.Contains("You can rename it later.", _dialogs.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeitherScreenHasAnInfoButtonAnAccessChoiceOrTheWordsStandardOrSelectedProfiles()
    {
        await SignInAsAdministratorAsync();
        await OpenAsync(chooseFolder: false);
        Assert.Empty(_dialogs.FindAll(".app-info-tip"));
        Next();
        _dialogs.WaitForElement(".create-library[data-step='2']");

        Assert.Empty(_dialogs.FindAll(".app-info-tip"));
        Assert.Empty(_dialogs.FindAll("[role=switch]"));
        Assert.DoesNotContain("Selected profiles", _dialogs.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Standard", _dialogs.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Vault", _dialogs.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddLibraryIsOffUntilTheFolderHasBeenCheckedAndAccepted()
    {
        _picked = Folder("E:\\Nope", canSelect: false, count: null, label: null, issues: new ServerFolderValidationIssueDto { Code = "already_configured", Message = "This folder is already used by the library \"Movies\". Choose a different folder." });
        await OpenAsync(kind: "tv", chooseFolder: false);
        Assert.True(CreateButton.HasAttribute("disabled"));

        _dialogs.WaitForElement(".create-library[data-step='2']");
        _dialogs.WaitForElement(".server-folder-picker");
        _dialogs.WaitForAssertion(() => Assert.Contains("already used by the library \"Movies\"", _dialogs.Find("[data-folder-result=refused]").TextContent, StringComparison.Ordinal));
        Assert.True(CreateButton.HasAttribute("disabled"));
        Assert.Empty(_dialogs.FindAll("[data-folder-result=accessible]"));

        _picked = Folder("E:\\Shows");
        _dialogs.Find("button[aria-label='Refresh folders']").Click();
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));
        Assert.Equal("Folder accessible, 126 video files found", Text(_dialogs.Find(".create-library [data-folder-result=accessible]")));
    }

    [Fact]
    public async Task AddLibraryIsOffWithoutAName()
    {
        await OpenAsync(kind: "tv");
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));

        NameInput.Input(string.Empty);

        _dialogs.WaitForAssertion(() => Assert.True(CreateButton.HasAttribute("disabled")));
        Assert.Contains("Give the library a name.", _dialogs.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNameStartsAsTheContentTypeAndFollowsItUntilTheyTypeTheirOwn()
    {
        await OpenAsync(chooseFolder: false);
        KindRow("Other").Click();
        _dialogs.WaitForAssertion(() => Assert.True(KindRow("Other").ClassList.Contains("is-selected")));
        Next();
        _dialogs.WaitForAssertion(() => Assert.Equal("Other", NameInput.GetAttribute("value")));

        Back();
        _dialogs.WaitForElement(".create-library[data-step='1']");
        KindRow("TV Shows").Click();
        Next();
        _dialogs.WaitForAssertion(() => Assert.Equal("TV Shows", NameInput.GetAttribute("value")));

        NameInput.Input("Dad's shows");
        Back();
        _dialogs.WaitForElement(".create-library[data-step='1']");
        KindRow("Other").Click();
        Next();
        _dialogs.WaitForAssertion(() => Assert.Equal("Dad's shows", NameInput.GetAttribute("value")));
    }

    [Fact]
    public async Task AKindThatAllowsOnlyOneLibraryIsDisabledWithAPlainReasonWhenOneExists()
    {
        await OpenAsync(chooseFolder: false);

        var movies = KindRow("Movies");
        Assert.True(movies.HasAttribute("disabled"));
        Assert.Contains("You already have a Movies library. Add a folder to it instead.", Text(movies), StringComparison.Ordinal);
        Assert.False(KindRow("TV Shows").HasAttribute("disabled"));
        // The dialog opens on the first type that can still take a library, not on the disabled one.
        Assert.True(KindRow("TV Shows").ClassList.Contains("is-selected"));
        Assert.False(movies.ClassList.Contains("is-selected"));
    }

    [Fact]
    public async Task ThreeOtherLibrariesStillLeaveRoomForAFourth()
    {
        _configuration.Libraries.AddRange([OtherVideos("Lectures"), OtherVideos("Home videos"), OtherVideos("TikTok edits")]);
        await OpenAsync(chooseFolder: false);

        var row = KindRow("Other");
        Assert.False(row.HasAttribute("disabled"));
        Assert.Contains("Anything else, like home videos, recordings or clips.", Text(row), StringComparison.Ordinal);
        row.Click();
        Next();
        _dialogs.WaitForAssertion(() => Assert.Equal("Other", NameInput.GetAttribute("value")));
    }

    [Fact]
    public async Task AKindThatAllowsSeveralStopsAtItsLimit()
    {
        _configuration.Libraries.AddRange(Enumerable.Range(1, 5).Select(number => OtherVideos($"Set {number}")));
        await OpenAsync(chooseFolder: false);

        var row = KindRow("Other");
        Assert.True(row.HasAttribute("disabled"));
        Assert.Contains("You already have 5", Text(row), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANameAlreadyUsedInTheAreaIsRefused()
    {
        await OpenAsync(kind: "tv");

        NameInput.Input("movies");

        _dialogs.WaitForAssertion(() => Assert.Contains("You already have a library called \"movies\" in this area.", _dialogs.Markup, StringComparison.Ordinal));
        Assert.True(CreateButton.HasAttribute("disabled"));
    }

    [Fact]
    public async Task TheSecondOtherLibraryIsSuggestedAUniqueName()
    {
        _configuration.Libraries.Add(OtherVideos("Other"));
        await OpenAsync(kind: "other_videos");

        Assert.Equal("Other 2", NameInput.GetAttribute("value"));
    }

    [Fact]
    public async Task TheOrdinaryAddLibraryNeverOffersAdultContentOrTheVault_EvenToAnAdministrator()
    {
        await SignInAsAdministratorAsync();
        await OpenAsync(area: "watch", chooseFolder: false);

        Assert.DoesNotContain("Adult content", _dialogs.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Vault", _dialogs.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_dialogs.FindAll("[role=switch]"));
    }

    [Fact]
    public async Task AnOrdinaryLibraryIsSavedAsAStandardLibraryNotAVaultOne()
    {
        await SignInAsAdministratorAsync();
        await OpenAsync(kind: "tv");

        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));
        CreateButton.Click();

        _dialogs.WaitForAssertion(() => Assert.NotEmpty(_mutations));
        Assert.Empty(_vaultCreates);
    }

    [Fact]
    public async Task OpenedFromTheVaultAreaOffersTheVaultsOwnListAndNothingElse()
    {
        await SignInAsAdministratorAsync();
        await OpenAsync(area: "vault", chooseFolder: false);

        Assert.Equal("Stored in the Vault, away from everyone without Vault access.", Text(_dialogs.Find(".app-dialog-shell__subtitle")));
        Assert.Equal(["Movies", "TV Shows", "Music", "Books", "Adult content", "Other"], KindRows.Select(row => row.QuerySelector("strong")!.TextContent).ToArray());
        Assert.Empty(_dialogs.FindAll("[role=switch]"));
    }

    [Fact]
    public async Task OpenedForTheVault_NamesTheLibraryPrivateAndAsksTheEngineToMakeItInTheVault_WithoutAnyVaultSwitch()
    {
        await SignInAsAdministratorAsync();
        await OpenAsync(kind: "tv", access: "vault");

        Assert.Empty(_dialogs.FindAll("[role=switch]"));
        Assert.Equal("Private TV Shows", NameInput.GetAttribute("value"));
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));
        CreateButton.Click();

        _dialogs.WaitForAssertion(() => Assert.Single(_vaultCreates));
        Assert.Equal("tv", _vaultCreates[0].KindKey);
        Assert.Equal("Private TV Shows", _vaultCreates[0].Name);
        Assert.Empty(_mutations);
        Assert.Empty(_setupUpdates);
        Assert.Equal("99999999-9999-4999-8999-999999999999", _created);
    }

    [Fact]
    public async Task OpenedForTheVault_AContentTypeThatAlreadyHasAVaultLibraryIsDisabledWithAPlainReason()
    {
        await SignInAsAdministratorAsync();
        _configuration.Libraries.Add(new() { Id = Guid.NewGuid().ToString("D"), Name = "Private TV Shows", KindKey = "tv", Area = "watch", Kind = "catalogued", IsVault = true, MediaTypes = ["TV"] });
        await OpenAsync(area: "vault", chooseFolder: false);

        Assert.True(KindRow("TV Shows").HasAttribute("disabled"));
        Assert.Contains("You already have a Vault-protected TV Shows library.", _dialogs.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrdinaryLibraryHoldingAKindDoesNotUseUpTheVaultLibraryOfThatKind()
    {
        await SignInAsAdministratorAsync();
        _configuration.Libraries.Add(new() { Id = Guid.NewGuid().ToString("D"), Name = "Movies", KindKey = "movies", Area = "watch", Kind = "catalogued", MediaTypes = ["Movies"] });
        await OpenAsync(area: "vault", chooseFolder: false);

        Assert.False(KindRow("Movies").HasAttribute("disabled"));
    }

    [Fact]
    public async Task AVaultLibraryHoldingAKindDoesNotUseUpTheOrdinaryLibraryOfThatKind()
    {
        await SignInAsAdministratorAsync();
        _configuration.Libraries.Add(new() { Id = Guid.NewGuid().ToString("D"), Name = "Private TV Shows", KindKey = "tv", Area = "watch", Kind = "catalogued", IsVault = true, MediaTypes = ["TV"] });
        await OpenAsync(area: "watch", chooseFolder: false);

        Assert.False(KindRow("TV Shows").HasAttribute("disabled"));
    }

    [Fact]
    public async Task OpenedForTheVault_OffersAdultContent_AndMakesTheVaultLibrary()
    {
        await SignInAsAdministratorAsync();
        await OpenAsync(kind: CreateLibraryDialog.AdultContentKindKey, access: "vault");

        Assert.Equal("Private Adult content", NameInput.GetAttribute("value"));
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));
        CreateButton.Click();

        _dialogs.WaitForAssertion(() => Assert.Single(_vaultCreates));
        Assert.Equal(CreateLibraryDialog.AdultContentKindKey, _vaultCreates[0].KindKey);
        Assert.Empty(_mutations);
    }

    [Fact]
    public async Task AdultContentExistsOnlyInTheVault_SoTheOrdinaryAddLibraryDoesNotOfferIt()
    {
        await SignInAsAdministratorAsync();
        await OpenAsync(area: "watch", chooseFolder: false);

        Assert.DoesNotContain("Adult content", _dialogs.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAdministratorWhoseVaultModeIsOff_IsNeverPutInTheVault_EvenWhenAskedTo()
    {
        await SignInAsAdministratorAsync(vaultMode: false);
        await OpenAsync(kind: "tv", access: "vault");

        Assert.DoesNotContain("Adult content", _dialogs.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Vault", _dialogs.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("TV Shows", NameInput.GetAttribute("value"));
    }

    [Fact]
    public async Task SomeoneWhoIsNotAnAdministratorSeesNoVaultWordAndNoAdultContent()
    {
        await OpenAsync(area: "vault", access: "vault", chooseFolder: false);

        Assert.DoesNotContain("Adult content", _dialogs.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Vault", _dialogs.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SimpleModeAsksNoAccessQuestionAndMentionsNoVaultEvenForAnAdministrator()
    {
        await SignInAsAdministratorAsync();
        await OpenAsync(kind: "tv", simple: true, access: "vault");

        Assert.Empty(_dialogs.FindAll("[aria-label='Access']"));
        Assert.DoesNotContain("Vault", _dialogs.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SimpleModeDoesNotOfferTheAdultContentKind()
    {
        var adult = new LibraryKindDto
        {
            Key = CreateLibraryDialog.AdultContentKindKey,
            Area = "watch",
            Label = "Adult content",
            Icon = "VideoLibrary",
            Format = "Videos",
            NamingTemplate = "Videos",
            Misfit = "keep",
            DefaultHandling = "read_only",
            Tooltip = "Adult videos and pictures.",
            Order = 9,
            Available = true,
        };
        var kinds = new LibraryKindCatalog(LibraryKindsTestData.BuiltIn(adult));
        await OpenAsync(area: "watch", simple: true, kinds: kinds, chooseFolder: false);

        Assert.DoesNotContain("Adult content", _dialogs.Markup, StringComparison.Ordinal);
        Assert.Contains("TV Shows", _dialogs.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFolderCardSaysHowManyFilesWereFoundOrWhyTheFolderCannotBeUsed()
    {
        await OpenAsync(kind: "tv", chooseFolder: false);
        _dialogs.WaitForElement(".create-library[data-step='2']");
        _dialogs.WaitForElement(".server-folder-picker");

        _dialogs.WaitForAssertion(() => Assert.Equal("Folder accessible, 126 video files found", Text(_dialogs.Find("[data-folder-result=accessible]"))));
        Assert.Equal("tv", _validations.Last().KindKey);

        _picked = Folder("E:\\Huge", count: 10_000, capped: true);
        _dialogs.Find("button[aria-label='Refresh folders']").Click();
        _dialogs.WaitForAssertion(() => Assert.Equal("Folder accessible, 10,000+ video files found", Text(_dialogs.Find("[data-folder-result=accessible]"))));

        _picked = new ServerFolderValidationResultDto { Path = "E:\\Slow", StorageLocationId = "media", Exists = true, HasRead = true, CanSelect = true, SupportedFileCountUnavailable = true };
        _dialogs.Find("button[aria-label='Refresh folders']").Click();
        _dialogs.WaitForAssertion(() => Assert.Contains("file count isn't available", Text(_dialogs.Find("[data-folder-result=accessible]")), StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheScanNoteIsAQuietInfoLineNotAnAmberWarning()
    {
        await OpenAsync(kind: "tv");

        var note = _dialogs.Find("[data-scan-line]");
        Assert.Empty(note.QuerySelectorAll(".tl-alert"));
        Assert.Empty(_dialogs.FindAll(".tl-alert-warning"));
    }

    [Fact]
    public async Task EveryContentTypeRowCarriesItsOwnIcon()
    {
        await OpenAsync(chooseFolder: false);

        Assert.All(KindRows, row => Assert.NotNull(row.QuerySelector(".app-choice-row__icon svg, .app-choice-row__icon .app-material-icon")));
    }

    [Fact]
    public async Task AddLibrarySavesALibraryReadOnlyWithTheFolderStartsAScanClosesAndOpensItsPageWithNoToast()
    {
        await OpenAsync(kind: "tv");
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));
        NameInput.Input("Series");

        CreateButton.Click();

        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".create-library")));
        var saved = Assert.Single(_mutations).Library!;
        Assert.Equal("Series", saved.Name);
        Assert.Equal("tv", saved.KindKey);
        Assert.Equal("watch", saved.Area);
        Assert.False(saved.IsVault);
        var source = Assert.Single(saved.Sources);
        Assert.Equal("E:\\Incoming\\Films", source.Path);
        Assert.Equal("existing_library", source.ManagementMode);
        Assert.Equal("read_only", source.AccessMode);
        Assert.Equal(["E:\\Incoming\\Films"], _rescans);
        Assert.Equal(saved.Id, _created);
        Assert.Empty(_dialogs.FindAll(".tl-toast, .app-toast"));
        // Straight to the new library's page, with no "go to" question in between.
        Assert.EndsWith($"/settings/libraries/{saved.Id}", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddLibraryKeepsTheNameForAKindThatAllowsOnlyOneLibrary()
    {
        await OpenAsync(area: "listen", kind: "music");
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));
        NameInput.Input("Mum's music");

        CreateButton.Click();

        _dialogs.WaitForAssertion(() => Assert.Single(_mutations));
        Assert.Equal("Mum's music", _mutations[0].Library!.Name);
    }

    [Fact]
    public async Task SimpleModeSavesThroughSetupAndStaysWhereItIs()
    {
        var startAddress = Services.GetRequiredService<NavigationManager>().Uri;
        await OpenAsync(kind: "tv", simple: true, setupSession: "setup-code");
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));

        CreateButton.Click();

        _dialogs.WaitForAssertion(() => Assert.Single(_setupUpdates));
        Assert.Empty(_mutations);
        Assert.Empty(_rescans);
        Assert.Equal(startAddress, Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Contains(_setupUpdates[0].Libraries, library => library.KindKey == "tv" && !library.IsVault);
    }
}
