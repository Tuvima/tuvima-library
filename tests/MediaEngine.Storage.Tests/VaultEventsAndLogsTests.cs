using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The activity log, the operation announcements, the queue screen and the operation counts never show a file in a Vault library
/// (Videos spec D17, D33, WP17). Real temporary SQLite; the Vault folder list is process wide, so these cases share one class.
/// </summary>
public sealed class VaultEventsAndLogsTests : IDisposable
{
    private static readonly string VaultFolder = "/mnt/private-" + Guid.NewGuid().ToString("N") + "/Clips";
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_vault_events_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly MediaOperationRepository _operations;
    private readonly VaultLibraryRepository _vault;

    public VaultEventsAndLogsTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _operations = new MediaOperationRepository(_db);
        _vault = new VaultLibraryRepository(_db);
        VaultLocationGuard.Replace([VaultFolder]);
    }

    public void Dispose()
    {
        VaultLocationGuard.Replace([]);
        try { _db.Dispose(); } catch (ObjectDisposedException) { }
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task AnActivityNoteThatNamesAVaultFile_IsNeverWritten_AndAnOrdinaryOneIs()
    {
        var activity = new SystemActivityRepository(_db);

        await activity.LogAsync(new SystemActivityEntry { ActionType = SystemActionType.FileHashed, Detail = $"Hashed {VaultFolder}/one.mkv" });
        await activity.LogAsync(new SystemActivityEntry
        {
            ActionType = SystemActionType.FileHashed,
            ChangesJson = $"{{\"path\":\"{VaultFolder}/one.mkv\"}}",
        });
        await activity.LogAsync(new SystemActivityEntry { ActionType = SystemActionType.FileHashed, Detail = "Hashed /mnt/movies/Dune.mkv" });

        Assert.Equal(1, await activity.CountAsync());
    }

    [Fact]
    public async Task AnOperationAboutAVaultFile_IsNotAnnounced_ButAnOrdinaryOneIs()
    {
        var vaultAsset = BuildAsset(inVault: true);
        var ordinaryAsset = BuildAsset(inVault: false);
        var publisher = new SpyPublisher();
        var tracker = new MediaOperationTracker(
            _operations, new MediaOperationEventRepository(_db), publisher, NullLogger<MediaOperationTracker>.Instance, _vault);

        await tracker.EnsureQueuedAsync(Operation("vault-one", vaultAsset));
        Assert.Empty(publisher.Names);

        await tracker.EnsureQueuedAsync(Operation("ordinary-one", ordinaryAsset));
        Assert.Single(publisher.Names);
    }

    [Fact]
    public async Task TheQueueScreen_NeverListsAnOperationAboutAVaultFile()
    {
        var vaultAsset = BuildAsset(inVault: true);
        var ordinaryAsset = BuildAsset(inVault: false);
        var vaultOperation = await _operations.EnsureAsync(Operation("vault-two", vaultAsset));
        var ordinaryOperation = await _operations.EnsureAsync(Operation("ordinary-two", ordinaryAsset));
        await _operations.AttachEntityAsync(vaultOperation.Id, vaultAsset, "MediaAsset");
        await _operations.AttachEntityAsync(ordinaryOperation.Id, ordinaryAsset, "MediaAsset");

        var rows = await new IngestionQueueProgressRepository(_db).ReadAsync(Now.AddHours(-1), Now.AddHours(1), []);

        Assert.Equal([ordinaryOperation.Id], rows.Select(row => row.OperationId));
    }

    [Fact]
    public async Task TheOperationCounts_LeaveOutOperationsAboutVaultFiles()
    {
        var vaultAsset = BuildAsset(inVault: true);
        var ordinaryAsset = BuildAsset(inVault: false);
        var vaultOperation = await _operations.EnsureAsync(Operation("vault-three", vaultAsset));
        var ordinaryOperation = await _operations.EnsureAsync(Operation("ordinary-three", ordinaryAsset));
        await _operations.AttachEntityAsync(vaultOperation.Id, vaultAsset, "MediaAsset");
        await _operations.AttachEntityAsync(ordinaryOperation.Id, ordinaryAsset, "MediaAsset");

        var summary = await _operations.GetSummaryAsync();

        Assert.Equal(1, summary.Values.Sum());
    }

    private static MediaOperation Operation(string key, Guid assetId) => new()
    {
        EntityId = assetId,
        OperationType = MediaOperationType.IngestionFile,
        OperationKind = MediaOperationKind.Ingestion,
        SourcePath = $"/watch/{key}.mkv",
        Status = MediaOperationStatus.Queued,
        Stage = MediaOperationStage.Queued,
        QueueName = "ingestion",
        IdempotencyKey = $"ingestion:file:{key}",
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private Guid BuildAsset(bool inVault)
    {
        var libraryId = inVault ? "vault-lib" : "movies-lib";
        using var conn = _db.CreateConnection();
        if (inVault && conn.ExecuteScalar<int>("SELECT COUNT(*) FROM vault_libraries WHERE library_id = @libraryId;", new { libraryId }) == 0)
        {
            conn.Execute(
                """
                INSERT INTO vault_libraries (library_id, lookup_source, adults_confirmed_at, created_at, updated_at)
                VALUES (@libraryId, 'none', @now, @now, @now);
                """,
                new { libraryId, now = Now.ToString("O") });
        }

        var collectionId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        conn.Execute(
            """
            INSERT INTO collections (id, created_at) VALUES (@collectionId, datetime('now'));
            INSERT INTO works (id, collection_id, media_type) VALUES (@workId, @collectionId, 'Videos');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id)
                VALUES (@assetId, @editionId, @hash, @filePath, 'Normal', @libraryId);
            """,
            new { collectionId, workId, editionId, assetId, libraryId, hash = $"hash_{assetId:N}", filePath = $"/library/{assetId:N}.bin" });
        return assetId;
    }

    private sealed class SpyPublisher : IEventPublisher
    {
        public List<string> Names { get; } = [];

        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
        {
            Names.Add(eventName);
            return Task.CompletedTask;
        }
    }
}
