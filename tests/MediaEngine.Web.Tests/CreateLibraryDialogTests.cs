using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AppDialogHost = MediaEngine.Web.Components.Shared.AppDialogHost;

namespace MediaEngine.Web.Tests;

// The Add library dialog (Libraries spec WP3): one screen, name and content type rules, the three access choices, the first folder
// checked as it is chosen, and what Create does.
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
                return Task.FromResult<LibraryFolderDto?>(new LibraryFolderDto { Id = "99999999-9999-4999-8999-999999999999", Name = request.Name, KindKey = request.KindKey, Access = "vault" });
            });
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

    private void SignInAsAdministrator() =>
        Services.GetRequiredService<DashboardSessionAccessor>().Set("token", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new DashboardAuthorityResponse(Guid.NewGuid(), Guid.NewGuid(), true, true, 1, 1, true, true, null, 1, [], ["settings.administration"], ["household.manage"]));

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private async Task OpenAsync(string? area = null, string? kind = null, bool simple = false, string? setupSession = null, LibraryKindCatalog? kinds = null)
    {
        var parameters = new AppDialogParameters<CreateLibraryDialog>
        {
            { component => component.Configuration, _configuration },
            { component => component.Kinds, kinds ?? LibraryKindsTestData.Catalog },
            { component => component.StartAreaKey, area },
            { component => component.StartKindKey, kind },
            { component => component.Simple, simple },
            { component => component.SetupSession, setupSession },
            { component => component.OnCreated, (Func<string, Task>)(id => { _created = id; return Task.CompletedTask; }) },
        };
        await Services.GetRequiredService<IAppDialogService>().ShowAsync<CreateLibraryDialog>(string.Empty, parameters, CreateLibraryDialog.DialogOptions);
        _dialogs.WaitForElement(".create-library");
        // The picker checks its starting folder when it opens; the dialog follows what it reports.
        _dialogs.WaitForElement(".server-folder-picker");
    }

    private AngleSharp.Dom.IElement Button(string text) => _dialogs.FindAll("button").Single(x => Text(x).StartsWith(text, StringComparison.Ordinal));

    private AngleSharp.Dom.IElement CreateButton => _dialogs.FindAll("button").Single(x => Text(x) == "Create");

    private IEnumerable<AngleSharp.Dom.IElement> KindRows => _dialogs.Find("[aria-label='Content type']").QuerySelectorAll("button");

    private AngleSharp.Dom.IElement KindRow(string title) => KindRows.Single(x => x.QuerySelector("strong")!.TextContent == title);

    private AngleSharp.Dom.IElement NameInput => _dialogs.Find(".create-library input[type=text], .create-library input:not([type])");

    [Fact]
    public async Task TheDialogIsOneScreenWithEverythingTheLibraryNeeds()
    {
        await OpenAsync();

        var headings = _dialogs.FindAll(".create-library__heading").Select(Text).ToArray();
        Assert.Equal(["Media area", "Content type", "Access", "First folder"], headings);
        Assert.Equal(["Watch", "Listen", "Read"], _dialogs.Find("[aria-label='Media area']").QuerySelectorAll("button").Select(Text).ToArray());
        Assert.Contains("Additional folders can be added later.", _dialogs.Markup, StringComparison.Ordinal);
        Assert.Contains("Only this folder and its subfolders are scanned.", _dialogs.Markup, StringComparison.Ordinal);
        Assert.Equal("Name", Text(_dialogs.Find("label")));
    }

    [Fact]
    public async Task CreateIsOffUntilTheFolderHasBeenCheckedAndAccepted()
    {
        _picked = Folder("E:\\Nope", canSelect: false, count: null, label: null, issues: new ServerFolderValidationIssueDto { Code = "already_configured", Message = "This folder is already used by the library \"Movies\". Choose a different folder." });
        await OpenAsync(kind: "tv");

        _dialogs.WaitForAssertion(() => Assert.Contains("already used by the library \"Movies\"", _dialogs.Find("[data-folder-result=refused]").TextContent, StringComparison.Ordinal));
        Assert.True(CreateButton.HasAttribute("disabled"));
        Assert.Empty(_dialogs.FindAll("[data-folder-result=accessible]"));

        _picked = Folder("E:\\Shows");
        _dialogs.Find("button[aria-label='Refresh folders']").Click();
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));
    }

    [Fact]
    public async Task CreateIsOffWithoutAName()
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
        await OpenAsync(kind: "tv");
        Assert.Equal("TV Shows", NameInput.GetAttribute("value"));

        _dialogs.Find("[aria-label='Media area']").QuerySelectorAll("button").Single(x => Text(x) == "Listen").Click();
        _dialogs.WaitForAssertion(() => Assert.Equal("Music", NameInput.GetAttribute("value")));

        NameInput.Input("Dad's records");
        KindRow("Audiobooks").Click();
        _dialogs.WaitForAssertion(() => Assert.True(KindRow("Audiobooks").ClassList.Contains("is-selected")));
        Assert.Equal("Dad's records", NameInput.GetAttribute("value"));
    }

    [Fact]
    public async Task AKindThatAllowsOnlyOneLibraryIsDisabledWithAPlainReasonWhenOneExists()
    {
        await OpenAsync();

        var movies = KindRow("Movies");
        Assert.True(movies.HasAttribute("disabled"));
        Assert.Contains("You already have a Movies library. Add a folder to it instead.", Text(movies), StringComparison.Ordinal);
        Assert.False(KindRow("TV Shows").HasAttribute("disabled"));
        // The dialog opens on the first type that can still take a library, not on the disabled one.
        Assert.True(KindRow("TV Shows").ClassList.Contains("is-selected"));
        Assert.False(movies.ClassList.Contains("is-selected"));
    }

    [Fact]
    public async Task ThreeOtherVideosLibrariesStillLeaveRoomForAFourth()
    {
        _configuration.Libraries.AddRange([OtherVideos("Lectures"), OtherVideos("Home videos"), OtherVideos("TikTok edits")]);
        await OpenAsync(kind: "other_videos");

        var row = KindRow("Videos");
        Assert.False(row.HasAttribute("disabled"));
        Assert.True(row.ClassList.Contains("is-selected"));
        Assert.Equal("Videos", NameInput.GetAttribute("value"));
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));
    }

    [Fact]
    public async Task AKindThatAllowsSeveralStopsAtItsLimit()
    {
        _configuration.Libraries.AddRange(Enumerable.Range(1, 5).Select(number => OtherVideos($"Set {number}")));
        await OpenAsync();

        var row = KindRow("Videos");
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
    public async Task TheSecondOtherVideosLibraryIsSuggestedAUniqueName()
    {
        _configuration.Libraries.Add(OtherVideos("Videos"));
        await OpenAsync(kind: "other_videos");

        Assert.Equal("Videos 2", NameInput.GetAttribute("value"));
    }

    [Fact]
    public async Task AccessOffersThreeChoicesWithVaultOnlyForAdministrators()
    {
        SignInAsAdministrator();
        await OpenAsync(kind: "tv");

        var rows = _dialogs.Find("[aria-label='Access']").QuerySelectorAll("button").ToList();
        Assert.Equal(["Standard", "Selected profiles", "Vault-protected"], rows.Select(row => row.QuerySelector("strong")!.TextContent).ToArray());
        Assert.False(rows[0].HasAttribute("disabled"));
        Assert.True(rows[0].ClassList.Contains("is-selected"));
        Assert.False(rows[1].HasAttribute("disabled"));
        Assert.Contains("Only the profiles you choose.", Text(rows[1]), StringComparison.Ordinal);
        Assert.False(rows[2].HasAttribute("disabled"));
        Assert.DoesNotContain("Available after the Vault update", _dialogs.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChoosingVaultProtectedNamesTheLibraryPrivateAndAsksTheEngineToMakeItInTheVault()
    {
        SignInAsAdministrator();
        await OpenAsync(kind: "tv");

        _dialogs.Find("[aria-label='Access']").QuerySelectorAll("button")[2].Click();
        Assert.Equal("Private TV Shows", _dialogs.Find("input").GetAttribute("value"));
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
    public async Task TheVaultChoiceIsDisabledWithAPlainReasonWhenTheContentTypeAlreadyHasAVaultLibrary()
    {
        SignInAsAdministrator();
        _configuration.Libraries.Add(new() { Id = Guid.NewGuid().ToString("D"), Name = "Private TV Shows", KindKey = "tv", Area = "watch", Kind = "catalogued", Access = "vault", MediaTypes = ["TV"] });
        await OpenAsync(kind: "tv");

        var vault = _dialogs.Find("[aria-label='Access']").QuerySelectorAll("button")[2];
        Assert.True(vault.HasAttribute("disabled"));
        Assert.Contains("You already have a Vault-protected TV Shows library.", Text(vault), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessShowsNoVaultChoiceForAnyoneWhoIsNotAnAdministrator()
    {
        await OpenAsync(kind: "tv");

        var titles = _dialogs.Find("[aria-label='Access']").QuerySelectorAll("button strong").Select(Text).ToArray();
        Assert.Equal(["Standard", "Selected profiles"], titles);
        Assert.DoesNotContain("Vault", _dialogs.Find("[aria-label='Access']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimpleModeOffersStandardAccessAndNoVaultEvenForAnAdministrator()
    {
        SignInAsAdministrator();
        await OpenAsync(kind: "tv", simple: true);

        Assert.DoesNotContain("Vault", _dialogs.Find("[aria-label='Access']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdultContentComesWithTheVaultTickOnForAnAdministratorAndMakesTheVaultLibrary()
    {
        SignInAsAdministrator();
        await OpenAsync(kind: CreateLibraryDialog.AdultContentKindKey);

        var tick = _dialogs.Find("[role=switch]");
        Assert.False(tick.HasAttribute("disabled"));
        Assert.True(tick.HasAttribute("checked") || tick.GetAttribute("aria-checked") == "true");
        Assert.Equal("Private Adult content", _dialogs.Find("input").GetAttribute("value"));
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));
        CreateButton.Click();

        _dialogs.WaitForAssertion(() => Assert.Single(_vaultCreates));
        Assert.Equal(CreateLibraryDialog.AdultContentKindKey, _vaultCreates[0].KindKey);
        Assert.Empty(_mutations);
    }

    [Fact]
    public async Task UntickingTheVaultMakesAnOrdinaryWatchLibraryThatOnlySelectedProfilesSee()
    {
        SignInAsAdministrator();
        await OpenAsync(kind: CreateLibraryDialog.AdultContentKindKey);

        _dialogs.Find("[role=switch]").Change(false);
        Assert.True(_dialogs.Find("[aria-label='Access']").QuerySelectorAll("button")[1].ClassList.Contains("is-selected"));
        Assert.Contains("Nobody but administrators will see this library", _dialogs.Markup, StringComparison.Ordinal);
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));
        CreateButton.Click();

        _dialogs.WaitForAssertion(() => Assert.NotNull(_created));
        Assert.Empty(_vaultCreates);
        Assert.Equal("selected", _mutations.Single().Library!.Access);
        Assert.Equal(CreateLibraryDialog.AdultContentKindKey, _mutations.Single().Library!.KindKey);
    }

    [Fact]
    public async Task AdultContentAndEveryVaultWordAreMissingForSomeoneWhoIsNotAnAdministrator()
    {
        await OpenAsync(area: "watch");

        Assert.DoesNotContain("Adult content", _dialogs.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Vault", _dialogs.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheFolderCardSaysHowManyFilesWereFoundOrWhyTheFolderCannotBeUsed()
    {
        await OpenAsync(kind: "tv");

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

        var note = _dialogs.FindAll(".app-quiet-note").Single(x => Text(x).Contains("Only this folder and its subfolders are scanned.", StringComparison.Ordinal));
        Assert.Empty(note.QuerySelectorAll(".tl-alert"));
        Assert.Empty(_dialogs.FindAll(".tl-alert-warning"));
        Assert.Contains("a folder of several videos is a series", Text(_dialogs.Find("[data-subfolder-hint=watch]")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThereIsOneInfoIconPerSectionHeadingAndNoneOnTheOptions()
    {
        await OpenAsync(kind: "tv");

        Assert.Equal(3, _dialogs.FindAll(".create-library__heading .app-info-tip").Count);
        Assert.Empty(_dialogs.FindAll(".app-choice-row .app-info-tip"));
    }

    [Fact]
    public async Task CreateSavesALibraryReadOnlyWithTheFolderStartsAScanClosesAndOpensItsPage()
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
        Assert.Equal("standard", saved.Access);
        var source = Assert.Single(saved.Sources);
        Assert.Equal("E:\\Incoming\\Films", source.Path);
        Assert.Equal("existing_library", source.ManagementMode);
        Assert.Equal("read_only", source.AccessMode);
        Assert.Equal(["E:\\Incoming\\Films"], _rescans);
        Assert.Equal(saved.Id, _created);
        // Straight to the new library's page, with no "go to" question in between.
        Assert.EndsWith($"/settings/libraries/{saved.Id}", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChoosingSelectedProfilesShowsAPlainNoteAndCreatesTheLibraryWithThatAccess()
    {
        await OpenAsync(kind: "tv");
        _dialogs.WaitForAssertion(() => Assert.False(CreateButton.HasAttribute("disabled")));
        Assert.DoesNotContain("Nobody but administrators", _dialogs.Markup, StringComparison.Ordinal);

        _dialogs.Find("[aria-label='Access']").QuerySelectorAll("button").Single(x => Text(x).StartsWith("Selected profiles", StringComparison.Ordinal)).Click();

        _dialogs.WaitForAssertion(() => Assert.Contains("Nobody but administrators will see this library until you tick profiles on its Access tab.", _dialogs.Markup, StringComparison.Ordinal));
        Assert.False(CreateButton.HasAttribute("disabled"));
        CreateButton.Click();

        _dialogs.WaitForAssertion(() => Assert.Single(_mutations));
        Assert.Equal("selected", _mutations[0].Library!.Access);
    }

    [Fact]
    public async Task CreateKeepsTheNameForAKindThatAllowsOnlyOneLibrary()
    {
        await OpenAsync(kind: "music");
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
        Assert.Contains(_setupUpdates[0].Libraries, library => library.KindKey == "tv" && library.Access == "standard");
    }
}
