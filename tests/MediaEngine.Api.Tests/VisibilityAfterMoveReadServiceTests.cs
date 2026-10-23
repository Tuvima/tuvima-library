using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Account = MediaEngine.Domain.Entities.Account;
using AccountProfileGrant = MediaEngine.Domain.Entities.AccountProfileGrant;

namespace MediaEngine.Api.Tests;

/// <summary>
/// "If these files move into library L, who can see them now but would not in L?" (simpler access D13), on real temp SQLite: the account
/// facts, the hidden libraries and the per-library file counts are real; only the libraries list is a hand-written loader.
/// </summary>
public sealed class VisibilityAfterMoveReadServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Movies = Guid.NewGuid();
    private static readonly Guid Lectures = Guid.NewGuid();
    private static readonly Guid Vault = Guid.NewGuid();
    private static readonly Guid Kids = Guid.NewGuid();
    private static readonly Guid Music = Guid.NewGuid();

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_visibility_after_move_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly VisibilityAfterMoveReadService _service;
    private readonly Guid _edition = Guid.NewGuid();

    public VisibilityAfterMoveReadServiceTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        var vaultLibraries = new VaultLibraryRepository(_database);
        var libraries = new LibrariesOnlyLoader(
            Library(Movies, "Movies", LibraryAreas.Watch),
            Library(Lectures, "Lectures", LibraryAreas.Watch),
            Library(Vault, "Private", LibraryAreas.Watch),
            Library(Kids, "Kids", LibraryAreas.Watch, ("kids-source", "/media/kids")),
            Library(Music, "Music", LibraryAreas.Listen));
        _service = new VisibilityAfterMoveReadService(
            _accounts, new ProfileRepository(_database), new MediaAssetLibraryCountRepository(_database),
            vaultLibraries, libraries, new LibraryVisibilityRule(_accounts, vaultLibraries));
        var work = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works(id,media_type,work_kind) VALUES(@work,'Movies','child');", new { work });
        connection.Execute("INSERT INTO editions(id,work_id) VALUES(@edition,@work);", new { edition = _edition, work });
        connection.Execute(
            "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@vault,'none',@now,@now,@now);",
            new { vault = Vault.ToString("D"), now = Now.ToString("O") });
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }

    [Fact]
    public async Task WhenSamHasLecturesHidden_MovingFortyFilesFromMoviesIntoLectures_ReturnsSamWithForty()
    {
        var sam = await CreateAccountAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Lectures]);
        var ids = AddAssets(Movies, 40);

        var result = await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: ids));

        var loss = Assert.Single(result.People!);
        Assert.Equal((sam.Id, "Sam", 40), (loss.AccountId, loss.DisplayName, loss.FileCount));
    }

    [Fact]
    public async Task WhenNobodyHidesTheTarget_TheListIsEmpty()
    {
        await CreateAccountAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Kids]);
        var ids = AddAssets(Movies, 5);

        var result = await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: ids));

        Assert.Null(result.Refusal);
        Assert.Empty(result.People!);
    }

    [Fact]
    public async Task WhenSamLacksTheWatchArea_SamCannotSeeTheFilesNow_SoSamIsNotListed()
    {
        await CreateAccountAsync("sam@example.test", "Sam", [AccountFeatureId.Listen], hidden: [Lectures]);
        var ids = AddAssets(Movies, 3);

        var result = await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: ids));

        Assert.Empty(result.People!);
    }

    [Fact]
    public async Task WhenTheFilesAreHiddenFromSamNowAndStayHiddenInTheTarget_SamIsNotListed()
    {
        await CreateAccountAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Movies, Lectures]);
        var ids = AddAssets(Movies, 3);

        Assert.Empty((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: ids))).People!);
    }

    [Fact]
    public async Task WhenTheTargetIsInAnAreaThePersonLacks_ThePersonLosesSightOfFilesTheySeeNow()
    {
        var sam = await CreateAccountAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: []);
        var ids = AddAssets(Movies, 4);

        var result = await _service.GetAsync(new VisibilityAfterMoveRequest(Music, AssetIds: ids));

        var loss = Assert.Single(result.People!);
        Assert.Equal((sam.Id, 4), (loss.AccountId, loss.FileCount));
    }

    [Fact]
    public async Task AHouseholdMemberInheritsThePrimarysHiddenLibraries_AndIsListedWithThePrimary()
    {
        var primary = await CreateAccountAsync("primary@example.test", "Pat", [AccountFeatureId.Watch], hidden: [Lectures]);
        var member = await CreateAccountAsync("member@example.test", "Mia", [], hidden: []);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE accounts SET grants_inherit_from_account_id=@primary WHERE id=@member;", new { primary = primary.Id, member = member.Id });
        }

        var result = await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: AddAssets(Movies, 7)));

        Assert.Equal(
            new[] { (primary.Id, "Pat", 7), (member.Id, "Mia", 7) }.OrderBy(row => row.Item1),
            result.People!.Select(row => (row.AccountId, row.DisplayName, row.FileCount)).OrderBy(row => row.AccountId));
    }

    [Fact]
    public async Task Administrators_AreNeverListed_EvenWithALibraryHiddenRow()
    {
        await CreateAccountAsync("admin@example.test", "Ada", [AccountFeatureId.Watch], hidden: [Lectures], administrator: true);

        Assert.Empty((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: AddAssets(Movies, 2)))).People!);
    }

    [Fact]
    public async Task AFileAlreadyInTheTarget_IsNotCounted()
    {
        await CreateAccountAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Lectures]);
        var ids = AddAssets(Lectures, 6).Concat(AddAssets(Movies, 2)).ToList();

        var loss = Assert.Single((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: ids))).People!);

        Assert.Equal(2, loss.FileCount);
    }

    [Fact]
    public async Task AFileInTheVault_IsNotSeenNow_SoItIsNotCounted()
    {
        await CreateAccountAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Lectures]);
        var ids = AddAssets(Vault, 3).Concat(AddAssets(Movies, 1)).ToList();

        var loss = Assert.Single((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: ids))).People!);

        Assert.Equal(1, loss.FileCount);
    }

    [Fact]
    public async Task APersonWithNoProfile_IsNamedByTheirEmail()
    {
        var sam = await CreateAccountAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Lectures]);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM account_profile_grants WHERE account_id=@id;", new { id = sam.Id });
        }

        var loss = Assert.Single((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: AddAssets(Movies, 1)))).People!);

        Assert.Equal("sam@example.test", loss.DisplayName);
    }

    [Fact]
    public async Task AVaultTarget_IsRefused()
    {
        var result = await _service.GetAsync(new VisibilityAfterMoveRequest(Vault, AssetIds: AddAssets(Movies, 1)));

        Assert.Null(result.People);
        Assert.NotNull(result.Refusal);
    }

    [Fact]
    public async Task AnUnknownTarget_IsRefused()
    {
        var result = await _service.GetAsync(new VisibilityAfterMoveRequest(Guid.NewGuid(), AssetIds: AddAssets(Movies, 1)));

        Assert.Null(result.People);
        Assert.NotNull(result.Refusal);
    }

    [Fact]
    public async Task AFolderByItsPath_CountsOnlyTheFilesInsideIt_AndNotASiblingWhoseNameStartsTheSame()
    {
        var sam = await CreateAccountAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Lectures]);
        AddAsset(Kids, "/media/kids/a.mkv");
        AddAsset(Kids, "/media/kids/deeper/b.mkv");
        AddAsset(Kids, "/media/kids2/c.mkv");

        var loss = Assert.Single((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, FolderPath: "/media/kids", LibraryId: Kids))).People!);

        Assert.Equal((sam.Id, 2), (loss.AccountId, loss.FileCount));
    }

    [Fact]
    public async Task AFolderBySourceId_IsFoundFromTheLibrariesFolders()
    {
        await CreateAccountAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Lectures]);
        AddAsset(Kids, "/media/kids/a.mkv");
        AddAsset(Kids, "/media/kids2/b.mkv");

        var loss = Assert.Single((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, SourceId: "kids-source"))).People!);

        Assert.Equal(1, loss.FileCount);
    }

    [Fact]
    public async Task AnUnknownSourceId_IsRefused()
    {
        var result = await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, SourceId: "no-such-source"));

        Assert.Null(result.People);
        Assert.NotNull(result.Refusal);
    }

    [Fact]
    public async Task MoreThanFiveThousandAssetIds_AreRefusedWithAPlainMessage_AndExactlyFiveThousandAreAnswered()
    {
        var tooMany = Enumerable.Range(0, VisibilityAfterMoveReadService.MaxAssetIds + 1).Select(_ => Guid.NewGuid()).ToList();
        var allowed = tooMany.Take(VisibilityAfterMoveReadService.MaxAssetIds).ToList();

        var refused = await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: tooMany));
        var answered = await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: allowed));

        Assert.Null(refused.People);
        Assert.Contains("too many", refused.Refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Null(answered.Refusal);
    }

    [Fact]
    public async Task NamingNoFilesOrBothWays_IsRefused()
    {
        Assert.NotNull((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures))).Refusal);
        Assert.NotNull((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, FolderPath: "/media/kids", AssetIds: [Guid.NewGuid()]))).Refusal);
        Assert.NotNull((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, SourceId: "kids-source", FolderPath: "/media/kids"))).Refusal);
    }

    private static LibraryFolderConfig Library(Guid id, string name, string area, params (string Id, string Path)[] sources) => new()
    {
        Id = id.ToString("D"),
        Name = name,
        Kind = LibraryKinds.Catalogued,
        Area = area,
        Sources = [.. sources.Select(source => new LibrarySourceConfig { Id = source.Id, Path = source.Path })],
    };

    private List<Guid> AddAssets(Guid library, int count) =>
        [.. Enumerable.Range(0, count).Select(index => AddAsset(library, $"/media/{library:N}/{index}.mkv"))];

    private Guid AddAsset(Guid library, string path)
    {
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,library_id)
            VALUES(@asset,@edition,@hash,@path,@library);
            """, new { asset, edition = _edition, hash = Guid.NewGuid().ToString("N"), path, library = library.ToString("D") });
        return asset;
    }

    private async Task<Account> CreateAccountAsync(
        string email, string name, IEnumerable<AccountFeatureId> features, IEnumerable<Guid> hidden, bool administrator = false)
    {
        var profileId = Guid.NewGuid();
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsEnabled = true,
            IsAdministrator = administrator,
            AuthorizationVersion = 1,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        await _accounts.CreateAccountAsync(
            account,
            new AccountProfileGrant
            {
                AccountId = account.Id,
                ProfileId = profileId,
                IsDefault = true,
                IsEnabled = true,
                AuthorizationVersion = 1,
                GrantedAt = Now,
            },
            features.ToHashSet(),
            hidden.ToHashSet(),
            newProfile: new Profile { Id = profileId, DisplayName = name, Role = ProfileRole.StandardUser, CreatedAt = Now });
        return account;
    }

    /// <summary>A configuration loader that holds only the libraries list; the library kinds are the built-in ones.</summary>
    private sealed class LibrariesOnlyLoader : IConfigurationLoader
    {
        public LibrariesOnlyLoader(params LibraryFolderConfig[] libraries) =>
            Libraries = new LibrariesConfiguration { Libraries = [.. libraries] };

        public LibrariesConfiguration Libraries { get; }

        public event EventHandler<ConfigurationFileChangedEventArgs>? ConfigurationChanged { add { } remove { } }

        public LibrariesConfiguration LoadLibraries() => Libraries;

        public string ConfigDirectoryPath => string.Empty;
        public CoreConfiguration LoadCore() => new();
        public void SaveCore(CoreConfiguration config) { }
        public ScoringSettings LoadScoring() => new();
        public void SaveScoring(ScoringSettings settings) { }
        public MaintenanceSettings LoadMaintenance() => new();
        public void SaveMaintenance(MaintenanceSettings settings) { }
        public HydrationSettings LoadHydration() => new();
        public void SaveHydration(HydrationSettings settings) { }
        public PipelineConfiguration LoadPipelines() => new();
        public void SavePipelines(PipelineConfiguration config) { }
        public DisambiguationSettings LoadDisambiguation() => new();
        public void SaveDisambiguation(DisambiguationSettings settings) { }
        public MediaTypeConfiguration LoadMediaTypes() => new();
        public void SaveMediaTypes(MediaTypeConfiguration config) { }
        public TranscodingSettings LoadTranscoding() => new();
        public void SaveTranscoding(TranscodingSettings settings) { }
        public FieldPriorityConfiguration LoadFieldPriorities() => new();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
        public ProviderConfiguration? LoadProvider(string name) => null;
        public void DeleteProvider(string name) { }
        public void SaveProvider(ProviderConfiguration config) { }
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
        public T? LoadAi<T>() where T : class => default;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
    }
}
