using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The part files of one audiobook recording share one identity job. Which part carries it (and so the retail
/// match, people and review rows) must not depend on which part finished ingesting first.
/// </summary>
public sealed class AudiobookRecordingJobTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_recording_job_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly IdentityJobRepository _jobs;
    private readonly Guid _recording = Guid.NewGuid();

    public AudiobookRecordingJobTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _jobs = new IdentityJobRepository(_db);
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO works (id, media_type, work_kind, parent_key) VALUES (@id, 'Audiobooks', 'standalone', 'source-folder:recording')",
            new { id = _recording });
    }

    public void Dispose()
    {
        _db.Dispose();
        DatabaseConnection.ReleasePooledConnections(_dbPath);
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task RecordingJob_LaterLowerPart_TakesOverTheQueuedJob()
    {
        var part3 = AddPart(3);
        var part1 = AddPart(1);
        var part2 = AddPart(2);

        await _jobs.CreateAsync(Job(part3));
        await _jobs.CreateAsync(Job(part1));
        await _jobs.CreateAsync(Job(part2));

        Assert.Equal([part1], JobEntities());
    }

    [Fact]
    public async Task RecordingJob_PartsArrivingInAnyOrder_EndOnTheLeadPart()
    {
        var part1 = AddPart(1);
        var part2 = AddPart(2);
        var part3 = AddPart(3);

        await _jobs.CreateAsync(Job(part2));
        await _jobs.CreateAsync(Job(part3));
        await _jobs.CreateAsync(Job(part1));

        Assert.Equal([part1], JobEntities());
    }

    [Fact]
    public async Task RecordingJob_AlreadyLeased_StaysWhereItIs()
    {
        var part3 = AddPart(3);
        await _jobs.CreateAsync(Job(part3));
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("UPDATE identity_jobs SET lease_owner = 'RetailMatchWorker', lease_expires_at = @expires",
                new { expires = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O") });
        }

        var part1 = AddPart(1);
        await _jobs.CreateAsync(Job(part1));

        Assert.Equal([part3], JobEntities());
    }

    [Fact]
    public async Task RecordingJob_PartsStillMissing_IsNotLeasedUntilEveryPartIsRegistered()
    {
        var part1 = AddPart(1, partCount: 2);
        await _jobs.CreateAsync(Job(part1));

        Assert.Empty(await _jobs.LeaseNextAsync("test", [IdentityJobState.Queued], 10, TimeSpan.FromMinutes(5)));

        AddPart(2, partCount: 2);

        var leased = Assert.Single(await _jobs.LeaseNextAsync("test", [IdentityJobState.Queued], 10, TimeSpan.FromMinutes(5)));
        Assert.Equal(part1, leased.EntityId);
    }

    [Fact]
    public async Task RecordingJob_PartThatNeverArrives_HoldsTheRecordingOnlyForTheHoldTime()
    {
        var part1 = AddPart(1, partCount: 2);
        var job = Job(part1);
        job.CreatedAt = DateTimeOffset.UtcNow - IdentityJobRepository.IncompleteRecordingHold - TimeSpan.FromMinutes(1);
        await _jobs.CreateAsync(job);

        var leased = Assert.Single(await _jobs.LeaseNextAsync("test", [IdentityJobState.Queued], 10, TimeSpan.FromMinutes(5)));
        Assert.Equal(part1, leased.EntityId);
    }

    private Guid AddPart(int part, int partCount = 3)
    {
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow.ToString("O");
        using var conn = _db.CreateConnection();
        conn.Execute("INSERT INTO editions (id, work_id) VALUES (@edition, @work)", new { edition, work = _recording });
        conn.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@asset, @edition, @hash, @path, 'Normal')",
            new { asset, edition, hash = $"part-{part}", path = $"Project Hail Mary/{part:000} - Part {part:00}.mp3" });
        conn.Execute(
            """
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@asset, 'audiobook_part_number', @part, @stamp);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@asset, 'audiobook_part_count', @partCount, @stamp);
            """,
            new { asset, part = part.ToString(), partCount = partCount.ToString(), stamp });
        return asset;
    }

    private static IdentityJob Job(Guid assetId) => new()
    {
        EntityId = assetId,
        EntityType = "MediaAsset",
        MediaType = MediaType.Audiobooks.ToString(),
        Pass = "Quick",
    };

    private List<Guid> JobEntities()
    {
        using var conn = _db.CreateConnection();
        return conn.Query<Guid>("SELECT entity_id FROM identity_jobs").AsList();
    }
}
