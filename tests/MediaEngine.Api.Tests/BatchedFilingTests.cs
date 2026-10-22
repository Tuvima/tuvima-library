using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.FileWrites;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Library's own filing waits for the batch with the tags (Library setup D62, D63): a newly identified file in a Managed folder is not
/// moved when it is identified, stays exactly where it is and playable until the next run (or Write now), is filed first in that run
/// and has its tags written at its final place; a file in use is left untouched; a held file is never moved. Real temp folders, a real
/// data store and the real organiser; only the tag writer is a spy.
/// </summary>
public sealed class BatchedFilingTests : IDisposable
{
    private readonly LibraryMoveTestWorld _world = new(withFiles: true);

    private MoveFileWorld Files => _world.Files!;

    public void Dispose() => _world.Dispose();

    // ----- waiting for the run ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANewlyIdentifiedFile_IsNotMovedStraightAway_ItIsFiledInTheNextRun_AndItsTagsAreWrittenAtTheFinalPath()
    {
        var song = SongOutsideItsPattern("Paranoid Android", 4);
        Files.WriteBack.PathOf = Files.StoredPath;
        var expected = await ExpectedPathAsync(song.Asset);

        await Files.FilingOrganizer.TryAutoOrganizeAsync(song.Asset);
        // The pipeline asks for the file's tags when it identifies it (the same row).
        await Files.Intents.EnqueueAssetsAsync([song.Asset], "auto_match");

        // Nothing moves, however many passes the Engine makes before the next scheduled run.
        await Files.Scheduler.TickAsync();
        await Files.Scheduler.TickAsync();
        Assert.True(File.Exists(song.OldPath));
        Assert.False(File.Exists(expected));
        Assert.Equal(song.OldPath, Files.StoredPath(song.Asset));
        Assert.Empty(Files.WriteBack.Calls);
        Assert.Equal(("waiting", "filing"), MoveState(song.Asset));

        await Files.Scheduler.RunBatchAsync();

        Assert.False(File.Exists(song.OldPath));
        Assert.True(File.Exists(expected));
        Assert.Equal(expected, Files.StoredPath(song.Asset));
        var call = Assert.Single(Files.WriteBack.Calls);
        Assert.Equal(expected, call.PathAtTheTime);
        Assert.Equal("auto_match", call.Trigger);
        Assert.Equal("verified", Files.Intent(song.Asset).Status);
        Assert.Null(MoveState(song.Asset).Step);
    }

    [Fact]
    public async Task WriteNow_FilesAWaitingFileAtOnce()
    {
        var song = SongOutsideItsPattern("Paranoid Android", 4);
        var expected = await ExpectedPathAsync(song.Asset);
        await Files.FilingOrganizer.TryAutoOrganizeAsync(song.Asset);
        Assert.True(File.Exists(song.OldPath));

        Assert.True(Files.Scheduler.RequestRun());
        await Files.Scheduler.TickAsync();

        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(song.OldPath));
        Assert.Equal(expected, Files.StoredPath(song.Asset));
    }

    [Fact]
    public async Task ALibraryMove_RunsStraightAway_WhileAWaitingFilingStaysUntilTheRun()
    {
        var waiting = SongOutsideItsPattern("Airbag", 1);
        var moved = SongOutsideItsPattern("Paranoid Android", 4);
        var waitingTarget = await ExpectedPathAsync(waiting.Asset);
        var movedTarget = await ExpectedPathAsync(moved.Asset);
        await Files.FilingOrganizer.TryAutoOrganizeAsync(waiting.Asset);

        RequestAPersonsMove(moved.Asset);
        await Files.Scheduler.RunMoveLaneAsync(CancellationToken.None);

        Assert.True(File.Exists(movedTarget));
        Assert.False(File.Exists(moved.OldPath));
        Assert.True(File.Exists(waiting.OldPath));
        Assert.False(File.Exists(waitingTarget));
        Assert.Equal(("waiting", "filing"), MoveState(waiting.Asset));
    }

    [Fact]
    public async Task APersonsMoveAskedForWhileAFilingWaits_TakesItsPlace_AndIsDoneStraightAway()
    {
        var song = SongOutsideItsPattern("Paranoid Android", 4);
        var expected = await ExpectedPathAsync(song.Asset);
        await Files.FilingOrganizer.TryAutoOrganizeAsync(song.Asset);

        RequestAPersonsMove(song.Asset);
        Assert.Equal(("requested", "person"), MoveState(song.Asset));
        await Files.Scheduler.RunMoveLaneAsync(CancellationToken.None);

        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(song.OldPath));
    }

    [Fact]
    public async Task AFilingRequestedWhileAPersonsMoveIsAskedFor_IsDropped_AndTheirMoveStillRuns()
    {
        var song = SongOutsideItsPattern("Paranoid Android", 4);
        var expected = await ExpectedPathAsync(song.Asset);
        RequestAPersonsMove(song.Asset);

        await Files.FilingOrganizer.TryAutoOrganizeAsync(song.Asset);

        Assert.Equal(("requested", "person"), MoveState(song.Asset));
        await Files.Scheduler.RunMoveLaneAsync(CancellationToken.None);
        Assert.True(File.Exists(expected));
    }

    // ----- a waiting file stays put and playable ------------------------------------------------------------------------------

    [Fact]
    public async Task WhileAFileWaits_ItsStoredPathItsSidecarsAndItsPosterAreUnchanged_AndStreamingItWorks()
    {
        var song = SongOutsideItsPattern("Paranoid Android", 4);
        var folder = Path.GetDirectoryName(song.OldPath)!;
        var lyrics = MoveFileWorld.WriteFile(folder, Path.GetFileNameWithoutExtension(song.OldPath) + ".lrc", "la la");
        var poster = MoveFileWorld.WriteFile(folder, "poster.jpg", "poster bytes");
        await Files.FilingOrganizer.TryAutoOrganizeAsync(song.Asset);
        await Files.Scheduler.TickAsync();

        Assert.Equal(song.OldPath, Files.StoredPath(song.Asset));
        Assert.True(File.Exists(song.OldPath));
        Assert.True(File.Exists(lyrics));
        Assert.True(File.Exists(poster));

        // Playing opens the file for reading and shares it, as a stream request does.
        using (var stream = new FileStream(song.OldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(song.Content.Length, stream.Length);
        }

        // The watcher and the daily check treat the file normally at its current path: it has no move in progress.
        Assert.False(await Files.Intents.HasMoveForAssetAsync(song.Asset));
        Assert.Empty(await Files.Intents.GetPathsWithMoveInProgressAsync());

        // Anything that respects the waiting mark still sees pending work on it.
        Assert.Contains(song.Asset, await Files.Intents.GetWaitingAssetIdsAsync([song.Asset]));
        Assert.True((await Files.Intents.GetAssetStateAsync(song.Asset)).Waiting);
    }

    // ----- a file in use is left alone ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AFileOpenExclusivelyByAnotherHandle_IsLeftUntouchedWithNoFailedAttempt_AndFiledInALaterRunWithItsTags()
    {
        var song = SongOutsideItsPattern("Paranoid Android", 4);
        Files.WriteBack.PathOf = Files.StoredPath;
        var expected = await ExpectedPathAsync(song.Asset);
        await Files.FilingOrganizer.TryAutoOrganizeAsync(song.Asset);
        await Files.Intents.EnqueueAssetsAsync([song.Asset], "auto_match");

        using (new FileStream(song.OldPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Files.Scheduler.RunBatchAsync();

            Assert.True(File.Exists(song.OldPath));
            Assert.False(File.Exists(expected));
            Assert.Equal(song.OldPath, Files.StoredPath(song.Asset));
            Assert.Empty(Files.WriteBack.Calls);
            var row = Files.Row(song.Asset);
            Assert.Equal(("waiting", "pending", 0L), (row.MoveStep, row.Status, row.Attempts));
            Assert.Equal(MediaFileWriteIntentRepository.InUseReason, row.LastError);
        }

        await Files.Scheduler.RunBatchAsync();

        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(song.OldPath));
        Assert.Equal(expected, Files.StoredPath(song.Asset));
        Assert.Equal(expected, Assert.Single(Files.WriteBack.Calls).PathAtTheTime);
        Assert.Equal("verified", Files.Intent(song.Asset).Status);
    }

    // ----- filing ignores the tag-writing switch ------------------------------------------------------------------------------

    [Fact]
    public async Task WithTagWritingSwitchedOff_FilingStillHappensOnTheSchedule_AndNoTagIsWritten()
    {
        var song = SongOutsideItsPattern("Paranoid Android", 4);
        var expected = await ExpectedPathAsync(song.Asset);
        var other = SongOutsideItsPattern("Airbag", 1);
        SaveWriteBack(settings => settings.Enabled = false);
        await Files.FilingOrganizer.TryAutoOrganizeAsync(song.Asset);
        // A tag row left from before the switch was turned off is not written by the scheduled run either.
        await Files.Intents.EnqueueAssetsAsync([other.Asset], "auto_match");

        var clock = new FileWriteTestClock(DateTimeOffset.UtcNow);
        var scheduler = new FileWriteScheduler(
            new MediaFileWriteIntentRepository(Files.Database, clock),
            [new MoveFileWorld.SpyMediaAssetWriter(Files.WriteBack)],
            Files.CreateMover(Files.Organizer),
            Files.Configuration,
            clock,
            NullLogger<FileWriteScheduler>.Instance);
        await scheduler.AnchorScheduleAsync(CancellationToken.None);

        var schedule = await scheduler.GetScheduleAsync();
        Assert.NotNull(schedule.NextRunAt);

        Assert.False(await scheduler.TickAsync());
        Assert.True(File.Exists(song.OldPath));

        clock.Advance(TimeSpan.FromMinutes(61));
        await scheduler.TickAsync();

        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(song.OldPath));
        Assert.Empty(Files.WriteBack.Calls);

        // Nobody asked for tags, so filing was the whole job and nothing is left waiting on the file.
        Assert.Equal(0, Files.Scalar<long>("SELECT COUNT(*) FROM media_file_write_intents WHERE asset_id = @asset;", new { asset = song.Asset }));
    }

    [Fact]
    public async Task WriteNow_WithTagWritingSwitchedOff_IsAcceptedForAWaitingFile_AndFilesItWithoutWritingTags()
    {
        var song = SongOutsideItsPattern("Paranoid Android", 4);
        var expected = await ExpectedPathAsync(song.Asset);
        SaveWriteBack(settings => settings.Enabled = false);
        await Files.FilingOrganizer.TryAutoOrganizeAsync(song.Asset);

        var accepted = await SettingsEndpoints.StartFileWriteRunAsync(Files.Scheduler, Files.Configuration, CancellationToken.None);

        Assert.Equal(StatusCodes.Status202Accepted, Assert.IsAssignableFrom<IStatusCodeHttpResult>(accepted).StatusCode);
        await Files.Scheduler.TickAsync();
        Assert.True(File.Exists(expected));
        Assert.Empty(Files.WriteBack.Calls);
    }

    // ----- held for the first check -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHeldRow_IsNeverMoved_NotInARunNorOnWriteNow_AndItsTagsWaitWithIt()
    {
        var song = SongOutsideItsPattern("Paranoid Android", 4);
        var expected = await ExpectedPathAsync(song.Asset);
        var organizer = Files.CreateFilingOrganizer(new HoldEverything());
        await organizer.TryAutoOrganizeAsync(song.Asset);
        await Files.Intents.EnqueueAssetsAsync([song.Asset], "auto_match");
        Assert.Equal(("held", "filing"), MoveState(song.Asset));

        Assert.True(Files.Scheduler.RequestRun());
        await Files.Scheduler.TickAsync();
        await Files.Scheduler.RunBatchAsync();
        await Files.Scheduler.RunMoveLaneAsync(CancellationToken.None);
        // Asking again, while held, does not release it.
        await organizer.TryAutoOrganizeAsync(song.Asset);

        Assert.True(File.Exists(song.OldPath));
        Assert.False(File.Exists(expected));
        Assert.Equal(song.OldPath, Files.StoredPath(song.Asset));
        Assert.Equal(("held", "filing"), MoveState(song.Asset));
        Assert.Empty(Files.WriteBack.Calls);
    }

    // ----- helpers ------------------------------------------------------------------------------------------------------------

    private sealed class HoldEverything : IFilingHold
    {
        public bool IsHeld(string libraryId, string sourceId, string path) => true;
    }

    /// <summary>A song that sits in the Managed folder but not where the library's pattern would put it.</summary>
    private Song SongOutsideItsPattern(string title, int track)
    {
        var album = _world.AddParent("Music", "The Best Of");
        _world.Put(album, "year", "2000");
        var content = $"{title} audio bytes";
        var path = MoveFileWorld.WriteFile(Files.Managed, $"inbox/{track:00} {title}.flac", content);
        var (_, _, assetId) = _world.AddChild(album, "Music", title, track, filePath: path);
        var asset = assetId!.Value;
        _world.Put(asset, "title", title);
        _world.Put(asset, "media_type", "Music");
        _world.Put(asset, "artist", "Radiohead");
        _world.Put(asset, "album", "The Best Of");
        _world.Put(asset, "track_number", track.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new Song(asset, path, content);
    }

    private async Task<string> ExpectedPathAsync(Guid asset)
    {
        var plan = await Files.Organizer.PlanRefileAsync(asset, null);
        Assert.Equal(AssetRefileDisposition.Moves, plan.Disposition);
        return plan.TargetPath!;
    }

    private (string? Step, string? Origin) MoveState(Guid asset)
    {
        using var connection = Files.Database.CreateConnection();
        return connection.QuerySingle<(string?, string?)>(
            "SELECT move_step, move_origin FROM media_file_write_intents WHERE asset_id = @asset;", new { asset });
    }

    private void RequestAPersonsMove(Guid asset)
    {
        using var connection = Files.Database.CreateConnection();
        using var transaction = connection.BeginTransaction();
        MediaFileWriteIntentRepository.UpsertAsset(
            connection, transaction, asset, "library_move", "move:" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, requestMove: true);
        transaction.Commit();
    }

    private void SaveWriteBack(Action<WriteBackConfiguration> change)
    {
        var settings = Files.Configuration.LoadConfig<WriteBackConfiguration>(string.Empty, "writeback") ?? new WriteBackConfiguration();
        change(settings);
        Files.Configuration.SaveConfig(string.Empty, "writeback", settings);
    }

    private sealed record Song(Guid Asset, string OldPath, string Content);
}
