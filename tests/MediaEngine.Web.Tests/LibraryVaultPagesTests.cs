using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Services.Vault;
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
    private VaultMovePreviewDto _preview = new()
    {
        State = VaultMovePreviewStates.Ready,
        Items = 3,
        Bytes = 3L * 1024 * 1024 * 1024,
        SourceName = "Library Movies",
        DestinationName = "Private Movies",
        Fingerprint = "f1",
    };
    private readonly List<StartVaultMoveRequest> _startedMoves = [];
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
        IsVault = true,
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
                return Task.FromResult<VaultLibraryActionResultDto?>(new VaultLibraryActionResultDto(true, VaultMoviesId));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetNsfwStatusAsync), _ => Task.FromResult(
                VaultCall<NsfwStatusDto>.Ok(new NsfwStatusDto { Mode = "allowed", Unlocked = true, HasPin = true, IdleLockMinutes = 60 })));
            stub.SetHandler(nameof(IEngineApiClient.GetVaultMoveDestinationAsync), _ => Task.FromResult(_destination));
            stub.SetHandler(nameof(IEngineApiClient.PreviewVaultMoveAsync), _ => Task.FromResult(VaultCall<VaultMovePreviewDto>.Ok(_preview)));
            stub.SetHandler(nameof(IEngineApiClient.StartVaultMoveAsync), args =>
            {
                _startedMoves.Add((StartVaultMoveRequest)args![0]!);
                return Task.FromResult(VaultCall<StartVaultMoveResultDto>.Ok(new StartVaultMoveResultDto
                {
                    Started = true,
                    Job = new VaultMoveJobDto { Id = Guid.NewGuid(), State = "running", Total = 3, Moved = 0 },
                }));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetVaultMovesAsync), _ => Task.FromResult(
                VaultCall<IReadOnlyList<VaultMoveJobDto>>.Ok([])));
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

    // An administrator signs in with Vault mode on; the Vault choices are drawn only then.
    private void SignIn(bool administrator, bool vaultMode = true)
    {
        var profile = Guid.NewGuid();
        Services.GetRequiredService<DashboardSessionAccessor>().Set("token", profile, profile, Guid.NewGuid(),
            new DashboardAuthorityResponse(Guid.NewGuid(), profile, true, true, 1, 1, administrator, administrator, null, 1, [], ["settings.administration"], ["household.manage"]));
        if (administrator && vaultMode)
        {
            Services.GetRequiredService<NsfwModeState>().RefreshAsync(profile, isKids: false).GetAwaiter().GetResult();
        }
    }

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

        dialogs.WaitForAssertion(() => Assert.Contains("Choose your ", dialogs.Markup, StringComparison.Ordinal));
        dialogs.WaitForAssertion(() => Assert.Contains(dialogs.FindAll("button"), button => Text(button) == "Choose folder"));
        Assert.DoesNotContain("Vault", dialogs.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAdministratorWhoseVaultModeIsOffGetsNoVaultSwitchNoMoveToVaultAndNoVaultWord()
    {
        SignIn(administrator: true, vaultMode: false);
        var dialogs = Render<AppDialogHost>();
        var cut = OpenPage(_configuration.Libraries[0]);
        cut.WaitForElement(".folder-grid");

        await cut.ClickAsync("[data-source='movies-main'] .app-row-menu__trigger");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=menuitem]")));
        Assert.DoesNotContain(cut.FindAll("[role=menuitem]"), item => Text(item).Contains("Vault", StringComparison.OrdinalIgnoreCase));

        await cut.ClickAsync(page => page.FindAll(".libpage__actions button").First(button => Text(button) == "Add folder"));
        dialogs.WaitForAssertion(() => Assert.Contains(dialogs.FindAll("button"), button => Text(button) == "Choose folder"));
        Assert.DoesNotContain("Vault", dialogs.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vault", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddFolderOnAnOrdinaryLibraryOffersNoVaultChoiceEvenToAnAdministratorAndTheFolderJoinsThatLibrary()
    {
        SignIn(administrator: true);
        var dialogs = Render<AppDialogHost>();
        var cut = OpenPage(_configuration.Libraries[0], vaultVisible: true);

        await cut.ClickAsync(page => page.FindAll(".libpage__actions button").First(button => Text(button) == "Add folder"));
        dialogs.WaitForAssertion(() => Assert.Contains(dialogs.FindAll("button"), button => Text(button) == "Choose folder" && !button.HasAttribute("disabled")));
        Assert.DoesNotContain("Vault", dialogs.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(dialogs.FindAll("[role=switch]"));
        await dialogs.ClickAsync(host => host.FindAll("button").First(button => Text(button) == "Choose folder" && !button.HasAttribute("disabled")));

        cut.WaitForAssertion(() => Assert.NotEmpty(_mutations));
        Assert.Empty(_vaultFolders);
    }

    [Fact]
    public async Task AddFolderOnAVaultLibraryAlwaysAddsToTheVaultAndOffersOnlyTheManagedFolderSwitchOffByDefault()
    {
        SignIn(administrator: true);
        var dialogs = Render<AppDialogHost>();
        var cut = OpenPage(VaultMovies(), vaultVisible: true);

        await cut.ClickAsync(page => page.FindAll(".libpage__actions button").First(button => Text(button) == "Add folder"));
        dialogs.WaitForAssertion(() => Assert.Contains(dialogs.FindAll("button"), button => Text(button) == "Choose folder" && !button.HasAttribute("disabled")));
        var managed = Assert.Single(dialogs.FindAll("[role=switch]"));
        Assert.False(managed.HasAttribute("checked"));
        await dialogs.ClickAsync(host => host.FindAll("button").First(button => Text(button) == "Choose folder" && !button.HasAttribute("disabled")));

        cut.WaitForAssertion(() => Assert.Single(_vaultFolders));
        Assert.Equal("movies", _vaultFolders[0].KindKey);
        Assert.False(_vaultFolders[0].Managed);
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
        Assert.Contains(LibraryLookupRules.VaultPrivacyNote, cut.Find("[data-testid=vault-lookup-privacy]").TextContent, StringComparison.Ordinal);
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
    public void MoveToVaultCountsWhatWouldMoveAndNamesTheDestinationWithoutAnyTitle()
    {
        _destination = new("movies", VaultMoveDestinationStates.Ready, Guid.Parse(VaultMoviesId), "Private Movies", 1);

        var dialogs = OpenMove();

        var ready = dialogs.WaitForElement("[data-testid=move-to-vault-ready]");
        Assert.Contains("3 items (3 GB)", Text(ready), StringComparison.Ordinal);
        Assert.Contains("Private Movies", Text(ready), StringComparison.Ordinal);
        Assert.Empty(dialogs.FindAll("[role=switch]"));
        Assert.Contains(dialogs.FindAll("button"), button => Text(button) == "Move to Vault");
    }

    [Fact]
    public void MoveToVaultOnAReadOnlyFolderSaysTheOriginalsStayAndOffersRemovingThemAsASeparateChoiceOffByDefault()
    {
        _destination = new("movies", VaultMoveDestinationStates.Ready, Guid.Parse(VaultMoviesId), "Private Movies", 1);
        _preview = new()
        {
            State = VaultMovePreviewStates.Ready,
            Items = 1,
            Bytes = 10,
            SourceName = "Library Movies",
            DestinationName = "Private Movies",
            OriginalsStay = true,
            Fingerprint = "f2",
        };

        var dialogs = OpenMove();

        dialogs.WaitForElement("[data-testid=move-to-vault-originals]");
        var remove = Assert.Single(dialogs.FindAll("[role=switch]"));
        Assert.False(remove.HasAttribute("checked"));
    }

    [Fact]
    public void MoveToVaultSaysWhyWhenTheEngineWillNotMoveTheFolder()
    {
        _destination = new("movies", VaultMoveDestinationStates.Ready, Guid.Parse(VaultMoviesId), "Private Movies", 1);
        _preview = new() { State = VaultMovePreviewStates.NothingToMove, Message = "Nothing in this folder can be moved." };

        var dialogs = OpenMove();

        var blocked = dialogs.WaitForElement("[data-testid=move-to-vault-blocked]");
        Assert.Contains("Nothing in this folder can be moved.", Text(blocked), StringComparison.Ordinal);
        Assert.DoesNotContain(dialogs.FindAll("button"), button => Text(button) == "Move to Vault");
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
