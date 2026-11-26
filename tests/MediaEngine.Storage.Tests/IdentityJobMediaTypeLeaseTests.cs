using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Storage.Tests;

/// <summary>A type with a slow provider (comics) is leased in its own lane, so it never holds up the other types in a batch.</summary>
public sealed class IdentityJobMediaTypeLeaseTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_media_type_lease_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly IdentityJobRepository _jobs;

    public IdentityJobMediaTypeLeaseTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _jobs = new IdentityJobRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        DatabaseConnection.ReleasePooledConnections(_dbPath);
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task LeaseExceptComics_NeverTakesAComic_EvenWhenComicsAreOldest()
    {
        await AddAsync(MediaType.Comics);
        await AddAsync(MediaType.Comics);
        await AddAsync(MediaType.Books);
        await AddAsync(MediaType.Movies);

        var leased = await _jobs.LeaseNextAsync(
            "test", [IdentityJobState.Queued], 10, TimeSpan.FromMinutes(5),
            onlyMediaTypes: null, exceptMediaTypes: ["Comics"]);

        Assert.Equal(2, leased.Count);
        Assert.DoesNotContain(leased, job => job.MediaType == "Comics");
    }

    [Fact]
    public async Task LeaseOnlyComics_TakesJustComics_UpToTheBatchSize()
    {
        await AddAsync(MediaType.Comics);
        await AddAsync(MediaType.Comics);
        await AddAsync(MediaType.Comics);
        await AddAsync(MediaType.Books);

        var leased = await _jobs.LeaseNextAsync(
            "test", [IdentityJobState.Queued], 2, TimeSpan.FromMinutes(5),
            onlyMediaTypes: ["Comics"], exceptMediaTypes: null);

        Assert.Equal(2, leased.Count);
        Assert.All(leased, job => Assert.Equal("Comics", job.MediaType));
    }

    [Fact]
    public async Task LeaseWithNoFilter_StillTakesEveryType()
    {
        await AddAsync(MediaType.Comics);
        await AddAsync(MediaType.Books);

        var leased = await _jobs.LeaseNextAsync("test", [IdentityJobState.Queued], 10, TimeSpan.FromMinutes(5));

        Assert.Equal(2, leased.Count);
    }

    private Task AddAsync(MediaType type) => _jobs.CreateAsync(new IdentityJob
    {
        EntityId = Guid.NewGuid(),
        EntityType = "MediaAsset",
        MediaType = type.ToString(),
        Pass = "Quick",
    });
}
