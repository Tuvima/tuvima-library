using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Storage.Tests;

/// <summary>One crowded media type must not keep the others at the back of the waiting line (#1360).</summary>
public sealed class IdentityJobFairLeaseTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_fair_lease_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly IdentityJobRepository _jobs;

    public IdentityJobFairLeaseTests()
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
    public async Task Lease_WithManyOldMusicJobsAndOneFilm_StillTakesTheFilm()
    {
        for (var i = 0; i < 30; i++)
        {
            await CreateAsync(MediaType.Music);
        }

        var film = await CreateAsync(MediaType.Movies);

        var leased = await _jobs.LeaseNextAsync("w", [IdentityJobState.Queued], 10, TimeSpan.FromMinutes(1));

        Assert.Equal(10, leased.Count);
        Assert.Contains(leased, job => job.Id == film);
    }

    [Fact]
    public async Task Lease_WithOneMediaType_KeepsOldestFirst()
    {
        var first = await CreateAsync(MediaType.Books);
        await Task.Delay(5);
        var second = await CreateAsync(MediaType.Books);
        await Task.Delay(5);
        await CreateAsync(MediaType.Books);

        var leased = await _jobs.LeaseNextAsync("w", [IdentityJobState.Queued], 2, TimeSpan.FromMinutes(1));

        Assert.Equal([first, second], leased.Select(job => job.Id).OrderBy(id => id == first ? 0 : 1).ToArray());
    }

    private async Task<Guid> CreateAsync(MediaType mediaType)
    {
        var job = new IdentityJob { EntityId = Guid.NewGuid(), EntityType = "MediaAsset", MediaType = mediaType.ToString() };
        await _jobs.CreateAsync(job);
        return job.Id;
    }
}
