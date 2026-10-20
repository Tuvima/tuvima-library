using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Jobs;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// A provider outage or timeout that a job retried past depends on load, not on what the Library decided, so it is
/// not left on a job that went on to reach its outcome.
/// </summary>
public sealed class IdentityJobOutcomeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_job_outcome_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly IdentityJobRepository _jobs;

    public IdentityJobOutcomeTests()
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

    [Theory]
    [InlineData(IdentityJobState.Ready)]
    [InlineData(IdentityJobState.ReadyWithoutUniverse)]
    [InlineData(IdentityJobState.RetailNoMatch)]
    [InlineData(IdentityJobState.QidNoMatch)]
    public async Task RetriedTransientFailure_JobReachesItsOutcome_ForgetsTheFailure(IdentityJobState outcome)
    {
        var job = await RetriedJobAsync();

        await _jobs.UpdateStateAsync(job.Id, outcome);

        Assert.Null((await _jobs.GetByIdAsync(job.Id))!.LastOutcomeCategory);
    }

    [Fact]
    public async Task RetriedTransientFailure_JobStillWorking_KeepsTheFailure()
    {
        var job = await RetriedJobAsync();

        await _jobs.UpdateStateAsync(job.Id, IdentityJobState.RetailSearching);

        Assert.Equal(
            nameof(BackgroundJobOutcomeCategory.TransientDependencyFailure),
            (await _jobs.GetByIdAsync(job.Id))!.LastOutcomeCategory);
    }

    private async Task<IdentityJob> RetriedJobAsync()
    {
        var job = new IdentityJob
        {
            EntityId = Guid.NewGuid(),
            EntityType = "MediaAsset",
            MediaType = MediaType.Comics.ToString(),
        };
        await _jobs.CreateAsync(job);
        await _jobs.ScheduleRetryForOutcomeAsync(
            job.Id,
            IdentityJobState.Queued,
            DateTimeOffset.UtcNow,
            "Comic Vine timed out",
            BackgroundJobOutcomeCategory.TransientDependencyFailure);
        return job;
    }
}
