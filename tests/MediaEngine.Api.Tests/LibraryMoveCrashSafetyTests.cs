using Dapper;
using MediaEngine.Api.Services.FileWrites;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A library move that is stopped at any moment ends, after the Engine starts again, with the file in exactly one place that Library
/// knows about and its tags written on the next run (batched write-back D10). Real temp folders, a real data store and the real
/// organiser; only the tag writer is a spy. A stop is made by leaving the disk and the row as the stop would have.
/// </summary>
public sealed class LibraryMoveCrashSafetyTests : IDisposable
{
    private const string OkComputerManifest = """
        {"tracks":[
          {"title":"Airbag","track_number":1,"disc_number":1,"duration_seconds":284},
          {"title":"Paranoid Android","track_number":2,"disc_number":1,"duration_seconds":387}]}
        """;

    private const string KidAManifest = """
        {"tracks":[
          {"title":"Everything in Its Right Place","track_number":1,"disc_number":1,"duration_seconds":251},
          {"title":"Kid A","track_number":2,"disc_number":1,"duration_seconds":284}]}
        """;

    private readonly LibraryMoveTestWorld _world = new(withFiles: true);

    private MoveFileWorld Files => _world.Files!;

    public void Dispose() => _world.Dispose();

    // ----- a move stopped at each step ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AMoveStoppedAfterBeingPlanned_WithTheFileStillAtItsOldPlace_IsTriedAgainAfterARestart()
    {
        var move = await RequestMoveAsync();
        var claimed = await ClaimAsync(move.Asset);
        await Files.Intents.RecordMovePlannedAsync(claimed.Id, move.OldPath, move.NewPath, await HashOfAsync(move.OldPath));

        await RestartAsync();

        Assert.Equal("requested", MoveStep(move.Asset));
        Assert.True(File.Exists(move.OldPath));
        Assert.False(File.Exists(move.NewPath));

        await Files.DrainQueueAsync();

        AssertFileOnlyAt(move.NewPath, move.OldPath);
        Assert.Equal(move.NewPath, Files.StoredPath(move.Asset));
        Assert.Equal(move.NewPath, Assert.Single(Files.WriteBack.Calls).PathAtTheTime);
    }

    [Theory]
    [InlineData("planned")]
    [InlineData("moved")]
    public async Task AMoveStoppedAfterTheFileMoved_ButBeforeLibraryFollowed_IsFinishedAfterARestart(string recordedStep)
    {
        var move = await RequestMoveAsync();
        var claimed = await ClaimAsync(move.Asset);
        await Files.Intents.RecordMovePlannedAsync(claimed.Id, move.OldPath, move.NewPath, await HashOfAsync(move.OldPath));
        Directory.CreateDirectory(Path.GetDirectoryName(move.NewPath)!);
        File.Move(move.OldPath, move.NewPath);
        if (recordedStep == "moved")
        {
            await Files.Intents.RecordMoveMovedAsync(claimed.Id, move.NewPath);
        }

        Assert.Equal(move.OldPath, Files.StoredPath(move.Asset));

        await RestartAsync();

        AssertFileOnlyAt(move.NewPath, move.OldPath);
        Assert.Equal(move.NewPath, Files.StoredPath(move.Asset));
        Assert.Null(MoveStep(move.Asset));
        Assert.Equal("pending", Files.Intent(move.Asset).Status);

        await Files.DrainQueueAsync();

        Assert.Equal(move.NewPath, Assert.Single(Files.WriteBack.Calls).PathAtTheTime);
        Assert.Equal("verified", Files.Intent(move.Asset).Status);
    }

    [Fact]
    public async Task AMoveStoppedAfterBeingCheckedButBeforeItWasCompleted_IsCompletedAfterARestart()
    {
        var move = await RequestMoveAsync();
        var claimed = await ClaimAsync(move.Asset);
        await Files.Intents.RecordMovePlannedAsync(claimed.Id, move.OldPath, move.NewPath, await HashOfAsync(move.OldPath));
        Directory.CreateDirectory(Path.GetDirectoryName(move.NewPath)!);
        File.Move(move.OldPath, move.NewPath);
        Files.Execute("UPDATE media_assets SET file_path_root = @path WHERE id = @asset;", new { path = move.NewPath, asset = move.Asset });
        await Files.Intents.RecordMoveCheckedAsync(claimed.Id);

        await RestartAsync();

        Assert.Null(MoveStep(move.Asset));
        AssertFileOnlyAt(move.NewPath, move.OldPath);
        await Files.DrainQueueAsync();
        Assert.Equal("verified", Files.Intent(move.Asset).Status);
    }

    [Fact]
    public async Task AnInterruptedCopyAcrossDrivesThatFinishedCopying_IsCompletedByRemovingTheOldCopy()
    {
        var move = await RequestMoveAsync();
        var claimed = await ClaimAsync(move.Asset);
        await Files.Intents.RecordMovePlannedAsync(claimed.Id, move.OldPath, move.NewPath, await HashOfAsync(move.OldPath));
        Directory.CreateDirectory(Path.GetDirectoryName(move.NewPath)!);
        File.Copy(move.OldPath, move.NewPath);

        await RestartAsync();

        AssertFileOnlyAt(move.NewPath, move.OldPath);
        Assert.Equal(move.NewPath, Files.StoredPath(move.Asset));
        await Files.DrainQueueAsync();
        Assert.Equal(move.NewPath, Assert.Single(Files.WriteBack.Calls).PathAtTheTime);
    }

    [Fact]
    public async Task AnInterruptedCopyAcrossDrivesThatDidNotFinish_IsUndoneAndTheMoveIsTriedAgain()
    {
        var move = await RequestMoveAsync();
        var claimed = await ClaimAsync(move.Asset);
        await Files.Intents.RecordMovePlannedAsync(claimed.Id, move.OldPath, move.NewPath, await HashOfAsync(move.OldPath));
        Directory.CreateDirectory(Path.GetDirectoryName(move.NewPath)!);
        File.WriteAllText(move.NewPath, "audio");

        await RestartAsync();

        Assert.True(File.Exists(move.OldPath));
        Assert.False(File.Exists(move.NewPath));
        Assert.Equal("requested", MoveStep(move.Asset));
        Assert.Equal(move.OldPath, Files.StoredPath(move.Asset));

        await Files.DrainQueueAsync();

        AssertFileOnlyAt(move.NewPath, move.OldPath);
        Assert.Equal(move.NewPath, Files.StoredPath(move.Asset));
    }

    [Fact]
    public async Task AFileThatVanishedFromBothPlacesDuringAMove_IsMarkedFailedWithAPlainReason_AndTheAssetIsKept()
    {
        var move = await RequestMoveAsync();
        var claimed = await ClaimAsync(move.Asset);
        await Files.Intents.RecordMovePlannedAsync(claimed.Id, move.OldPath, move.NewPath, await HashOfAsync(move.OldPath));
        File.Delete(move.OldPath);
        var assetsBefore = Files.AssetCount();

        await RestartAsync();

        var intent = Files.Intent(move.Asset);
        Assert.Equal("failed", intent.Status);
        Assert.Equal("The file was not found at its old or new place after the Engine stopped mid-move.", intent.Error);
        Assert.Null(MoveStep(move.Asset));
        Assert.Equal(assetsBefore, Files.AssetCount());
        Assert.Equal(1, (await Files.Intents.GetCountsAsync()).Failed);
        Assert.DoesNotContain(await Files.Intents.GetPathsWithMoveInProgressAsync(), path => path.EndsWith(".flac", StringComparison.Ordinal));
    }

    // ----- a drive that cannot be seen is not a file that is gone ------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMoveWhoseFolderCannotBeSeen_IsLeftAsItIs_NotMarkedLost_AndFinishesOnceItIsBack(bool lookedAtByTheMoveLane)
    {
        var move = await RequestMoveAsync();
        var claimed = await ClaimAsync(move.Asset);
        await Files.Intents.RecordMovePlannedAsync(claimed.Id, move.OldPath, move.NewPath, await HashOfAsync(move.OldPath));
        var away = Files.Managed + "_away";
        Directory.Move(Files.Managed, away);
        try
        {
            await RestartAsync();

            Assert.Equal("planned", MoveStep(move.Asset));
            Assert.Equal("pending", Files.Intent(move.Asset).Status);
            Assert.Equal(move.OldPath, Files.Scalar<string>("SELECT move_source_path FROM media_file_write_intents WHERE asset_id = @asset;", new { asset = move.Asset }));
            Assert.Equal(move.NewPath, Files.Scalar<string>("SELECT move_target_path FROM media_file_write_intents WHERE asset_id = @asset;", new { asset = move.Asset }));
            Assert.Equal(0, (await Files.Intents.GetCountsAsync()).Failed);
            Assert.Contains(await Files.Intents.GetPathsWithMoveInProgressAsync(), path => string.Equals(path, move.OldPath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Move(away, Files.Managed);
        }

        if (lookedAtByTheMoveLane)
        {
            // The wait before the next look passes, and the move lane looks again on its next pass.
            Files.Execute("UPDATE media_file_write_intents SET next_attempt_at = NULL;");
            await Files.DrainQueueAsync();
        }
        else
        {
            await RestartAsync();
            await Files.DrainQueueAsync();
        }

        AssertFileOnlyAt(move.NewPath, move.OldPath);
        Assert.Equal(move.NewPath, Files.StoredPath(move.Asset));
        Assert.Null(MoveStep(move.Asset));
        Assert.Equal("verified", Files.Intent(move.Asset).Status);
    }

    [Fact]
    public async Task AFileThatVanishedFromBothPlaces_WhileTheirFoldersCanBeSeen_IsKeptOnTheFailedListWithItsPaths()
    {
        var move = await RequestMoveAsync();
        var claimed = await ClaimAsync(move.Asset);
        await Files.Intents.RecordMovePlannedAsync(claimed.Id, move.OldPath, move.NewPath, await HashOfAsync(move.OldPath));
        File.Delete(move.OldPath);

        await RestartAsync();

        Assert.Equal("failed", Files.Intent(move.Asset).Status);
        Assert.Equal(move.OldPath, Files.Scalar<string>("SELECT move_source_path FROM media_file_write_intents WHERE asset_id = @asset;", new { asset = move.Asset }));
        Assert.Equal(move.NewPath, Files.Scalar<string>("SELECT move_target_path FROM media_file_write_intents WHERE asset_id = @asset;", new { asset = move.Asset }));
        Assert.Empty(await Files.Intents.GetPathsWithMoveInProgressAsync());
    }

    // ----- a move that cannot be settled does not hide real deletions forever ----------------------------------------------------------

    [Fact]
    public async Task AMoveThatCannotBeSettledAfterSeveralTries_IsGivenUpWithItsPathsKept_AndNoLongerCountsAsMoving()
    {
        var move = await RequestMoveAsync();
        var claimed = await ClaimAsync(move.Asset);
        await Files.Intents.RecordMovePlannedAsync(claimed.Id, move.OldPath, move.NewPath, await HashOfAsync(move.OldPath));
        Directory.CreateDirectory(Path.GetDirectoryName(move.NewPath)!);
        File.Move(move.OldPath, move.NewPath);
        await Files.Intents.RecordMoveMovedAsync(claimed.Id, move.NewPath);
        var mover = Files.CreateMover(new NeverFollowsOrganizer(Files.Organizer));

        for (var start = 1; start < LibraryMoveRunner.MaxRecoveryAttempts; start++)
        {
            await mover.RecoverInterruptedAsync(CancellationToken.None);
            Assert.Equal("moved", MoveStep(move.Asset));
            Assert.Contains(await Files.Intents.GetPathsWithMoveInProgressAsync(), path => string.Equals(path, move.NewPath, StringComparison.OrdinalIgnoreCase));
        }

        await mover.RecoverInterruptedAsync(CancellationToken.None);

        var intent = Files.Intent(move.Asset);
        Assert.Equal("failed", intent.Status);
        Assert.Null(MoveStep(move.Asset));
        Assert.StartsWith(LibraryMoveRunner.GaveUpReason, intent.Error, StringComparison.Ordinal);
        Assert.Equal(move.NewPath, Files.Scalar<string>("SELECT move_target_path FROM media_file_write_intents WHERE asset_id = @asset;", new { asset = move.Asset }));
        Assert.Empty(await Files.Intents.GetPathsWithMoveInProgressAsync());
        Assert.True(File.Exists(move.NewPath));
    }

    // ----- an old place that is not Library's -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AFileFoundAtTheOldPlaceAfterTheMoveFinished_IsNotLibrarysAndIsLeftAlone()
    {
        var move = await RequestMoveAsync();
        var claimed = await ClaimAsync(move.Asset);
        await Files.Intents.RecordMovePlannedAsync(claimed.Id, move.OldPath, move.NewPath, await HashOfAsync(move.OldPath));
        Directory.CreateDirectory(Path.GetDirectoryName(move.NewPath)!);
        File.Move(move.OldPath, move.NewPath);
        await Files.Intents.RecordMoveMovedAsync(claimed.Id, move.NewPath);
        File.WriteAllText(move.OldPath, "someone else put this here");

        await RestartAsync();

        Assert.Equal(move.NewPath, Files.StoredPath(move.Asset));
        Assert.Null(MoveStep(move.Asset));
        Assert.Equal("someone else put this here", File.ReadAllText(move.OldPath));
        Assert.True(File.Exists(move.NewPath));
    }

    [Fact]
    public async Task AnOldCopyThatChangedAfterTheMoveWasPlanned_IsNotDeleted_EvenWhenTheNewCopyIsComplete()
    {
        var move = await RequestMoveAsync();
        var claimed = await ClaimAsync(move.Asset);
        await Files.Intents.RecordMovePlannedAsync(claimed.Id, move.OldPath, move.NewPath, await HashOfAsync(move.OldPath));
        Directory.CreateDirectory(Path.GetDirectoryName(move.NewPath)!);
        File.Copy(move.OldPath, move.NewPath);
        File.AppendAllText(move.OldPath, " edited since");

        await RestartAsync();

        Assert.True(File.Exists(move.OldPath));
        Assert.True(File.Exists(move.NewPath));
        Assert.Equal("planned", MoveStep(move.Asset));
        Assert.Equal(1, Files.Scalar<int>("SELECT recovery_attempts FROM media_file_write_intents WHERE asset_id = @asset;", new { asset = move.Asset }));
    }

    [Fact]
    public async Task ANewNameTakenAfterThePlanWasMade_DropsTheMove_LeavesTheOtherFileAlone_AndTheMoveIsTriedAgain()
    {
        var move = await RequestMoveAsync();
        await ClaimAsync(move.Asset);
        var recorder = new NameTakingRecorder(move.NewPath);

        var outcome = await Files.Organizer.RefileAsync(move.Asset, recorder);

        Assert.Equal(MediaEngine.Domain.Models.AssetRefileOutcomeKind.Failed, outcome.Kind);
        Assert.True(recorder.NotMoved);
        Assert.Equal("someone else's file", File.ReadAllText(move.NewPath));
        Assert.True(File.Exists(move.OldPath));
        Assert.Equal(move.OldPath, Files.StoredPath(move.Asset));
        Assert.Equal("requested", MoveStep(move.Asset));
    }

    [Fact]
    public async Task AMoveSettledOnTheSpotAfterAStop_IsCompletedOnce()
    {
        var move = await RequestMoveAsync();
        var claimed = await ClaimAsync(move.Asset);
        var stopsAfterMoving = new MoveThenFailOrganizer(new FileOrganizer(NullLogger<FileOrganizer>.Instance));
        var mover = Files.CreateMover(Files.CreateOrganizer(stopsAfterMoving));

        var result = await mover.MoveAsync(claimed, CancellationToken.None);

        Assert.Equal(LibraryMoveRunner.MoveResultKind.Done, result.Kind);
        Assert.True(result.Completed);
        AssertFileOnlyAt(move.NewPath, move.OldPath);
        Assert.Equal(move.NewPath, Files.StoredPath(move.Asset));
        Assert.Null(MoveStep(move.Asset));
    }

    // ----- the record while the move runs ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ARunningMove_HasItsPlanOnTheRowBeforeTheFileMoves_AndNothingLeftOnceLibraryFollowed()
    {
        var move = await RequestMoveAsync();
        var seen = new List<(string? Step, string? Source, string? Target, string? Hash)>();
        var observing = new ObservingFileOrganizer(new FileOrganizer(NullLogger<FileOrganizer>.Instance), () =>
        {
            using var connection = Files.Database.CreateConnection();
            seen.Add(connection.QuerySingle<(string?, string?, string?, string?)>(
                "SELECT move_step, move_source_path, move_target_path, move_source_hash FROM media_file_write_intents WHERE asset_id = @asset;",
                new { asset = move.Asset }));
        });
        var scheduler = Files.CreateScheduler(Files.CreateOrganizer(observing));

        await scheduler.RunMoveLaneAsync(CancellationToken.None);

        var planned = Assert.Single(seen);
        Assert.Equal("planned", planned.Step);
        Assert.Equal(move.OldPath, planned.Source);
        Assert.Equal(move.NewPath, planned.Target);
        Assert.False(string.IsNullOrWhiteSpace(planned.Hash));
        Assert.Null(MoveStep(move.Asset));
        using var connection = Files.Database.CreateConnection();
        Assert.Null(connection.ExecuteScalar<string?>(
            "SELECT move_target_path FROM media_file_write_intents WHERE asset_id = @asset;", new { asset = move.Asset }));
        AssertFileOnlyAt(move.NewPath, move.OldPath);
        Assert.Empty(await Files.Intents.GetPathsWithMoveInProgressAsync());
    }

    [Fact]
    public async Task AMoveWhileWriteBackIsOff_StillMovesTheFile_ButItsTagWriteIsDropped()
    {
        Files.Configuration.SaveConfig(string.Empty, "writeback", new WriteBackConfiguration { Enabled = false });
        var move = await RequestMoveAsync();

        await Files.DrainQueueAsync();

        AssertFileOnlyAt(move.NewPath, move.OldPath);
        Assert.Equal(move.NewPath, Files.StoredPath(move.Asset));
        Assert.Empty(Files.WriteBack.Calls);
        var intent = Files.Intent(move.Asset);
        Assert.Equal("blocked", intent.Status);
        Assert.Equal(MediaFileWriteIntentRepository.MoveTagsBlockedReason, intent.Error);
    }

    // ----- a second move while the first is in progress ---------------------------------------------------------------------------

    [Fact]
    public async Task ASecondMoveAskedForWhileTheFirstIsInProgress_EndsWithTheFileInTheSecondAlbum()
    {
        var first = await RequestMoveAsync();
        var claimed = await ClaimAsync(first.Asset);
        var result = await Files.CreateMover(Files.Organizer).MoveAsync(claimed, CancellationToken.None);
        Assert.Equal(LibraryMoveRunner.MoveResultKind.Done, result.Kind);
        AssertFileOnlyAt(first.NewPath, first.OldPath);

        // The person moves the same song again before the first move was marked done.
        var kidA = _world.AddParent("Music", "Kid A", manifestJson: KidAManifest);
        _world.Put(kidA, "year", "2000");
        var preview = await PreviewAsync([first.Song], kidA);
        var second = await CommitAsync(preview, Match(first.Song, preview.Entries.Single(entry => entry.Title == "Kid A").EntryKey));
        Assert.True(second.IsSuccess, second.Refusal?.Message);

        Assert.False(await Files.Intents.CompleteMoveAsync(claimed.Id, claimed.Generation));
        Assert.Equal("requested", MoveStep(first.Asset));

        await Files.DrainQueueAsync();

        var stored = Files.StoredPath(first.Asset);
        Assert.Equal("Kid A (2000)", Path.GetFileName(Path.GetDirectoryName(stored)));
        Assert.True(File.Exists(stored));
        Assert.False(File.Exists(first.NewPath));
        Assert.False(File.Exists(first.OldPath));
        Assert.Null(MoveStep(first.Asset));
        Assert.Equal("verified", Files.Intent(first.Asset).Status);
    }

    [Theory]
    [InlineData("planned")]
    [InlineData("moved")]
    public async Task ASecondMoveAskedForWhileTheFirstWasPartWayAndTheEngineStopped_EndsWithTheFileInTheSecondAlbum(string recordedStep)
    {
        var first = await RequestMoveAsync();
        var claimed = await ClaimAsync(first.Asset);
        await Files.Intents.RecordMovePlannedAsync(claimed.Id, first.OldPath, first.NewPath, await HashOfAsync(first.OldPath), claimed.Generation);

        // The person moves the same song again while the first move is part-way; then the Engine stops.
        var kidA = _world.AddParent("Music", "Kid A", manifestJson: KidAManifest);
        _world.Put(kidA, "year", "2000");
        var preview = await PreviewAsync([first.Song], kidA);
        var second = await CommitAsync(preview, Match(first.Song, preview.Entries.Single(entry => entry.Title == "Kid A").EntryKey));
        Assert.True(second.IsSuccess, second.Refusal?.Message);
        Assert.Equal("planned", MoveStep(first.Asset));

        Directory.CreateDirectory(Path.GetDirectoryName(first.NewPath)!);
        File.Move(first.OldPath, first.NewPath);
        if (recordedStep == "moved")
        {
            await Files.Intents.RecordMoveMovedAsync(claimed.Id, first.NewPath);
        }

        await RestartAsync();

        Assert.Equal("requested", MoveStep(first.Asset));
        await Files.DrainQueueAsync();

        var stored = Files.StoredPath(first.Asset);
        Assert.Equal("Kid A (2000)", Path.GetFileName(Path.GetDirectoryName(stored)));
        Assert.True(File.Exists(stored));
        Assert.False(File.Exists(first.NewPath));
        Assert.False(File.Exists(first.OldPath));
        Assert.Null(MoveStep(first.Asset));
        Assert.Equal("verified", Files.Intent(first.Asset).Status);
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private sealed record RequestedMove(Guid Song, Guid Asset, string OldPath, string NewPath);

    /// <summary>A song in another album, moved to track 2 of OK Computer: the library change is saved and the file move is requested.</summary>
    private async Task<RequestedMove> RequestMoveAsync()
    {
        var okComputer = _world.AddParent("Music", "OK Computer", manifestJson: OkComputerManifest);
        _world.Put(okComputer, "year", "1997");
        var bestOf = _world.AddParent("Music", "The Best Of");
        _world.Put(bestOf, "year", "2000");
        var oldPath = MoveFileWorld.WriteFile(Files.Managed, "Music/Radiohead/The Best Of (2000)/04 - Paranoid Android.flac");
        var (song, _, assetId) = _world.AddChild(bestOf, "Music", "Paranoid Android", 4, filePath: oldPath);
        var asset = assetId!.Value;
        _world.Put(asset, "title", "Paranoid Android");
        _world.Put(asset, "media_type", "Music");
        _world.Put(asset, "artist", "Radiohead");
        _world.Put(asset, "album", "The Best Of");
        _world.Put(asset, "track_number", "4");
        Files.WriteBack.PathOf = Files.StoredPath;

        var preview = await PreviewAsync([song], okComputer);
        var committed = await CommitAsync(preview, Match(song, preview.Rows[0].SuggestedEntryKey!));
        Assert.True(committed.IsSuccess, committed.Refusal?.Message);

        var newPath = Path.GetFullPath(Path.Combine(Files.Managed, "Music", "Radiohead", "OK Computer (1997)", "02 - Paranoid Android.flac"));
        Assert.Equal("requested", MoveStep(asset));
        return new RequestedMove(song, asset, oldPath, newPath);
    }

    /// <summary>The Engine takes the move off the list (as the move lane does) and then stops.</summary>
    private async Task<MediaFileWriteIntent> ClaimAsync(Guid asset)
    {
        var claimed = await Files.Intents.ClaimNextMoveAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(asset, claimed!.AssetId);
        return claimed;
    }

    /// <summary>The Engine starts again: what the stop left part-way is looked at before the move lane begins.</summary>
    private async Task RestartAsync() => await Files.CreateMover(Files.Organizer).RecoverInterruptedAsync(CancellationToken.None);

    private static async Task<string> HashOfAsync(string path) => (await new AssetHasher().ComputeAsync(path)).Hex;

    private string? MoveStep(Guid asset) => Files.Scalar<string?>(
        "SELECT move_step FROM media_file_write_intents WHERE asset_id = @asset;", new { asset });

    private static void AssertFileOnlyAt(string expected, string notThere)
    {
        Assert.True(File.Exists(expected), $"The file should be at {expected}.");
        Assert.False(File.Exists(notThere), $"The file should not still be at {notThere}.");
    }

    private async Task<LibraryMovePreview> PreviewAsync(IReadOnlyList<Guid> items, Guid destination)
    {
        var result = await _world.Service.PreviewAsync(
            new LibraryMovePreviewRequest { ItemIds = items, DestinationId = destination },
            LibraryMoveTestWorld.Actor,
            LibraryMoveTestWorld.Everything);
        Assert.True(result.IsSuccess, result.Refusal?.Message);
        return result.Value!;
    }

    private Task<LibraryMoveResult<LibraryMoveCommitResult>> CommitAsync(LibraryMovePreview preview, params LibraryMoveCommitRow[] rows) =>
        _world.Service.CommitAsync(
            new LibraryMoveCommitRequest { PreviewToken = preview.PreviewToken, Rows = rows },
            LibraryMoveTestWorld.Actor,
            LibraryMoveTestWorld.Everything);

    private static LibraryMoveCommitRow Match(Guid item, string entryKey) =>
        new() { ItemId = item, Mode = LibraryMoveModes.Match, EntryKey = entryKey };

    /// <summary>An organiser whose data store never follows an interrupted move (the asset went away, say).</summary>
    private sealed class NeverFollowsOrganizer(MediaEngine.Domain.Contracts.IAutoOrganizeService inner) : MediaEngine.Domain.Contracts.IAutoOrganizeService
    {
        public Task TryAutoOrganizeAsync(Guid assetId, CancellationToken ct = default) => inner.TryAutoOrganizeAsync(assetId, ct);

        public Task<MediaEngine.Domain.Models.AssetRefilePlan> PlanRefileAsync(
            Guid assetId, MediaEngine.Domain.Models.AssetRefilePlacement? placement, CancellationToken ct = default) =>
            inner.PlanRefileAsync(assetId, placement, ct);

        public Task<MediaEngine.Domain.Models.AssetRefileOutcome> RefileAsync(Guid assetId, CancellationToken ct = default) =>
            inner.RefileAsync(assetId, ct);

        public Task CompleteInterruptedMoveAsync(Guid assetId, string sourcePath, string targetPath, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>Takes the new name between the plan and the move, as another program could.</summary>
    private sealed class NameTakingRecorder(string targetPath) : MediaEngine.Domain.Contracts.IRefileStepRecorder
    {
        public bool NotMoved { get; private set; }

        public Task PlannedAsync(string sourcePath, string targetPath1, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.WriteAllText(targetPath, "someone else's file");
            return Task.CompletedTask;
        }

        public Task NotMovedAsync(CancellationToken ct)
        {
            NotMoved = true;
            return Task.CompletedTask;
        }

        public Task MovedAsync(string actualPath, CancellationToken ct) => Task.CompletedTask;

        public Task CheckedAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Moves the file for real and then fails, as a stop right after the move would.</summary>
    private sealed class MoveThenFailOrganizer(IFileOrganizer inner) : IFileOrganizer
    {
        public string CalculatePath(MediaEngine.Ingestion.Models.IngestionCandidate candidate, string template) =>
            inner.CalculatePath(candidate, template);

        public string? ValidateTemplate(string template, out string? error) => inner.ValidateTemplate(template, out error);

        public async Task<string?> ExecuteMoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
        {
            await inner.ExecuteMoveAsync(sourcePath, destinationPath, ct);
            throw new IOException("The Engine stopped right after the file moved.");
        }
    }

    /// <summary>Lets a test look at the data store at the moment the file is about to be moved, then moves it for real.</summary>
    private sealed class ObservingFileOrganizer(IFileOrganizer inner, Action beforeMove) : IFileOrganizer
    {
        public string CalculatePath(MediaEngine.Ingestion.Models.IngestionCandidate candidate, string template) =>
            inner.CalculatePath(candidate, template);

        public string? ValidateTemplate(string template, out string? error) => inner.ValidateTemplate(template, out error);

        public Task<string?> ExecuteMoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
        {
            beforeMove();
            return inner.ExecuteMoveAsync(sourcePath, destinationPath, ct);
        }
    }
}
