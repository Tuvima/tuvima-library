using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Constants;
using MediaEngine.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// What a move does to files (Details overlay D19), over real temp folders and a real data store: in a Managed folder the file is
/// renamed and moved to fit its new place; As-is, read-only and protected files are never touched; a name that is taken is never
/// overwritten; and the file work is a durable job that runs after the library change is saved.
/// </summary>
public sealed class LibraryMoveFileTests : IDisposable
{
    private const string OkComputerManifest = """
        {"tracks":[
          {"title":"Airbag","track_number":1,"disc_number":1,"duration_seconds":284},
          {"title":"Paranoid Android","track_number":2,"disc_number":1,"duration_seconds":387},
          {"title":"Subterranean Homesick Alien","track_number":3,"disc_number":1,"duration_seconds":267}]}
        """;

    private readonly LibraryMoveTestWorld _world = new(withFiles: true);

    private MoveFileWorld Files => _world.Files!;

    public void Dispose() => _world.Dispose();

    // ----- managed songs: the preview says it, the job does it ---------------------------------------------------------------

    [Fact]
    public async Task AManagedSongMovedToAnotherAlbumAsTrack2_IsPreviewedWithItsNewNameAndPlace_AndFiledThereByTheJob()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out var asset);
        var assetCountBefore = Files.AssetCount();
        await Files.HashCache.UpsertAsync(oldPath, new FileInfo(oldPath).Length, new FileInfo(oldPath).LastWriteTimeUtc, "hash-before");

        var preview = await PreviewAsync([song], okComputer);
        var row = Assert.Single(preview.Rows);
        var expected = Path.GetFullPath(Path.Combine(Files.Managed, "Music", "Radiohead", "OK Computer (1997)", "02 - Paranoid Android.flac"));
        Assert.Equal(Path.GetFullPath(oldPath), row.FileChange!.From);
        Assert.Equal(expected, row.FileChange.To);
        Assert.Null(row.FileNote);

        var moved = await CommitAsync(preview, Match(song, row.SuggestedEntryKey!));

        // The library change is saved first; the file work waits in the durable queue.
        Assert.True(moved.IsSuccess);
        Assert.Equal(okComputer, _world.Work(song).Parent);
        Assert.True(File.Exists(oldPath));
        var queued = Files.Intent(asset);
        Assert.Equal("pending", queued.Status);
        Assert.Equal(MediaFileWriteTriggers.LibraryMove, queued.Trigger);

        await Files.DrainQueueAsync();

        // The job did what the preview said: the file is at the previewed path, the stored path follows, nothing was ingested twice.
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(expected));
        Assert.Equal(expected, Files.StoredPath(asset));
        Assert.Equal(assetCountBefore, Files.AssetCount());
        Assert.Equal("hash-before", await Files.HashCache.TryGetAsync(expected, new FileInfo(expected).Length, new FileInfo(expected).LastWriteTimeUtc));
        Assert.Contains(Files.ActivityDetails(asset), detail => detail.StartsWith("Re-organized after a library move: 02 - Paranoid Android.flac", StringComparison.Ordinal));
        Assert.Equal("verified", Files.Intent(asset).Status);
    }

    [Fact]
    public async Task TheTagsAreWrittenAfterTheFileHasMoved_SoTheyLandOnTheFinalFile()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out _, out var asset);
        Files.WriteBack.PathOf = Files.StoredPath;
        var preview = await PreviewAsync([song], okComputer);
        await CommitAsync(preview, Match(song, preview.Rows[0].SuggestedEntryKey!));

        await Files.DrainQueueAsync();

        var call = Assert.Single(Files.WriteBack.Calls);
        Assert.Equal(asset, call.AssetId);
        Assert.Equal(MediaFileWriteTriggers.LibraryMove, call.Trigger);
        Assert.EndsWith("02 - Paranoid Android.flac", call.PathAtTheTime, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASongAddedAnywayIsStillMovedIntoTheAlbumFolder_AndTheRowSaysItWillBeRenamedToFit()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Hidden Bonus Jam", 9, Files.Managed, out var oldPath, out var asset);

        var preview = await PreviewAsync([song], okComputer);
        var row = Assert.Single(preview.Rows);
        Assert.Null(row.SuggestedEntryKey);
        Assert.Null(row.FileChange);
        Assert.Equal(MoveFilePlanner.RenameToFitNote, row.FileNote);

        await CommitAsync(preview, AddAnyway(song));
        await Files.DrainQueueAsync();

        Assert.False(File.Exists(oldPath));
        var stored = Files.StoredPath(asset);
        Assert.True(File.Exists(stored));
        Assert.Equal("OK Computer (1997)", Path.GetFileName(Path.GetDirectoryName(stored)));
    }

    // ----- the file line follows the person's choice, not the Engine's guess ---------------------------------------------------

    [Fact]
    public async Task APickedEntryThatIsNotTheSuggestedOne_ShowsThatEntrysFileName_AndTheCommitFilesItThere()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out _, out var asset);

        var suggested = await PreviewAsync([song], okComputer);
        var guess = Assert.Single(suggested.Rows);
        Assert.EndsWith("02 - Paranoid Android.flac", guess.FileChange!.To, StringComparison.Ordinal);

        // The person picks track 3 instead: the preview made for that choice names the file for track 3.
        var track3 = suggested.Entries.Single(entry => entry.Title == "Subterranean Homesick Alien").EntryKey;
        Assert.NotEqual(guess.SuggestedEntryKey, track3);
        var chosen = await PreviewAsync([song], okComputer, Match(song, track3));
        var row = Assert.Single(chosen.Rows);
        Assert.Equal(guess.SuggestedEntryKey, row.SuggestedEntryKey);
        Assert.StartsWith("03 - ", Path.GetFileName(row.FileChange!.To), StringComparison.Ordinal);
        Assert.NotEqual(guess.FileChange.To, row.FileChange.To);

        var moved = await CommitAsync(chosen, Match(song, track3));
        Assert.True(moved.IsSuccess, moved.Refusal?.Message);
        await Files.DrainQueueAsync();

        Assert.Equal(row.FileChange.To, Files.StoredPath(asset));
        Assert.True(File.Exists(row.FileChange.To));
    }

    [Fact]
    public async Task ACommitThatChoosesDifferentlyFromTheChoicesPreviewed_IsAChange_WithAFreshPreviewForWhatIsCommitted()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out _);
        var preview = await PreviewAsync([song], okComputer);
        var track3 = preview.Entries.Single(entry => entry.Title == "Subterranean Homesick Alien").EntryKey;
        var chosen = await PreviewAsync([song], okComputer, Match(song, track3));

        // The dialog named the file for track 3, so committing the Engine's guess (track 2) would file a name nobody saw.
        var refused = await CommitAsync(chosen, Match(song, preview.Rows[0].SuggestedEntryKey!));

        Assert.False(refused.IsSuccess);
        Assert.Equal(LibraryMoveRefusalKind.Changed, refused.Refusal!.Kind);
        var fresh = Assert.Single(refused.Refusal.FreshPreview!.Rows);
        Assert.EndsWith("02 - Paranoid Android.flac", fresh.FileChange!.To, StringComparison.Ordinal);
        Assert.True(File.Exists(oldPath));
        Assert.NotEqual(okComputer, _world.Work(song).Parent);
    }

    [Fact]
    public async Task APreviewWithAddAnywayChosen_ShowsTheFileNameWithNoNumber()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Hidden Bonus Jam", 9, Files.Managed, out _, out var asset);

        var preview = await PreviewAsync([song], okComputer, AddAnyway(song));
        var row = Assert.Single(preview.Rows);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Files.Managed, "Music", "Radiohead", "OK Computer (1997)", "Hidden Bonus Jam.flac")),
            row.FileChange!.To);

        var moved = await CommitAsync(preview, AddAnyway(song));
        Assert.True(moved.IsSuccess, moved.Refusal?.Message);
        await Files.DrainQueueAsync();

        Assert.Equal(row.FileChange.To, Files.StoredPath(asset));
        Assert.True(File.Exists(row.FileChange.To));
    }

    [Fact]
    public async Task AnItemWithNoChoice_KeepsItsSuggestedEntryForTheFileLine_WhileAnotherItemsChoiceIsUsed()
    {
        var (okComputer, _) = OkComputerAlbum();
        var first = BestOfSong("Paranoid Android", 4, Files.Managed, out _, out _);
        var second = BestOfSong("Subterranean Homesick Alien", 5, Files.Managed, out _, out _);
        var preview = await PreviewAsync([first, second], okComputer);
        Assert.NotNull(preview.Rows.Single(row => row.ItemId == second).SuggestedEntryKey);

        // Only the first item has a choice; the second keeps its suggestion.
        var chosen = await PreviewAsync([first, second], okComputer, AddAnyway(first));

        Assert.Equal("Paranoid Android.flac", Path.GetFileName(chosen.Rows.Single(row => row.ItemId == first).FileChange!.To));
        Assert.EndsWith("03 - Subterranean Homesick Alien.flac", chosen.Rows.Single(row => row.ItemId == second).FileChange!.To, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASongWithTwoFiles_IsSummarisedInOneLine_AndBothFilesAreFiled()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var flac, out var flacAsset);
        var mp3 = MoveFileWorld.WriteFile(Files.Managed, "Music/Radiohead/The Best Of (2000)/04 - Paranoid Android.mp3", "mp3 bytes");
        var mp3Asset = AddSecondFile(song, mp3, "Paranoid Android", 4);

        var preview = await PreviewAsync([song], okComputer);
        var row = Assert.Single(preview.Rows);
        Assert.Null(row.FileChange);
        Assert.Equal("2 files will be renamed and moved to OK Computer (1997).", row.FileNote);

        await CommitAsync(preview, Match(song, row.SuggestedEntryKey!));
        await Files.DrainQueueAsync();

        Assert.False(File.Exists(flac));
        Assert.False(File.Exists(mp3));
        Assert.EndsWith("02 - Paranoid Android.flac", Files.StoredPath(flacAsset), StringComparison.Ordinal);
        Assert.EndsWith("02 - Paranoid Android.mp3", Files.StoredPath(mp3Asset), StringComparison.Ordinal);
        Assert.All([flacAsset, mp3Asset], assetId => Assert.True(File.Exists(Files.StoredPath(assetId))));
    }

    [Fact]
    public async Task CompanionFilesThatShareTheSongsName_MoveWithIt()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out _);
        var folder = Path.GetDirectoryName(oldPath)!;
        File.WriteAllText(Path.Combine(folder, "04 - Paranoid Android.lrc"), "[00:01.00]lyrics");
        File.WriteAllText(Path.Combine(folder, "04 - Paranoid Android.cue"), "FILE cue");

        var preview = await PreviewAsync([song], okComputer);
        await CommitAsync(preview, Match(song, preview.Rows[0].SuggestedEntryKey!));
        await Files.DrainQueueAsync();

        var target = Path.Combine(Files.Managed, "Music", "Radiohead", "OK Computer (1997)");
        Assert.True(File.Exists(Path.Combine(target, "02 - Paranoid Android.flac")));
        Assert.Equal("[00:01.00]lyrics", File.ReadAllText(Path.Combine(target, "02 - Paranoid Android.lrc")));
        Assert.True(File.Exists(Path.Combine(target, "02 - Paranoid Android.cue")));
        Assert.False(File.Exists(Path.Combine(folder, "04 - Paranoid Android.lrc")));
        Assert.False(File.Exists(Path.Combine(folder, "04 - Paranoid Android.cue")));
    }

    // ----- files that are never touched --------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAsIsSong_IsMovedInTheLibraryOnly_AndItsFileIsByteIdenticalAtItsOriginalPath()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.AsIs, out var oldPath, out var asset);
        var before = File.ReadAllBytes(oldPath);
        var stamp = File.GetLastWriteTimeUtc(oldPath);

        var preview = await PreviewAsync([song], okComputer);
        var row = Assert.Single(preview.Rows);
        Assert.Null(row.FileChange);
        Assert.Equal(MoveFilePlanner.AsIsNote, row.FileNote);

        var moved = await CommitAsync(preview, Match(song, row.SuggestedEntryKey!));
        await Files.DrainQueueAsync();

        Assert.True(moved.IsSuccess);
        Assert.Equal(okComputer, _world.Work(song).Parent);
        Assert.Equal(before, File.ReadAllBytes(oldPath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(oldPath));
        Assert.Equal(Path.GetFullPath(oldPath), Path.GetFullPath(Files.StoredPath(asset)));
        Assert.Equal([oldPath], Directory.EnumerateFiles(Files.AsIs, "*", SearchOption.AllDirectories).ToArray());
        Assert.Contains(Files.ActivityDetails(asset), detail => detail.StartsWith("Kept in place after a library move", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFileInAReadOnlyFolder_IsNeverTouched_AndTheRowSaysSo()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.ReadOnly, out var oldPath, out _);

        var preview = await PreviewAsync([song], okComputer);
        await CommitAsync(preview, Match(song, preview.Rows[0].SuggestedEntryKey!));
        await Files.DrainQueueAsync();

        Assert.Equal(MoveFilePlanner.ReadOnlyNote, preview.Rows[0].FileNote);
        Assert.Null(preview.Rows[0].FileChange);
        Assert.True(File.Exists(oldPath));
        Assert.Equal([oldPath], Directory.EnumerateFiles(Files.ReadOnly, "*", SearchOption.AllDirectories).ToArray());
    }

    [Fact]
    public async Task AMergeOriginal_IsProtected_ItsFileIsNotTouched_AndTheRowSaysSo()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out var asset);
        await Files.MarkMergeOriginalAsync(Path.GetFullPath(oldPath));
        var before = File.ReadAllBytes(oldPath);

        var preview = await PreviewAsync([song], okComputer);
        var row = Assert.Single(preview.Rows);
        Assert.Null(row.FileChange);
        Assert.Equal(MoveFilePlanner.ProtectedNote, row.FileNote);

        var moved = await CommitAsync(preview, Match(song, row.SuggestedEntryKey!));
        await Files.DrainQueueAsync();

        Assert.True(moved.IsSuccess);
        Assert.Equal(okComputer, _world.Work(song).Parent);
        Assert.Equal(before, File.ReadAllBytes(oldPath));
        Assert.Equal(Path.GetFullPath(oldPath), Path.GetFullPath(Files.StoredPath(asset)));
    }

    [Fact]
    public async Task ANameThatIsAlreadyTaken_IsFlaggedInThePreview_AndTheJobNeverOverwritesIt()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out var asset);
        var taken = MoveFileWorld.WriteFile(
            Files.Managed, "Music/Radiohead/OK Computer (1997)/02 - Paranoid Android.flac", "somebody else's file");

        var preview = await PreviewAsync([song], okComputer);
        var row = Assert.Single(preview.Rows);
        Assert.Null(row.FileChange);
        Assert.Equal(MoveFilePlanner.TakenNote, row.FileNote);
        Assert.Contains(preview.Warnings, warning => warning.Contains("02 - Paranoid Android.flac is already there", StringComparison.Ordinal));
        Assert.Null(row.BlockedReason);

        var moved = await CommitAsync(preview, Match(song, row.SuggestedEntryKey!));
        await Files.DrainQueueAsync();

        // The library change stands; both files are exactly as they were.
        Assert.True(moved.IsSuccess);
        Assert.Equal(okComputer, _world.Work(song).Parent);
        Assert.Equal("somebody else's file", File.ReadAllText(taken));
        Assert.Equal("audio bytes", File.ReadAllText(oldPath));
        Assert.Equal(Path.GetFullPath(oldPath), Path.GetFullPath(Files.StoredPath(asset)));
        Assert.Contains(Files.ActivityDetails(asset), detail => detail.StartsWith("Kept in place after a library move", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFileThatBecomesProtectedBetweenPreviewAndConfirm_GetsAFreshPreview_AndNothingMoves()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out _);
        var preview = await PreviewAsync([song], okComputer);
        Assert.NotNull(preview.Rows[0].FileChange);

        await Files.MarkMergeOriginalAsync(Path.GetFullPath(oldPath));
        var result = await CommitAsync(preview, Match(song, preview.Rows[0].SuggestedEntryKey!));

        Assert.Equal(LibraryMoveRefusalKind.Changed, result.Refusal!.Kind);
        var fresh = Assert.Single(result.Refusal.FreshPreview!.Rows);
        Assert.Equal(MoveFilePlanner.ProtectedNote, fresh.FileNote);
        Assert.NotEqual(okComputer, _world.Work(song).Parent);
        Assert.Equal(0, Files.Scalar<long>("SELECT COUNT(*) FROM media_file_write_intents;"));
    }

    [Fact]
    public async Task AFileThatBecomesProtectedAfterTheLibraryChange_IsSkippedByTheJob_AndTheLibraryChangeStands()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out var asset);
        var preview = await PreviewAsync([song], okComputer);
        await CommitAsync(preview, Match(song, preview.Rows[0].SuggestedEntryKey!));

        // Protected after the library change was saved, before the job looked at the file: the job asks the policy again.
        await Files.MarkMergeOriginalAsync(Path.GetFullPath(oldPath));
        await Files.DrainQueueAsync();

        Assert.Equal(okComputer, _world.Work(song).Parent);
        Assert.Equal(2, _world.Work(song).Ordinal);
        Assert.True(File.Exists(oldPath));
        Assert.Equal(Path.GetFullPath(oldPath), Path.GetFullPath(Files.StoredPath(asset)));
        Assert.Contains(Files.ActivityDetails(asset), detail => detail.Contains("protected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TwoSongsOfOneMoveThatWouldTakeTheSameName_NeverOverwriteEachOther()
    {
        var (okComputer, _) = OkComputerAlbum();
        var first = BestOfSong("Hidden Jam", 9, Files.Managed, out var firstPath, out var firstAsset);
        var second = BestOfSong("Hidden Jam", 10, Files.Managed, out var secondPath, out var secondAsset);
        File.WriteAllText(secondPath, "the other jam");

        var preview = await PreviewAsync([first, second], okComputer);
        await CommitAsync(preview, AddAnyway(first), AddAnyway(second));
        await Files.DrainQueueAsync();

        var stored = new[] { Files.StoredPath(firstAsset), Files.StoredPath(secondAsset) };
        Assert.Equal(2, stored.Select(File.ReadAllText).Distinct().Count());
        Assert.All(stored, path => Assert.True(File.Exists(path)));
        Assert.Equal(1, new[] { firstPath, secondPath }.Count(File.Exists));
    }

    // ----- TV --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEpisodeMovedToAnotherSeason_IsRenamedForThatSeasonAndEpisode_UnderTheSameShowFolder()
    {
        var show = _world.AddParent("TV", "Northwatch");
        _world.Put(show, "year", "2020");
        var collection = _world.AddSeriesCollection("tvdb:77", [
            ("s2e3", "tvdb:77:season:2", 3, "The lost reel", null)]);
        _world.Scalar<long>("UPDATE works SET collection_id = @collection WHERE id = @show; SELECT 0;", new { collection, show });
        var seasonOne = _world.AddParent("TV", string.Empty, show, ordinal: 1);
        var seasonTwo = _world.AddParent("TV", string.Empty, show, ordinal: 2);
        var oldPath = MoveFileWorld.WriteFile(Files.Managed, "TV/Northwatch (2020)/Season 01/Northwatch - s01e05 - The lost reel.mkv");
        var (episode, _, asset) = _world.AddChild(seasonOne, "TV", "The lost reel", 5, filePath: oldPath);
        _world.Put(asset!.Value, "title", "The lost reel");
        _world.Put(asset.Value, "media_type", "TV");
        _world.Put(asset.Value, "season_number", "1");
        _world.Put(asset.Value, "episode_number", "5");

        var preview = await PreviewAsync([episode], seasonTwo);
        var row = Assert.Single(preview.Rows);
        var expected = Path.GetFullPath(Path.Combine(Files.Managed, "TV", "Northwatch (2020)", "Season 02", "Northwatch - s02e03 - The lost reel.mkv"));
        Assert.Equal(expected, row.FileChange!.To);

        await CommitAsync(preview, Match(episode, row.SuggestedEntryKey!));
        await Files.DrainQueueAsync();

        Assert.Equal(expected, Files.StoredPath(asset.Value));
        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(oldPath));
    }

    // ----- audiobooks --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAudiobookPartMovedIntoAnotherAudiobook_TakesItsPartNumberAndFolder()
    {
        var (bookOne, bookOneParts) = Audiobook("Book One", [(1, "Part 1"), (2, "Part 2")], partCount: 2);
        var (bookTwo, _) = Audiobook("Book Two", [(1, "Part 1")], partCount: 3);
        var movingPart = bookOneParts[1];
        var oldPath = Files.StoredPath(movingPart.AssetId);

        var preview = await PreviewAsync([movingPart.EditionId], bookTwo);
        var row = Assert.Single(preview.Rows);
        Assert.Equal("part:2", row.SuggestedEntryKey);
        Assert.Equal(oldPath, row.FileChange!.From);
        Assert.StartsWith("Book Two", Path.GetFileName(Path.GetDirectoryName(row.FileChange.To)), StringComparison.Ordinal);
        Assert.StartsWith("02 ", Path.GetFileName(row.FileChange.To), StringComparison.Ordinal);

        var moved = await CommitAsync(preview, Match(movingPart.EditionId, row.SuggestedEntryKey!));
        await Files.DrainQueueAsync();

        Assert.True(moved.IsSuccess);
        Assert.Equal(bookTwo, _world.WorkOfAsset(movingPart.AssetId));
        var stored = Files.StoredPath(movingPart.AssetId);
        Assert.Equal(row.FileChange.To, stored);
        Assert.True(File.Exists(stored));
        Assert.False(File.Exists(oldPath));
        Assert.Equal(bookOne, _world.WorkOfAsset(bookOneParts[0].AssetId));
    }

    // ----- the durable job -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AJobThatWasBeingWorkedOnWhenTheEngineStopped_IsPickedUpAgain_AndFinishesTheMove()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out var asset);
        var preview = await PreviewAsync([song], okComputer);
        await CommitAsync(preview, Match(song, preview.Rows[0].SuggestedEntryKey!));

        // The Engine claimed the row and stopped before doing anything; its lease runs out.
        var claimed = await Files.Intents.ClaimNextMoveAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(asset, claimed!.AssetId);
        Files.Execute("UPDATE media_file_write_intents SET lease_expires_at = '2000-01-01T00:00:00.0000000+00:00' WHERE status = 'writing';");
        Assert.True(File.Exists(oldPath));

        var handled = await Files.DrainQueueAsync();
        var again = await Files.DrainQueueAsync();

        Assert.True(handled);
        Assert.False(again);
        Assert.False(File.Exists(oldPath));
        Assert.EndsWith("02 - Paranoid Android.flac", Files.StoredPath(asset), StringComparison.Ordinal);
        Assert.Equal("verified", Files.Intent(asset).Status);
    }

    [Fact]
    public async Task AFileThatCouldNotBeMovedThisTime_StaysWhereItWas_ReportsWhy_AndIsTriedAgain()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out var asset);
        var preview = await PreviewAsync([song], okComputer);
        await CommitAsync(preview, Match(song, preview.Rows[0].SuggestedEntryKey!));
        var flaky = new MoveFileWorld.FlakyFileOrganizer(new FileOrganizer(NullLogger<FileOrganizer>.Instance), failures: 1);
        var scheduler = Files.CreateScheduler(Files.CreateOrganizer(flaky));

        await scheduler.RunMoveLaneAsync(CancellationToken.None);
        var afterFirst = Files.Intent(asset);

        Assert.True(File.Exists(oldPath));
        Assert.Equal("pending", afterFirst.Status);
        Assert.Contains("could not be renamed and moved", afterFirst.Error, StringComparison.Ordinal);

        // The wait before the next try passes.
        Files.Execute("UPDATE media_file_write_intents SET next_attempt_at = NULL;");
        await Files.DrainQueueAsync(scheduler);

        Assert.False(File.Exists(oldPath));
        Assert.EndsWith("02 - Paranoid Android.flac", Files.StoredPath(asset), StringComparison.Ordinal);
        Assert.Equal("verified", Files.Intent(asset).Status);
    }

    [Fact]
    public async Task AFileThatIsGoneFromDisk_IsReportedInTheHistory_AndTheLibraryChangeStands()
    {
        var (okComputer, _) = OkComputerAlbum();
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out var asset);
        var preview = await PreviewAsync([song], okComputer);
        await CommitAsync(preview, Match(song, preview.Rows[0].SuggestedEntryKey!));
        File.Delete(oldPath);

        await Files.DrainQueueAsync();

        Assert.Equal(okComputer, _world.Work(song).Parent);
        Assert.Contains(Files.ActivityDetails(asset), detail => detail.Contains("The file is not on disk.", StringComparison.Ordinal));
    }

    // ----- places that never touch a file ---------------------------------------------------------------------------------------

    [Fact]
    public async Task MovingIntoAHandPickedCollection_SaysNothingAboutFiles_AndMovesNone()
    {
        var song = BestOfSong("Paranoid Android", 4, Files.Managed, out var oldPath, out _);
        var collection = _world.AddCollection("Road trip");

        var preview = await PreviewAsync([song], collection);
        var moved = await CommitAsync(preview, new LibraryMoveCommitRow { ItemId = song, Mode = LibraryMoveModes.Match });
        await Files.DrainQueueAsync();

        var row = Assert.Single(preview.Rows);
        Assert.Null(row.FileChange);
        Assert.Null(row.FileNote);
        Assert.True(moved.IsSuccess);
        Assert.True(File.Exists(oldPath));
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private (Guid Album, Guid Airbag) OkComputerAlbum()
    {
        var album = _world.AddParent("Music", "OK Computer", manifestJson: OkComputerManifest);
        _world.Put(album, "year", "1997");
        var (airbag, _, _) = _world.AddChild(album, "Music", "Airbag", 1);
        return (album, airbag);
    }

    /// <summary>A song in "The Best Of" (2000) with its real file in the given folder, named the way the organiser would.</summary>
    private Guid BestOfSong(string title, int track, string folder, out string path, out Guid asset)
    {
        var bestOf = _world.AddParent("Music", "The Best Of");
        _world.Put(bestOf, "year", "2000");
        path = MoveFileWorld.WriteFile(folder, $"Music/Radiohead/The Best Of (2000)/{track:00} - {title}.flac");
        var (work, _, assetId) = _world.AddChild(bestOf, "Music", title, track, filePath: path);
        asset = assetId!.Value;
        DescribeSongFile(asset, title, track, "The Best Of");
        return work;
    }

    private void DescribeSongFile(Guid asset, string title, int track, string album)
    {
        _world.Put(asset, "title", title);
        _world.Put(asset, "media_type", "Music");
        _world.Put(asset, "artist", "Radiohead");
        _world.Put(asset, "album", album);
        _world.Put(asset, "track_number", track.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private Guid AddSecondFile(Guid work, string path, string title, int track)
    {
        var asset = Guid.NewGuid();
        _world.Execute(
            """
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
            VALUES (@asset, (SELECT id FROM editions WHERE work_id = @work LIMIT 1), @hash, @path, CURRENT_TIMESTAMP);
            """,
            new { asset, work, hash = Guid.NewGuid().ToString("N"), path });
        DescribeSongFile(asset, title, track, "The Best Of");
        return asset;
    }

    private (Guid Work, List<(Guid EditionId, Guid AssetId)> Parts) Audiobook(string title, IReadOnlyList<(int Number, string Title)> parts, int partCount)
    {
        var (work, _) = _world.AddAudiobook(title, [], partCount);
        var made = new List<(Guid EditionId, Guid AssetId)>();
        foreach (var (number, partTitle) in parts)
        {
            var path = MoveFileWorld.WriteFile(Files.Managed, $"Audiobooks/Jane Author/{title}/{number:00} {title}.m4b", $"{title} {number}");
            var (edition, asset) = _world.AddPart(work, number, partTitle, partCount, path);
            _world.Put(asset, "media_type", "Audiobooks");
            _world.Put(asset, "title", partTitle);
            _world.Put(asset, "author", "Jane Author");
            made.Add((edition, asset));
        }

        return (work, made);
    }

    private async Task<LibraryMovePreview> PreviewAsync(IReadOnlyList<Guid> items, Guid destination, params LibraryMoveCommitRow[] choices)
    {
        var result = await _world.Service.PreviewAsync(
            new LibraryMovePreviewRequest { ItemIds = items, DestinationId = destination, Choices = choices.Length == 0 ? null : choices },
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

    private static LibraryMoveCommitRow AddAnyway(Guid item) =>
        new() { ItemId = item, Mode = LibraryMoveModes.AddAnyway };
}
