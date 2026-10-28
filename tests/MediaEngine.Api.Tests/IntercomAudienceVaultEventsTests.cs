using System.Reflection;
using Dapper;
using MediaEngine.Api.Realtime;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Events;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Live pushes to open screens never name a Vault file to anyone who has not switched Vault mode on in that very session, administrators
/// included (Videos spec D17, D33). Ingestion and operations events carry paths, not items, so they are judged by where the path sits.
/// A new live event cannot be added without saying which of the two kinds it is.
/// </summary>
public sealed class IntercomAudienceVaultEventsTests : IDisposable
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), $"tuvima_intercom_vault_{Guid.NewGuid():N}").Replace('\\', '/');
    private static readonly string VaultFolder = Root + "/Media/Private";
    private static readonly string VaultFile = VaultFolder + "/clips/one.mkv";
    private static readonly string OrdinaryFile = Root + "/Media/Movies/Dune.mkv";

    private readonly string _root = Root;
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly IdentityRepository _identities;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _vaultLibrary = Guid.NewGuid();
    private readonly Guid _vaultAsset = Guid.NewGuid();
    private readonly Guid _vaultWork = Guid.NewGuid();
    private readonly Guid _ordinaryLibrary = Guid.NewGuid();
    private readonly Guid _ordinaryAsset = Guid.NewGuid();
    private readonly Guid _ordinaryWork = Guid.NewGuid();

    public IntercomAudienceVaultEventsTests()
    {
        Directory.CreateDirectory(_root);
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _identities = new IdentityRepository(_database);
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
    }

    public void Dispose()
    {
        _configuration.Dispose();
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(Path.Combine(_root, "library.db"));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void EveryLiveEvent_IsEitherJudgedByWhereItSits_OrProvenFreeOfVaultContent()
    {
        var allEvents = typeof(SignalREvents)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name != nameof(SignalREvents.IntercomPath))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(allEvents);
        var unclassified = allEvents
            .Where(name => !IntercomAudienceAuthorizer.ClassifiedEvents.Contains(name) && !IntercomAudienceAuthorizer.VaultFreeEvents.ContainsKey(name))
            .ToList();
        Assert.True(unclassified.Count == 0, $"Say whether these live events can carry Vault content: {string.Join(", ", unclassified)}");
        Assert.All(IntercomAudienceAuthorizer.ClassifiedEvents, name => Assert.DoesNotContain(name, IntercomAudienceAuthorizer.VaultFreeEvents.Keys));
        Assert.All(IntercomAudienceAuthorizer.VaultFreeEvents, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value)));
        Assert.Empty(IntercomAudienceAuthorizer.ClassifiedEvents.Concat(IntercomAudienceAuthorizer.VaultFreeEvents.Keys).Except(allEvents));
    }

    public static IEnumerable<object[]> VaultEvents()
    {
        var asset = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
        var work = new Guid("aaaaaaaa-0000-0000-0000-000000000002");
        var now = DateTimeOffset.UtcNow;
        yield return Case(SignalREvents.IngestionStarted, new IngestionStartedEvent(VaultFile, now));
        yield return Case(SignalREvents.IngestionHashed, new IngestionHashedEvent(VaultFile, "hash", 10, TimeSpan.Zero));
        yield return Case(SignalREvents.IngestionCompleted, new IngestionCompletedEvent(VaultFile, "Videos", now));
        yield return Case(SignalREvents.IngestionCompleted, new AutoOrganizedIngestionCompletedEvent(VaultFile, "Videos", now));
        yield return Case(SignalREvents.IngestionFailed, new IngestionFailedEvent(VaultFile, "Unreadable", now));
        yield return Case(SignalREvents.IngestionItemProgress, new IngestionItemProgressEvent(Guid.NewGuid(), null, VaultFile, "one.mkv", "Scan", 1, 10, false));
        yield return Case(SignalREvents.IngestionItemProgress, new IngestionItemProgressEvent(Guid.NewGuid(), asset, "elsewhere", "x", "Scan", 1, 10, false));
        yield return Case(SignalREvents.MediaAdded, new MediaAddedEvent(work, null, "Videos", "Secret", null));
        yield return Case(SignalREvents.MediaRemoved, new MediaRemovedEvent(Guid.NewGuid(), VaultFile, "Missing"));
        yield return Case(SignalREvents.MetadataHarvested, new MetadataHarvestedEvent(asset, "stash", ["title"]));
        yield return Case(SignalREvents.MetadataHarvested, new ManualMetadataHarvestedEvent(asset, "stash", ["title"]));
        yield return Case(SignalREvents.MetadataHarvested, new RetailMetadataHarvestedEvent(asset, "scope", "group", "stash", "id", ["title"]));
        yield return Case(SignalREvents.MetadataHarvested, new WikidataMetadataHarvestedEvent(asset, "scope", "group", "wikidata", ["title"]));
        yield return Case(SignalREvents.MetadataHarvested, new ReclassifiedMetadataHarvestedEvent(asset, "Videos"));
        yield return Case(SignalREvents.HydrationStageCompleted, new HydrationStageCompletedEvent(asset, 1, 3, "stash"));
        yield return Case(SignalREvents.ReviewItemCreated, new ReviewItemCreatedEvent(Guid.NewGuid(), work, "LowConfidence", "Secret"));
        yield return Case(SignalREvents.ReviewItemResolved, new ReviewItemResolvedEvent(Guid.NewGuid(), work, "Resolved"));
        yield return Case(SignalREvents.ReviewItemResolved, new ReviewItemResolvedSupplementaryEvent(Guid.NewGuid(), asset, "Resolved"));
        yield return Case(SignalREvents.ReviewItemResolved, new EntityReviewResolvedEvent(work, "Resolved"));
        yield return Case(SignalREvents.FolderHealthChanged, new FolderHealthChangedEvent(VaultFolder, true, true, true, now));
        yield return Case(SignalREvents.WatchFolderActive, new WatchFolderActiveEvent(VaultFolder + "/new", now));
        yield return Case(SignalREvents.InitialSweepStarted, new InitialSweepStartedEvent([OrdinaryFile, VaultFolder], now));
        yield return Case(SignalREvents.LibraryItemsRemoved, new LibraryItemsRemovedEvent([work], [asset]));
    }

    private static object[] Case(string name, object payload) => [name, payload];

    [Theory]
    [MemberData(nameof(VaultEvents))]
    public async Task AVaultEvent_ReachesNobody_UntilTheirOwnSessionHasNsfwModeOn_AdministratorsIncluded(string eventName, object payload)
    {
        await SeedAsync(administrator: true);
        var authorizer = Authorizer();
        var connection = new IntercomAudienceConnection("connection", _sessionId, _accountId);
        payload = Retarget(payload);

        Assert.False(await Receives(authorizer, connection, eventName, payload));

        await UnlockAsync();

        Assert.True(await Receives(authorizer, connection, eventName, payload));
    }

    [Fact]
    public async Task AnOrdinaryIngestionEvent_StillReachesAnAdministrator()
    {
        await SeedAsync(administrator: true);
        var authorizer = Authorizer();
        var connection = new IntercomAudienceConnection("connection", _sessionId, _accountId);
        var now = DateTimeOffset.UtcNow;

        Assert.True(await authorizer.CanReceiveAsync(connection, SignalREvents.IngestionStarted, new IngestionStartedEvent(OrdinaryFile, now)));
        Assert.True(await authorizer.CanReceiveAsync(connection, SignalREvents.FolderHealthChanged, new FolderHealthChangedEvent(Root + "/Media/Movies", true, true, true, now)));
        Assert.True(await authorizer.CanReceiveAsync(connection, SignalREvents.ReviewItemResolved, new ReviewItemResolvedEvent(Guid.NewGuid(), _ordinaryWork, "Resolved")));
        Assert.True(await authorizer.CanReceiveAsync(connection, SignalREvents.IngestionItemProgress,
            new IngestionItemProgressEvent(Guid.NewGuid(), _ordinaryAsset, OrdinaryFile, "Dune.mkv", "Scan", 1, 10, false)));
        Assert.True(await authorizer.CanReceiveAsync(connection, SignalREvents.LibraryItemsRemoved,
            new LibraryItemsRemovedEvent([_ordinaryWork, Guid.NewGuid()], [_ordinaryAsset])));
    }

    [Fact]
    public async Task APathBesideTheVaultFolder_IsNotTheVault_AndSlashesAndCaseDoNotMatter()
    {
        await SeedAsync(administrator: true);
        var classifier = new VaultPathClassifier(new VaultLibraryRepository(_database), _configuration);

        Assert.True(await classifier.IsVaultPathAsync(VaultFolder));
        Assert.True(await classifier.IsVaultPathAsync((VaultFolder + "/Clips/One.mkv").Replace('/', '\\').ToUpperInvariant()));
        Assert.False(await classifier.IsVaultPathAsync(Root + "/Media/PrivateStuff/one.mkv"));
        Assert.False(await classifier.IsVaultPathAsync(Root + "/Media"));
        Assert.False(await classifier.IsVaultPathAsync(OrdinaryFile));
        Assert.False(await classifier.IsVaultPathAsync(""));
        Assert.False(await classifier.IsVaultPathAsync(null));
    }

    [Fact]
    public async Task AFolderThatHoldsTheVaultFolder_IsFlaggedByTheContainsCheck_ButNotByThePlainVaultCheck()
    {
        await SeedAsync(administrator: true);
        var classifier = new VaultPathClassifier(new VaultLibraryRepository(_database), _configuration);

        Assert.False(await classifier.IsVaultPathAsync(Root + "/Media"));
        Assert.True(await classifier.IsOrContainsVaultPathAsync(Root + "/Media"));
        Assert.True(await classifier.IsOrContainsVaultPathAsync((Root + "/Media/").Replace('/', '\\').ToUpperInvariant()));
        Assert.True(await classifier.IsOrContainsVaultPathAsync(VaultFolder + "/Clips"));
        Assert.False(await classifier.IsOrContainsVaultPathAsync(Root + "/Media/Movies"));
        Assert.False(await classifier.IsOrContainsVaultPathAsync(Root + "/Media/Priv"));
        Assert.False(await classifier.IsOrContainsVaultPathAsync(null));
    }

    private IntercomAudienceAuthorizer Authorizer() => new(
        _identities, _accounts, new ProfileAccessRepository(_database),
        new Resources(_vaultAsset, _vaultLibrary, _ordinaryAsset, _ordinaryLibrary), TimeProvider.System,
        new VaultLibraryRepository(_database), new NsfwAccessRepository(_database),
        new VaultPathClassifier(new VaultLibraryRepository(_database), _configuration),
        TestLibraryVisibility.Rule(new ProfileAccessRepository(_database), new VaultLibraryRepository(_database)));

    private static async Task<bool> Receives(IntercomAudienceAuthorizer authorizer, IntercomAudienceConnection connection, string eventName, object payload) =>
        await authorizer.CanReceiveAsync(connection, eventName, payload);

    // The static cases use placeholder ids; point them at the seeded Vault file and work.
    private object Retarget(object payload) => payload switch
    {
        IngestionItemProgressEvent { MediaAssetId: not null } value => value with { MediaAssetId = _vaultAsset },
        MediaAddedEvent value => value with { WorkId = _vaultWork },
        MediaRemovedEvent value => value with { AssetId = _vaultAsset },
        MetadataHarvestedEvent value => value with { EntityId = _vaultAsset },
        ManualMetadataHarvestedEvent value => value with { EntityId = _vaultAsset },
        RetailMetadataHarvestedEvent value => value with { EntityId = _vaultAsset },
        WikidataMetadataHarvestedEvent value => value with { EntityId = _vaultAsset },
        ReclassifiedMetadataHarvestedEvent value => value with { EntityId = _vaultAsset },
        HydrationStageCompletedEvent value => value with { EntityId = _vaultAsset },
        ReviewItemCreatedEvent value => value with { EntityId = _vaultWork },
        ReviewItemResolvedEvent value => value with { EntityId = _vaultWork },
        ReviewItemResolvedSupplementaryEvent value => value with { EntityId = _vaultAsset },
        EntityReviewResolvedEvent value => value with { EntityId = _vaultWork },
        LibraryItemsRemovedEvent => new LibraryItemsRemovedEvent([_vaultWork], [_vaultAsset]),
        _ => payload,
    };

    private async Task UnlockAsync()
    {
        var now = DateTimeOffset.UtcNow;
        using (var database = _database.CreateConnection())
        {
            database.Execute(
                "INSERT INTO profile_nsfw_settings(profile_id,access_mode,updated_at) VALUES(@profile,'allowed',@now);",
                new { profile = GuidSql.ToBlob(Profile.SeedProfileId), now = now.ToString("O") });
        }

        await new NsfwAccessRepository(_database).UnlockAsync(_sessionId, Profile.SeedProfileId, NsfwUnlockMethod.Allowed, 15, now);
    }

    private async Task SeedAsync(bool administrator)
    {
        var now = DateTimeOffset.UtcNow;
        await _accounts.CreateAccountAsync(
            new Account
            {
                Id = _accountId,
                Email = "viewer@example.com",
                NormalizedEmail = "VIEWER@EXAMPLE.COM",
                IsEnabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new TestGrant(_accountId, Profile.SeedProfileId, IsDefault: true, AdminEnabled: administrator),
            new HashSet<AccountFeatureId> { AccountFeatureId.Watch },
            new HashSet<Guid>());
        await _identities.InsertSessionAsync(new AuthSession
        {
            Id = _sessionId,
            AccountId = _accountId,
            ActiveProfileId = Profile.SeedProfileId,
            TokenHash = "test",
            DeviceId = "browser",
            DeviceName = "Browser",
            Client = "dashboard",
            AuthenticationMethod = "password",
            SecurityStamp = "test",
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.AddHours(1),
        });

        using (var database = _database.CreateConnection())
        {
            database.Execute(
                "INSERT INTO vault_libraries(library_id,lookup_source,adults_confirmed_at,created_at,updated_at) VALUES(@vault,'none',@now,@now,@now);",
                new { vault = _vaultLibrary.ToString("D"), now = now.ToString("O") });
            InsertFile(database, _vaultWork, _vaultAsset, _vaultLibrary, VaultFile);
            InsertFile(database, _ordinaryWork, _ordinaryAsset, _ordinaryLibrary, OrdinaryFile);
        }

        _configuration.SaveLibraries(new LibrariesConfiguration
        {
            StorageLocations = [new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = Root + "/Media", AllowWrite = true }],
            Libraries =
            [
                Library(_vaultLibrary, "Private", VaultFolder),
                Library(_ordinaryLibrary, "Movies", Root + "/Media/Movies"),
            ],
        });
    }

    private static LibraryFolderConfig Library(Guid id, string name, string path) => new()
    {
        Id = id.ToString("D"),
        Name = name,
        Category = "Movies",
        Sources = [new LibrarySourceConfig { Id = Guid.NewGuid().ToString("D"), Path = path }],
    };

    private static void InsertFile(System.Data.IDbConnection database, Guid work, Guid asset, Guid library, string path) =>
        Dapper.SqlMapper.Execute(database,
            """
            INSERT INTO works(id,media_type,work_kind,is_catalog_only,ownership) VALUES(@work,'Videos','standalone',0,'Owned');
            INSERT INTO editions(id,work_id) VALUES(@edition,@work);
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,library_id) VALUES(@asset,@edition,@hash,@path,@library);
            """,
            new { work = GuidSql.ToBlob(work), edition = GuidSql.ToBlob(Guid.NewGuid()), asset = GuidSql.ToBlob(asset), hash = asset.ToString("N"), path, library = library.ToString("D") });

    private sealed class Resources(Guid vaultAsset, Guid vaultLibrary, Guid ordinaryAsset, Guid ordinaryLibrary) : IApplicationEventResourceResolver
    {
        public Task<ApplicationEventResourceProvenance?> ResolveAsync(Guid assetId, CancellationToken ct = default) =>
            Task.FromResult<ApplicationEventResourceProvenance?>(
                assetId == vaultAsset ? new(vaultLibrary, AccountFeatureId.Watch)
                : assetId == ordinaryAsset ? new(ordinaryLibrary, AccountFeatureId.Watch)
                : null);
    }
}
