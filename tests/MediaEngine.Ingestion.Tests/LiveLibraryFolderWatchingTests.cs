using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Pipeline;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Tests.Helpers;
using MediaEngine.Intelligence;
using MediaEngine.Intelligence.Models;
using MediaEngine.Processors.Models;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// Adding, removing or changing a library folder is picked up by the running ingestion engine with no restart: a new
/// folder is watched and scanned, a removed one stops being watched, a changed one is restarted, an unchanged one is left
/// alone, and a read-only folder is only ever read. All on temp folders and a real SQLite data store.
/// </summary>
public sealed class LiveLibraryFolderWatchingTests : IDisposable
{
    private const string BooksLibraryId = "bbbbbbbb-0000-4000-8000-000000000001";
    private const string BooksSourceId = "books-asis";

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task A_folder_added_while_running_is_watched_and_its_existing_files_are_scanned()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var bookPath = WriteFile(h.AsIsDir, "Dune.epub", "dune book");
        var run = await StartAsync(h, watcher, BooksProcessor(bookPath), BuildOptions(h));
        Assert.False(watcher.IsWatching(h.AsIsDir));

        run.Monitor.Publish(BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir))));

        await WaitUntilAsync(() => Task.FromResult(watcher.IsWatching(h.AsIsDir)));
        await WaitUntilAsync(async () => await h.Assets.FindByPathRootAsync(bookPath) is not null);
        Assert.Equal(1, watcher.CountEntries(h.AsIsDir, "add"));
        Assert.True(watcher.IsRecursive(h.AsIsDir));
    }

    [Fact]
    public async Task A_folder_with_scanning_off_is_not_watched_or_scanned_until_scanning_is_switched_on()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var bookPath = WriteFile(h.AsIsDir, "Dune.epub", "dune book");
        var run = await StartAsync(h, watcher, BooksProcessor(bookPath), BuildOptions(h));
        var paused = AsIsSource(h.AsIsDir);
        paused.ScanEnabled = false;

        run.Monitor.Publish(BuildOptions(h, BooksLibrary(paused)));
        await Task.Delay(2000);

        Assert.False(watcher.IsWatching(h.AsIsDir));
        Assert.Null(await h.Assets.FindByPathRootAsync(bookPath));

        run.Monitor.Publish(BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir))));

        await WaitUntilAsync(() => Task.FromResult(watcher.IsWatching(h.AsIsDir)));
        await WaitUntilAsync(async () => await h.Assets.FindByPathRootAsync(bookPath) is not null);
    }

    [Fact]
    public async Task A_folder_removed_and_added_back_skips_files_the_known_path_filter_already_knows()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var logger = new CapturingLogger<IngestionEngine>();
        var bookPath = WriteFile(h.AsIsDir, "Dune.epub", "dune book");
        var run = await StartAsync(
            h, watcher, BooksProcessor(bookPath), BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir))),
            logger: logger, withHashCache: true);
        await WaitUntilAsync(async () => await h.Assets.FindByPathRootAsync(bookPath) is not null);

        // The asset row is written well before the ingest finishes; a folder re-added while that ingest is still running
        // rightly resumes it instead of skipping it, so wait for the first ingest to be done.
        var operations = new MediaOperationRepository(h.Db.Connection);
        await WaitUntilAsync(async () => await operations.GetLatestBySourcePathAsync(bookPath) is not null
            && await operations.GetActiveBySourcePathAsync(bookPath) is null);
        Assert.Equal(1, await AssetCountAsync(h));

        // The asset row appears before the file's intake job finishes. Re-adding the folder while that job is still open
        // resumes the file instead of skipping it as known, so the first intake must be over before the folder is removed.
        await WaitUntilAsync(async () => await OpenOperationCountAsync(h) == 0);

        run.Monitor.Publish(BuildOptions(h));
        await WaitUntilAsync(() => Task.FromResult(!watcher.IsWatching(h.AsIsDir)));
        var logLinesBefore = logger.Messages.Count;
        run.Monitor.Publish(BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir))));
        await WaitUntilAsync(() => Task.FromResult(watcher.IsWatching(h.AsIsDir)));

        // The re-added folder's scan finds the file already catalogued and queues nothing (not even for hash dedup).
        await WaitUntilAsync(() => Task.FromResult(logger.Messages.Skip(logLinesBefore)
            .Any(message => message.StartsWith("Initial scan: skipped 1 already-known", StringComparison.Ordinal))));
        await Task.Delay(1500);
        Assert.DoesNotContain(
            logger.Messages.Skip(logLinesBefore),
            message => message.StartsWith("Initial scan: enqueued", StringComparison.Ordinal));
        Assert.Equal(1, await AssetCountAsync(h));
    }

    [Fact]
    public async Task A_save_made_before_the_engine_started_listening_is_not_lost()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var bookPath = WriteFile(h.AsIsDir, "Dune.epub", "dune book");

        // The Books folder is saved while the engine is still waiting (for example for first-run setup to finish).
        await StartAsync(
            h, watcher, BooksProcessor(bookPath), BuildOptions(h),
            beforeStart: monitor => monitor.Publish(BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir)))));

        Assert.True(watcher.IsWatching(h.AsIsDir));
        await WaitUntilAsync(async () => await h.Assets.FindByPathRootAsync(bookPath) is not null);
        Assert.Equal(1, watcher.CountEntries(h.AsIsDir, "add"));
    }

    [Fact]
    public async Task Stopping_never_waits_for_a_rebuild_stuck_on_a_slow_folder()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var run = await StartAsync(h, watcher, new TestProcessorLibraryItem(), BuildOptions(h));
        watcher.BlockAddsFor(h.AsIsDir);

        run.Monitor.Publish(BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir))));
        Assert.True(watcher.AddEntered.Wait(TimeSpan.FromSeconds(10)), "the rebuild should reach the slow folder");

        // The rebuild now holds its lock inside the slow call; unsubscribing must not need that lock.
        var stopping = Task.Run(run.Stop);
        await WaitUntilAsync(() => Task.FromResult(run.Monitor.ListenerCount == 0), TimeSpan.FromSeconds(5));

        watcher.ReleaseAdd.Set();
        await stopping;
    }

    [Fact]
    public async Task A_watcher_that_already_matches_is_kept_and_one_with_another_sub_folder_setting_is_restarted()
    {
        var h = NewHarness();
        var matching = new RecordingFileWatcher();
        matching.AddDirectory(h.AsIsDir, includeSubdirectories: true);
        await StartAsync(h, matching, new TestProcessorLibraryItem(), BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir))));
        Assert.Equal(1, matching.CountEntries(h.AsIsDir, "add"));
        Assert.Equal(0, matching.CountEntries(h.AsIsDir, "remove"));

        var h2 = NewHarness();
        var mismatched = new RecordingFileWatcher();
        mismatched.AddDirectory(h2.AsIsDir, includeSubdirectories: false);
        await StartAsync(h2, mismatched, new TestProcessorLibraryItem(), BuildOptions(h2, BooksLibrary(AsIsSource(h2.AsIsDir))));
        Assert.Equal(1, mismatched.CountEntries(h2.AsIsDir, "remove"));
        Assert.True(mismatched.IsRecursive(h2.AsIsDir));
    }

    [Fact]
    public async Task A_folder_removed_while_running_stops_being_watched_and_the_rest_keep_running()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var run = await StartAsync(h, watcher, new TestProcessorLibraryItem(), BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir))));
        Assert.True(watcher.IsWatching(h.AsIsDir));
        Assert.True(watcher.IsWatching(h.MoviesDir));
        Assert.True(watcher.IsWatching(h.ImportDir));

        run.Monitor.Publish(BuildOptions(h));

        await WaitUntilAsync(() => Task.FromResult(!watcher.IsWatching(h.AsIsDir)));
        Assert.Equal(1, watcher.CountEntries(h.AsIsDir, "remove"));
        Assert.True(watcher.IsWatching(h.MoviesDir));
        Assert.True(watcher.IsWatching(h.ImportDir));
        Assert.Equal(0, watcher.CountEntries(h.MoviesDir, "remove"));
        Assert.Equal(0, watcher.CountEntries(h.ImportDir, "remove"));
    }

    [Fact]
    public async Task Changing_the_sub_folder_setting_restarts_only_that_folder_and_scans_it()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var nested = WriteFile(Path.Combine(h.AsIsDir, "Series"), "Dune.epub", "dune book");
        var run = await StartAsync(h, watcher, BooksProcessor(nested), BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir, includeSubdirectories: false))));
        Assert.False(watcher.IsRecursive(h.AsIsDir));
        Assert.Equal(0, await AssetCountAsync(h));

        run.Monitor.Publish(BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir, includeSubdirectories: true))));

        await WaitUntilAsync(() => Task.FromResult(watcher.IsRecursive(h.AsIsDir) == true));
        await WaitUntilAsync(async () => await h.Assets.FindByPathRootAsync(nested) is not null);
        Assert.Equal(1, watcher.CountEntries(h.AsIsDir, "remove"));
        Assert.Equal(2, watcher.CountEntries(h.AsIsDir, "add"));
        Assert.Equal(1, watcher.CountEntries(h.MoviesDir, "add"));
        Assert.Equal(0, watcher.CountEntries(h.MoviesDir, "remove"));
    }

    [Fact]
    public async Task A_folder_that_did_not_change_is_not_restarted()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var run = await StartAsync(h, watcher, new TestProcessorLibraryItem(), BuildOptions(h));

        // Saving the same settings again, and saving with an extra folder, never touch the unchanged folders.
        run.Monitor.Publish(BuildOptions(h));
        run.Monitor.Publish(BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir))));
        await WaitUntilAsync(() => Task.FromResult(watcher.IsWatching(h.AsIsDir)));
        run.Monitor.Publish(BuildOptions(h));
        await Task.Delay(300);

        Assert.Equal(1, watcher.CountEntries(h.MoviesDir, "add"));
        Assert.Equal(1, watcher.CountEntries(h.ImportDir, "add"));
        Assert.Equal(0, watcher.CountEntries(h.MoviesDir, "remove"));
        Assert.Equal(0, watcher.CountEntries(h.ImportDir, "remove"));
        Assert.Equal(1, watcher.CountEntries(h.AsIsDir, "add"));
    }

    [Fact]
    public async Task Rapid_saves_end_with_exactly_the_folders_of_the_last_save()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var run = await StartAsync(h, watcher, new TestProcessorLibraryItem(), BuildOptions(h));

        for (var i = 0; i < 20; i++)
        {
            run.Monitor.Publish(i % 2 == 0
                ? BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir)))
                : BuildOptions(h));
        }

        // The last save (i = 19) removed the Books folder.
        await WaitUntilAsync(() => Task.FromResult(
            !watcher.IsWatching(h.AsIsDir)
            && watcher.CountEntries(h.AsIsDir, "add") == watcher.CountEntries(h.AsIsDir, "remove")));
        await Task.Delay(300);

        Assert.False(watcher.IsWatching(h.AsIsDir));
        Assert.Equal(watcher.CountEntries(h.AsIsDir, "add"), watcher.CountEntries(h.AsIsDir, "remove"));
        Assert.Equal(1, watcher.WatchedPaths.Count(p => SamePath(p, h.MoviesDir)));
        Assert.Equal(1, watcher.CountEntries(h.MoviesDir, "add"));
    }

    [Fact]
    public async Task A_read_only_folder_is_watched_and_scanned_without_anything_being_written_to_it()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var bookPath = WriteFile(h.AsIsDir, "Dune.epub", "dune book");
        WriteFile(Path.Combine(h.AsIsDir, "Sub"), "Arrakis.epub", "arrakis book");
        var before = Snapshot(h.AsIsDir);
        var run = await StartAsync(h, watcher, BooksProcessor(bookPath), BuildOptions(h));

        run.Monitor.Publish(BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir))));

        await WaitUntilAsync(() => Task.FromResult(watcher.IsWatching(h.AsIsDir)));
        await WaitUntilAsync(async () => await h.Assets.FindByPathRootAsync(bookPath) is not null);
        await Task.Delay(300);

        Assert.Equal(before, Snapshot(h.AsIsDir));
    }

    [Fact]
    public async Task A_missing_read_only_folder_is_not_created_and_not_watched()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var offline = Path.Combine(h.Root, "offline-drive", "books");
        var run = await StartAsync(h, watcher, new TestProcessorLibraryItem(), BuildOptions(h));

        // A second, valid folder in the same save proves the save was applied.
        run.Monitor.Publish(BuildOptions(h, BooksLibrary(AsIsSource(offline), AsIsSource(h.AsIsDir, "books-asis-2"))));
        await WaitUntilAsync(() => Task.FromResult(watcher.IsWatching(h.AsIsDir)));

        Assert.False(watcher.IsWatching(offline));
        Assert.False(Directory.Exists(offline));
        Assert.False(Directory.Exists(Path.GetDirectoryName(offline)));
    }

    [Fact]
    public async Task With_the_real_watcher_a_new_folder_picks_up_new_files_and_a_removed_folder_does_not()
    {
        var h = NewHarness();
        var watcher = new FileWatcher();
        _disposables.Add(watcher);
        var existing = WriteFile(h.AsIsDir, "Dune.epub", "dune book");
        var live = Path.Combine(h.AsIsDir, "Messiah.epub");
        var afterRemoval = Path.Combine(h.AsIsDir, "Children.epub");
        var control = Path.Combine(h.MoviesDir, "Control.mkv");
        var processors = BooksProcessor(existing, live, afterRemoval);
        processors.SetResultForPath(control, new ProcessorResult
        {
            FilePath = control,
            DetectedType = MediaType.Movies,
            Claims = [new ExtractedClaim { Key = "title", Value = "Control", Confidence = 0.9 }],
        });
        var run = await StartAsync(h, watcher, processors, BuildOptions(h));

        // Added while running: the existing file is scanned and a file that appears afterwards is picked up live.
        run.Monitor.Publish(BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir))));
        await WaitUntilAsync(() => Task.FromResult(watcher.WatchedPaths.Any(p => SamePath(p, h.AsIsDir))));
        await WaitUntilAsync(async () => await h.Assets.FindByPathRootAsync(existing) is not null);
        File.WriteAllText(live, "messiah book");
        await WaitUntilAsync(async () => await h.Assets.FindByPathRootAsync(live) is not null, TimeSpan.FromSeconds(30));

        // Removed while running: a new file there is no longer picked up, while a control file elsewhere still is.
        run.Monitor.Publish(BuildOptions(h));
        await WaitUntilAsync(() => Task.FromResult(!watcher.WatchedPaths.Any(p => SamePath(p, h.AsIsDir))));
        File.WriteAllText(afterRemoval, "children book");
        File.WriteAllText(control, "control movie");
        await WaitUntilAsync(async () => await AssetCountAsync(h) >= 3, TimeSpan.FromSeconds(30));
        await Task.Delay(2500);

        Assert.Null(await h.Assets.FindByPathRootAsync(afterRemoval));
        Assert.Equal(3, await AssetCountAsync(h));
        Assert.Contains(watcher.WatchedPaths, p => SamePath(p, h.MoviesDir));
    }

    [Fact]
    public async Task Stopping_the_engine_unsubscribes_and_a_late_save_is_ignored()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var run = await StartAsync(h, watcher, new TestProcessorLibraryItem(), BuildOptions(h));
        Assert.Equal(1, run.Monitor.ListenerCount);

        run.Stop();
        Assert.Equal(0, run.Monitor.ListenerCount);

        run.Monitor.Publish(BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir))));
        await Task.Delay(200);
        Assert.False(watcher.IsWatching(h.AsIsDir));
    }

    [Fact]
    public void A_drive_or_filesystem_root_keeps_its_separator_when_normalised_and_other_folders_lose_theirs()
    {
        var normalise = typeof(IngestionEngine).GetMethod(
            "NormalizeDirectoryPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(normalise);

        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Equal(root, (string)normalise.Invoke(null, [root])!);

        var folder = Path.Combine(Path.GetTempPath(), "tuvima_normalise_check");
        Assert.Equal(folder, (string)normalise.Invoke(null, [folder + Path.DirectorySeparatorChar])!);
    }

    [Fact]
    public async Task A_rescan_does_not_ingest_a_file_that_stayed_on_disk_when_its_title_was_deleted_and_does_not_read_it()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var logger = new CapturingLogger<IngestionEngine>();
        var hasher = new CountingHasher();
        var deleted = WriteFile(h.AsIsDir, "Dune.epub", "dune book");
        var kept = WriteFile(h.AsIsDir, "Emma.epub", "emma book");
        var excluded = new ExcludedSourceFileRepository(h.Db.Connection);
        await ExcludeAsync(excluded, deleted);
        var filter = NewFilter(h, excluded, hasher);

        var run = await StartAsync(
            h, watcher, BooksProcessor(deleted, kept), BuildOptions(h), logger: logger, excludedFilter: filter);
        run.Monitor.Publish(BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir))));

        await WaitUntilAsync(async () => await h.Assets.FindByPathRootAsync(kept) is not null);
        await WaitUntilAsync(() => Task.FromResult(logger.Messages.Any(message =>
            message.StartsWith("Initial scan: left 1 excluded file(s)", StringComparison.Ordinal))));
        Assert.Null(await h.Assets.FindByPathRootAsync(deleted));
        Assert.Equal(0, hasher.Count);
    }

    [Fact]
    public async Task The_folder_watcher_skips_an_excluded_file_and_still_ingests_a_new_one()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var deleted = WriteFile(h.AsIsDir, "Dune.epub", "dune book");
        var excluded = new ExcludedSourceFileRepository(h.Db.Connection);
        await ExcludeAsync(excluded, deleted);
        var filter = NewFilter(h, excluded, new CountingHasher());
        var options = BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir)));

        // The folder is watched from the start, so its existing files are scanned first; the excluded file must stay out of that too.
        var arrived = Path.Combine(h.AsIsDir, "Emma.epub");
        var run = await StartAsync(h, watcher, BooksProcessor(deleted, arrived), options, excludedFilter: filter);
        Assert.NotNull(run);
        await Task.Delay(500);
        Assert.Null(await h.Assets.FindByPathRootAsync(deleted));

        File.WriteAllText(arrived, "emma book");
        watcher.RaiseForTest(new FileEvent { Path = deleted, EventType = FileEventType.Modified, OccurredAt = DateTimeOffset.UtcNow });
        watcher.RaiseForTest(new FileEvent { Path = arrived, EventType = FileEventType.Created, OccurredAt = DateTimeOffset.UtcNow });

        await WaitUntilAsync(async () => await h.Assets.FindByPathRootAsync(arrived) is not null);
        Assert.Null(await h.Assets.FindByPathRootAsync(deleted));
    }

    // -- A library move owns the old and new place of its file (batched write-back D11) --------------------------------------

    [Fact]
    public async Task The_folder_watcher_ignores_events_on_a_file_Library_is_moving_and_still_ingests_a_new_one()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var options = BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir)));
        var oldPlace = WriteFile(h.AsIsDir, "Dune.epub", "dune book");
        var newPlace = Path.Combine(h.AsIsDir, "Frank Herbert", "Dune.epub");
        var other = Path.Combine(h.AsIsDir, "Emma.epub");
        var moves = new MovesInProgress();
        var run = await StartAsync(h, watcher, BooksProcessor(oldPlace, newPlace, other), options, moveInProgress: moves);
        Assert.NotNull(run);
        await WaitUntilAsync(async () => await h.Assets.FindByPathRootAsync(oldPlace) is not null);
        var dune = (await h.Assets.FindByPathRootAsync(oldPlace))!;

        // The move is under way: Library has marked both places, the file is gone from its old place and sits at the new one, but Library has not followed yet.
        moves.Mark(oldPlace, newPlace);
        Directory.CreateDirectory(Path.GetDirectoryName(newPlace)!);
        File.Move(oldPlace, newPlace);
        File.WriteAllText(other, "emma book");
        watcher.RaiseForTest(new FileEvent { Path = oldPlace, EventType = FileEventType.Deleted, OccurredAt = DateTimeOffset.UtcNow });
        watcher.RaiseForTest(new FileEvent { Path = newPlace, EventType = FileEventType.Created, OccurredAt = DateTimeOffset.UtcNow });
        watcher.RaiseForTest(new FileEvent { Path = other, EventType = FileEventType.Created, OccurredAt = DateTimeOffset.UtcNow });

        await WaitUntilAsync(async () => await h.Assets.FindByPathRootAsync(other) is not null);
        await Task.Delay(300);
        var after = (await h.Assets.FindByIdAsync(dune.Id))!;
        Assert.Equal(AssetStatus.Normal, after.Status);
        Assert.Equal(oldPlace, after.FilePathRoot);
        Assert.Null(await h.Assets.FindByPathRootAsync(newPlace));
        Assert.Equal(2, await AssetCountAsync(h));
    }

    [Fact]
    public async Task A_rescan_does_not_ingest_the_new_place_of_a_file_Library_is_moving()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var options = BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir)));
        var moving = WriteFile(Path.Combine(h.AsIsDir, "Frank Herbert"), "Dune.epub", "dune book");
        var kept = WriteFile(h.AsIsDir, "Emma.epub", "emma book");

        var run = await StartAsync(
            h, watcher, BooksProcessor(moving, kept), options, moveInProgress: new MovesInProgress(moving));
        run.Monitor.Publish(BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir))));

        await WaitUntilAsync(async () => await h.Assets.FindByPathRootAsync(kept) is not null);
        await Task.Delay(300);
        Assert.Null(await h.Assets.FindByPathRootAsync(moving));
    }

    [Fact]
    public async Task A_file_that_changed_in_place_is_read_again_and_keeps_the_value_a_person_locked()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var options = BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir)));
        var book = WriteFile(h.AsIsDir, "Dune.epub", "dune book");
        var processors = BooksProcessor(book);
        var run = await StartAsync(h, watcher, processors, options);
        await WaitUntilAsync(async () => await h.Assets.FindByPathRootAsync(book) is not null);
        var asset = (await h.Assets.FindByPathRootAsync(book))!;

        // The person locked the title; then another program rewrote the file's own tags with a different one.
        var claims = new MetadataClaimRepository(h.Db.Connection);
        await claims.InsertBatchAsync([new MetadataClaim
        {
            Id = Guid.NewGuid(),
            EntityId = asset.Id,
            ProviderId = WellKnownProviders.UserManual,
            ClaimKey = "title",
            ClaimValue = "The Title I Chose",
            Confidence = 1.0,
            ClaimedAt = DateTimeOffset.UtcNow,
            IsUserLocked = true,
        }]);
        processors.SetResultForPath(book, new ProcessorResult
        {
            FilePath = book,
            DetectedType = MediaType.Books,
            Claims = [new ExtractedClaim { Key = "title", Value = "Edited By Another Program", Confidence = 0.9 }],
        });
        File.WriteAllText(book, "dune book, edited elsewhere");

        var reread = await run.Engine.RereadAssetMetadataAsync(asset.Id);

        Assert.Equal("Updated", reread.Status);
        Assert.Equal("The Title I Chose", (await h.Canonicals.GetByEntityAsync(asset.Id)).Single(value => value.Key == "title").Value);
    }

    [Fact]
    public async Task Ingestion_puts_a_file_on_the_write_list_instead_of_writing_into_it()
    {
        var h = NewHarness();
        var watcher = new RecordingFileWatcher();
        var options = BuildOptions(h);
        options.WriteBack = true;
        var tagger = new RecordingTagger();
        var queue = new RecordingFileWriteQueue();
        var movie = Path.Combine(h.MoviesDir, "Dune (2021).mkv");
        var processors = new TestProcessorLibraryItem();
        processors.SetResultForPath(movie, new ProcessorResult
        {
            FilePath = movie,
            DetectedType = MediaType.Movies,
            Claims =
            [
                new ExtractedClaim { Key = "title", Value = "Dune", Confidence = 0.9 },
                new ExtractedClaim { Key = "year", Value = "2021", Confidence = 0.9 },
            ],
        });
        var run = await StartAsync(h, watcher, processors, options, taggers: [tagger], fileWrites: queue);
        Assert.NotNull(run);

        File.WriteAllText(movie, "the movie bytes");
        watcher.RaiseForTest(new FileEvent { Path = movie, EventType = FileEventType.Created, OccurredAt = DateTimeOffset.UtcNow });

        await WaitUntilAsync(async () => await h.Assets.FindByPathRootAsync(movie) is not null || queue.Queued.Count > 0);
        await WaitUntilAsync(() => Task.FromResult(queue.Queued.Count > 0));
        Assert.Equal(0, tagger.Writes);
        var queued = Assert.Single(queue.Queued);
        Assert.Equal("auto_match", queued.Trigger);
        Assert.NotNull(await h.Assets.FindByIdAsync(queued.AssetId));
        Assert.Equal("the movie bytes", await File.ReadAllTextAsync(movie));
    }

    private sealed class MovesInProgress(params string[] paths) : IFileMoveInProgress
    {
        private readonly HashSet<string> _paths = new(paths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

        public void Mark(params string[] more)
        {
            lock (_paths)
            {
                foreach (var path in more)
                {
                    _paths.Add(Path.GetFullPath(path));
                }
            }
        }

        public Task<IReadOnlySet<string>> GetPathsAsync(CancellationToken ct = default)
        {
            lock (_paths)
            {
                return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(_paths, StringComparer.OrdinalIgnoreCase));
            }
        }

        public Task<bool> HasMoveForAssetAsync(Guid assetId, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class RecordingFileWriteQueue : IFileWriteQueue
    {
        public List<(Guid AssetId, string Trigger)> Queued { get; } = [];

        public Task EnqueueAssetAsync(Guid assetId, string trigger, CancellationToken ct = default)
        {
            lock (Queued)
            {
                Queued.Add((assetId, trigger));
            }

            return Task.CompletedTask;
        }

        public Task EnqueueAssetsAsync(IReadOnlyCollection<Guid> assetIds, string trigger, CancellationToken ct = default) =>
            Task.WhenAll(assetIds.Select(id => EnqueueAssetAsync(id, trigger, ct)));

        public Task EnqueueViewItemAsync(Guid localItemId, string trigger, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Handles every file and counts the writes it is asked to make; it must never be asked.</summary>
    private sealed class RecordingTagger : IMetadataTagger
    {
        private int _writes;

        public int Writes => Volatile.Read(ref _writes);

        public bool CanHandle(string filePath) => true;

        public MetadataTaggerCapabilities GetCapabilities(string filePath) =>
            new("test", ["title"], canWriteArtwork: true, adapterVersion: 1);

        public Task WriteTagsAsync(string filePath, IReadOnlyDictionary<string, string> tags, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writes);
            return Task.CompletedTask;
        }

        public Task WriteCoverArtAsync(string filePath, byte[] imageData, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writes);
            return Task.CompletedTask;
        }
    }

    // -- Harness -----------------------------------------------------------

    private static ExcludedSourceFileFilter NewFilter(
        ImportFolderHarness h, IExcludedSourceFileRepository excluded, IAssetHasher hasher)
        => new(
            new LibraryFolderResolver(new ReloadableOptionsMonitor<IngestionOptions>(
                BuildOptions(h, BooksLibrary(AsIsSource(h.AsIsDir))))),
            excluded,
            hasher,
            NullLogger<ExcludedSourceFileFilter>.Instance);

    private static async Task ExcludeAsync(IExcludedSourceFileRepository excluded, string path)
    {
        var info = new FileInfo(path);
        await excluded.AddOrReplaceAsync(new ExcludedSourceFile(
            Guid.NewGuid(),
            BooksLibraryId,
            BooksSourceId,
            ExcludedSourceFilePaths.RelativePathOf(Path.GetDirectoryName(path)!, path)!,
            (await new TestAssetHasher().ComputeAsync(path)).Hex,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            Path.GetFileNameWithoutExtension(path),
            DateTimeOffset.UtcNow,
            null,
            "Test",
            ExcludedSourceFileReasons.AsIs));
    }

    /// <summary>Hashes like the real thing and counts how many files it was asked to read.</summary>
    private sealed class CountingHasher : IAssetHasher
    {
        private readonly TestAssetHasher _inner = new();
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task<HashResult> ComputeAsync(string filePath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            return _inner.ComputeAsync(filePath, ct);
        }
    }

    private ImportFolderHarness NewHarness()
    {
        var harness = new ImportFolderHarness();
        _disposables.Add(harness);
        return harness;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string WriteFile(string folder, string name, string content)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static LibrarySourceConfig AsIsSource(string path, string id = BooksSourceId, bool includeSubdirectories = true)
    {
        var source = ImportFolderHarness.AsIs(id, path);
        source.IncludeSubdirectories = includeSubdirectories;
        return source;
    }

    private static LibraryFolderConfig BooksLibrary(params LibrarySourceConfig[] sources)
        => ImportFolderHarness.NewLibrary(BooksLibraryId, "Books", "Books", destinationId: null, sources);

    private static TestProcessorLibraryItem BooksProcessor(params string[] paths)
    {
        var processors = new TestProcessorLibraryItem();
        foreach (var path in paths)
        {
            processors.SetResultForPath(path, new ProcessorResult
            {
                FilePath = path,
                DetectedType = MediaType.Books,
                Claims = [new ExtractedClaim { Key = "title", Value = Path.GetFileNameWithoutExtension(path), Confidence = 0.9 }],
            });
        }

        return processors;
    }

    /// <summary>The options as the folder settings currently describe them, the way a reload of libraries.json builds them.</summary>
    private static IngestionOptions BuildOptions(ImportFolderHarness h, params LibraryFolderConfig[] extraLibraries) => new()
    {
        LibraryRoot = h.LibraryRoot,
        AutoOrganize = true,
        DuplicatePolicy = h.Options.DuplicatePolicy,
        ImportFolderPath = h.ImportDir,
        // The harness configuration (Movies and the Import location) is never edited once the Engine runs; a "save" is a new set of options.
        LibraryFolders = h.Config.Libraries.Libraries.Concat(extraLibraries).Select(LibraryFolderEntryFactory.Create).ToList(),
        PollIntervalSeconds = 0,
        IncludeSubdirectories = true,
        FswQuietPeriodSeconds = 1,
        LockProbeRetryMaxAttempts = 10,
        LockProbeRetryBaseDelaySeconds = 1,
    };

    private static List<string> Snapshot(string folder)
        => Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Select(path => File.Exists(path)
                ? $"{path}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path).Ticks}"
                : $"{path}|dir|{Directory.GetLastWriteTimeUtc(path).Ticks}")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

    private static async Task<int> AssetCountAsync(ImportFolderHarness h)
    {
        using var connection = h.Db.Connection.CreateConnection();
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM media_assets;");
    }

    private static async Task<int> OpenOperationCountAsync(ImportFolderHarness h)
    {
        using var connection = h.Db.Connection.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM media_operations WHERE status IN ('pending', 'queued', 'running');");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the Engine to apply the folder change.");
            await Task.Delay(50);
        }
    }

    /// <summary>A started engine plus the monitor a test "saves" folder settings into. Disposed with the test.</summary>
    private sealed class RunningEngine(
        IngestionEngine engine,
        DebounceQueue debounce,
        ReloadableOptionsMonitor<IngestionOptions> monitor) : IDisposable
    {
        private bool _stopped;

        public ReloadableOptionsMonitor<IngestionOptions> Monitor { get; } = monitor;

        public IngestionEngine Engine => engine;

        public void Stop()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                engine.StopAsync(stop.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // A slow stop is acceptable in a test.
            }

            engine.Dispose();
            debounce.Dispose();
        }

        public void Dispose() => Stop();
    }

    private async Task<RunningEngine> StartAsync(
        ImportFolderHarness h,
        IFileWatcher watcher,
        TestProcessorLibraryItem processors,
        IngestionOptions options,
        ILogger<IngestionEngine>? logger = null,
        bool withHashCache = false,
        Action<ReloadableOptionsMonitor<IngestionOptions>>? beforeStart = null,
        IExcludedSourceFileFilter? excludedFilter = null,
        IFileMoveInProgress? moveInProgress = null,
        IFileWriteQueue? fileWrites = null,
        IEnumerable<IMetadataTagger>? taggers = null)
    {
        var db = h.Db.Connection;
        var publisher = new StubEventPublisher();
        var workRepo = new WorkRepository(db);
        var operationRepo = new MediaOperationRepository(db);
        var tracker = new MediaOperationTracker(
            operationRepo,
            new MediaOperationEventRepository(db),
            publisher,
            NullLogger<MediaOperationTracker>.Instance, new VaultLibraryRepository(db));

        var debounce = new DebounceQueue(new DebounceOptions
        {
            SettleDelay = TimeSpan.FromMilliseconds(1),
            ProbeInterval = TimeSpan.FromMilliseconds(20),
            MaxProbeAttempts = 2,
            MaxProbeDelay = TimeSpan.FromMilliseconds(50),
        });
        var monitor = new ReloadableOptionsMonitor<IngestionOptions>(options);

        var engine = new IngestionEngine(
            watcher,
            debounce,
            new TestAssetHasher(),
            processors,
            new PriorityCascadeEngine(new StubConfigurationLoader(), NullLogger<PriorityCascadeEngine>.Instance),
            new StubFileOrganizer(),
            taggers ?? Enumerable.Empty<IMetadataTagger>(),
            h.Assets,
            new InlineBackgroundWorker(),
            publisher,
            Microsoft.Extensions.Options.Options.Create(options),
            logger ?? NullLogger<IngestionEngine>.Instance,
            new MetadataClaimRepository(db),
            h.Canonicals,
            new StubRecursiveIdentity(),
            new MediaEntityChainFactory(db, new HierarchyResolver(workRepo, null, new BookFormatSiblingFinder(db))),
            h.Reviews,
            h.Activity,
            new StubReconciliation(),
            new OrganizationGate(new ScoringConfiguration()),
            new IngestionLogRepository(db),
            new StubSmartLabeler(),
            new StubMediaTypeAdvisor(),
            new StubEntityTimelineRepository(),
            new ScoringConfiguration(),
            h.Jobs,
            new MediaTypeResolver(
                monitor,
                new LibraryFolderResolver(monitor),
                new StubMediaTypeAdvisor(),
                new MediaTypeExtensionCatalog(new StubConfigurationLoader()),
                NullLogger<MediaTypeResolver>.Instance,
                h.Importer),
            new DuplicateResolver(h.Assets),
            new IngestionLogScribe(new IngestionLogRepository(db), publisher, NullLogger<IngestionLogScribe>.Instance),
            new HashDedupeStageDependencies(withHashCache ? new FileHashCacheRepository(db) : null),
            new ScoreIdentifyStageDependencies(null),
            new OrganizeStageDependencies(null),
            new WriteBackStageDependencies(new EntityAssetRepository(db), new AssetPathService(h.LibraryRoot), workRepo, null, fileWrites),
            new IdentityJobStageDependencies(new IdentityPipelineSignal()),
            new NoVaultIngestionHook(),
            operationTracker: tracker,
            operationRepository: operationRepo,
            libraryFolderResolver: new LibraryFolderResolver(monitor),
            sourceMutationPolicyGate: new SourceMutationPolicyGate(),
            importFolders: h.Importer,
            importFiler: h.Filer,
            optionsMonitor: monitor,
            excludedFileFilter: excludedFilter,
            moveInProgress: moveInProgress);

        var run = new RunningEngine(engine, debounce, monitor);
        _disposables.Add(run);

        beforeStart?.Invoke(monitor);
        await engine.StartAsync(CancellationToken.None);

        // The engine subscribes and starts its first watchers on its own thread; wait until it has.
        await WaitUntilAsync(() => Task.FromResult(monitor.ListenerCount == 1 && watcher.WatchedPaths.Count > 0));
        return run;
    }
}
