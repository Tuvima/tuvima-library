using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The first check of a newly added folder in the data store (Library setup spec D35, D61): which files count for which folder, what the
/// counts and the end of a check do to the held filing rows, and what a paused or checking folder stops from being claimed. Real temp
/// SQLite, no spies.
/// </summary>
public sealed class FolderFirstCheckRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private const string Library = "movies";
    private const string SourceId = "source-a";
    private const string Folder = "D:/Media/Movies";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_first_check_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly FolderFirstCheckSignal _signal = new();
    private readonly FolderFirstCheckRepository _checks;
    private readonly IdentityJobRepository _jobs;
    private readonly MediaFileWriteIntentRepository _intents;
    private readonly Guid _work = Guid.NewGuid();
    private readonly Guid _edition = Guid.NewGuid();

    public FolderFirstCheckRepositoryTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _checks = new FolderFirstCheckRepository(_database, _signal);
        _jobs = new IdentityJobRepository(_database, _signal);
        _intents = new MediaFileWriteIntentRepository(_database);
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works(id,media_type,work_kind) VALUES(@work,'Movies','child');", new { work = _work });
        connection.Execute("INSERT INTO editions(id,work_id) VALUES(@edition,@work);", new { edition = _edition, work = _work });
    }

    public void Dispose()
    {
        _signal.Dispose();
        _database.Dispose();
        DatabaseConnection.ReleasePooledConnections(_path);
        File.Delete(_path);
    }

    // ----- which files belong to a folder ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AFile_CountsForTheFolderItIsIn_WhateverTheSeparatorsAndLetterCase()
    {
        await _checks.StartAsync(Library, SourceId, "D:\\Media\\Movies\\", Now);
        var inside = AddAsset("d:/media/MOVIES/Dune (2021)/Dune.mkv");
        var nested = AddAsset(@"D:\Media\Movies\Sub\Deeper\Heat.mkv");

        await _checks.RecordOutcomeAsync(inside, FolderFirstCheckOutcomes.Matched);
        await _checks.RecordOutcomeAsync(nested, FolderFirstCheckOutcomes.Unmatched);

        var progress = await _checks.GetProgressAsync(SourceId, 50);
        Assert.Equal((2, 1, 2), (progress.Tried, progress.Unmatched, progress.TotalFiles));
    }

    [Fact]
    public async Task AFile_InASiblingFolderWhoseNameStartsTheSame_DoesNotCountForTheFolder()
    {
        await _checks.StartAsync(Library, SourceId, Folder, Now);
        var sibling = AddAsset("D:/Media/Movies2/Heat.mkv");

        await _checks.RecordOutcomeAsync(sibling, FolderFirstCheckOutcomes.Unmatched);

        var progress = await _checks.GetProgressAsync(SourceId, 50);
        Assert.Equal((0, 0, 0), (progress.Tried, progress.Unmatched, progress.TotalFiles));
    }

    [Fact]
    public async Task AFile_CountsForTheInnermostFolderBeingChecked()
    {
        await _checks.StartAsync(Library, SourceId, Folder, Now);
        await _checks.StartAsync(Library, "source-b", Folder + "/Kids", Now);
        var file = AddAsset("D:/Media/Movies/Kids/Cars.mkv");

        await _checks.RecordOutcomeAsync(file, FolderFirstCheckOutcomes.Matched);

        Assert.Equal(1, (await _checks.GetProgressAsync("source-b", 50)).Tried);
        Assert.Equal(0, (await _checks.GetProgressAsync(SourceId, 50)).Tried);
    }

    [Fact]
    public async Task AFileCounts_OnceAndItsLaterOutcomeReplacesTheEarlierOne()
    {
        await _checks.StartAsync(Library, SourceId, Folder, Now);
        var file = AddAsset("D:/Media/Movies/Heat.mkv");

        await _checks.RecordOutcomeAsync(file, FolderFirstCheckOutcomes.Unmatched);
        await _checks.RecordOutcomeAsync(file, FolderFirstCheckOutcomes.Matched);

        var progress = await _checks.GetProgressAsync(SourceId, 50);
        Assert.Equal((1, 0), (progress.Tried, progress.Unmatched));
    }

    [Fact]
    public async Task OnlyTheFirstFilesToReachAnOutcomeCount_ForAsManyFilesAsTheCheckTries()
    {
        await _checks.StartAsync(Library, SourceId, Folder, Now);
        for (var i = 0; i < 5; i++)
        {
            await _checks.RecordOutcomeAsync(
                AddAsset($"D:/Media/Movies/f{i}.mkv"), i < 3 ? FolderFirstCheckOutcomes.Unmatched : FolderFirstCheckOutcomes.Matched);
        }

        var progress = await _checks.GetProgressAsync(SourceId, 4);

        Assert.Equal((4, 3), (progress.Tried, progress.Unmatched));
    }

    [Fact]
    public async Task AFolderWithNoCheck_CountsNothing_AndAFinishedCheckCountsNothingMore()
    {
        var early = AddAsset("D:/Media/Movies/Early.mkv");
        await _checks.RecordOutcomeAsync(early, FolderFirstCheckOutcomes.Matched);
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM folder_first_check_files;"));

        await _checks.StartAsync(Library, SourceId, Folder, Now);
        Assert.True(await _checks.PassAsync(SourceId, 0, 0, Now));
        await _checks.RecordOutcomeAsync(early, FolderFirstCheckOutcomes.Matched);

        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM folder_first_check_files;"));
    }

    [Fact]
    public async Task ACheckNeverRestartsByItself()
    {
        Assert.True(await _checks.StartAsync(Library, SourceId, Folder, Now));
        Assert.True(await _checks.PassAsync(SourceId, 0, 0, Now));

        Assert.False(await _checks.StartAsync(Library, SourceId, Folder, Now));
        Assert.Equal(FolderFirstCheckStates.Passed, (await _checks.GetAsync(SourceId))!.State);
    }

    [Fact]
    public async Task AnIdentityJobsRetailOutcome_IsCountedForTheFolder_AndWakesTheEvaluator()
    {
        await _checks.StartAsync(Library, SourceId, Folder, Now);
        var matched = AddAsset("D:/Media/Movies/Heat.mkv");
        var unmatched = AddAsset("D:/Media/Movies/Unknown.mkv");
        var failed = AddAsset("D:/Media/Movies/Broken.mkv");
        var matchedJob = await QueueJobAsync(matched);
        var unmatchedJob = await QueueJobAsync(unmatched);
        var failedJob = await QueueJobAsync(failed);

        await _jobs.UpdateStateAsync(matchedJob, IdentityJobState.RetailMatchedNeedsReview);
        Assert.True(await _signal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        await _jobs.UpdateStateAsync(unmatchedJob, IdentityJobState.RetailNoMatch);
        await _jobs.UpdateStateAsync(failedJob, IdentityJobState.Failed, "boom");

        var progress = await _checks.GetProgressAsync(SourceId, 50);
        Assert.Equal((2, 1), (progress.Tried, progress.Unmatched));
        Assert.Equal("needs_review", Scalar<string>("SELECT outcome FROM folder_first_check_files WHERE asset_id=@matched;", new { matched }));
    }

    [Fact]
    public async Task AFileStillWaitingForItsLookup_IsWaiting_AndAnOpenIntakeInTheFolderIsNotSettled()
    {
        await _checks.StartAsync(Library, SourceId, Folder, Now);
        var waiting = AddAsset("D:/Media/Movies/Heat.mkv");
        await QueueJobAsync(waiting);
        Assert.Equal(1, (await _checks.GetProgressAsync(SourceId, 50)).Waiting);
        Assert.True((await _checks.GetProgressAsync(SourceId, 50)).IngestionSettled);

        AddIntake("D:/Media/Movies/Coming.mkv", "running");
        AddIntake("D:/Media/Elsewhere/Other.mkv", "running");
        Assert.False((await _checks.GetProgressAsync(SourceId, 50)).IngestionSettled);

        Execute("UPDATE media_operations SET status = 'completed' WHERE source_path LIKE 'D:/Media/Movies/%';");
        Assert.True((await _checks.GetProgressAsync(SourceId, 50)).IngestionSettled);
    }

    // ----- what a paused or checking folder stops -------------------------------------------------------------------------------

    [Fact]
    public async Task APausedFolder_ClaimsNoIdentityJobs_ButACheckingOrOtherFolderIsClaimedAsNormal()
    {
        await _checks.StartAsync(Library, SourceId, Folder, Now);
        var inside = AddAsset("D:/Media/Movies/Heat.mkv");
        var outside = AddAsset("D:/Media/Music/Song.flac");
        await QueueJobAsync(inside);
        await QueueJobAsync(outside);
        var queued = new[] { IdentityJobState.Queued };

        // Identification of a folder that is only being checked goes on as normal.
        var whileChecking = await _jobs.LeaseNextAsync("w1", queued, 10, TimeSpan.FromMinutes(1));
        Assert.Equal(2, whileChecking.Count);
        foreach (var job in whileChecking)
        {
            await _jobs.ReleaseLeaseAsync(job.Id);
        }

        Assert.True(await _checks.PauseAsync(SourceId, 5, 4, Now));
        var whilePaused = await _jobs.LeaseNextAsync("w1", queued, 10, TimeSpan.FromMinutes(1));

        Assert.Equal(outside, Assert.Single(whilePaused).EntityId);
    }

    [Fact]
    public async Task APausedFolder_ClaimsIdentityJobsAgain_OnceAdministratorChoosesKeepGoing()
    {
        await _checks.StartAsync(Library, SourceId, Folder, Now);
        var inside = AddAsset("D:/Media/Movies/Heat.mkv");
        await QueueJobAsync(inside);
        await _checks.PauseAsync(SourceId, 5, 4, Now);
        Assert.Empty(await _jobs.LeaseNextAsync("w1", [IdentityJobState.Queued], 10, TimeSpan.FromMinutes(1)));

        Assert.True(await _checks.KeepGoingAsync(SourceId, Now));

        Assert.Equal(inside, Assert.Single(await _jobs.LeaseNextAsync("w1", [IdentityJobState.Queued], 10, TimeSpan.FromMinutes(1))).EntityId);
    }

    [Theory]
    [InlineData(FolderFirstCheckStates.Checking)]
    [InlineData(FolderFirstCheckStates.Paused)]
    public async Task ATagWrite_ForAFileInAHeldFolder_StaysPending_AndIsClaimedOnceTheFolderIsReleased(string state)
    {
        await _checks.StartAsync(Library, SourceId, Folder, Now);
        if (state == FolderFirstCheckStates.Paused)
        {
            await _checks.PauseAsync(SourceId, 5, 4, Now);
        }

        var inside = AddAsset("D:/Media/Movies/Heat.mkv");
        var outside = AddAsset("D:/Media/Music/Song.flac");
        await _intents.EnqueueAssetsAsync([inside, outside], "auto_match");

        // The held file is waiting, but not claimable, so a run does not count it as work it could do.
        Assert.Equal(2, (await _intents.GetCountsAsync()).Waiting);
        Assert.Equal(1, (await _intents.GetCountsAsync()).Claimable);
        var claimed = await _intents.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(outside, Assert.Single(claimed).AssetId);
        Assert.Equal("pending", Scalar<string>("SELECT status FROM media_file_write_intents WHERE asset_id=@inside;", new { inside }));

        if (state == FolderFirstCheckStates.Paused)
        {
            await _checks.KeepGoingAsync(SourceId, Now);
        }
        else
        {
            await _checks.PassAsync(SourceId, 0, 0, Now);
        }

        var later = await _intents.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(inside, Assert.Single(later).AssetId);
    }

    [Fact]
    public async Task ATagWrite_ForAnUnmatchedFile_KeptOrNot_StaysPending_AndIsClaimedOnceItsRowClears()
    {
        var open = AddAsset("D:/Media/Movies/Open.mkv");
        var kept = AddAsset("D:/Media/Music/Kept.flac");
        var matched = AddAsset("D:/Media/Music/Matched.flac");
        foreach (var asset in new[] { open, kept })
        {
            Execute("""
                INSERT INTO unmatched_assets(asset_id,library_id,reason,first_unmatched_at,last_tried_at,next_retry_at,kept_as_is)
                VALUES(@asset,'movies','no_match','2026-10-22T09:00:00.0000000Z','2026-10-22T09:00:00.0000000Z','2026-10-29T09:00:00.0000000Z',0);
                """, new { asset });
        }

        Execute("UPDATE unmatched_assets SET kept_as_is = 1, next_retry_at = NULL WHERE asset_id = @kept;", new { kept });
        await _intents.EnqueueAssetsAsync([open, kept, matched], "auto_match");

        // Library writes nothing into a file it could not match; the others are written as normal.
        Assert.Equal(1, (await _intents.GetCountsAsync()).Claimable);
        var claimed = await _intents.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(matched, Assert.Single(claimed).AssetId);
        Assert.Equal("pending", Scalar<string>("SELECT status FROM media_file_write_intents WHERE asset_id=@open;", new { open }));
        Assert.Equal("pending", Scalar<string>("SELECT status FROM media_file_write_intents WHERE asset_id=@kept;", new { kept }));

        // A lookup identifies the open one: its Unmatched row goes and its waiting tags are claimed on the next run.
        var job = await QueueJobAsync(open);
        await _jobs.UpdateStateAsync(job, IdentityJobState.RetailMatched);
        var later = await _intents.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(open, Assert.Single(later).AssetId);
    }

    // ----- ending a check and the held filing rows ------------------------------------------------------------------------------

    [Fact]
    public async Task Passing_ReleasesTheFoldersHeldRowsToWaiting_AndLeavesOtherFoldersHeldRowsAlone()
    {
        await _checks.StartAsync(Library, SourceId, Folder, Now);
        var inside = AddAsset("D:/Media/Movies/Heat.mkv");
        var otherFolder = AddAsset("D:/Media/Music/Song.flac");
        await _intents.RequestFilingAsync(inside, held: true);
        await _intents.RequestFilingAsync(otherFolder, held: true);

        Assert.True(await _checks.PassAsync(SourceId, 10, 2, Now));

        Assert.Equal("waiting", MoveStep(inside));
        Assert.Equal("held", MoveStep(otherFolder));
        var check = (await _checks.GetAsync(SourceId))!;
        Assert.Equal((FolderFirstCheckStates.Passed, 10, 2), (check.State, check.Tried, check.Unmatched));
        Assert.NotNull(check.EndedAt);
        Assert.False(await _checks.PassAsync(SourceId, 10, 2, Now));
    }

    [Fact]
    public async Task Pausing_KeepsTheHeldRowsHeld_AndKeepGoingReleasesTheMatchedOnesOnly()
    {
        await _checks.StartAsync(Library, SourceId, Folder, Now);
        var matched = AddAsset("D:/Media/Movies/Heat.mkv");
        var unmatchedNow = AddAsset("D:/Media/Movies/Unknown.mkv");
        await _intents.RequestFilingAsync(matched, held: true);
        await _intents.RequestFilingAsync(unmatchedNow, held: true);
        Execute("""
            INSERT INTO unmatched_assets(asset_id,library_id,reason,first_unmatched_at,last_tried_at,next_retry_at)
            VALUES(@unmatchedNow,'movies','no_match','2026-10-22T09:00:00.0000000Z','2026-10-22T09:00:00.0000000Z','2026-10-29T09:00:00.0000000Z');
            """, new { unmatchedNow });

        Assert.True(await _checks.PauseAsync(SourceId, 5, 4, Now));
        Assert.Equal("held", MoveStep(matched));
        Assert.False(await _checks.KeepGoingAsync("no-such-folder", Now));

        Assert.True(await _checks.KeepGoingAsync(SourceId, Now));

        Assert.Equal("waiting", MoveStep(matched));
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM media_file_write_intents WHERE asset_id=@unmatchedNow;", new { unmatchedNow }));
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM unmatched_assets WHERE asset_id=@unmatchedNow;", new { unmatchedNow }));
        Assert.Equal(FolderFirstCheckStates.KeptGoing, (await _checks.GetAsync(SourceId))!.State);
        Assert.False(await _checks.KeepGoingAsync(SourceId, Now));
    }

    [Fact]
    public async Task Dropping_RemovesTheCheckAndTheFoldersHeldRows_AndTouchesNothingElse()
    {
        await _checks.StartAsync(Library, SourceId, Folder, Now);
        var inside = AddAsset("D:/Media/Movies/Heat.mkv");
        var waitingInside = AddAsset("D:/Media/Movies/Dune.mkv");
        var otherFolder = AddAsset("D:/Media/Music/Song.flac");
        await _intents.RequestFilingAsync(inside, held: true);
        await _intents.RequestFilingAsync(waitingInside, held: false);
        await _intents.RequestFilingAsync(otherFolder, held: true);
        await _checks.RecordOutcomeAsync(inside, FolderFirstCheckOutcomes.Matched);

        Assert.True(await _checks.DropAsync(SourceId));

        Assert.Null(await _checks.GetAsync(SourceId));
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM media_file_write_intents WHERE asset_id=@inside;", new { inside }));
        Assert.Equal("waiting", MoveStep(waitingInside));
        Assert.Equal("held", MoveStep(otherFolder));
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM folder_first_check_files;"));
        Assert.False(await _checks.DropAsync(SourceId));
    }

    [Fact]
    public async Task TheListing_ShowsOnlyFoldersThatAreCheckingOrPaused_WithLiveCountsAndTheFoldersTotal()
    {
        await _checks.StartAsync(Library, SourceId, Folder, Now);
        await _checks.StartAsync(Library, "source-b", "D:/Media/Shows", Now);
        await _checks.StartAsync(Library, "source-c", "D:/Media/Done", Now);
        await _checks.PassAsync("source-c", 1, 0, Now);
        await _checks.RecordOutcomeAsync(AddAsset("D:/Media/Movies/A.mkv"), FolderFirstCheckOutcomes.Matched);
        await _checks.RecordOutcomeAsync(AddAsset("D:/Media/Movies/B.mkv"), FolderFirstCheckOutcomes.Unmatched);
        AddAsset("D:/Media/Movies/C.mkv");
        await _checks.PauseAsync("source-b", 8, 6, Now);

        var open = await _checks.ListOpenAsync();

        Assert.Equal([SourceId, "source-b"], open.Select(item => item.Check.SourceId).Order(StringComparer.Ordinal).ToArray());
        var checking = open.Single(item => item.Check.SourceId == SourceId);
        Assert.Equal((FolderFirstCheckStates.Checking, 2, 1, 3), (checking.Check.State, checking.Check.Tried, checking.Check.Unmatched, checking.TotalFiles));
        var paused = open.Single(item => item.Check.SourceId == "source-b");
        Assert.Equal((FolderFirstCheckStates.Paused, 8, 6), (paused.Check.State, paused.Check.Tried, paused.Check.Unmatched));
    }

    [Fact]
    public async Task OnlyAFolderThatIsCheckingOrPausedIsHeld()
    {
        Assert.False(_checks.IsHeld(SourceId));
        await _checks.StartAsync(Library, SourceId, Folder, Now);
        Assert.True(_checks.IsHeld(SourceId));
        await _checks.PauseAsync(SourceId, 5, 4, Now);
        Assert.True(_checks.IsHeld(SourceId));
        await _checks.KeepGoingAsync(SourceId, Now);
        Assert.False(_checks.IsHeld(SourceId));
        Assert.False(new FolderFirstCheckFilingHold(_checks).IsHeld(Library, SourceId, Folder));
    }

    // ----- helpers ------------------------------------------------------------------------------------------------------------

    private Guid AddAsset(string path)
    {
        var asset = Guid.NewGuid();
        Execute("""
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root)
            VALUES(@asset,@edition,@hash,@path);
            """, new { asset, edition = _edition, hash = Guid.NewGuid().ToString("N"), path });
        return asset;
    }

    private async Task<Guid> QueueJobAsync(Guid asset)
    {
        var job = new IdentityJob { EntityId = asset, EntityType = EntityType.MediaAsset.ToString(), MediaType = "Movies", Pass = "Quick" };
        await _jobs.CreateAsync(job);
        return job.Id;
    }

    private void AddIntake(string path, string status) =>
        Execute("""
            INSERT INTO media_operations(id,operation_type,operation_kind,source_path,status,position_key,created_at,updated_at,idempotency_key)
            VALUES(@id,'ingestion.file','ingestion',@path,@status,1,'2026-10-22T09:00:00Z','2026-10-22T09:00:00Z',@key);
            """, new { id = Guid.NewGuid(), path, status, key = Guid.NewGuid().ToString("N") });

    private string? MoveStep(Guid asset) =>
        Scalar<string?>("SELECT move_step FROM media_file_write_intents WHERE asset_id=@asset;", new { asset });

    private void Execute(string sql, object? args = null)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(sql, args);
    }

    private T Scalar<T>(string sql, object? args = null)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<T>(sql, args)!;
    }
}
