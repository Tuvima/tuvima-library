using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The tag refresh sweep leaves a file that already waits on the write list alone (batched write-back D2): asking again would bump
/// its entry and push it past the run that is about to write it.
/// </summary>
public sealed class RetagSweepWaitingFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_retag_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly WritebackConfigState _hashes;
    private readonly Guid _edition = Guid.NewGuid();

    public RetagSweepWaitingFilesTests()
    {
        Directory.CreateDirectory(_root);
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _configuration.SaveConfig(string.Empty, "writeback-fields", new MediaEngine.Domain.Configuration.WritebackFieldsConfiguration { Music = ["title"] });
        _hashes = new WritebackConfigState(_configuration, NullLogger<WritebackConfigState>.Instance);
        var work = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works(id,media_type,work_kind) VALUES(@work,'Music','child');", new { work });
        connection.Execute("INSERT INTO editions(id,work_id) VALUES(@edition,@work);", new { edition = _edition, work });
    }

    [Fact]
    public async Task AFileAlreadyWaitingOnTheWriteList_IsNotAskedForAgain_WhileOtherStaleFilesAre()
    {
        var waiting = AddStaleAsset();
        var notWaiting = AddStaleAsset();
        var intents = new MediaFileWriteIntentRepository(_database);
        await intents.EnqueueAssetsAsync([waiting], "auto_match");
        var generationBefore = Generation(waiting);
        var queue = new SpyQueue();
        var worker = new RetagSweepWorker(
            new MediaAssetRepository(_database),
            queue,
            intents,
            new ReviewQueueRepository(_database),
            _configuration,
            new NoEvents(),
            _hashes,
            new ScheduledTaskRunner(new ScheduledTaskRunRepository(_database), NullLogger<ScheduledTaskRunner>.Instance),
            NullLogger<RetagSweepWorker>.Instance);

        await worker.RunSweepAsync(CancellationToken.None);

        Assert.Equal([notWaiting], queue.Asked);
        Assert.Equal(generationBefore, Generation(waiting));
    }

    private Guid AddStaleAsset()
    {
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,writeback_fields_hash)
            VALUES(@asset,@edition,@hash,@path,'an-older-tag-setting');
            """, new { asset, edition = _edition, hash = Guid.NewGuid().ToString("N"), path = $"C:/music/{asset:N}.flac" });
        return asset;
    }

    private long Generation(Guid asset)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<long>("SELECT generation FROM media_file_write_intents WHERE asset_id = @asset;", new { asset });
    }

    public void Dispose()
    {
        _hashes.Dispose();
        _configuration.Dispose();
        TestTemp.ReleaseSqlitePool(Path.Combine(_root, "library.db"));
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }

    private sealed class SpyQueue : IFileWriteQueue
    {
        public List<Guid> Asked { get; } = [];

        public Task EnqueueAssetAsync(Guid assetId, string trigger, CancellationToken ct = default)
        {
            Asked.Add(assetId);
            return Task.CompletedTask;
        }

        public Task EnqueueAssetsAsync(IReadOnlyCollection<Guid> assetIds, string trigger, CancellationToken ct = default)
        {
            Asked.AddRange(assetIds);
            return Task.CompletedTask;
        }

        public Task EnqueueViewItemAsync(Guid localItemId, string trigger, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoEvents : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull => Task.CompletedTask;
    }
}
