using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Components.Navigation;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Vault;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Services.Vault;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// NSFW mode and the Vault in the Dashboard (Videos spec WP10). The rule under test: nothing about them is drawn, or even present in
/// the page source, for a profile the Engine's status route answers "not found" to (Off, Kids, limited) and a Kids profile is never even
/// asked; for a profile that can use it, everything follows what the Engine says about this device session.
/// </summary>
public sealed class VaultExperienceTests : AsyncBunitContext
{
    private static readonly Guid Me = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private const string RightPin = "2468";
    private const string AdminPin = "1357";
    private static readonly string[] Tokens = ["nsfw", "vault", "sfw", "pin-pad"];

    private string? _mode;
    private bool _unlocked;
    private bool _hasPin = true;
    private int _lockCalls;
    private int _keepAlives;
    private readonly List<string?> _unlockPins = [];
    private readonly List<CreateVaultLibraryRequest> _created = [];
    private readonly List<NsfwSetPinRequest> _pinSaves = [];
    private readonly List<(Guid Profile, SetNsfwAccessRequest Request)> _accessSaves = [];
    private readonly List<(Guid Profile, ResetNsfwPinRequest Request)> _resets = [];
    private IReadOnlyList<VaultLibraryDto> _libraries = [];
    private VaultOutcome _createOutcome = VaultOutcome.Ok;
    private VaultOutcome _firstPinSaveOutcome = VaultOutcome.Ok;
    private VaultOutcome _lockOutcome = VaultOutcome.Ok;
    private EngineApiClientStub _stub = default!;
    private readonly ManualTime _time = new();

    public VaultExperienceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddScoped(_ => new DashboardSessionAccessor());
    }

    private NsfwStatusDto Status() => new() { Mode = _mode ?? string.Empty, Unlocked = _unlocked, HasPin = _hasPin, IdleLockMinutes = 15 };

    private void UseEngine()
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            _stub = stub;
            stub.SetHandler(nameof(IEngineApiClient.GetNsfwStatusAsync), _ =>
                Task.FromResult(_mode is null ? VaultCall<NsfwStatusDto>.Of(VaultOutcome.NotAvailable) : VaultCall<NsfwStatusDto>.Ok(Status())));
            stub.SetHandler(nameof(IEngineApiClient.UnlockNsfwAsync), args =>
            {
                var pin = (string?)args![0];
                _unlockPins.Add(pin);
                if (_mode == "allowed" || pin == RightPin)
                {
                    _unlocked = true;
                    return Task.FromResult(VaultCall<NsfwStatusDto>.Ok(Status()));
                }

                return Task.FromResult(VaultCall<NsfwStatusDto>.Of(VaultOutcome.PinRequired));
            });
            stub.SetHandler(nameof(IEngineApiClient.LockNsfwAsync), _ =>
            {
                _lockCalls++;
                _unlocked = false;
                return Task.FromResult(_lockOutcome == VaultOutcome.Ok ? VaultCall<bool>.Ok(true) : VaultCall<bool>.Of(_lockOutcome));
            });
            stub.SetHandler(nameof(IEngineApiClient.KeepNsfwAliveAsync), _ =>
            {
                _keepAlives++;
                return Task.FromResult(_unlocked ? VaultCall<bool>.Ok(true) : VaultCall<bool>.Of(VaultOutcome.NotAvailable));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetVaultLibrariesAsync), _ =>
                Task.FromResult(VaultCall<IReadOnlyList<VaultLibraryDto>>.Ok(_libraries)));
            stub.SetHandler(nameof(IEngineApiClient.CreateVaultLibraryAsync), args =>
            {
                var request = (CreateVaultLibraryRequest)args![0]!;
                _created.Add(request);
                return Task.FromResult(_createOutcome == VaultOutcome.Ok && request.AdminPin == AdminPin
                    ? VaultCall<VaultLibraryDto>.Ok(new VaultLibraryDto { Id = "vault-1", LookupSource = "none" })
                    : VaultCall<VaultLibraryDto>.Of(_createOutcome == VaultOutcome.Ok ? VaultOutcome.PinRequired : _createOutcome));
            });
            stub.SetHandler(nameof(IEngineApiClient.SetNsfwPinAsync), args =>
            {
                var request = (NsfwSetPinRequest)args![0]!;
                _pinSaves.Add(request);
                if (_pinSaves.Count == 1 && _firstPinSaveOutcome != VaultOutcome.Ok)
                {
                    return Task.FromResult(VaultCall<bool>.Of(_firstPinSaveOutcome));
                }

                var wrongCurrent = _hasPin && request.CurrentPin != RightPin;
                return Task.FromResult(wrongCurrent ? VaultCall<bool>.Of(VaultOutcome.PinRequired) : VaultCall<bool>.Ok(true));
            });
            stub.SetHandler(nameof(IEngineApiClient.SetProfileNsfwAccessAsync), args =>
            {
                var request = (SetNsfwAccessRequest)args![1]!;
                _accessSaves.Add(((Guid)args[0]!, request));
                return Task.FromResult(request.Mode != "off" && request.AdminPin != AdminPin
                    ? VaultCall<NsfwStatusDto>.Of(VaultOutcome.PinRequired)
                    : VaultCall<NsfwStatusDto>.Ok(new NsfwStatusDto { Mode = request.Mode }));
            });
            stub.SetHandler(nameof(IEngineApiClient.ResetProfileNsfwPinAsync), args =>
            {
                _resets.Add(((Guid)args![0]!, (ResetNsfwPinRequest)args[1]!));
                return Task.FromResult(VaultCall<bool>.Ok(true));
            });
        });
        Services.AddSingleton(api);
        Services.AddSingleton<TimeProvider>(_time);
    }

    private NsfwModeState State => Services.GetRequiredService<NsfwModeState>();

    private NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

    private static void AssertNothingDrawn(string markup)
    {
        foreach (var token in Tokens)
        {
            Assert.DoesNotContain(token, markup, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task LoadAsync(bool kids = false) => await State.RefreshAsync(Me, kids);

    private static ProfileViewModel Person(Guid id, bool kids = false, string? limit = null, bool hasPin = false) =>
        new(id, "Sam", "#C9922E", kids ? "RestrictedProfile" : "Profile", DateTimeOffset.UtcNow, HasPin: hasPin, ContentLimit: limit);

    // -- Off and Kids profiles: not drawn, not even present -------------------------------------------------------------

    [Fact]
    public async Task AnOffProfile_HasNoTraceOfNsfwModeOrTheVault_InTheMenuTheSearchTheProfilePageOrTheNavigation()
    {
        UseEngine();
        _mode = null;
        await LoadAsync();

        Assert.False(State.IsAvailable);
        var menu = Render<TopNavAccountMenu>(parameters => parameters.Add(p => p.DisplayName, "Sam").Add(p => p.ShowSignOut, true));
        menu.Find(".top-nav-account-menu__trigger").Click();
        AssertNothingDrawn(menu.Markup);

        AssertNothingDrawn(Render<NsfwQuickCheck>().Markup);
        AssertNothingDrawn(Render<VaultSearchGroup>(parameters => parameters.Add(p => p.Query, "dune")).Markup);
        var settings = Render<VaultProfileSettings>(parameters => parameters
            .Add(p => p.Person, Person(Me)).Add(p => p.IsSelf, true).Add(p => p.IsAdministrator, false));
        Assert.Equal(string.Empty, settings.Markup.Trim());
        Assert.DoesNotContain(ShellNavigation.WithVault(ShellNavigation.TopBarLinks(isKids: false), State.Unlocked), link => link.Key == "Vault");
        Assert.DoesNotContain(ShellNavigation.WithVault(ShellNavigation.MenuLinks(isKids: false), State.Unlocked), link => link.Key == "Vault");
    }

    [Fact]
    public async Task AKidsProfile_IsNeverAskedAboutNsfwMode_EvenIfTheEngineWouldSayYes_AndNothingIsDrawn()
    {
        UseEngine();
        _mode = "allowed";
        await LoadAsync(kids: true);

        Assert.False(State.IsAvailable);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetNsfwStatusAsync), _stub.Calls);

        var menu = Render<TopNavAccountMenu>(parameters => parameters.Add(p => p.DisplayName, "Maya").Add(p => p.IsKids, true));
        menu.Find(".top-nav-account-menu__trigger").Click();
        AssertNothingDrawn(menu.Markup);

        // A Kids or limited profile's own page, and an administrator looking at one, offer nothing either.
        foreach (var person in new[] { Person(Me, kids: true), Person(Me, limit: "PG") })
        {
            foreach (var administrator in new[] { false, true })
            {
                var cut = Render<VaultProfileSettings>(parameters => parameters
                    .Add(p => p.Person, person).Add(p => p.IsSelf, true).Add(p => p.IsAdministrator, administrator).Add(p => p.AdministratorHasPin, true));
                Assert.Equal(string.Empty, cut.Markup.Trim());
            }
        }

        Assert.DoesNotContain(ShellNavigation.WithVault(ShellNavigation.TopBarLinks(isKids: true), showVault: false), link => link.Key == "Vault");
        Assert.DoesNotContain(nameof(IEngineApiClient.GetNsfwStatusAsync), _stub.Calls);
    }

    // -- A profile on "PIN to unlock" -----------------------------------------------------------------------------------

    [Fact]
    public async Task APinProfile_SeesNoVaultUntilItEntersItsPin_ThenTheMenuGainsTheArea()
    {
        UseEngine();
        _mode = "pin";
        await LoadAsync();
        await State.UpdatePreferencesAsync(preferences => preferences with { ShowQuickCheck = true });
        var dialogs = Render<AppDialogHost>();
        var check = Render<NsfwQuickCheck>();

        Assert.True(State.IsAvailable);
        Assert.False(State.Unlocked);
        Assert.Contains("SFW", check.Markup);
        Assert.DoesNotContain(ShellNavigation.WithVault(ShellNavigation.TopBarLinks(isKids: false), State.Unlocked), link => link.Key == "Vault");

        // Flip the check: the PIN pad opens, and a wrong PIN changes nothing.
        await check.InvokeAsync(() => check.Find("input[type='checkbox'], input[role='switch']").Change(true));
        dialogs.WaitForAssertion(() => Assert.Contains("Enter your NSFW PIN", dialogs.Markup));
        await TypePinAsync(dialogs, "1111");
        await PressAsync(dialogs, "Switch on");
        dialogs.WaitForAssertion(() => Assert.Contains(VaultMessages.WrongPin, dialogs.Markup));
        Assert.False(State.Unlocked);

        await TypePinAsync(dialogs, RightPin);
        await PressAsync(dialogs, "Switch on");
        dialogs.WaitForAssertion(() => Assert.True(State.Unlocked));
        Assert.Equal(["1111", RightPin], _unlockPins);
        Assert.Contains(ShellNavigation.WithVault(ShellNavigation.TopBarLinks(isKids: false), State.Unlocked), link => link.Key == "Vault" && link.Path == "/vault");
        check.WaitForAssertion(() => Assert.Contains(">NSFW<", check.Markup));
    }

    [Fact]
    public async Task AnAllowedProfile_SwitchesOnWithoutAPin()
    {
        UseEngine();
        _mode = "allowed";
        await LoadAsync();

        var switched = await Services.GetRequiredService<NsfwModeSwitching>().SetAsync(true);

        Assert.True(switched);
        Assert.True(State.Unlocked);
        Assert.Equal([null], _unlockPins);
    }

    [Fact]
    public async Task APinProfileWithNoPinYet_IsToldToSetOneFirst_AndNothingIsSent()
    {
        UseEngine();
        _mode = "pin";
        _hasPin = false;
        await LoadAsync();

        Assert.False(await Services.GetRequiredService<NsfwModeSwitching>().SetAsync(true));

        Assert.Empty(_unlockPins);
        Assert.False(State.Unlocked);
    }

    [Fact]
    public async Task SwitchingOff_IsInstant_HidesTheVaultAndReturnsHome()
    {
        UseEngine();
        _mode = "allowed";
        await LoadAsync();
        await State.UnlockAsync(null);
        Navigation.NavigateTo("/vault");

        await Services.GetRequiredService<NsfwModeSwitching>().SetAsync(false);

        Assert.False(State.Unlocked);
        Assert.Equal(1, _lockCalls);
        Assert.Equal("/", new Uri(Navigation.Uri).AbsolutePath);
    }

    [Fact]
    public async Task Locking_FailsClosed_WhenTheEngineCannotBeReached()
    {
        UseEngine();
        _mode = "allowed";
        _lockOutcome = VaultOutcome.Failed;
        await LoadAsync();
        await State.UnlockAsync(null);

        await State.LockAsync();

        Assert.False(State.Unlocked);
    }

    [Fact]
    public async Task EveryVaultViewStartsBlurred_AfterTheAreaLocksAndUnlocksAgain()
    {
        UseEngine();
        _mode = "allowed";
        await LoadAsync();
        await State.UnlockAsync(null);
        Assert.Equal(VaultCoverMode.Blur, State.EffectiveCovers);

        State.SetCoversBlurred(false);
        Assert.Equal(VaultCoverMode.Show, State.EffectiveCovers);

        await State.LockAsync();
        await State.UnlockAsync(null);
        Assert.Equal(VaultCoverMode.Blur, State.EffectiveCovers);

        await State.UpdatePreferencesAsync(preferences => preferences with { Covers = VaultCoverMode.Hide });
        State.SetCoversBlurred(false);
        Assert.Equal(VaultCoverMode.Hide, State.EffectiveCovers);
    }

    // -- Instant lock, lock on profile switch, idle ---------------------------------------------------------------------

    [Fact]
    public async Task PressingEscapeTwice_LocksAtOnce_AndReturnsHome()
    {
        UseEngine();
        _mode = "allowed";
        await LoadAsync();
        await State.UnlockAsync(null);
        Navigation.NavigateTo("/vault/scenes");
        var watcher = Render<NsfwLockWatcher>(parameters => parameters.Add(p => p.ActiveProfileId, Me).Add(p => p.IsKids, false));

        await watcher.InvokeAsync(() => watcher.Instance.OnDoubleEscape());

        Assert.False(State.Unlocked);
        Assert.Equal(1, _lockCalls);
        Assert.Equal("/", new Uri(Navigation.Uri).AbsolutePath);
    }

    [Fact]
    public async Task TheLockScript_IsLoadedOnlyWhileNsfwModeIsOn()
    {
        UseEngine();
        _mode = "allowed";
        var module = JSInterop.SetupModule("./js/vault-lock.js");
        module.Mode = JSRuntimeMode.Loose;
        var watcher = Render<NsfwLockWatcher>(parameters => parameters.Add(p => p.ActiveProfileId, Me).Add(p => p.IsKids, false));
        watcher.WaitForAssertion(() => Assert.True(State.IsAvailable));

        Assert.DoesNotContain(module.Invocations, call => call.Identifier == "start");

        await watcher.InvokeAsync(() => State.UnlockAsync(null));
        watcher.WaitForAssertion(() => Assert.Contains(module.Invocations, call => call.Identifier == "start"));

        await watcher.InvokeAsync(() => State.LockAsync());
        watcher.WaitForAssertion(() => Assert.Contains(module.Invocations, call => call.Identifier == "stop"));
    }

    [Fact]
    public async Task SwitchingProfile_LeavesTheNewProfileLocked_AndSendsAnOpenVaultHome()
    {
        UseEngine();
        _mode = "allowed";
        await LoadAsync();
        await State.UnlockAsync(null);
        Navigation.NavigateTo("/vault");
        var watcher = Render<NsfwLockWatcher>(parameters => parameters.Add(p => p.ActiveProfileId, Me).Add(p => p.IsKids, false));
        watcher.WaitForAssertion(() => Assert.True(State.Unlocked));

        // The Engine locks the device session when the profile changes; the Dashboard follows what the status route now says.
        _unlocked = false;
        watcher.Render(parameters => parameters.Add(p => p.ActiveProfileId, Other).Add(p => p.IsKids, false));

        watcher.WaitForAssertion(() => Assert.Equal(Other, State.ProfileId));
        Assert.False(State.Unlocked);
        watcher.WaitForAssertion(() => Assert.Equal("/", new Uri(Navigation.Uri).AbsolutePath));

        // And a switch to a Kids profile clears everything without asking the Engine again.
        var asked = _stub.Calls.Count(call => call == nameof(IEngineApiClient.GetNsfwStatusAsync));
        watcher.Render(parameters => parameters.Add(p => p.ActiveProfileId, Me).Add(p => p.IsKids, true));
        watcher.WaitForAssertion(() => Assert.False(State.IsAvailable));
        Assert.Equal(asked, _stub.Calls.Count(call => call == nameof(IEngineApiClient.GetNsfwStatusAsync)));
    }

    [Fact]
    public async Task RealUse_RestartsTheIdleTimer_Throttled_AndALockedAnswerLocksTheDashboard()
    {
        UseEngine();
        _mode = "allowed";
        await LoadAsync();
        await State.UnlockAsync(null);
        var watcher = Render<NsfwLockWatcher>(parameters => parameters.Add(p => p.ActiveProfileId, Me).Add(p => p.IsKids, false));

        await watcher.InvokeAsync(() => watcher.Instance.OnActivity());
        Assert.Equal(0, _keepAlives);

        _time.Advance(TimeSpan.FromSeconds(30));
        await watcher.InvokeAsync(() => watcher.Instance.OnActivity());
        await watcher.InvokeAsync(() => watcher.Instance.OnActivity());
        Assert.Equal(1, _keepAlives);

        // The Engine's idle limit ran out in the meantime: the keep-alive learns it, and the Vault goes away.
        _unlocked = false;
        _time.Advance(TimeSpan.FromSeconds(30));
        await watcher.InvokeAsync(() => watcher.Instance.OnActivity());
        Assert.False(State.Unlocked);
    }

    // -- The area ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheVaultPage_WaitsForTheEngine_ThenSendsAnyoneNotUnlockedToTheSameNotFoundAMissingPageGets()
    {
        UseEngine();
        _mode = null;

        var cut = Render<VaultPage>();
        AssertNothingDrawn(cut.Markup);
        Assert.DoesNotContain("not-found", Navigation.Uri);

        await State.RefreshAsync(Me, false);

        cut.WaitForAssertion(() => Assert.EndsWith("/not-found", Navigation.Uri));
        AssertNothingDrawn(cut.Markup);
    }

    [Fact]
    public async Task TheVaultPage_ForAnUnlockedSession_ShowsTheAreaWithItsToolsAndTheViewsOfTheSpec()
    {
        UseEngine();
        _mode = "allowed";
        await LoadAsync();
        await State.UnlockAsync(null);

        var cut = Render<VaultPage>();

        Assert.Contains("data-vault-area", cut.Markup);
        Assert.Contains("Blur", cut.Markup);
        Assert.Contains("Lock now", cut.Markup);
        foreach (var label in new[] { "Recently added", "Scenes", "Folders", "Unmatched", "Images", "Performers", "Studios", "Tags" })
        {
            Assert.Contains(label, cut.Markup);
        }

        foreach (var option in new[] { "Shelf rows", "Grid of covers", "Details list", "Timeline by date added", "Blur until pointed at" })
        {
            Assert.Contains(option, cut.Markup);
        }

        // Not an administrator: the library settings tab is not offered.
        Assert.DoesNotContain("Library settings", cut.Markup);
        // The list is the marked seam: it says the Vault is empty rather than pretending.
        Assert.Contains("Nothing in Recently added yet", cut.Markup);
    }

    [Fact]
    public async Task AnAdministrator_GetsTheLibrarySettingsInsideTheArea_AndLockNowReturnsHome()
    {
        UseEngine();
        _mode = "allowed";
        _libraries = [new VaultLibraryDto { Id = "vault-1", LookupSource = "none" }];
        var session = new DashboardSessionAccessor();
        session.Set("token", Me, Me, Guid.NewGuid(),
            new DashboardAuthorityResponse(Me, Me, true, true, 1, 1, true, true, null, 1, [], ["settings.administration"], ["household.manage"]));
        Services.AddScoped(_ => session);
        await LoadAsync();
        await State.UnlockAsync(null);

        var cut = Render<VaultArea>(parameters => parameters.Add(p => p.View, VaultViews.LibrarySettings));

        cut.WaitForAssertion(() => Assert.Contains("data-vault-library-settings", cut.Markup));
        Assert.Contains("Library settings", cut.Markup);
        Assert.Contains("visible on disk", cut.Markup);
        Assert.Contains("Remove library", cut.Markup);

        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Trim() == "Lock now").Click());
        Assert.False(State.Unlocked);
        Assert.Equal("/", new Uri(Navigation.Uri).AbsolutePath);
    }

    [Fact]
    public async Task ViewingOptions_AreRememberedPerProfile()
    {
        UseEngine();
        _mode = "allowed";
        await LoadAsync();
        await State.UnlockAsync(null);
        var cut = Render<VaultViewOptions>();

        await cut.InvokeAsync(() => cut.Find("select[aria-label='Layout']").Change(VaultLayout.Timeline.ToString()));
        await cut.InvokeAsync(() => cut.Find("select[aria-label='Covers']").Change(VaultCoverMode.Hide.ToString()));
        await cut.InvokeAsync(() => cut.Find("select[aria-label='Titles']").Change("hidden"));

        Assert.Equal(VaultLayout.Timeline, State.Preferences.Layout);
        Assert.Equal(VaultCoverMode.Hide, State.Preferences.Covers);
        Assert.True(State.Preferences.TitlesHiddenUntilPointed);
        var saved = await Services.GetRequiredService<IVaultPreferenceStore>().LoadAsync(Me);
        Assert.Equal(State.Preferences, saved);
        Assert.Equal(VaultViewPreferences.Default, await Services.GetRequiredService<IVaultPreferenceStore>().LoadAsync(Other));
    }

    // -- Search and covers ------------------------------------------------------------------------------------------------

    private sealed class FakeCatalogue : IVaultCatalogue
    {
        public Task<VaultCatalogueResult> ListAsync(string viewKey, VaultViewPreferences preferences, CancellationToken ct = default) =>
            Task.FromResult(VaultCatalogueResult.NotYetAvailable);

        public Task<VaultCatalogueResult> SearchAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(new VaultCatalogueResult(true, [new VaultCatalogueItem("1", "Secret Title", null, "/cover/1.jpg", "/vault/item/1")]));
    }

    [Fact]
    public async Task TheVaultGroupInSearch_AppearsOnlyWhileNsfwModeIsOn_WithTheBlurToggle_AndCoversBlurredUntilToggled()
    {
        Services.AddScoped<IVaultCatalogue>(_ => new FakeCatalogue());
        UseEngine();
        _mode = "pin";
        await LoadAsync();

        var cut = Render<VaultSearchGroup>(parameters => parameters.Add(p => p.Query, "secret"));
        AssertNothingDrawn(cut.Markup);

        await State.UnlockAsync(RightPin);
        cut.WaitForAssertion(() => Assert.Contains("data-vault-search-group", cut.Markup), TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => Assert.Contains("Secret Title", cut.Markup), TimeSpan.FromSeconds(10));
        Assert.Equal("Vault", cut.Find("#vault-search-title").TextContent);
        Assert.Contains("Blur", cut.Markup);
        Assert.Contains("vault-cover--blur", cut.Markup);

        await cut.InvokeAsync(() => State.SetCoversBlurred(false));
        cut.WaitForAssertion(() => Assert.Contains("vault-cover--show", cut.Markup));

        // Locked again: the group is gone, and so are the covers.
        await cut.InvokeAsync(() => State.LockAsync());
        cut.WaitForAssertion(() => AssertNothingDrawn(cut.Markup));
    }

    [Fact]
    public void Covers_ShowBlurOrHide_AndAHiddenCoverNeverPutsTheImageAddressOnThePage()
    {
        var blur = Render<VaultCover>(parameters => parameters.Add(p => p.Title, "Some Title").Add(p => p.CoverUrl, "/cover/a.jpg").Add(p => p.Mode, VaultCoverMode.Blur));
        Assert.Contains("vault-cover--blur", blur.Markup);
        Assert.Contains("/cover/a.jpg", blur.Markup);

        var show = Render<VaultCover>(parameters => parameters.Add(p => p.Title, "Some Title").Add(p => p.CoverUrl, "/cover/a.jpg").Add(p => p.Mode, VaultCoverMode.Show));
        Assert.Contains("vault-cover--show", show.Markup);

        var hidden = Render<VaultCover>(parameters => parameters
            .Add(p => p.Title, "Some Title").Add(p => p.CoverUrl, "/cover/a.jpg").Add(p => p.Mode, VaultCoverMode.Hide).Add(p => p.TitleHidden, true));
        Assert.DoesNotContain("/cover/a.jpg", hidden.Markup);
        Assert.Contains("ST", hidden.Markup);
        Assert.Contains("vault-cover--title-hidden", hidden.Markup);
        Assert.Contains("aria-label=\"Open item\"", hidden.Markup);
    }

    // -- Add folder: the third choice -------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAddFolderQuestion_OffersTheVaultOnlyWhenAskedTo_AndTheAnswerSaysSo()
    {
        var dialogs = Render<AppDialogHost>();
        var service = Services.GetRequiredService<IAppDialogService>();

        async Task<IAppDialogReference> AskAsync(bool showVault) => await service.ShowAsync<AddFolderPlaceDialog>(string.Empty,
            new AppDialogParameters<AddFolderPlaceDialog> { { component => component.ShowVault, showVault } }, new AppDialogOptions());

        var without = await AskAsync(showVault: false);
        dialogs.WaitForAssertion(() => Assert.Contains("Where does this folder belong?", dialogs.Markup));
        AssertNothingDrawn(dialogs.Markup);
        without.Close();

        var with = await AskAsync(showVault: true);
        dialogs.WaitForAssertion(() => Assert.Contains("Vault (adults only)", dialogs.Markup));
        dialogs.FindAll(".app-choice-card").Single(card => card.TextContent.Contains("Vault (adults only)")).Click();
        var answer = Assert.IsType<AddFolderPlaceDialog.AddFolderPlaceAnswer>((await with.Result)!.Data);
        Assert.True(answer.Vault);
        Assert.False(answer.Watch);
    }

    private async Task<(IRenderedComponent<AppDialogHost> Dialogs, VaultLibraryOnboarding Onboarding)> OnboardingAsync()
    {
        UseEngine();
        _mode = "allowed";
        await LoadAsync();
        await State.UnlockAsync(null);
        return (Render<AppDialogHost>(), Services.GetRequiredService<VaultLibraryOnboarding>());
    }

    [Fact]
    public async Task AddToVault_ForAnAdministratorWithoutAPin_AsksThemToSetOneFirst_AndCreatesNothing()
    {
        var (dialogs, onboarding) = await OnboardingAsync();

        var task = onboarding.AddFolderAsync("/media/adult", Me, administratorHasPin: false);
        dialogs.WaitForAssertion(() => Assert.Contains("Set your PIN first", dialogs.Markup));
        await PressAsync(dialogs, "Open my profile");
        var result = await task;

        Assert.Equal(VaultOnboardingOutcome.Cancelled, result.Outcome);
        Assert.Empty(_created);
        Assert.DoesNotContain(nameof(IEngineApiClient.CreateVaultLibraryAsync), _stub.Calls);
        dialogs.WaitForAssertion(() => Assert.EndsWith($"/settings/profiles/{Me:D}", Navigation.Uri));
    }

    [Fact]
    public async Task AddToVault_ConfirmsAdultsAndThePin_CreatesTheLibraryOnce_AndLeavesTheFolderForTheImportWork()
    {
        var (dialogs, onboarding) = await OnboardingAsync();

        var task = onboarding.AddFolderAsync("/media/adult", Me, administratorHasPin: true);
        dialogs.WaitForAssertion(() => Assert.Contains("Adults only", dialogs.Markup));
        Assert.Contains("visible on disk", dialogs.Markup);
        await PressAsync(dialogs, "I confirm");

        dialogs.WaitForAssertion(() => Assert.Contains("Enter your PIN to continue", dialogs.Markup));
        await TypePinAsync(dialogs, "9999");
        await PressAsync(dialogs, "Create library");
        dialogs.WaitForAssertion(() => Assert.Contains(VaultMessages.WrongPin, dialogs.Markup));
        await TypePinAsync(dialogs, AdminPin);
        await PressAsync(dialogs, "Create library");
        var result = await task;

        Assert.Equal(VaultOnboardingOutcome.LibraryReadyFolderNotAttached, result.Outcome);
        Assert.Equal(VaultLibraryOnboarding.ReadyButNotAttached, result.Message);
        Assert.Equal(2, _created.Count);
        var accepted = _created[^1];
        Assert.True(accepted.AdultsConfirmed);
        Assert.Equal(AdminPin, accepted.AdminPin);
        Assert.Equal("none", accepted.LookupSource);
        // Nothing but the create was written to the Engine: the folder is the marked seam.
        Assert.DoesNotContain(_stub.Calls, call => call.StartsWith("Update", StringComparison.Ordinal) || call.StartsWith("Save", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddToVault_ReusesTheLibraryThatExists_AndTheSeamIsAskedForTheFolder()
    {
        var spy = new SpyAttachment();
        Services.AddScoped<IVaultFolderAttachment>(_ => spy);
        var (dialogs, onboarding) = await OnboardingAsync();
        _libraries = [new VaultLibraryDto { Id = "vault-1", LookupSource = "none" }];

        var task = onboarding.AddFolderAsync("/media/adult", Me, administratorHasPin: true);
        dialogs.WaitForAssertion(() => Assert.Contains("Adults only", dialogs.Markup));
        await PressAsync(dialogs, "I confirm");
        var result = await task;

        Assert.Equal(VaultOnboardingOutcome.FolderAttached, result.Outcome);
        Assert.Empty(_created);
        Assert.Equal(("vault-1", "/media/adult"), Assert.Single(spy.Attached));
    }

    [Fact]
    public async Task AddToVault_DoesNothing_WhenNsfwModeIsNotOn()
    {
        UseEngine();
        _mode = "pin";
        await LoadAsync();
        var onboarding = Services.GetRequiredService<VaultLibraryOnboarding>();

        var result = await onboarding.AddFolderAsync("/media/adult", Me, administratorHasPin: true);

        Assert.Equal(VaultOnboardingOutcome.NotAvailable, result.Outcome);
        Assert.DoesNotContain(nameof(IEngineApiClient.GetVaultLibrariesAsync), _stub.Calls);
    }

    private sealed class SpyAttachment : IVaultFolderAttachment
    {
        public List<(string Library, string Folder)> Attached { get; } = [];

        public Task<VaultFolderAttachmentResult> AttachAsync(VaultLibraryDto library, string folderPath, CancellationToken ct = default)
        {
            Attached.Add((library.Id, folderPath));
            return Task.FromResult(VaultFolderAttachmentResult.Attached);
        }
    }

    // -- The profile page: own switch and PIN, administrator access ------------------------------------------------------

    [Fact]
    public async Task OnTheirOwnPage_APinProfile_HasTheSwitch_TheQuickCheckOption_AndAnNsfwPinRow()
    {
        UseEngine();
        _mode = "pin";
        var cut = Render<VaultProfileSettings>(parameters => parameters
            .Add(p => p.Person, Person(Me)).Add(p => p.IsSelf, true).Add(p => p.IsAdministrator, false));

        cut.WaitForAssertion(() => Assert.Contains("data-profile-section=\"nsfw-mode\"", cut.Markup));
        Assert.Contains("Show an SFW/NSFW check in the top menu", cut.Markup);
        Assert.Contains("Different from your profile PIN", cut.Markup);
        Assert.DoesNotContain("data-profile-section=\"nsfw-access\"", cut.Markup);

        await cut.InvokeAsync(() => cut.FindAll("input[role='switch']").Single(input => input.GetAttribute("aria-label") == "Show an SFW/NSFW check in the top menu").Change(true));
        Assert.True(State.ShowQuickCheck);
    }

    [Fact]
    public async Task APinProfile_SavesItsFirstPin_TypedTwice_ConfirmingItsYouWhenTheEngineAsks()
    {
        UseEngine();
        _mode = "pin";
        _hasPin = false;
        _firstPinSaveOutcome = VaultOutcome.ConfirmItsYou;
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: true));
        var dialogs = Render<AppDialogHost>();
        var cut = Render<VaultProfileSettings>(parameters => parameters
            .Add(p => p.Person, Person(Me)).Add(p => p.IsSelf, true).Add(p => p.IsAdministrator, false));
        cut.WaitForAssertion(() => Assert.Contains("No PIN yet", cut.Markup));

        await cut.InvokeAsync(() => cut.Find("[data-nav-row='nsfw-pin']").Click());
        dialogs.WaitForAssertion(() => Assert.Contains("Choose an NSFW PIN", dialogs.Markup));
        await TypePinAsync(dialogs, RightPin);
        await PressAsync(dialogs, "Next");
        dialogs.WaitForAssertion(() => Assert.Contains("Enter it again", dialogs.Markup));
        await TypePinAsync(dialogs, "9999");
        await PressAsync(dialogs, "Save PIN");
        dialogs.WaitForAssertion(() => Assert.Contains("Those PINs didn't match.", dialogs.Markup));
        Assert.Empty(_pinSaves);
        await TypePinAsync(dialogs, RightPin);
        await PressAsync(dialogs, "Save PIN");

        dialogs.WaitForAssertion(() => Assert.DoesNotContain("Enter it again", dialogs.Markup));
        Assert.Equal(2, _pinSaves.Count);
        Assert.All(_pinSaves, save =>
        {
            Assert.Equal(RightPin, save.NewPin);
            Assert.Null(save.CurrentPin);
            Assert.Null(save.ProfilePin);
        });
    }

    [Fact]
    public async Task ChangingAPin_AsksForTheCurrentOneLast_SoAWrongOneCanBeRetriedWithoutRetypingTheNewPin()
    {
        UseEngine();
        _mode = "pin";
        _hasPin = true;
        var dialogs = Render<AppDialogHost>();
        var cut = Render<VaultProfileSettings>(parameters => parameters
            .Add(p => p.Person, Person(Me)).Add(p => p.IsSelf, true).Add(p => p.IsAdministrator, false));
        cut.WaitForAssertion(() => Assert.Contains("PIN set", cut.Markup));

        await cut.InvokeAsync(() => cut.Find("[data-nav-row='nsfw-pin']").Click());
        await TypePinAsync(dialogs, "8642");
        await PressAsync(dialogs, "Next");
        await TypePinAsync(dialogs, "8642");
        await PressAsync(dialogs, "Next");
        dialogs.WaitForAssertion(() => Assert.Contains("Enter your current NSFW PIN", dialogs.Markup));
        await TypePinAsync(dialogs, "0000");
        await PressAsync(dialogs, "Save PIN");
        dialogs.WaitForAssertion(() => Assert.Contains(VaultMessages.WrongPin, dialogs.Markup));
        await TypePinAsync(dialogs, RightPin);
        await PressAsync(dialogs, "Save PIN");

        dialogs.WaitForAssertion(() => Assert.DoesNotContain("Enter your current NSFW PIN", dialogs.Markup));
        Assert.Equal(["0000", RightPin], _pinSaves.Select(save => save.CurrentPin));
        Assert.All(_pinSaves, save => Assert.Equal("8642", save.NewPin));
    }

    [Fact]
    public async Task APinProfileWithAProfilePin_ProvesItWhenSavingItsFirstNsfwPin()
    {
        UseEngine();
        _mode = "pin";
        _hasPin = false;
        var dialogs = Render<AppDialogHost>();
        var cut = Render<VaultProfileSettings>(parameters => parameters
            .Add(p => p.Person, Person(Me, hasPin: true)).Add(p => p.IsSelf, true).Add(p => p.IsAdministrator, false));
        cut.WaitForAssertion(() => Assert.Contains("No PIN yet", cut.Markup));

        await cut.InvokeAsync(() => cut.Find("[data-nav-row='nsfw-pin']").Click());
        await TypePinAsync(dialogs, "8642");
        await PressAsync(dialogs, "Next");
        await TypePinAsync(dialogs, "8642");
        await PressAsync(dialogs, "Next");
        dialogs.WaitForAssertion(() => Assert.Contains("Enter your profile PIN", dialogs.Markup));
        await TypePinAsync(dialogs, "5555");
        await PressAsync(dialogs, "Save PIN");

        dialogs.WaitForAssertion(() => Assert.Single(_pinSaves));
        Assert.Equal("5555", _pinSaves[0].ProfilePin);
        Assert.Null(_pinSaves[0].CurrentPin);
    }

    [Fact]
    public async Task AnAdministrator_SetsAnotherAdultsAccess_WithTheirOwnPin_AndClosingItNeedsNone()
    {
        UseEngine();
        _mode = null;
        var dialogs = Render<AppDialogHost>();
        var cut = Render<VaultProfileSettings>(parameters => parameters
            .Add(p => p.Person, Person(Other)).Add(p => p.IsSelf, false).Add(p => p.IsAdministrator, true).Add(p => p.AdministratorHasPin, true));

        cut.WaitForAssertion(() => Assert.Contains("data-profile-section=\"nsfw-access\"", cut.Markup));
        Assert.DoesNotContain("data-profile-section=\"nsfw-mode\"", cut.Markup);
        Assert.Contains("The current setting isn't shown for other people", cut.Markup);
        Assert.True(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save access").HasAttribute("disabled"));

        await cut.InvokeAsync(() => cut.Find("select[aria-label='NSFW access for Sam']").Change("pin"));
        await cut.InvokeAsync(() => cut.Find("select[aria-label='Switch NSFW mode off after']").Change("30"));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save access").Click());
        dialogs.WaitForAssertion(() => Assert.Contains("Enter your PIN to continue", dialogs.Markup));
        await TypePinAsync(dialogs, "0000");
        await PressAsync(dialogs, "Save access");
        dialogs.WaitForAssertion(() => Assert.Contains(VaultMessages.WrongPin, dialogs.Markup));
        await TypePinAsync(dialogs, AdminPin);
        await PressAsync(dialogs, "Save access");
        dialogs.WaitForAssertion(() => Assert.DoesNotContain("Enter your PIN to continue", dialogs.Markup));

        var saved = _accessSaves[^1];
        Assert.Equal(Other, saved.Profile);
        Assert.Equal("pin", saved.Request.Mode);
        Assert.Equal(30, saved.Request.IdleLockMinutes);
        Assert.Equal(AdminPin, saved.Request.AdminPin);

        // Reset their PIN: asks for the administrator's own PIN again.
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Trim() == "Reset their NSFW PIN").Click());
        await TypePinAsync(dialogs, AdminPin);
        await PressAsync(dialogs, "Reset PIN");
        dialogs.WaitForAssertion(() => Assert.Single(_resets));
        Assert.Equal((Other, AdminPin), (_resets[0].Profile, _resets[0].Request.AdminPin));

        // Closing access is not a grant, so it asks for no PIN.
        await cut.InvokeAsync(() => cut.Find("select[aria-label='NSFW access for Sam']").Change("off"));
        var before = _accessSaves.Count;
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save access").Click());
        cut.WaitForAssertion(() => Assert.Equal(before + 1, _accessSaves.Count));
        Assert.Null(_accessSaves[^1].Request.AdminPin);
    }

    [Fact]
    public void AnAdministratorWithNoProfilePin_IsToldToSetOneFirst_AndGetsNoControls()
    {
        UseEngine();
        var cut = Render<VaultProfileSettings>(parameters => parameters
            .Add(p => p.Person, Person(Other)).Add(p => p.IsSelf, false).Add(p => p.IsAdministrator, true).Add(p => p.AdministratorHasPin, false));

        Assert.Contains("Set your own profile PIN first", cut.Markup);
        Assert.Empty(cut.FindAll("select"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Save access");
    }

    // -- Helpers ----------------------------------------------------------------------------------------------------------

    private static async Task TypePinAsync(IRenderedComponent<AppDialogHost> dialogs, string pin)
    {
        foreach (var digit in pin)
        {
            await dialogs.InvokeAsync(() => dialogs.Find($".pin-pad__key[aria-label='{digit}']").Click());
        }
    }

    private static async Task PressAsync(IRenderedComponent<AppDialogHost> dialogs, string label)
    {
        dialogs.WaitForAssertion(() => Assert.Contains(dialogs.FindAll("button"), button => button.TextContent.Trim() == label && !button.HasAttribute("disabled")));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").First(button => button.TextContent.Trim() == label && !button.HasAttribute("disabled")).Click());
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
