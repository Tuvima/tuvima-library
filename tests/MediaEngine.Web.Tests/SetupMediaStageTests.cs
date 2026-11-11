using System.Security.Claims;
using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Setup;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Setup;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// First-run setup's "What do you have?" step (first-run setup rework): one libraries table (Library, Area, Folders, Status) with one Add library button,
// libraries as rows, every change saved through setup's own session, with the footer (Skip for now, Continue), step key and deferral.
public sealed class SetupMediaStageTests : AsyncBunitContext
{
    private const string Session = "setup-session";
    private const string MoviesId = "11111111-1111-4111-8111-111111111111";
    private const string VaultId = "99999999-9999-4999-8999-999999999999";

    private LibrariesConfigurationDto _configuration = new()
    {
        StorageLocations = [new() { Id = "media", Label = "Media storage", Path = "C:\\media", AllowWrite = true }],
    };

    private readonly List<string?> _setupSessions = [];
    private readonly List<UpdateLibrariesRequest> _setupSaves = [];
    private bool _failLoads;
    private TaskCompletionSource<LibrariesConfigurationDto?>? _loadGate;
    private bool _failKinds;
    private int _deferred;
    private int _validated;
    private int _created;

    public SetupMediaStageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(new DashboardIdentityClient(new IdleClientFactory()));
        Services.AddScoped<AuthenticationStateProvider>(_ => new SignedOutProvider());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler("get_LastError", _ => null);
            stub.SetHandler(nameof(IEngineApiClient.GetSetupLibrariesAsync), args =>
            {
                _setupSessions.Add((string?)args![0]);
                if (_loadGate is not null) return _loadGate.Task;
                return Task.FromResult<LibrariesConfigurationDto?>(_failLoads ? null : _configuration);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetSetupLibraryKindsAsync), _ =>
                Task.FromResult<IReadOnlyList<LibraryKindDto>?>(_failKinds ? null : LibraryKindsTestData.BuiltIn()));
            stub.SetHandler(nameof(IEngineApiClient.UpdateSetupLibrariesAsync), args =>
            {
                var request = (UpdateLibrariesRequest)args![0]!;
                _setupSaves.Add(request);
                _configuration = new LibrariesConfigurationDto { StorageLocations = request.StorageLocations, Libraries = request.Libraries, FileHandling = request.FileHandling };
                return Task.FromResult<LibrariesConfigurationDto?>(_configuration);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetSetupServerFolderRootsAsync), _ => Task.FromResult<IReadOnlyList<ServerStorageLocationDto>>(_configuration.StorageLocations));
            stub.SetHandler(nameof(IEngineApiClient.BrowseSetupServerFoldersAsync), _ => Task.FromResult<BrowseServerFoldersResultDto?>(new() { DisplayPath = "C:\\media" }));
            stub.SetHandler(nameof(IEngineApiClient.ValidateSetupServerFolderAsync), _ => Task.FromResult<ServerFolderValidationResultDto?>(
                new() { Path = "C:\\media", StorageLocationId = "media", Exists = true, HasRead = true, HasWrite = true, CanSelect = true }));
        }));
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        _dialogs = Render<AppDialogHost>();
    }

    private readonly IRenderedComponent<AppDialogHost> _dialogs;

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private void AddMoviesLibrary() => _configuration.Libraries.Add(new()
    {
        Id = MoviesId,
        Name = "Movies",
        KindKey = "movies",
        Area = "watch",
        Kind = "catalogued",
        MediaTypes = ["Movies"],
        Sources = [new() { Id = "movies-main", Path = "E:\\Library\\Movies", ManagementMode = "existing_library", AccessMode = "read_only" }],
    });

    private IRenderedComponent<SetupLibrariesStage> RenderStage(bool hasLibraries = false)
    {
        var cut = Render<SetupLibrariesStage>(parameters => parameters
            .Add(x => x.SetupSession, Session)
            .Add(x => x.HasLibraries, hasLibraries)
            .Add(x => x.DeferMedia, EventCallback.Factory.Create(this, () => _deferred++))
            .Add(x => x.ValidateMedia, EventCallback.Factory.Create(this, () => _validated++))
            .Add(x => x.LibraryCreated, EventCallback.Factory.Create(this, () => _created++)));
        cut.WaitForAssertion(() => Assert.True(cut.FindAll("[data-setup-libraries-table], .alert, [role=alert]").Count > 0));
        return cut;
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<SetupLibrariesStage> cut, string text) => cut.FindAll("button").Single(x => Text(x) == text);

    private static string[] Names(IRenderedComponent<SetupLibrariesStage> cut) => cut.FindAll(".setup-libs__name strong").Select(Text).ToArray();

    private AngleSharp.Dom.IElement DialogButton(string text) => _dialogs.FindAll("button").Single(x => Text(x) == text);

    // 1. One table: Library, Area, Folders and Status, with one Add library button and an empty state while nothing is added.
    [Fact]
    public void TheStageAsksWhatDoYouHaveAndDrawsOneLibrariesTableWithOneAddLibraryButton()
    {
        var cut = RenderStage();

        Assert.Equal("What do you have?", Text(cut.Find("h2")));
        Assert.Single(cut.FindComponents<SetupLibrariesTable>());
        Assert.Equal(Session, cut.FindComponent<SetupLibrariesTable>().Instance.SetupSession);
        Assert.Contains("No libraries yet", cut.Markup, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("button"), button => Text(button) == "Add library");
        Assert.Empty(cut.FindAll(".setup-area"));

        // No search, filter or layout switch.
        Assert.Empty(cut.FindAll("input[type=search], select, [aria-label='Libraries layout'], [aria-label='Search libraries']"));
        Assert.Contains(Session, _setupSessions);
    }

    // Added libraries are rows: name, area, folder path and Pending (nothing is scanned in setup), with rename, change folder and remove. Nothing in the vault shows here.
    [Fact]
    public void ALibraryIsARowWithItsAreaFolderAndPendingStatusAndRenameChangeFolderAndRemove()
    {
        AddMoviesLibrary();
        _configuration.Libraries.Add(new() { Id = VaultId, Name = "Private movies", KindKey = "movies", Area = "watch", Kind = "catalogued", MediaTypes = ["Movies"], IsVault = true, Sources = [new() { Id = "v1", Path = "E:\\Vault" }] });
        var cut = RenderStage(hasLibraries: true);

        Assert.Equal(["Movies"], Names(cut));
        var row = cut.Find("tr[data-library]");
        Assert.Contains("Watch", Text(row), StringComparison.Ordinal);
        Assert.Contains("1 folder", Text(row), StringComparison.Ordinal);
        Assert.Contains("Pending", Text(row), StringComparison.Ordinal);
        Assert.NotNull(row.QuerySelector(".lib-kind-tile svg, .lib-kind-tile .tl-icon-root"));
        Assert.NotEmpty(cut.FindAll("button[aria-label='Edit Movies']"));
        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private movies", cut.Markup, StringComparison.Ordinal);
    }

    // The setup table has no area chips above it, as in the design (the Area column already names each area).
    [Fact]
    public void TheSetupTableHasNoAreaChipsAboveIt()
    {
        AddMoviesLibrary();
        var cut = RenderStage(hasLibraries: true);

        Assert.Empty(cut.FindAll("[data-lib-chips], .lib-chips"));
        Assert.Single(cut.FindAll("tr[data-library]"));
    }

    // Skeleton rows stand in while the folders load (states table); the table replaces them once they arrive.
    [Fact]
    public async Task WhileTheLibrariesLoadTheStageDrawsSkeletonRowsAndNoTable()
    {
        var release = _loadGate = new TaskCompletionSource<LibrariesConfigurationDto?>();

        var cut = Render<SetupLibrariesStage>(parameters => parameters.Add(x => x.SetupSession, Session));

        var skeleton = cut.Find("[data-testid='setup-libraries-skeleton']");
        Assert.Equal(4, skeleton.QuerySelectorAll(".app-skeleton").Length);
        Assert.Empty(cut.FindAll("[data-setup-libraries-table]"));
        Assert.Empty(cut.FindAll("[role=alert]"));

        await cut.InvokeAsync(() => release.SetResult(_configuration));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-setup-libraries-table]")));
        Assert.Empty(cut.FindAll("[data-testid='setup-libraries-skeleton']"));
    }

    // Nothing a person clicks in the table changes the browser address (first-run setup D14).
    [Fact]
    public void TheTableHasNoLinksAndNoRailSoNothingInItLeavesTheWizard()
    {
        AddMoviesLibrary();
        var cut = RenderStage(hasLibraries: true);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var before = navigation.Uri;

        Assert.Empty(cut.FindAll(".librail, [aria-label='Library areas'], .setup-libs a[href]"));

        Assert.Equal(before, navigation.Uri);
    }

    // 3. Add library opens the four-step wizard on the area question; the name sits in the folder picker's footer; the library is saved through the setup session.
    [Fact]
    public void AddLibraryOpensTheWizardOnTheAreaQuestionAndTheNewLibraryBecomesARow()
    {
        var cut = RenderStage();

        cut.FindAll("button").Single(x => Text(x) == "Add library").Click();

        _dialogs.WaitForElement(".create-library[data-step='1']");
        Assert.Equal(["Watch", "Listen", "Read", "View"], _dialogs.Find("[data-area-tiles]").QuerySelectorAll(".lib-tile__name").Select(Text).ToArray());
        Assert.DoesNotContain("Vault", _dialogs.Markup, StringComparison.OrdinalIgnoreCase);
        DialogButton("Next").Click();
        _dialogs.WaitForElement(".create-library[data-step='2']");
        Assert.Contains("Movies", Text(_dialogs.Find("[data-kind-tiles]")), StringComparison.Ordinal);
        DialogButton("Next").Click();
        _dialogs.WaitForElement(".create-library[data-step='3'] .server-folder-picker");
        Assert.NotNull(_dialogs.Find(".server-folder-picker input"));
        _dialogs.WaitForAssertion(() => Assert.False(DialogButton("Add library").HasAttribute("disabled")));

        DialogButton("Add library").Click();

        _dialogs.WaitForAssertion(() => Assert.Single(_setupSaves));
        Assert.Contains(_setupSaves[0].Libraries, library => library.KindKey == "movies" && library.Area == "watch");
        cut.WaitForAssertion(() => Assert.Equal(["Movies"], Names(cut)));
        cut.WaitForAssertion(() => Assert.Equal(1, _created));
    }

    // Edit opens the same window as Settings. A library is never renamed (Libraries v3 D17): it shows its folders and their handling, and nothing is saved until a change is made.
    [Fact]
    public void EditOpensTheSameWindowAsSettingsWithNoLibraryNameFieldAndSavesNothingUntilSomethingChanges()
    {
        AddMoviesLibrary();
        var cut = RenderStage(hasLibraries: true);

        cut.Find("button[aria-label='Edit Movies']").Click();
        _dialogs.WaitForElement(".lib-edit");

        Assert.Empty(_dialogs.FindAll(".lib-edit__name"));
        Assert.Single(_dialogs.FindAll("[data-folder-rows] .lib-edit__folder"));
        Assert.Contains("Read-only", Text(_dialogs.Find(".lib-edit__tag")), StringComparison.Ordinal);
        Assert.Empty(_setupSaves);
        Assert.Equal(["Movies"], Names(cut));
    }

    // Setup scans nothing and moves nothing, so the Edit window leaves out Scan now and Move to Vault there.
    [Fact]
    public void TheEditWindowInSetupHasNoScanNow()
    {
        AddMoviesLibrary();
        var cut = RenderStage(hasLibraries: true);

        cut.Find("button[aria-label='Edit Movies']").Click();
        _dialogs.WaitForElement(".lib-edit");

        Assert.DoesNotContain(_dialogs.FindAll("button"), button => Text(button) == "Scan now");
        Assert.Contains(_dialogs.FindAll("button"), button => Text(button) == "Remove library");
    }

    // Remove library (inside Edit) asks once, then the library leaves the setup session's list.
    [Fact]
    public void RemoveAsksOnceAndThenTheLibraryLeavesTheListAndTheEmptyStateReturns()
    {
        AddMoviesLibrary();
        var cut = RenderStage(hasLibraries: true);

        cut.Find("button[aria-label='Edit Movies']").Click();
        _dialogs.WaitForElement(".lib-edit");
        DialogButton("Remove library").Click();
        _dialogs.WaitForAssertion(() => Assert.Contains("Remove Movies?", _dialogs.Markup, StringComparison.Ordinal));
        Assert.Empty(_setupSaves);
        DialogButton("Remove").Click();

        _dialogs.WaitForAssertion(() => Assert.Single(_setupSaves));
        Assert.Empty(_setupSaves[0].Libraries);
        cut.WaitForAssertion(() => Assert.Contains("No libraries yet", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void KeepingALibraryInTheRemoveQuestionChangesNothing()
    {
        AddMoviesLibrary();
        var cut = RenderStage(hasLibraries: true);

        cut.Find("button[aria-label='Edit Movies']").Click();
        _dialogs.WaitForElement(".lib-edit");
        DialogButton("Remove library").Click();
        _dialogs.WaitForAssertion(() => Assert.Contains("Remove Movies?", _dialogs.Markup, StringComparison.Ordinal));
        DialogButton("Keep").Click();

        Assert.Empty(_setupSaves);
        Assert.Equal(["Movies"], Names(cut));
    }

    // 4. The footer.
    [Fact]
    public void WithNoLibraryTheFooterOffersSkipForNowAndSkipDefersTheStep()
    {
        var cut = RenderStage();

        Assert.DoesNotContain(cut.FindAll("button"), button => Text(button) == "Continue");
        Button(cut, "Skip for now").Click();

        Assert.Equal(1, _deferred);
        Assert.Equal(0, _validated);
    }

    [Fact]
    public void WithALibraryTheFooterOffersContinueAndContinueValidatesTheStep()
    {
        AddMoviesLibrary();
        var cut = RenderStage(hasLibraries: true);

        Assert.DoesNotContain(cut.FindAll("button"), button => Text(button) == "Skip for now");
        Button(cut, "Continue").Click();

        Assert.Equal(1, _validated);
        Assert.Equal(0, _deferred);
    }

    // 5. A phone reads the cards but changes nothing.
    [Fact]
    public async Task OnAPhoneTheCardsAreReadOnlyWithTheUsualNote()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync("mobile");
        AddMoviesLibrary();
        var cut = RenderStage(hasLibraries: true);

        Assert.Contains("Use a desktop or tablet to change folders.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("button[aria-label^='Rename '], button[aria-label^='Remove ']"));
        Assert.DoesNotContain(cut.FindAll("button"), button => Text(button) == "Add library");
        Assert.Equal(["Movies"], Names(cut));
    }

    // 6. Errors.
    [Fact]
    public void WhenTheFoldersCannotBeLoadedTheStageSaysSoAndOffersTryAgain()
    {
        _failLoads = true;
        var cut = RenderStage();

        Assert.Contains("Your folders could not be loaded.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-setup-libraries-table]"));
        _failLoads = false;
        Button(cut, "Try again").Click();

        cut.WaitForElement("[data-setup-libraries-table]");
        cut.WaitForAssertion(() => Assert.Equal(1, _created));
    }

    [Fact]
    public void WhenTheKindsCannotBeLoadedTheStageSaysSo()
    {
        _failKinds = true;
        var cut = RenderStage();

        Assert.Contains("The kinds of library could not be loaded.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-setup-libraries-table]"));
    }

    // 7. The step is built from the one libraries-table component alone; the old table, wizard and tick cards are gone.
    [Fact]
    public void TheStageSourceHoldsNoTableNoTickCardsNoWizardAndNoSecondFoldersPanel()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components", "Setup", "SetupLibrariesStage.razor");
        var source = File.ReadAllText(path);

        Assert.Contains("<SetupLibrariesTable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<LibrariesOverview", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddFolderWizard", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryFoldersPanel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("setup-have__card", source, StringComparison.Ordinal);
    }

    // Setup has no copy of the dialog or the pop-up: Add goes through LibraryAddFlow (the one Add library pop-up) and the row changes through LibraryFolderAddService,
    // both Settings' own. LibrariesOverview no longer has a setup mode.
    [Fact]
    public void SetupDrawsOnlySettingsOwnDialogAndFolderPopUpWithNoCopyOfAnyAndLibrariesOverviewHasNoSetupMode()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components");
        var setupSources = Directory.GetFiles(Path.Combine(root, "Setup"), "*.razor", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray();
        var cards = File.ReadAllText(Path.Combine(root, "Setup", "SetupLibrariesTable.razor"));
        var overview = File.ReadAllText(Path.Combine(root, "Settings", "Libraries", "LibrariesOverview.razor"));

        Assert.Contains("LibraryAddFlow.OpenAsync", cards, StringComparison.Ordinal);
        // Every row, View kinds included, has one Edit button; the old three-icon cell (rename, change folder, remove) is gone (View folders spec D7).
        Assert.Contains("EditAsync(row)", cards, StringComparison.Ordinal);
        Assert.DoesNotContain("RenameSetup", cards, StringComparison.Ordinal);
        Assert.DoesNotContain("AppIconButton", cards, StringComparison.Ordinal);
        Assert.Contains("AppDialogParameters<CreateLibraryDialog>", File.ReadAllText(Path.Combine(root, "Settings", "Libraries", "LibraryAddFlow.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("Simple", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("SetupSession", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("OnlyArea", overview, StringComparison.Ordinal);
        Assert.All(setupSources, source =>
        {
            Assert.DoesNotContain("<CreateLibraryDialog", source, StringComparison.Ordinal);
            Assert.DoesNotContain("<ServerFolderPicker", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AppDialogParameters<ServerFolderPicker>", source, StringComparison.Ordinal);
            Assert.DoesNotContain("<AppSegmentedControl", source, StringComparison.Ordinal);
            Assert.DoesNotContain("<LibrariesRail", source, StringComparison.Ordinal);
            Assert.DoesNotContain("<LibrariesOverview", source, StringComparison.Ordinal);
        });
    }

    // The folder picker opens at one shared size, with the name in its footer, for every caller (local check 2026-11-01, item 7).
    [Fact]
    public void EveryFolderPickerCallerUsesTheOneSharedDialogSizeAndTheFolderWindowCarriesTheFriendlyNameAndHandling()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components");
        foreach (var file in Directory.GetFiles(root, "*.razor", SearchOption.AllDirectories).Where(file => File.ReadAllText(file).Contains("ShowAsync<ServerFolderPicker>", StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(file);
            Assert.Contains("ServerFolderPicker.DialogOptions", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AppMaxWidth.ExtraExtraLarge", source, StringComparison.Ordinal);
        }

        var create = File.ReadAllText(Path.Combine(root, "Settings", "Libraries", "CreateLibraryDialog.razor"));
        Assert.Contains("ShowFolderName=\"LibraryFolderStep.ShowFolderName\"", create, StringComparison.Ordinal);
        Assert.Contains("OfferHandling=", create, StringComparison.Ordinal);

        // Add library's folder step and Edit's Add folder read their heading, lead, chips and friendly-name choice from the one LibraryFolderStep, so they cannot drift.
        var edit = File.ReadAllText(Path.Combine(root, "Settings", "Libraries", "LibraryEditDialog.razor"));
        foreach (var source in new[] { create, edit })
        {
            Assert.Contains("LibraryFolderStep.Heading(", source, StringComparison.Ordinal);
            Assert.Contains("LibraryFolderStep.Lead(", source, StringComparison.Ordinal);
            Assert.Contains("LibraryFolderStep.Chips(", source, StringComparison.Ordinal);
            Assert.Contains("LibraryFolderStep.ShowFolderName", source, StringComparison.Ordinal);
        }
        Assert.Contains("DialogOptions => ServerFolderPicker.DialogOptions", create, StringComparison.Ordinal);
    }

    // Add-folder v2 (A4 for View and the Vault): setup's View folders go through the one Add library pop-up and the View folder dialog, the Vault step draws the same area card,
    // and no other setup screen knows the word Vault (the step list draws its optional entry only while it is offered; the page itself only routes to it), so a skipped Vault step leaves nothing behind.
    [Fact]
    public void SetupsViewFoldersAndVaultStepReuseSettingsComponentsAndTheVaultStaysInOneStep()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components");
        var viewDialog = File.ReadAllText(Path.Combine(root, "Settings", "Libraries", "AddViewFolderDialog.razor"));
        var createDialog = File.ReadAllText(Path.Combine(root, "Settings", "Libraries", "CreateLibraryDialog.razor"));
        var vaultStep = File.ReadAllText(Path.Combine(root, "Setup", "SetupVaultOffer.razor"));
        var eligibility = File.ReadAllText(Path.Combine(root, "Setup", "SetupVaultEligibility.cs"));

        Assert.Contains("AppDialogParameters<AddViewFolderDialog>", createDialog, StringComparison.Ordinal);
        Assert.Contains("<ServerFolderPicker", viewDialog, StringComparison.Ordinal);
        Assert.Contains("EffectiveAdministrator != true", eligibility, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppTable", vaultStep, StringComparison.Ordinal);
        Assert.DoesNotContain("<CreateLibraryDialog", vaultStep, StringComparison.Ordinal);
        Assert.DoesNotContain("<LibrariesOverview", vaultStep, StringComparison.Ordinal);

        var others = Directory.GetFiles(Path.Combine(root, "Setup"), "*.razor", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) is not ("SetupVaultOffer.razor" or "SetupStepper.razor" or "SetupLibrariesStage.razor" or "SetupLibrariesTable.razor"));
        Assert.All(others, file => Assert.DoesNotContain("Vault", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase));
    }

    // First-run setup D14: nothing a person clicks inside a step changes the browser address. Only All set may link out (to Settings, and to sign in).
    [Fact]
    public void NoSetupStepChangesTheBrowserAddressExceptAllSetsTwoLinks()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components");
        var offenders = Directory.GetFiles(Path.Combine(root, "Setup"), "*.razor", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) is not ("SetupReadinessStage.razor" or "SetupIntroStage.razor"))
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return source.Contains("NavigateTo(", StringComparison.Ordinal)
                    || source.Contains("href=", StringComparison.OrdinalIgnoreCase)
                    || source.Contains("Href=", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);

    }

    private sealed class IdleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler()) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class SignedOutProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
