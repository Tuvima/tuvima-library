using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Tests.Helpers;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// Filing out of the Import folder, on real temp folders and a real data store: a file goes into the type's one marked
/// New media folder with the type's naming, or it stays exactly where it is.
/// </summary>
public sealed class ImportFolderFilingTests : IDisposable
{
    private readonly ImportFolderHarness _h;

    public ImportFolderFilingTests() => _h = new ImportFolderHarness();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task A_file_in_the_Movies_folder_is_filed_into_the_marked_folder_with_standard_naming()
    {
        var source = _h.WriteImportFile(Path.Combine("Movies", "Dune.2021.1080p.mkv"), "dune bytes");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021");

        await _h.Organizer.TryAutoOrganizeAsync(assetId);

        var expected = Path.Combine(_h.MoviesDir, "Movies", "Dune (2021)", "Dune (2021).mkv");
        Assert.True(File.Exists(expected));
        Assert.Equal("dune bytes", File.ReadAllText(expected));
        Assert.False(File.Exists(source));
        Assert.Equal(expected, (await _h.Assets.FindByIdAsync(assetId))!.FilePathRoot);

        // The file shows up in Recently Added: it is marked presented and announced as added.
        Assert.Contains(_h.Publisher.Published, item => item.EventName == SignalREvents.MediaAdded);
        using var connection = _h.Db.Connection.CreateConnection();
        Assert.NotNull(await Dapper.SqlMapper.ExecuteScalarAsync<string>(
            connection, "SELECT presented_at FROM media_assets WHERE content_hash IS NOT NULL LIMIT 1;"));

        // The folder a download program made is cleaned up, but the per-type folder itself is never removed.
        Assert.False(Directory.Exists(Path.GetDirectoryName(source)!) && Directory.GetFiles(Path.GetDirectoryName(source)!).Length > 0);
        Assert.True(Directory.Exists(Path.Combine(_h.ImportDir, "Movies")));
    }

    [Fact]
    public async Task Empty_folders_a_program_left_behind_are_removed_but_the_type_folder_stays()
    {
        var source = _h.WriteImportFile(Path.Combine("Movies", "Dune.2021", "Season Pack", "Dune.mkv"), "dune");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021");

        await _h.Organizer.TryAutoOrganizeAsync(assetId);

        Assert.False(Directory.Exists(Path.Combine(_h.ImportDir, "Movies", "Dune.2021")));
        Assert.True(Directory.Exists(Path.Combine(_h.ImportDir, "Movies")));
        Assert.True(Directory.Exists(_h.ImportDir));
    }

    [Fact]
    public async Task Nothing_is_ever_filed_into_a_managed_folder_that_is_not_marked_New_media()
    {
        var source = _h.WriteImportFile(Path.Combine("Movies", "Dune.mkv"), "dune");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021");

        await _h.Organizer.TryAutoOrganizeAsync(assetId);

        Assert.Empty(_h.FilesUnder(_h.ElsewhereDir));
        Assert.Single(_h.FilesUnder(_h.MoviesDir));
    }

    [Fact]
    public async Task When_the_marked_folder_is_offline_the_file_stays_untouched_and_is_filed_when_it_returns()
    {
        Directory.Delete(_h.MoviesDir);
        var source = _h.WriteImportFile(Path.Combine("Movies", "Dune.mkv"), "dune");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021");

        await _h.Organizer.TryAutoOrganizeAsync(assetId);

        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(_h.MoviesDir)); // nothing was created on the missing drive
        Assert.Empty(_h.FilesUnder(_h.ElsewhereDir)); // and nothing spilled into another folder
        Assert.Equal(source, (await _h.Assets.FindByIdAsync(assetId))!.FilePathRoot);

        var waiting = Assert.Single(await _h.Waiting.GetWaitingAsync());
        Assert.Equal(ImportDestinationState.Unavailable, waiting.Reason);

        Directory.CreateDirectory(_h.MoviesDir);
        var filed = await _h.Filer.FilePendingAsync();

        Assert.Equal(1, filed);
        Assert.False(File.Exists(source));
        Assert.Single(_h.FilesUnder(_h.MoviesDir));
        Assert.Empty(await _h.Waiting.GetWaitingAsync());
    }

    [Fact]
    public async Task A_type_without_a_managed_folder_keeps_its_files_and_reports_one_plain_sentence()
    {
        var comics = _h.WriteImportFile(Path.Combine("Comics", "Saga 01.cbz"), "saga");
        var more = _h.WriteImportFile(Path.Combine("Comics", "Saga 02.cbz"), "saga two");
        var third = _h.WriteImportFile(Path.Combine("Comics", "Saga 03.cbz"), "saga three");
        foreach (var (path, title) in new[] { (comics, "Saga 1"), (more, "Saga 2"), (third, "Saga 3") })
        {
            var id = await _h.RegisterAsync(path, MediaType.Comics, title, libraryId: null);
            await _h.Organizer.TryAutoOrganizeAsync(id);
        }

        Assert.All(new[] { comics, more, third }, path => Assert.True(File.Exists(path)));

        var waiting = Assert.Single(await _h.Waiting.GetWaitingAsync());
        Assert.Equal(ImportDestinationState.NoManagedFolder, waiting.Reason);
        Assert.Equal(3, waiting.FileCount);
        Assert.Equal("3 files in Import\\Comics can't be filed until Comics has a managed folder.", waiting.Message);

        // The next pass files them once a managed folder exists. Never before.
        var comicsDir = Path.Combine(_h.Root, "library", "Comics");
        Directory.CreateDirectory(comicsDir);
        _h.SetLibrary(ImportFolderHarness.NewLibrary(
            ImportFolderHarness.ComicsLibraryId, "Comics", "Comics", "comics-new",
            ImportFolderHarness.Managed("comics-new", comicsDir)));

        var filed = await _h.Filer.FilePendingAsync();

        Assert.Equal(3, filed);
        Assert.Equal(3, _h.FilesUnder(comicsDir).Count());
        Assert.Empty(await _h.Waiting.GetWaitingAsync());
    }

    [Fact]
    public async Task A_type_whose_only_folder_is_as_is_has_no_destination_and_nothing_is_written_to_it()
    {
        var comicsAsIs = Path.Combine(_h.Root, "asis-comics");
        Directory.CreateDirectory(comicsAsIs);
        _h.SetLibrary(ImportFolderHarness.NewLibrary(
            ImportFolderHarness.ComicsLibraryId, "Comics", "Comics",
            destinationId: "comics-asis",
            ImportFolderHarness.AsIs("comics-asis", comicsAsIs)));
        var source = _h.WriteImportFile(Path.Combine("Comics", "Saga 01.cbz"), "saga");
        var assetId = await _h.RegisterAsync(source, MediaType.Comics, "Saga 1", libraryId: ImportFolderHarness.ComicsLibraryId);

        await _h.Organizer.TryAutoOrganizeAsync(assetId);

        var destination = _h.Importer.ResolveDestination(MediaType.Comics);
        Assert.Equal(ImportDestinationState.NoManagedFolder, destination.State);
        Assert.True(File.Exists(source));
        Assert.Empty(_h.FilesUnder(comicsAsIs));
    }

    [Fact]
    public void The_source_policy_gate_refuses_an_as_is_folder_as_a_destination()
    {
        var asIsLibrary = new LibraryFolderEntry
        {
            Id = "lib",
            Sources = [new LibrarySourceEntry { Id = "asis", Path = _h.AsIsDir }],
            PrimaryDestinationSourceId = "asis",
        };
        var destination = new ImportDestination(
            "Movies", ImportDestinationState.Ready, asIsLibrary, asIsLibrary.Sources[0]);

        var allowed = ImportFolderGate.CanFile(
            new SourceMutationPolicyGate(),
            _h.Importer,
            Path.Combine(_h.ImportDir, "Movies", "Dune.mkv"),
            Path.Combine(_h.AsIsDir, "Dune.mkv"),
            destination,
            out var reason);

        Assert.False(allowed);
        Assert.Contains("read-only", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_source_policy_gate_refuses_a_path_outside_the_destination_folder()
    {
        var destination = _h.Importer.ResolveDestination(MediaType.Movies);
        Assert.True(destination.IsReady);

        var allowed = ImportFolderGate.CanFile(
            new SourceMutationPolicyGate(),
            _h.Importer,
            Path.Combine(_h.ImportDir, "Movies", "Dune.mkv"),
            Path.Combine(_h.ElsewhereDir, "Dune.mkv"),
            destination,
            out _);

        Assert.False(allowed);
    }

    [Fact]
    public async Task An_unmatched_file_stays_in_the_Import_folder_and_is_filed_once_it_is_resolved()
    {
        var source = _h.WriteImportFile(Path.Combine("Movies", "mystery.mkv"), "mystery");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Mystery", "2020", jobState: IdentityJobState.RetailNoMatch);
        await _h.AddPendingReviewAsync(assetId);

        await _h.Organizer.TryAutoOrganizeAsync(assetId);
        await _h.Filer.FilePendingAsync();

        Assert.True(File.Exists(source));
        Assert.Empty(_h.FilesUnder(_h.MoviesDir));

        // A person resolves the review; the next pass files it.
        await _h.Reviews.ResolveAllByEntityAsync(assetId, "person");
        var filed = await _h.Filer.FilePendingAsync();

        Assert.Equal(1, filed);
        Assert.False(File.Exists(source));
        Assert.Single(_h.FilesUnder(_h.MoviesDir));
    }

    [Fact]
    public async Task A_file_whose_identity_work_is_still_running_is_left_for_the_pipeline()
    {
        var source = _h.WriteImportFile(Path.Combine("Movies", "Dune.mkv"), "dune");
        await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021", jobState: IdentityJobState.RetailSearching);

        var filed = await _h.Filer.FilePendingAsync();

        Assert.Equal(0, filed);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_file_dropped_while_a_name_is_taken_gets_a_unique_name_and_nothing_is_overwritten()
    {
        var existing = Path.Combine(_h.MoviesDir, "Movies", "Dune (2021)", "Dune (2021).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "a different file with the same name");
        var source = _h.WriteImportFile(Path.Combine("Movies", "Dune.mkv"), "dune");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021");

        await _h.Organizer.TryAutoOrganizeAsync(assetId);

        Assert.Equal("a different file with the same name", File.ReadAllText(existing));
        var filedPath = (await _h.Assets.FindByIdAsync(assetId))!.FilePathRoot;
        Assert.NotEqual(existing, filedPath);
        Assert.Equal("dune", File.ReadAllText(filedPath));
        Assert.StartsWith(Path.GetDirectoryName(existing)!, filedPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Filing_twice_in_a_row_files_the_file_once()
    {
        var source = _h.WriteImportFile(Path.Combine("Movies", "Dune.mkv"), "dune");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021");

        await Task.WhenAll(
            _h.Organizer.TryAutoOrganizeAsync(assetId),
            _h.Organizer.TryAutoOrganizeAsync(assetId));
        await _h.Filer.FilePendingAsync();

        Assert.Single(_h.FilesUnder(_h.MoviesDir));
        Assert.Empty(_h.FilesUnder(_h.ImportDir));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_restart_after_the_copy_but_before_the_original_was_removed_resumes_without_a_second_copy(bool crossDrive)
    {
        using var harness = new ImportFolderHarness(alwaysCopyAndVerify: crossDrive);
        var source = harness.WriteImportFile(Path.Combine("Movies", "Dune.mkv"), "dune bytes");
        var assetId = await harness.RegisterAsync(source, MediaType.Movies, "Dune", "2021");

        // The previous run copied the file into place and stopped before it removed the original or updated the data store.
        var landed = Path.Combine(harness.MoviesDir, "Movies", "Dune (2021)", "Dune (2021).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(landed)!);
        File.Copy(source, landed);

        await harness.Filer.FilePendingAsync();

        Assert.False(File.Exists(source));
        Assert.Equal(landed, Assert.Single(harness.FilesUnder(harness.MoviesDir)));
        Assert.Equal(landed, (await harness.Assets.FindByIdAsync(assetId))!.FilePathRoot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_crash_between_the_move_and_the_data_store_update_is_finished_on_the_next_start(bool originalStillThere)
    {
        var source = _h.WriteImportFile(Path.Combine("Movies", "Dune.mkv"), "dune bytes");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021");
        var asset = (await _h.Assets.FindByIdAsync(assetId))!;
        var destination = Path.Combine(_h.MoviesDir, "Movies", "Dune (2021)", "Dune (2021).mkv");

        // The previous run wrote its intent and moved the file, then stopped before it updated the data store.
        Assert.True(_h.Journal.TryBegin(new ImportFilingIntent(
            assetId, source, destination, asset.ContentHash, ImportFolderHarness.MoviesLibraryId, DateTimeOffset.UtcNow)));
        Assert.True(_h.Journal.IsInProgress(assetId));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
        if (!originalStillThere)
        {
            File.Delete(source);
        }

        var finished = await _h.Filer.RecoverInterruptedFilingsAsync();

        Assert.Equal(1, finished);
        Assert.False(_h.Journal.IsInProgress(assetId));
        Assert.Equal(destination, (await _h.Assets.FindByIdAsync(assetId))!.FilePathRoot);
        Assert.False(File.Exists(source));
        Assert.Single(_h.FilesUnder(_h.MoviesDir));
    }

    [Fact]
    public async Task A_filing_that_never_moved_the_file_leaves_the_asset_where_it_was()
    {
        var source = _h.WriteImportFile(Path.Combine("Movies", "Dune.mkv"), "dune bytes");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021");
        var asset = (await _h.Assets.FindByIdAsync(assetId))!;
        _h.Journal.TryBegin(new ImportFilingIntent(
            assetId, source, Path.Combine(_h.MoviesDir, "Dune.mkv"), asset.ContentHash, null, DateTimeOffset.UtcNow));

        var finished = await _h.Filer.RecoverInterruptedFilingsAsync();

        Assert.Equal(0, finished);
        Assert.False(_h.Journal.IsInProgress(assetId));
        Assert.Equal(source, (await _h.Assets.FindByIdAsync(assetId))!.FilePathRoot);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_restart_when_the_first_attempt_used_a_unique_name_adopts_it_instead_of_making_another()
    {
        var standard = Path.Combine(_h.MoviesDir, "Movies", "Dune (2021)", "Dune (2021).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(standard)!);
        File.WriteAllText(standard, "a different file that took the standard name");
        var landed = Path.Combine(Path.GetDirectoryName(standard)!, "Dune (2021) (2).mkv");
        var source = _h.WriteImportFile(Path.Combine("Movies", "Dune.mkv"), "dune bytes");
        File.Copy(source, landed); // the interrupted attempt got this far
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021");

        await _h.Organizer.TryAutoOrganizeAsync(assetId);

        Assert.False(File.Exists(source));
        Assert.Equal(landed, (await _h.Assets.FindByIdAsync(assetId))!.FilePathRoot);
        Assert.Equal(2, _h.FilesUnder(_h.MoviesDir).Count());
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(standard)!, "Dune (2021) (3).mkv")));
    }

    [Fact]
    public async Task A_filing_in_progress_is_recorded_and_cleared_when_it_is_done()
    {
        var source = _h.WriteImportFile(Path.Combine("Movies", "Dune.mkv"), "dune bytes");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021");

        await _h.Organizer.TryAutoOrganizeAsync(assetId);

        Assert.False(_h.Journal.IsInProgress(assetId));
        Assert.Empty(_h.Journal.ReadPending());
    }

    [Fact]
    public async Task A_copy_across_drives_is_checked_before_the_original_is_removed()
    {
        using var harness = new ImportFolderHarness(alwaysCopyAndVerify: true);
        var source = harness.WriteImportFile(Path.Combine("Movies", "Dune.mkv"), "dune bytes");
        var assetId = await harness.RegisterAsync(source, MediaType.Movies, "Dune", "2021");

        await harness.Organizer.TryAutoOrganizeAsync(assetId);

        var landed = (await harness.Assets.FindByIdAsync(assetId))!.FilePathRoot;
        Assert.Equal("dune bytes", File.ReadAllText(landed));
        Assert.False(File.Exists(source));
        Assert.Empty(harness.FilesUnder(harness.ImportDir));
        Assert.DoesNotContain(harness.FilesUnder(harness.MoviesDir), file => file.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_copy_that_does_not_match_the_original_is_discarded_and_the_original_stays()
    {
        using var harness = new ImportFolderHarness(alwaysCopyAndVerify: true);
        var source = harness.WriteImportFile(Path.Combine("Movies", "Dune.mkv"), "the file changed after it was fingerprinted");
        var destination = Path.Combine(harness.MoviesDir, "Dune.mkv");

        // The fingerprint taken at ingestion no longer matches what is on disk.
        var moved = await harness.Mover.MoveAsync(source, destination, new string('0', 64));

        Assert.Null(moved);
        Assert.True(File.Exists(source));
        Assert.Empty(harness.FilesUnder(harness.MoviesDir));
    }

    [Fact]
    public async Task Leftover_temporary_copies_from_an_interrupted_attempt_are_cleaned_up()
    {
        using var harness = new ImportFolderHarness(alwaysCopyAndVerify: true);
        var source = harness.WriteImportFile(Path.Combine("Movies", "Dune.mkv"), "dune");
        var destination = Path.Combine(harness.MoviesDir, "Dune.mkv");
        var stale = Path.Combine(harness.MoviesDir, $".Dune.{Guid.NewGuid():N}.part");
        File.WriteAllText(stale, "half a copy");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-3));

        var moved = await harness.Mover.MoveAsync(source, destination, null);

        Assert.Equal(destination, moved);
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public async Task The_review_badge_data_marks_only_files_still_in_the_Import_folder()
    {
        var source = _h.WriteImportFile(Path.Combine("Movies", "mystery.mkv"), "mystery");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Mystery", "2020", jobState: IdentityJobState.RetailNoMatch);
        await _h.AddPendingReviewAsync(assetId);

        var asset = await _h.Assets.FindByIdAsync(assetId);
        Assert.True(ImportFolderRules.IsInside(_h.ImportDir, asset!.FilePathRoot));
        Assert.False(ImportFolderRules.IsInside(_h.ImportDir, Path.Combine(_h.MoviesDir, "Movies", "x.mkv")));
        Assert.False(ImportFolderRules.IsInside(_h.ImportDir, _h.ImportDir + "-other" + Path.DirectorySeparatorChar + "x.mkv"));
    }

    [Fact]
    public async Task The_activity_log_records_the_filing()
    {
        var source = _h.WriteImportFile(Path.Combine("Movies", "Dune.mkv"), "dune");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021");

        await _h.Organizer.TryAutoOrganizeAsync(assetId);

        using var connection = _h.Db.Connection.CreateConnection();
        var details = Dapper.SqlMapper.Query<string>(
            connection, "SELECT detail FROM system_activity WHERE action_type = @type;", new { type = SystemActionType.PathUpdated }).ToList();
        Assert.Contains(details, detail => detail.StartsWith("Filed from the Import folder", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_filesystem_change_in_the_import_flow_is_guarded_by_the_source_policy_gate()
    {
        var autoOrganize = ReadRepoSource(@"src/MediaEngine.Ingestion/AutoOrganizeService.cs");
        var pipeline = ReadRepoSource(@"src/MediaEngine.Ingestion/IngestionEngine.ImportFolder.cs");
        var filer = ReadRepoSource(@"src/MediaEngine.Ingestion/Services/ImportFolderFiler.cs");

        // Filing: the gate is asked about both ends before the mover runs.
        var gate = autoOrganize.IndexOf("if (!CanMoveBetween(sourcePath, destPath, destination))", StringComparison.Ordinal);
        var move = autoOrganize.IndexOf("_importMover!.MoveAsync(", StringComparison.Ordinal);
        Assert.True(gate >= 0 && move > gate);

        // Duplicates: only the import copy is deleted, and only after the gate allows it.
        var method = pipeline.IndexOf("private async Task<bool> HandleImportDuplicateAsync(", StringComparison.Ordinal);
        var allow = pipeline.IndexOf("Mutation = SourceMutationKind.Delete,", method, StringComparison.Ordinal);
        var delete = pipeline.IndexOf("TryDeleteImportCopyAsync(candidate.Path", method, StringComparison.Ordinal);
        Assert.True(method >= 0 && allow > method && delete > allow);

        // The duplicate copy kept under Keep both goes through the same gate as every other filing.
        Assert.Contains("ImportFolderGate.CanFile(", filer, StringComparison.Ordinal);

        // Nothing in the import flow ever picks a destination other than the one marked New media.
        var service = ReadRepoSource(@"src/MediaEngine.Ingestion/Services/ImportFolderService.cs");
        Assert.Contains("entry.PrimaryDestination", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Sources.First", service, StringComparison.Ordinal);
    }

    private static string ReadRepoSource(
        string relativePath,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
        return File.ReadAllText(Path.Combine(root, relativePath)).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
