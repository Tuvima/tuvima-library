using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

public sealed class FictionalEntityWorkerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly DatabaseConnection _db;
    private readonly WorkRepository _workRepo;
    private readonly MediaAssetRepository _assetRepo;
    private readonly CanonicalValueRepository _canonicalRepo;
    private readonly CanonicalValueArrayRepository _canonicalArrayRepo;
    private readonly FictionalEntityRepository _fictionalEntityRepo;

    public FictionalEntityWorkerTests()
    {
        DapperConfiguration.Configure();

        _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_stage3_worker_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        _db = new DatabaseConnection(Path.Combine(_tempRoot, "library.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();

        _workRepo = new WorkRepository(_db);
        _assetRepo = new MediaAssetRepository(_db);
        _canonicalRepo = new CanonicalValueRepository(_db);
        _canonicalArrayRepo = new CanonicalValueArrayRepository(_db);
        _fictionalEntityRepo = new FictionalEntityRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task EnrichAsync_UsesParentWorkCanonicals_ToCreateEntitiesAndWorkLinks()
    {
        var showWorkId = await _workRepo.InsertParentAsync(MediaType.TV, "tv:the-expanse", null, null);
        var seasonWorkId = await _workRepo.InsertChildAsync(MediaType.TV, showWorkId, 1);
        var episodeWorkId = await _workRepo.InsertChildAsync(MediaType.TV, seasonWorkId, 2);
        var assetId = await SeedAssetForExistingWorkAsync(episodeWorkId, Path.Combine("TV", "The Expanse", "Season 01", "The Expanse - S01E02.mkv"));

        await SeedCanonicalsAsync(
            showWorkId,
            (MetadataFieldConstants.Title, "The Expanse"),
            ("wikidata_qid", "QSHOW"));
        await _canonicalArrayRepo.SetValuesAsync(showWorkId, MetadataFieldConstants.FictionalUniverse,
        [
            new CanonicalArrayEntry { Ordinal = 0, Value = "The Expanse universe", ValueQid = "QUNIVERSE" },
        ]);
        await _canonicalArrayRepo.SetValuesAsync(showWorkId, MetadataFieldConstants.Characters,
        [
            new CanonicalArrayEntry { Ordinal = 0, Value = "James Holden", ValueQid = "QCHAR1" },
            new CanonicalArrayEntry { Ordinal = 1, Value = "Chrisjen Avasarala", ValueQid = "QCHAR2" },
        ]);
        await _canonicalArrayRepo.SetValuesAsync(showWorkId, MetadataFieldConstants.NarrativeLocation,
        [
            new CanonicalArrayEntry { Ordinal = 0, Value = "Rocinante", ValueQid = "QLOC1" },
        ]);
        await _canonicalArrayRepo.SetValuesAsync(showWorkId, MetadataFieldConstants.NarrativeEvent,
        [
            new CanonicalArrayEntry { Ordinal = 0, Value = "The Canterbury incident", ValueQid = "QEVENT1" },
        ]);
        await _canonicalArrayRepo.SetValuesAsync(showWorkId, MetadataFieldConstants.NarrativeObject,
        [
            new CanonicalArrayEntry { Ordinal = 0, Value = "The protomolecule", ValueQid = "QOBJECT1" },
        ]);

        var harvesting = new RecordingMetadataHarvestingService();
        var resolver = new NarrativeRootResolver(
            _canonicalRepo,
            _canonicalArrayRepo,
            new NarrativeRootRepository(_db),
            new CollectionRepository(_db),
            new QidLabelRepository(_db),
            new SystemActivityRepository(_db),
            NullLogger<NarrativeRootResolver>.Instance);
        var recursiveService = new RecursiveFictionalEntityService(
            _fictionalEntityRepo,
            harvesting,
            NullLogger<RecursiveFictionalEntityService>.Instance);
        var worker = new FictionalEntityWorker(
            resolver,
            recursiveService,
            _canonicalRepo,
            _canonicalArrayRepo,
            _workRepo,
            NullLogger<FictionalEntityWorker>.Instance);

        await worker.EnrichAsync(assetId, "QSHOW", CancellationToken.None);

        Assert.Equal(5, await _fictionalEntityRepo.CountAsync());

        var linkedEntities = await _fictionalEntityRepo.GetByWorkQidAsync("QSHOW");
        Assert.Equal(5, linkedEntities.Count);
        Assert.Contains(linkedEntities, entity => entity.WikidataQid == "QCHAR1");
        Assert.Contains(linkedEntities, entity => entity.WikidataQid == "QCHAR2");
        Assert.Contains(linkedEntities, entity => entity.WikidataQid == "QLOC1");
        Assert.Contains(linkedEntities, entity => entity.WikidataQid == "QEVENT1" && entity.EntitySubType == FictionalEntityType.Event);
        Assert.Contains(linkedEntities, entity => entity.WikidataQid == "QOBJECT1" && entity.EntitySubType == FictionalEntityType.Object);

        Assert.Equal(5, harvesting.Requests.Count);
        Assert.All(harvesting.Requests, request => Assert.Equal(MediaType.Unknown, request.MediaType));
    }

    [Fact]
    public async Task NarrativeRootResolver_DoesNotPromoteCharacterIdentityToUniverse()
    {
        var workId = await _workRepo.InsertParentAsync(MediaType.Movies, "movie:batman", null, null);
        await SeedCanonicalsAsync(
            workId,
            ("wikidata_qid", "Q200"),
            (MetadataFieldConstants.FictionalUniverse, "Batman"),
            ($"{MetadataFieldConstants.FictionalUniverse}_qid", "Q2695156"));
        await _canonicalArrayRepo.SetValuesAsync(workId, MetadataFieldConstants.Characters,
        [
            new CanonicalArrayEntry { Ordinal = 0, Value = "Batman", ValueQid = "Q2695156" },
        ]);
        var resolver = new NarrativeRootResolver(
            _canonicalRepo,
            _canonicalArrayRepo,
            new NarrativeRootRepository(_db),
            new CollectionRepository(_db),
            new QidLabelRepository(_db),
            new SystemActivityRepository(_db),
            NullLogger<NarrativeRootResolver>.Instance);

        var root = await resolver.ResolveAsync(workId);

        Assert.Null(root);
    }

    private async Task<Guid> SeedAssetForExistingWorkAsync(Guid workId, string relativeFilePath)
    {
        var editionId = Guid.NewGuid();
        var filePath = Path.Combine(_tempRoot, relativeFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, [0, 1, 2]);

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO editions (id, work_id) VALUES (@EditionId, @WorkId);",
            new
            {
                EditionId = editionId,
                WorkId = workId,
            });

        var assetId = Guid.NewGuid();
        await _assetRepo.InsertAsync(new MediaAsset
        {
            Id = assetId,
            EditionId = editionId,
            ContentHash = $"hash_{assetId:N}",
            FilePathRoot = filePath,
            Status = AssetStatus.Normal,
        });

        return assetId;
    }

    private Task SeedCanonicalsAsync(Guid entityId, params (string Key, string Value)[] values)
    {
        return _canonicalRepo.UpsertBatchAsync(values
            .Select(value => new CanonicalValue
            {
                EntityId = entityId,
                Key = value.Key,
                Value = value.Value,
                LastScoredAt = DateTimeOffset.UtcNow,
            })
            .ToList());
    }

    private sealed class RecordingMetadataHarvestingService : IMetadataHarvestingService
    {
        public List<HarvestRequest> Requests { get; } = [];

        public int PendingCount => Requests.Count;

        public ValueTask EnqueueAsync(HarvestRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return ValueTask.CompletedTask;
        }

        public Task ProcessSynchronousAsync(HarvestRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }
}
