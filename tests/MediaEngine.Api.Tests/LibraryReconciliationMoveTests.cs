using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The daily check for missing files leaves a file that Library is moving right now alone (batched write-back D11): its record is not
/// deleted while its file is between the old place and the new one, and a file that really is gone is still cleaned up.
/// </summary>
public sealed class LibraryReconciliationMoveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_reconcile_move_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly MediaFileWriteIntentRepository _intents;
    private readonly Guid _edition = Guid.NewGuid();

    public LibraryReconciliationMoveTests()
    {
        Directory.CreateDirectory(_root);
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _intents = new MediaFileWriteIntentRepository(_database);
        var work = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works(id,media_type,work_kind) VALUES(@work,'Music','child');", new { work });
        connection.Execute("INSERT INTO editions(id,work_id) VALUES(@edition,@work);", new { edition = _edition, work });
    }

    public void Dispose()
    {
        _configuration.Dispose();
        TestTemp.ReleaseSqlitePool(Path.Combine(_root, "library.db"));
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* The OS temp sweep removes it. */ }
    }

    [Fact]
    public async Task AFileInTheMiddleOfAMove_IsNotTreatedAsMissing_AndItsRecordIsKept()
    {
        var moving = AddAsset(Path.Combine(_root, "old", "song.flac"));
        var gone = AddAsset(Path.Combine(_root, "old", "really-gone.flac"));
        await RequestMoveAsync(moving);

        // The file has left its old place and Library has not followed yet: the old path no longer exists.
        var result = await CreateService().ReconcileAsync();

        Assert.Equal(1, result.MissingCount);
        Assert.True(AssetExists(moving));
        Assert.False(AssetExists(gone));
    }

    [Fact]
    public async Task WithoutAMoveInProgress_AFileThatIsGone_IsStillCleanedUp()
    {
        var asset = AddAsset(Path.Combine(_root, "old", "song.flac"));

        var result = await CreateService().ReconcileAsync();

        Assert.Equal(1, result.MissingCount);
        Assert.False(AssetExists(asset));
    }

    private Guid AddAsset(string path)
    {
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root) VALUES(@asset,@edition,@hash,@path);
            """, new { asset, edition = _edition, hash = Guid.NewGuid().ToString("N"), path });
        return asset;
    }

    private bool AssetExists(Guid asset)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM media_assets WHERE id=@asset;", new { asset }) > 0;
    }

    private async Task RequestMoveAsync(Guid asset)
    {
        using var connection = _database.CreateConnection();
        using var transaction = connection.BeginTransaction();
        MediaFileWriteIntentRepository.UpsertAsset(
            connection, transaction, asset, "library_move", "move:1", DateTimeOffset.UtcNow, requestMove: true);
        transaction.Commit();
        var claimed = (await _intents.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;
        await _intents.RecordMovePlannedAsync(claimed.Id, Path.Combine(_root, "old", "song.flac"), Path.Combine(_root, "new", "song.flac"), null);
    }

    private LibraryReconciliationService CreateService()
    {
        var collections = new CollectionRepository(_database);
        var canonicals = new CanonicalValueRepository(_database);
        var activity = new SystemActivityRepository(_database);
        var assignment = new CollectionAssignmentService(
            collections, canonicals, new WorkRepository(_database), NullLogger<CollectionAssignmentService>.Instance);
        var finalization = new CollectionFinalizationService(
            assignment, new NoParentResolver(), activity, NullLogger<CollectionFinalizationService>.Instance);
        return new LibraryReconciliationService(
            new MediaAssetRepository(_database),
            new MetadataClaimRepository(_database),
            canonicals,
            activity,
            new PersonRepository(_database),
            new ReviewQueueRepository(_database),
            collections,
            new NoEvents(),
            new WorkHierarchyMaintenanceService(_database),
            new WorkIdentityReconciliationService(_database),
            new CollectionBackfillService(collections, finalization, NullLogger<CollectionBackfillService>.Instance),
            _configuration,
            new AssetPathService(_root),
            _database,
            new NoLibraries(),
            new ScheduledTaskRunner(new ScheduledTaskRunRepository(_database), NullLogger<ScheduledTaskRunner>.Instance),
            NullLogger<LibraryReconciliationService>.Instance,
            moveInProgress: _intents);
    }

    private sealed class NoParentResolver : IParentCollectionResolver
    {
        public Task ResolveParentCollectionAsync(Guid collectionId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoEvents : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull => Task.CompletedTask;
    }

    private sealed class NoLibraries : ILibraryFolderResolver
    {
        public LibraryFolderEntry? ResolveById(string libraryId) => null;
        public LibraryFolderEntry? ResolveForPath(string absolutePath) => null;
        public ResolvedLibrarySource? ResolveSourceForPath(string absolutePath) => null;
        public string? ResolveSourcePath(string absolutePath) => null;
    }
}
