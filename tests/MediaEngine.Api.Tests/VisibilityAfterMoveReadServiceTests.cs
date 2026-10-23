using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using Account = MediaEngine.Domain.Entities.Account;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Profile = MediaEngine.Domain.Aggregates.Profile;

namespace MediaEngine.Api.Tests;

/// <summary>
/// "If these files move into library L, who can see them now but would not in L?" (simpler access D13), on real temp SQLite: the profile
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
    private readonly ProfileAccessRepository _profileAccess;
    private readonly VisibilityAfterMoveReadService _service;
    private readonly Guid _edition = Guid.NewGuid();

    public VisibilityAfterMoveReadServiceTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _profileAccess = new ProfileAccessRepository(_database);
        var vaultLibraries = new VaultLibraryRepository(_database);
        var libraries = new LibrariesOnlyLoader(
            Library(Movies, "Movies", LibraryAreas.Watch),
            Library(Lectures, "Lectures", LibraryAreas.Watch),
            Library(Vault, "Private", LibraryAreas.Watch),
            Library(Kids, "Kids", LibraryAreas.Watch, ("kids-source", "/media/kids")),
            Library(Music, "Music", LibraryAreas.Listen));
        _service = new VisibilityAfterMoveReadService(
            _accounts, new ProfileRepository(_database), _profileAccess, new MediaAssetLibraryCountRepository(_database),
            vaultLibraries, libraries, new LibraryVisibilityRule(_profileAccess, vaultLibraries));
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
        var sam = await CreateProfileAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Lectures]);
        var ids = AddAssets(Movies, 40);

        var result = await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: ids));

        var loss = Assert.Single(result.People!);
        Assert.Equal((sam.Id, "Sam", 40), (loss.ProfileId, loss.DisplayName, loss.FileCount));
    }

    [Fact]
    public async Task WhenNobodyHidesTheTarget_TheListIsEmpty()
    {
        await CreateProfileAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Kids]);
        var ids = AddAssets(Movies, 5);

        var result = await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: ids));

        Assert.Null(result.Refusal);
        Assert.Empty(result.People!);
    }

    [Fact]
    public async Task WhenSamLacksTheWatchArea_SamCannotSeeTheFilesNow_SoSamIsNotListed()
    {
        await CreateProfileAsync("sam@example.test", "Sam", [AccountFeatureId.Listen], hidden: [Lectures]);
        var ids = AddAssets(Movies, 3);

        var result = await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: ids));

        Assert.Empty(result.People!);
    }

    [Fact]
    public async Task WhenTheFilesAreHiddenFromSamNowAndStayHiddenInTheTarget_SamIsNotListed()
    {
        await CreateProfileAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Movies, Lectures]);
        var ids = AddAssets(Movies, 3);

        Assert.Empty((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: ids))).People!);
    }

    [Fact]
    public async Task WhenTheTargetIsInAnAreaThePersonLacks_ThePersonLosesSightOfFilesTheySeeNow()
    {
        var sam = await CreateProfileAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: []);
        var ids = AddAssets(Movies, 4);

        var result = await _service.GetAsync(new VisibilityAfterMoveRequest(Music, AssetIds: ids));

        var loss = Assert.Single(result.People!);
        Assert.Equal((sam.Id, 4), (loss.ProfileId, loss.FileCount));
    }

    [Fact]
    public async Task EachProfileIsJudgedOnItsOwnHiddenLibraries_NotOnAnotherProfilesInTheSameHousehold()
    {
        var pat = await CreateProfileAsync("primary@example.test", "Pat", [AccountFeatureId.Watch], hidden: [Lectures]);
        var mia = await AddPersonAsync(pat, "Mia", [AccountFeatureId.Watch], hidden: []);

        var result = await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: AddAssets(Movies, 7)));

        var loss = Assert.Single(result.People!);
        Assert.Equal((pat.Id, "Pat", 7), (loss.ProfileId, loss.DisplayName, loss.FileCount));
        Assert.NotEqual(mia.Id, loss.ProfileId);
    }

    [Fact]
    public async Task APersonWithoutASignIn_IsListedLikeAnyoneElse()
    {
        var pat = await CreateProfileAsync("primary@example.test", "Pat", [AccountFeatureId.Listen], hidden: []);
        var mia = await AddPersonAsync(pat, "Mia", [AccountFeatureId.Watch], hidden: [Lectures]);

        var loss = Assert.Single((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: AddAssets(Movies, 2)))).People!);

        Assert.Equal((mia.Id, "Mia", 2), (loss.ProfileId, loss.DisplayName, loss.FileCount));
    }

    [Fact]
    public async Task ServerAdministrators_AreNeverListed_EvenWithALibraryHiddenRow()
    {
        await CreateProfileAsync("admin@example.test", "Ada", [AccountFeatureId.Watch], hidden: [Lectures], administrator: true);

        Assert.Empty((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: AddAssets(Movies, 2)))).People!);
    }

    [Fact]
    public async Task AProfileWhoseOnlySignInIsDisabled_IsNotListed_EvenWhenItWouldLoseSight()
    {
        var sam = await CreateProfileAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Lectures]);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE accounts SET is_enabled = 0 WHERE profile_id = @id;", new { id = sam.Id });
        }

        Assert.Empty((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: AddAssets(Movies, 3)))).People!);
    }

    [Fact]
    public async Task AFileAlreadyInTheTarget_IsNotCounted()
    {
        await CreateProfileAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Lectures]);
        var ids = AddAssets(Lectures, 6).Concat(AddAssets(Movies, 2)).ToList();

        var loss = Assert.Single((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: ids))).People!);

        Assert.Equal(2, loss.FileCount);
    }

    [Fact]
    public async Task AFileInTheVault_IsNotSeenNow_SoItIsNotCounted()
    {
        await CreateProfileAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Lectures]);
        var ids = AddAssets(Vault, 3).Concat(AddAssets(Movies, 1)).ToList();

        var loss = Assert.Single((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: ids))).People!);

        Assert.Equal(1, loss.FileCount);
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
        var sam = await CreateProfileAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Lectures]);
        AddAsset(Kids, "/media/kids/a.mkv");
        AddAsset(Kids, "/media/kids/deeper/b.mkv");
        AddAsset(Kids, "/media/kids2/c.mkv");

        var loss = Assert.Single((await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, FolderPath: "/media/kids", LibraryId: Kids))).People!);

        Assert.Equal((sam.Id, 2), (loss.ProfileId, loss.FileCount));
    }

    [Fact]
    public async Task AFolderBySourceId_IsFoundFromTheLibrariesFolders()
    {
        await CreateProfileAsync("sam@example.test", "Sam", [AccountFeatureId.Watch], hidden: [Lectures]);
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
    public async Task AnEmptyListOfFiles_IsRefused_NotAnsweredWithAnEmptyList()
    {
        var result = await _service.GetAsync(new VisibilityAfterMoveRequest(Lectures, AssetIds: []));

        Assert.Null(result.People);
        Assert.NotNull(result.Refusal);
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

    /// <summary>A person with a sign-in and a profile of their own (in a household of their own); a server administrator when asked.</summary>
    private async Task<Profile> CreateProfileAsync(
        string email, string name, IEnumerable<AccountFeatureId> features, IEnumerable<Guid> hidden, bool administrator = false)
    {
        var profile = new Profile { Id = Guid.NewGuid(), DisplayName = name, Role = ProfileRole.StandardUser, CreatedAt = Now };
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        await _accounts.CreateAccountAsync(
            account,
            new TestGrant(account.Id, profile.Id, IsDefault: true, AdminEnabled: administrator),
            features.ToHashSet(),
            hidden.ToHashSet(),
            newProfile: profile,
            newHouseholdName: $"{name}'s home");
        await _profileAccess.ReplaceAccessAsync(profile.Id, features.ToHashSet(), hidden.ToHashSet(), Now);
        return profile;
    }

    /// <summary>A person in the same household as <paramref name="of"/>, without a sign-in of their own.</summary>
    private async Task<Profile> AddPersonAsync(Profile of, string name, IEnumerable<AccountFeatureId> features, IEnumerable<Guid> hidden)
    {
        var household = (await new HouseholdRepository(_database).GetForAccountAsync(FindUser(of.Id)))!;
        var person = new Profile { Id = Guid.NewGuid(), DisplayName = name, Role = ProfileRole.StandardUser, CreatedAt = Now };
        await _accounts.CreateHouseholdPersonAsync(person, household.Id);
        await _profileAccess.ReplaceAccessAsync(person.Id, features.ToHashSet(), hidden.ToHashSet(), Now);
        return person;
    }

    private Guid FindUser(Guid profileId)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<Guid>("SELECT id FROM accounts WHERE profile_id = @profileId;", new { profileId });
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
