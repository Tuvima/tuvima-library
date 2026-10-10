using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class ImageCacheRepositoryTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly DatabaseConnection _database;

    public ImageCacheRepositoryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_image_cache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _database = new DatabaseConnection(Path.Combine(_tempRoot, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDirectory(_tempRoot);
    }

    [Fact]
    public async Task InsertAsync_RecordsEverySourceUrlForSharedContent()
    {
        var repository = new ImageCacheRepository(_database);
        var cachedPath = Path.Combine(_tempRoot, "cached.jpg");
        await File.WriteAllBytesAsync(cachedPath, [1, 2, 3, 4]);

        const string hash = "shared-content-hash";
        await repository.InsertAsync(hash, cachedPath, "https://images.test/first.jpg");
        await repository.InsertAsync(hash, Path.Combine(_tempRoot, "unused.jpg"), "https://images.test/second.jpg");

        Assert.Equal(cachedPath, await repository.FindBySourceUrlAsync("https://images.test/first.jpg"));
        Assert.Equal(cachedPath, await repository.FindBySourceUrlAsync("https://images.test/second.jpg"));
    }

    [Fact]
    public async Task FindBySourceUrlAsync_NormalizesSchemeAndHost()
    {
        var repository = new ImageCacheRepository(_database);
        var cachedPath = Path.Combine(_tempRoot, "normalized.jpg");
        await File.WriteAllBytesAsync(cachedPath, [5, 6, 7, 8]);

        await repository.InsertAsync(
            "normalized-content-hash",
            cachedPath,
            "HTTPS://IMAGES.TEST/art/poster.jpg");

        Assert.Equal(
            cachedPath,
            await repository.FindBySourceUrlAsync("https://images.test/art/poster.jpg"));
    }
}
