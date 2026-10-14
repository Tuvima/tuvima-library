using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class ReviewQueueReadServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public ReviewQueueReadServiceTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_review_read_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task PendingReviews_IncludeAssetRowsAndUseSameEligibilityAsCount()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        SeedMediaAsset(workId, editionId, assetId);
        SeedCanonicalValue(assetId, "title", "Asset Level Review Movie");
        SeedCanonicalValue(assetId, "media_type", "Movies");
        SeedCanonicalValue(assetId, "tmdb_id", "603");
        SeedCanonicalValue(workId, "wikidata_qid", "Q83495");

        var repository = new ReviewQueueRepository(_db);
        var assetReviewId = Guid.NewGuid();
        var workReviewId = Guid.NewGuid();
        await repository.InsertAsync(Review(assetReviewId, assetId, "MediaAsset", "Asset needs confirmation"));
        await repository.InsertAsync(Review(workReviewId, workId, "Work", "Work needs confirmation"));
        await repository.InsertAsync(Review(Guid.NewGuid(), Guid.NewGuid(), "MediaAsset", "Stale missing asset"));

        var service = new ReviewQueueReadService(_db);

        var pending = await service.GetPendingAsync(10, CancellationToken.None);
        var count = await service.GetPendingCountAsync(CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, item => item.Id == assetReviewId && item.EntityId == assetId);
        Assert.Contains(pending, item => item.Id == workReviewId && item.EntityId == workId);

        var assetReview = Assert.Single(pending, item => item.Id == assetReviewId);
        Assert.Equal("Asset Level Review Movie", assetReview.EntityTitle);
        Assert.Equal("Movies", assetReview.MediaType);
        Assert.Equal("603", assetReview.BridgeIdentifiers["tmdb_id"]);
        Assert.Equal("Q83495", assetReview.BridgeIdentifiers["wikidata_qid"]);
        Assert.NotNull(assetReview.SourceOperationId);
        Assert.Equal("identity.retail-match", assetReview.SourceCapabilityId);
    }

    [Fact]
    public async Task PendingReasonCounts_MaterializeSqliteCountValues()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        SeedMediaAsset(workId, editionId, assetId);

        var repository = new ReviewQueueRepository(_db);
        await repository.InsertAsync(Review(Guid.NewGuid(), assetId, "MediaAsset", "Asset needs confirmation"));

        var service = new ReviewQueueReadService(_db);

        var reasons = await service.GetPendingReasonCountsAsync(CancellationToken.None);

        var reason = Assert.Single(reasons);
        Assert.Equal(ReviewTrigger.MetadataConflict, reason.Trigger);
        Assert.Equal("Asset needs confirmation", reason.Detail);
        Assert.Equal(1, reason.Count);
    }

    [Fact]
    public async Task PendingReviews_MarkFilesThatAreStillInTheImportFolder()
    {
        var importFolder = Path.Combine(Path.GetTempPath(), "tuvima-review-import");
        var inImport = Guid.NewGuid();
        var inLibrary = Guid.NewGuid();
        SeedMediaAsset(Guid.NewGuid(), Guid.NewGuid(), inImport, Path.Combine(importFolder, "Movies", "mystery.mkv"));
        SeedMediaAsset(Guid.NewGuid(), Guid.NewGuid(), inLibrary, Path.Combine(importFolder + "-library", "Movies", "known.mkv"));
        var repository = new ReviewQueueRepository(_db);
        var importReview = Guid.NewGuid();
        var libraryReview = Guid.NewGuid();
        await repository.InsertAsync(Review(importReview, inImport, "MediaAsset", "Unmatched"));
        await repository.InsertAsync(Review(libraryReview, inLibrary, "MediaAsset", "Unmatched"));

        var withImport = new ReviewQueueReadService(_db, new FixedImportFolder(importFolder));
        var pending = await withImport.GetPendingAsync(10, CancellationToken.None);

        Assert.True(Assert.Single(pending, item => item.Id == importReview).FromImportFolder);
        Assert.False(Assert.Single(pending, item => item.Id == libraryReview).FromImportFolder);
        Assert.True((await withImport.GetByIdAsync(importReview, CancellationToken.None))!.FromImportFolder);

        // With no Import folder set, nothing carries the badge.
        var withoutImport = new ReviewQueueReadService(_db, new FixedImportFolder(null));
        Assert.All(await withoutImport.GetPendingAsync(10, CancellationToken.None), item => Assert.False(item.FromImportFolder));
    }

    [Fact]
    public async Task PendingReviews_MarkFilesInAManagedFoldersFromMergeFolder()
    {
        var fixture = new FromMergeFixture(_db);
        var unidentified = fixture.SeedReviewed(
            Path.Combine(fixture.ManagedRoot, FolderMergeFolders.HoldingFolderName, "Old drive", "Movies", "mystery.mkv"),
            ReviewTrigger.StagedUnidentifiable);
        var alreadyInReview = fixture.SeedReviewed(
            Path.Combine(fixture.ManagedRoot, FolderMergeFolders.HoldingFolderName, "Old drive", "known.mkv"),
            ReviewTrigger.MetadataConflict);
        var ordinary = fixture.SeedReviewed(Path.Combine(fixture.ManagedRoot, "Movies", "Dune (2021)", "Dune.mkv"), ReviewTrigger.MetadataConflict);
        var lookalike = fixture.SeedReviewed(
            Path.Combine(fixture.ManagedRoot, "Movies", FolderMergeFolders.HoldingFolderName, "inner.mkv"), ReviewTrigger.MetadataConflict);
        var siblingName = fixture.SeedReviewed(
            Path.Combine(fixture.ManagedRoot, FolderMergeFolders.HoldingFolderName + " later", "other.mkv"), ReviewTrigger.MetadataConflict);
        var asIs = fixture.SeedReviewed(
            Path.Combine(fixture.AsIsRoot, FolderMergeFolders.HoldingFolderName, "kept.mkv"), ReviewTrigger.MetadataConflict);

        var service = new ReviewQueueReadService(_db, configuration: fixture.Config);
        var pending = await service.GetPendingAsync(20, CancellationToken.None);

        Assert.True(Assert.Single(pending, item => item.Id == unidentified).FromMerge);
        Assert.True(Assert.Single(pending, item => item.Id == alreadyInReview).FromMerge);
        Assert.False(Assert.Single(pending, item => item.Id == ordinary).FromMerge);
        Assert.False(Assert.Single(pending, item => item.Id == lookalike).FromMerge);
        Assert.False(Assert.Single(pending, item => item.Id == siblingName).FromMerge);
        Assert.False(Assert.Single(pending, item => item.Id == asIs).FromMerge);
        Assert.True((await service.GetByIdAsync(unidentified, CancellationToken.None))!.FromMerge);
        Assert.False((await service.GetByIdAsync(ordinary, CancellationToken.None))!.FromMerge);

        // Without folder settings nothing can be told apart, so nothing carries the badge.
        var withoutConfig = new ReviewQueueReadService(_db);
        Assert.All(await withoutConfig.GetPendingAsync(20, CancellationToken.None), item => Assert.False(item.FromMerge));
    }

    [Fact]
    public async Task TheFromMergeBadge_FollowsTheFolderTheMergeOrganiserPutsFilesIn()
    {
        var fixture = new FromMergeFixture(_db);
        var libraries = fixture.Config.LoadLibraries();
        var context = FolderMergeContext.TryCreateForPlan(
            libraries, Guid.Parse(fixture.SourceId), fixture.TargetId, LibraryMergeModes.Copy, out var check);
        Assert.True(check.IsOk, check.Message);
        Assert.NotNull(context);

        var heldBack = fixture.SeedReviewed(
            context.HoldingPathFor(Path.Combine(fixture.SourceRoot, "Films", "unknown.mkv")), ReviewTrigger.StagedUnidentifiable);

        var service = new ReviewQueueReadService(_db, configuration: fixture.Config);
        Assert.True((await service.GetByIdAsync(heldBack, CancellationToken.None))!.FromMerge);
    }

    [Fact]
    public void TheHoldingFolderName_ComesFromOneSharedConstant()
    {
        Assert.Equal("From merge", FolderMergeFolders.HoldingFolderName);
        Assert.Null(typeof(FolderMergeContext).GetField("HoldingFolderName"));

        var root = FindRepoRoot();
        var merge = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Ingestion", "Organise", "FolderMergeModels.cs"));
        var review = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Api", "Services", "ReadServices", "ReviewQueueReadService.cs"));
        Assert.Contains("FolderMergeFolders.HoldingFolderName", merge, StringComparison.Ordinal);
        Assert.Contains("FolderMergeFolders.IsInsideHoldingFolder", review, StringComparison.Ordinal);
        Assert.DoesNotContain("\"From merge\"", merge, StringComparison.Ordinal);
        Assert.DoesNotContain("\"From merge\"", review, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    /// <summary>Two managed folders and one as-is folder saved through the real settings loader, with a way to seed reviewed files.</summary>
    private sealed class FromMergeFixture
    {
        private readonly DatabaseConnection _db;
        private readonly ReviewQueueRepository _reviews;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-review-from-merge", Guid.NewGuid().ToString("N"));

        public FromMergeFixture(DatabaseConnection db)
        {
            _db = db;
            _reviews = new ReviewQueueRepository(db);
            ManagedRoot = Path.Combine(_root, "media", "managed");
            SourceRoot = Path.Combine(_root, "media", "other-managed");
            AsIsRoot = Path.Combine(_root, "media", "as-is");
            SourceId = Guid.NewGuid().ToString("D");
            TargetId = Guid.NewGuid().ToString("D");
            Config = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
            Config.SaveLibraries(new LibrariesConfiguration
            {
                StorageLocations =
                [
                    new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = Path.Combine(_root, "media"), AllowWrite = true },
                    new ServerStorageLocationConfig { Id = "view", Label = "View", Path = Path.Combine(_root, "view"), AllowWrite = true },
                ],
                ViewStorage = new ViewStorageConfig { StorageLocationId = "view", RelativeRoot = "." },
                Libraries =
                [
                    new LibraryFolderConfig
                    {
                        Id = Guid.NewGuid().ToString("D"),
                        Name = "Movies",
                        Category = "Movies",
                        MediaTypes = ["Movies"],
                        PrimaryDestinationSourceId = TargetId,
                        Sources =
                        [
                            new LibrarySourceConfig { Id = TargetId, Path = ManagedRoot, ManagementMode = LibrarySourceManagementModes.ManagedByTuvima, AccessMode = LibrarySourceAccessModes.Writable },
                            new LibrarySourceConfig { Id = SourceId, Path = SourceRoot, ManagementMode = LibrarySourceManagementModes.ManagedByTuvima, AccessMode = LibrarySourceAccessModes.Writable },
                            new LibrarySourceConfig { Id = Guid.NewGuid().ToString("D"), Path = AsIsRoot, ManagementMode = LibrarySourceManagementModes.ExistingLibrary },
                        ],
                    },
                ],
            });
        }

        public ConfigurationDirectoryLoader Config { get; }
        public string ManagedRoot { get; }
        public string SourceRoot { get; }
        public string AsIsRoot { get; }
        public string SourceId { get; }
        public string TargetId { get; }

        /// <summary>Seeds a catalogued file at <paramref name="path"/> that is waiting in Review, and returns the review item's id.</summary>
        public Guid SeedReviewed(string path, string trigger)
        {
            var assetId = SeedAsset(path);
            var reviewId = Guid.NewGuid();
            _reviews.InsertAsync(new ReviewQueueEntry
            {
                Id = reviewId,
                EntityId = assetId,
                EntityType = "MediaAsset",
                Trigger = trigger,
                Status = ReviewStatus.Pending,
                Detail = "Waiting",
                CreatedAt = DateTimeOffset.UtcNow,
                ReviewReadyAt = DateTimeOffset.UtcNow,
                AutomationCompletedAt = DateTimeOffset.UtcNow,
            }).GetAwaiter().GetResult();
            return reviewId;
        }

        private Guid SeedAsset(string path)
        {
            var workId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO works (id, media_type, work_kind)
                VALUES ($workId, 'Movies', 'standalone');
                INSERT INTO editions (id, work_id)
                VALUES ($editionId, $workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES ($assetId, $editionId, $hash, $path);
                """;
            AddGuid(cmd, "$workId", workId);
            AddGuid(cmd, "$editionId", editionId);
            AddGuid(cmd, "$assetId", assetId);
            cmd.Parameters.AddWithValue("$hash", $"review-{assetId:N}");
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
            return assetId;
        }
    }

    private sealed class FixedImportFolder(string? path) : IImportFolderService
    {
        public string? GetImportPath() => path;
        public bool IsInImportFolder(string candidate) => throw new NotSupportedException();
        public bool IsWorkingPath(string candidate) => throw new NotSupportedException();
        public MediaType? ResolveTypePrior(string candidate) => throw new NotSupportedException();
        public string? ResolveTypeFolder(string candidate) => throw new NotSupportedException();
        public MediaEngine.Ingestion.Models.LibraryFolderEntry? ResolveLibraryForType(MediaType mediaType) => throw new NotSupportedException();
        public ImportDestination ResolveDestination(MediaType mediaType, long requiredBytes = 0) => throw new NotSupportedException();
        public string DuplicatePolicy => throw new NotSupportedException();
        public FileSourceMutationPolicy? CreateImportPolicy(bool allowDelete = false) => throw new NotSupportedException();
        public IReadOnlyList<string> EnsureSubfolders(bool createImportFolder = false) => throw new NotSupportedException();
        public void RemoveEmptyFolders(string startFolder) => throw new NotSupportedException();
    }

    private static ReviewQueueEntry Review(Guid id, Guid entityId, string entityType, string detail) => new()
    {
        Id = id,
        EntityId = entityId,
        EntityType = entityType,
        Trigger = ReviewTrigger.MetadataConflict,
        Status = ReviewStatus.Pending,
        Detail = detail,
        CreatedAt = DateTimeOffset.UtcNow,
        ReviewReadyAt = DateTimeOffset.UtcNow,
        AutomationCompletedAt = DateTimeOffset.UtcNow,
        SourceOperationId = Guid.NewGuid(),
        SourceCapabilityId = "identity.retail-match",
    };

    private void SeedMediaAsset(Guid workId, Guid editionId, Guid assetId, string? path = null)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO works (id, media_type, work_kind)
            VALUES ($workId, 'Movies', 'standalone');
            INSERT INTO editions (id, work_id)
            VALUES ($editionId, $workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES ($assetId, $editionId, $hash, $path);
            """;
        AddGuid(cmd, "$workId", workId);
        AddGuid(cmd, "$editionId", editionId);
        AddGuid(cmd, "$assetId", assetId);
        cmd.Parameters.AddWithValue("$hash", $"review-{assetId:N}");
        cmd.Parameters.AddWithValue("$path", path ?? $"C:/library/{assetId:N}.mkv");
        cmd.ExecuteNonQuery();
    }

    private void SeedCanonicalValue(Guid entityId, string key, string value)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES ($entityId, $key, $value, $now);
            """;
        AddGuid(cmd, "$entityId", entityId);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void AddGuid(SqliteCommand command, string name, Guid value) =>
        command.Parameters.Add(name, SqliteType.Blob).Value = GuidSql.ToBlob(value);
}
