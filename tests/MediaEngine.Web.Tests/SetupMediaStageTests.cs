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

// First-run setup's "What do you have?" step (settings and setup simplified D8): four area cards (Watch, Listen, Read, View), each with its own Add,
// libraries as rows under their area, every change saved through setup's own session, with the footer (Skip for now, Continue), step key and deferral.
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
        cut.WaitForAssertion(() => Assert.True(cut.FindAll("[data-setup-areas], .alert, [role=alert]").Count > 0));
        return cut;
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<SetupLibrariesStage> cut, string text) => cut.FindAll("button").Single(x => Text(x) == text);

    private AngleSharp.Dom.IElement Card(IRenderedComponent<SetupLibrariesStage> cut, string area) => cut.Find($".setup-area[data-area={area}]");

    private AngleSharp.Dom.IElement DialogButton(string text) => _dialogs.FindAll("button").Single(x => Text(x) == text);

    // 1. Four area cards are always drawn, each with its icon, one line on what fits, its own Add and "Nothing yet" while empty.
    [Fact]
    public void TheStageAsksWhatDoYouHaveAndAlwaysDrawsTheFourAreaCardsEachSayingNothingYet()
    {
        var cut = RenderStage();

        Assert.Equal("What do you have?", Text(cut.Find("h2")));
        Assert.Single(cut.FindComponents<SetupAreaCards>());
        Assert.Equal(Session, cut.FindComponent<SetupAreaCards>().Instance.SetupSession);
        Assert.Equal(["Watch", "Listen", "Read", "View"], cut.FindAll(".setup-area").Select(card => card.GetAttribute("aria-label")!).ToArray());
        Assert.All(cut.FindAll(".setup-area"), card => Assert.NotNull(card.QuerySelector(".setup-area__head svg")));
        Assert.Equal(["Nothing yet", "Nothing yet", "Nothing yet", "Nothing yet"], cut.FindAll(".setup-area__empty").Select(Text).ToArray());
        Assert.Contains("Movies, TV shows, home videos", Text(Card(cut, "watch").QuerySelector(".setup-area__copy")!), StringComparison.Ordinal);
        Assert.Contains("Music, audiobooks, podcasts", Text(Card(cut, "listen").QuerySelector(".setup-area__copy")!), StringComparison.Ordinal);
        Assert.Contains("Books, comics, documents", Text(Card(cut, "read").QuerySelector(".setup-area__copy")!), StringComparison.Ordinal);
        Assert.Contains("Photos and personal videos", Text(Card(cut, "view").QuerySelector(".setup-area__copy")!), StringComparison.Ordinal);
        foreach (var area in new[] { "Watch", "Listen", "Read", "View" })
        {
            Assert.Single(cut.FindAll($"button[aria-label='Add a {area} library']"));
        }

        // No search, filter, layout switch or per-row Add folder.
        Assert.Empty(cut.FindAll("input[type=search], select, [aria-label='Libraries layout'], [aria-label='Search libraries']"));
        Assert.DoesNotContain(cut.FindAll("button"), button => Text(button) is "Add library" or "Add folder");
        Assert.Contains(Session, _setupSessions);
    }

    // Added libraries are rows under their area: icon, name, folder path, rename, change folder, remove. Nothing in the vault shows here.
    [Fact]
    public void ALibraryIsARowUnderItsAreaWithRenameChangeFolderAndRemoveAndOnlyThatAreaLosesNothingYet()
    {
        AddMoviesLibrary();
        _configuration.Libraries.Add(new() { Id = VaultId, Name = "Private movies", KindKey = "movies", Area = "watch", Kind = "catalogued", MediaTypes = ["Movies"], IsVault = true, Sources = [new() { Id = "v1", Path = "E:\\Vault" }] });
        var cut = RenderStage(hasLibraries: true);

        var watch = Card(cut, "watch");
        Assert.Equal(["Movies"], watch.QuerySelectorAll(".setup-area__name").Select(Text).ToArray());
        Assert.Equal("E:\\Library\\Movies", Text(watch.QuerySelector(".setup-area__path")!));
        Assert.NotNull(watch.QuerySelector(".setup-area__row .lib-kind-tile svg, .setup-area__row .lib-kind-tile .tl-icon-root"));
        Assert.Empty(watch.QuerySelectorAll(".setup-area__empty"));
        Assert.Equal(["Nothing yet", "Nothing yet", "Nothing yet"], cut.FindAll(".setup-area__empty").Select(Text).ToArray());
        Assert.NotEmpty(watch.QuerySelectorAll("button[aria-label='Rename Movies']"));
        Assert.NotEmpty(watch.QuerySelectorAll("button[aria-label='Change the folder of Movies']"));
        Assert.NotEmpty(watch.QuerySelectorAll("button[aria-label='Remove Movies']"));
        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private movies", cut.Markup, StringComparison.Ordinal);
    }

    // Nothing a person clicks in the cards changes the browser address (first-run setup D14).
    [Fact]
    public void TheCardsHaveNoLinksAndNoRailSoNothingInThemLeavesTheWizard()
    {
        AddMoviesLibrary();
        var cut = RenderStage(hasLibraries: true);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var before = navigation.Uri;

        Assert.Empty(cut.FindAll(".librail, [aria-label='Library areas'], .setup-areas a[href]"));
        cut.Find(".setup-area__row").Click();

        Assert.Equal(before, navigation.Uri);
    }

    // 3. Add opens the one Add library pop-up already on that area; the name sits in the folder picker's footer; the library is saved through the setup session.
    [Fact]
    public void AddOnTheWatchCardOpensTheAddLibraryPopUpOnWatchAndTheNewLibraryIsARowUnderWatch()
    {
        var cut = RenderStage();

        cut.Find("button[aria-label='Add a Watch library']").Click();

        _dialogs.WaitForElement(".create-library");
        Assert.Equal("What is it?", Text(_dialogs.Find(".app-dialog-shell__title")));
        Assert.Contains("Movies", Text(_dialogs.Find("[aria-label='Content type']")), StringComparison.Ordinal);
        Assert.DoesNotContain("Vault", _dialogs.Markup, StringComparison.OrdinalIgnoreCase);
        DialogButton("Next").Click();
        _dialogs.WaitForElement(".create-library[data-step='2'] .server-folder-picker");
        Assert.NotNull(_dialogs.Find(".server-folder-picker__footer-start [data-library-name] input"));
        _dialogs.WaitForAssertion(() => Assert.False(DialogButton("Add library").HasAttribute("disabled")));

        DialogButton("Add library").Click();

        _dialogs.WaitForAssertion(() => Assert.Single(_setupSaves));
        Assert.Contains(_setupSaves[0].Libraries, library => library.KindKey == "movies" && library.Area == "watch");
        cut.WaitForAssertion(() => Assert.Equal(["Movies"], Card(cut, "watch").QuerySelectorAll(".setup-area__name").Select(Text).ToArray()));
        Assert.Empty(Card(cut, "listen").QuerySelectorAll(".setup-area__name"));
        cut.WaitForAssertion(() => Assert.Equal(1, _created));
    }

    // Rename opens the small name dialog and saves the whole list through the setup session.
    [Fact]
    public void RenameOpensTheSmallNameDialogAndSavesTheNewNameThroughSetup()
    {
        AddMoviesLibrary();
        var cut = RenderStage(hasLibraries: true);

        cut.Find("button[aria-label='Rename Movies']").Click();
        _dialogs.WaitForElement("input");
        _dialogs.Find("input").Input("Family films");
        DialogButton("Apply").Click();

        _dialogs.WaitForAssertion(() => Assert.Single(_setupSaves));
        Assert.Equal("Family films", Assert.Single(_setupSaves[0].Libraries).Name);
        cut.WaitForAssertion(() => Assert.Equal(["Family films"], Card(cut, "watch").QuerySelectorAll(".setup-area__name").Select(Text).ToArray()));
        Assert.Equal(1, _created);
    }

    // Change folder opens the shared folder picker and swaps the library's folder.
    [Fact]
    public void ChangeFolderOpensTheFolderPickerAndSavesTheChosenFolderThroughSetup()
    {
        AddMoviesLibrary();
        var cut = RenderStage(hasLibraries: true);

        cut.Find("button[aria-label='Change the folder of Movies']").Click();
        _dialogs.WaitForAssertion(() => Assert.Contains("Choose your Movies folder", _dialogs.Markup, StringComparison.Ordinal));
        _dialogs.WaitForAssertion(() => Assert.False(DialogButton("Choose folder").HasAttribute("disabled")));
        DialogButton("Choose folder").Click();

        _dialogs.WaitForAssertion(() => Assert.Single(_setupSaves));
        var saved = Assert.Single(_setupSaves[0].Libraries);
        Assert.Equal(MoviesId, saved.Id);
        Assert.Equal(["C:\\media"], saved.Sources.Select(source => source.Path).ToArray());
        cut.WaitForAssertion(() => Assert.Equal("C:\\media", Text(Card(cut, "watch").QuerySelector(".setup-area__path")!)));
    }

    // Remove asks once, then the library leaves the setup session's list.
    [Fact]
    public void RemoveAsksOnceAndThenTheLibraryLeavesTheListAndTheAreaSaysNothingYet()
    {
        AddMoviesLibrary();
        var cut = RenderStage(hasLibraries: true);

        cut.Find("button[aria-label='Remove Movies']").Click();
        _dialogs.WaitForAssertion(() => Assert.Contains("Remove Movies?", _dialogs.Markup, StringComparison.Ordinal));
        Assert.Empty(_setupSaves);
        DialogButton("Remove").Click();

        _dialogs.WaitForAssertion(() => Assert.Single(_setupSaves));
        Assert.Empty(_setupSaves[0].Libraries);
        cut.WaitForAssertion(() => Assert.Equal("Nothing yet", Text(Card(cut, "watch").QuerySelector(".setup-area__empty")!)));
    }

    [Fact]
    public void KeepingALibraryInTheRemoveQuestionChangesNothing()
    {
        AddMoviesLibrary();
        var cut = RenderStage(hasLibraries: true);

        cut.Find("button[aria-label='Remove Movies']").Click();
        _dialogs.WaitForAssertion(() => Assert.Contains("Remove Movies?", _dialogs.Markup, StringComparison.Ordinal));
        DialogButton("Keep").Click();

        Assert.Empty(_setupSaves);
        Assert.Equal(["Movies"], Card(cut, "watch").QuerySelectorAll(".setup-area__name").Select(Text).ToArray());
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
        Assert.Empty(cut.FindAll("button[aria-label^='Add a '], button[aria-label^='Rename '], button[aria-label^='Remove ']"));
        Assert.Equal(["Movies"], Card(cut, "watch").QuerySelectorAll(".setup-area__name").Select(Text).ToArray());
    }

    // 6. Errors.
    [Fact]
    public void WhenTheFoldersCannotBeLoadedTheStageSaysSoAndOffersTryAgain()
    {
        _failLoads = true;
        var cut = RenderStage();

        Assert.Contains("Your folders could not be loaded.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".setup-area"));
        _failLoads = false;
        Button(cut, "Try again").Click();

        cut.WaitForElement(".setup-area");
        cut.WaitForAssertion(() => Assert.Equal(1, _created));
    }

    [Fact]
    public void WhenTheKindsCannotBeLoadedTheStageSaysSo()
    {
        _failKinds = true;
        var cut = RenderStage();

        Assert.Contains("The kinds of library could not be loaded.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".setup-area"));
    }

    // 7. The step is built from the one area-cards component alone; the old table, wizard and tick cards are gone.
    [Fact]
    public void TheStageSourceHoldsNoTableNoTickCardsNoWizardAndNoSecondFoldersPanel()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components", "Setup", "SetupLibrariesStage.razor");
        var source = File.ReadAllText(path);

        Assert.Contains("<SetupAreaCards", source, StringComparison.Ordinal);
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
        var cards = File.ReadAllText(Path.Combine(root, "Setup", "SetupAreaCards.razor"));
        var overview = File.ReadAllText(Path.Combine(root, "Settings", "Libraries", "LibrariesOverview.razor"));

        Assert.Contains("LibraryAddFlow.OpenAsync", cards, StringComparison.Ordinal);
        Assert.Contains("Editor.RenameSetupLibraryAsync", cards, StringComparison.Ordinal);
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
            Assert.DoesNotContain("<AppTable", source, StringComparison.Ordinal);
            Assert.DoesNotContain("<LibrariesRail", source, StringComparison.Ordinal);
            Assert.DoesNotContain("<LibrariesOverview", source, StringComparison.Ordinal);
        });
    }

    // The folder picker opens at one shared size, with the name in its footer, for every caller (local check 2026-11-01, item 7).
    [Fact]
    public void EveryFolderPickerCallerUsesTheOneSharedDialogSizeAndAddLibraryPutsTheNameInThePickersFooter()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components");
        foreach (var file in Directory.GetFiles(root, "*.razor", SearchOption.AllDirectories).Where(file => File.ReadAllText(file).Contains("ShowAsync<ServerFolderPicker>", StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(file);
            Assert.Contains("ServerFolderPicker.DialogOptions", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AppMaxWidth.ExtraExtraLarge", source, StringComparison.Ordinal);
        }

        var create = File.ReadAllText(Path.Combine(root, "Settings", "Libraries", "CreateLibraryDialog.razor"));
        Assert.Contains("<FooterStart>", create, StringComparison.Ordinal);
        Assert.Contains("MaxWidth = ServerFolderPicker.DialogOptions.MaxWidth", create, StringComparison.Ordinal);
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
        Assert.Contains("AppDialogParameters<ServerFolderPicker>", viewDialog, StringComparison.Ordinal);
        Assert.Contains("<SetupAreaCards VaultOnly=\"true\"", vaultStep, StringComparison.Ordinal);
        Assert.Contains("EffectiveAdministrator != true", eligibility, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppTable", vaultStep, StringComparison.Ordinal);
        Assert.DoesNotContain("<CreateLibraryDialog", vaultStep, StringComparison.Ordinal);
        Assert.DoesNotContain("<LibrariesOverview", vaultStep, StringComparison.Ordinal);

        var others = Directory.GetFiles(Path.Combine(root, "Setup"), "*.razor", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) is not ("SetupVaultOffer.razor" or "SetupStepper.razor" or "SetupAreaCards.razor"));
        Assert.All(others, file => Assert.DoesNotContain("Vault", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase));
    }

    // First-run setup D14: nothing a person clicks inside a step changes the browser address. Only All set may link out (to Settings, and to sign in).
    [Fact]
    public void NoSetupStepChangesTheBrowserAddressExceptAllSetsTwoLinks()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components");
        var offenders = Directory.GetFiles(Path.Combine(root, "Setup"), "*.razor", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) != "SetupReadinessStage.razor")
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
