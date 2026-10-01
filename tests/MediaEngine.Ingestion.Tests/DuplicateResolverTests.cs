using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;

namespace MediaEngine.Ingestion.Tests;

public sealed class DuplicateResolverTests
{
    [Fact]
    public async Task ResolveAsync_PrefersSamePathBeforeHashLookup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-same-path-{Guid.NewGuid():N}.epub");
        File.WriteAllText(path, "changed bytes after writeback");

        try
        {
            var existing = new MediaAsset
            {
                Id = Guid.NewGuid(),
                EditionId = Guid.NewGuid(),
                ContentHash = "old-hash",
                FilePathRoot = path,
                Status = AssetStatus.Normal,
            };
            var repository = new PathFirstAssetRepository(existing);
            var resolver = new DuplicateResolver(repository);
            var candidate = new IngestionCandidate
            {
                Path = path,
                EventType = FileEventType.Created,
                DetectedAt = DateTimeOffset.UtcNow,
                ReadyAt = DateTimeOffset.UtcNow,
            };

            var result = await resolver.ResolveAsync(candidate, "new-hash");

            Assert.Equal(DuplicateResolutionKind.SamePathRedetected, result.Kind);
            Assert.Equal(existing.Id, result.ExistingAsset!.Id);
            Assert.Equal(1, repository.PathLookupCount);
            Assert.Equal(0, repository.HashLookupCount);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private sealed class PathFirstAssetRepository(MediaAsset existingAsset) : IMediaAssetRepository
    {
        public int PathLookupCount { get; private set; }
        public int HashLookupCount { get; private set; }

        public Task<MediaAsset?> FindByPathRootAsync(string pathRoot, CancellationToken ct = default)
        {
            PathLookupCount++;
            return Task.FromResult<MediaAsset?>(string.Equals(pathRoot, existingAsset.FilePathRoot, StringComparison.OrdinalIgnoreCase)
                ? existingAsset
                : null);
        }

        public Task<MediaAsset?> FindByHashAsync(string contentHash, CancellationToken ct = default)
        {
            HashLookupCount++;
            return Task.FromResult<MediaAsset?>(null);
        }

        public Task<MediaAsset?> FindByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> InsertAsync(MediaAsset asset, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateStatusAsync(Guid id, AssetStatus status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateFilePathAsync(Guid id, string newPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> MarkPresentedAsync(Guid id, DateTimeOffset presentedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UpdateContentHashAsync(Guid id, string contentHash, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MediaAsset>> ListByStatusAsync(AssetStatus status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MediaAsset?> FindFirstByWorkIdAsync(Guid workId, CancellationToken ct = default, Guid? profileId = null) => throw new NotSupportedException();
        public Task<HashSet<string>> GetAllFilePathsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StaleRetagAsset>> GetStaleForRetagAsync(IReadOnlyDictionary<string, string> expectedHashesByMediaType, int batchSize, long nowEpochSeconds, CancellationToken ct = default, Guid? afterAssetId = null) => throw new NotSupportedException();
        public Task UpdateWritebackHashAsync(Guid assetId, string newHash, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkWritebackUnverifiedAsync(Guid assetId, string expectedHash, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkWritebackUnsupportedAsync(Guid assetId, string expectedHash, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ScheduleRetagRetryAsync(Guid assetId, long nextRetryAtEpochSeconds, string error, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkRetagFailedAsync(Guid assetId, string error, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetLibraryIdAsync(Guid id, string? libraryId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkOrphanedAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearOrphanedAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
