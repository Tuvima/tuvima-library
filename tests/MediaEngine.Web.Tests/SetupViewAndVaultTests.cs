using System.Net;
using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Setup;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Vault;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Vault;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// First-run setup after library setup and add-folder v2 (packets 7 to 9): View is the fourth area and takes folders through the View folder dialog;
// after Continue the first administrator is offered the Vault once ("Turn on the Vault?"), with a PIN that is strongly suggested and never required.
public sealed class SetupViewAndVaultTests : AsyncBunitContext
{
    private const string Session = "setup-session";
    private const string MoviesId = "11111111-1111-4111-8111-111111111111";
    private const string RightPin = "2468";

    private readonly Guid _me = Guid.NewGuid();
    private LibrariesConfigurationDto _configuration = Configuration();
    private readonly List<string> _order = [];
    private readonly List<CreateViewSourceRequest> _viewCreates = [];
    private readonly List<UpdateLibrariesRequest> _setupSaves = [];
    private readonly List<(Guid Profile, SetNsfwAccessRequest Request)> _accessSaves = [];
    private readonly List<string?> _unlocks = [];
    private readonly List<string> _pinRequests = [];
    private readonly List<ViewSourceAdminDto> _sharedSources = [];
    private readonly IRenderedComponent<AppDialogHost> _dialogs;
    private EngineApiClientStub _stub = null!;
    private string? _mode;
    private bool _unlocked;
    private bool _hasPin;
    private bool _isKid;
    private ServerFolderValidationResultDto _picked = Folder("E:\\Pictures");

    private static LibrariesConfigurationDto Configuration() => new()
    {
        StorageLocations = [new() { Id = "media", Label = "Library data", Path = "C:\\data", AllowWrite = true }],
        ViewStorage = new ViewStorageDto { StorageLocationId = "media", RelativeRoot = "View" },
        Libraries =
        [
            new()
            {
                Id = MoviesId, Name = "Movies", KindKey = "movies", Area = "watch", Kind = "catalogued", MediaTypes = ["Movies"],
                Sources = [new() { Id = "movies-main", Path = "E:\\Library\\Movies", ManagementMode = "existing_library", AccessMode = "read_only" }],
            },
        ],
    };

    private static ServerFolderValidationResultDto Folder(string path) => new()
    {
        Path = path,
        StorageLocationId = "media",
        Exists = true,
        HasRead = true,
        HasWrite = true,
        CanSelect = true,
        SupportedFileCount = 42,
        SupportedFileKindLabel = "photo",
    };

    public SetupViewAndVaultTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            _stub = stub;
            stub.SetHandler("get_LastError", _ => null);
            stub.SetHandler(nameof(IEngineApiClient.GetSetupLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(_configuration));
            stub.SetHandler(nameof(IEngineApiClient.GetSetupLibraryKindsAsync), _ => Task.FromResult<IReadOnlyList<LibraryKindDto>?>(LibraryKindsTestData.BuiltIn()));
            stub.SetHandler(nameof(IEngineApiClient.UpdateSetupLibrariesAsync), args =>
            {
                var request = (UpdateLibrariesRequest)args![0]!;
                _order.Add("storage");
                _setupSaves.Add(request);
                _configuration = new LibrariesConfigurationDto { StorageLocations = request.StorageLocations, ViewStorage = request.ViewStorage, Libraries = request.Libraries, FileHandling = request.FileHandling };
                return Task.FromResult<LibrariesConfigurationDto?>(_configuration);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetSetupServerFolderRootsAsync), _ => Task.FromResult<IReadOnlyList<ServerStorageLocationDto>>(_configuration.StorageLocations));
            stub.SetHandler(nameof(IEngineApiClient.BrowseSetupServerFoldersAsync), _ => Task.FromResult<BrowseServerFoldersResultDto?>(new() { DisplayPath = _picked.Path }));
            stub.SetHandler(nameof(IEngineApiClient.ValidateSetupServerFolderAsync), _ => Task.FromResult<ServerFolderValidationResultDto?>(_picked));
            stub.SetHandler(nameof(IEngineApiClient.GetViewSharedSourcesAsync), _ => Task.FromResult<IReadOnlyList<ViewSourceAdminDto>?>(_sharedSources));
            stub.SetHandler(nameof(IEngineApiClient.CreateViewSharedSourceAsync), args =>
            {
                var request = (CreateViewSourceRequest)args![0]!;
                _order.Add("view-folder");
                _viewCreates.Add(request);
                var created = new ViewSourceAdminDto(Guid.NewGuid(), "folder", request.Name, request.StorageMode, request.Path ?? string.Empty, true, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    request.IncludeInTimeline, request.ContentKind ?? "photos");
                _sharedSources.Add(created);
                return Task.FromResult<ViewSourceAdminDto?>(created);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
            {
                new(_me, "Sam", "#C9922E", _isKid ? "RestrictedProfile" : "Administrator", DateTimeOffset.UtcNow, HasPin: _hasPin),
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetNsfwStatusAsync), _ => Task.FromResult(_mode is null
                ? VaultCall<NsfwStatusDto>.Of(VaultOutcome.NotAvailable)
                : VaultCall<NsfwStatusDto>.Ok(new NsfwStatusDto { Mode = _mode, Unlocked = _unlocked, HasPin = _hasPin, IdleLockMinutes = 60 })));
            stub.SetHandler(nameof(IEngineApiClient.SetProfileNsfwAccessAsync), args =>
            {
                var request = (SetNsfwAccessRequest)args![1]!;
                _accessSaves.Add(((Guid)args[0]!, request));
                _mode = request.Mode;
                return Task.FromResult(VaultCall<NsfwStatusDto>.Ok(new NsfwStatusDto { Mode = _mode, HasPin = _hasPin, IdleLockMinutes = 60 }));
            });
            stub.SetHandler(nameof(IEngineApiClient.UnlockNsfwAsync), args =>
            {
                _unlocks.Add((string?)args![0]);
                _unlocked = true;
                return Task.FromResult(VaultCall<NsfwStatusDto>.Ok(new NsfwStatusDto { Mode = _mode!, Unlocked = true, HasPin = _hasPin, IdleLockMinutes = 60 }));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetVaultSiblingLibrariesAsync), _ => Task.FromResult<IReadOnlyList<LibraryFolderDto>?>([]));
            stub.SetHandler(nameof(IEngineApiClient.SetNsfwNavigationAsync), args =>
                Task.FromResult(VaultCall<NsfwStatusDto>.Ok(new NsfwStatusDto { Mode = _mode!, Unlocked = _unlocked, HasPin = _hasPin, ShowInNavigation = (bool)args![0]! })));
        }));
        var handler = new RecordingHandler(request =>
        {
            _pinRequests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            _hasPin = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        Services.AddSingleton(new DashboardIdentityClient(new SingleClientFactory(handler)));
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        Services.AddScoped<ConfirmedActionRunner>();
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        _dialogs = Render<AppDialogHost>();
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private void SignInAsAdministrator()
    {
        Services.GetRequiredService<DashboardSessionAccessor>().Set("token", _me, _me, Guid.NewGuid(),
            new DashboardAuthorityResponse(Guid.NewGuid(), _me, true, true, 1, 1, true, true, null, 1, [], ["settings.administration"], ["household.manage"]));
    }

    private int _continued;
    private int _deferred;

    private IRenderedComponent<SetupMediaStage> RenderStage()
    {
        var cut = Render<SetupMediaStage>(parameters => parameters
            .Add(x => x.SetupSession, Session)
            .Add(x => x.HasLibraries, true)
            .Add(x => x.DeferMedia, EventCallback.Factory.Create(this, () => _deferred++))
            .Add(x => x.ValidateMedia, EventCallback.Factory.Create(this, () => _continued++)));
        cut.WaitForAssertion(() => Assert.True(cut.FindAll(".libs").Count > 0));
        return cut;
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<SetupMediaStage> cut, string text) => cut.FindAll("button").Single(x => Text(x) == text);

    private AngleSharp.Dom.IElement DialogButton(string text) => _dialogs.FindAll("button").Single(x => Text(x) == text);

    private static void ChooseArea(IRenderedComponent<SetupMediaStage> cut, string label) =>
        cut.FindAll("[aria-label='Library areas'] button").Single(tab => Text(tab) == label).Click();

    private void OpenViewFolderDialog(IRenderedComponent<SetupMediaStage> cut, string kindLabel)
    {
        ChooseArea(cut, "View");
        cut.FindAll("button").Single(button => button.GetAttribute("aria-label") == $"Add a folder to {kindLabel}").Click();
        _dialogs.WaitForElement(".add-view-folder");
    }

    // ----- View in setup (packet 7) ----------------------------------------------------------------------------------------------

    [Fact]
    public void ViewIsTheFourthAreaWithItsThreeKindsEachTakingFoldersAndNoAddLibrary()
    {
        var cut = RenderStage();

        ChooseArea(cut, "View");

        Assert.Equal(["Watch", "Listen", "Read", "View"], cut.FindAll("[aria-label='Library areas'] button").Select(Text).ToArray());
        Assert.Equal(3, cut.FindAll("tbody tr").Count);
        Assert.Equal(3, cut.FindAll("button").Count(button => button.GetAttribute("aria-label")?.StartsWith("Add a folder to ", StringComparison.Ordinal) == true));
        Assert.DoesNotContain(cut.FindAll("button"), button => Text(button) == "Add library");
        Assert.Empty(cut.FindAll("a.libs__link"));
    }

    [Fact]
    public void TheViewFolderDialogIsTheSharedTwoQuestionScreenWithNoInfoButtonsAndNoStorageAskWhenViewStorageIsReady()
    {
        var cut = RenderStage();

        OpenViewFolderDialog(cut, "Photos");

        Assert.Equal("Choose your Photos folder", Text(_dialogs.Find("h1, h2, [role=heading]")));
        Assert.Empty(_dialogs.FindAll(".add-view-folder [aria-label='More information'], .add-view-folder .app-info-tip"));
        Assert.Empty(_dialogs.FindAll("[data-view-storage]"));
        Assert.Contains("Library scans this folder and its subfolders. Your files are never changed.", _dialogs.Markup, StringComparison.Ordinal);
        Assert.True(DialogButton("Add folder").HasAttribute("disabled"));
        Assert.Contains("Choose a folder to continue.", Text(_dialogs.Find("[data-choose-folder]")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddingAPhotosFolderInSetupSavesAFolderSharedWithEveryoneAndReadWhereItIs()
    {
        var cut = RenderStage();
        OpenViewFolderDialog(cut, "Photos");

        _dialogs.Find(".add-view-folder__folder button").Click();
        _dialogs.WaitForElement(".server-folder-picker");
        _dialogs.WaitForAssertion(() => Assert.False(DialogButton("Choose folder").HasAttribute("disabled")));
        DialogButton("Choose folder").Click();
        _dialogs.WaitForAssertion(() => Assert.Contains("Folder accessible, 42 photo files found", Text(_dialogs.Find("[data-folder-result=accessible]")), StringComparison.Ordinal));
        Assert.False(DialogButton("Add folder").HasAttribute("disabled"));
        await _dialogs.ClickAsync(host => host.FindAll("button").Single(x => Text(x) == "Add folder"));

        _dialogs.WaitForAssertion(() => Assert.Single(_viewCreates));
        var created = _viewCreates.Single();
        Assert.Equal("linked", created.StorageMode);
        Assert.Equal("E:\\Pictures", created.Path);
        Assert.Equal("photos", created.ContentKind);
        Assert.True(created.IncludeSubdirectories);
        Assert.True(created.IncludeInTimeline);
        Assert.Equal("Pictures", created.Name);
        cut.WaitForAssertion(() => Assert.Contains("libs__row--new", cut.Find("tr[data-library='photos']").ClassName));
        cut.WaitForAssertion(() => Assert.Equal("1 folder", Text(cut.Find("tr[data-library='photos'] .libs__counts"))));
    }

    [Fact]
    public async Task ADocumentsFolderStaysOutOfThePhotosTimeline()
    {
        _picked = Folder("E:\\Papers");
        var cut = RenderStage();
        OpenViewFolderDialog(cut, "Documents");

        _dialogs.Find(".add-view-folder__folder button").Click();
        _dialogs.WaitForElement(".server-folder-picker");
        _dialogs.WaitForAssertion(() => Assert.False(DialogButton("Choose folder").HasAttribute("disabled")));
        DialogButton("Choose folder").Click();
        _dialogs.WaitForAssertion(() => Assert.False(DialogButton("Add folder").HasAttribute("disabled")));
        await _dialogs.ClickAsync(host => host.FindAll("button").Single(x => Text(x) == "Add folder"));

        _dialogs.WaitForAssertion(() => Assert.Single(_viewCreates));
        Assert.Equal("documents", _viewCreates.Single().ContentKind);
        Assert.False(_viewCreates.Single().IncludeInTimeline);
    }

    [Fact]
    public async Task WhenViewHasNoStorageYetTheDialogAsksForItPrefilledAndSavesItBeforeTheFolder()
    {
        _configuration = new LibrariesConfigurationDto
        {
            StorageLocations = _configuration.StorageLocations,
            Libraries = [.. _configuration.Libraries, new LibraryFolderDto { Id = "99999999-9999-4999-8999-999999999999", Name = "Private", KindKey = "movies", Area = "watch", IsVault = true }],
            ViewStorage = new ViewStorageDto { StorageLocationId = "gone", RelativeRoot = "View" },
        };
        var cut = RenderStage();
        OpenViewFolderDialog(cut, "Photos");

        var storage = _dialogs.Find("[data-view-storage]");
        Assert.Contains("Where View keeps its files", Text(storage), StringComparison.Ordinal);
        Assert.Equal(Path.Combine("C:\\data", "View"), Text(storage.QuerySelector("[data-storage-path]")!));

        _dialogs.Find(".add-view-folder__folder button").Click();
        _dialogs.WaitForElement(".server-folder-picker");
        _dialogs.WaitForAssertion(() => Assert.False(DialogButton("Choose folder").HasAttribute("disabled")));
        DialogButton("Choose folder").Click();
        _dialogs.WaitForAssertion(() => Assert.False(DialogButton("Add folder").HasAttribute("disabled")));
        await _dialogs.ClickAsync(host => host.FindAll("button").Single(x => Text(x) == "Add folder"));

        _dialogs.WaitForAssertion(() => Assert.Single(_viewCreates));
        Assert.Equal(["storage", "view-folder"], _order);
        var saved = Assert.Single(_setupSaves);
        Assert.Equal("media", saved.ViewStorage.StorageLocationId);
        Assert.Equal("View", saved.ViewStorage.RelativeRoot);
        Assert.DoesNotContain(saved.Libraries, library => library.IsVault); // the Engine refuses any Vault library in a setup save
    }

    [Fact]
    public void WhenThereIsNoWritableLocationAtAllTheDialogRefusesInOnePlainLineAndAddStaysOff()
    {
        _configuration = new LibrariesConfigurationDto
        {
            StorageLocations = [new() { Id = "media", Label = "Library data", Path = "C:\\data", AllowWrite = false }],
            ViewStorage = _configuration.ViewStorage,
            Libraries = _configuration.Libraries,
        };
        var cut = RenderStage();
        OpenViewFolderDialog(cut, "Photos");

        _dialogs.Find(".add-view-folder__folder button").Click();
        _dialogs.WaitForElement(".server-folder-picker");
        _dialogs.WaitForAssertion(() => Assert.False(DialogButton("Choose folder").HasAttribute("disabled")));
        DialogButton("Choose folder").Click();
        _dialogs.WaitForAssertion(() => Assert.NotEmpty(_dialogs.FindAll("[data-folder-result=accessible]")));

        Assert.Contains("no place to keep View files yet", Text(_dialogs.Find("[data-view-storage]")), StringComparison.Ordinal);
        Assert.True(DialogButton("Add folder").HasAttribute("disabled"));
        Assert.Empty(_viewCreates);
    }

    // ----- The Vault step (packet 8) ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAdministratorWhoPressesContinueIsOfferedTheVaultOnceBeforeSetupMovesOn()
    {
        SignInAsAdministrator();
        var cut = RenderStage();

        await cut.ClickAsync(page => Button(page, "Continue"));

        cut.WaitForAssertion(() => Assert.Equal("Turn on the Vault?", Text(cut.Find("h2"))));
        Assert.Equal(0, _continued);
        Assert.Contains("stays hidden until you turn it on", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(["Not now", "Set up the Vault"], cut.FindAll("[data-setup-vault] button").Select(Text).ToArray());
    }

    [Fact]
    public async Task NotNowLeavesNoTraceAnywhere()
    {
        SignInAsAdministrator();
        var cut = RenderStage();
        await cut.ClickAsync(page => Button(page, "Continue"));
        cut.WaitForAssertion(() => Assert.Equal("Turn on the Vault?", Text(cut.Find("h2"))));

        await cut.ClickAsync(page => Button(page, "Not now"));

        Assert.Equal(1, _continued);
        Assert.Empty(_accessSaves);
        Assert.Empty(_unlocks);
        Assert.Empty(_pinRequests);
        Assert.DoesNotContain(nameof(IEngineApiClient.SetProfileNsfwAccessAsync), _stub.Calls);
        Assert.DoesNotContain(nameof(IEngineApiClient.SetNsfwNavigationAsync), _stub.Calls);
        cut.WaitForAssertion(() => Assert.Equal("What do you have?", Text(cut.Find("h2"))));
        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APersonWhoIsNotAnAdministratorSeesNoVaultWordAndTheEngineIsNeverAskedAboutIt()
    {
        var cut = RenderStage();

        await cut.ClickAsync(page => Button(page, "Continue"));

        Assert.Equal(1, _continued);
        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetNsfwStatusAsync), _stub.Calls);
    }

    [Fact]
    public async Task AKidsProfileIsNeverOfferedTheVault()
    {
        _isKid = true;
        SignInAsAdministrator();
        var cut = RenderStage();

        await cut.ClickAsync(page => Button(page, "Continue"));

        Assert.Equal(1, _continued);
        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetNsfwStatusAsync), _stub.Calls);
    }

    [Fact]
    public async Task APersonWhoAlreadyHasVaultAccessIsNotOfferedItAgain()
    {
        _mode = "pin";
        _hasPin = true;
        SignInAsAdministrator();
        var cut = RenderStage();

        await cut.ClickAsync(page => Button(page, "Continue"));

        Assert.Equal(1, _continued);
        Assert.DoesNotContain("Turn on the Vault?", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePinIsTypedTwiceAMismatchShowsOneAmberLineAndSkipAsksOnceMore()
    {
        SignInAsAdministrator();
        var cut = RenderStage();
        await cut.ClickAsync(page => Button(page, "Continue"));
        await cut.ClickAsync(page => Button(page, "Set up the Vault"));
        cut.WaitForAssertion(() => Assert.Equal("Protect it with a PIN?", Text(cut.Find("h2"))));
        Assert.Contains("A PIN is strongly suggested", cut.Markup, StringComparison.Ordinal);
        Assert.True(Button(cut, "Turn on the Vault").HasAttribute("disabled"));

        cut.FindAll("input")[0].Input(RightPin);
        cut.FindAll("input")[1].Input("1357");

        Assert.Contains("The two PINs don't match.", cut.Markup, StringComparison.Ordinal);
        Assert.True(Button(cut, "Turn on the Vault").HasAttribute("disabled"));

        cut.FindAll("input")[1].Input(RightPin);
        Assert.False(Button(cut, "Turn on the Vault").HasAttribute("disabled"));

        await cut.ClickAsync(page => Button(page, "Skip PIN"));
        cut.WaitForAssertion(() => Assert.Equal("Without a PIN, anyone using this profile can open the Vault. Skip anyway?", Text(cut.Find("[data-pin-skip-warning]"))));
        Assert.Empty(_accessSaves);
    }

    [Fact]
    public async Task SettingAPinTurnsTheVaultOnForThisAdministratorOnlyUnlocksItAndLeavesItOutOfTheMenu()
    {
        SignInAsAdministrator();
        var cut = RenderStage();
        await cut.ClickAsync(page => Button(page, "Continue"));
        await cut.ClickAsync(page => Button(page, "Set up the Vault"));
        cut.WaitForAssertion(() => Assert.Equal("Protect it with a PIN?", Text(cut.Find("h2"))));
        cut.FindAll("input")[0].Input(RightPin);
        cut.FindAll("input")[1].Input(RightPin);

        await cut.ClickAsync(page => Button(page, "Turn on the Vault"));

        cut.WaitForAssertion(() => Assert.Equal("Your Vault is on", Text(cut.Find("h2"))));
        Assert.Equal([$"PUT /auth/profiles/{_me:D}/pin"], _pinRequests);
        var (profile, request) = Assert.Single(_accessSaves);
        Assert.Equal(_me, profile);
        Assert.Equal("pin", request.Mode);
        Assert.Equal(RightPin, request.AdminPin);
        Assert.Equal([RightPin], _unlocks);
        Assert.DoesNotContain(nameof(IEngineApiClient.SetNsfwNavigationAsync), _stub.Calls);
        Assert.False(Services.GetRequiredService<NsfwModeState>().ShowInNavigation);
        Assert.Contains(cut.FindAll("[aria-label='Library areas'] button"), tab => Text(tab) == "Vault");
        Assert.Equal("true", cut.FindAll("[aria-label='Library areas'] button").Single(tab => Text(tab) == "Vault").GetAttribute("aria-pressed"));
        Assert.Contains("stays out of the menu", cut.Markup, StringComparison.Ordinal);

        await cut.ClickAsync(page => Button(page, "Continue"));
        Assert.Equal(1, _continued);
    }

    [Fact]
    public async Task SkippingThePinOpensTheVaultWithNoPinAfterOnePlainWarning()
    {
        SignInAsAdministrator();
        var cut = RenderStage();
        await cut.ClickAsync(page => Button(page, "Continue"));
        await cut.ClickAsync(page => Button(page, "Set up the Vault"));
        await cut.ClickAsync(page => Button(page, "Skip PIN"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-pin-skip-warning]")));

        await cut.ClickAsync(page => Button(page, "Skip anyway"));

        cut.WaitForAssertion(() => Assert.Equal("Your Vault is on", Text(cut.Find("h2"))));
        Assert.Empty(_pinRequests);
        var (_, request) = Assert.Single(_accessSaves);
        Assert.Equal("allowed", request.Mode);
        Assert.Null(request.AdminPin);
        Assert.Equal([(string?)null], _unlocks);
    }

    [Fact]
    public async Task AnAdministratorWhoAlreadyHasAPinEntersItOnceAndSetsNoNewOne()
    {
        _hasPin = true;
        SignInAsAdministrator();
        var cut = RenderStage();
        await cut.ClickAsync(page => Button(page, "Continue"));
        await cut.ClickAsync(page => Button(page, "Set up the Vault"));
        cut.WaitForAssertion(() => Assert.Equal("Enter your PIN", Text(cut.Find("h2"))));
        cut.Find("input").Input(RightPin);

        await cut.ClickAsync(page => Button(page, "Turn on the Vault"));

        cut.WaitForAssertion(() => Assert.Equal("Your Vault is on", Text(cut.Find("h2"))));
        Assert.Empty(_pinRequests);
        Assert.Equal("pin", Assert.Single(_accessSaves).Request.Mode);
        Assert.Equal([RightPin], _unlocks);
    }

    // ----- The reminder line -----------------------------------------------------------------------------------------------------

    private async Task<IRenderedComponent<VaultHiddenReminder>> RenderReminderAsync(string? mode, bool showInNavigation = false)
    {
        _mode = mode;
        var state = Services.GetRequiredService<NsfwModeState>();
        await state.RefreshAsync(_me, isKids: false);
        if (showInNavigation)
        {
            await state.SetShowInNavigationAsync(true);
        }

        return Render<VaultHiddenReminder>();
    }

    [Fact]
    public async Task TheReminderIsOneQuietLineOnlyForSomeoneWithVaultAccessWhoKeepsItOutOfTheMenu()
    {
        var cut = await RenderReminderAsync("allowed");

        Assert.Equal("The Vault is set up but hidden. Turn on Show Vault in the menu to use it.", Text(cut.Find("[data-vault-hidden-reminder] .vault-hidden-reminder__text")));
    }

    [Fact]
    public async Task TheReminderNeverAppearsForSomeoneWithoutVaultAccess()
    {
        var cut = await RenderReminderAsync(null);

        Assert.Empty(cut.FindAll("[data-vault-hidden-reminder]"));
        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheReminderGoesOnceTheVaultIsInTheMenu()
    {
        var cut = await RenderReminderAsync("allowed");

        await cut.ClickAsync(page => page.FindAll("button").Single(button => Text(button) == "Turn on"));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-vault-hidden-reminder]")));
        Assert.True(Services.GetRequiredService<NsfwModeState>().ShowInNavigation);
    }

    [Fact]
    public async Task ClosingTheReminderKeepsItClosed()
    {
        var cut = await RenderReminderAsync("allowed");

        await cut.ClickAsync(page => page.Find("button[aria-label='Dismiss this reminder']"));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-vault-hidden-reminder]")));
        Assert.True(Services.GetRequiredService<NsfwModeState>().Preferences.HiddenReminderDismissed);
    }

    // ----- Helpers ---------------------------------------------------------------------------------------------------------------

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
