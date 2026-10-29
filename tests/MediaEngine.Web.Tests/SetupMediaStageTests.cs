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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// First-run setup's "What do you have?" step (Library pages WP8, D13): the same Libraries table Settings shows, in simple mode, read
// through setup's own session, with the unchanged footer (Skip for now, Continue), step key and deferral.
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

    private IRenderedComponent<SetupMediaStage> RenderStage(bool hasLibraries = false)
    {
        var cut = Render<SetupMediaStage>(parameters => parameters
            .Add(x => x.SetupSession, Session)
            .Add(x => x.HasLibraries, hasLibraries)
            .Add(x => x.DeferMedia, EventCallback.Factory.Create(this, () => _deferred++))
            .Add(x => x.ValidateMedia, EventCallback.Factory.Create(this, () => _validated++))
            .Add(x => x.LibraryCreated, EventCallback.Factory.Create(this, () => _created++)));
        cut.WaitForAssertion(() => Assert.True(cut.FindAll(".libs, .alert, [role=alert]").Count > 0));
        return cut;
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<SetupMediaStage> cut, string text) => cut.FindAll("button").Single(x => Text(x) == text);

    // 1. The step draws the shared Libraries table, in simple mode.
    [Fact]
    public void TheStageAsksWhatDoYouHaveAndDrawsTheSameLibrariesTableAsSettings()
    {
        AddMoviesLibrary();
        var cut = RenderStage(hasLibraries: true);

        Assert.Equal("What do you have?", Text(cut.Find("h2")));
        Assert.Contains("You can add more later in Settings.", cut.Markup, StringComparison.Ordinal);
        Assert.Single(cut.FindComponents<LibrariesOverview>());
        Assert.True(cut.FindComponent<LibrariesOverview>().Instance.Simple);
        Assert.Equal(Session, cut.FindComponent<LibrariesOverview>().Instance.SetupSession);
        Assert.Equal(["Movies"], cut.FindAll(".libs__link").Select(Text).ToArray());
        Assert.Contains(Session, _setupSessions);
    }

    // 2. Simple mode: Watch, Listen and Read; no Access, no Vault, rows that open nothing.
    [Fact]
    public void SimpleModeHasNoViewAreaNoAccessColumnNoVaultAndRowsThatOpenNothing()
    {
        AddMoviesLibrary();
        _configuration.Libraries.Add(new() { Id = VaultId, Name = "Private movies", KindKey = "movies", Area = "watch", Kind = "catalogued", MediaTypes = ["Movies"], IsVault = true, Sources = [new() { Id = "v1", Path = "E:\\Vault" }] });
        var cut = RenderStage(hasLibraries: true);

        Assert.Equal(["Watch", "Listen", "Read"], cut.FindAll("[aria-label='Library areas'] button").Select(Text).ToArray());
        Assert.DoesNotContain(cut.FindAll("th"), header => Text(header) == "Access");
        Assert.Empty(cut.FindAll("a.libs__link"));
        Assert.Empty(cut.FindAll(".libs__chevron"));
        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private movies", cut.Markup, StringComparison.Ordinal);

        var navigation = Services.GetRequiredService<NavigationManager>();
        var before = navigation.Uri;
        cut.Find($"tr[data-library='{MoviesId}']").Click();
        Assert.Equal(before, navigation.Uri);
    }

    // Choosing Watch, Listen or Read in setup changes the table in place; leaving the wizard would abandon the first run.
    [Fact]
    public void ChoosingAnotherAreaInSetupStaysInTheWizard()
    {
        var cut = RenderStage();
        var navigation = Services.GetRequiredService<NavigationManager>();
        var before = navigation.Uri;

        cut.FindAll("[aria-label='Library areas'] button").Single(tab => Text(tab) == "Listen").Click();

        Assert.Equal(before, navigation.Uri);
        Assert.Equal("true", cut.FindAll("[aria-label='Library areas'] button").Single(tab => Text(tab) == "Listen").GetAttribute("aria-pressed"));
    }

    [Fact]
    public void SimpleModeSaysPhotosAndTheImportFolderComeAfterSetup()
    {
        var cut = RenderStage();

        Assert.Contains("added after setup, in Settings", Text(cut.Find("[data-later]")), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("a[href='/settings/libraries/import']"));
    }

    // 3. Add library opens the one Create library dialog.
    [Fact]
    public void AddLibraryOpensTheCreateLibraryDialogInSimpleMode()
    {
        var cut = RenderStage();

        Button(cut, "Add library").Click();

        _dialogs.WaitForElement(".create-library");
        Assert.DoesNotContain("Vault-protected", _dialogs.Markup, StringComparison.Ordinal);
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

    // 5. A phone reads the table but changes nothing.
    [Fact]
    public async Task OnAPhoneTheTableIsReadOnlyWithTheUsualNote()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync("mobile");
        AddMoviesLibrary();
        var cut = RenderStage(hasLibraries: true);

        Assert.Contains("Use a desktop or tablet to change folders.", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(cut.FindAll("button"), button => Text(button) == "Add library");
    }

    // 6. Errors.
    [Fact]
    public void WhenTheFoldersCannotBeLoadedTheStageSaysSoAndOffersTryAgain()
    {
        _failLoads = true;
        var cut = RenderStage();

        Assert.Contains("Your folders could not be loaded.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".libs"));
        _failLoads = false;
        Button(cut, "Try again").Click();

        cut.WaitForElement(".libs");
        cut.WaitForAssertion(() => Assert.Equal(1, _created));
    }

    [Fact]
    public void WhenTheKindsCannotBeLoadedTheStageSaysSo()
    {
        _failKinds = true;
        var cut = RenderStage();

        Assert.Contains("The kinds of library could not be loaded.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".libs"));
    }

    // 7. The old card UI and wizard are gone: the stage is built from the shared table alone.
    [Fact]
    public void TheStageSourceHoldsNoTickCardsNoWizardAndNoSecondFoldersPanel()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components", "Setup", "SetupMediaStage.razor");
        var source = File.ReadAllText(path);

        Assert.Contains("<LibrariesOverview", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddFolderWizard", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryFoldersPanel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("setup-have__card", source, StringComparison.Ordinal);
    }

    // A4: setup has no copy of the table, the dialog or the pop-up. It draws Settings' own, and the only things Simple changes are what a first-time install
    // must not show (no Vault, no Access choice) and the Continue button below the table.
    [Fact]
    public void SetupDrawsOnlySettingsOwnTableDialogAndFolderPopUpWithNoCopyOfAny()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components");
        var setupSources = Directory.GetFiles(Path.Combine(root, "Setup"), "*.razor", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray();
        var stage = File.ReadAllText(Path.Combine(root, "Setup", "SetupMediaStage.razor"));
        var overview = File.ReadAllText(Path.Combine(root, "Settings", "Libraries", "LibrariesOverview.razor"));

        Assert.Contains("<LibrariesOverview", stage, StringComparison.Ordinal);
        Assert.Contains("AppDialogParameters<CreateLibraryDialog>", overview, StringComparison.Ordinal);
        Assert.All(setupSources, source =>
        {
            Assert.DoesNotContain("<CreateLibraryDialog", source, StringComparison.Ordinal);
            Assert.DoesNotContain("<ServerFolderPicker", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AppDialogParameters<ServerFolderPicker>", source, StringComparison.Ordinal);
            Assert.DoesNotContain("<AppSegmentedControl", source, StringComparison.Ordinal);
            Assert.DoesNotContain("<AppTable", source, StringComparison.Ordinal);
        });
    }
}
