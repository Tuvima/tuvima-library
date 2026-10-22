using Dapper;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class MediaFileWriteIntentRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_write_intent_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Clock _clock = new(Start);
    private readonly Guid _asset = Guid.NewGuid();
    private readonly Guid _work = Guid.NewGuid();
    private readonly Guid _edition = Guid.NewGuid();

    public MediaFileWriteIntentRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works(id,media_type,work_kind) VALUES(@work,'Movies','child');", new { work = _work });
        connection.Execute("INSERT INTO editions(id,work_id) VALUES(@edition,@work);", new { edition = _edition, work = _work });
        connection.Execute("""
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root)
            VALUES(@asset,@edition,@hash,'C:/fixture/movie.mp4');
            """, new { asset = _asset, edition = _edition, hash = Guid.NewGuid().ToString("N") });
    }

    private MediaFileWriteIntentRepository Repository => new(_database, _clock);

    private Guid AddAsset(string path = "C:/fixture/other.mp4")
    {
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root)
            VALUES(@asset,@edition,@hash,@path);
            """, new { asset, edition = _edition, hash = Guid.NewGuid().ToString("N"), path });
        return asset;
    }

    private T Scalar<T>(string sql, object? args = null)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<T>(sql, args)!;
    }

    private T Row<T>(string sql, object? args = null)
    {
        using var connection = _database.CreateConnection();
        return connection.QuerySingle<T>(sql, args);
    }

    [Fact]
    public async Task ExpiredLeaseIsRecoveredAndCompletionIsGenerationGuarded()
    {
        var repository = Repository;
        await repository.EnqueueAssetsAsync([_asset], "editor_commit");
        var first = Assert.Single(await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(1), Start.AddHours(1)));
        Assert.Equal("writing", first.Status);
        Assert.Equal(1, first.Attempts);

        // A change lands while the older generation is being written.
        await repository.EnqueueAssetsAsync([_asset], "manual_override");

        Assert.False(await repository.CompleteByIdAsync(first.Id, first.Generation, "verified"));
        var latest = Assert.Single(await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(1), Start.AddHours(1)));
        Assert.Equal(2, latest.Generation);
        Assert.Equal("manual_override", latest.Trigger);
        Assert.True(await repository.CompleteByIdAsync(latest.Id, latest.Generation, "verified"));
        Assert.Equal("verified", Scalar<string>("SELECT status FROM media_file_write_intents WHERE asset_id=@asset;", new { asset = _asset }));
    }

    [Fact]
    public async Task AClaimThatRanOutIsClaimedAgain_WhenTheEngineStoppedMidWrite()
    {
        var repository = Repository;
        await repository.EnqueueAssetsAsync([_asset], "auto_match");
        await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(5), Start.AddHours(1));

        Assert.Empty(await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(5), Start.AddHours(1)));

        _clock.Advance(TimeSpan.FromMinutes(6));
        var again = Assert.Single(await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(5), Start.AddHours(1)));
        Assert.Equal(2, again.Attempts);
    }

    [Fact]
    public async Task FiveChangesToOneFile_LeaveOneEntryAtTheNewestGeneration()
    {
        var repository = Repository;
        foreach (var trigger in new[] { "auto_match", "quick_hydration", "universe_enrichment", "manual_override", "item_preferences" })
        {
            await repository.EnqueueAssetsAsync([_asset], trigger);
        }

        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM media_file_write_intents;"));
        var row = Assert.Single(await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(5), Start.AddHours(1)));
        Assert.Equal(5, row.Generation);
        Assert.Equal("item_preferences", row.Trigger);
    }

    [Fact]
    public async Task AChangeLandingWhileTheFileIsBeingWritten_PutsTheEntryBackToPendingWithAFreshAttemptCount()
    {
        var repository = Repository;
        await repository.EnqueueAssetsAsync([_asset], "auto_match");
        var claimed = Assert.Single(await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(5), Start.AddHours(1)));

        await repository.EnqueueAssetsAsync([_asset], "manual_override");

        var row = Row<(string Status, long Attempts, long Generation)>(
            "SELECT status AS Status, attempts AS Attempts, generation AS Generation FROM media_file_write_intents WHERE asset_id=@asset;", new { asset = _asset });
        Assert.Equal("pending", row.Status);
        Assert.Equal(0, row.Attempts);
        Assert.Equal(claimed.Generation + 1, row.Generation);
    }

    [Fact]
    public async Task AnIdThatIsNotAMediaAsset_AddsNothingToTheList()
    {
        var queued = await Repository.EnqueueAssetsAsync([Guid.NewGuid(), _work], "auto_match");

        Assert.Equal(0, queued);
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM media_file_write_intents;"));
    }

    [Fact]
    public async Task DeletingTheAsset_DropsItsEntry()
    {
        await Repository.EnqueueAssetsAsync([_asset], "auto_match");

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM media_assets WHERE id=@asset;", new { asset = _asset });
        }

        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM media_file_write_intents;"));
    }

    [Fact]
    public async Task ClaimBatch_LeavesAFileWithAMoveInProgressAlone_UntilTheMoveIsDone()
    {
        var repository = Repository;
        UpsertWithMove(_asset);

        Assert.Empty(await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(5), Start.AddHours(1)));

        var move = await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5));
        Assert.NotNull(move);
        Assert.Equal("requested", move.MoveStep);
        Assert.True(await repository.CompleteMoveAsync(move.Id, move.Generation));

        var tags = Assert.Single(await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(5), Start.AddHours(1)));
        Assert.Equal(_asset, tags.AssetId);
        Assert.Null(tags.MoveStep);
        Assert.Equal(1, tags.Attempts);
    }

    [Fact]
    public async Task AnotherChangeWhileAMoveIsWaiting_NeverClearsTheMoveStep()
    {
        var repository = Repository;
        UpsertWithMove(_asset);

        await repository.EnqueueAssetsAsync([_asset], "manual_override");

        var row = Row<(string? MoveStep, string Trigger)>(
            "SELECT move_step AS MoveStep, trigger AS Trigger FROM media_file_write_intents WHERE asset_id=@asset;", new { asset = _asset });
        Assert.Equal("requested", row.MoveStep);
        Assert.Equal("library_move", row.Trigger);
    }

    [Fact]
    public async Task AMoveClaimedBeforeANewerChange_IsCheckedAgainInsteadOfBeingCompleted()
    {
        var repository = Repository;
        UpsertWithMove(_asset);
        var move = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;

        await repository.EnqueueAssetsAsync([_asset], "editor_commit");

        Assert.False(await repository.CompleteMoveAsync(move.Id, move.Generation));
        var again = await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5));
        Assert.NotNull(again);
        Assert.Equal(move.Generation + 1, again.Generation);
    }

    [Fact]
    public async Task ClaimNextMove_SkipsAFailedMoveUntilItsNextAttemptIsDue()
    {
        var repository = Repository;
        UpsertWithMove(_asset);
        var move = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;
        Assert.True(await repository.FailMoveAsync(move.Id, "pending", "in use", TimeSpan.FromMinutes(5)));

        Assert.Null(await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)));
        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Null(await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)));
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.NotNull(await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task ClaimBatch_DoesNotTakeAChangeMadeAfterTheRunStarted()
    {
        var repository = Repository;
        await repository.EnqueueAssetsAsync([_asset], "auto_match");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var late = AddAsset();
        await repository.EnqueueAssetsAsync([late], "auto_match");

        var claimed = await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(5), Start);

        Assert.Equal(_asset, Assert.Single(claimed).AssetId);
    }

    [Fact]
    public async Task ClaimBatch_TakesAtMostTheLimit_OldestFirst()
    {
        var repository = Repository;
        await repository.EnqueueAssetsAsync([_asset], "auto_match");
        _clock.Advance(TimeSpan.FromSeconds(1));
        var second = AddAsset();
        await repository.EnqueueAssetsAsync([second], "auto_match");

        var first = Assert.Single(await repository.ClaimBatchAsync("media_asset", 1, TimeSpan.FromMinutes(5), Start.AddHours(1)));

        Assert.Equal(_asset, first.AssetId);
    }

    [Fact]
    public async Task Completing_RecordsWhenTheWriteFinished_AndCountsSplitWaitingFromFailed()
    {
        var repository = Repository;
        var other = AddAsset();
        await repository.EnqueueAssetsAsync([_asset, other], "auto_match");
        var claimed = await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(5), Start.AddHours(1));
        var mine = claimed.Single(row => row.AssetId == _asset);
        var theirs = claimed.Single(row => row.AssetId == other);

        Assert.True(await repository.CompleteByIdAsync(mine.Id, mine.Generation, "failed", "The file is read-only."));
        var counts = await repository.GetCountsAsync();
        Assert.Equal(1, counts.Waiting);
        Assert.Equal(1, counts.Failed);

        var failed = Assert.Single(await repository.GetFailedAsync());
        Assert.Equal(_asset, failed.AssetId);
        Assert.Equal("C:/fixture/movie.mp4", failed.Path);
        Assert.Equal("The file is read-only.", failed.Reason);

        Assert.True(await repository.CompleteByIdAsync(theirs.Id, theirs.Generation, "verified"));
        var state = await repository.GetAssetStateAsync(other);
        Assert.False(state.Waiting);
        Assert.NotNull(state.LastWrittenAt);
    }

    [Fact]
    public async Task AFileWithWaitingChanges_IsReportedAsWaiting()
    {
        var repository = Repository;
        Assert.False((await repository.GetAssetStateAsync(_asset)).Waiting);

        await repository.EnqueueAssetsAsync([_asset], "auto_match");

        Assert.True((await repository.GetAssetStateAsync(_asset)).Waiting);
        Assert.Contains(_asset, await repository.GetWaitingAssetIdsAsync([_asset, Guid.NewGuid()]));
    }

    [Fact]
    public async Task TheScheduleStartsFromTheFirstAnchor_AndRemembersTheLastRun()
    {
        var repository = Repository;
        Assert.Null(await repository.GetRunStateAsync());

        await repository.EnsureAnchorAsync(Start);
        await repository.EnsureAnchorAsync(Start.AddDays(3));
        var anchored = await repository.GetRunStateAsync();
        Assert.Equal(Start, DateTimeOffset.Parse(anchored!.AnchorAt));
        Assert.Null(anchored.LastRunFinishedAt);

        await repository.RecordRunAsync(Start.AddHours(1), Start.AddHours(1).AddMinutes(2), written: 7, failed: 1, kept: 2);
        var recorded = await repository.GetRunStateAsync();
        Assert.Equal(Start, DateTimeOffset.Parse(recorded!.AnchorAt));
        Assert.Equal(Start.AddHours(1).AddMinutes(2), DateTimeOffset.Parse(recorded.LastRunFinishedAt!));
        Assert.Equal(7, recorded.LastRunWritten);
        Assert.Equal(1, recorded.LastRunFailed);
        Assert.Equal(2, recorded.LastRunKept);
    }

    [Fact]
    public void ARowMustPointAtExactlyTheTargetItsKindNames()
    {
        using var connection = _database.CreateConnection();

        Assert.Throws<SqliteException>(() => connection.Execute("""
            INSERT INTO media_file_write_intents (id,target_kind,generation,operation_token,trigger,created_at,updated_at)
            VALUES (@id,'media_asset',1,'t','auto_match','2026-10-22T00:00:00.0000000+00:00','2026-10-22T00:00:00.0000000+00:00');
            """, new { id = Guid.NewGuid() }));
        Assert.Throws<SqliteException>(() => connection.Execute("""
            INSERT INTO media_file_write_intents (id,target_kind,asset_id,generation,operation_token,trigger,created_at,updated_at,move_step)
            VALUES (@id,'media_asset',@asset,1,'t','auto_match','2026-10-22T00:00:00.0000000+00:00','2026-10-22T00:00:00.0000000+00:00','teleported');
            """, new { id = Guid.NewGuid(), asset = _asset }));
    }

    // ----- the steps of a move, the waiting mark and the schedule (BW1b) ---------------------------------------------------------

    [Fact]
    public async Task AMove_RecordsEachStepOnItsRow_AndWhenCheckedClearsThemAndLeavesTheTagWriteWaiting()
    {
        var repository = Repository;
        UpsertWithMove(_asset);
        var move = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;

        await repository.RecordMovePlannedAsync(move.Id, "C:/a/old.mp4", "C:/b/new.mp4", "hash-1");
        var planned = (await repository.GetByIdAsync(move.Id))!;
        Assert.Equal("planned", planned.MoveStep);
        Assert.Equal(("C:/a/old.mp4", "C:/b/new.mp4", "hash-1"), (planned.MoveSourcePath, planned.MoveTargetPath, planned.MoveSourceHash));

        await repository.RecordMoveMovedAsync(move.Id, "C:/b/new (2).mp4");
        var moved = (await repository.GetByIdAsync(move.Id))!;
        Assert.Equal("moved", moved.MoveStep);
        Assert.Equal("C:/b/new (2).mp4", moved.MoveTargetPath);

        await repository.RecordMoveCheckedAsync(move.Id);
        Assert.Equal("checked", (await repository.GetByIdAsync(move.Id))!.MoveStep);

        Assert.True(await repository.CompleteMoveAsync(move.Id, move.Generation));
        var done = (await repository.GetByIdAsync(move.Id))!;
        Assert.Null(done.MoveStep);
        Assert.Null(done.MoveSourcePath);
        Assert.Null(done.MoveTargetPath);
        Assert.Null(done.MoveSourceHash);
        Assert.Equal("pending", done.Status);
        Assert.Single(await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(5), Start.AddHours(1)));
    }

    [Fact]
    public async Task AMoveCheckedWhileWriteBackIsOff_DropsItsTagWrite_ButStillCountsAsDone()
    {
        var repository = Repository;
        UpsertWithMove(_asset);
        var move = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;

        Assert.True(await repository.CompleteMoveAsync(move.Id, move.Generation, writeTags: false));

        var row = (await repository.GetByIdAsync(move.Id))!;
        Assert.Null(row.MoveStep);
        Assert.Equal("blocked", row.Status);
        Assert.Equal(0, (await repository.GetCountsAsync()).Waiting);
        Assert.Empty(await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(5), Start.AddHours(1)));
    }

    [Fact]
    public async Task AnotherMoveAskedForWhileOneIsInProgress_KeepsTheFirstStepAndIsPlannedAgainWhenTheFirstIsChecked()
    {
        var repository = Repository;
        UpsertWithMove(_asset);
        var move = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;
        await repository.RecordMovePlannedAsync(move.Id, "C:/a/old.mp4", "C:/b/new.mp4", "hash-1");

        UpsertWithMove(_asset);

        var during = (await repository.GetByIdAsync(move.Id))!;
        Assert.Equal("planned", during.MoveStep);
        Assert.Equal("C:/b/new.mp4", during.MoveTargetPath);
        Assert.Equal(move.Generation + 1, during.Generation);

        await repository.RecordMoveCheckedAsync(move.Id);
        Assert.False(await repository.CompleteMoveAsync(move.Id, move.Generation));

        var again = Assert.IsType<MediaFileWriteIntent>(await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)));
        Assert.Equal("requested", again.MoveStep);
        Assert.Null(again.MoveTargetPath);
    }

    [Fact]
    public async Task ResettingAMove_ReleasesItsClaimAndAsksForTheMoveAgain()
    {
        var repository = Repository;
        UpsertWithMove(_asset);
        var move = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;
        await repository.RecordMovePlannedAsync(move.Id, "C:/a/old.mp4", "C:/b/new.mp4", "hash-1");

        await repository.ResetMoveAsync(move.Id);

        var row = (await repository.GetByIdAsync(move.Id))!;
        Assert.Equal(("requested", "pending"), (row.MoveStep, row.Status));
        Assert.Null(row.MoveSourcePath);
        Assert.NotNull(await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task TheWaitingMark_NamesBothPlacesOfEveryMoveInProgress_AndNoOtherFile()
    {
        var repository = Repository;
        var other = AddAsset("C:/fixture/untouched.mp4");
        UpsertWithMove(_asset);
        Assert.True((await repository.GetPathsWithMoveInProgressAsync()).SetEquals([Normalise("C:/fixture/movie.mp4")]));
        Assert.True(await repository.HasMoveForAssetAsync(_asset));
        Assert.False(await repository.HasMoveForAssetAsync(other));

        var move = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;
        await repository.RecordMovePlannedAsync(move.Id, "C:/fixture/movie.mp4", "C:/library/Movie (2020)/movie.mp4", null);

        var marked = await repository.GetPathsWithMoveInProgressAsync();
        Assert.True(marked.SetEquals([Normalise("C:/fixture/movie.mp4"), Normalise("C:/library/Movie (2020)/movie.mp4")]));
        Assert.DoesNotContain(Normalise("C:/fixture/untouched.mp4"), await repository.GetPathsAsync());

        await repository.CompleteMoveAsync(move.Id, move.Generation);
        Assert.Empty(await repository.GetPathsWithMoveInProgressAsync());
        Assert.False(await repository.HasMoveForAssetAsync(_asset));
    }

    [Fact]
    public async Task AFileReadBackAfterAnOutsideChange_WaitsForTheNextRun_WithoutSpendingAnAttempt()
    {
        var repository = Repository;
        await repository.EnqueueAssetsAsync([_asset], "auto_match");
        var claimed = Assert.Single(await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(5), Start.AddHours(1)));
        Assert.Equal(1, claimed.Attempts);

        Assert.True(await repository.ReleaseForReReadAsync(claimed.Id, claimed.Generation, "The file changed outside Library."));

        var row = Row<(string Status, long Attempts, string? Error)>(
            "SELECT status AS Status, attempts AS Attempts, last_error AS Error FROM media_file_write_intents WHERE asset_id=@asset;",
            new { asset = _asset });
        Assert.Equal(("pending", 0L, "The file changed outside Library."), (row.Status, row.Attempts, row.Error));
        Assert.True((await repository.GetAssetStateAsync(_asset)).Waiting);
    }

    [Fact]
    public async Task TheScheduleAnchor_OnlyEverMovesForward()
    {
        var repository = Repository;
        await repository.EnsureAnchorAsync(Start);

        await repository.MoveAnchorForwardAsync(Start.AddHours(5));
        await repository.MoveAnchorForwardAsync(Start.AddHours(2));

        Assert.Equal(Start.AddHours(5), DateTimeOffset.Parse((await repository.GetRunStateAsync())!.AnchorAt));
    }

    private static string Normalise(string path) => Path.GetFullPath(path);

    [Fact]
    public async Task ARunOverMoreRowsThanOneChunk_TakesEachRowOnce_AndLeavesRowsChangedMidRunForTheNextRun()
    {
        var repository = Repository;
        var assets = Enumerable.Range(0, 250).Select(_ => AddAsset()).ToList();
        await repository.EnqueueAssetsAsync(assets, "auto_match");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var runStarted = _clock.GetUtcNow();

        var taken = new List<MediaFileWriteIntent>();
        var firstChunk = await repository.ClaimBatchAsync("media_asset", 100, TimeSpan.FromMinutes(5), runStarted, "run-1");
        taken.AddRange(firstChunk);

        // Mid-run: a row the run took is put back to pending (a retry), and a row it has not reached is changed again.
        Assert.True(await repository.CompleteByIdAsync(firstChunk[0].Id, firstChunk[0].Generation, "pending", "try again"));
        var notReached = assets.Except(taken.Select(row => row.AssetId)).First();
        _clock.Advance(TimeSpan.FromSeconds(1));
        await repository.EnqueueAssetsAsync([notReached], "manual_override");

        while (await repository.ClaimBatchAsync("media_asset", 100, TimeSpan.FromMinutes(5), runStarted, "run-1") is { Count: > 0 } chunk)
        {
            taken.AddRange(chunk);
        }

        Assert.Equal(249, taken.Count);
        Assert.Equal(249, taken.Select(row => row.AssetId).Distinct().Count());
        Assert.DoesNotContain(notReached, taken.Select(row => row.AssetId));

        var nextRun = await repository.ClaimBatchAsync("media_asset", 500, TimeSpan.FromMinutes(5), _clock.GetUtcNow(), "run-2");
        Assert.Equivalent(new[] { firstChunk[0].AssetId, notReached }, nextRun.Select(row => row.AssetId));
    }

    [Fact]
    public async Task ADeferredMove_KeepsItsStepsAndPaths_CountsOnlyWhenAsked_AndWaitsForItsNextAttempt()
    {
        var repository = Repository;
        UpsertWithMove(_asset);
        var move = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;
        await repository.RecordMovePlannedAsync(move.Id, "C:/a/old.mp4", "C:/b/new.mp4", "hash-1", move.Generation);

        Assert.Equal(0, await repository.DeferMoveAsync(move.Id, TimeSpan.FromMinutes(5), countAttempt: false, "drive away"));
        Assert.Equal(1, await repository.DeferMoveAsync(move.Id, TimeSpan.FromMinutes(5), countAttempt: true, "could not settle"));

        var row = (await repository.GetByIdAsync(move.Id))!;
        Assert.Equal(("planned", "pending", "C:/a/old.mp4", "C:/b/new.mp4"), (row.MoveStep, row.Status, row.MoveSourcePath, row.MoveTargetPath));
        Assert.Equal(move.Generation, row.MoveGeneration);
        Assert.Empty(await repository.GetStartedMovesAsync(onlyDue: true));
        Assert.Single(await repository.GetStartedMovesAsync(onlyDue: false));

        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Single(await repository.GetStartedMovesAsync(onlyDue: true));
    }

    [Fact]
    public async Task AMoveGivenUp_KeepsItsPathsForDiagnosis_ButNoLongerCountsAsMoving_AndANewAskStartsClean()
    {
        var repository = Repository;
        UpsertWithMove(_asset);
        var move = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;
        await repository.RecordMovePlannedAsync(move.Id, "C:/a/old.mp4", "C:/b/new.mp4", "hash-1");

        Assert.True(await repository.FailMoveAsync(move.Id, "failed", "gone"));

        var failed = (await repository.GetByIdAsync(move.Id))!;
        Assert.Null(failed.MoveStep);
        Assert.Equal(("C:/a/old.mp4", "C:/b/new.mp4"), (failed.MoveSourcePath, failed.MoveTargetPath));
        Assert.Empty(await repository.GetPathsWithMoveInProgressAsync());

        UpsertWithMove(_asset);
        var again = (await repository.GetByIdAsync(move.Id))!;
        Assert.Equal("requested", again.MoveStep);
        Assert.Null(again.MoveSourcePath);
        Assert.Null(again.MoveTargetPath);
    }

    [Fact]
    public async Task OnlyRowsARunCouldTakeCountAsClaimable_WhileWaitingStillCountsEverything()
    {
        var repository = Repository;
        var other = AddAsset();
        await repository.EnqueueAssetsAsync([other], "auto_match");
        UpsertWithMove(_asset);
        var move = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;
        await repository.RecordMovePlannedAsync(move.Id, "C:/a/old.mp4", "C:/b/new.mp4", "hash-1");

        var counts = await repository.GetCountsAsync();

        Assert.Equal(2, counts.Waiting);
        Assert.Equal(1, counts.Claimable);
    }

    [Fact]
    public async Task TheFailedListAndCounts_LeaveOutFilesInHiddenLibraries_WhateverTheCase()
    {
        var repository = Repository;
        var hiddenAsset = AddAsset("C:/vault/secret.mp4");
        var shownAsset = AddAsset("C:/shown/film.mp4");
        var hiddenLibrary = Guid.NewGuid().ToString();
        Execute("UPDATE media_assets SET library_id = @library WHERE id = @asset;", new { library = hiddenLibrary.ToLowerInvariant(), asset = hiddenAsset });
        Execute("UPDATE media_assets SET library_id = 'open-library' WHERE id = @asset;", new { asset = shownAsset });
        await repository.EnqueueAssetsAsync([hiddenAsset, shownAsset], "auto_match");
        Execute("UPDATE media_file_write_intents SET status = 'failed', last_error = 'no' WHERE asset_id IN (@a, @b);", new { a = hiddenAsset, b = shownAsset });
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hiddenLibrary.ToUpperInvariant() };

        Assert.Equal(2, (await repository.GetFailedAsync()).Count);
        Assert.Equal(shownAsset, Assert.Single(await repository.GetFailedAsync(hiddenLibraryIds: hidden)).AssetId);
        Assert.Equal(2, (await repository.GetCountsAsync()).Failed);
        Assert.Equal(1, (await repository.GetCountsAsync(hidden)).Failed);
    }

    private void Execute(string sql, object? args = null)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(sql, args);
    }

    // ----- automatic filing waits for the next run (Library setup D62) ----------------------------------------------------------

    private (string? Step, string? Origin, string Trigger, string Status) FilingRow(Guid asset) =>
        Row<(string?, string?, string, string)>(
            "SELECT move_step, move_origin, trigger, status FROM media_file_write_intents WHERE asset_id = @asset;", new { asset });

    [Fact]
    public async Task AFilingRequest_WaitsForTheNextRun_AndTheFileIsNotMarkedAsMoving()
    {
        var repository = Repository;

        Assert.True(await repository.RequestFilingAsync(_asset, held: false));

        Assert.Equal(("waiting", "filing", "filing", "pending"), FilingRow(_asset));
        Assert.False(await repository.HasMoveForAssetAsync(_asset));
        Assert.Empty(await repository.GetPathsWithMoveInProgressAsync());
        Assert.Null(await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)));
        Assert.Empty(await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(1), Start.AddHours(1)));
        Assert.True((await repository.GetAssetStateAsync(_asset)).Waiting);
        // The item's own waiting line covers it; the sweeps, which only care about tag writes, do not count it (see below).
    }

    [Fact]
    public async Task AskingForFilingAgain_ChangesNothing_SoASweepDoesNotChurnTheRow()
    {
        var repository = Repository;
        await repository.RequestFilingAsync(_asset, held: false);
        var generation = Scalar<long>("SELECT generation FROM media_file_write_intents WHERE asset_id=@asset;", new { asset = _asset });

        Assert.False(await repository.RequestFilingAsync(_asset, held: false));

        Assert.Equal(generation, Scalar<long>("SELECT generation FROM media_file_write_intents WHERE asset_id=@asset;", new { asset = _asset }));
    }

    [Fact]
    public async Task TagsAskedForWhileAFileWaitsToBeFiled_StayOnTheSameRow_AndWaitForTheMove()
    {
        var repository = Repository;
        await repository.RequestFilingAsync(_asset, held: false);

        await repository.EnqueueAssetsAsync([_asset], "auto_match");

        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM media_file_write_intents WHERE asset_id=@asset;", new { asset = _asset }));
        Assert.Equal(("waiting", "filing", "auto_match", "pending"), FilingRow(_asset));
        Assert.Empty(await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(1), Start.AddHours(1)));
    }

    [Fact]
    public async Task FilingAskedForWhileTagsAreWaiting_KeepsTheTagsTrigger_SoTheyAreWrittenAfterTheMove()
    {
        var repository = Repository;
        await repository.EnqueueAssetsAsync([_asset], "auto_match");

        Assert.True(await repository.RequestFilingAsync(_asset, held: false));

        Assert.Equal(("waiting", "filing", "auto_match", "pending"), FilingRow(_asset));
    }

    [Fact]
    public async Task PromotingTheWaitingFilings_MakesThemRequestedMoves_AndNeverTouchesAHeldOne()
    {
        var repository = Repository;
        var held = AddAsset("C:/fixture/held.mp4");
        await repository.RequestFilingAsync(_asset, held: false);
        await repository.RequestFilingAsync(held, held: true);
        Assert.Equal(("held", "filing", "filing", "pending"), FilingRow(held));

        Assert.Equal(1, await repository.PromoteWaitingFilingsAsync());

        Assert.Equal(("requested", "filing", "filing", "pending"), FilingRow(_asset));
        Assert.Equal(("held", "filing", "filing", "pending"), FilingRow(held));
        Assert.Equal(_asset, (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!.AssetId);
        Assert.Null(await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task AHeldRequest_IsNotReleasedByAskingAgain_AndAWaitingOneBecomesHeldWhenTheFolderIsHeld()
    {
        var repository = Repository;
        await repository.RequestFilingAsync(_asset, held: false);

        Assert.True(await repository.RequestFilingAsync(_asset, held: true));
        Assert.False(await repository.RequestFilingAsync(_asset, held: false));

        Assert.Equal("held", FilingRow(_asset).Step);
    }

    [Fact]
    public async Task APersonsMove_TakesOverAWaitingFiling_AndAFilingRequestNeverDisturbsAPersonsMove()
    {
        var repository = Repository;
        var other = AddAsset("C:/fixture/other-file.mp4");
        await repository.RequestFilingAsync(_asset, held: false);
        UpsertWithMove(_asset);
        Assert.Equal(("requested", "person"), (FilingRow(_asset).Step, FilingRow(_asset).Origin));

        UpsertWithMove(other);
        Assert.False(await repository.RequestFilingAsync(other, held: false));

        Assert.Equal(("requested", "person"), (FilingRow(other).Step, FilingRow(other).Origin));
    }

    [Fact]
    public async Task AFilingThatFoundItsFileInUse_GoesBackToWaiting_WithoutCountingAnAttempt()
    {
        var repository = Repository;
        await repository.RequestFilingAsync(_asset, held: false);
        await repository.PromoteWaitingFilingsAsync();
        var move = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;
        Assert.Equal(1, move.Attempts);

        Assert.True(await repository.DeferFilingAsync(move.Id));

        var row = (await repository.GetByIdAsync(move.Id))!;
        Assert.Equal((MediaFileWriteIntentRepository.MoveStepWaiting, "pending", 0), (row.MoveStep, row.Status, row.Attempts));
        Assert.Equal(MediaFileWriteIntentRepository.InUseReason, Scalar<string>("SELECT last_error FROM media_file_write_intents WHERE id=@id;", new { id = move.Id }));
        Assert.Null(await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)));
        Assert.Equal(1, (await repository.GetCountsAsync()).Claimable);
    }

    [Fact]
    public async Task AFilingWhoseTagsNobodyAskedFor_IsRemovedOnceItsMoveIsDone_ButOneWithTagsWaitingKeepsThem()
    {
        var repository = Repository;
        var tagged = AddAsset("C:/fixture/tagged.mp4");
        await repository.RequestFilingAsync(_asset, held: false);
        await repository.EnqueueAssetsAsync([tagged], "auto_match");
        await repository.RequestFilingAsync(tagged, held: false);
        await repository.PromoteWaitingFilingsAsync();

        var first = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;
        var second = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;
        foreach (var move in new[] { first, second })
        {
            await repository.RecordMovePlannedAsync(move.Id, "C:/a.mp4", "C:/b.mp4", null);
            Assert.True(await repository.CompleteMoveAsync(move.Id, move.Generation));
        }

        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM media_file_write_intents WHERE asset_id=@asset;", new { asset = _asset }));
        Assert.Equal((null, null, "auto_match", "pending"), FilingRow(tagged));
        Assert.Single(await repository.ClaimBatchAsync("media_asset", 10, TimeSpan.FromMinutes(1), Start.AddHours(1)));
    }

    [Fact]
    public async Task AFilingWithTagsPending_KeepsThemPendingAfterItsMove_EvenWhenWriteBackIsOff()
    {
        var repository = Repository;
        await repository.EnqueueAssetsAsync([_asset], "auto_match");
        await repository.RequestFilingAsync(_asset, held: false);
        await repository.PromoteWaitingFilingsAsync();
        var move = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;
        await repository.RecordMovePlannedAsync(move.Id, "C:/a.mp4", "C:/b.mp4", null);

        // Write-back is off, so the tags are not wanted now: they stay waiting, as a plain tag row's would, not dropped as blocked.
        Assert.True(await repository.CompleteMoveAsync(move.Id, move.Generation, writeTags: false));

        var row = Row<(string Status, string? Error, string? Step)>(
            "SELECT status, last_error, move_step FROM media_file_write_intents WHERE asset_id=@asset;", new { asset = _asset });
        Assert.Equal(("pending", (string?)null, (string?)null), row);
    }

    [Fact]
    public async Task AFilingWithNoTagsAskedFor_DoesNotHideTheFileFromTheSweeps_AndALaterRetagRequestIsNotLost()
    {
        var repository = Repository;
        await repository.RequestFilingAsync(_asset, held: false);

        // The artwork and retag sweeps skip a file that already has a tag write waiting; a file only waiting to be filed has none.
        Assert.DoesNotContain(_asset, await repository.GetWaitingAssetIdsAsync([_asset]));

        await repository.EnqueueAssetsAsync([_asset], "config_change");

        Assert.Equal(("waiting", "filing", "config_change", "pending"), FilingRow(_asset));
        Assert.Contains(_asset, await repository.GetWaitingAssetIdsAsync([_asset]));
    }

    [Fact]
    public async Task AFilingThatFailed_GoesBackToWaiting_WithTheAttemptCounted_AndIsFailedAtTheLimit()
    {
        var repository = Repository;
        await repository.RequestFilingAsync(_asset, held: false);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await repository.PromoteWaitingFilingsAsync();
            var move = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;
            Assert.Equal(attempt, move.Attempts);

            Assert.True(await repository.FailFilingAttemptAsync(move.Id, "The disk said no.", maxAttempts: 3));

            var row = Row<(string Status, string? Step, string? Error, int Attempts)>(
                "SELECT status, move_step, last_error, attempts FROM media_file_write_intents WHERE asset_id=@asset;", new { asset = _asset });
            if (attempt < 3)
            {
                Assert.Equal(("pending", "waiting", "The disk said no.", attempt), row);
                Assert.Null(await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)));
            }
            else
            {
                Assert.Equal(("failed", (string?)null, "The disk said no.", 3), row);
            }
        }

        Assert.Equal(_asset, Assert.Single(await repository.GetFailedAsync()).AssetId);
    }

    [Fact]
    public async Task AClaimHandedBack_IsPendingAgainWithTheTryNotCounted()
    {
        var repository = Repository;
        UpsertWithMove(_asset);
        var move = (await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)))!;

        await repository.ReleaseClaimAsync(move.Id);

        var row = (await repository.GetByIdAsync(move.Id))!;
        Assert.Equal(("pending", 0), (row.Status, row.Attempts));
        Assert.NotNull(await repository.ClaimNextMoveAsync(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task ARunsHistory_CountsTheFilesItFiledApartFromTheOnesItWrote()
    {
        var repository = Repository;

        await repository.RecordRunAsync(Start, Start.AddMinutes(1), written: 3, failed: 1, kept: 2, filed: 5);

        var state = (await repository.GetRunStateAsync())!;
        Assert.Equal((3, 1, 2, 5), (state.LastRunWritten, state.LastRunFailed, state.LastRunKept, state.LastRunFiled));
    }

    [Fact]
    public async Task TheCounts_TreatAWaitingFilingAsClaimable_AndAHeldOneAsWaitingButNotClaimable()
    {
        var repository = Repository;
        var held = AddAsset("C:/fixture/held.mp4");
        await repository.RequestFilingAsync(_asset, held: false);
        await repository.RequestFilingAsync(held, held: true);

        var counts = await repository.GetCountsAsync();

        Assert.Equal((2, 1, 1), (counts.Waiting, counts.Claimable, counts.FilingWaiting));
    }

    private void UpsertWithMove(Guid asset)
    {
        using var connection = _database.CreateConnection();
        using var transaction = connection.BeginTransaction();
        Assert.True(MediaFileWriteIntentRepository.UpsertAsset(
            connection, transaction, asset, "library_move", "move:1", _clock.GetUtcNow(), requestMove: true));
        transaction.Commit();
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch { }
        try { File.Delete(_path); } catch { }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
