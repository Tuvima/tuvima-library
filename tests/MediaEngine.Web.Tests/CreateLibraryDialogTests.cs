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

// The Add library dialog (library setup spec D0, D0d): four screens (area / What is it? / Choose your folder / the check), name and content type rules, the folder
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
            stub.SetHandler(nameof(IEngineApiClient.GetPluginsAsync), _ => Task.FromResult<IReadOnlyList<MediaEngine.Contracts.Plugins.PluginSummaryResponse>>(_plugins));
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
    private async Task OpenAsync(string? area = null, string? kind = null, bool simple = false, string? setupSession = null, LibraryKindCatalog? kinds = null, bool chooseFolder = true, string? access = null, bool noArea = false)
    {
        var parameters = new AppDialogParameters<CreateLibraryDialog>
        {
            { component => component.Configuration, _configuration },
            { component => component.Kinds, kinds ?? LibraryKindsTestData.Catalog },
            { component => component.StartAreaKey, noArea ? null : area ?? "watch" },
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

        while (Step != "3")
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
        _dialogs.WaitForElement(".create-library[data-step='3'] .server-folder-picker");
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));
        return Task.CompletedTask;
    }

    private AngleSharp.Dom.IElement CreateButton => _dialogs.FindAll("button").Single(x => Text(x) == "Add library");

    private IEnumerable<AngleSharp.Dom.IElement> KindRows => _dialogs.Find("[data-kind-tiles]").QuerySelectorAll("button");

    private IEnumerable<AngleSharp.Dom.IElement> AreaTiles => _dialogs.Find("[data-area-tiles]").QuerySelectorAll("button");

    // Hovering a tile opens its card (Libraries v3 D24); the words are only in the page while it is open.
    private string HoverWords(string title)
    {
        _dialogs.FindAll(".lib-peek").Single(card => card.QuerySelector(".lib-tile__name")?.TextContent == title).MouseEnter();
        return Text(_dialogs.Find("[data-hover-card]"));
    }

    private static bool IsOff(AngleSharp.Dom.IElement tile) => tile.GetAttribute("aria-disabled") == "true";

    private AngleSharp.Dom.IElement KindRow(string title) => KindRows.Single(x => x.QuerySelector(".lib-tile__name")!.TextContent == title);

    // The folder window's friendly name field (Libraries v3 D18); the library itself is never named by the person.
    private AngleSharp.Dom.IElement FolderNameInput => _dialogs.Find(".server-folder-picker input");

    // Local-check bug 1: the Add library tiles drew a 28px icon beside the text. They are the shared choice tile: a 48px icon above the name.
    [Fact]
    public void PlainVideosOffersManagedAndEveryKindFollowsItsAreaColor()
    {
        var kinds = new LibraryKindCatalog(LibraryKindsTestData.BuiltIn());
        var videos = kinds.Find("other_videos")!;
        var other = kinds.Find("music")!;

        Assert.True(LibraryKindLook.ManagedOffered(videos));
        Assert.True(LibraryKindLook.ManagedOffered(other));
        Assert.Contains("lib-area--watch", LibraryKindLook.TileClass(videos), StringComparison.Ordinal);
        Assert.Contains("lib-area--listen", LibraryKindLook.TileClass(other), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAddLibraryAreaTilesUseTheChoiceTile()
    {
        await OpenAsync(noArea: true, chooseFolder: false);

        var tiles = _dialogs.Find("[data-area-tiles]").QuerySelectorAll("button.app-choice-tile").ToArray();
        Assert.Equal(4, tiles.Length);
        Assert.All(tiles, tile =>
        {
            var square = tile.QuerySelector(".app-choice-tile__square")!;
            Assert.NotNull(square.QuerySelector("svg.tl-icon-size-choice48"));
            Assert.NotNull(tile.QuerySelector(".app-choice-tile__label"));
            // The icon square comes first in the tile, so the name sits under it (the tile is a column).
            Assert.Same(square, tile.Children[0]);
            Assert.Null(tile.QuerySelector("svg.tl-icon-size-control20, svg.tl-icon-size-inline16"));
        });

        AreaTiles.Single(x => Text(x).StartsWith("Listen", StringComparison.Ordinal)).Click();
        Next();
        _dialogs.WaitForAssertion(() => Assert.Equal("Step 2 of 3", Text(_dialogs.Find(".app-dialog-shell__eyebrow"))));
        var kindTiles = _dialogs.Find("[data-kind-tiles]").QuerySelectorAll("button.app-choice-tile").ToArray();
        Assert.NotEmpty(kindTiles);
        Assert.All(kindTiles, tile => Assert.NotNull(tile.QuerySelector("svg.tl-icon-size-choice48")));
    }

    [Fact]
    public async Task TheDialogIsThreeStepsOfSquareTilesAndNeverAsksForAName()
    {
        await OpenAsync(noArea: true, chooseFolder: false);

        // Step 1 asks for the area as square tiles.
        Assert.Equal("Step 1 of 3", Text(_dialogs.Find(".app-dialog-shell__eyebrow")));
        Assert.Equal(["Watch", "Listen", "Read", "View"], AreaTiles.Select(tile => tile.QuerySelector(".lib-tile__name")!.TextContent).ToArray());
        Assert.Contains("Movies, TV shows and home videos", HoverWords("Watch"), StringComparison.Ordinal);
        Assert.DoesNotContain("Vault", _dialogs.Markup, StringComparison.OrdinalIgnoreCase);
        AreaTiles.Single(x => Text(x).StartsWith("Listen", StringComparison.Ordinal)).Click();
        Next();

        // Step 2 asks for the kind within that area.
        _dialogs.WaitForAssertion(() => Assert.Equal("Step 2 of 3", Text(_dialogs.Find(".app-dialog-shell__eyebrow"))));
        Assert.Equal("What is it?", Text(_dialogs.Find(".app-dialog-shell__title")));
        Assert.Contains("Music", KindRows.Select(row => row.QuerySelector(".lib-tile__name")!.TextContent));
        Assert.DoesNotContain("Movies", KindRows.Select(row => row.QuerySelector(".lib-tile__name")!.TextContent));
        Back();
        _dialogs.WaitForElement(".create-library[data-step='1']");
        AreaTiles.Single(x => Text(x).StartsWith("Watch", StringComparison.Ordinal)).Click();
        Next();
        _dialogs.WaitForElement(".create-library[data-step='2']");
        Assert.Equal(["Movies", "TV Shows", "Videos"], KindRows.Select(row => row.QuerySelector(".lib-tile__name")!.TextContent).ToArray().Take(3).ToArray());
        Assert.Empty(_dialogs.FindAll(".create-library input"));
        Assert.DoesNotContain(_dialogs.FindAll(".create-library button"), x => Text(x) == "Browse");

        // A kind that already has a library is greyed and says where its next folder goes (D23).
        Assert.True(IsOff(KindRow("Movies")));
        KindRow("TV Shows").Click();
        Next();

        _dialogs.WaitForAssertion(() => Assert.Equal("Step 3 of 3 · Watch", Text(_dialogs.Find(".app-dialog-shell__eyebrow"))));
        Assert.Equal("Where is it? · TV Shows", Text(_dialogs.Find(".app-dialog-shell__title")));
        _dialogs.WaitForElement(".create-library .server-folder-picker");
        Assert.Empty(_dialogs.FindAll("[data-folder-path]"));
        Assert.Equal("Library scans this folder and its subfolders.", Text(_dialogs.Find("[data-scan-line]")));
        Assert.DoesNotContain("You can rename it later.", _dialogs.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheViewAreaOffersItsThreeKindsAndNextOpensTheViewFolderPopUp()
    {
        await OpenAsync(area: "view", chooseFolder: false);

        _dialogs.WaitForAssertion(() => Assert.Equal(["Photos and Videos", "Documents"], KindRows.Select(row => row.QuerySelector(".lib-tile__name")!.TextContent).ToArray()));
        Assert.All(KindRows, row => Assert.False(IsOff(row)));

        Next();

        _dialogs.WaitForAssertion(() => Assert.Contains("add-view-folder", _dialogs.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheVaultsOwnAddLibraryHasNoAreaTabs()
    {
        await SignInAsAdministratorAsync();
        await OpenAsync(area: "vault", chooseFolder: false);

        Assert.Empty(_dialogs.FindAll("[data-area-tabs]"));
    }

    [Fact]
    public async Task NeitherScreenHasAnInfoButtonAnAccessChoiceOrTheWordsStandardOrSelectedProfiles()
    {
        await SignInAsAdministratorAsync();
        await OpenAsync(chooseFolder: false);
        Assert.Empty(_dialogs.FindAll(".app-info-tip"));
        Next();
        _dialogs.WaitForElement(".create-library[data-step='3']");

        Assert.Empty(_dialogs.FindAll(".app-info-tip"));
        Assert.Empty(_dialogs.FindAll("[role=switch]"));
        Assert.DoesNotContain("Selected profiles", _dialogs.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Standard", _dialogs.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Vault", _dialogs.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChoosingAFolderChecksNothingAndAddingChecksItOnceAndStopsOnAFolderAnotherLibraryUses()
    {
        _picked = Folder("E:\\Nope", canSelect: false, count: null, label: null, issues: new ServerFolderValidationIssueDto { Code = "already_configured", Message = "This folder is already used by the library \"Movies\". Choose a different folder." });
        await OpenAsync(kind: "tv", chooseFolder: false);
        _dialogs.WaitForElement(".create-library[data-step='3'] .server-folder-picker");

        // Choosing a folder runs no check (the picker is dumb); the button is on as soon as a path is chosen.
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));
        Assert.Empty(_validations);

        CreateButton.Click();

        _dialogs.WaitForElement(".create-library[data-step='4'] [data-fit='already_used']");
        Assert.Single(_validations);
        Assert.Contains("already used by the library \"Movies\"", _dialogs.Find("[data-fit]").TextContent, StringComparison.Ordinal);
        Assert.Empty(_dialogs.FindAll("[data-fit='looks_right']"));
    }

    [Fact]
    public async Task AddingAFolderThatLooksRightSaysHowManyFilesWereFound()
    {
        await OpenAsync(kind: "tv", chooseFolder: false);
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));

        CreateButton.Click();

        _dialogs.WaitForElement(".create-library[data-step='4'] [data-fit='looks_right']");
        Assert.Contains("Found 126 video files", _dialogs.Find("[data-fit]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKindThatAlreadyHasALibraryIsGreyedWithAddAFolderToItInstead()
    {
        await OpenAsync(chooseFolder: false);

        var movies = KindRow("Movies");
        Assert.True(IsOff(movies));
        Assert.Contains("is-disabled", movies.ClassList);
        Assert.Equal("Add a folder to it instead", Text(movies.QuerySelector("[data-add-folder-instead]")!));
        Assert.DoesNotContain("Added", Text(movies), StringComparison.Ordinal);
        Assert.False(movies.ClassList.Contains("is-selected"));
        Assert.Contains("You already have a Movies library. Add a folder to Movies instead.", HoverWords("Movies"), StringComparison.Ordinal);

        // Clicking it chooses nothing; the first kind that can still take a library is the one chosen.
        movies.Click();
        Assert.False(KindRow("Movies").ClassList.Contains("is-selected"));
        Assert.True(KindRow("TV Shows").ClassList.Contains("is-selected"));
        Assert.False(IsOff(KindRow("TV Shows")));
        Assert.Null(KindRow("TV Shows").QuerySelector("[data-add-folder-instead]"));
    }

    [Fact]
    public async Task TheAreaTilesCarryTheSecondLineUnderTheirName()
    {
        await OpenAsync(noArea: true, chooseFolder: false);

        var lines = AreaTiles.Select(tile => Text(tile.QuerySelector(".app-choice-tile__description")!)).ToArray();
        Assert.Equal(["Movies, TV, videos", "Music, audiobooks", "Books, comics", "Photos, videos, documents"], lines);
    }

    [Fact]
    public async Task TheFolderStepSaysYourFilesAreNeverChangedOnceAndNamesTheFolderInOneSentence()
    {
        await OpenAsync(kind: "tv", chooseFolder: false);
        _dialogs.WaitForElement(".create-library[data-step='3'] .server-folder-picker");

        Assert.Single(_dialogs.FindAll("[data-picker-safety]"));
        Assert.Equal("Your files are never changed.", Text(_dialogs.Find("[data-picker-safety]")));
        // Outside the Read-only choice's own explanation, the step says it exactly once: the footer line.
        var outsideTheChoice = Text(_dialogs.Find(".create-library")).Replace(Text(_dialogs.Find("[data-folder-handling]")), string.Empty, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(outsideTheChoice, "Your files are never changed"));
        Assert.DoesNotContain("never changed", Text(_dialogs.Find("[data-scan-line]")), StringComparison.Ordinal);
        Assert.Equal("Names this folder, not the library.", Text(_dialogs.Find(".server-folder-picker__name-hint")));
        Assert.Equal("Where is it? · TV Shows", Text(_dialogs.Find(".app-dialog-shell__title")));
        Assert.Equal(["Watch", "TV Shows"], _dialogs.Find("[data-picker-chips]").QuerySelectorAll("span").Select(Text).ToArray());
    }

    [Fact]
    public async Task ManagedFoldersAreSaidToBeKeptOrganizedInUsSpelling()
    {
        await OpenAsync(kind: "tv", chooseFolder: false);
        _dialogs.WaitForElement(".create-library[data-step='3'] .server-folder-picker");

        _dialogs.Find("[data-folder-handling]").QuerySelectorAll("button").Single(x => Text(x).StartsWith("Managed", StringComparison.Ordinal)).Click();

        _dialogs.WaitForAssertion(() => Assert.Equal("Library scans this folder and its subfolders, and keeps it organized.", Text(_dialogs.Find("[data-scan-line]"))));
        Assert.DoesNotContain("organised", _dialogs.Markup, StringComparison.Ordinal);
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
        Assert.Equal(["Movies", "TV Shows", "Music", "Books", "Adult content", "Videos"], KindRows.Select(row => row.QuerySelector(".lib-tile__name")!.TextContent).ToArray());
        Assert.Empty(_dialogs.FindAll("[role=switch]"));
    }

    [Fact]
    public async Task OpenedForTheVault_NamesTheLibraryPrivateAndAsksTheEngineToMakeItInTheVault_WithoutAnyVaultSwitch()
    {
        await SignInAsAdministratorAsync();
        await OpenAsync(kind: "tv", access: "vault");

        Assert.Empty(_dialogs.FindAll("[role=switch]"));
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

        Assert.True(IsOff(KindRow("TV Shows")));
        Assert.Contains("You already have a Vault-protected TV Shows library.", HoverWords("TV Shows"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrdinaryLibraryHoldingAKindDoesNotUseUpTheVaultLibraryOfThatKind()
    {
        await SignInAsAdministratorAsync();
        _configuration.Libraries.Add(new() { Id = Guid.NewGuid().ToString("D"), Name = "Movies", KindKey = "movies", Area = "watch", Kind = "catalogued", MediaTypes = ["Movies"] });
        await OpenAsync(area: "vault", chooseFolder: false);

        Assert.False(IsOff(KindRow("Movies")));
    }

    [Fact]
    public async Task AVaultLibraryHoldingAKindDoesNotUseUpTheOrdinaryLibraryOfThatKind()
    {
        await SignInAsAdministratorAsync();
        _configuration.Libraries.Add(new() { Id = Guid.NewGuid().ToString("D"), Name = "Private TV Shows", KindKey = "tv", Area = "watch", Kind = "catalogued", IsVault = true, MediaTypes = ["TV"] });
        await OpenAsync(area: "watch", chooseFolder: false);

        Assert.False(IsOff(KindRow("TV Shows")));
    }

    [Fact]
    public async Task OpenedForTheVault_OffersAdultContent_AndMakesTheVaultLibrary()
    {
        await SignInAsAdministratorAsync();
        await OpenAsync(kind: CreateLibraryDialog.AdultContentKindKey, access: "vault");

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

    private IReadOnlyList<MediaEngine.Contracts.Plugins.PluginSummaryResponse> _plugins = [];

    private static MediaEngine.Contracts.Plugins.PluginSummaryResponse Source(string id, bool keyPresent) => new()
    {
        Id = id,
        Name = id,
        Enabled = true,
        Provider = new MediaEngine.Contracts.Plugins.PluginProviderInfoDto { RequiresKey = true, KeyPresent = keyPresent },
    };

    [Fact]
    public async Task ATvLibrary_WarnsInAmber_WhileNothingCanIdentifyShows_AndOffersToConnectASource()
    {
        _plugins = [Source("tmdb", keyPresent: false), Source("tvdb", keyPresent: false)];

        await OpenAsync(kind: "tv");

        var held = _dialogs.WaitForElement("[data-testid=create-library-held]");
        Assert.Contains("Nothing can identify TV shows yet. Connect TMDB or TheTVDB first.", held.TextContent);
        Assert.Contains(held.QuerySelectorAll("button"), button => button.TextContent.Trim() == "Connect a source");
    }

    [Fact]
    public async Task ATvLibrary_SaysNothing_OnceOneSourceHasItsKey()
    {
        _plugins = [Source("tmdb", keyPresent: true), Source("tvdb", keyPresent: false)];

        await OpenAsync(kind: "tv");

        Assert.Empty(_dialogs.FindAll("[data-testid=create-library-held]"));
    }

    [Fact]
    public void OnlyMoviesTvAndComicsAreEverHeld_AndOnlyWhileNoAcceptedSourceWorks()
    {
        var none = new[] { Source("tmdb", keyPresent: false), Source("tvdb", keyPresent: false), Source("comicvine", keyPresent: false) };
        Assert.Equal(new[] { "comics", "movies", "tv" }, LibraryFolderHealth.HeldKinds(none).Order().ToArray());

        var tmdb = new[] { Source("tmdb", keyPresent: true), Source("tvdb", keyPresent: false), Source("comicvine", keyPresent: false) };
        Assert.Equal(new[] { "comics" }, LibraryFolderHealth.HeldKinds(tmdb).ToArray());

        Assert.Empty(LibraryFolderHealth.HeldKinds([]));
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
    public async Task AddLibrarySavesALibraryReadOnlyWithTheFolderStartsAScanClosesAndOpensItsPageWithNoToast()
    {
        await OpenAsync(kind: "tv");
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));

        CreateButton.Click();

        // The check after adding stays on screen (Looks right, with what was found) until Done.
        _dialogs.WaitForElement(".create-library[data-step='4'] [data-fit='looks_right']");
        Assert.Contains("Found 126 video files", _dialogs.Find("[data-fit]").TextContent, StringComparison.Ordinal);
        _dialogs.FindAll("button").Single(x => Text(x) == "Done").Click();
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".create-library")));
        var saved = Assert.Single(_mutations).Library!;
        Assert.Equal("TV Shows", saved.Name);
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
    public async Task TheFolderGetsItsFriendlyNameFromTheFolderUntilTheyTypeOne()
    {
        await OpenAsync(area: "listen", kind: "music");
        _dialogs.WaitForAssertion(() => Assert.Equal("Films", FolderNameInput.GetAttribute("value")));
        FolderNameInput.Input("Mum's music");
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));

        CreateButton.Click();

        _dialogs.WaitForAssertion(() => Assert.Single(_mutations));
        Assert.Equal("Music", _mutations[0].Library!.Name);
        Assert.Equal("Mum's music", Assert.Single(_mutations[0].Library!.Sources).Label);
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

    [Fact]
    public async Task AFolderWithNoFilesOfTheKindGivesAHeadsUpAndAddsNothingUntilAddAnyway()
    {
        _picked = Folder("E:\\Empty", count: 0);
        await OpenAsync(kind: "tv");
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));

        CreateButton.Click();

        _dialogs.WaitForElement(".create-library[data-step='4'] [data-fit='heads_up']");
        Assert.Empty(_mutations);
        _dialogs.FindAll("button").Single(x => Text(x) == "Add anyway").Click();
        _dialogs.WaitForAssertion(() => Assert.Single(_mutations));
    }

    [Fact]
    public async Task AFolderAnotherLibraryAlreadyUsesGivesAnAlreadyUsedAnswerAndAddsNothing()
    {
        _picked = Folder("E:\\Library\\Movies", issues: new ServerFolderValidationIssueDto { Code = "already_configured", Message = "This folder is already used by the library \"Movies\". Choose a different folder.", Severity = "warning" });
        await OpenAsync(kind: "tv");
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));

        CreateButton.Click();

        _dialogs.WaitForElement(".create-library[data-step='4'] [data-fit='already_used']");
        Assert.Empty(_mutations);
        Assert.Contains("already used by the library", _dialogs.Find("[data-fit]").TextContent, StringComparison.Ordinal);
    }
}
