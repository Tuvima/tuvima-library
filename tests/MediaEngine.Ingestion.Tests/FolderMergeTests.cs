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
        Action<CrashProxy<IMediaAssetRepository>>? scriptAssets = null)
    {
        var harness = new FolderMergeHarness(duplicatePolicy, sourceManaged, move, scriptAssets);
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
