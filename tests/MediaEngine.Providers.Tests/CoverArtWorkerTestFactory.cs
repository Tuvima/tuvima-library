using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

internal static class CoverArtWorkerTestFactory
{
    public static CoverArtWorker Create(
        ICanonicalValueRepository canonicalRepo,
        IWorkRepository workRepo)
        => new(
            new NoOpMediaAssetRepository(),
            canonicalRepo,
            workRepo,
            new NoOpImageCacheRepository(),
            new NoOpHttpClientFactory(),
            new AssetPathService(Path.Combine(Path.GetTempPath(), "tuvima-provider-tests")),
            NullLogger<CoverArtWorker>.Instance);

    private sealed class NoOpMediaAssetRepository : IMediaAssetRepository
    {
        public Task<MediaAsset?> FindByHashAsync(string contentHash, CancellationToken ct = default) => Task.FromResult<MediaAsset?>(null);
        public Task<MediaAsset?> FindByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<MediaAsset?>(null);
        public Task<bool> InsertAsync(MediaAsset asset, CancellationToken ct = default) => Task.FromResult(false);
        public Task<MediaAsset?> FindByPathRootAsync(string pathRoot, CancellationToken ct = default) => Task.FromResult<MediaAsset?>(null);
        public Task UpdateStatusAsync(Guid id, AssetStatus status, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateFilePathAsync(Guid id, string newPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> MarkPresentedAsync(Guid id, DateTimeOffset presentedAt, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> UpdateContentHashAsync(Guid id, string contentHash, CancellationToken ct = default) => Task.FromResult(false);
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MediaAsset>> ListByStatusAsync(AssetStatus status, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MediaAsset>>([]);
        public Task<MediaAsset?> FindFirstByWorkIdAsync(Guid workId, CancellationToken ct = default, Guid? profileId = null) => Task.FromResult<MediaAsset?>(null);
        public Task<HashSet<string>> GetAllFilePathsAsync(CancellationToken ct = default) => Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        public Task<IReadOnlyList<StaleRetagAsset>> GetStaleForRetagAsync(IReadOnlyDictionary<string, string> expectedHashesByMediaType, int batchSize, long nowEpochSeconds, CancellationToken ct = default, Guid? afterAssetId = null) => Task.FromResult<IReadOnlyList<StaleRetagAsset>>([]);
        public Task UpdateWritebackHashAsync(Guid assetId, string newHash, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkWritebackUnverifiedAsync(Guid assetId, string expectedHash, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkWritebackUnsupportedAsync(Guid assetId, string expectedHash, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task ScheduleRetagRetryAsync(Guid assetId, long nextRetryAtEpochSeconds, string error, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkRetagFailedAsync(Guid assetId, string error, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLibraryIdAsync(Guid id, string? libraryId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkOrphanedAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearOrphanedAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpImageCacheRepository : IImageCacheRepository
    {
        public Task<string?> FindByHashAsync(string contentHash, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task InsertAsync(string contentHash, string filePath, string? sourceUrl = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> IsUserOverrideAsync(string contentHash, CancellationToken ct = default) => Task.FromResult(false);
        public Task<string?> FindBySourceUrlAsync(string sourceUrl, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetUserOverrideAsync(string contentHash, bool isOverride, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetPerceptualHashAsync(string contentHash, ulong phash, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ulong?> GetPerceptualHashAsync(string contentHash, CancellationToken ct = default) => Task.FromResult<ulong?>(null);
    }

    private sealed class NoOpHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
