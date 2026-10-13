using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Tests.Helpers;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// Converting a folder from As-is to Managed, on real temp folders and a real data store: files are renamed to the
/// standard names, sidecars travel with them, nothing is overwritten, every move is in the ledger, and an interrupted
/// conversion resumes without losing or doubling a file.
/// </summary>
public sealed class FolderConversionTests : IDisposable
{
    private readonly List<FolderConversionHarness> _harnesses = [];

    public void Dispose()
    {
        foreach (var harness in _harnesses)
        {
            harness.Dispose();
        }
    }

    private FolderConversionHarness NewHarness(string duplicatePolicy = LibraryDuplicatePolicies.KeepOne, bool managed = true)
    {
        var harness = new FolderConversionHarness(duplicatePolicy, managed);
        _harnesses.Add(harness);
        return harness;
    }

    private static string Standard(string title, string year) => $"Movies/{title} ({year})/{title} ({year}).mkv";

    // ── C1, C7: the conversion renames, logs, and points the library at the new path ──────────────────────────────

    [Fact]
    public async Task Files_are_renamed_to_the_standard_names_and_every_move_is_logged()
    {
        var h = NewHarness();
        var dune = h.Write("Dune.2021.1080p.mkv", "dune");
        var alien = h.Write(Path.Combine("old stuff", "alien_1979.mkv"), "alien");
        var duneId = await h.RegisterAsync(dune, "Dune", "2021");
        var alienId = await h.RegisterAsync(alien, "Alien", "1979");

        var result = await h.RunAsync();

        Assert.Equal(FolderOrganiseStop.Finished, result.Stop);
        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Completed);
        Assert.Equal(0, result.Failed);
        Assert.Equal([Standard("Alien", "1979"), Standard("Dune", "2021")], h.Files());

        Assert.Equal(Path.Combine(h.SourceDir, "Movies", "Dune (2021)", "Dune (2021).mkv"), (await h.Assets.FindByIdAsync(duneId))!.FilePathRoot);
        Assert.Equal(Path.Combine(h.SourceDir, "Movies", "Alien (1979)", "Alien (1979).mkv"), (await h.Assets.FindByIdAsync(alienId))!.FilePathRoot);

        // The ledger has the old and new path of every move, and History says so for each item.
        var entry = await h.Ledger.GetAsync(h.OperationId, dune);
        Assert.NotNull(entry);
        Assert.Equal(FolderOrganiseOutcomes.Moved, entry.Outcome);
        Assert.Equal(Path.Combine(h.SourceDir, "Movies", "Dune (2021)", "Dune (2021).mkv"), entry.NewPath);
        Assert.Equal(2, await h.Ledger.CountAsync(h.OperationId));
        var history = await h.Activity.GetRecentAsync(20);
        Assert.Equal(2, history.Count(item => item.ActionType == SystemActionType.PathUpdated));
        Assert.Contains(history, item => item.EntityId == duneId && item.Detail!.Contains("Dune.2021.1080p.mkv"));

        // The folder an old name lived in is gone once empty; the folder being converted never is.
        Assert.False(Directory.Exists(Path.Combine(h.SourceDir, "old stuff")));
        Assert.True(Directory.Exists(h.SourceDir));
    }

    [Fact]
    public async Task A_file_that_already_has_the_standard_name_is_left_alone_and_logged()
    {
        var h = NewHarness();
        var path = h.Write(Standard("Dune", "2021"), "dune");
        await h.RegisterAsync(path, "Dune", "2021");

        var result = await h.RunAsync();

        Assert.Equal(1, result.Count(FolderOrganiseOutcomes.AlreadyCorrect));
        Assert.Empty(h.Mover.Moves);
        Assert.Equal([Standard("Dune", "2021")], h.Files());
    }

    [Fact]
    public async Task A_file_waiting_in_Review_or_without_a_title_is_not_renamed()
    {
        var h = NewHarness();
        var unsure = h.Write("mystery.mkv", "mystery");
        var untitled = h.Write("nothing.mkv", "nothing");
        var unsureId = await h.RegisterAsync(unsure, "Mystery Movie", "2000");
        await h.AddPendingReviewAsync(unsureId);
        await h.RegisterAsync(untitled, "Unknown");

        var result = await h.RunAsync();

        Assert.Equal(2, result.Count(FolderOrganiseOutcomes.NeedsReview));
        Assert.Equal(["mystery.mkv", "nothing.mkv"], h.Files());
        Assert.Empty(h.Mover.Moves);
    }

    // ── C6: sidecars ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Subtitles_notes_and_artwork_move_with_the_file_and_keep_their_suffixes()
    {
        var h = NewHarness();
        var movie = h.Write(Path.Combine("rips", "Dune.2021.mkv"), "dune");
        h.Write(Path.Combine("rips", "Dune.2021.en.srt"), "english");
        h.Write(Path.Combine("rips", "Dune.2021.pt-BR.forced.srt"), "portuguese");
        h.Write(Path.Combine("rips", "Dune.2021.nfo"), "nfo");
        h.Write(Path.Combine("rips", "Dune.2021.jpg"), "jpg");
        h.Write(Path.Combine("rips", "readme.txt"), "keep me where I am");
        await h.RegisterAsync(movie, "Dune", "2021");

        var result = await h.RunAsync();

        Assert.Equal(
            [
                "Movies/Dune (2021)/Dune (2021).en.srt",
                "Movies/Dune (2021)/Dune (2021).jpg",
                "Movies/Dune (2021)/Dune (2021).mkv",
                "Movies/Dune (2021)/Dune (2021).nfo",
                "Movies/Dune (2021)/Dune (2021).pt-BR.forced.srt",
                "rips/readme.txt",
            ],
            h.Files());
        Assert.Equal(4, result.Count(FolderOrganiseOutcomes.SidecarMoved));

        // The file the job does not know how to move is listed, and its folder is kept.
        var left = await h.Ledger.GetAsync(h.OperationId, Path.Combine(h.SourceDir, "rips", "readme.txt"));
        Assert.Equal(FolderOrganiseOutcomes.NotMoved, left!.Outcome);
        Assert.True(Directory.Exists(Path.Combine(h.SourceDir, "rips")));
    }

    [Fact]
    public async Task A_neighbours_subtitles_are_not_taken_by_a_shorter_named_file()
    {
        var h = NewHarness();
        var first = h.Write("Movie.mkv", "first");
        var second = h.Write("Movie.2021.mkv", "second");
        h.Write("Movie.2021.en.srt", "belongs to the second");
        await h.RegisterAsync(first, "First Film", "1999");
        await h.RegisterAsync(second, "Second Film", "2021");

        await h.RunAsync();

        Assert.Equal(
            [Standard("First Film", "1999"), "Movies/Second Film (2021)/Second Film (2021).en.srt", Standard("Second Film", "2021")],
            h.Files());
    }

    [Fact]
    public void A_playlist_that_names_the_old_file_is_rewritten_and_other_lines_are_kept()
    {
        var text = "#EXTM3U\r\nmovie.mkv\r\n..\\other\\movie.mkv\r\nsomething else.mkv\r\n";

        var rewritten = SidecarFiles.RewritePlaylist(text, "movie.mkv", "Movie (2021).mkv");

        Assert.Equal("#EXTM3U\r\nMovie (2021).mkv\r\n..\\other\\Movie (2021).mkv\r\nsomething else.mkv\r\n", rewritten);
        Assert.Null(SidecarFiles.RewritePlaylist(text, "absent.mkv", "x.mkv"));
    }

    // ── C5: names never collide destructively ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_different_files_for_one_name_both_survive_and_a_Review_item_is_opened()
    {
        var h = NewHarness();
        var theatrical = h.Write("dune theatrical.mkv", "theatrical cut bytes");
        var extended = h.Write("dune extended.mkv", "extended cut bytes, longer");
        await h.RegisterAsync(theatrical, "Dune", "2021");
        var extendedId = await h.RegisterAsync(extended, "Dune", "2021");

        await h.RunAsync();

        var files = h.Files().ToList();
        Assert.Equal(["Movies/Dune (2021)/Dune (2021) (2).mkv", Standard("Dune", "2021")], files);
        var kept = File.ReadAllText(Path.Combine(h.SourceDir, "Movies", "Dune (2021)", "Dune (2021).mkv"));
        var numbered = File.ReadAllText(Path.Combine(h.SourceDir, "Movies", "Dune (2021)", "Dune (2021) (2).mkv"));
        Assert.Equal(new[] { "extended cut bytes, longer", "theatrical cut bytes" }, new[] { kept, numbered }.OrderBy(text => text));

        var collisions = (await h.Reviews.GetPendingAsync(50)).Where(r => r.Trigger == ReviewTrigger.NameCollision).ToList();
        Assert.Single(collisions);
        _ = extendedId;
    }

    [Fact]
    public async Task An_exact_copy_at_the_standard_name_is_removed_under_Keep_one_copy()
    {
        var h = NewHarness(LibraryDuplicatePolicies.KeepOne);
        var kept = h.Write(Path.Combine("Movies", "Dune (2021)", "Dune (2021).mkv"), "same bytes");
        var extra = h.Write(Path.Combine("downloads", "Dune.2021.mkv"), "same bytes");
        h.Write(Path.Combine("downloads", "Dune.2021.en.srt"), "subtitles with no twin");
        var extraId = await h.RegisterAsync(extra, "Dune", "2021");

        var result = await h.RunAsync();

        Assert.Equal(1, result.Count(FolderOrganiseOutcomes.DuplicateRemoved));
        Assert.False(File.Exists(extra));
        Assert.True(File.Exists(kept));

        // The one library record now points at the copy that stayed; the subtitles were carried across, not lost.
        Assert.Equal(kept, (await h.Assets.FindByIdAsync(extraId))!.FilePathRoot);
        Assert.Equal(
            ["Movies/Dune (2021)/Dune (2021).en.srt", Standard("Dune", "2021")],
            h.Files());
        Assert.False(Directory.Exists(Path.Combine(h.SourceDir, "downloads")));
    }

    [Fact]
    public async Task An_exact_copy_at_the_standard_name_is_kept_under_a_numbered_name_under_Keep_both()
    {
        var h = NewHarness(LibraryDuplicatePolicies.KeepBoth);
        h.Write(Standard("Dune", "2021"), "same bytes");
        var extra = h.Write(Path.Combine("downloads", "Dune.2021.mkv"), "same bytes");
        await h.RegisterAsync(extra, "Dune", "2021");

        var result = await h.RunAsync();

        Assert.Equal(1, result.Count(FolderOrganiseOutcomes.MovedWithUniqueName));
        Assert.Equal(["Movies/Dune (2021)/Dune (2021) (2).mkv", Standard("Dune", "2021")], h.Files());
        Assert.DoesNotContain(await h.Reviews.GetPendingAsync(50), r => r.Trigger == ReviewTrigger.NameCollision);
    }

    [Fact]
    public async Task A_media_file_the_library_does_not_know_is_sent_for_identification_and_an_exact_copy_is_removed()
    {
        var h = NewHarness();
        var known = h.Write("Dune.2021.mkv", "dune");
        await h.RegisterAsync(known, "Dune", "2021");
        var copy = h.Write(Path.Combine("backup", "Dune copy.mkv"), "dune");
        var fresh = h.Write("brand new film.mkv", "never seen");

        var result = await h.RunAsync();

        Assert.False(File.Exists(copy));
        Assert.Equal(1, result.Count(FolderOrganiseOutcomes.DuplicateRemoved));
        Assert.Equal(1, result.Count(FolderOrganiseOutcomes.QueuedForIdentification));
        Assert.Equal([fresh], h.Intake.Paths);
        Assert.Equal(["brand new film.mkv", Standard("Dune", "2021")], h.Files());
    }

    // ── C4: a file that is open is skipped and retried ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_file_open_for_playback_is_skipped_and_converted_once_it_is_released()
    {
        var h = NewHarness();
        var playing = h.Write("playing now.mkv", "playing");
        var other = h.Write("other.mkv", "other");
        await h.RegisterAsync(playing, "Dune", "2021");
        await h.RegisterAsync(other, "Alien", "1979");
        h.Mover.LockFor(playing, 2);

        var result = await h.RunAsync();

        Assert.Equal(FolderOrganiseStop.Finished, result.Stop);
        Assert.Equal(0, result.Failed);
        Assert.Equal([Standard("Alien", "1979"), Standard("Dune", "2021")], h.Files());
        // The open file was tried again after the rest of the pass, not before it.
        var firstSuccess = h.Mover.Moves.FindIndex(move => move.From == playing);
        Assert.True(firstSuccess > h.Mover.Moves.FindIndex(move => move.From == other));
    }

    [Fact]
    public async Task A_file_that_stays_in_use_is_counted_as_failed_and_the_rest_still_finish()
    {
        var h = NewHarness();
        var stuck = h.Write("stuck.mkv", "stuck");
        var fine = h.Write("fine.mkv", "fine");
        await h.RegisterAsync(stuck, "Dune", "2021");
        await h.RegisterAsync(fine, "Alien", "1979");
        h.Mover.LockFor(stuck, 1000);

        var result = await h.RunAsync();

        Assert.Equal(FolderOrganiseStop.Finished, result.Stop);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, result.Completed);
        Assert.Equal([Standard("Alien", "1979"), "stuck.mkv"], h.Files());
        var entry = await h.Ledger.GetAsync(h.OperationId, stuck);
        Assert.Equal(FolderOrganiseOutcomes.Failed, entry!.Outcome);
        Assert.Contains("in use", entry.Detail);
    }

    // ── C2: killing the Engine and restarting resumes without double moves ───────────────────────────────────────

    [Fact]
    public async Task A_conversion_stopped_in_the_middle_of_a_move_resumes_and_finishes_every_file_once()
    {
        var h = NewHarness();
        var titles = new[] { ("Alpha", "2001"), ("Bravo", "2002"), ("Charlie", "2003"), ("Delta", "2004"), ("Echo", "2005") };
        var ids = new List<Guid>();
        foreach (var (title, year) in titles)
        {
            var path = h.Write($"raw {title}.mkv", $"{title} bytes");
            ids.Add(await h.RegisterAsync(path, title, year));
        }

        // The Engine dies right after the third rename: the file moved, but nothing recorded it.
        var renames = 0;
        h.Mover.AfterMove = (_, _) =>
        {
            if (++renames == 3)
            {
                throw new SimulatedCrashException();
            }
        };
        await Assert.ThrowsAsync<SimulatedCrashException>(() => h.RunAsync());
        var unfinished = await h.Ledger.ListUnfinishedAsync(h.OperationId);
        Assert.Single(unfinished);

        // On restart the job finishes that file from its ledger entry and carries on with the rest.
        h.Mover.AfterMove = null;
        var result = await h.RunAsync();

        Assert.Equal(FolderOrganiseStop.Finished, result.Stop);
        Assert.Equal(5, result.Total);
        Assert.Equal(5, result.Completed);
        Assert.Equal(
            titles.Select(item => Standard(item.Item1, item.Item2)).OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            h.Files());
        for (var index = 0; index < ids.Count; index++)
        {
            var asset = await h.Assets.FindByIdAsync(ids[index]);
            Assert.True(File.Exists(asset!.FilePathRoot), $"{titles[index].Item1} must be where the library says");
            Assert.Equal(Path.Combine(h.SourceDir, "Movies", $"{titles[index].Item1} ({titles[index].Item2})", $"{titles[index].Item1} ({titles[index].Item2}).mkv"), asset.FilePathRoot);
        }

        Assert.Empty(await h.Ledger.ListUnfinishedAsync(h.OperationId));
        // Exactly five renames ever happened: the resumed run did not move anything a second time.
        Assert.Equal(5, h.Mover.Moves.Count + 0);
    }

    // ── C1: pause, resume and cancel ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pause_stops_after_the_current_file_and_resume_continues_where_it_left_off()
    {
        var h = NewHarness();
        var names = new[] { "Alpha", "Bravo", "Charlie", "Delta" };
        for (var index = 0; index < names.Length; index++)
        {
            var path = h.Write($"raw {names[index]}.mkv", $"{names[index]} bytes");
            await h.RegisterAsync(path, names[index], (2000 + index).ToString());
        }

        var host = new TestHost { Signal = asked => asked >= 2 ? FolderJobSignal.Pause : FolderJobSignal.Continue };
        var paused = await h.RunAsync(host);

        Assert.Equal(FolderOrganiseStop.Paused, paused.Stop);
        Assert.Equal(2, paused.Completed);
        Assert.Equal(2, h.Files().Count(path => path.StartsWith("Movies/")));
        Assert.Equal(4, host.Progress[^1].Total);

        var resumed = await h.RunAsync();

        Assert.Equal(FolderOrganiseStop.Finished, resumed.Stop);
        Assert.Equal(4, resumed.Completed);
        Assert.Equal(4, h.Files().Count(path => path.StartsWith("Movies/")));
        Assert.Equal(4, h.Mover.Moves.Count);
    }

    [Fact]
    public async Task Cancel_keeps_the_work_done_and_a_later_pass_can_still_finish_the_rest()
    {
        var h = NewHarness();
        for (var index = 0; index < 3; index++)
        {
            var path = h.Write($"raw {index}.mkv", $"bytes {index}");
            await h.RegisterAsync(path, $"Film {index}", (2000 + index).ToString());
        }

        var cancelled = await h.RunAsync(new TestHost { Signal = asked => asked >= 1 ? FolderJobSignal.Cancel : FolderJobSignal.Continue });

        Assert.Equal(FolderOrganiseStop.Cancelled, cancelled.Stop);
        Assert.Equal(1, cancelled.Completed);
        Assert.Equal(1, h.Files().Count(path => path.StartsWith("Movies/")));
        Assert.Equal(2, h.Files().Count(path => path.StartsWith("raw ")));
    }

    // ── C8: switching back to As-is stops the job and changes nothing afterwards ─────────────────────────────────

    [Fact]
    public async Task Switching_the_folder_back_to_As_is_stops_the_job_and_nothing_more_changes_on_disk()
    {
        var h = NewHarness();
        for (var index = 0; index < 4; index++)
        {
            var path = h.Write($"raw {index}.mkv", $"bytes {index}");
            await h.RegisterAsync(path, $"Film {index}", (2000 + index).ToString());
        }

        var host = new TestHost
        {
            Signal = asked =>
            {
                if (asked == 2)
                {
                    h.SetManaged(false);
                }

                return FolderJobSignal.Continue;
            },
        };
        var result = await h.RunAsync(host);

        Assert.Equal(FolderOrganiseStop.SourceNotManaged, result.Stop);
        var moved = h.Mover.Moves.Count;
        Assert.Equal(2, moved);
        var snapshot = h.Files().ToList();

        // Running it again changes nothing on disk.
        var again = await h.RunAsync();

        Assert.Equal(FolderOrganiseStop.SourceNotManaged, again.Stop);
        Assert.Equal(moved, h.Mover.Moves.Count);
        Assert.Equal(snapshot, h.Files().ToList());
    }

    [Fact]
    public async Task An_As_is_folder_is_never_touched_by_the_job_or_the_organiser()
    {
        var h = NewHarness(managed: false);
        var path = h.Write("Dune.2021.mkv", "dune");
        await h.RegisterAsync(path, "Dune", "2021");

        var result = await h.RunAsync();

        Assert.Equal(FolderOrganiseStop.SourceNotManaged, result.Stop);
        Assert.Empty(h.Mover.Moves);
        Assert.Equal(["Dune.2021.mkv"], h.Files());
        Assert.Equal(0, await h.Ledger.CountAsync(h.OperationId));
    }

    // ── C3: the preview reports what the job then does, without touching anything ────────────────────────────────

    [Fact]
    public async Task The_preview_counts_what_the_job_then_does_and_changes_nothing()
    {
        var h = NewHarness(managed: false);
        var renameA = h.Write("alpha raw.mkv", "alpha");
        var renameB = h.Write(Path.Combine("sub", "bravo raw.mkv"), "bravo");
        var correct = h.Write(Standard("Charlie", "2003"), "charlie");
        var review = h.Write("delta.mkv", "delta");
        var collisionOne = h.Write("echo one.mkv", "echo one");
        var collisionTwo = h.Write("echo two.mkv", "echo two");
        var copy = h.Write(Path.Combine("dupes", "alpha copy.mkv"), "alpha");
        await h.RegisterAsync(renameA, "Alpha", "2001");
        await h.RegisterAsync(renameB, "Bravo", "2002");
        await h.RegisterAsync(correct, "Charlie", "2003");
        await h.AddPendingReviewAsync(await h.RegisterAsync(review, "Delta", "2004"));
        await h.RegisterAsync(collisionOne, "Echo", "2005");
        await h.RegisterAsync(collisionTwo, "Echo", "2005");
        var before = h.Files().ToList();
        var pathsBefore = (await h.Assets.GetAllFilePathsAsync()).OrderBy(p => p).ToList();

        var preview = await h.Previewer.PreviewAsync(h.SourceId, fileLimit: 0);

        Assert.NotNull(preview);
        Assert.Equal(7, preview.TotalFiles);
        Assert.True(preview.Complete);
        Assert.Equal(1, preview.AlreadyCorrect);
        Assert.Equal(1, preview.NeedsReview);
        Assert.Equal(1, preview.DuplicatesToRemove);
        Assert.Equal(1, preview.NameCollisions);
        Assert.Equal(4, preview.ToRename); // alpha, bravo and the pair that wants one name
        Assert.Equal(4, preview.Samples.Count);
        Assert.All(preview.Samples, sample => Assert.DoesNotContain(h.SourceDir, sample.OldPath));
        // Nothing moved, nothing was written.
        Assert.Equal(before, h.Files().ToList());
        Assert.Equal(pathsBefore, (await h.Assets.GetAllFilePathsAsync()).OrderBy(p => p).ToList());
        Assert.Equal(0, await h.Ledger.CountAsync(h.OperationId));

        // Then the job does what the preview said.
        h.SetManaged(true);
        var result = await h.RunAsync();

        Assert.Equal(preview.AlreadyCorrect, result.Count(FolderOrganiseOutcomes.AlreadyCorrect));
        Assert.Equal(preview.NeedsReview, result.Count(FolderOrganiseOutcomes.NeedsReview));
        Assert.Equal(preview.DuplicatesToRemove, result.Count(FolderOrganiseOutcomes.DuplicateRemoved));
        Assert.Equal(preview.NameCollisions, result.Count(FolderOrganiseOutcomes.MovedWithUniqueName));
        Assert.Equal(preview.ToRename, result.Count(FolderOrganiseOutcomes.Moved) + result.Count(FolderOrganiseOutcomes.MovedWithUniqueName));
        _ = copy;
    }

    [Fact]
    public async Task A_large_folder_can_be_sampled_and_a_quick_count_opens_no_files()
    {
        var h = NewHarness(managed: false);
        for (var index = 0; index < 12; index++)
        {
            var path = h.Write($"film {index}.mkv", $"bytes {index}");
            await h.RegisterAsync(path, $"Film {index}", (1990 + index).ToString());
        }

        var count = await h.Previewer.CountAsync(h.SourceId);
        var sample = await h.Previewer.PreviewAsync(h.SourceId, fileLimit: 5);

        Assert.Equal((12, 12), count);
        Assert.NotNull(sample);
        Assert.Equal(12, sample.TotalFiles);
        Assert.Equal(5, sample.Examined);
        Assert.False(sample.Complete);
        Assert.Equal(5, sample.ToRename);
    }

    // ── Found in review: shared artwork, resume after a duplicate, stale fingerprints, case-only names ───────────

    [Fact]
    public async Task A_folder_wide_cover_moves_with_the_last_file_and_a_different_cover_is_never_deleted()
    {
        var h = NewHarness();
        var first = h.Write(Path.Combine("CD1", "dune.mkv"), "first cut");
        h.Write(Path.Combine("CD1", "cover.jpg"), "cover one");
        var second = h.Write(Path.Combine("CD2", "dune extended.mkv"), "second cut, longer");
        h.Write(Path.Combine("CD2", "cover.jpg"), "cover two");
        await h.RegisterAsync(first, "Dune", "2021");
        await h.RegisterAsync(second, "Dune", "2021");

        var result = await h.RunAsync();

        Assert.Equal(0, result.Failed);
        var files = h.Files().ToList();
        // The first folder's cover travelled with its film; the second cover differs, so it stayed where it was.
        Assert.Equal("cover one", File.ReadAllText(Path.Combine(h.SourceDir, "Movies", "Dune (2021)", "cover.jpg")));
        Assert.Equal("cover two", File.ReadAllText(Path.Combine(h.SourceDir, "CD2", "cover.jpg")));
        Assert.Equal(4, files.Count);
        var left = await h.Ledger.GetAsync(h.OperationId, Path.Combine(h.SourceDir, "CD2", "cover.jpg"));
        Assert.Equal(FolderOrganiseOutcomes.NotMoved, left!.Outcome);
    }

    [Fact]
    public async Task Per_file_artwork_follows_its_film_and_every_move_is_in_the_ledger()
    {
        var h = NewHarness();
        var film = h.Write("Dune.2021.mkv", "dune");
        h.Write("Dune.2021-poster.jpg", "poster");
        h.Write("Dune.2021-fanart-1.jpg", "fanart");
        await h.RegisterAsync(film, "Dune", "2021");

        await h.RunAsync();

        Assert.Equal(
            ["Movies/Dune (2021)/Dune (2021)-fanart-1.jpg", "Movies/Dune (2021)/Dune (2021)-poster.jpg", Standard("Dune", "2021")],
            h.Files());
        Assert.Equal(2, (await h.Ledger.CountByOutcomeAsync(h.OperationId))[FolderOrganiseOutcomes.SidecarMoved]);
    }

    [Fact]
    public async Task Pausing_after_a_duplicate_was_removed_still_renames_the_copy_that_stayed()
    {
        var h = NewHarness();
        var kept = h.Write("Dune.2021.mkv", "same bytes");
        h.Write(Path.Combine("a backup", "Dune copy.mkv"), "same bytes");
        await h.RegisterAsync(kept, "Dune", "2021");

        // "a backup/Dune copy.mkv" sorts first: it is removed as a copy, then the person pauses.
        var paused = await h.RunAsync(new TestHost { Signal = asked => asked >= 1 ? FolderJobSignal.Pause : FolderJobSignal.Continue });
        Assert.Equal(FolderOrganiseStop.Paused, paused.Stop);
        Assert.Equal(["Dune.2021.mkv"], h.Files());

        var resumed = await h.RunAsync();

        Assert.Equal(FolderOrganiseStop.Finished, resumed.Stop);
        Assert.Equal([Standard("Dune", "2021")], h.Files());
    }

    [Fact]
    public async Task A_file_edited_outside_Tuvima_is_never_removed_because_of_an_old_fingerprint()
    {
        var h = NewHarness();
        var edited = h.Write("raw.mkv", "aaaa");
        await h.RegisterAsync(edited, "Dune", "2021");
        // Someone changes the file in place (same length), and a different file sits at the standard name.
        File.WriteAllText(edited, "bbbb");
        h.Write(Standard("Dune", "2021"), "aaaa");

        await h.RunAsync();

        Assert.Equal(2, h.Files().Count());
        Assert.Contains("bbbb", Directory.EnumerateFiles(h.SourceDir, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Contains("aaaa", Directory.EnumerateFiles(h.SourceDir, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
    }

    [Fact]
    public async Task A_name_that_differs_only_in_letter_case_is_renamed_in_one_step_on_Windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // On other systems a different letter case is a different name, so this is an ordinary move.
        }

        var h = NewHarness();
        var path = h.Write(Path.Combine("Movies", "Dune (2021)", "dune (2021).mkv"), "dune");
        await h.RegisterAsync(path, "Dune", "2021");

        var result = await h.RunAsync();

        Assert.Equal(0, result.Failed);
        Assert.Equal("Dune (2021).mkv", Path.GetFileName(Directory.EnumerateFiles(Path.Combine(h.SourceDir, "Movies", "Dune (2021)")).Single()));
    }

    [Fact]
    public async Task A_move_finished_on_disk_is_recorded_even_when_the_folder_is_switched_to_As_is_before_restart()
    {
        var h = NewHarness();
        var path = h.Write("raw.mkv", "dune");
        var id = await h.RegisterAsync(path, "Dune", "2021");
        h.Mover.AfterMove = (_, _) => throw new SimulatedCrashException();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => h.RunAsync());

        h.Mover.AfterMove = null;
        h.SetManaged(false);
        var result = await h.RunAsync();

        Assert.Equal(FolderOrganiseStop.SourceNotManaged, result.Stop);
        var asset = await h.Assets.FindByIdAsync(id);
        Assert.True(File.Exists(asset!.FilePathRoot));
        Assert.Equal(Path.Combine(h.SourceDir, "Movies", "Dune (2021)", "Dune (2021).mkv"), asset.FilePathRoot);
        Assert.Empty(await h.Ledger.ListUnfinishedAsync(h.OperationId));
    }

    // ── The time estimate ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_estimate_uses_the_defaults_then_earlier_jobs_then_the_live_rate()
    {
        // About five seconds a video and one a song or book.
        Assert.Equal(TimeSpan.FromSeconds(100 * 5 + 40 * 1), FolderConversionEstimator.Estimate(100, 40));
        // An earlier job that averaged 2 seconds a file replaces the defaults.
        Assert.Equal(TimeSpan.FromSeconds(280), FolderConversionEstimator.Estimate(100, 40, measuredSecondsPerFile: 2));

        var upFront = TimeSpan.FromSeconds(1000);
        // Before 50 files the up-front estimate scales with what is left.
        Assert.Equal(TimeSpan.FromSeconds(900), FolderConversionEstimator.Remaining(100, 10, TimeSpan.FromSeconds(5), upFront));
        // After 50 files the live rate takes over: 60 files in 120 seconds is 2 seconds a file, 40 files left.
        Assert.Equal(TimeSpan.FromSeconds(80), FolderConversionEstimator.Remaining(100, 60, TimeSpan.FromSeconds(120), upFront));
        Assert.Equal(TimeSpan.Zero, FolderConversionEstimator.Remaining(100, 100, TimeSpan.FromSeconds(1), upFront));
    }

    [Fact]
    public async Task Folders_below_the_folder_being_converted_are_found_but_dot_folders_are_not()
    {
        var h = NewHarness();
        h.Write(Path.Combine("a", "b", "one.mkv"));
        h.Write(Path.Combine(".data", "hidden.mkv"));
        h.Write(Path.Combine("a", "note.txt"));

        var files = FolderMediaEnumerator.Enumerate(h.SourceDir, includeSubdirectories: true, FolderMediaEnumerator.DefaultMediaExtensions);

        Assert.Equal([Path.Combine(h.SourceDir, "a", "b", "one.mkv")], files);
        await Task.CompletedTask;
    }
}
