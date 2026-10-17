using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Tests.Helpers;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// Merging one folder into a managed folder, on real temp folders and a real data store: copies are checked byte for byte, a
/// Copy merge never writes the source, a Move removes an original only after its copy is confirmed, duplicates follow the
/// global rule, files that cannot be named yet go to Review with the From merge badge, and a stopped merge resumes without
/// losing or doubling a file.
/// </summary>
public sealed class FolderMergeTests : IDisposable
{
    private readonly List<FolderMergeHarness> _harnesses = [];

    public void Dispose()
    {
        foreach (var harness in _harnesses)
        {
            harness.Dispose();
        }
    }

    private FolderMergeHarness NewHarness(
        string duplicatePolicy = LibraryDuplicatePolicies.KeepOne,
        bool sourceManaged = false,
        bool move = false,
        Action<CrashProxy<IMediaAssetRepository>>? scriptAssets = null,
        TimeSpan? heartbeat = null)
    {
        var harness = new FolderMergeHarness(duplicatePolicy, sourceManaged, move, scriptAssets, heartbeat);
        _harnesses.Add(harness);
        return harness;
    }

    private static string Standard(string title, string year) => $"Movies/{title} ({year})/{title} ({year}).mkv";

    // ── Copy: the source is never written ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_copy_merge_leaves_the_source_byte_identical_and_puts_checked_copies_at_the_standard_names()
    {
        var h = NewHarness();
        var dune = h.WriteSource("Dune.2021.1080p.mkv", "dune bytes");
        var alien = h.WriteSource(Path.Combine("old stuff", "alien_1979.mkv"), "alien bytes");
        h.WriteSource("Dune.2021.1080p.en.srt", "english subtitles");
        h.WriteSource("Dune.2021.1080p.nfo", "nfo");
        h.WriteSource("readme.txt", "not a media file");
        var duneId = await h.RegisterAsync(dune, "Dune", "2021");
        var alienId = await h.RegisterAsync(alien, "Alien", "1979");
        var before = FolderMergeHarness.Fingerprint(h.SourceDir);

        var result = await h.RunAsync();

        Assert.Equal(FolderOrganiseStop.Finished, result.Stop);
        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Completed);
        Assert.Equal(0, result.Failed);
        Assert.False(result.IsMove);
        Assert.Equal(2, result.Count(FolderOrganiseOutcomes.Copied));
        Assert.Equal(before, FolderMergeHarness.Fingerprint(h.SourceDir));
        Assert.Equal(
            [
                "Movies/Alien (1979)/Alien (1979).mkv",
                "Movies/Dune (2021)/Dune (2021).en.srt",
                "Movies/Dune (2021)/Dune (2021).mkv",
                "Movies/Dune (2021)/Dune (2021).nfo",
            ],
            h.TargetFiles());
        Assert.Equal("dune bytes", File.ReadAllText(Path.Combine(h.TargetDir, "Movies", "Dune (2021)", "Dune (2021).mkv")));
        Assert.Equal("english subtitles", File.ReadAllText(Path.Combine(h.TargetDir, "Movies", "Dune (2021)", "Dune (2021).en.srt")));

        // The library is catalogued once, as the target copies.
        Assert.Equal(Path.Combine(h.TargetDir, "Movies", "Dune (2021)", "Dune (2021).mkv"), (await h.Assets.FindByIdAsync(duneId))!.FilePathRoot);
        Assert.Equal(Path.Combine(h.TargetDir, "Movies", "Alien (1979)", "Alien (1979).mkv"), (await h.Assets.FindByIdAsync(alienId))!.FilePathRoot);

        // Every copy is in the ledger, old path to new path, and History says so for each item.
        var entry = await h.Ledger.GetAsync(h.OperationId, dune);
        Assert.NotNull(entry);
        Assert.Equal(FolderOrganiseOutcomes.Copied, entry.Outcome);
        Assert.Equal(Path.Combine(h.TargetDir, "Movies", "Dune (2021)", "Dune (2021).mkv"), entry.NewPath);
        var sidecar = await h.Ledger.GetAsync(h.OperationId, Path.Combine(h.SourceDir, "Dune.2021.1080p.en.srt"));
        Assert.Equal(FolderOrganiseOutcomes.SidecarCopied, sidecar!.Outcome);
        var history = await h.Activity.GetRecentAsync(20);
        Assert.Equal(2, history.Count(item => item.ActionType == SystemActionType.PathUpdated));
        Assert.Contains(history, item => item.EntityId == duneId && item.Detail!.Contains("Copied by a folder merge"));
    }

    [Fact]
    public async Task A_copy_out_of_a_managed_folder_does_not_write_the_source_either()
    {
        var h = NewHarness(sourceManaged: true);
        var dune = h.WriteSource(Path.Combine("rips", "Dune.2021.mkv"), "dune");
        h.WriteSource(Path.Combine("rips", "Dune.2021.jpg"), "jpg");
        await h.RegisterAsync(dune, "Dune", "2021");
        var before = FolderMergeHarness.Fingerprint(h.SourceDir);

        var result = await h.RunAsync();

        Assert.Equal(1, result.Count(FolderOrganiseOutcomes.Copied));
        Assert.Equal(before, FolderMergeHarness.Fingerprint(h.SourceDir));
        Assert.Equal(["Movies/Dune (2021)/Dune (2021).jpg", Standard("Dune", "2021")], h.TargetFiles());
    }

    // ── Move: bytes are verified before an original is removed ───────────────────────────────────────────────────

    [Fact]
    public async Task A_move_removes_each_original_after_its_copy_is_checked_and_tidies_empty_folders_but_not_the_source()
    {
        var h = NewHarness(sourceManaged: true, move: true);
        var dune = h.WriteSource("Dune.2021.mkv", "dune");
        var alien = h.WriteSource(Path.Combine("old", "alien.mkv"), "alien");
        h.WriteSource(Path.Combine("old", "alien.en.srt"), "subs");
        h.WriteSource(Path.Combine("gone", "film.mkv"), "film");
        h.WriteSource(Path.Combine("old", "notes.txt"), "keep me");
        await h.RegisterAsync(dune, "Dune", "2021");
        var alienId = await h.RegisterAsync(alien, "Alien", "1979");
        await h.RegisterAsync(Path.Combine(h.SourceDir, "gone", "film.mkv"), "Heat", "1995");

        var result = await h.RunAsync();

        Assert.Equal(FolderOrganiseStop.Finished, result.Stop);
        Assert.True(result.IsMove);
        Assert.Equal(3, result.Count(FolderOrganiseOutcomes.Moved));
        Assert.Equal(
            [
                "Movies/Alien (1979)/Alien (1979).en.srt",
                "Movies/Alien (1979)/Alien (1979).mkv",
                "Movies/Dune (2021)/Dune (2021).mkv",
                "Movies/Heat (1995)/Heat (1995).mkv",
            ],
            h.TargetFiles());
        Assert.Equal(["old/notes.txt"], h.SourceFiles());
        Assert.False(Directory.Exists(Path.Combine(h.SourceDir, "gone")));
        Assert.True(Directory.Exists(h.SourceDir));
        Assert.Equal(Path.Combine(h.TargetDir, "Movies", "Alien (1979)", "Alien (1979).mkv"), (await h.Assets.FindByIdAsync(alienId))!.FilePathRoot);
        Assert.Equal(FolderOrganiseOutcomes.NotMoved, (await h.Ledger.GetAsync(h.OperationId, Path.Combine(h.SourceDir, "old", "notes.txt")))!.Outcome);
    }

    [Fact]
    public async Task A_move_never_deletes_an_original_when_the_copy_does_not_match()
    {
        var h = NewHarness(sourceManaged: true, move: true);
        var dune = h.WriteSource("Dune.2021.mkv", "dune");
        var duneId = await h.RegisterAsync(dune, "Dune", "2021");
        // The temporary copy reads back different bytes than the original.
        h.Hasher.Override = path => path.EndsWith(MergeFileTransfer.PartSuffix, StringComparison.Ordinal) ? new string('f', 64) : null;

        var result = await h.RunAsync();

        Assert.Equal(1, result.Failed);
        Assert.Equal(["Dune.2021.mkv"], h.SourceFiles());
        Assert.Equal("dune", File.ReadAllText(dune));
        Assert.Empty(h.TargetFiles());
        Assert.Equal(dune, (await h.Assets.FindByIdAsync(duneId))!.FilePathRoot);
        var entry = await h.Ledger.GetAsync(h.OperationId, dune);
        Assert.Equal(FolderOrganiseOutcomes.Failed, entry!.Outcome);
        Assert.Contains("still in the source folder", entry.Detail);
    }

    [Fact]
    public async Task A_move_keeps_the_original_when_the_copy_cannot_be_confirmed_just_before_removal()
    {
        var h = NewHarness(sourceManaged: true, move: true);
        var dune = h.WriteSource("Dune.2021.mkv", "dune");
        await h.RegisterAsync(dune, "Dune", "2021");
        // The copy checks out while it is a temporary file, but the file at its final name reads back different.
        h.Hasher.Override = path => path.StartsWith(h.TargetDir, StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(MergeFileTransfer.PartSuffix, StringComparison.Ordinal)
            ? new string('e', 64)
            : null;

        var result = await h.RunAsync();

        Assert.Equal(0, result.Failed);
        Assert.Equal(["Dune.2021.mkv"], h.SourceFiles());
        Assert.Equal([Standard("Dune", "2021")], h.TargetFiles());
        var entry = await h.Ledger.GetAsync(h.OperationId, dune);
        Assert.Equal(FolderOrganiseOutcomes.Copied, entry!.Outcome);
        Assert.Contains("original could not be removed", entry.Detail);
    }

    [Fact]
    public async Task A_folders_shared_artwork_is_copied_beside_each_film_in_a_copy_and_moved_with_the_last_one_in_a_move()
    {
        var copy = NewHarness();
        var alien = copy.WriteSource("alien.mkv", "alien");
        var brazil = copy.WriteSource("brazil.mkv", "brazil");
        copy.WriteSource("poster.jpg", "shared poster");
        await copy.RegisterAsync(alien, "Alien", "1979");
        await copy.RegisterAsync(brazil, "Brazil", "1985");
        var before = FolderMergeHarness.Fingerprint(copy.SourceDir);

        await copy.RunAsync();

        Assert.Equal(before, FolderMergeHarness.Fingerprint(copy.SourceDir));
        Assert.Equal("shared poster", File.ReadAllText(Path.Combine(copy.TargetDir, "Movies", "Alien (1979)", "poster.jpg")));
        Assert.Equal("shared poster", File.ReadAllText(Path.Combine(copy.TargetDir, "Movies", "Brazil (1985)", "poster.jpg")));

        var move = NewHarness(sourceManaged: true, move: true);
        var dune = move.WriteSource(Path.Combine("one film", "Dune.2021.mkv"), "dune");
        move.WriteSource(Path.Combine("one film", "cover.jpg"), "cover");
        var heat = move.WriteSource(Path.Combine("two films", "heat.mkv"), "heat");
        var ronin = move.WriteSource(Path.Combine("two films", "ronin.mkv"), "ronin");
        move.WriteSource(Path.Combine("two films", "poster.jpg"), "shared");
        await move.RegisterAsync(dune, "Dune", "2021");
        await move.RegisterAsync(heat, "Heat", "1995");
        await move.RegisterAsync(ronin, "Ronin", "1998");

        await move.RunAsync();

        // The only film in its folder takes the cover with it; with two films the shared poster goes with the last to leave.
        Assert.Equal("cover", File.ReadAllText(Path.Combine(move.TargetDir, "Movies", "Dune (2021)", "cover.jpg")));
        Assert.Empty(move.SourceFiles());
        Assert.Contains("Movies/Ronin (1998)/poster.jpg", move.TargetFiles());
        Assert.DoesNotContain("Movies/Heat (1995)/poster.jpg", move.TargetFiles());
    }

    [Fact]
    public async Task While_a_big_file_is_copied_the_job_keeps_reporting_in()
    {
        var h = NewHarness(heartbeat: TimeSpan.FromMilliseconds(20));
        var dune = h.WriteSource("Dune.2021.mkv", "dune");
        await h.RegisterAsync(dune, "Dune", "2021");
        var host = new TestHost();
        bool MidCopy(FolderOrganiseProgress progress) => progress.Completed == 0 && progress.CurrentPath == dune;

        // The copy stays busy until the heartbeat has reported in twice, so a slow machine can't end it first.
        h.Hasher.Override = path =>
        {
            if (path.EndsWith(MergeFileTransfer.PartSuffix, StringComparison.Ordinal))
            {
                SpinWait.SpinUntil(() => host.CountProgress(MidCopy) >= 2, TimeSpan.FromSeconds(10));
            }

            return null;
        };

        await h.RunAsync(host);

        Assert.True(host.CountProgress(MidCopy) >= 2);
    }

    // ── Duplicates follow the global rule ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Keep_one_copy_does_not_copy_an_exact_duplicate_and_leaves_the_original_alone()
    {
        var h = NewHarness();
        var dune = h.WriteSource("Dune.2021.mkv", "dune");
        h.WriteTarget(Standard("Dune", "2021"), "dune");
        var duneId = await h.RegisterAsync(dune, "Dune", "2021");

        var result = await h.RunAsync();

        Assert.Equal(1, result.Count(FolderOrganiseOutcomes.DuplicateSkipped));
        Assert.Equal([Standard("Dune", "2021")], h.TargetFiles());
        Assert.Equal(["Dune.2021.mkv"], h.SourceFiles());
        // The item is catalogued once, as the copy the target already had.
        Assert.Equal(Path.Combine(h.TargetDir, "Movies", "Dune (2021)", "Dune (2021).mkv"), (await h.Assets.FindByIdAsync(duneId))!.FilePathRoot);
    }

    [Fact]
    public async Task Keep_one_copy_with_a_move_removes_the_duplicate_original_once_the_kept_copy_is_confirmed()
    {
        var h = NewHarness(sourceManaged: true, move: true);
        var dune = h.WriteSource("Dune.2021.mkv", "dune");
        h.WriteTarget(Standard("Dune", "2021"), "dune");
        var duneId = await h.RegisterAsync(dune, "Dune", "2021");

        var result = await h.RunAsync();

        Assert.Equal(1, result.Count(FolderOrganiseOutcomes.DuplicateRemoved));
        Assert.Empty(h.SourceFiles());
        Assert.Equal([Standard("Dune", "2021")], h.TargetFiles());
        Assert.Equal(Path.Combine(h.TargetDir, "Movies", "Dune (2021)", "Dune (2021).mkv"), (await h.Assets.FindByIdAsync(duneId))!.FilePathRoot);
    }

    [Fact]
    public async Task Keep_both_copies_an_exact_duplicate_under_a_numbered_name_without_a_review_item()
    {
        var h = NewHarness(LibraryDuplicatePolicies.KeepBoth);
        var dune = h.WriteSource("Dune.2021.mkv", "dune");
        h.WriteTarget(Standard("Dune", "2021"), "dune");
        await h.RegisterAsync(dune, "Dune", "2021");

        var result = await h.RunAsync();

        Assert.Equal(1, result.Count(FolderOrganiseOutcomes.CopiedWithUniqueName));
        Assert.Equal(["Movies/Dune (2021)/Dune (2021) (2).mkv", Standard("Dune", "2021")], h.TargetFiles());
        Assert.DoesNotContain(await h.Reviews.GetPendingAsync(50), review => review.Trigger == ReviewTrigger.NameCollision);
    }

    [Fact]
    public async Task Two_different_files_with_one_standard_name_never_overwrite_and_open_a_review_item()
    {
        var h = NewHarness();
        var dune = h.WriteSource("Dune.2021.mkv", "the new dune");
        h.WriteTarget(Standard("Dune", "2021"), "the old dune");
        await h.RegisterAsync(dune, "Dune", "2021");

        var result = await h.RunAsync();

        Assert.Equal(1, result.Count(FolderOrganiseOutcomes.CopiedWithUniqueName));
        Assert.Equal("the old dune", File.ReadAllText(Path.Combine(h.TargetDir, "Movies", "Dune (2021)", "Dune (2021).mkv")));
        Assert.Equal("the new dune", File.ReadAllText(Path.Combine(h.TargetDir, "Movies", "Dune (2021)", "Dune (2021) (2).mkv")));
        Assert.Single(await h.Reviews.GetPendingAsync(50), review => review.Trigger == ReviewTrigger.NameCollision);
    }

    // ── Files that cannot be named yet go to Review with the From merge badge ────────────────────────────────────

    [Fact]
    public async Task Files_that_cannot_be_named_go_to_the_From_merge_folder_and_open_a_From_merge_review_item()
    {
        var h = NewHarness();
        var untitled = h.WriteSource("nothing.mkv", "nothing");
        var unsure = h.WriteSource("mystery.mkv", "mystery");
        var untitledId = await h.RegisterAsync(untitled, "Unknown");
        var unsureId = await h.RegisterAsync(unsure, "Mystery Movie", "2000");
        await h.AddPendingReviewAsync(unsureId);
        var before = FolderMergeHarness.Fingerprint(h.SourceDir);

        var result = await h.RunAsync();

        Assert.Equal(2, result.Count(FolderOrganiseOutcomes.MergedForReview));
        Assert.Equal(["From merge/Movies/mystery.mkv", "From merge/Movies/nothing.mkv"], h.TargetFiles());
        Assert.Equal(before, FolderMergeHarness.Fingerprint(h.SourceDir));
        Assert.Equal(Path.Combine(h.TargetDir, "From merge", "Movies", "nothing.mkv"), (await h.Assets.FindByIdAsync(untitledId))!.FilePathRoot);

        // The badge goes on files nothing else flagged; one already waiting in Review keeps its own item.
        var fromMerge = (await h.Reviews.GetPendingAsync(50)).Where(review => review.Trigger == ReviewTrigger.FromMerge).ToList();
        var entry = Assert.Single(fromMerge);
        Assert.Equal(untitledId, entry.EntityId);
        Assert.Equal(ReviewRootCause.InsufficientEvidence, ReviewRootCauseExtensions.FromTrigger(entry.Trigger));
        Assert.NotNull(entry.ReviewReadyAt);
        Assert.Single((await h.Reviews.GetPendingByEntityAsync(unsureId)));
    }

    [Fact]
    public async Task A_file_the_library_has_not_seen_goes_to_From_merge_and_is_sent_to_be_identified()
    {
        var h = NewHarness();
        var unseen = h.WriteSource("home video.mkv", "family");

        var result = await h.RunAsync();

        Assert.Equal(1, result.Count(FolderOrganiseOutcomes.MergedForReview));
        Assert.Equal(["From merge/Movies/home video.mkv"], h.TargetFiles());
        Assert.Equal([Path.Combine(h.TargetDir, "From merge", "Movies", "home video.mkv")], h.Intake.Paths);
        Assert.True(File.Exists(unseen));
    }

    [Fact]
    public async Task A_file_the_library_has_not_seen_that_is_an_exact_copy_of_one_it_holds_is_not_copied_again()
    {
        var h = NewHarness();
        var held = h.WriteTarget(Standard("Dune", "2021"), "dune");
        await h.RegisterAsync(held, "Dune", "2021");
        h.WriteSource("Dune.copy.mkv", "dune");

        var result = await h.RunAsync();

        Assert.Equal(1, result.Count(FolderOrganiseOutcomes.DuplicateSkipped));
        Assert.Equal([Standard("Dune", "2021")], h.TargetFiles());
        Assert.Empty(h.Intake.Paths);
    }

    // ── Progress, pause, cancel, restart ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<Guid>> ThreeFilmsAsync(FolderMergeHarness h)
    {
        var ids = new List<Guid>();
        foreach (var (name, title, year) in new[] { ("a.mkv", "Alien", "1979"), ("b.mkv", "Brazil", "1985"), ("c.mkv", "Casablanca", "1942") })
        {
            ids.Add(await h.RegisterAsync(h.WriteSource(name, title), title, year));
        }

        return ids;
    }

    [Fact]
    public async Task Progress_is_reported_and_a_paused_merge_resumes_without_repeating_a_file()
    {
        var h = NewHarness();
        await ThreeFilmsAsync(h);
        var host = new TestHost { Signal = asked => asked == 1 ? FolderJobSignal.Pause : FolderJobSignal.Continue };

        var paused = await h.RunAsync(host);

        Assert.Equal(FolderOrganiseStop.Paused, paused.Stop);
        Assert.Equal(1, paused.Completed);
        Assert.Equal(3, paused.Total);
        Assert.Single(h.TargetFiles());
        Assert.All(host.Progress, progress => Assert.Equal(3, progress.Total));
        Assert.Contains(host.Progress, progress => progress.Completed == 1);

        var resumedHost = new TestHost();
        var resumed = await h.RunAsync(resumedHost);

        Assert.Equal(FolderOrganiseStop.Finished, resumed.Stop);
        Assert.Equal(3, resumed.Completed);
        Assert.Equal(3, h.TargetFiles().Count());
        Assert.Equal(3, await h.Ledger.CountAsync(h.OperationId));
        Assert.Equal(3, resumed.Count(FolderOrganiseOutcomes.Copied));
    }

    [Fact]
    public async Task Cancelling_keeps_what_was_copied_and_the_log_says_what_is_still_only_in_the_source()
    {
        var h = NewHarness();
        await ThreeFilmsAsync(h);
        var host = new TestHost { Signal = asked => asked == 2 ? FolderJobSignal.Cancel : FolderJobSignal.Continue };
        var before = FolderMergeHarness.Fingerprint(h.SourceDir);

        var result = await h.RunAsync(host);

        Assert.Equal(FolderOrganiseStop.Cancelled, result.Stop);
        Assert.Equal(2, h.TargetFiles().Count());
        Assert.Equal(before, FolderMergeHarness.Fingerprint(h.SourceDir));
        Assert.Equal(1, result.Count(FolderOrganiseOutcomes.LeftInSource));
        var left = await h.Ledger.GetAsync(h.OperationId, Path.Combine(h.SourceDir, "c.mkv"));
        Assert.Equal(FolderOrganiseOutcomes.LeftInSource, left!.Outcome);
        Assert.Contains("still only in the source folder", left.Detail);
    }

    // ── A cancelled merge never loses an original, and starting it again does not copy twice ────────────────────

    [Theory]
    [InlineData(LibraryDuplicatePolicies.KeepOne)]
    [InlineData(LibraryDuplicatePolicies.KeepBoth)]
    public async Task A_cancelled_copy_merge_started_again_skips_files_already_copied_and_makes_no_numbered_duplicates(string policy)
    {
        var h = NewHarness(policy);
        await ThreeFilmsAsync(h);
        var cancelled = await h.RunAsync(new TestHost { Signal = asked => asked == 2 ? FolderJobSignal.Cancel : FolderJobSignal.Continue });
        Assert.Equal(FolderOrganiseStop.Cancelled, cancelled.Stop);
        var copiedBefore = h.TargetFiles().ToList();
        Assert.Equal(2, copiedBefore.Count);

        // The folder is a plain folder again; the person starts the same merge once more.
        h.SettleCancelledMerge();
        h.StartMergeAgain();
        var before = FolderMergeHarness.Fingerprint(h.SourceDir);
        var restarted = await h.RunAsync();

        Assert.Equal(FolderOrganiseStop.Finished, restarted.Stop);
        Assert.Equal(
            [Standard("Alien", "1979"), Standard("Brazil", "1985"), Standard("Casablanca", "1942")],
            h.TargetFiles());
        Assert.Equal(before, FolderMergeHarness.Fingerprint(h.SourceDir));
        Assert.Equal(3, restarted.Count(FolderOrganiseOutcomes.Copied));
        var carried = await h.Ledger.GetAsync(h.OperationId, Path.Combine(h.SourceDir, "a.mkv"));
        Assert.Equal(FolderOrganiseOutcomes.Copied, carried!.Outcome);
        Assert.Contains("not copied again", carried.Detail);
        Assert.DoesNotContain(await h.Reviews.GetPendingAsync(50), review => review.Trigger == ReviewTrigger.NameCollision);
    }

    [Fact]
    public async Task A_cancelled_copy_merge_started_again_as_a_move_removes_each_original_after_the_copy_is_checked()
    {
        var h = NewHarness(sourceManaged: true);
        var ids = await ThreeFilmsAsync(h);
        await h.RunAsync(new TestHost { Signal = asked => asked == 2 ? FolderJobSignal.Cancel : FolderJobSignal.Continue });
        h.SettleCancelledMerge();
        h.StartMergeAgain(move: true);

        var restarted = await h.RunAsync();

        Assert.Equal(FolderOrganiseStop.Finished, restarted.Stop);
        Assert.Empty(h.SourceFiles());
        Assert.Equal(
            [Standard("Alien", "1979"), Standard("Brazil", "1985"), Standard("Casablanca", "1942")],
            h.TargetFiles());
        Assert.Equal(3, restarted.Count(FolderOrganiseOutcomes.Moved));
        Assert.Equal(Path.Combine(h.TargetDir, "Movies", "Alien (1979)", "Alien (1979).mkv"), (await h.RealAssets.FindByIdAsync(ids[0]))!.FilePathRoot);
    }

    [Fact]
    public async Task A_copy_that_no_longer_matches_its_original_is_not_taken_as_already_copied()
    {
        var h = NewHarness();
        await ThreeFilmsAsync(h);
        await h.RunAsync(new TestHost { Signal = asked => asked == 1 ? FolderJobSignal.Cancel : FolderJobSignal.Continue });
        var copy = Path.Combine(h.TargetDir, "Movies", "Alien (1979)", "Alien (1979).mkv");
        File.WriteAllText(copy, "somebody changed this");
        h.SettleCancelledMerge();
        h.StartMergeAgain();

        var carried = await h.Organiser.TryCarryOnEarlierCopyAsync(
            Path.Combine(h.SourceDir, "a.mkv"), FolderMergeContext.TryCreateForJob(h.Config.Libraries, h.OperationId, h.SourceId, out _)!);

        Assert.Null(carried);
        Assert.Equal("somebody changed this", File.ReadAllText(copy));
        Assert.Equal("Alien", File.ReadAllText(Path.Combine(h.SourceDir, "a.mkv")));
    }

    [Fact]
    public async Task After_a_cancelled_merge_the_gate_refuses_to_delete_or_move_every_original_it_listed()
    {
        var h = NewHarness(sourceManaged: true);
        await ThreeFilmsAsync(h);
        await h.RunAsync(new TestHost { Signal = asked => asked == 2 ? FolderJobSignal.Cancel : FolderJobSignal.Continue });
        h.SettleCancelledMerge();
        var untouched = h.WriteSource("d.mkv", "not part of the merge");
        var gate = new MediaEngine.Ingestion.SourceMutationPolicyGate(h.Ledger);
        var policy = MediaEngine.Ingestion.Services.FileSourceMutationPolicyFactory.Create(
            h.Config.Libraries.Libraries[0], h.Source, allowDelete: true);

        foreach (var name in new[] { "a.mkv", "b.mkv", "c.mkv" })
        {
            foreach (var kind in new[]
            {
                MediaEngine.Ingestion.Models.SourceMutationKind.Delete,
                MediaEngine.Ingestion.Models.SourceMutationKind.Move,
                MediaEngine.Ingestion.Models.SourceMutationKind.Rename,
            })
            {
                var decision = gate.Evaluate(new MediaEngine.Ingestion.Models.SourceMutationRequest
                {
                    Source = policy,
                    Mutation = kind,
                    Path = Path.Combine(h.SourceDir, name),
                });
                Assert.False(decision.Allowed, $"{kind} of {name} must be refused");
            }
        }

        // c.mkv was never reached (left in source) and a.mkv and b.mkv were copied: all three are protected. A file no merge
        // listed is an ordinary file and follows the usual rules.
        Assert.True(gate.Evaluate(new MediaEngine.Ingestion.Models.SourceMutationRequest
        {
            Source = policy,
            Mutation = MediaEngine.Ingestion.Models.SourceMutationKind.Delete,
            Path = untouched,
        }).Allowed);
    }

    [Fact]
    public async Task The_merge_job_itself_may_still_remove_an_original_it_listed_once_its_copy_is_checked()
    {
        var h = NewHarness(sourceManaged: true, move: true);
        await ThreeFilmsAsync(h);

        var result = await h.RunAsync();

        // Every original is a ledger line, and the merge's own policy still lets it remove them.
        Assert.Equal(3, result.Count(FolderOrganiseOutcomes.Moved));
        Assert.Empty(h.SourceFiles());
    }

    [Fact]
    public async Task A_merge_stopped_right_after_a_copy_finishes_it_on_restart_without_a_second_copy()
    {
        var h = NewHarness(scriptAssets: assets => assets.CrashOnNext("UpdateFilePathAsync"));
        var ids = await ThreeFilmsAsync(h);

        // The Engine is killed after the first file was copied but before the library learned about it.
        await Assert.ThrowsAsync<SimulatedCrashException>(() => h.RunAsync());
        Assert.Single(h.TargetFiles());
        Assert.Single(await h.Ledger.ListUnfinishedAsync(h.OperationId));
        Assert.Equal(Path.Combine(h.SourceDir, "a.mkv"), (await h.RealAssets.FindByIdAsync(ids[0]))!.FilePathRoot);

        var restarted = h.NewRunner(h.RealAssets);
        var result = await restarted.RunAsync(h.Job, new TestHost());

        Assert.Equal(FolderOrganiseStop.Finished, result.Stop);
        Assert.Equal(3, h.TargetFiles().Count());
        Assert.Empty(await h.Ledger.ListUnfinishedAsync(h.OperationId));
        Assert.Equal(Path.Combine(h.TargetDir, "Movies", "Alien (1979)", "Alien (1979).mkv"), (await h.RealAssets.FindByIdAsync(ids[0]))!.FilePathRoot);
        Assert.Equal(["a.mkv", "b.mkv", "c.mkv"], h.SourceFiles());
    }

    [Fact]
    public async Task A_move_stopped_after_the_copy_is_finished_on_restart_and_only_then_is_the_original_removed()
    {
        var h = NewHarness(sourceManaged: true, move: true, scriptAssets: assets => assets.CrashOnNext("UpdateFilePathAsync"));
        var ids = await ThreeFilmsAsync(h);

        await Assert.ThrowsAsync<SimulatedCrashException>(() => h.RunAsync());
        // The copy is in place and checked, and the original has not been touched yet.
        Assert.Single(h.TargetFiles());
        Assert.Equal(["a.mkv", "b.mkv", "c.mkv"], h.SourceFiles());

        var restarted = h.NewRunner(h.RealAssets);
        var result = await restarted.RunAsync(h.Job, new TestHost());

        Assert.Equal(FolderOrganiseStop.Finished, result.Stop);
        Assert.Equal(3, h.TargetFiles().Count());
        Assert.Empty(h.SourceFiles());
        Assert.Equal(Path.Combine(h.TargetDir, "Movies", "Alien (1979)", "Alien (1979).mkv"), (await h.RealAssets.FindByIdAsync(ids[0]))!.FilePathRoot);
    }

    [Fact]
    public async Task A_temporary_copy_a_stopped_merge_left_behind_is_cleared_and_the_file_is_copied_afresh()
    {
        var h = NewHarness();
        var dune = h.WriteSource("Dune.2021.mkv", "dune");
        await h.RegisterAsync(dune, "Dune", "2021");
        var target = Path.Combine(h.TargetDir, "Movies", "Dune (2021)", "Dune (2021).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var stale = Path.Combine(Path.GetDirectoryName(target)!, $".Dune (2021).{Guid.NewGuid():N}{MergeFileTransfer.PartSuffix}");
        File.WriteAllText(stale, "half a file");
        await h.Ledger.UpsertAsync(new FolderOrganiseLogEntry(
            h.OperationId, h.SourceId, dune, target, FolderOrganiseOutcomes.Moving, DateTimeOffset.UtcNow, "transfer:"));

        var result = await h.RunAsync();

        Assert.Equal(1, result.Count(FolderOrganiseOutcomes.Copied));
        Assert.Equal([Standard("Dune", "2021")], h.TargetFiles());
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public async Task A_target_that_stops_being_managed_halts_the_merge_and_changes_nothing_more()
    {
        var h = NewHarness();
        await ThreeFilmsAsync(h);
        var host = new TestHost
        {
            Signal = asked =>
            {
                if (asked == 1)
                {
                    h.Target.ManagementMode = LibrarySourceManagementModes.ExistingLibrary;
                    h.Target.AccessMode = LibrarySourceAccessModes.ReadOnly;
                }

                return FolderJobSignal.Continue;
            },
        };

        var result = await h.RunAsync(host);

        Assert.Equal(FolderOrganiseStop.TargetUnavailable, result.Stop);
        Assert.Single(h.TargetFiles());
        Assert.Equal(2, result.Count(FolderOrganiseOutcomes.LeftInSource));
    }

    [Fact]
    public async Task A_move_stops_as_soon_as_its_source_is_switched_to_As_is_and_deletes_nothing_more()
    {
        var h = NewHarness(sourceManaged: true, move: true);
        await ThreeFilmsAsync(h);
        var host = new TestHost
        {
            Signal = asked =>
            {
                if (asked == 1)
                {
                    h.Source.ManagementMode = LibrarySourceManagementModes.ExistingLibrary;
                    h.Source.AccessMode = LibrarySourceAccessModes.ReadOnly;
                }

                return FolderJobSignal.Continue;
            },
        };

        var result = await h.RunAsync(host);

        Assert.Equal(FolderOrganiseStop.SourceNotManaged, result.Stop);
        Assert.Equal(["b.mkv", "c.mkv"], h.SourceFiles());
        Assert.Single(h.TargetFiles());
        Assert.Equal(2, result.Count(FolderOrganiseOutcomes.LeftInSource));
    }

    [Fact]
    public async Task After_a_paused_move_resumes_the_total_still_counts_the_files_already_moved()
    {
        var h = NewHarness(sourceManaged: true, move: true);
        await ThreeFilmsAsync(h);

        var paused = await h.RunAsync(new TestHost { Signal = asked => asked == 1 ? FolderJobSignal.Pause : FolderJobSignal.Continue });
        var resumed = await h.RunAsync();

        Assert.Equal(3, paused.Total);
        Assert.Equal(3, resumed.Total);
        Assert.Equal(3, resumed.Completed);
        Assert.Empty(h.SourceFiles());
    }

    [Fact]
    public async Task A_stop_after_a_copy_that_took_a_numbered_name_is_finished_at_that_name_without_a_second_copy()
    {
        var h = NewHarness(scriptAssets: assets => assets.CrashOnNext("UpdateFilePathAsync"));
        var dune = h.WriteSource("Dune.2021.mkv", "the new dune");
        h.WriteTarget(Standard("Dune", "2021"), "the old dune");
        var id = await h.RegisterAsync(dune, "Dune", "2021");

        await Assert.ThrowsAsync<SimulatedCrashException>(() => h.RunAsync());
        var unfinished = Assert.Single(await h.Ledger.ListUnfinishedAsync(h.OperationId));
        Assert.EndsWith("Dune (2021) (2).mkv", unfinished.NewPath, StringComparison.Ordinal);

        var result = await h.NewRunner(h.RealAssets).RunAsync(h.Job, new TestHost());

        Assert.Equal(FolderOrganiseStop.Finished, result.Stop);
        Assert.Equal(2, h.TargetFiles().Count());
        Assert.Equal(unfinished.NewPath, (await h.RealAssets.FindByIdAsync(id))!.FilePathRoot);
        Assert.Equal("the old dune", File.ReadAllText(Path.Combine(h.TargetDir, "Movies", "Dune (2021)", "Dune (2021).mkv")));
    }

    [Fact]
    public async Task A_folder_reached_through_a_shortcut_is_never_listed_copied_or_removed()
    {
        var h = NewHarness(sourceManaged: true, move: true);
        var outside = Path.Combine(h.Root, "elsewhere");
        Directory.CreateDirectory(outside);
        var real = Path.Combine(outside, "Heat.1995.mkv");
        File.WriteAllText(real, "heat");
        var link = Path.Combine(h.SourceDir, "Link");
        if (!TryCreateJunction(link, outside))
        {
            // This machine does not allow creating a link, so there is nothing to prove here.
            return;
        }

        h.WriteSource("Dune.2021.mkv", "dune");
        await h.RegisterAsync(Path.Combine(h.SourceDir, "Dune.2021.mkv"), "Dune", "2021");
        await h.RegisterAsync(real, "Heat", "1995");

        var result = await h.RunAsync();

        Assert.Equal(1, result.Total);
        Assert.True(File.Exists(real));
        Assert.Equal("heat", File.ReadAllText(real));
        Assert.Equal([Standard("Dune", "2021")], h.TargetFiles());
        Directory.Delete(link);
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                });
                process!.WaitForExit();
                return Directory.Exists(link);
            }

            Directory.CreateSymbolicLink(link, target);
            return Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _ = ex;
            return false;
        }
    }

    [Fact]
    public void A_target_that_is_itself_merging_or_merged_away_is_refused()
    {
        var h = NewHarness();
        h.Source.MergeState = LibrarySourceMergeStates.None;

        h.Target.MergeState = LibrarySourceMergeStates.Merging;
        Assert.Equal(FolderMergeProblem.TargetBusy, Check(h, h.TargetId.ToString("D")));
        h.Target.MergeState = LibrarySourceMergeStates.Merged;
        Assert.Equal(FolderMergeProblem.TargetBusy, Check(h, h.TargetId.ToString("D")));
        h.Target.MergeState = LibrarySourceMergeStates.None;
        Assert.Equal(FolderMergeProblem.None, Check(h, h.TargetId.ToString("D")));
    }

    [Fact]
    public void A_merged_away_folder_is_neither_scanned_nor_resolved_as_a_library_folder()
    {
        var h = NewHarness();
        h.Source.MergeState = LibrarySourceMergeStates.Merged;
        var entry = MediaEngine.Ingestion.Services.LibraryFolderEntryFactory.Create(h.Config.Libraries.Libraries[0]);
        h.Options.LibraryFolders = [entry];
        var resolver = new MediaEngine.Ingestion.Services.LibraryFolderResolver(new OptionsMonitorStub<MediaEngine.Ingestion.Models.IngestionOptions>(h.Options));

        Assert.DoesNotContain(h.SourceDir, entry.EffectiveSourcePaths);
        Assert.Contains(h.TargetDir, entry.EffectiveSourcePaths);
        Assert.Null(resolver.ResolveSourceForPath(Path.Combine(h.SourceDir, "Dune.2021.mkv")));
        Assert.NotNull(resolver.ResolveSourceForPath(Path.Combine(h.TargetDir, "Movies", "x.mkv")));
    }

    // ── Rules about the target and the source ────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_target_that_is_used_as_is_is_refused_and_never_written_to()
    {
        var h = NewHarness();
        h.Target.ManagementMode = LibrarySourceManagementModes.ExistingLibrary;
        h.Target.AccessMode = LibrarySourceAccessModes.ReadOnly;
        h.Source.MergeState = LibrarySourceMergeStates.None;

        var context = FolderMergeContext.TryCreateForPlan(h.Config.Libraries, h.SourceId, h.TargetId.ToString("D"), "copy", out var check);

        Assert.Null(context);
        Assert.Equal(FolderMergeProblem.TargetNotManaged, check.Problem);

        // Even a policy built for a merge target refuses an as-is folder at the gate.
        var library = h.Config.Libraries.Libraries[0];
        var policy = MediaEngine.Ingestion.Services.FileSourceMutationPolicyFactory.Create(library, h.Target, allowDelete: false, isMergeTarget: true);
        var decision = new SourceMutationPolicyGate().Evaluate(new MediaEngine.Ingestion.Models.SourceMutationRequest
        {
            Source = policy,
            Mutation = MediaEngine.Ingestion.Models.SourceMutationKind.UseAsDestination,
            Path = Path.Combine(h.TargetDir, "x.mkv"),
        });
        Assert.False(decision.Allowed);
    }

    [Fact]
    public void A_managed_target_that_is_not_the_new_media_folder_is_accepted_for_a_merge()
    {
        var h = NewHarness();
        h.Source.MergeState = LibrarySourceMergeStates.None;
        h.Config.Libraries.Libraries[0].PrimaryDestinationSourceId = null;

        var context = FolderMergeContext.TryCreateForPlan(h.Config.Libraries, h.SourceId, h.TargetId.ToString("D"), "copy", out var check);

        Assert.NotNull(context);
        Assert.True(check.IsOk);
        Assert.True(context.TargetPolicy.AllowAsDestination);
        Assert.False(context.TargetPolicy.AllowDelete);
        Assert.False(context.IsMove);
    }

    [Fact]
    public void A_target_in_another_library_or_inside_the_source_or_the_same_folder_is_refused()
    {
        var h = NewHarness();
        h.Source.MergeState = LibrarySourceMergeStates.None;
        h.Config.Libraries.Libraries.Add(new LibraryFolderConfig
        {
            Id = Guid.NewGuid().ToString("D"),
            Kind = LibraryKinds.Catalogued,
            MediaTypes = ["Music"],
            Sources =
            [
                new LibrarySourceConfig
                {
                    Id = "dddddddd-0000-4000-8000-000000000001",
                    Path = Path.Combine(h.Root, "Music"),
                    ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                    AccessMode = LibrarySourceAccessModes.Writable,
                },
            ],
        });

        Assert.Equal(FolderMergeProblem.NotSameLibrary, Check(h, "dddddddd-0000-4000-8000-000000000001"));
        Assert.Equal(FolderMergeProblem.SameFolder, Check(h, h.SourceId.ToString("D")));
        Assert.Equal(FolderMergeProblem.TargetRemoved, Check(h, Guid.NewGuid().ToString("D")));

        h.Target.Path = Path.Combine(h.SourceDir, "inside");
        Assert.Equal(FolderMergeProblem.NestedFolders, Check(h, h.TargetId.ToString("D")));
    }

    [Fact]
    public void Move_is_only_available_for_a_folder_Tuvima_manages_and_the_new_media_folder_cannot_be_merged_away()
    {
        var h = NewHarness();
        h.Source.MergeState = LibrarySourceMergeStates.None;
        Assert.Equal(FolderMergeProblem.MoveNeedsManagedSource, Check(h, h.TargetId.ToString("D"), "move"));

        h.Source.ManagementMode = LibrarySourceManagementModes.ManagedByTuvima;
        h.Source.AccessMode = LibrarySourceAccessModes.Writable;
        Assert.Equal(FolderMergeProblem.None, Check(h, h.TargetId.ToString("D"), "move"));
        Assert.Equal(FolderMergeProblem.InvalidMode, Check(h, h.TargetId.ToString("D"), "shred"));

        h.Config.Libraries.Libraries[0].PrimaryDestinationSourceId = h.SourceId.ToString("D");
        Assert.Equal(FolderMergeProblem.SourceIsNewMediaFolder, Check(h, h.TargetId.ToString("D")));

        h.Config.Libraries.Libraries[0].PrimaryDestinationSourceId = h.TargetId.ToString("D");
        h.Source.MergeState = LibrarySourceMergeStates.Merged;
        Assert.Equal(FolderMergeProblem.SourceBusy, Check(h, h.TargetId.ToString("D")));
    }

    private static FolderMergeProblem Check(FolderMergeHarness h, string targetId, string mode = "copy")
    {
        FolderMergeContext.TryCreateForPlan(h.Config.Libraries, h.SourceId, targetId, mode, out var check);
        return check.Problem;
    }

    // ── The preview changes nothing ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_preview_counts_what_would_happen_and_changes_nothing()
    {
        var h = NewHarness();
        h.Source.MergeState = LibrarySourceMergeStates.None;
        var dune = h.WriteSource("Dune.2021.mkv", "dune");
        var mystery = h.WriteSource("mystery.mkv", "mystery");
        h.WriteSource("home.mkv", "unseen");
        var alien = h.WriteSource("alien.mkv", "alien");
        h.WriteTarget(Standard("Alien", "1979"), "alien");
        await h.RegisterAsync(dune, "Dune", "2021");
        await h.RegisterAsync(mystery, "Unknown");
        await h.RegisterAsync(alien, "Alien", "1979");
        var sourceBefore = FolderMergeHarness.Fingerprint(h.SourceDir);
        var targetBefore = FolderMergeHarness.Fingerprint(h.TargetDir);

        var quick = h.Previewer.Count(h.SourceId, h.TargetId.ToString("D"), "copy", out _);
        var preview = await h.Previewer.PreviewAsync(h.SourceId, h.TargetId.ToString("D"), "copy", 0);

        Assert.Equal(4, quick!.TotalFiles);
        Assert.Equal(4, quick.VideoFiles);
        Assert.Equal("dune".Length + "mystery".Length + "unseen".Length + "alien".Length, quick.SourceBytes);
        Assert.NotNull(preview);
        Assert.True(preview.Complete);
        Assert.Equal(1, preview.ToTransfer);
        Assert.Equal(1, preview.AlreadyInTarget);
        Assert.Equal(2, preview.NeedsReview);
        Assert.Equal(sourceBefore, FolderMergeHarness.Fingerprint(h.SourceDir));
        Assert.Equal(targetBefore, FolderMergeHarness.Fingerprint(h.TargetDir));
        Assert.Equal(0, await h.Ledger.CountAsync(h.OperationId));
        Assert.Empty(h.Intake.Paths);
    }
}
