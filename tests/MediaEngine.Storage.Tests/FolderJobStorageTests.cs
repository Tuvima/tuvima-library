using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The storage behind the organising job: pausing and resuming a durable operation, progress that never disturbs a person's
/// pause or cancel, a shutdown that never un-cancels a job, and the per-file ledger.
/// </summary>
public sealed class FolderJobStorageTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly MediaOperationRepository _operations;
    private readonly FolderOrganiseLogRepository _ledger;

    public FolderJobStorageTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_folder_jobs_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _operations = new MediaOperationRepository(_db);
        _ledger = new FolderOrganiseLogRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { _ = ex; }
        try { File.Delete(_dbPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
    }

    private async Task<MediaOperation> QueuedJobAsync(int total = 10) => await _operations.EnsureAsync(new MediaOperation
    {
        Id = Guid.NewGuid(),
        OperationType = MediaOperationType.OrganiseFolder,
        OperationKind = MediaOperationKind.Maintenance,
        EntityId = Guid.NewGuid(),
        Status = MediaOperationStatus.Queued,
        Stage = MediaOperationStage.Queued,
        QueueName = MediaOperationQueue.FolderJobs,
        ItemsTotal = total,
        IdempotencyKey = $"organise_folder:{Guid.NewGuid():N}",
    });

    [Fact]
    public async Task A_paused_job_is_not_leased_until_it_is_resumed()
    {
        var job = await QueuedJobAsync();

        Assert.True(await _operations.PauseAsync(job.Id));
        Assert.Equal(MediaOperationStatus.Paused, (await _operations.GetByIdAsync(job.Id))!.Status);
        Assert.Empty(await _operations.LeaseNextAsync("worker", [MediaOperationType.OrganiseFolder], 1, TimeSpan.FromMinutes(1)));
        Assert.False(await _operations.PauseAsync(job.Id));

        Assert.True(await _operations.ResumePausedAsync(job.Id));
        var leased = await _operations.LeaseNextAsync("worker", [MediaOperationType.OrganiseFolder], 1, TimeSpan.FromMinutes(1));
        Assert.Equal(job.Id, Assert.Single(leased).Id);
        Assert.False(await _operations.ResumePausedAsync(job.Id));
    }

    [Fact]
    public async Task A_finished_job_cannot_be_paused()
    {
        var job = await QueuedJobAsync();
        await _operations.MarkSucceededAsync(job.Id, "done");

        Assert.False(await _operations.PauseAsync(job.Id));
        Assert.Equal(MediaOperationStatus.Succeeded, (await _operations.GetByIdAsync(job.Id))!.Status);
    }

    [Fact]
    public async Task Recording_progress_never_changes_a_status_a_person_set()
    {
        var job = await QueuedJobAsync(total: 100);
        await _operations.LeaseNextAsync("worker", [MediaOperationType.OrganiseFolder], 1, TimeSpan.FromMinutes(1));
        await _operations.PauseAsync(job.Id);

        await _operations.RecordProgressAsync(job.Id, 100, 40, 2, 40, TimeSpan.FromMinutes(10));

        var after = (await _operations.GetByIdAsync(job.Id))!;
        Assert.Equal(MediaOperationStatus.Paused, after.Status);
        Assert.Equal((100, 40, 2, 40), (after.ItemsTotal, after.ItemsCompleted, after.ItemsFailed, after.ProgressPercent));
        Assert.Null(after.LeaseExpiresAt);
    }

    [Fact]
    public async Task Recording_progress_keeps_a_running_jobs_lease_alive()
    {
        var job = await QueuedJobAsync();
        var leased = (await _operations.LeaseNextAsync("worker", [MediaOperationType.OrganiseFolder], 1, TimeSpan.FromSeconds(30))).Single();

        await _operations.RecordProgressAsync(job.Id, 10, 3, 0, 30, TimeSpan.FromHours(1));

        var after = (await _operations.GetByIdAsync(job.Id))!;
        Assert.True(after.LeaseExpiresAt > leased.LeaseExpiresAt!.Value.AddMinutes(30));
        Assert.Equal(MediaOperationStatus.Leased, after.Status);
    }

    [Fact]
    public async Task Stopping_the_worker_releases_a_running_job_but_never_un_cancels_one()
    {
        var running = await QueuedJobAsync();
        await _operations.LeaseNextAsync("worker", [MediaOperationType.OrganiseFolder], 1, TimeSpan.FromMinutes(1));
        Assert.True(await _operations.InterruptIfRunningAsync(running.Id, "stopping"));
        Assert.Equal(MediaOperationStatus.Interrupted, (await _operations.GetByIdAsync(running.Id))!.Status);

        // An interrupted job is picked up again on the next start.
        Assert.Equal(running.Id, Assert.Single(await _operations.LeaseNextAsync("worker", [MediaOperationType.OrganiseFolder], 1, TimeSpan.FromMinutes(1))).Id);

        var cancelled = await QueuedJobAsync();
        await _operations.MarkCancelledAsync(cancelled.Id, "Cancelled by user.");
        Assert.False(await _operations.InterruptIfRunningAsync(cancelled.Id, "stopping"));
        Assert.Equal(MediaOperationStatus.Cancelled, (await _operations.GetByIdAsync(cancelled.Id))!.Status);
    }

    [Fact]
    public async Task A_paused_job_still_counts_as_open_in_the_queue_order()
    {
        var paused = await QueuedJobAsync();
        var finished = await QueuedJobAsync();
        await _operations.MarkSucceededAsync(finished.Id, "done");
        await _operations.PauseAsync(paused.Id);

        var queue = await _operations.GetQueueAsync(MediaOperationQueue.FolderJobs, 10);

        Assert.Equal(paused.Id, queue[0].Id);
    }

    [Fact]
    public async Task The_ledger_keeps_one_row_per_file_and_an_intent_becomes_its_final_outcome()
    {
        var operation = Guid.NewGuid();
        var source = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        await _ledger.UpsertAsync(new FolderOrganiseLogEntry(operation, source, "D:/raw/a.mkv", "D:/Movies/A (2001)/A (2001).mkv", FolderOrganiseOutcomes.Moving, at));
        await _ledger.UpsertAsync(new FolderOrganiseLogEntry(operation, source, "D:/raw/b.mkv", "D:/raw/b.mkv", FolderOrganiseOutcomes.AlreadyCorrect, at));

        Assert.Equal("D:/raw/a.mkv", Assert.Single(await _ledger.ListUnfinishedAsync(operation)).OldPath);
        // An unfinished move is not "done": a resumed job must look at that file again.
        Assert.Equal(["D:/raw/b.mkv"], await _ledger.GetFinishedPathsAsync(operation));

        await _ledger.UpsertAsync(new FolderOrganiseLogEntry(operation, source, "D:/raw/a.mkv", "D:/Movies/A (2001)/A (2001).mkv", FolderOrganiseOutcomes.Moved, at.AddSeconds(1), "ok"));

        Assert.Equal(2, await _ledger.CountAsync(operation));
        Assert.Empty(await _ledger.ListUnfinishedAsync(operation));
        var finished = await _ledger.GetFinishedPathsAsync(operation);
        Assert.Contains("D:/Movies/A (2001)/A (2001).mkv", finished);
        Assert.Contains("D:/raw/a.mkv", finished);
        var entry = await _ledger.GetAsync(operation, "D:/raw/a.mkv");
        Assert.Equal((FolderOrganiseOutcomes.Moved, "ok"), (entry!.Outcome, entry.Detail));
        Assert.Equal(1, (await _ledger.CountByOutcomeAsync(operation))[FolderOrganiseOutcomes.Moved]);
    }

    [Fact]
    public async Task The_ledger_pages_in_the_order_things_happened_and_keeps_jobs_apart()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var source = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        for (var index = 0; index < 5; index++)
        {
            await _ledger.UpsertAsync(new FolderOrganiseLogEntry(first, source, $"old/{index}", $"new/{index}", FolderOrganiseOutcomes.Moved, start.AddSeconds(index)));
        }

        await _ledger.UpsertAsync(new FolderOrganiseLogEntry(second, source, "old/0", "new/0", FolderOrganiseOutcomes.Moved, start));

        var page = await _ledger.ListAsync(first, offset: 2, limit: 2);

        Assert.Equal(["old/2", "old/3"], page.Select(entry => entry.OldPath));
        Assert.Equal(5, await _ledger.CountAsync(first));
        Assert.Equal(1, await _ledger.CountAsync(second));

        await _ledger.DeleteAsync(first, "old/0");
        Assert.Equal(4, await _ledger.CountAsync(first));
    }

    [Fact]
    public async Task Catalogued_files_under_a_folder_are_listed_without_matching_a_sibling_folder_of_the_same_prefix()
    {
        var assets = new MediaAssetRepository(_db);
        using (var connection = _db.CreateConnection())
        {
            Dapper.SqlMapper.Execute(connection, """
                INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@w, 'Movies', 'standalone', 'Owned');
                INSERT INTO editions (id, work_id) VALUES (@e, @w);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@a1, @e, 'h1', @p1, 'Normal');
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@a2, @e, 'h2', @p2, 'Normal');
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@a3, @e, 'h3', @p3, 'Normal');
                """,
                new
                {
                    w = Guid.NewGuid(),
                    e = Guid.NewGuid(),
                    a1 = Guid.NewGuid(),
                    a2 = Guid.NewGuid(),
                    a3 = Guid.NewGuid(),
                    p1 = "D:\\Plex\\Movies\\Dune\\Dune.mkv",
                    p2 = "D:\\Plex\\Movies2\\Other.mkv",
                    p3 = "d:/plex/movies/Alien.mkv",
                });
        }

        var found = await assets.ListFilesUnderAsync("D:\\Plex\\Movies");

        Assert.Equal(["d:/plex/movies/Alien.mkv", "D:\\Plex\\Movies\\Dune\\Dune.mkv"], found.Select(file => file.FilePath));
    }
}
