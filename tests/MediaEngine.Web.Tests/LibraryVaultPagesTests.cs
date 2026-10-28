using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// The Vault choices on the Libraries pages (Library pages WP9): Add as a Vault folder, the read-only page of a Vault-protected library, its
// Lookup switch and the Move to Vault doorway. The rule they all share: for anyone who is not an administrator, and for any profile that may not
// see the Vault (a Kids profile included), no page and no dialog holds the word Vault or any hint of it.
public sealed class LibraryVaultPagesTests : AsyncBunitContext
{
    private const string MoviesId = "11111111-1111-4111-8111-111111111111";
    private const string VaultMoviesId = "33333333-3333-4333-8333-333333333333";

    private readonly List<AddVaultFolderRequest> _vaultFolders = [];
    private readonly List<LibraryMutationRequest> _mutations = [];
    private readonly List<(string LibraryId, bool Enabled)> _lookupSwitches = [];
    private readonly List<string> _activityReads = [];
    private VaultMoveDestinationDto? _destination = new("movies", VaultMoveDestinationStates.None, null, null, 0);
    private bool _lookupSaves = true;
    private LibrariesConfigurationDto _configuration = BuildConfiguration();

    private static LibrariesConfigurationDto BuildConfiguration() => new()
    {
        StorageLocations = [new() { Id = "media", Label = "Media storage", Path = "C:\\media", AllowWrite = true }],
        Libraries =
        [
            new()
            {
                Id = MoviesId, Name = "Movies", KindKey = "movies", Area = "watch", Kind = "catalogued", MediaTypes = ["Movies"],
                Sources = [new() { Id = "movies-main", Path = "E:\\Library\\Movies", ManagementMode = "existing_library", AccessMode = "read_only" }],
            },
        ],
    };

    private static LibraryFolderDto VaultMovies(string policy = "local_only") => new()
    {
        Id = VaultMoviesId,
        Name = "Private Movies",
        KindKey = "movies",
        Area = "watch",
        Kind = "catalogued",
        MediaTypes = ["Movies"],
        Access = "vault",
        MetadataPolicy = policy,
        Sources = [new() { Id = "vault-movies-1", Path = "E:\\Private\\Movies", ManagementMode = "existing_library", AccessMode = "read_only" }],
    };

    public LibraryVaultPagesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler("get_LastError", _ => null);
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(_configuration));
            stub.SetHandler(nameof(IEngineApiClient.TestPathAsync), _ => Task.FromResult<PathTestResultDto?>(new() { Exists = true, HasRead = true, HasWrite = true }));
            stub.SetHandler(nameof(IEngineApiClient.GetImportFolderWaitingAsync), _ => Task.FromResult<MediaEngine.Contracts.Ingestion.ImportFolderWaitingResponse?>(null));
            stub.SetHandler(nameof(IEngineApiClient.GetFolderJobsAsync), _ => Task.FromResult<IReadOnlyList<FolderJobStatusDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetExcludedFilesAsync), _ => Task.FromResult<IReadOnlyList<ExcludedFileDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryFolderTitlesAsync), _ => Task.FromResult<LibraryFolderTitlesDto?>(new() { Counts = [] }));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryAccessAsync), _ => Task.FromResult<LibraryAccessGrantsDto?>(null));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryActivityAsync), args =>
            {
                _activityReads.Add((string)args![0]!);
                return Task.FromResult<LibraryActivityDto?>(null);
            });
            stub.SetHandler(nameof(IEngineApiClient.MutateLibraryAsync), args =>
            {
                _mutations.Add((LibraryMutationRequest)args![0]!);
                return Task.FromResult<LibrariesConfigurationDto?>(_configuration);
            });
            stub.SetHandler(nameof(IEngineApiClient.TriggerRescanAsync), _ => Task.FromResult(true));
            stub.SetHandler(nameof(IEngineApiClient.GetServerFolderRootsAsync), _ => Task.FromResult<IReadOnlyList<ServerStorageLocationDto>>(_configuration.StorageLocations));
            stub.SetHandler(nameof(IEngineApiClient.BrowseServerFoldersAsync), _ => Task.FromResult<BrowseServerFoldersResultDto?>(new() { DisplayPath = "C:\\media", Files = [] }));
            stub.SetHandler(nameof(IEngineApiClient.ValidateServerFolderAsync), _ => Task.FromResult<ServerFolderValidationResultDto?>(new()
            {
                Path = "G:\\More\\Movies",
                StorageLocationId = "media",
                Exists = true,
                HasRead = true,
                HasWrite = true,
                CanSelect = true,
            }));
            stub.SetHandler(nameof(IEngineApiClient.AddVaultFolderAsync), args =>
            {
                _vaultFolders.Add((AddVaultFolderRequest)args![0]!);
                return Task.FromResult<LibraryFolderDto?>(VaultMovies());
            });
            stub.SetHandler(nameof(IEngineApiClient.GetVaultMoveDestinationAsync), _ => Task.FromResult(_destination));
            stub.SetHandler(nameof(IEngineApiClient.SetVaultOnlineLookupAsync), args =>
            {
                var enabled = (bool)args![1]!;
                _lookupSwitches.Add(((string)args[0]!, enabled));
                return Task.FromResult<LibraryFolderDto?>(_lookupSaves ? VaultMovies(enabled ? "enriched" : "local_only") : null);
            });
        }));
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.LibraryKindsState>();
        Render<AppPopoverHost>();
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private void SignIn(bool administrator) =>
        Services.GetRequiredService<DashboardSessionAccessor>().Set("token", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new DashboardAuthorityResponse(Guid.NewGuid(), Guid.NewGuid(), true, true, 1, 1, administrator, administrator, null, 1, [], ["settings.administration"], ["household.manage"]));

    private IRenderedComponent<LibraryShell> OpenPage(LibraryFolderDto library, string? tab = null, bool vaultVisible = false)
    {
        if (_configuration.Libraries.All(item => item.Id != library.Id))
        {
            _configuration.Libraries.Add(library);
        }

        var cut = Render<LibraryShell>(parameters => parameters
            .Add(x => x.Library, library)
            .Add(x => x.Configuration, _configuration)
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog)
            .Add(x => x.Tab, tab)
            .Add(x => x.VaultVisible, vaultVisible));
        cut.WaitForElement(".libpage");
        return cut;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("access")]
    [InlineData("file-handling")]
    [InlineData("activity")]
    public void EveryTabOfAnOrdinaryLibraryHoldsNoVaultWordForSomeoneWhoIsNotAnAdministrator(string? tab)
    {
        SignIn(administrator: false);

        var cut = OpenPage(_configuration.Libraries[0], tab);
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading", cut.Markup, StringComparison.Ordinal));

        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheFoldersTabOfAnOrdinaryLibraryHoldsNoMoveToVaultEntryForSomeoneWhoIsNotAnAdministrator()
    {
        SignIn(administrator: false);

        var cut = OpenPage(_configuration.Libraries[0]);
        cut.WaitForElement(".folder-grid");

        Assert.DoesNotContain("Move to Vault", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddFolderOffersNoVaultSwitchToSomeoneWhoIsNotAnAdministrator()
    {
        SignIn(administrator: false);
        var dialogs = Render<AppDialogHost>();
        var cut = OpenPage(_configuration.Libraries[0]);

        await cut.ClickAsync(page => page.FindAll(".libpage__actions button").First(button => Text(button) == "Add folder"));

        dialogs.WaitForAssertion(() => Assert.Contains("Add folder", dialogs.Markup, StringComparison.Ordinal));
        dialogs.WaitForAssertion(() => Assert.Contains(dialogs.FindAll("button"), button => Text(button) == "Add"));
        Assert.DoesNotContain("Vault", dialogs.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddFolderOffersTheVaultSwitchToAnAdministratorAndAVaultFolderGoesToTheVaultNotToThisLibrary()
    {
        SignIn(administrator: true);
        var dialogs = Render<AppDialogHost>();
        var cut = OpenPage(_configuration.Libraries[0]);

        await cut.ClickAsync(page => page.FindAll(".libpage__actions button").First(button => Text(button) == "Add folder"));
        dialogs.WaitForAssertion(() => Assert.Contains("Add as a Vault folder", dialogs.Markup, StringComparison.Ordinal));
        dialogs.Find("[role=switch]").Change(true);
        dialogs.WaitForAssertion(() => Assert.Contains(dialogs.FindAll("button"), button => Text(button) == "Add" && !button.HasAttribute("disabled")));
        await dialogs.ClickAsync(host => host.FindAll("button").First(button => Text(button) == "Add" && !button.HasAttribute("disabled")));

        cut.WaitForAssertion(() => Assert.Single(_vaultFolders));
        Assert.Equal("movies", _vaultFolders[0].KindKey);
        Assert.Equal("G:\\More\\Movies", _vaultFolders[0].FolderPath);
        Assert.Empty(_mutations);
    }

    [Fact]
    public async Task TheMoveToVaultEntryIsInTheFolderMenuOfAnAdministratorOnAnOrdinaryLibrary()
    {
        SignIn(administrator: true);
        var cut = OpenPage(_configuration.Libraries[0], vaultVisible: true);
        cut.WaitForElement(".folder-grid");

        await cut.ClickAsync("[data-source='movies-main'] .app-row-menu__trigger");

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role=menuitem]"), item => Text(item) == "Move to Vault…"));
    }

    [Fact]
    public void AVaultLibraryFolderTableHasNoRowMenuSoNothingCanMoveOrChangeItsFolders()
    {
        SignIn(administrator: true);
        var cut = OpenPage(VaultMovies(), vaultVisible: true);
        cut.WaitForElement(".folder-grid");

        Assert.Empty(cut.FindAll(".app-row-menu__trigger"));
        Assert.DoesNotContain("Move to Vault", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOpenedVaultProtectedLibraryForAnAdministratorIsReadOnlyHasNoScanOrAddFilesAndKeepsItsActivityInTheVault()
    {
        SignIn(administrator: true);

        var cut = OpenPage(VaultMovies(), vaultVisible: true);
        cut.WaitForElement(".folder-grid");

        var actions = cut.FindAll(".libpage__actions button").Select(Text).ToArray();
        Assert.Equal(["Add folder"], actions);
        Assert.Contains("E:\\Private\\Movies", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(cut.FindAll("[role=tab]"), tab => Text(tab) == "Lookup");
        Assert.Empty(_activityReads);
    }

    [Fact]
    public void TheLookupTabOfAVaultLibraryHoldsTheOnlineLookupSwitchOffByDefaultWithThePrivacyNote()
    {
        SignIn(administrator: true);

        var cut = OpenPage(VaultMovies(), tab: LibraryShell.LookupTab, vaultVisible: true);

        var lookup = cut.Find("section.vault-lookup");
        Assert.False(lookup.QuerySelector("[role=switch]")!.HasAttribute("checked"));
        Assert.Contains("Titles from this section will be sent to online services.", lookup.TextContent + cut.Find("[data-testid=vault-lookup-privacy]").TextContent, StringComparison.Ordinal);
        Assert.Empty(_activityReads);
    }

    [Fact]
    public void SwitchingOnlineLookupOnSavesItForThatLibraryOnly()
    {
        SignIn(administrator: true);
        var cut = OpenPage(VaultMovies(), tab: LibraryShell.LookupTab, vaultVisible: true);

        cut.Find("section.vault-lookup [role=switch]").Change(true);

        cut.WaitForAssertion(() => Assert.Equal([(VaultMoviesId, true)], _lookupSwitches));
    }

    [Fact]
    public void WhenTheLookupSwitchCannotBeSavedItGoesBackAndTheEngineIsToldNothingElse()
    {
        SignIn(administrator: true);
        _lookupSaves = false;
        var cut = OpenPage(VaultMovies(), tab: LibraryShell.LookupTab, vaultVisible: true);

        cut.Find("section.vault-lookup [role=switch]").Change(true);

        cut.WaitForAssertion(() => Assert.Single(_lookupSwitches));
        cut.WaitForAssertion(() => Assert.False(cut.Find("section.vault-lookup [role=switch]").HasAttribute("checked")));
    }

    [Fact]
    public void AddFolderDialogOnAVaultLibraryAlwaysAddsToTheVaultAndDrawsNoSwitch()
    {
        var dialogs = Render<AppDialogHost>();
        Services.GetRequiredService<IAppDialogService>();
        var parameters = new AppDialogParameters<LibraryAddFolderDialog>
        {
            { component => component.KindKey, "movies" },
            { component => component.ForceVault, true },
        };
        _ = Services.GetRequiredService<IAppDialogService>().ShowAsync<LibraryAddFolderDialog>(string.Empty, parameters, LibraryAddFolderDialog.DialogOptions);

        dialogs.WaitForElement(".add-folder");
        Assert.Empty(dialogs.FindAll("[role=switch]"));
    }

    [Fact]
    public void MoveToVaultSaysThereIsNoVaultLibraryYetAndOffersToAddOneWithoutMovingAnything()
    {
        var dialogs = OpenMove();

        dialogs.WaitForElement("[data-testid=move-to-vault-none]");
        Assert.Contains(dialogs.FindAll("button"), button => Text(button) == "Add a Vault library");
        Assert.Contains("Nothing has been moved", dialogs.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveToVaultSaysItNeedsAManagedFolderWhenTheVaultLibraryHasNone()
    {
        _destination = new("movies", VaultMoveDestinationStates.NeedsManagedFolder, Guid.Parse(VaultMoviesId), "Private Movies", 0);

        var dialogs = OpenMove();

        dialogs.WaitForElement("[data-testid=move-to-vault-managed]");
        Assert.DoesNotContain(dialogs.FindAll("button"), button => Text(button) == "Add a Vault library");
    }

    [Fact]
    public void MoveToVaultNamesTheDestinationAndStatesPlainlyThatNothingHasBeenMoved()
    {
        _destination = new("movies", VaultMoveDestinationStates.Ready, Guid.Parse(VaultMoviesId), "Private Movies", 1);

        var dialogs = OpenMove();

        var ready = dialogs.WaitForElement("[data-testid=move-to-vault-ready]");
        Assert.Contains("Private Movies", Text(ready), StringComparison.Ordinal);
        Assert.Contains("Nothing has been moved.", dialogs.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveToVaultSaysSoWhenTheEngineCannotAnswer()
    {
        _destination = null;

        var dialogs = OpenMove();

        dialogs.WaitForAssertion(() => Assert.Contains("Library couldn't check", dialogs.Markup, StringComparison.Ordinal));
    }

    private IRenderedComponent<AppDialogHost> OpenMove()
    {
        var dialogs = Render<AppDialogHost>();
        var parameters = new AppDialogParameters<MoveToVaultDialog>
        {
            { component => component.KindKey, "movies" },
            { component => component.PluralLabel, "Movies" },
            { component => component.FolderName, "Library Movies" },
        };
        _ = Services.GetRequiredService<IAppDialogService>().ShowAsync<MoveToVaultDialog>(string.Empty, parameters, MoveToVaultDialog.DialogOptions);
        dialogs.WaitForElement("[data-testid=move-to-vault]");
        return dialogs;
    }
}
