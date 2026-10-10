using MediaEngine.TestSupport;
namespace MediaEngine.Storage.Tests;

public sealed class FileHashCacheRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public FileHashCacheRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_hash_cache_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Fact]
    public async Task TryGetAsync_ReturnsHashOnlyWhenSizeAndMtimeMatch()
    {
        var repo = new FileHashCacheRepository(_db);
        var path = Path.GetFullPath("C:/watch/movie.mkv");
        var mtime = DateTimeOffset.UtcNow;

        await repo.UpsertAsync(path, 1024, mtime, "abc123");

        var hit = await repo.TryGetAsync(path, 1024, mtime);
        var staleSize = await repo.TryGetAsync(path, 2048, mtime);
        var staleMtime = await repo.TryGetAsync(path, 1024, mtime.AddMinutes(5));

        Assert.Equal("abc123", hit);
        Assert.Null(staleSize);
        Assert.Null(staleMtime);
    }
}
