using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Services.Vault;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// The Vault card on Settings > Libraries (Library setup spec WP6, D12, D13): the strip, the Vault page and the wizard's Vault tile,
// for the Vault as built. Only an administrator with Vault mode on sees any of it; for everyone else there is no word and no hint.
public sealed class LibraryVaultCardTests : AsyncBunitContext
{
    private static readonly Guid Me = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string AdminPin = "1357";
    private const string NewFolder = "/media/adult";
    private const string OrdinaryFolder = "/media/movies-and-more";
    private const string OrdinaryFilesMessage = "That folder already holds files that are in your normal libraries, so nothing was added. Put the Vault videos in a folder of their own first.";

    private readonly LibrariesConfigurationDto _configuration = new()
    {
        StorageLocations = [new() { Id = "media", Label = "Media storage", Path = "C:\\media", AllowWrite = true }],
        Libraries = [],
    };
    private bool _modeAvailable;
    private bool _unlocked;
    private bool _hasPin = true;
    private List<VaultLibraryDto> _libraries = [];
    private bool _librariesUnreadable;
    private readonly List<CreateVaultLibraryRequest> _created = [];
    private readonly List<(string Library, AttachVaultFolderRequest Request)> _attached = [];
    private string _picked = NewFolder;
    private readonly IRenderedComponent<AppDialogHost> _dialogs;

    public LibraryVaultCardTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(_configuration));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryOverviewAsync), _ => Task.FromResult<LibraryOverviewDto?>(new()));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryKindsAsync), _ => Task.FromResult<IReadOnlyList<LibraryKindDto>?>(LibraryKindsTestData.BuiltIn()));
            stub.SetHandler(nameof(IEngineApiClient.TestPathAsync), _ => Task.FromResult<PathTestResultDto?>(new() { Exists = true, HasRead = true, HasWrite = true }));
            stub.SetHandler(nameof(IEngineApiClient.GetViewProfileSourcesAsync), args =>
                Task.FromResult<ViewPersonalSpaceAdminReviewDto?>(new((Guid)args![0]!, null, [], [])));
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(
                new List<ProfileViewModel> { new(Me, "Test User", "#C9922E", "Administrator", DateTimeOffset.UtcNow) }));
            stub.SetHandler("get_LastError", _ => null);
            stub.SetHandler(nameof(IEngineApiClient.GetServerFolderRootsAsync), _ => Task.FromResult<IReadOnlyList<ServerStorageLocationDto>>(_configuration.StorageLocations));
            stub.SetHandler(nameof(IEngineApiClient.BrowseServerFoldersAsync), _ => Task.FromResult<BrowseServerFoldersResultDto?>(new() { DisplayPath = _picked }));
            stub.SetHandler(nameof(IEngineApiClient.ValidateServerFolderAsync), _ => Task.FromResult<ServerFolderValidationResultDto?>(
                new() { Path = _picked, StorageLocationId = "media", Exists = true, HasRead = true, HasWrite = true, CanSelect = true, ContainsFiles = true }));
            stub.SetHandler(nameof(IEngineApiClient.GetNsfwStatusAsync), _ => Task.FromResult(_modeAvailable
                ? VaultCall<NsfwStatusDto>.Ok(new NsfwStatusDto { Mode = "allowed", Unlocked = _unlocked, HasPin = _hasPin, IdleLockMinutes = 60 })
                : VaultCall<NsfwStatusDto>.Of(VaultOutcome.NotAvailable)));
            stub.SetHandler(nameof(IEngineApiClient.LockNsfwAsync), _ =>
            {
                _unlocked = false;
                return Task.FromResult(VaultCall<bool>.Ok(true));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetVaultLibrariesAsync), _ => Task.FromResult(_librariesUnreadable
                ? VaultCall<IReadOnlyList<VaultLibraryDto>>.Of(VaultOutcome.Failed)
                : VaultCall<IReadOnlyList<VaultLibraryDto>>.Ok(_libraries)));
            stub.SetHandler(nameof(IEngineApiClient.CreateVaultLibraryAsync), args =>
            {
                var request = (CreateVaultLibraryRequest)args![0]!;
                _created.Add(request);
                if (request.AdminPin != AdminPin)
                {
                    return Task.FromResult(VaultCall<VaultLibraryDto>.Of(VaultOutcome.PinRequired));
                }

                var library = new VaultLibraryDto { Id = "vault-1", LookupSource = request.LookupSource };
                _libraries = [library];
                return Task.FromResult(VaultCall<VaultLibraryDto>.Ok(library));
            });
            stub.SetHandler(nameof(IEngineApiClient.AttachVaultFolderAsync), args =>
            {
                var request = (AttachVaultFolderRequest)args![1]!;
                if (request.AdminPin != AdminPin)
                {
                    return Task.FromResult(VaultCall<VaultLibraryDto>.Of(VaultOutcome.PinRequired));
                }

                if (request.FolderPath == OrdinaryFolder)
                {
                    return Task.FromResult(VaultCall<VaultLibraryDto>.Of(VaultOutcome.Rejected, OrdinaryFilesMessage));
                }

                _attached.Add(((string)args[0]!, request));
                return Task.FromResult(VaultCall<VaultLibraryDto>.Ok(new VaultLibraryDto { Id = (string)args[0]!, LookupSource = "none" }));
            });
        }));
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.LibraryKindsState>();
        _dialogs = Render<AppDialogHost>();
    }

    private NsfwModeState State => Services.GetRequiredService<NsfwModeState>();

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    // The signed-in person's situation: an administrator (or not) whose profile may use Vault mode (or not), with it on (or not).
    private async Task SetUpAsync(bool administrator = true, bool modeAvailable = true, bool unlocked = true, bool kids = false)
    {
        if (administrator)
        {
            Services.GetRequiredService<DashboardSessionAccessor>().Set("token", Me, Me, Guid.NewGuid(),
                new DashboardAuthorityResponse(Me, Me, true, true, 1, 1, true, true, null, 1, [], ["settings.administration"], ["household.manage"]));
        }

        _modeAvailable = modeAvailable;
        _unlocked = unlocked;
        await State.RefreshAsync(Me, kids);
    }

    private IRenderedComponent<LibrariesTab> Open(string? subsection = null, string? detail = null)
    {
        var cut = Render<LibrariesTab>(parameters =>
        {
            if (subsection is not null)
            {
                parameters.Add(x => x.Subsection, subsection);
            }

            if (detail is not null)
            {
                parameters.Add(x => x.Detail, detail);
            }
        });
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading libraries", cut.Markup));
        return cut;
    }

    private AngleSharp.Dom.IElement Button(string text) => _dialogs.FindAll("button").Single(x => Text(x) == text);

    private void PickFolder(string path)
    {
        _picked = path;
        _dialogs.WaitForElement(".server-folder-picker");
        _dialogs.WaitForAssertion(() => Assert.False(Button("Next").HasAttribute("disabled")));
        Button("Next").Click();
    }

    // The number pad opens over the wizard, so its button is the last "Add folder" in the page.
    private async Task EnterPinAsync(string pin)
    {
        _dialogs.WaitForAssertion(() => Assert.Contains("Enter your PIN to continue", _dialogs.Markup));
        foreach (var digit in pin)
        {
            await _dialogs.InvokeAsync(() => _dialogs.Find($".pin-pad__key[aria-label='{digit}']").Click());
        }

        _dialogs.WaitForAssertion(() => Assert.Contains(_dialogs.FindAll("button"), button => Text(button) == "Add folder" && !button.HasAttribute("disabled")));
        await _dialogs.InvokeAsync(() => _dialogs.FindAll("button").Last(button => Text(button) == "Add folder").Click());
    }

    // 1. Nothing, not even a hint, unless an administrator has Vault mode on.
    [Theory]
    [InlineData("the Engine says this profile may not use the Vault")]
    [InlineData("Vault mode is off in this session")]
    [InlineData("it is a Kids profile")]
    [InlineData("the person is not an administrator")]
    public async Task WithoutAdministratorVaultModeOn_EveryLibrariesAddressTheWizardAndTheTitlesMentionNoVault(string situation)
    {
        await (situation switch
        {
            "the Engine says this profile may not use the Vault" => SetUpAsync(modeAvailable: false),
            "Vault mode is off in this session" => SetUpAsync(unlocked: false),
            "it is a Kids profile" => SetUpAsync(kids: true),
            _ => SetUpAsync(administrator: false),
        });

        foreach (var (subsection, detail) in new (string?, string?)[] { (null, null), ("watch", null), ("listen", null), ("read", null), ("view", null), ("watch", "movies"), ("import", null) })
        {
            var cut = Open(subsection, detail);
            Assert.DoesNotContain("vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
        }

        var vaultAddress = Open("vault");
        Assert.Contains("This library page does not exist", vaultAddress.Markup);
        Assert.Equal("/settings/libraries", vaultAddress.Find("a[href='/settings/libraries']").GetAttribute("href"));
        Assert.DoesNotContain("vault", vaultAddress.Markup, StringComparison.OrdinalIgnoreCase);

        var front = Open();
        front.Find(".lib-door__add").Click();
        _dialogs.WaitForElement(".add-wizard");
        Assert.Equal(["Watch", "Listen", "Read", "View"], _dialogs.FindAll(".add-wizard__tile").Select(Text).ToArray());
        Assert.DoesNotContain("vault", _dialogs.Markup, StringComparison.OrdinalIgnoreCase);

        Assert.False(LibraryVaultAccess.CanShow(State, Services.GetRequiredService<DashboardSessionAccessor>(), isKids: false));
    }

    [Fact]
    public async Task AKidsProfileNeverSeesTheVaultEvenIfVaultModeWereOnAndTheyAreAnAdministrator()
    {
        await SetUpAsync();
        var session = Services.GetRequiredService<DashboardSessionAccessor>();

        Assert.True(LibraryVaultAccess.CanShow(State, session, isKids: false));
        Assert.False(LibraryVaultAccess.CanShow(State, session, isKids: true));
    }

    // 2. With Vault mode on for an administrator: the strip, then its page.
    [Fact]
    public async Task WithVaultModeOnTheFrontDoorEndsWithAThinVaultStripThatLinksToTheVaultPage()
    {
        await SetUpAsync();

        var cut = Open();

        var cards = cut.FindAll(".lib-door__group");
        Assert.Equal(["watch", "listen", "read", "view", "vault"], cards.Select(card => card.GetAttribute("data-group")!).ToArray());
        var strip = cards.Last();
        Assert.Contains("lib-door__group--strip", strip.ClassName);
        Assert.Equal("/settings/libraries/vault", strip.GetAttribute("href"));
        Assert.Equal("Vault", Text(strip.QuerySelector(".lib-door__name")!));
        // The Vault's folders are not part of what the Dashboard can read, so the strip shows no count rather than a wrong zero.
        Assert.Equal(string.Empty, Text(strip.QuerySelector(".lib-door__counts")!));
        Assert.Equal("Vault", strip.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task TheVaultPageShowsTheVaultLibraryItsLookupAndALinkToManageItInTheVault()
    {
        await SetUpAsync();
        _libraries = [new VaultLibraryDto { Id = "vault-1", LookupSource = "stash_box" }];

        var cut = Open("vault");

        var row = cut.WaitForElement("[data-vault-library=vault-1]");
        Assert.Contains("Vault", Text(row.QuerySelector(".lib-page__name")!));
        Assert.Equal("Stash-box", Text(row.QuerySelector(".lib-page__counts")!));
        var manage = row.QuerySelector("a.lib-page__manage")!;
        Assert.Equal("Manage in the Vault", Text(manage));
        Assert.Equal("/vault/library-settings", manage.GetAttribute("href"));
        Assert.NotNull(cut.Find(".lib-page__add"));
    }

    [Fact]
    public async Task WithoutAVaultLibraryTheVaultPageSaysSoInOneLineAndOffersAddFolder()
    {
        await SetUpAsync();

        var cut = Open("vault");

        cut.WaitForAssertion(() => Assert.Equal("No Vault library yet.", Text(cut.Find(".lib-page__empty"))));
        Assert.Empty(cut.FindAll("[data-vault-library]"));
        Assert.NotNull(cut.Find(".lib-page__add"));
    }

    [Fact]
    public async Task ThePageAndItsAddressDisappearAtOnceWhenVaultModeIsSwitchedOff()
    {
        await SetUpAsync();
        var cut = Open("vault");
        cut.WaitForElement(".lib-page__add");

        await State.LockAsync();

        cut.WaitForAssertion(() => Assert.Contains("This library page does not exist", cut.Markup));
        Assert.DoesNotContain("vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheSettingsFrameTitlesTheVaultPageOnlyWhileTheVaultMayBeShown()
    {
        await SetUpAsync();
        var session = Services.GetRequiredService<DashboardSessionAccessor>();

        var shown = LibraryAddress.Describe(LibraryKindsTestData.Catalog, "vault", null, LibraryVaultAccess.CanShow(State, session, isKids: false));
        await State.LockAsync();
        var hidden = LibraryAddress.Describe(LibraryKindsTestData.Catalog, "vault", null, LibraryVaultAccess.CanShow(State, session, isKids: false));

        Assert.Equal("Vault", shown.Title);
        Assert.Equal(["Libraries", "Vault"], shown.Crumbs.Select(crumb => crumb.Label).ToArray());
        Assert.Equal("Libraries", hidden.Title);
        Assert.Empty(hidden.Crumbs);
    }

    // 3. The wizard's Vault tile.
    [Fact]
    public async Task TheWizardOffersTheVaultTileAndPickingItSkipsStraightToTheFolderStep()
    {
        await SetUpAsync();
        Open().Find(".lib-door__add").Click();
        _dialogs.WaitForElement(".add-wizard");

        Assert.Equal(["Watch", "Listen", "Read", "View", "Vault"], _dialogs.FindAll(".add-wizard__tile").Select(Text).ToArray());
        _dialogs.Find(".add-wizard__tile[data-group=vault]").Click();

        _dialogs.WaitForElement(".server-folder-picker");
        Assert.Empty(_dialogs.FindAll(".add-wizard__kind"));
        Assert.Equal("1 What is it? · 2 Folder · 3 Vault", Text(_dialogs.Find(".add-wizard__steps")));
    }

    [Fact]
    public async Task AddFolderOnTheVaultPageOpensTheWizardOnTheFolderStepForTheVault()
    {
        await SetUpAsync();
        var cut = Open("vault");

        cut.WaitForElement(".lib-page__add").Click();

        _dialogs.WaitForElement(".server-folder-picker");
        Assert.Equal("1 What is it? · 2 Folder · 3 Vault", Text(_dialogs.Find(".add-wizard__steps")));
    }

    [Fact]
    public async Task WithNoVaultLibraryStepThreeAsksForTheLookupTheAdultsConfirmationAndThePinThenCreatesTheLibraryAndAddsTheFolder()
    {
        await SetUpAsync();
        Open().Find(".lib-door__add").Click();
        _dialogs.WaitForElement(".add-wizard");
        _dialogs.Find(".add-wizard__tile[data-group=vault]").Click();
        PickFolder(NewFolder);

        _dialogs.WaitForElement("[data-vault-adults]");
        Assert.Contains("visible on disk", _dialogs.Markup);
        Assert.Equal(["none", "stash_box"], _dialogs.FindAll(".add-wizard__field option").Select(option => option.GetAttribute("value")!).ToArray());
        Assert.Empty(_dialogs.FindAll(".add-wizard [data-handling]"));
        Assert.True(Button("Add folder").HasAttribute("disabled"));

        _dialogs.Find(".add-wizard__field select").Change("stash_box");
        Assert.Contains("arrives with the Vault lookup update", Text(_dialogs.Find(".add-wizard__field")));
        _dialogs.Find("[data-vault-adults]").Change(true);
        _dialogs.WaitForAssertion(() => Assert.False(Button("Add folder").HasAttribute("disabled")));
        Button("Add folder").Click();
        await EnterPinAsync(AdminPin);

        _dialogs.WaitForAssertion(() => Assert.Contains("Folder added to Vault", _dialogs.Markup));
        var created = Assert.Single(_created);
        Assert.Equal(("stash_box", true, AdminPin), (created.LookupSource, created.AdultsConfirmed, created.AdminPin));
        var (library, request) = Assert.Single(_attached);
        Assert.Equal("vault-1", library);
        Assert.Equal((NewFolder, AdminPin), (request.FolderPath, request.AdminPin));
    }

    [Fact]
    public async Task WithAVaultLibraryStepThreeAsksOnlyForThePinAndAddsTheFolderToIt()
    {
        await SetUpAsync();
        _libraries = [new VaultLibraryDto { Id = "vault-1", LookupSource = "none" }];
        Open().Find(".lib-door__add").Click();
        _dialogs.WaitForElement(".add-wizard");
        _dialogs.Find(".add-wizard__tile[data-group=vault]").Click();
        PickFolder(NewFolder);

        _dialogs.WaitForAssertion(() => Assert.False(Button("Add folder").HasAttribute("disabled")));
        Assert.Empty(_dialogs.FindAll("[data-vault-adults]"));
        Assert.Empty(_dialogs.FindAll(".add-wizard__field"));
        Button("Add folder").Click();
        await EnterPinAsync(AdminPin);

        _dialogs.WaitForAssertion(() => Assert.Contains("Folder added to Vault", _dialogs.Markup));
        Assert.Empty(_created);
        Assert.Equal(("vault-1", NewFolder), (_attached.Single().Library, _attached.Single().Request.FolderPath));
    }

    [Fact]
    public async Task AWrongPinIsAnsweredWithTheEnginesWordsOnThePadAndNothingIsSaved()
    {
        await SetUpAsync();
        _libraries = [new VaultLibraryDto { Id = "vault-1", LookupSource = "none" }];
        Open().Find(".lib-door__add").Click();
        _dialogs.WaitForElement(".add-wizard");
        _dialogs.Find(".add-wizard__tile[data-group=vault]").Click();
        PickFolder(NewFolder);
        _dialogs.WaitForAssertion(() => Assert.False(Button("Add folder").HasAttribute("disabled")));

        Button("Add folder").Click();
        await EnterPinAsync("9999");

        _dialogs.WaitForAssertion(() => Assert.Contains(VaultMessages.WrongPin, _dialogs.Markup));
        Assert.Empty(_attached);
        Assert.DoesNotContain("Folder added", _dialogs.Markup);
    }

    [Fact]
    public async Task AFolderThatAlreadyHoldsOrdinaryFilesIsRefusedWithTheEnginesMessageUnchangedAndNothingIsAdded()
    {
        await SetUpAsync();
        _libraries = [new VaultLibraryDto { Id = "vault-1", LookupSource = "none" }];
        Open().Find(".lib-door__add").Click();
        _dialogs.WaitForElement(".add-wizard");
        _dialogs.Find(".add-wizard__tile[data-group=vault]").Click();
        PickFolder(OrdinaryFolder);
        _dialogs.WaitForAssertion(() => Assert.False(Button("Add folder").HasAttribute("disabled")));

        Button("Add folder").Click();
        await EnterPinAsync(AdminPin);

        _dialogs.WaitForAssertion(() => Assert.Equal(OrdinaryFilesMessage, Text(_dialogs.Find(".add-wizard .app-alert"))));
        Assert.Empty(_attached);
        Assert.DoesNotContain("Folder added", _dialogs.Markup);
        Assert.Equal("1 What is it? · 2 Folder · 3 Vault", Text(_dialogs.Find(".add-wizard__steps")));
    }

    [Fact]
    public async Task WithoutAProfilePinStepThreeSaysToSetOneFirstAndWillNotAdd()
    {
        _hasPin = false;
        await SetUpAsync();
        Open().Find(".lib-door__add").Click();
        _dialogs.WaitForElement(".add-wizard");
        _dialogs.Find(".add-wizard__tile[data-group=vault]").Click();
        PickFolder(NewFolder);

        _dialogs.WaitForAssertion(() => Assert.Equal(VaultMessages.SetProfilePinFirst, Text(_dialogs.Find(".add-wizard .app-alert"))));
        Assert.True(Button("Add folder").HasAttribute("disabled"));
        Assert.Empty(_created);
    }

    [Fact]
    public async Task TheVaultTileLeavesStepOneAtOnceWhenVaultModeIsSwitchedOffWhileTheWizardIsOpen()
    {
        await SetUpAsync();
        Open().Find(".lib-door__add").Click();
        _dialogs.WaitForElement(".add-wizard");
        Assert.Contains("Vault", _dialogs.FindAll(".add-wizard__tile").Select(Text));

        await State.LockAsync();

        _dialogs.WaitForAssertion(() => Assert.Equal(["Watch", "Listen", "Read", "View"], _dialogs.FindAll(".add-wizard__tile").Select(Text).ToArray()));
        Assert.DoesNotContain("vault", _dialogs.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AVaultLibraryThatCannotBeReadShowsAnErrorWithTryAgainOnTheVaultPageNotASkeletonForever()
    {
        await SetUpAsync();
        _librariesUnreadable = true;
        var cut = Open("vault");

        cut.WaitForAssertion(() => Assert.Contains("The Vault library could not be read", cut.Markup));
        Assert.Empty(cut.FindAll(".lib-page__empty, [data-vault-library]"));
        _librariesUnreadable = false;
        _libraries = [new VaultLibraryDto { Id = "vault-1", LookupSource = "none" }];
        cut.FindAll("button").Single(button => Text(button) == "Try again").Click();

        cut.WaitForElement("[data-vault-library=vault-1]");
        Assert.DoesNotContain("could not be read", cut.Markup);
    }

    [Fact]
    public async Task WhenTheVaultLibraryCannotBeReadStepThreeOffersNeitherCreationNorAddingUntilTryAgainWorks()
    {
        await SetUpAsync();
        _librariesUnreadable = true;
        Open().Find(".lib-door__add").Click();
        _dialogs.WaitForElement(".add-wizard");
        _dialogs.Find(".add-wizard__tile[data-group=vault]").Click();
        PickFolder(NewFolder);

        _dialogs.WaitForAssertion(() => Assert.Equal(VaultMessages.Unreachable, Text(_dialogs.Find(".add-wizard .app-alert"))));
        Assert.Empty(_dialogs.FindAll("[data-vault-adults], .add-wizard__field"));
        Assert.True(Button("Add folder").HasAttribute("disabled"));
        Assert.Empty(_created);

        _librariesUnreadable = false;
        Button("Try again").Click();

        _dialogs.WaitForElement("[data-vault-adults]");
        Assert.Empty(_dialogs.FindAll(".add-wizard .app-alert"));
        Assert.Empty(_created);
        Assert.Empty(_attached);
    }

    [Fact]
    public async Task TheWizardClosesWhenVaultModeIsSwitchedOffWhileItIsOpenOnTheVault()
    {
        await SetUpAsync();
        Open().Find(".lib-door__add").Click();
        _dialogs.WaitForElement(".add-wizard");
        _dialogs.Find(".add-wizard__tile[data-group=vault]").Click();
        _dialogs.WaitForElement(".server-folder-picker");

        await State.LockAsync();

        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".add-wizard")));
    }

    // 5. First-run setup never offers the Vault, whatever the host passes.
    [Fact]
    public async Task TheWizardInFirstRunSetupNeverOffersTheVaultEvenWithVaultModeOn()
    {
        await SetUpAsync();
        var parameters = new AppDialogParameters<AddFolderWizard>
        {
            { component => component.Embedded, true },
            { component => component.ShowVault, true },
            { component => component.Configuration, _configuration },
            { component => component.Kinds, LibraryKindsTestData.Catalog },
        };

        await Services.GetRequiredService<IAppDialogService>().ShowAsync<AddFolderWizard>(string.Empty, parameters, AddFolderWizard.DialogOptions);
        _dialogs.WaitForElement(".add-wizard");

        Assert.Equal(["Watch", "Listen", "Read"], _dialogs.FindAll(".add-wizard__tile").Select(Text).ToArray());
        Assert.DoesNotContain("vault", _dialogs.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
